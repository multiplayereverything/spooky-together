using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace SpookyTogether
{
    // Server list: hosts broadcast a small UDP beacon once a second; the F5 panel lists games found on the network.
    // Non-blocking sockets on the main thread, ported from Baldi Together's LanDiscovery.
    public sealed partial class TogetherRuntime
    {
        public const int DiscoveryPort = 27021;
        const string DiscoveryMagic = "STLAN1";

        public sealed class LanGame
        {
            public string Key, Address, Name, Version, Mansion;
            public int Port, Protocol, Players;
            public float SeenAt;
            public bool Compatible { get { return Protocol == Wire.Version && Version == ModVersion; } }
        }

        readonly Dictionary<string, LanGame> lanGames = new Dictionary<string, LanGame>();
        UdpClient lanBeacon, lanListener;
        IPEndPoint[] lanTargets;
        float nextLanBeacon, lanTargetsAt, lanListenRetryAt;
        string lanStatus = "Searching your network for games...";
        readonly string lanKey = Guid.NewGuid().ToString("N");
        bool labLanListen;

        void UpdateLanDiscovery()
        {
            float now = Time.realtimeSinceStartup;
            if (Hosting && !Network.OnSteam) { if (now >= nextLanBeacon) { nextLanBeacon = now + 1; SendLanBeacon(now); } }
            else if (lanBeacon != null) { lanBeacon.Close(); lanBeacon = null; }
            if ((show && !Active) || labLanListen) PollLanGames(now);
            else CloseLanListener();
        }

        void SendLanBeacon(float now)
        {
            try
            {
                if (lanBeacon == null) { lanBeacon = new UdpClient(); lanBeacon.EnableBroadcast = true; }
                if (lanTargets == null || now >= lanTargetsAt) { lanTargets = LanBroadcastTargets(); lanTargetsAt = now + 10; }
                string payload = string.Join("\n", new[] { DiscoveryMagic, Wire.Version.ToString(), ModVersion, lanKey, CleanName(Name), Port.ToString(),
                    (AcceptedCount + 1).ToString(), runMansion ?? "" });
                byte[] data = Encoding.UTF8.GetBytes(payload);
                foreach (IPEndPoint target in lanTargets)
                {
                    try { lanBeacon.Send(data, data.Length, target); }
                    catch (SocketException) { }   // an adapter without broadcast: the other adapters still announce
                }
            }
            catch (Exception ex) { Log("LAN beacon unavailable: " + ex.Message); if (lanBeacon != null) lanBeacon.Close(); lanBeacon = null; nextLanBeacon = now + 10; }
        }

        // Limited broadcast plus each adapter's directed broadcast, because Windows sends 255.255.255.255 on one adapter only.
        static IPEndPoint[] LanBroadcastTargets()
        {
            List<IPEndPoint> targets = new List<IPEndPoint> { new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), new IPEndPoint(IPAddress.Loopback, DiscoveryPort) };
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null) continue;
                        byte[] address = unicast.Address.GetAddressBytes(), mask = unicast.IPv4Mask.GetAddressBytes();
                        if (mask[0] == 0 && mask[1] == 0 && mask[2] == 0 && mask[3] == 0) continue;
                        for (int i = 0; i < 4; i++) address[i] = (byte)(address[i] | ~mask[i]);
                        IPEndPoint broadcast = new IPEndPoint(new IPAddress(address), DiscoveryPort);
                        if (!targets.Contains(broadcast)) targets.Add(broadcast);
                    }
                }
            }
            catch (Exception ex) { Log("LAN adapters unavailable: " + ex.Message); }
            return targets.ToArray();
        }

        void PollLanGames(float now)
        {
            if (lanListener == null)
            {
                if (now < lanListenRetryAt) return;
                try
                {
                    UdpClient listener = new UdpClient();
                    // Several game copies on one PC can browse at once.
                    listener.ExclusiveAddressUse = false;
                    listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                    lanListener = listener; lanStatus = "Searching your network for games...";
                }
                catch (Exception ex) { lanStatus = "LAN search unavailable: " + ex.Message; lanListenRetryAt = now + 3; return; }
            }
            for (int budget = 0; budget < 32; budget++)
            {
                byte[] data; IPEndPoint from = null;
                try { if (lanListener.Available <= 0) break; data = lanListener.Receive(ref from); }
                catch (SocketException) { break; }
                ReadLanBeacon(data, from, now);
            }
            List<string> stale = null;
            foreach (KeyValuePair<string, LanGame> pair in lanGames) if (now - pair.Value.SeenAt > 4) (stale ?? (stale = new List<string>())).Add(pair.Key);
            if (stale != null) foreach (string key in stale) lanGames.Remove(key);
        }

        void ReadLanBeacon(byte[] data, IPEndPoint from, float now)
        {
            if (data == null || data.Length > 512 || from == null) return;
            string[] fields;
            try { fields = Encoding.UTF8.GetString(data).Split('\n'); }
            catch (ArgumentException) { return; }   // not our beacon: ignored
            int protocol, port, players;
            if (fields.Length != 8 || fields[0] != DiscoveryMagic || !int.TryParse(fields[1], out protocol) || fields[2].Length > 16 || fields[3].Length != 32
                || !int.TryParse(fields[5], out port) || port < 1024 || port > 65535 || !int.TryParse(fields[6], out players) || players < 1 || players > 4) return;
            if (fields[3] == lanKey) return;
            // A LAN has a handful of games; a flood of made-up keys must not grow the list without limit.
            if (lanGames.Count >= 32 && !lanGames.ContainsKey(fields[3])) return;
            LanGame existing;
            bool loopback = IPAddress.IsLoopback(from.Address);
            // The same game arrives by loopback and by broadcast; keep the LAN address friends can also use.
            if (lanGames.TryGetValue(fields[3], out existing) && loopback && !IPAddress.IsLoopback(IPAddress.Parse(existing.Address))) { existing.SeenAt = now; existing.Players = players; return; }
            lanGames[fields[3]] = new LanGame
            {
                Key = fields[3], Address = from.Address.ToString(), Protocol = protocol, Version = fields[2], Name = CleanName(fields[4]),
                Port = port, Players = players, Mansion = fields[7].Length > 64 ? "" : fields[7], SeenAt = now
            };
        }

        void CloseLanListener()
        {
            if (lanListener != null) { lanListener.Close(); lanListener = null; }
            if (lanGames.Count > 0) lanGames.Clear();
            lanListenRetryAt = 0;
        }

        void ShutdownLanDiscovery()
        {
            CloseLanListener();
            if (lanBeacon != null) { lanBeacon.Close(); lanBeacon = null; }
        }

        List<LanGame> VisibleLanGames()
        {
            List<LanGame> list = new List<LanGame>(lanGames.Values);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            if (list.Count > 6) list.RemoveRange(6, list.Count - 6);
            return list;
        }

        void DrawServerList(GUIStyle label)
        {
            GUILayout.Label("Games on your network:", label);
            List<LanGame> games = VisibleLanGames();
            if (games.Count == 0) GUILayout.Label("  " + lanStatus, label);
            foreach (LanGame game in games)
            {
                string state = !game.Compatible ? "needs " + game.Version : game.Players >= 4 ? "full" : game.Players + "/4" + (game.Mansion.Length > 0 ? " in a run" : " in lobby");
                GUI.enabled = game.Compatible && game.Players < 4;
                if (GUILayout.Button(game.Name + "'s game  (" + state + ")", GUILayout.Height(26)))
                {
                    Address = game.Address; portText = game.Port.ToString();
                    StartJoin();
                }
                GUI.enabled = true;
            }
        }
    }
}
