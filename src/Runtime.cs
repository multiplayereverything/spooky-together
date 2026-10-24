using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpookyTogether
{
    public sealed class Peer
    {
        public int Id;
        public string Name = "Player";
        public bool Accepted, HasPose, Downed, InRun;
        public byte Colour;
        public int RoomSeq = -1;
        public float PoseAt, LastHeard;
        public Vector3 Position, ShownPosition;
        public float Yaw, Pitch, ShownYaw;
        public bool Crawling;
        public string Weapon = "";
        public float SwingAt = -100;
        public RemoteAvatar Avatar;
    }

    public sealed partial class TogetherRuntime : MonoBehaviour
    {
        public const string ModVersion = "0.2.7";
        public static TogetherRuntime Instance;   // process-scoped: the one runtime object
        public static Action<string> Log = delegate { };   // process-scoped: logger hook set by the loader entry
        public Transport Network;
        public bool Hosting { get { return Network != null && Network.Hosting; } }
        public bool Active { get { return Network != null; } }
        public bool IsClient { get { return Network != null && !Network.Hosting; } }
        public bool Connected { get { return Hosting || (IsClient && self > 0); } }
        public string Name = "Player", Address = "127.0.0.1";
        public byte Colour;
        public int Port = 27020;
        public string Status = "F5: host a LAN game or join a friend.";
        readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        readonly List<string> messages = new List<string>();
        readonly List<float> messageTimes = new List<float>();
        string gameHash = "";
        int self = -1;
        bool show;
        string portText = "27020";
        string autoRole;
        bool labAutoStart, labAutoDoor, labShots, labHurt, labJoinLan;
        int labShotSeq = -1;
        public float lastJoinAt;
        int labExpect, labSoakSeq, labSoakCount, labLocalCount, labDumpedSeq = -1;
        bool labGod, labSoakSpawn, labMansionsLogged;
        string labMansion;
        float labShotAt;
        float labDoorAt = -1;
        float nextPose, nextPing;
        GUIStyle boxStyle, labelStyle, tagStyle;
        public static readonly Color[] Colours = { new Color(0.95f, 0.35f, 0.3f), new Color(0.35f, 0.75f, 1f), new Color(0.45f, 0.95f, 0.45f), new Color(1f, 0.85f, 0.3f), new Color(0.85f, 0.5f, 1f), new Color(1f, 0.6f, 0.25f) };
        public static string Root { get { return Directory.GetParent(Application.dataPath).FullName; } }
        public int SelfId { get { return Hosting ? 0 : self; } }
        public IEnumerable<Peer> Peers { get { return peers.Values; } }

        public static void Boot()
        {
            if (Instance != null) return;
            GameObject root = new GameObject("SpookyTogether");
            Object.DontDestroyOnLoad(root);
            root.hideFlags = HideFlags.HideAndDontSave;
            Instance = root.AddComponent<TogetherRuntime>();
        }

        void Awake()
        {
            Instance = this;
            try
            {
                using (SHA256 sha = SHA256.Create())
                    gameHash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(Application.dataPath, "Managed/Assembly-CSharp.dll")))).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex) { Log("Could not hash the game assembly: " + ex.Message); }
            LoadSettings();
            string[] args = Environment.GetCommandLineArgs();
            InitLab(args);
            InitDebug();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-st-host") autoRole = "host";
                if (args[i] == "-st-join" && i + 1 < args.Length) { autoRole = "join"; Address = args[++i]; }
                if (args[i] == "-st-name" && i + 1 < args.Length) Name = CleanName(args[++i]);
                if (args[i] == "-st-port" && i + 1 < args.Length) portText = args[++i];
                if (args[i] == "-st-auto-start") labAutoStart = true;
                if (args[i] == "-st-god") labGod = true;
                if (args[i] == "-st-specs" && i + 1 < args.Length) SpecFilter = args[++i];
                if (args[i] == "-st-mansion" && i + 1 < args.Length) labMansion = args[++i];
                if (args[i] == "-st-soak-spawn") labSoakSpawn = true;
                if (args[i] == "-st-expect" && i + 1 < args.Length) int.TryParse(args[++i], out labExpect);
                if (args[i] == "-st-auto-door") labAutoDoor = true;
                if (args[i] == "-st-shots") labShots = true;
                if (args[i] == "-st-join-lan") { labJoinLan = true; labLanListen = true; autoRole = null; }
                if (args[i] == "-st-lab-hurt") labHurt = true;
                if (args[i] == "-st-relay" && i + 1 < args.Length) { Online = true; RelayAddress = args[++i]; } // online through the global relay
                if (args[i] == "-st-modinfo" && i + 1 < args.Length) { modInfoUrl = args[++i]; modInfo = null; } // lab: mods-info from another URL
                if (args[i] == "-st-online") Online = true; // lab: online mode with the server from mods-info
                if (args[i] == "-st-join-online") { Online = true; labJoinOnline = true; autoRole = null; } // lab: join the first public online room
            }
            Application.runInBackground = true;
            Log("SpookyTogether " + ModVersion + " ready. F5 opens the multiplayer panel. Game SHA256 " + gameHash);
        }

        void Start()
        {
            if (autoRole == "host") { if (Online) StartHostOnline(); else StartHost(); }
            if (autoRole == "join") StartJoin();
        }

        public void Tell(string message)
        {
            Status = message;
            messages.Add(message); messageTimes.Add(Time.realtimeSinceStartup);
            if (messages.Count > 6) { messages.RemoveAt(0); messageTimes.RemoveAt(0); }
            Log(message);
        }

        static string SettingsPath { get { return Path.Combine(Root, "UserData/SpookyTogether.ini"); } }

        void LoadSettings()
        {
            Name = CleanName(Environment.UserName);
            if (!File.Exists(SettingsPath)) return;
            foreach (string line in File.ReadAllLines(SettingsPath))
            {
                int split = line.IndexOf('='); if (split < 0) continue;
                string key = line.Substring(0, split), value = line.Substring(split + 1);
                if (key == "Name") Name = CleanName(value);
                if (key == "Address") Address = value.Substring(0, Math.Min(128, value.Length)).Trim();
                if (key == "Port") portText = value.Trim();
                if (key == "Relay" && value.Trim().Length > 0) RelayAddress = value.Trim().Substring(0, Math.Min(128, value.Trim().Length));
                if (key == "OnlinePublic") OnlinePublic = value.Trim() != "0";
                if (key == "Mode") Online = value.Trim() == "online";
                int colour; if (key == "Colour" && int.TryParse(value, out colour)) Colour = (byte)Mathf.Clamp(colour, 0, Colours.Length - 1);
            }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, new[] { "Name=" + Name, "Address=" + Address, "Port=" + portText, "Colour=" + Colour, "Mode=" + (Online ? "online" : "lan"), "Relay=" + relayTyped, "OnlinePublic=" + (OnlinePublic ? 1 : 0) });
            }
            catch (Exception ex) { Log("Could not save settings: " + ex.Message); }
        }

        public static string CleanName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Player";
            System.Text.StringBuilder clean = new System.Text.StringBuilder();
            foreach (char c in value) if (!char.IsControl(c) && clean.Length < 16) clean.Append(c);
            string result = clean.ToString().Trim();
            return result.Length == 0 ? "Player" : result;
        }

        bool ParsePort()
        {
            int port;
            if (!int.TryParse(portText, out port) || port < 1024 || port > 65535) { Tell("Port must be between 1024 and 65535."); return false; }
            Port = port; return true;
        }

        public void StartHost()
        {
            if (Active || !ParsePort()) return;
            SaveSettings();
            try
            {
                Network = new Transport();
                Network.Host(Port, false);
                ResetSession();
                Tell("Hosting on port " + Port + ". Friends join your LAN or VPN address. Start a run from the main menu.");
            }
            catch (Exception ex) { Shutdown(); Tell("Could not host: " + ex.Message); }
        }

        public void StartJoin()
        {
            if (Active || !ParsePort()) return;
            SaveSettings();
            Network = new Transport();
            ResetSession();
            Network.Join(Address, Port);
            Tell("Connecting to " + Address + ":" + Port + "...");
        }

        public void Shutdown()
        {
            if (IsClient && runId > 0 && RunActive) { keptRunId = runId; keptRunMansion = runMansion; }
            if (Network != null)
            {
                try { if (Connected) Broadcast(Wire.Packet(Op.Leave, null)); } catch { }   // best effort: the socket may already be gone
                Network.Dispose();
            }
            Network = null;
            foreach (Peer peer in peers.Values) DestroyAvatar(peer);
            peers.Clear();
            self = -1;
            ResetSession();
        }

        void OnApplicationQuit() { Shutdown(); ShutdownLanDiscovery(); }

        void Update()
        {
            if (LabMode) { labWatch.Reset(); labWatch.Start(); }
            UpdateCore();
            if (LabMode) { labWatch.Stop(); labModTicks += labWatch.ElapsedTicks; labFrames++; }
        }

        void UpdateCore()
        {
            if (Input.GetKeyDown(KeyCode.F5)) SetPanel(!show);
            DebugUpdate();
            if (Network != null) PumpNetwork();
            if (Connected && Time.realtimeSinceStartup >= nextPose) { nextPose = Time.realtimeSinceStartup + 1f / 15f; SendPose(); }
            if (Connected && Time.realtimeSinceStartup >= nextPing)
            {
                nextPing = Time.realtimeSinceStartup + 2f;
                if (Hosting) { foreach (Peer p in peers.Values) if (p.Accepted) NotePingSent(p.Id); } else NotePingSent(0);
                Broadcast(Wire.Packet(Op.Ping, null));
            }
            UpdateLanDiscovery();
            UpdateOnline();
            UpdateRooms();
            HostTick();
            if (Hosting && RunActive) { HostEnemyTick(); HostStateTick(); }
            if (RunActive) { WeaponTick(); PropRefreshTick(); LabLookTick(); if (Time.frameCount % 30 == 0) ClampNegativeLocks(); }
            LabTick();
            LabUpdate();
            UpdateDowned();
            UpdateAvatars();
        }

        void LateUpdate()
        {
            if (LabMode) { labWatch.Reset(); labWatch.Start(); }
            LateUpdateDowned();
            if (IsClient && Connected) ClientEnemyLateUpdate();
            if (LabMode) { labWatch.Stop(); labModTicks += labWatch.ElapsedTicks; }
        }

        // --- Panel -----------------------------------------------------------------------------
        bool panelLockedPlayer;
        bool panelCursorWasLocked;

        void SetPanel(bool open)
        {
            if (show == open) return;
            show = open;
            if (open && debugOpen) { show = false; SetDebug(false); show = true; }
            GameManager gm = GameManager.GetInstance();
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (open)
            {
                if (gm != null) { panelCursorWasLocked = gm.lockCursor; gm.lockCursor = false; }
                if (player != null && player.ready) { player.AddMoveLock(); player.AddLookLock(); player.AddInteractionLock(); panelLockedPlayer = true; }
            }
            else
            {
                if (gm != null) gm.lockCursor = panelCursorWasLocked;
                if (panelLockedPlayer && player != null) { player.DecMoveLock(); player.DecLookLock(); player.DecInteractionLock(); }
                panelLockedPlayer = false;
            }
        }

        void Styles()
        {
            if (boxStyle != null) return;
            boxStyle = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.UpperLeft, padding = new RectOffset(12, 12, 10, 10) };
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            tagStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        }

        void OnGUI()
        {
            Styles();
            DrawNameTags(tagStyle);
            DrawHud();
            DrawDebug();
            if (!show) return;
            float scale = Mathf.Max(1f, Screen.height / 900f);
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            Rect area = new Rect(30, 30, 430, Active ? 520 : 680);
            GUI.Box(area, "Spooky Together " + ModVersion, boxStyle);
            GUILayout.BeginArea(new Rect(area.x + 14, area.y + 36, area.width - 28, area.height - 46));
            GUILayout.Label("Name", labelStyle);
            string name = GUILayout.TextField(Name, 16);
            if (name != Name) Name = CleanName(name);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Colour", labelStyle, GUILayout.Width(70));
            for (int i = 0; i < Colours.Length; i++)
            {
                Color previous = GUI.backgroundColor; GUI.backgroundColor = Colours[i];
                if (GUILayout.Button(i == Colour ? "X" : " ", GUILayout.Width(36))) { Colour = (byte)i; if (Connected) SendHello(); }
                GUI.backgroundColor = previous;
            }
            GUILayout.EndHorizontal();
            GUI.enabled = !Active;
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(!Online, "LAN / direct", "Button", GUILayout.Height(26)) && Online) Online = false;
            if (GUILayout.Toggle(Online, "Online (global server)", "Button", GUILayout.Height(26)) && !Online) Online = true;
            GUILayout.EndHorizontal();
            if (Online) DrawOnlinePanel(labelStyle);
            else
            {
                GUILayout.Label("Host address (for joining)", labelStyle);
                Address = GUILayout.TextField(Address, 128);
                GUILayout.Label("Port", labelStyle);
                portText = GUILayout.TextField(portText, 5);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Host", GUILayout.Height(32))) StartHost();
                if (GUILayout.Button("Join", GUILayout.Height(32))) StartJoin();
                GUILayout.EndHorizontal();
            }
            GUI.enabled = Active;
            if (GUILayout.Button(Hosting ? "Stop hosting" : "Leave", GUILayout.Height(28))) { Shutdown(); Tell("Disconnected."); }
            GUI.enabled = true;
            GUILayout.Space(6);
            GUILayout.Label(Status, labelStyle);
            if (Connected)
            {
                GUILayout.Label("Players:", labelStyle);
                GUILayout.Label("- " + Name + (Hosting ? " (host, you)" : " (you)") + (LocallyDowned ? " - down" : ""), labelStyle);
                foreach (Peer peer in peers.Values) if (peer.Accepted) GUILayout.Label("- " + peer.Name + (peer.Id == 0 ? " (host)" : "") + (peer.Downed ? " - down" : ""), labelStyle);
            }
            if (!Active) { if (Online) DrawOnlineList(labelStyle); else DrawServerList(labelStyle); }
            GUILayout.Label("F6 opens the debug menu (host only).", labelStyle);
            GUILayout.Label("Only the host starts runs. Doors any player opens move everyone on.", labelStyle);
            GUILayout.EndArea();
            GUI.matrix = old;
        }

        void DrawHud()
        {
            if (!Active) return;
            float now = Time.realtimeSinceStartup;
            float y = 8;
            string header = "Spooky Together | " + (Hosting ? "host" : Connected ? "client" : "connecting") + " | " + (1 + AcceptedCount) + " player" + (AcceptedCount == 0 ? "" : "s");
            GUI.Label(new Rect(Screen.width - 420, y, 410, 24), header, labelStyle);
            y += 22;
            string wait = WaitingText();
            if (wait != null) { GUI.Label(new Rect(Screen.width / 2 - 200, Screen.height / 2 + 40, 400, 30), wait, tagStyle); }
            for (int i = 0; i < messages.Count; i++)
            {
                if (now - messageTimes[i] > 8) continue;
                // a message that wraps takes its real height, so the next one starts below it instead of on top
                float h = Mathf.Max(22, labelStyle.CalcHeight(new GUIContent(messages[i]), 410));
                GUI.Label(new Rect(Screen.width - 420, y, 410, h), messages[i], labelStyle); y += h;
            }
        }

        // Lab automation for unattended two-instance tests (command-line flags only).
        void LabTick()
        {
            if (labJoinLan && !Active)
                foreach (LanGame game in VisibleLanGames())
                    if (game.Compatible && game.Players < 4)
                    {
                        labJoinLan = false; labLanListen = false;
                        Check("serverlist.found", true, game.Name + " at " + game.Address + ":" + game.Port);
                        Log("Lab: server list found " + game.Name + " at " + game.Address + ":" + game.Port + " (" + game.Players + "/4); joining");
                        Address = game.Address; portText = game.Port.ToString(); StartJoin();
                        break;
                    }
            if (labAutoStart && Hosting && AcceptedCount > 0 && (AcceptedCount + 1 >= labExpect ? Time.realtimeSinceStartup - lastJoinAt > 8 : Time.realtimeSinceStartup - lastJoinAt > 90))
            {
                HSMMenuState menu = Closest<HSMMenuState>();
                AssetDataManager assets = AssetDataManager.GetInstance();
                if (menu != null && assets != null && assets.allMansionDefs != null)
                {
                    MansionConfig pick = null;
                    if (!labMansionsLogged)
                    {
                        labMansionsLogged = true;
                        foreach (MansionConfig m in assets.allMansionDefs)
                            if (m != null) Log("Lab: mansion " + m.uniqueIdentifier + " usable=" + m.usable + " show=" + m.show + " endless=" + m.isEndless + " floors=" + (m.floors != null ? m.floors.Length : 0));
                    }
                    foreach (MansionConfig candidate in assets.allMansionDefs)
                    {
                        if (candidate == null) continue;
                        if (labMansion != null) { if (candidate.uniqueIdentifier == labMansion) pick = candidate; continue; }
                        if (candidate.usable && candidate.show && (pick == null || candidate.sortOrder < pick.sortOrder)) pick = candidate;
                    }
                    if (pick != null) { labAutoStart = false; Log("Lab: starting mansion " + pick.uniqueIdentifier); menu.StartMansion(pick, null); }
                }
            }
            if ((labShots || labHurt) && Connected && CurrentRoomSeq > 0 && Closest<HSMGameplayState>() != null)
            {
                if (labShotSeq != CurrentRoomSeq) { labShotSeq = CurrentRoomSeq; labShotAt = Time.realtimeSinceStartup + 5; }
                else if (labShotAt > 0 && Time.realtimeSinceStartup >= labShotAt)
                {
                    labShotAt = -1;
                    SpookyCharacterController player = SpookyCharacterController.GetInstance();
                    foreach (Peer peer in peers.Values)
                        if (player != null && peer.HasPose) Log("Lab: room " + CurrentRoomSeq + " " + peer.Name + " seq=" + peer.RoomSeq + " distance=" + Vector3.Distance(player.transform.position, peer.Position).ToString("0.0") + " avatar=" + (peer.Avatar != null && peer.Avatar.Root.activeSelf) + " downed=" + peer.Downed);
                    if (labShots) { string shot = Path.Combine(Root, "shot-" + (Hosting ? "host" : "client") + "-" + CurrentRoomSeq + ".png"); ScreenCapture.CaptureScreenshot(shot); }
                    if (labHurt && CurrentRoomSeq >= 2 && player != null && !LocallyDowned) { labHurt = false; Log("Lab: taking a lethal hit"); player.Hurt(99999, new SimpleSpecStruct { _killscreenName = "GenericGameover" }, null); }
                }
            }
            SpookyCharacterController labPlayer = SpookyCharacterController.GetInstance();
            if (labGod && labPlayer != null && labPlayer.ready && labPlayer.invulnerable == 0) labPlayer.AddInvulnerability();
            if (labSoakSpawn && Hosting && RunActive && CurrentRoomSeq >= 3 && CurrentRoomSeq != labSoakSeq && labPlayer != null && labPlayer.ready && Closest<HSMGameplayState>() != null)
            {
                labSoakSeq = CurrentRoomSeq;
                SpecimenManager manager = Specimens;
                // Queue a chaser the way the game does (the endless skip TV path): it spawns on the next room load
                // and emerges from that room's door.
                if (manager != null && manager.activeSpecimens.Count < 3 && manager.allRechasableSpecimen != null)
                {
                    PersistenceData run = PersistenceManager.GetInstance() != null ? PersistenceManager.GetInstance().currentData : null;
                    // Only this mansion's own chasers: other modes' monsters lack the door points they need.
                    List<string> pool = run != null && run.spawnableSpecimens != null ? run.spawnableSpecimens : new List<string>();
                    List<string> allowed = pool.Count > 0 ? manager.GetCurrentSpawnables(pool) : pool;
                    if (allowed.Count > 0)
                    {
                        string id = allowed[labSoakCount++ % allowed.Count];
                        if (manager.enqueuedSpecimen == null) manager.enqueuedSpecimen = new List<string>();
                        manager.enqueuedSpecimen.Add(id);
                        Log("Lab: soak queued " + id);
                    }
                }
            }
            if (labAutoDoor && Connected && !(labTour.Length > 0 && labTour != "dollprops" && labTour != "cutouts" && labTour != "elevator" && labTour != "stuck" && labTour != "look" && labTour != "spec6" && CurrentRoomSeq >= 2 && !tourDone))
            {
                HSMGameplayState gameplay = Closest<HSMGameplayState>();
                if (gameplay == null) { labDoorAt = -1; return; }
                if (labDoorAt < 0) labDoorAt = Time.realtimeSinceStartup + 10;
                if (Time.realtimeSinceStartup >= labDoorAt)
                {
                    labDoorAt = Time.realtimeSinceStartup + 12;
                    LabUseDoor();
                }
            }
        }

        // Uses a real door like a player would: an unlocked room exit, otherwise a local (sub-room) door. Forcing raw
        // transfers loads rooms a mode never reaches (e.g. a base-mansion hall inside the hospital) and breaks the game.
        void LabUseDoor()
        {
            List<DoorComponent> exits = new List<DoorComponent>();
            foreach (DoorComponent d in Object.FindObjectsOfType<DoorComponent>())
                if (d.isActiveAndEnabled && !d.isLocked && !d.isOpening) exits.Add(d);
            if (exits.Count > 0)
            {
                exits.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                DoorComponent door = exits[(SelfId + CurrentRoomSeq) % exits.Count];
                Log("Lab: opening door " + door.name + " in room seq " + CurrentRoomSeq);
                door.OnInteract();
                return;
            }
            List<LocalDoorComponent> locals = new List<LocalDoorComponent>();
            foreach (LocalDoorComponent d in Object.FindObjectsOfType<LocalDoorComponent>())
                if (d.isActiveAndEnabled && !d.isLocked) locals.Add(d);
            if (locals.Count > 0)
            {
                locals.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                LocalDoorComponent door = locals[(SelfId * 7 + labLocalCount++) % locals.Count];
                Log("Lab: using local door " + door.name);
                door.OnInteract();
                return;
            }
            // Walk-in exits (e.g. the endless start room) fire when the player's collider enters them.
            foreach (WalkIntoDoor walk in Object.FindObjectsOfType<WalkIntoDoor>())
                if (walk.isActiveAndEnabled) { Log("Lab: walking into " + walk.name); walk.ColliderEntered(); return; }
            // Weapons and boarded doors: pick up the axe, then chop the boards like a player would.
            foreach (WeaponPickupComponent pickup in Object.FindObjectsOfType<WeaponPickupComponent>())
                if (pickup.isActiveAndEnabled) { Log("Lab: picking up " + pickup.newWeapon); pickup.OnInteract(); labDoorAt = Time.realtimeSinceStartup + 2; return; }
            foreach (BoardedDoorComponent boards in Object.FindObjectsOfType<BoardedDoorComponent>())
            {
                if (!boards.isActiveAndEnabled) continue;
                SendMessageOnHit hit = boards.GetComponentInChildren<SendMessageOnHit>();
                if (hit == null) hit = boards.GetComponentInParent<SendMessageOnHit>();
                if (hit == null) continue;
                Log("Lab: chopping " + boards.name + " with " + LocalWeapon());
                AfterSwing();
                hit.TakeHit();
                labDoorAt = Time.realtimeSinceStartup + 1.5f;
                return;
            }
            // Endless start room: the exit appears after the skip-room TV; emulate its "no skip" result (host only).
            if (Hosting)
                foreach (EndlessSkipRoomUIControl tv in Object.FindObjectsOfType<EndlessSkipRoomUIControl>(true))
                {
                    if (tv.actualExitDoor == null) continue;
                    tv.actualExitDoor.SetActive(true);
                    DoorComponent exit = tv.actualExitDoor.GetComponent<DoorComponent>();
                    if (exit == null) continue;
                    exit.isLocked = false; exit.overrideNextRoom = "Hall_01"; exit.shouldOverrideNextRoom = true;
                    Log("Lab: endless start exit via the skip-room TV door");
                    exit.OnInteract();
                    return;
                }
            if (labDumpedSeq != CurrentRoomSeq)
            {
                labDumpedSeq = CurrentRoomSeq;
                System.Text.StringBuilder found = new System.Text.StringBuilder();
                foreach (MonoBehaviour mb in Object.FindObjectsOfType<MonoBehaviour>(true))
                    if (mb is InteractableInterface || mb is WalkIntoDoor || mb.GetType().Name.Contains("Door"))
                        found.Append(mb.GetType().Name).Append('(').Append(mb.name).Append(mb.isActiveAndEnabled ? "" : ",off").Append(") ");
                Log("Lab: no usable door in room seq " + CurrentRoomSeq + "; interactables: " + found);
            }
        }

        int AcceptedCount { get { int n = 0; foreach (Peer p in peers.Values) if (p.Accepted) n++; return n; } }
    }
}
