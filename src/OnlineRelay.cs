using System;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SpookyTogether
{
    // Online play through the global relay (mod-relay/relay.py, tunnel mode): the host's room gets a short
    // code, friends join with it, no port forwarding or VPN. Transport.HostRelay/JoinRelay carry the same
    // framed packets as a LAN game, so everything after the connection is unchanged.
    public sealed partial class TogetherRuntime
    {
        // Built-in server, used until mods-info (github.com/multiplayereverything/mods-info) names one.
        // Players override both in the panel or with Relay= in UserData/SpookyTogether.ini.
        public const string DefaultRelay = "195.60.166.57:25569";
        // what builds before 2026-10-24 saved as Relay= without the player typing it: not the player's own server
        const string OldDefaultRelay = "127.0.0.1:7790";
        const string RelayGame = "spooky";
        public bool Online;
        public bool OnlinePublic = true;
        public string OnlineCode = "";
        // Relay= in the ini: the player's own server, or empty to follow mods-info (then DefaultRelay).
        string relayTyped = "";
        string modInfoUrl;
        ModRelay.ModInfo modInfo;
        bool modInfoStarted;
        int loggedInfoRevision = -1;
        bool hostWhenInfoReady;

        ModRelay.ModInfo Info
        {
            get
            {
                if (modInfo == null) modInfo = new ModRelay.ModInfo(RelayGame, ModVersion, Path.Combine(Root, "UserData/SpookyTogether.modinfo.json"), modInfoUrl);
                return modInfo;
            }
        }

        // The server everything connects to. Setting it to the automatic address (or "") clears the player's own,
        // so the panel's text field, written back every frame, stays automatic until the player edits it.
        public string RelayAddress
        {
            get { return Info.Pick(relayTyped, DefaultRelay); }
            set
            {
                string v = (value ?? "").Replace("\n", "").Replace("\r", "").Trim();
                relayTyped = v.Length == 0 || v == Info.ServerAddress(DefaultRelay) || v == OldDefaultRelay ? "" : v.Substring(0, Math.Min(128, v.Length));
            }
        }

        // Starts the mods-info download if it hasn't started (labs only with -st-modinfo: no internet in tests).
        void StartModInfo()
        {
            if (!modInfoStarted && (!LabMode || modInfoUrl != null)) { modInfoStarted = true; Info.Start(); }
        }

        // mods-info's min_version only guards the central server; a player's own server is their call.
        bool OnlineAllowed()
        {
            if (relayTyped.Length > 0 || !Info.Current.TooOld) return true;
            Tell(Info.StatusLine()); return false;
        }
        volatile string autoOnlineCode;
        volatile string[][] onlineRooms;
        volatile string onlineListStatus = "";
        volatile bool onlineListing;
        float nextOnlineList;
        bool labJoinOnline;

        // Host and friends must run the same build; the relay refuses a mismatch with a clear reason.
        static string RelayVersion { get { return ModVersion + "/" + Wire.Version; } }

        static string CleanCode(string code)
        { return new string((code ?? "").Where(char.IsLetterOrDigit).Take(8).ToArray()).ToUpperInvariant(); }

        bool RelayEndpoint(out string relayHost, out int relayPort)
        {
            if (ModRelay.RelayLink.ParseAddress(RelayAddress, ModRelay.RelayLink.DefaultPort, out relayHost, out relayPort)) return true;
            Tell("Server must be an address like host:port."); return false;
        }

        public void StartHostOnline()
        {
            string relayHost; int relayPort;
            StartModInfo();
            // Hosting in the first seconds after launch: wait for the server address from mods-info, then host.
            if (relayTyped.Length == 0 && Info.State == "loading") { hostWhenInfoReady = true; Tell("Getting the server address..."); return; }
            if (Active || !RelayEndpoint(out relayHost, out relayPort) || !OnlineAllowed()) return;
            SaveSettings();
            Network = new Transport();
            ResetSession();
            OnlineCode = "";
            Network.HostRelay(relayHost, relayPort, RelayGame, RelayVersion, OnlinePublic, Name);
            Tell("Opening an online room on " + RelayAddress + "...");
        }

        public void StartJoinOnline()
        {
            string relayHost; int relayPort;
            if (Active || !RelayEndpoint(out relayHost, out relayPort) || !OnlineAllowed()) return;
            OnlineCode = CleanCode(OnlineCode);
            if (OnlineCode.Length == 0) { Tell("Type the room code your friend got when they pressed Host."); return; }
            SaveSettings();
            Network = new Transport();
            ResetSession();
            Network.JoinRelay(relayHost, relayPort, RelayGame, RelayVersion, OnlineCode, Name);
            Tell("Joining online room " + OnlineCode + "...");
        }

        // Transport events only the relay produces. True: handled.
        bool HandleRelayEvent(NetworkEvent e)
        {
            if (e.Kind == "room")
            {
                OnlineCode = e.Message;
                Tell("Online room open. ROOM CODE: " + e.Message + ". Friends pick Online, type it and press Join.");
                if (LabMode) Check("online.room", e.Message.Length == 5, e.Message);
                return true;
            }
            if (e.Kind == "relay-lost")
            {
                bool wasHost = Hosting;
                Shutdown();
                Tell((wasHost ? "Online room closed: " : "") + e.Message);
                return true;
            }
            return false;
        }

        // Public rooms, fetched off the main thread. autoJoin (lab): keep trying until one appears.
        void FetchOnlineRooms(bool autoJoin)
        {
            string relayHost; int relayPort;
            if (onlineListing || !ModRelay.RelayLink.ParseAddress(RelayAddress, ModRelay.RelayLink.DefaultPort, out relayHost, out relayPort)) return;
            onlineListing = true;
            new Thread(delegate ()
            {
                try
                {
                    string[][] rooms = ModRelay.RelayLink.List(relayHost, relayPort, RelayGame, 4000);
                    onlineRooms = rooms;
                    onlineListStatus = rooms.Length == 0 ? "No public online rooms right now." : "";
                    if (autoJoin && rooms.Length > 0) autoOnlineCode = rooms[0][0];
                }
                catch (Exception ex) { onlineListStatus = "Can't reach " + RelayAddress + ": " + ex.Message; }
                finally { onlineListing = false; }
            }) { IsBackground = true, Name = "SpookyTogether relay list" }.Start();
        }

        // Per frame: refresh the public list while the panel shows it; the lab client joins the first room.
        void UpdateOnline()
        {
            StartModInfo();
            if (hostWhenInfoReady && Info.State != "loading") { hostWhenInfoReady = false; StartHostOnline(); }
            if (modInfoStarted && Info.Revision != loggedInfoRevision && Info.State != "loading")
            {
                loggedInfoRevision = Info.Revision;
                Log("mods-info: " + Info.State + (Info.Error.Length > 0 ? " (" + Info.Error + ")" : "") + ", source " + Info.Current.Source + ", server " + RelayAddress + (Info.StatusLine().Length > 0 ? ", says: " + Info.StatusLine() : ""));
                if (LabMode && modInfoUrl != null) Check("online.modinfo", Info.State == "ok", RelayAddress);
            }
            float now = Time.realtimeSinceStartup;
            if (((show && Online) || labJoinOnline) && !Active && now >= nextOnlineList) { nextOnlineList = now + (labJoinOnline ? 1 : 5); FetchOnlineRooms(labJoinOnline); }
            string code = autoOnlineCode;
            if (code != null && labJoinOnline && !Active)
            {
                autoOnlineCode = null; labJoinOnline = false; OnlineCode = code;
                Check("online.found", true, code);
                Log("Lab: online list found room " + code + "; joining");
                StartJoinOnline();
            }
        }

        // The panel's online half (instead of address, port and the LAN list).
        void DrawOnlinePanel(GUIStyle label)
        {
            if (Hosting && Network.Relayed)
            {
                GUILayout.Label(OnlineCode.Length > 0 ? "ROOM CODE:  " + OnlineCode : "Opening the room...", tagStyle);
                GUILayout.Label("Give your friends this code. They pick Online, type it and press Join.", label);
                return;
            }
            string info = Info.StatusLine();
            if (info.Length > 0) GUILayout.Label(info, label);
            GUI.enabled = !Active;
            GUILayout.Label("Room code (for joining)", label);
            OnlineCode = CleanCode(GUILayout.TextField(OnlineCode, 8));
            GUILayout.Label("Server", label);
            RelayAddress = GUILayout.TextField(RelayAddress, 128);
            OnlinePublic = GUILayout.Toggle(OnlinePublic, " List my room in the public list when I host");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Host", GUILayout.Height(32))) StartHostOnline();
            if (GUILayout.Button("Join", GUILayout.Height(32))) StartJoinOnline();
            GUILayout.EndHorizontal();
            GUI.enabled = true;
        }

        void DrawOnlineList(GUIStyle label)
        {
            GUILayout.Label("Public online games:", label);
            string[][] rooms = onlineRooms;
            if (rooms == null || rooms.Length == 0) { GUILayout.Label("  " + (onlineListStatus.Length > 0 ? onlineListStatus : "Searching..."), label); return; }
            foreach (string[] room in rooms.Take(6))
            {
                int players; int.TryParse(room[1], out players);
                GUI.enabled = players < 4;
                if (GUILayout.Button(room[3] + "'s game  (" + room[1] + "/" + room[2] + ")  " + room[0], GUILayout.Height(26))) { OnlineCode = room[0]; StartJoinOnline(); }
                GUI.enabled = true;
            }
        }
    }
}
