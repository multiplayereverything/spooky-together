using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpookyTogether
{
    // F6 debug menu. Room travel goes through the host so the whole team moves (a client's jump is a request to the host,
    // like a door); spawning specimens is host-only because the host runs them. Everything else acts on your own game.
    // "Save report" writes the current state, teammates, recent mod messages and recent game errors to a text file.
    public sealed partial class TogetherRuntime
    {
        bool debugOpen, debugLockedPlayer, debugCursorWasLocked;
        int debugTab;
        string roomFilter = "", specFilter = "", itemFilter = "", roomKeyText = "";
        Vector2 roomScroll, specScroll, itemScroll, errorScroll;
        bool debugGod;
        string debugStatus = "";
        static readonly string[] debugTabs = { "Rooms", "Specimens", "Player", "Info", "Errors" };
        static readonly List<string> recentErrors = new List<string>();
        static readonly List<string> recentMessages = new List<string>();
        static readonly object recentGate = new object();
        float frameMsAvg;
        readonly Dictionary<int, float> pingSentAt = new Dictionary<int, float>();
        readonly Dictionary<int, float> rttMs = new Dictionary<int, float>();
        GUIStyle debugBox, debugLabel, debugSmall;

        void InitDebug()
        {
            Application.logMessageReceivedThreaded += RecordForDebug;
            Action<string> previous = Log;
            Log = message => { previous(message); lock (recentGate) { recentMessages.Add(DateTime.Now.ToString("HH:mm:ss") + " " + message); if (recentMessages.Count > 80) recentMessages.RemoveAt(0); } };
        }

        static void RecordForDebug(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            string at = "";
            if (stackTrace != null) foreach (string line in stackTrace.Split('\n')) if (line.Trim().Length > 0) { at = line.Trim(); break; }
            lock (recentGate)
            {
                recentErrors.Add(DateTime.Now.ToString("HH:mm:ss") + " " + condition + (at.Length > 0 ? "  @ " + at : ""));
                if (recentErrors.Count > 60) recentErrors.RemoveAt(0);
            }
        }

        void SetDebug(bool open)
        {
            if (debugOpen == open) return;
            debugOpen = open;
            GameManager gm = GameManager.GetInstance();
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (open)
            {
                if (show) SetPanel(false);
                if (gm != null) { debugCursorWasLocked = gm.lockCursor; gm.lockCursor = false; }
                if (player != null && player.ready) { player.AddMoveLock(); player.AddLookLock(); player.AddInteractionLock(); debugLockedPlayer = true; }
            }
            else
            {
                if (gm != null) gm.lockCursor = debugCursorWasLocked;
                if (debugLockedPlayer && player != null) { player.DecMoveLock(); player.DecLookLock(); player.DecInteractionLock(); }
                debugLockedPlayer = false;
            }
        }

        void DebugUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F6))
            {
                // Host only: a connected client cannot open it.
                if (IsClient) Tell("Only the host can use the debug menu.");
                else SetDebug(!debugOpen);
            }
            frameMsAvg = Mathf.Lerp(frameMsAvg, Time.unscaledDeltaTime * 1000f, 0.05f);
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            // God mode survives room changes (the game resets invulnerability at mansion start only).
            if (debugGod && player != null && player.ready && player.invulnerable == 0) player.AddInvulnerability();
        }

        // Round-trip time: the host pings every peer, a client pings the host; the Pong answers.
        void NotePingSent(int peer) { pingSentAt[peer] = Time.realtimeSinceStartup; }
        void NotePong(int peer) { float sent; if (pingSentAt.TryGetValue(peer, out sent)) rttMs[peer] = (Time.realtimeSinceStartup - sent) * 1000f; }

        void DebugStyles()
        {
            if (debugBox != null) return;
            debugBox = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.UpperLeft, padding = new RectOffset(10, 10, 8, 8) };
            debugLabel = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
            debugSmall = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
        }

        void DrawDebug()
        {
            if (!debugOpen) return;
            DebugStyles();
            float scale = Mathf.Max(1f, Screen.height / 900f);
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            float w = 620, h = Mathf.Min(760, Screen.height / scale - 40);
            Rect area = new Rect(Screen.width / scale - w - 20, 20, w, h);
            GUI.Box(area, "Spooky Together debug (F6)", debugBox);
            GUILayout.BeginArea(new Rect(area.x + 10, area.y + 30, area.width - 20, area.height - 40));
            debugTab = GUILayout.Toolbar(debugTab, debugTabs, GUILayout.Height(26));
            if (debugStatus.Length > 0) GUILayout.Label(debugStatus, debugSmall);
            switch (debugTab)
            {
                case 0: DebugRooms(); break;
                case 1: DebugSpecimens(); break;
                case 2: DebugPlayer(); break;
                case 3: DebugInfo(); break;
                case 4: DebugErrors(); break;
            }
            GUILayout.EndArea();
            GUI.matrix = old;
        }

        // --- Rooms ----------------------------------------------------------------------------------------------
        void DebugRooms()
        {
            HSMFloorState floor = Closest<HSMFloorState>();
            GUILayout.Label(RunActive ? "In room: " + CurrentRoomId() + " (sequence " + CurrentRoomSeq + ")" : "Start a run first (rooms load inside a run).", debugLabel);
            if (IsClient && Connected) GUILayout.Label("You are a client: a jump is sent to the host and moves the whole team.", debugSmall);
            GUILayout.BeginHorizontal();
            GUI.enabled = floor != null;
            if (GUILayout.Button("Next room")) { DebugTravel(null, false); }
            if (GUILayout.Button("Reload room")) { string id = CurrentRoomId(); if (id != null) DebugTravel(id, true); }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Room number", debugLabel, GUILayout.Width(110));
            roomKeyText = GUILayout.TextField(roomKeyText, 6, GUILayout.Width(80));
            int roomNumber;
            if (GUILayout.Button("Set", GUILayout.Width(60)) && int.TryParse(roomKeyText, out roomNumber) && CurrentData != null)
            {
                CurrentData.SetKey("room", Mathf.Clamp(roomNumber, 0, 100000));
                debugStatus = "Room counter set to " + roomNumber + " (shown from the next room).";
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Go to room (type to filter):", debugLabel);
            roomFilter = GUILayout.TextField(roomFilter, 60);
            AssetDataManager assets = AssetDataManager.GetInstance();
            if (assets == null || assets.allRoomDefs == null) return;
            List<string> ids = new List<string>(assets.allRoomDefs.Keys);
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            roomScroll = GUILayout.BeginScrollView(roomScroll);
            int shown = 0;
            foreach (string id in ids)
            {
                if (roomFilter.Length > 0 && id.IndexOf(roomFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++shown > 250) { GUILayout.Label("... more; type to narrow the list", debugSmall); break; }
                GUI.enabled = floor != null;
                if (GUILayout.Button(id, GUILayout.Height(22))) DebugTravel(id, true);
                GUI.enabled = true;
            }
            GUILayout.EndScrollView();
        }

        string CurrentRoomId()
        {
            HSMRoomReadyState room = Closest<HSMRoomReadyState>();
            if (room != null && room.roomRuntimeData != null && room.roomRuntimeData.roomConfig != null) return room.roomRuntimeData.roomConfig.uniqueRoomId;
            return lastRoom != null ? lastRoom.RoomId : pendingRoom != null ? pendingRoom.RoomId : null;
        }

        // The game's own transfer (as a door with a destination). On a client it becomes a request to the host.
        void DebugTravel(string roomId, bool noIncrease)
        {
            HSMFloorState floor = Closest<HSMFloorState>();
            if (floor == null || Closest<HSMGameplayState>() == null) { debugStatus = "Not in a room right now."; return; }
            floor.TransferRequest(null, roomId, noIncrease);
            debugStatus = (IsClient && Connected ? "Asked the host to go to " : "Going to ") + (roomId ?? "the next room") + ".";
            Log("Debug: travel to " + (roomId ?? "next room"));
        }

        // --- Specimens ------------------------------------------------------------------------------------------
        void DebugSpecimens()
        {
            SpecimenManager manager = Specimens;
            AssetDataManager assets = AssetDataManager.GetInstance();
            bool canSpawn = RunActive && (!Connected || Hosting) && Closest<HSMRoomReadyState>() != null;
            if (Connected && IsClient) GUILayout.Label("Specimens are run by the host; only the host can spawn or clear them.", debugSmall);
            GUILayout.Label("Live specimens:", debugLabel);
            if (manager != null && manager.activeSpecimens != null)
                foreach (GameObject spec in manager.activeSpecimens)
                    if (spec != null) GUILayout.Label("  " + spec.name + "  hp " + HpOf(spec) + "  " + (spec.activeSelf ? "active" : "parked") + "  at " + spec.transform.position.ToString("F1"), debugSmall);
            if (IsClient && Connected) GUILayout.Label("  host-driven copies here: " + BoundPuppets + " of " + hostSpecCount + ", unpaired local: " + unpaired.Count, debugSmall);
            GUI.enabled = canSpawn && manager != null;
            if (GUILayout.Button("Clear all specimens")) { manager.ClearSpecimens(); debugStatus = "Cleared specimens."; }
            GUI.enabled = true;
            GUILayout.Label("Spawn (as the game's scripted chasers do, from your door):", debugLabel);
            specFilter = GUILayout.TextField(specFilter, 40);
            if (assets == null || assets.specConfigs == null) return;
            List<string> ids = new List<string>(assets.specConfigs.Keys);
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            specScroll = GUILayout.BeginScrollView(specScroll);
            foreach (string id in ids)
            {
                if (specFilter.Length > 0 && id.IndexOf(specFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                GUI.enabled = canSpawn;
                if (GUILayout.Button(id, GUILayout.Height(22))) DebugSpawn(id);
                GUI.enabled = true;
            }
            GUILayout.EndScrollView();
        }

        void DebugSpawn(string id)
        {
            HSMRoomReadyState room = Closest<HSMRoomReadyState>();
            if (room == null || room.roomRuntimeData == null || room.roomRuntimeData.playerSpawn == null) { debugStatus = "No door to spawn from in this room."; return; }
            try
            {
                GameObject spec = Specimens.StartSpecimen(id, room.roomRuntimeData.playerSpawn.transform, true);
                if (spec != null) { SpecimenInterface si = spec.GetComponent<SpecimenInterface>(); if (si != null) si.StartRoom(room.roomRuntimeData.playerSpawn); }
                debugStatus = spec != null ? "Spawned " + id + "." : "The game refused to spawn " + id + " (specimens option off, or not a chaser).";
                Log("Debug: spawn " + id + " -> " + (spec != null));
            }
            catch (Exception ex) { debugStatus = "Spawning " + id + " failed: " + ex.Message; }
        }

        // --- Player ---------------------------------------------------------------------------------------------
        void DebugPlayer()
        {
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (player == null) { GUILayout.Label("No player yet.", debugLabel); return; }
            GUILayout.Label("Health " + (player.currentHealth != null ? player.currentHealth.currentValue.ToString("0") : "?") + "   stamina " + (player.currentStamina != null ? player.currentStamina.currentValue.ToString("0") : "?") + "   weapon " + LocalWeapon() + (LocallyDowned ? "   DOWN" : ""), debugLabel);
            GUILayout.BeginHorizontal();
            bool god = GUILayout.Toggle(debugGod, " God mode (nothing hurts you)");
            if (god != debugGod) { debugGod = god; if (!god && player.invulnerable > 0) player.DecInvulnerability(); }
            if (GUILayout.Button("Refill health and stamina") && player.currentHealth != null) { player.currentHealth.Fill(); if (player.currentStamina != null) player.currentStamina.Fill(); }
            GUI.enabled = LocallyDowned;
            if (GUILayout.Button("Revive now")) { AfterRoomReady(); debugStatus = "Revived."; }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Teleport to a teammate:", debugLabel);
            GUILayout.BeginHorizontal();
            foreach (Peer peer in peers.Values)
            {
                GUI.enabled = peer.HasPose && peer.RoomSeq == CurrentRoomSeq && player.ready;
                if (GUILayout.Button(peer.Name)) DebugTeleport(player, peer.ShownPosition + Vector3.right * 0.8f);
                GUI.enabled = true;
            }
            if (peers.Count == 0) GUILayout.Label("(nobody connected)", debugSmall);
            GUILayout.EndHorizontal();
            AssetDataManager assets = AssetDataManager.GetInstance();
            if (assets == null) return;
            GUILayout.Label("Give weapon:", debugLabel);
            GUILayout.BeginHorizontal();
            if (assets.allWeapons != null) foreach (string id in assets.allWeapons.Keys) if (GUILayout.Button(id)) { GrantWeapon(id); debugStatus = "Gave " + id + "."; }
            GUILayout.EndHorizontal();
            GUILayout.Label("Give item (shared with the team's inventory):", debugLabel);
            itemFilter = GUILayout.TextField(itemFilter, 40);
            if (assets.allInventoryItems == null) return;
            List<string> items = new List<string>(assets.allInventoryItems.Keys);
            items.Sort(StringComparer.OrdinalIgnoreCase);
            itemScroll = GUILayout.BeginScrollView(itemScroll);
            foreach (string id in items)
            {
                if (itemFilter.Length > 0 && id.IndexOf(itemFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                GUI.enabled = RunActive && CurrentData != null;
                if (GUILayout.Button(id, GUILayout.Height(22))) { CurrentData.AddToInventory(id); RefreshInventory(); debugStatus = "Added " + id + "."; }
                GUI.enabled = true;
            }
            GUILayout.EndScrollView();
        }

        static void DebugTeleport(SpookyCharacterController player, Vector3 to)
        {
            CharacterController body = player.GetComponent<CharacterController>();
            bool was = body != null && body.enabled;
            if (body != null) body.enabled = false;
            player.transform.position = to;
            if (body != null) body.enabled = was;
        }

        // --- Info and errors ------------------------------------------------------------------------------------
        void DebugInfo()
        {
            GUILayout.Label(DebugSummary(), debugSmall);
            if (GUILayout.Button("Save report (send this file when reporting a bug)", GUILayout.Height(28))) SaveDebugReport();
        }

        string DebugSummary()
        {
            StringBuilder b = new StringBuilder();
            PersistenceData data = CurrentData;
            b.AppendLine("Spooky Together " + ModVersion + ", protocol " + Wire.Version + ", " + (Hosting ? "host" : IsClient ? (Connected ? "client " + self : "connecting") : "offline"));
            b.AppendLine("Run " + runId + " mansion " + (runMansion ?? "-") + "   active " + RunActive);
            b.AppendLine("Room " + (CurrentRoomId() ?? "-") + "   sequence " + CurrentRoomSeq + "   seed " + (hasSeed ? currentSeed.ToString() : "-"));
            if (data != null) b.AppendLine("Room counter " + data.GetKey("room") + "   floor " + data.GetKey("floor") + "   violence " + data.GetKey("violence") + "   items " + (data.inventory != null ? data.inventory.Count : 0));
            b.AppendLine("State checksum " + StateHash(data) + (look != null ? "   room look " + string.Join(" / ", look) : ""));
            b.AppendLine("Frame " + frameMsAvg.ToString("0.0") + " ms   specimens " + (Hosting ? hostSpecs.Count : BoundPuppets) + (IsClient ? " (host has " + hostSpecCount + ")" : ""));
            b.AppendLine("Downed " + LocallyDowned + "   minigame " + (activeMinigame != null ? activeMinigame.name : "-") + "   stale locks released " + StaleLocksReleased);
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (player != null) b.AppendLine("Position " + player.transform.position.ToString("F1") + "   can move " + player.CanMove() + "   ready " + player.ready);
            b.AppendLine("Teammates:");
            foreach (Peer p in peers.Values)
            {
                float rtt; string ping = rttMs.TryGetValue(Hosting ? p.Id : 0, out rtt) ? rtt.ToString("0") + " ms" : "?";
                b.AppendLine("  " + p.Name + " (id " + p.Id + ")  room seq " + p.RoomSeq + (p.Downed ? "  DOWN" : "") + "  weapon " + p.Weapon + "  pose age " + (Time.realtimeSinceStartup - p.PoseAt).ToString("0.0") + " s  ping " + ping);
            }
            int errors; lock (recentGate) errors = recentErrors.Count;
            b.AppendLine("Recent game errors: " + errors + " (Errors tab)");
            return b.ToString();
        }

        void DebugErrors()
        {
            List<string> copy;
            lock (recentGate) copy = new List<string>(recentErrors);
            if (GUILayout.Button("Clear list")) lock (recentGate) recentErrors.Clear();
            errorScroll = GUILayout.BeginScrollView(errorScroll);
            if (copy.Count == 0) GUILayout.Label("No game errors since start.", debugSmall);
            for (int i = copy.Count - 1; i >= 0; i--) GUILayout.Label(copy[i], debugSmall);
            GUILayout.EndScrollView();
        }

        void SaveDebugReport()
        {
            try
            {
                string dir = Path.Combine(Root, "UserData/SpookyTogether");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                StringBuilder b = new StringBuilder();
                b.AppendLine(DebugSummary());
                SpecimenManager manager = Specimens;
                b.AppendLine("Specimens here:");
                if (manager != null && manager.activeSpecimens != null)
                    foreach (GameObject spec in manager.activeSpecimens) if (spec != null) b.AppendLine("  " + spec.name + " hp " + HpOf(spec) + " active " + spec.activeSelf + " at " + spec.transform.position);
                PersistenceData data = CurrentData;
                b.AppendLine("Shared keys:");
                foreach (KeyValuePair<string, int> pair in SharedKeys(data)) b.AppendLine("  " + pair.Key + " = " + pair.Value);
                if (data != null && data.inventory != null) b.AppendLine("Inventory: " + string.Join(", ", data.inventory.ToArray()));
                b.AppendLine("Camera: " + (Camera.main != null ? Camera.main.name + " enabled " + Camera.main.enabled : "none"));
                b.AppendLine("Screen effects on the camera:");
                SpookyCharacterController player = SpookyCharacterController.GetInstance();
                if (player != null && player.myCamera != null) foreach (MonoBehaviour fx in player.myCamera.GetComponents<MonoBehaviour>()) b.AppendLine("  " + fx.GetType().Name + (fx.enabled ? "" : " (off)"));
                lock (recentGate)
                {
                    b.AppendLine("Recent mod messages:"); foreach (string line in recentMessages) b.AppendLine("  " + line);
                    b.AppendLine("Recent game errors:"); foreach (string line in recentErrors) b.AppendLine("  " + line);
                }
                File.WriteAllText(path, b.ToString());
                debugStatus = "Saved " + path;
                Log("Debug report saved: " + path);
            }
            catch (Exception ex) { debugStatus = "Could not save the report: " + ex.Message; }
        }
    }
}
