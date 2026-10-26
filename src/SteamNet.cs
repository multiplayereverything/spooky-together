using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;

namespace SpookyTogether
{
    // Online play through Steam: Steam Networking Sockets carry the same packets as a LAN game, peer to peer through
    // Valve's relay, so nobody forwards a port. Unlike the TCP links this runs on the main thread only: Poll pumps
    // Steam's queues, and connection changes arrive through SteamAPI.RunCallbacks in the game's own Update.
    public sealed partial class Transport
    {
        public bool OnSteam { get; private set; }
        HSteamListenSocket steamListen = HSteamListenSocket.Invalid;
        HSteamNetPollGroup steamGroup = HSteamNetPollGroup.Invalid;
        readonly Dictionary<uint, int> steamPeerOf = new Dictionary<uint, int>();   // connection handle -> peer id
        readonly Dictionary<int, HSteamNetConnection> steamLinks = new Dictionary<int, HSteamNetConnection>();
        Callback<SteamNetConnectionStatusChangedCallback_t> steamStatus;
        IntPtr steamBuffer = IntPtr.Zero;
        readonly IntPtr[] steamInbox = new IntPtr[64];

        // labPort > 0 (lab only): the same Steam sockets over 127.0.0.1:labPort, because lab copies share one Steam
        // account and an account cannot open a P2P connection to itself.
        public void HostSteam(int labPort)
        {
            if (listener != null || Hosting || disposed) throw new InvalidOperationException("Transport already used.");
            BeginSteam();
            Hosting = true;
            if (labPort > 0) { SteamNetworkingIPAddr local = new SteamNetworkingIPAddr(); local.Clear(); local.SetIPv6LocalHost((ushort)labPort); steamListen = SteamNetworkingSockets.CreateListenSocketIP(ref local, 0, null); }
            else steamListen = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
            if (steamListen == HSteamListenSocket.Invalid) throw new InvalidOperationException("Steam would not open a listen socket.");
            steamGroup = SteamNetworkingSockets.CreatePollGroup();
            Port = labPort;
        }

        public void JoinSteam(CSteamID host, int labPort)
        {
            if (disposed) throw new ObjectDisposedException("Transport");
            BeginSteam();
            HSteamNetConnection link;
            if (labPort > 0) { SteamNetworkingIPAddr local = new SteamNetworkingIPAddr(); local.Clear(); local.SetIPv6LocalHost((ushort)labPort); link = SteamNetworkingSockets.ConnectByIPAddress(ref local, 0, null); }
            else { SteamNetworkingIdentity identity = new SteamNetworkingIdentity(); identity.SetSteamID(host); link = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null); }
            if (link == HSteamNetConnection.Invalid) throw new InvalidOperationException("Steam would not open the connection.");
            steamLinks[0] = link; steamPeerOf[link.m_HSteamNetConnection] = 0;
        }

        void BeginSteam()
        {
            OnSteam = true;
            SteamNetworkingUtils.InitRelayNetworkAccess();
            steamStatus = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnSteamStatus);
            steamBuffer = Marshal.AllocHGlobal(MaxFrame);
        }

        void OnSteamStatus(SteamNetConnectionStatusChangedCallback_t change)
        {
            if (disposed) return;
            HSteamNetConnection link = change.m_hConn;
            ESteamNetworkingConnectionState state = change.m_info.m_eState;
            int id;
            bool ours = steamPeerOf.TryGetValue(link.m_HSteamNetConnection, out id);
            if (!ours)
            {
                // A new joiner on our listen socket (other connections in the process are not ours to touch).
                if (!Hosting || state != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting || change.m_info.m_hListenSocket != steamListen) return;
                if (SteamNetworkingSockets.AcceptConnection(link) != EResult.k_EResultOK) { SteamNetworkingSockets.CloseConnection(link, 0, "accept failed", false); return; }
                int free = 1;
                while (steamLinks.ContainsKey(free)) free++;
                if (free > 3)
                {
                    SendSteam(link, Wire.Packet(Op.Reject, w => Wire.Text(w, "The game is full (4 players).")));
                    SteamNetworkingSockets.CloseConnection(link, 0, "full", true);
                    return;
                }
                steamLinks[free] = link; steamPeerOf[link.m_HSteamNetConnection] = free;
                SteamNetworkingSockets.SetConnectionPollGroup(link, steamGroup);
                return;
            }
            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected) { Emit(id, "connected", null, null); return; }
            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
            {
                string why = change.m_info.m_szEndDebug;
                // Take what already arrived first: a Reject sent just before the host closed says why (full, version).
                PumpSteam();
                DropSteam(id, (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ? "closed by the other side" : "Steam connection problem") + (string.IsNullOrEmpty(why) ? "" : ": " + why), false);
            }
        }

        void PumpSteam()
        {
            int count;
            if (Hosting) count = steamGroup == HSteamNetPollGroup.Invalid ? 0 : SteamNetworkingSockets.ReceiveMessagesOnPollGroup(steamGroup, steamInbox, steamInbox.Length);
            else { HSteamNetConnection host; count = steamLinks.TryGetValue(0, out host) ? SteamNetworkingSockets.ReceiveMessagesOnConnection(host, steamInbox, steamInbox.Length) : 0; }
            for (int i = 0; i < count; i++)
            {
                SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(steamInbox[i]);
                int id;
                if (message.m_cbSize >= 1 && message.m_cbSize <= MaxFrame && steamPeerOf.TryGetValue(message.m_conn.m_HSteamNetConnection, out id))
                {
                    byte[] data = new byte[message.m_cbSize];
                    Marshal.Copy(message.m_pData, data, 0, data.Length);
                    Emit(id, "packet", data, null);
                }
                SteamNetworkingMessage_t.Release(steamInbox[i]);
            }
        }

        bool SendSteam(HSteamNetConnection link, byte[] packet)
        {
            Marshal.Copy(packet, 0, steamBuffer, packet.Length);
            long number;
            EResult result = SteamNetworkingSockets.SendMessageToConnection(link, steamBuffer, (uint)packet.Length, Constants.k_nSteamNetworkingSend_Reliable, out number);
            return result == EResult.k_EResultOK;
        }

        void SendSteam(int id, byte[] packet, bool close)
        {
            HSteamNetConnection link;
            if (!steamLinks.TryGetValue(id, out link)) return;
            // Steam buffers reliable messages itself; a failure means the link is gone or hopelessly behind.
            if (!SendSteam(link, packet)) { DropSteam(id, "Steam would not send (the connection stalled)", false); return; }
            if (close) DropSteam(id, null, true);
        }

        void DropSteam(int id, string reason, bool linger)
        {
            HSteamNetConnection link;
            if (!steamLinks.TryGetValue(id, out link)) return;
            steamLinks.Remove(id); steamPeerOf.Remove(link.m_HSteamNetConnection);
            SteamNetworkingSockets.CloseConnection(link, 0, null, linger);
            Emit(id, "disconnected", null, reason);
        }

        void DisposeSteam()
        {
            if (!OnSteam) return;
            foreach (HSteamNetConnection link in steamLinks.Values) SteamNetworkingSockets.CloseConnection(link, 0, null, true);
            steamLinks.Clear(); steamPeerOf.Clear();
            if (steamListen != HSteamListenSocket.Invalid) { SteamNetworkingSockets.CloseListenSocket(steamListen); steamListen = HSteamListenSocket.Invalid; }
            if (steamGroup != HSteamNetPollGroup.Invalid) { SteamNetworkingSockets.DestroyPollGroup(steamGroup); steamGroup = HSteamNetPollGroup.Invalid; }
            if (steamStatus != null) { steamStatus.Dispose(); steamStatus = null; }
            if (steamBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(steamBuffer); steamBuffer = IntPtr.Zero; }
        }
    }
}
