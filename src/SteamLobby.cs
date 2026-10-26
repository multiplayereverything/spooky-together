using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace SpookyTogether
{
    // Steam mode: the host opens a Steam lobby that carries the game's details. Friends find it in the panel's Steam
    // list, through an invite, or with Join Game on the host's Steam profile; joining reads the host's Steam ID from the
    // lobby and connects to it through Steam's relay (SteamNet.cs). The lobby only finds the game: packets never pass
    // through it.
    public sealed partial class TogetherRuntime
    {
        const string LobbyTag = "spooky-together";
        public bool OnSteamMode;
        public bool SteamPublic = true;
        CSteamID lobby = CSteamID.Nil;
        CallResult<LobbyCreated_t> lobbyCreated;
        CallResult<LobbyEnter_t> lobbyEntered;
        CallResult<LobbyMatchList_t> lobbyListed;
        Callback<GameLobbyJoinRequested_t> joinRequested;
        Callback<GameRichPresenceJoinRequested_t> presenceJoinRequested;
        readonly List<SteamGame> steamGames = new List<SteamGame>();
        string steamListStatus = "";
        bool steamListing;
        float nextSteamList;
        int lobbyPlayersShown = -1;
        string lobbyMansionShown;
        ulong launchLobby;   // +connect_lobby on the command line: Steam started the game to join a friend
        bool steamHostPending;   // -st-host with -st-steam: host once Steam is up
        // A lobby create or join is waiting for Steam: a second one would drop the first's result (CallResult.Set) and
        // leave us in a lobby we no longer know about.
        bool lobbyCallPending;
        float steamListAt;
        // Lab: -st-steam-lab <tag> keeps a test run to its own lobbies; its copies share one Steam account, so they
        // connect over 127.0.0.1 through the same Steam sockets (the lab port goes in the lobby).
        string labSteamTag;
        bool labJoinSteam;
        int labPendingPort;   // lab-only: the listed lobby's port, if Steam won't let the same account join its lobby

        sealed class SteamGame { public CSteamID Lobby; public string Name, Version, Mansion; public int Players, LabPort; }

        static string SteamVersion { get { return ModVersion + "/" + Wire.Version; } }

        static string steamNotReady = "";   // process-scoped: why Steam isn't usable yet, for the log
        static bool SteamReady
        {
            get
            {
                try
                {
                    // Not SteamAPI.IsSteamRunning(): it reads false in copies the game's own SteamAPI.Init succeeded in.
                    if (!SteamUser.BLoggedOn()) { steamNotReady = "not signed in to Steam"; return false; }
                    return true;
                }
                catch (Exception ex) { steamNotReady = ex.GetType().Name + ": " + ex.Message; return false; }   // Steamworks not initialised: the game was not started through Steam
            }
        }
        float steamWaitLogAt;

        void ReadSteamArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-st-steam") OnSteamMode = true;
                if (args[i] == "-st-join-steam") { OnSteamMode = true; labJoinSteam = true; autoRole = null; }
                if (args[i] == "-st-steam-lab" && i + 1 < args.Length) labSteamTag = args[++i];
                if (args[i] == "+connect_lobby" && i + 1 < args.Length) { ulong id; if (ulong.TryParse(args[++i], out id)) { launchLobby = id; OnSteamMode = true; } }
            }
        }

        void EnsureSteamCallbacks()
        {
            if (joinRequested != null || !SteamReady) return;
            lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            lobbyEntered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
            lobbyListed = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
            // A friend's invite accepted, or Join Game on a friend who is in a lobby.
            joinRequested = Callback<GameLobbyJoinRequested_t>.Create(r => RequestJoin(r.m_steamIDLobby));
            // Join Game through the host's rich presence (its "connect" string names the lobby).
            presenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(r => { ulong id; if (TryLobbyFromConnect(r.m_rgchConnect, out id)) RequestJoin(new CSteamID(id)); });
        }

        static bool TryLobbyFromConnect(string connect, out ulong id)
        {
            id = 0;
            if (string.IsNullOrEmpty(connect)) return false;
            string[] parts = connect.Split(' ');
            for (int i = 0; i + 1 < parts.Length; i++) if (parts[i] == "+connect_lobby") return ulong.TryParse(parts[i + 1], out id);
            return false;
        }

        void RequestJoin(CSteamID target)
        {
            OnSteamMode = true;
            if (Active) { Tell("Leave your current game first (F5, Leave), then accept the invite again."); return; }
            JoinSteamLobby(target);
        }

        public void StartHostSteam()
        {
            if (Active || (labSteamTag != null && !ParsePort())) return;
            if (!SteamReady) { Tell("Steam isn't available. Start the game through Steam to play online."); return; }
            EnsureSteamCallbacks();
            SaveSettings();
            Network = new Transport();
            ResetSession();
            try { Network.HostSteam(labSteamTag != null ? Port : 0); }
            catch (Exception ex) { Shutdown(); Tell("Could not host on Steam: " + ex.Message); return; }
            if (lobbyCallPending) { Tell("Steam is still answering the last request; try again in a moment."); return; }
            lobbyCallPending = true;
            lobbyCreated.Set(SteamMatchmaking.CreateLobby(SteamPublic ? ELobbyType.k_ELobbyTypePublic : ELobbyType.k_ELobbyTypeFriendsOnly, 4));
            Tell("Opening a Steam lobby...");
        }

        void OnLobbyCreated(LobbyCreated_t result, bool failed)
        {
            lobbyCallPending = false;
            if (!Hosting || !Network.OnSteam) { if (!failed && result.m_eResult == EResult.k_EResultOK) SteamMatchmaking.LeaveLobby(new CSteamID(result.m_ulSteamIDLobby)); return; }
            if (failed || result.m_eResult != EResult.k_EResultOK)
            {
                Tell("Steam couldn't open a lobby (" + (failed ? "no reply" : result.m_eResult.ToString()) + "). You are hosting, but friends can't find the game.");
                if (LabMode) Check("steam.lobby", false, failed ? "no reply" : result.m_eResult.ToString());
                return;
            }
            lobby = new CSteamID(result.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(lobby, "st", LobbyTag);
            SteamMatchmaking.SetLobbyData(lobby, "ver", SteamVersion);
            SteamMatchmaking.SetLobbyData(lobby, "name", Name);
            SteamMatchmaking.SetLobbyData(lobby, "host", SteamUser.GetSteamID().ToString());
            if (labSteamTag != null) { SteamMatchmaking.SetLobbyData(lobby, "lab", labSteamTag); SteamMatchmaking.SetLobbyData(lobby, "labport", Port.ToString()); }
            lobbyPlayersShown = -1; lobbyMansionShown = null;
            UpdateLobbyData();
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + lobby);
            Tell("Steam lobby open. Friends join from the Steam list in F5, with Join Game on your Steam profile, or by invite.");
            if (LabMode) Check("steam.lobby", true, "lobby " + lobby);
        }

        // Players and the current mansion, for the list; only when they change.
        void UpdateLobbyData()
        {
            if (!lobby.IsValid() || !Hosting) return;
            int players = 1 + AcceptedCount;
            string mansion = RunActive && runMansion != null ? runMansion : "menu";
            if (players == lobbyPlayersShown && mansion == lobbyMansionShown) return;
            lobbyPlayersShown = players; lobbyMansionShown = mansion;
            SteamMatchmaking.SetLobbyData(lobby, "players", players.ToString());
            SteamMatchmaking.SetLobbyData(lobby, "mansion", mansion);
        }

        void JoinSteamLobby(CSteamID target)
        {
            if (Active || !target.IsValid()) return;
            if (!SteamReady) { Tell("Steam isn't available. Start the game through Steam to play online."); return; }
            EnsureSteamCallbacks();
            if (lobbyCallPending) { Tell("Steam is still answering the last request; try again in a moment."); return; }
            SaveSettings();
            lobbyCallPending = true;
            lobbyEntered.Set(SteamMatchmaking.JoinLobby(target));
            Tell("Joining through Steam...");
        }

        void OnLobbyEntered(LobbyEnter_t result, bool failed)
        {
            lobbyCallPending = false;
            CSteamID entered = new CSteamID(result.m_ulSteamIDLobby);
            // 1 = k_EChatRoomEnterResponseSuccess
            if (failed || result.m_EChatRoomEnterResponse != 1)
            {
                if (labSteamTag != null && labPendingPort > 0 && !Active)
                {
                    Log("Lab: Steam refused the lobby join (response " + result.m_EChatRoomEnterResponse + "); connecting with the listed lab port");
                    ConnectSteam(SteamUser.GetSteamID(), labPendingPort, "the lab host");
                    return;
                }
                Tell("Couldn't join that Steam game (it may have closed)."); return;
            }
            if (Active) { SteamMatchmaking.LeaveLobby(entered); return; }
            if (SteamMatchmaking.GetLobbyData(entered, "st") != LobbyTag) { SteamMatchmaking.LeaveLobby(entered); Tell("That Steam lobby isn't a Spooky Together game."); return; }
            string version = SteamMatchmaking.GetLobbyData(entered, "ver");
            if (version != SteamVersion) { SteamMatchmaking.LeaveLobby(entered); Tell("The host runs Spooky Together " + version + "; you have " + SteamVersion + ". Use the same version."); return; }
            CSteamID host = SteamMatchmaking.GetLobbyOwner(entered);
            // Steam hands a lobby to another member when its owner leaves; the game's host is the one who opened it.
            ulong opener;
            if (ulong.TryParse(SteamMatchmaking.GetLobbyData(entered, "host"), out opener) && opener != host.m_SteamID)
            { SteamMatchmaking.LeaveLobby(entered); Tell("The host has left that game."); return; }
            int labPort = 0;
            if (labSteamTag != null) int.TryParse(SteamMatchmaking.GetLobbyData(entered, "labport"), out labPort);
            if (host == SteamUser.GetSteamID() && labPort == 0) { SteamMatchmaking.LeaveLobby(entered); Tell("That's your own game."); return; }
            // Lab copies share the host's account: leaving "our" membership later would take the host out of its lobby.
            if (host != SteamUser.GetSteamID()) lobby = entered;
            ConnectSteam(host, labPort, SteamMatchmaking.GetLobbyData(entered, "name"));
        }

        void ConnectSteam(CSteamID host, int labPort, string hostName)
        {
            Network = new Transport();
            ResetSession();
            try { Network.JoinSteam(host, labPort); }
            catch (Exception ex) { Shutdown(); Tell("Could not connect through Steam: " + ex.Message); return; }
            Tell("Connecting to " + hostName + " through Steam...");
        }

        void LeaveSteamLobby()
        {
            if (!lobby.IsValid()) return;
            bool hosted = Hosting;
            try
            {
                SteamMatchmaking.LeaveLobby(lobby);
                if (hosted) SteamFriends.ClearRichPresence();
            }
            catch (Exception ex) { Log("Leaving the Steam lobby failed: " + ex.Message); }
            lobby = CSteamID.Nil;
        }

        void RequestSteamList()
        {
            // A list request Steam never answers (it went offline) must not stop every later one.
            if ((steamListing && Time.realtimeSinceStartup - steamListAt < 15) || !SteamReady) return;
            EnsureSteamCallbacks();
            steamListing = true; steamListAt = Time.realtimeSinceStartup;
            SteamMatchmaking.AddRequestLobbyListStringFilter("st", LobbyTag, ELobbyComparison.k_ELobbyComparisonEqual);
            if (labSteamTag != null) SteamMatchmaking.AddRequestLobbyListStringFilter("lab", labSteamTag, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(20);
            SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
            if (call == SteamAPICall_t.Invalid) { steamListing = false; return; }
            lobbyListed.Set(call);
        }

        void OnLobbyList(LobbyMatchList_t result, bool failed)
        {
            steamListing = false;
            steamGames.Clear();
            if (failed) { steamListStatus = "Steam didn't answer. Trying again..."; return; }
            for (int i = 0; i < result.m_nLobbiesMatching; i++)
            {
                CSteamID id = SteamMatchmaking.GetLobbyByIndex(i);
                SteamGame game = new SteamGame { Lobby = id, Name = CleanName(SteamMatchmaking.GetLobbyData(id, "name")), Version = SteamMatchmaking.GetLobbyData(id, "ver"), Mansion = SteamMatchmaking.GetLobbyData(id, "mansion") };
                int.TryParse(SteamMatchmaking.GetLobbyData(id, "players"), out game.Players);
                int.TryParse(SteamMatchmaking.GetLobbyData(id, "labport"), out game.LabPort);
                steamGames.Add(game);
            }
            steamListStatus = steamGames.Count == 0 ? "No public Steam games right now. Friends' private games join by invite." : "";
        }

        // Per frame: the host keeps its lobby current; the panel's list refreshes while shown; the lab client joins.
        void UpdateSteam()
        {
            // Invites and Join Game must work before the player ever opens the Steam panel.
            if (joinRequested == null && Time.frameCount % 60 == 0) EnsureSteamCallbacks();
            if (launchLobby != 0 && SteamReady && !Active) { CSteamID target = new CSteamID(launchLobby); launchLobby = 0; RequestJoin(target); }
            if (steamHostPending)
            {
                if (SteamReady) { steamHostPending = false; StartHostSteam(); }
                else if (Time.realtimeSinceStartup >= steamWaitLogAt) { steamWaitLogAt = Time.realtimeSinceStartup + 10; Log("Waiting for Steam to host: " + steamNotReady); }
            }
            if (Hosting && Network.OnSteam) UpdateLobbyData();
            float now = Time.realtimeSinceStartup;
            if (((show && OnSteamMode) || labJoinSteam) && !Active && now >= nextSteamList) { nextSteamList = now + (labJoinSteam ? 2 : 6); RequestSteamList(); }
            if (labJoinSteam && !Active)
                foreach (SteamGame game in steamGames)
                    if (game.Version == SteamVersion && game.Players < 4)
                    {
                        labJoinSteam = false; labPendingPort = game.LabPort;
                        Check("steam.found", true, game.Name + " lobby " + game.Lobby);
                        JoinSteamLobby(game.Lobby);
                        break;
                    }
        }

        // The panel's Steam half (instead of address, port and the LAN list).
        void DrawSteamPanel(GUIStyle label)
        {
            if (Hosting && Network.OnSteam)
            {
                GUILayout.Label(lobby.IsValid() ? "Steam lobby open (" + (SteamPublic ? "public" : "friends and invites") + ")." : "Opening the Steam lobby...", label);
                GUI.enabled = lobby.IsValid();
                if (GUILayout.Button("Invite Steam friends", GUILayout.Height(30))) SteamFriends.ActivateGameOverlayInviteDialog(lobby);
                GUI.enabled = true;
                return;
            }
            if (!SteamReady) { GUILayout.Label("Steam isn't available. Start the game through Steam to play online.", label); return; }
            GUI.enabled = !Active;
            SteamPublic = GUILayout.Toggle(SteamPublic, " List my game publicly (off: friends and invites only)");
            if (GUILayout.Button("Host on Steam", GUILayout.Height(32))) StartHostSteam();
            GUI.enabled = true;
        }

        void DrawSteamList(GUIStyle label)
        {
            GUILayout.Label("Steam games:", label);
            if (steamGames.Count == 0) { GUILayout.Label("  " + (steamListStatus.Length > 0 ? steamListStatus : "Searching..."), label); return; }
            int shown = 0;
            foreach (SteamGame game in steamGames)
            {
                if (shown++ == 6) break;
                bool compatible = game.Version == SteamVersion;
                GUI.enabled = compatible && game.Players < 4;
                string where = string.IsNullOrEmpty(game.Mansion) || game.Mansion == "menu" ? "in the menu" : game.Mansion;
                if (GUILayout.Button(game.Name + "'s game  (" + game.Players + "/4, " + where + ")" + (compatible ? "" : "  needs " + game.Version), GUILayout.Height(26))) JoinSteamLobby(game.Lobby);
                GUI.enabled = true;
            }
        }
    }
}
