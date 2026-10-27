using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SpookyTogether
{
    // Host-led progression. The host's own HSMFloorState picks every room; each HSMLoadRoomState on the host announces
    // the room id, spawn door and a per-room seed, and every client's HSMLoadRoomState waits for that announcement and
    // loads the same scene. Client doors only ask the host to move everyone on.
    public sealed partial class TogetherRuntime
    {
        sealed class RoomInfo { public int Run, Seq, RoomKey, FloorKey, Seed, RootOffset; public string RoomId, Door; }

        int runId, hostSeq;
        string runMansion;
        RoomInfo lastRoom, pendingRoom;
        int consumedSeq, interruptedSeq;
        public int CurrentRoomSeq = -1;
        int currentSeed;
        bool hasSeed;
        string pendingStartMansion;
        float leaveRequestedAt = -100;
        bool waitingForHost;
        string hostDoorDoor, hostDoorRoom;
        bool hostDoorNoIncrease;
        float hostDoorAt = -100;
        int hostDoorSeq = -1;
        readonly System.Random seeds = new System.Random();
        public static bool AllowStart, BypassTransfer;

        public static HSMGameManager Hsm { get { try { return ServiceLocator.GetService<HSMGameManager>(); } catch { return null; } } }   // not registered yet at startup
        static T Closest<T>() where T : HSMBaseState { HSMGameManager gm = Hsm; if (gm == null || gm.hsm == null) return null; try { return gm.ClosestState<T>(); } catch { return null; } }   // no such state active
        public bool RunActive { get { return Closest<HSMMansionState>() != null; } }
        public bool HasSeed { get { return Connected && hasSeed; } }
        public int RoomSeed { get { return currentSeed; } }

        // A client that loses the host keeps its run identity, so reconnecting resumes the same run instead of restarting it.
        // The host's session id tells a reconnect apart from a host that restarted and began a new run 1.
        int keptRunId, keptRunSession; string keptRunMansion;
        int hostSession, runSession;
        // A client that lost the host and loaded rooms alone is no longer in the host's room.
        bool strayed;

        void ResetSession()
        {
            hostSession = seeds.Next(1, int.MaxValue); runSession = 0;
            runId = 0; hostSeq = 0; runMansion = null; lastRoom = null; pendingRoom = null;
            consumedSeq = 0; interruptedSeq = 0; pendingStartMansion = null; hasSeed = false; waitingForHost = false;
            hostDoorAt = -100;
            ResetDowned();
            ResetEnemies();
        }

        string WaitingText()
        {
            if (waitingForHost) return "Waiting for the host to open the next room...";
            if (pendingStartMansion != null) return "The host started a run. Joining...";
            return null;
        }

        // --- Starting and ending runs -------------------------------------------------------------
        public static bool BeforeStartMansion(MansionConfig mansionConfig)
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.IsClient || AllowStart) return true;
            rt.Tell("Only the host can start a run. You'll join automatically when they do.");
            return false;
        }

        public static void AfterStartMansion(MansionConfig mansionConfig)
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.Hosting || mansionConfig == null) return;
            // Lab: lab windows can still take controller or keyboard input and start a run from the menu, sometimes before
            // every client has joined (takerbad, 2026-10-27). Any run the lab's auto-start did not begin, and any run of
            // the wrong mansion, goes back to the menu, and the lab starts its own.
            if (LabMode && (rt.labAutoStart || (rt.labMansion != null && mansionConfig.uniqueIdentifier != rt.labMansion)))
            {
                Log("Lab: " + mansionConfig.uniqueIdentifier + " started without the lab; going back for " + (rt.labMansion ?? "the lab's own run"));
                rt.labAutoStart = true; rt.labRestartRun = true;
                return;
            }
            rt.runId++; rt.hostSeq = 0; rt.lastRoom = null; rt.runMansion = mansionConfig.uniqueIdentifier;
            rt.ResetDowned();
            foreach (Peer peer in rt.peers.Values) { peer.Downed = false; peer.RoomSeq = -1; }
            rt.Broadcast(rt.StartRunPacket());
            rt.Tell("Run started: " + rt.runMansion + ". Teammates are joining.");
        }

        byte[] StartRunPacket() { return Wire.Packet(Op.StartRun, w => { w.Write(runId); Wire.Text(w, runMansion); w.Write(hostSession); }); }

        void SendRunTo(int id)
        {
            if (!RunActive || runMansion == null) return;
            Network.Send(id, StartRunPacket());
            if (lastRoom != null) { Network.Send(id, RoomPacket(lastRoom)); string[] names = CurrentLook(); int seq = lastRoom.Seq; if (names != null) Network.Send(id, Wire.Packet(Op.RoomLook, w => { w.Write(seq); foreach (string n in names) Wire.Text(w, n); })); }
            SendSpecimensTo(id);
            Network.Send(id, FullStatePacket());
        }

        void ClientStartRun(BinaryReader reader)
        {
            int run = reader.ReadInt32();
            string mansion = Wire.Text(reader, 128);
            int session = reader.ReadInt32();
            // Reconnected into the run we are already playing: keep going, the room and state packets follow.
            if (RunActive && ((run == runId && mansion == runMansion && session == runSession) || (run == keptRunId && mansion == keptRunMansion && session == keptRunSession)))
            {
                runId = run; runMansion = mansion; runSession = session; keptRunId = 0;
                // Still in the room the host announced last: no reload unless the host has moved on (or we wandered off alone).
                consumedSeq = interruptedSeq = strayed ? 0 : Math.Max(0, CurrentRoomSeq); pendingRoom = null; strayed = false;
                AdoptStraySpecimens();
                Tell("Rejoined the host's run.");
                if (LabMode) Check("reconnect.rejoined", true, "run " + run);
                return;
            }
            keptRunId = 0; strayed = false;
            runId = run; runMansion = mansion; runSession = session; pendingStartMansion = mansion;
            pendingRoom = null; consumedSeq = 0; interruptedSeq = 0; hasSeed = false;
            ResetDowned();
            ResetEnemies();
            Tell("The host started a run.");
        }

        float lateLookUntil = -1;
        bool LateLookHold()
        {
            if (pendingRoom == null || pendingRoom.Seq < 2) return true;
            if (lateLookUntil < 0) lateLookUntil = Time.realtimeSinceStartup + 8;
            return Time.realtimeSinceStartup < lateLookUntil;
        }

        void ClientEndRun()
        {
            // Lab (loadending): the host's end-of-run follows its ending at once; it waits with the held ending.
            if (labHeldEnding != null) { labHeldEndRun = true; return; }
            pendingStartMansion = null; pendingRoom = null; runMansion = null;
            // Right after an ending the killscreen is taking this game out of the run itself (it may not have yet this
            // frame): check in 10 s instead, and leave then if it is still in the run.
            if (Time.realtimeSinceStartup - clientEndingAt < 15) { if (!Mutant("endingrescue")) endRunCheckAt = Time.realtimeSinceStartup + 10; return; }
            Tell("The host left the run.");
            if (RunActive) LeaveRun();
        }

        void LeaveRun()
        {
            if (Time.realtimeSinceStartup - leaveRequestedAt < 3) return;
            leaveRequestedAt = Time.realtimeSinceStartup;
            HSMGameManager gm = Hsm;
            if (gm == null) return;
            try
            {
                MansionManager mansion = ServiceLocator.GetService<MansionManager>();
                if (mansion != null) mansion.isPaused = false;
                gm.hsm.PopTo<HSMApplicationState>();
            }
            catch (Exception ex) { Log("Leaving the run failed: " + ex); }
        }

        public static void MansionExited()
        {
            TogetherRuntime rt = Instance;
            if (rt == null) return;
            rt.CurrentRoomSeq = -1; rt.hasSeed = false; rt.waitingForHost = false;
            bool wasDowned = rt.LocallyDowned, gameOver = rt.gameOverSent;
            rt.ResetDowned();
            // Also after an ending: a client that missed the ending is then told the run is over.
            if (rt.Hosting && rt.runMansion != null)
            {
                rt.Broadcast(Wire.Packet(Op.EndRun, null));
                if (!gameOver) rt.Tell("Run ended.");
            }
            if (rt.Hosting) rt.runMansion = null;
            rt.RestoreOptions(); rt.pendingFullState = null; rt.pendingDeltas.Clear(); rt.endingSent = null; rt.roomLoads = 0;
            rt.EndPuppets();
            rt.ResetEnemies();
            if (wasDowned) rt.SendStatus(false);
        }

        void UpdateRooms()
        {
            if (!IsClient || !Connected) return;
            if (pendingStartMansion != null)
            {
                HSMMenuState menu = Closest<HSMMenuState>();
                // Lab (latelook): C3 joins the run late, 8 s after the host reached its second room.
                bool labHold = LabMode && labTour == "latelook" && SelfId == 3 && LateLookHold();
                if (menu != null && !labHold)
                {
                    MansionConfig config = null;
                    AssetDataManager assets = AssetDataManager.GetInstance();
                    if (assets != null && assets.allMansionDefs != null)
                        foreach (MansionConfig candidate in assets.allMansionDefs) if (candidate != null && candidate.uniqueIdentifier == pendingStartMansion) config = candidate;
                    string wanted = pendingStartMansion;
                    pendingStartMansion = null;
                    if (config == null) { Tell("You don't have the host's mansion '" + wanted + "'."); return; }
                    AllowStart = true;
                    try { menu.StartMansion(config, null); }
                    finally { AllowStart = false; }
                }
                else if (RunActive) LeaveRun();
                return;
            }
            if (pendingRoom != null && pendingRoom.Run == runId && pendingRoom.Seq > consumedSeq && pendingRoom.Seq > interruptedSeq && RunActive)
            {
                HSMGameplayState gameplay = Closest<HSMGameplayState>();
                HSMFloorState floor = Closest<HSMFloorState>();
                if (gameplay != null && floor != null && !gameplay.interrupt)
                {
                    interruptedSeq = pendingRoom.Seq;
                    PrepareForcedTransfer();
                    // The host's room counter stayed put (a Dollhouse floor break, a scripted walk-in): so does ours.
                    PersistenceData save = CurrentData;
                    bool noIncrease = save != null && pendingRoom.Seq == consumedSeq + 1 && save.GetKey("room") == pendingRoom.RoomKey;
                    BypassTransfer = true;
                    try { floor.TransferRequest(null, null, noIncrease); }
                    finally { BypassTransfer = false; }
                }
            }
        }

        // --- Room announcements --------------------------------------------------------------------
        static byte[] RoomPacket(RoomInfo info)
        {
            return Wire.Packet(Op.Room, w =>
            {
                w.Write(info.Run); w.Write(info.Seq); Wire.Text(w, info.RoomId); Wire.Text(w, info.Door ?? "");
                w.Write(info.RoomKey); w.Write(info.FloorKey); w.Write(info.Seed); w.Write(info.RootOffset);
            });
        }

        void ClientRoom(BinaryReader reader)
        {
            RoomInfo info = new RoomInfo { Run = reader.ReadInt32(), Seq = reader.ReadInt32(), RoomId = Wire.Text(reader, 200), Door = Wire.Text(reader, 200), RoomKey = reader.ReadInt32(), FloorKey = reader.ReadInt32(), Seed = reader.ReadInt32(), RootOffset = reader.ReadInt32() };
            if (info.Door.Length == 0) info.Door = null;
            if (info.Run != runId) return;
            if (pendingRoom == null || info.Seq > pendingRoom.Seq) pendingRoom = info;
        }

        public static IEnumerator<HSMBaseState> WrapLoad(HSMLoadRoomState state, IEnumerator<HSMBaseState> original)
        {
            HSMSystemsLoadState systems = state.pushOnComplete as HSMSystemsLoadState;
            TogetherRuntime rt = Instance;
            if (systems != null && rt != null && !rt.Connected && rt.keptRunId > 0) rt.strayed = true;
            if (rt != null) rt.roomLoads++;
            try
            {
                if (systems != null && rt != null && rt.Connected)
                {
                    rt.ReleaseStaleLocks();
                    // A failure here must not escape into the game's room-load state machine and stall it.
                    if (rt.Hosting) { try { rt.HostRoomLoading(systems); } catch (Exception ex) { Log("Announcing the room failed: " + ex); } }
                    else
                    {
                        while (rt.IsClient && rt.Connected && !(rt.pendingRoom != null && rt.pendingRoom.Run == rt.runId && rt.pendingRoom.Seq > rt.consumedSeq))
                        {
                            rt.waitingForHost = true;
                            yield return state;
                        }
                        rt.waitingForHost = false;
                        if (rt.IsClient && rt.Connected) { try { rt.ApplyRoom(state, systems); } catch (Exception ex) { Log("Following the host's room failed: " + ex); } }
                        if (rt.labHeldEnding != null)
                        {
                            string held = rt.labHeldEnding; rt.labHeldEnding = null; Log("Lab: the ending arrives during this load");
                            rt.ClientEnding(held);
                            if (rt.labHeldEndRun) { rt.labHeldEndRun = false; rt.ClientEndRun(); }
                        }
                    }
                }
                while (original.MoveNext()) yield return original.Current;
            }
            // Room ready zeroes the count while this iterator may still be finishing: never below 0.
            finally { if (rt != null && rt.roomLoads > 0) rt.roomLoads--; }
        }
        // Room loads in progress here (WrapLoad running): a held ending waits for them. Also zeroed when a room is ready
        // or the run ends, in case the game drops a load's enumerator without disposing it.
        int roomLoads;

        void HostRoomLoading(HSMSystemsLoadState systems)
        {
            HSMRoomReadyState.RoomRuntimeData data = systems.roomRuntimeData;
            if (data == null || data.roomConfig == null) return;
            PersistenceData save = PersistenceManager.GetInstance() != null ? PersistenceManager.GetInstance().currentData : null;
            RoomInfo info = new RoomInfo
            {
                Run = runId, Seq = ++hostSeq, RoomId = data.roomConfig.uniqueRoomId, Door = data.namedSpawnDoor,
                RoomKey = save != null ? save.GetKey("room") : 0, FloorKey = save != null ? save.GetKey("floor") : 0, Seed = seeds.Next(),
                RootOffset = RootFloorOffset()
            };
            lastRoom = info;
            if (info.Seq == 1) Broadcast(FullStatePacket());
            // Lab: a key set right after the run's snapshot reaches every client before that snapshot is applied.
            if (info.Seq == 1 && LabMode && labTour == "latekey" && save != null) save.SetKey("st_latekey", 1);
            // Lab: the team's ending reaches every client the moment it starts loading the third room.
            if (info.Seq == 3 && LabMode && labTour == "loadending") labEndingAt = Time.realtimeSinceStartup + 5f;
            CurrentRoomSeq = info.Seq; currentSeed = info.Seed; hasSeed = true;
            // Everyone revives in the next room, so nobody counts as down while it loads.
            foreach (Peer peer in peers.Values) peer.Downed = false;
            Broadcast(RoomPacket(info));
            HostSendLook(info.Seq);
            Log("Room " + info.Seq + ": " + info.RoomId + " door=" + (info.Door ?? "-") + " room#=" + info.RoomKey);
        }

        void ApplyRoom(HSMLoadRoomState state, HSMSystemsLoadState systems)
        {
            RoomInfo info = pendingRoom;
            consumedSeq = info.Seq; if (interruptedSeq < info.Seq) interruptedSeq = info.Seq;
            CurrentRoomSeq = info.Seq; currentSeed = info.Seed; hasSeed = true;
            RoomConfig config;
            AssetDataManager assets = AssetDataManager.GetInstance();
            if (assets == null || assets.allRoomDefs == null || !assets.allRoomDefs.TryGetValue(info.RoomId, out config) || config == null)
            {
                Tell("Missing room '" + info.RoomId + "'; loading your own room instead.");
                return;
            }
            systems.roomRuntimeData.roomConfig = config;
            systems.roomRuntimeData.namedSpawnDoor = info.Door;
            state.sceneToLoad = config.sceneName;
            PersistenceData save = PersistenceManager.GetInstance() != null ? PersistenceManager.GetInstance().currentData : null;
            if (save != null) { save.SetKey("room", info.RoomKey); save.SetKey("floor", info.FloorKey); }
            AlignFloor(info);
            ApplyPendingState();
            Log("Following host into room " + info.Seq + ": " + info.RoomId + " (" + config.sceneName + ")");
        }

        // How far the host is into its current floor, or -1 between floors (its transition rooms) and in Endless.
        static int RootFloorOffset()
        {
            HSMFloorState floor = Closest<HSMFloorState>();
            if (floor == null || floor.activeFloors == null || floor.activeFloors.Count == 0) return -1;
            FloorConfigState root = floor.activeFloors[0];
            if (root == null || root.floorConfig == null || root.relativeRoomOffset >= root.floorConfig.numRooms) return -1;
            return root.relativeRoomOffset;
        }

        // A client's own floor counts rooms too (its room events, its floor's end). A client that joined mid-run, or
        // whose count drifted, would end floors (and on the last floor, its run) at the wrong time: put its root floor
        // where the host's is.
        void AlignFloor(RoomInfo info)
        {
            if (info.RootOffset < 0) return;
            HSMFloorState floor = Closest<HSMFloorState>();
            HSMMansionState mansion = Closest<HSMMansionState>();
            if (floor == null || mansion == null || mansion.mansion == null || mansion.mansion.isEndless || floor.activeFloors == null || floor.activeFloors.Count == 0) return;
            if (info.FloorKey < 0 || mansion.mansion.floors == null || info.FloorKey >= mansion.mansion.floors.Length) return;
            FloorConfig wanted = mansion.mansion.floors[info.FloorKey];
            FloorConfigState root = floor.activeFloors[0];
            if (root != null && root.floorConfig == wanted && floor.rootFloor == wanted)
            {
                if (root.relativeRoomOffset == info.RootOffset) return;
                Log("Floor offset " + root.relativeRoomOffset + " -> the host's " + info.RootOffset);
                root.relativeRoomOffset = info.RootOffset;
                while (root.roomEvents.Count > 0 && root.roomEvents[0].relativeRoom <= info.RootOffset) root.roomEvents.RemoveAt(0);
                return;
            }
            FloorConfigState fresh = new FloorConfigState();
            fresh.Initialize(mansion.mansion, wanted, info.RootOffset, false, info.FloorKey);
            // The room event at this offset already ran on the host; it must not fire again here on the next room.
            fresh.ClearActiveRoomEvent();
            floor.rootFloor = wanted;
            floor.activeFloors[0] = fresh;
            if (wanted.unlockCatDosSpecimen != null && mansion.currentData != null) mansion.currentData.UnlockCatDosSpecs(wanted.unlockCatDosSpecimen);
            Log("Floor moved to the host's floor " + info.FloorKey + " at room offset " + info.RootOffset);
        }

        // The same seed on every machine picks the same spawn door, so teammates appear together.
        public static void AfterRoomReadyEnter(HSMRoomReadyState state, bool unnamed)
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.HasSeed || !unnamed || state.roomRuntimeData == null) return;
            List<SpawnDoor> spawns = state.roomRuntimeData.spawns;
            if (spawns == null || spawns.Count == 0) return;
            int index = (rt.currentSeed & 0x7fffffff) % spawns.Count;
            state.roomRuntimeData.playerSpawn = spawns[index];
            Log("Spawn door " + index + "/" + spawns.Count + " " + spawns[index].name);
        }

        // --- Doors ---------------------------------------------------------------------------------
        public static bool BeforeTransferRequest(string door, string room, bool noIncrease)
        {
            TogetherRuntime rt = Instance;
            if (rt == null || BypassTransfer || !rt.IsClient || !rt.Connected || !rt.RunActive) return true;
            if (rt.LocallyDowned) return false;
            int seq = rt.CurrentRoomSeq;
            rt.SendToHost(Wire.Packet(Op.Door, w =>
            {
                w.Write(seq);
                w.Write(door != null); if (door != null) Wire.Text(w, door);
                w.Write(room != null); if (room != null) Wire.Text(w, room);
                w.Write(noIncrease);
            }));
            return false;
        }

        void HostDoorRequest(Peer peer, BinaryReader reader)
        {
            int seq = reader.ReadInt32();
            string door = reader.ReadBoolean() ? Wire.Text(reader, 200) : null;
            string room = reader.ReadBoolean() ? Wire.Text(reader, 200) : null;
            bool noIncrease = reader.ReadBoolean();
            // A request made in an earlier room (two players at two doors at once) must not skip the next room.
            if (peer.Downed || !RunActive || seq != CurrentRoomSeq) return;
            hostDoorSeq = seq; hostDoorDoor = door; hostDoorRoom = room; hostDoorNoIncrease = noIncrease; hostDoorAt = Time.realtimeSinceStartup;
            Notice(peer.Name + " opened a door. Moving on!");
            ApplyHostDoor();
        }

        void ApplyHostDoor()
        {
            if (Time.realtimeSinceStartup - hostDoorAt > 6 || hostDoorSeq != CurrentRoomSeq) { hostDoorAt = -100; return; }
            HSMGameplayState gameplay = Closest<HSMGameplayState>();
            HSMFloorState floor = Closest<HSMFloorState>();
            if (gameplay == null || floor == null || gameplay.interrupt) return;
            hostDoorAt = -100;
            PrepareForcedTransfer();
            floor.TransferRequest(hostDoorDoor, hostDoorRoom, hostDoorNoIncrease);
        }

        public void HostTick() { if (Hosting && hostDoorAt > 0) ApplyHostDoor(); }

        // --- Seeded room furniture -------------------------------------------------------------------
        public static UnityEngine.Random.State BeginSeeded(Component owner)
        {
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            TogetherRuntime rt = Instance;
            if (rt != null && rt.HasSeed && owner != null) UnityEngine.Random.InitState(rt.currentSeed ^ StableHash(owner.transform));
            return saved;
        }

        public static void EndSeeded(UnityEngine.Random.State saved)
        {
            TogetherRuntime rt = Instance;
            if (rt != null && rt.HasSeed) UnityEngine.Random.state = saved;
        }

        static int StableHash(Transform t)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (Transform node = t; node != null; node = node.parent)
                {
                    foreach (char c in node.name) { hash ^= c; hash *= 16777619; }
                    hash ^= (uint)node.GetSiblingIndex(); hash *= 16777619;
                }
                Vector3 p = t.position;
                hash ^= (uint)Mathf.RoundToInt(p.x * 10); hash *= 16777619;
                hash ^= (uint)Mathf.RoundToInt(p.y * 10); hash *= 16777619;
                hash ^= (uint)Mathf.RoundToInt(p.z * 10); hash *= 16777619;
                return (int)hash;
            }
        }
    }
}
