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
        sealed class RoomInfo { public int Run, Seq, RoomKey, FloorKey, Seed; public string RoomId, Door; }

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

        public static HSMGameManager Hsm { get { try { return ServiceLocator.GetService<HSMGameManager>(); } catch { return null; } } }
        static T Closest<T>() where T : HSMBaseState { HSMGameManager gm = Hsm; if (gm == null || gm.hsm == null) return null; try { return gm.ClosestState<T>(); } catch { return null; } }
        public bool RunActive { get { return Closest<HSMMansionState>() != null; } }
        public bool HasSeed { get { return Connected && hasSeed; } }
        public int RoomSeed { get { return currentSeed; } }

        // A client that loses the host keeps its run identity, so reconnecting resumes the same run instead of restarting it.
        int keptRunId; string keptRunMansion;

        void ResetSession()
        {
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
            rt.runId++; rt.hostSeq = 0; rt.lastRoom = null; rt.runMansion = mansionConfig.uniqueIdentifier;
            rt.ResetDowned();
            foreach (Peer peer in rt.peers.Values) { peer.Downed = false; peer.RoomSeq = -1; }
            rt.Broadcast(rt.StartRunPacket());
            rt.Tell("Run started: " + rt.runMansion + ". Teammates are joining.");
        }

        byte[] StartRunPacket() { return Wire.Packet(Op.StartRun, w => { w.Write(runId); Wire.Text(w, runMansion); }); }

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
            // Reconnected into the run we are already playing: keep going, the room and state packets follow.
            if (RunActive && ((run == runId && mansion == runMansion) || (run == keptRunId && mansion == keptRunMansion)))
            {
                runId = run; runMansion = mansion; keptRunId = 0;
                // Still in the room the host announced last: no reload unless the host has moved on.
                consumedSeq = interruptedSeq = Math.Max(0, CurrentRoomSeq); pendingRoom = null;
                Tell("Rejoined the host's run.");
                if (LabMode) Check("reconnect.rejoined", true, "run " + run);
                return;
            }
            keptRunId = 0;
            runId = run; runMansion = mansion; pendingStartMansion = mansion;
            pendingRoom = null; consumedSeq = 0; interruptedSeq = 0; hasSeed = false;
            ResetDowned();
            ResetEnemies();
            Tell("The host started a run.");
        }

        void ClientEndRun()
        {
            Tell("The host left the run.");
            pendingStartMansion = null; pendingRoom = null; runMansion = null;
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
            if (rt.Hosting && rt.runMansion != null && !gameOver)
            {
                rt.Broadcast(Wire.Packet(Op.EndRun, null));
                rt.Tell("Run ended.");
            }
            if (rt.Hosting) rt.runMansion = null;
            rt.RestoreOptions(); rt.pendingFullState = null; rt.endingSent = null;
            rt.ResetEnemies();
            if (wasDowned) rt.SendStatus(false);
        }

        void UpdateRooms()
        {
            if (!IsClient || !Connected) return;
            if (pendingStartMansion != null)
            {
                HSMMenuState menu = Closest<HSMMenuState>();
                if (menu != null)
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
                    BypassTransfer = true;
                    try { floor.TransferRequest(null, null, false); }
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
                w.Write(info.RoomKey); w.Write(info.FloorKey); w.Write(info.Seed);
            });
        }

        void ClientRoom(BinaryReader reader)
        {
            RoomInfo info = new RoomInfo { Run = reader.ReadInt32(), Seq = reader.ReadInt32(), RoomId = Wire.Text(reader, 200), Door = Wire.Text(reader, 200), RoomKey = reader.ReadInt32(), FloorKey = reader.ReadInt32(), Seed = reader.ReadInt32() };
            if (info.Door.Length == 0) info.Door = null;
            if (info.Run != runId) return;
            if (pendingRoom == null || info.Seq > pendingRoom.Seq) pendingRoom = info;
        }

        public static IEnumerator<HSMBaseState> WrapLoad(HSMLoadRoomState state, IEnumerator<HSMBaseState> original)
        {
            HSMSystemsLoadState systems = state.pushOnComplete as HSMSystemsLoadState;
            TogetherRuntime rt = Instance;
            if (systems != null && rt != null && rt.Connected)
            {
                rt.ReleaseStaleLocks();
                if (rt.Hosting) rt.HostRoomLoading(systems);
                else
                {
                    while (rt.IsClient && rt.Connected && !(rt.pendingRoom != null && rt.pendingRoom.Run == rt.runId && rt.pendingRoom.Seq > rt.consumedSeq))
                    {
                        rt.waitingForHost = true;
                        yield return state;
                    }
                    rt.waitingForHost = false;
                    if (rt.IsClient && rt.Connected) rt.ApplyRoom(state, systems);
                }
            }
            while (original.MoveNext()) yield return original.Current;
        }

        void HostRoomLoading(HSMSystemsLoadState systems)
        {
            HSMRoomReadyState.RoomRuntimeData data = systems.roomRuntimeData;
            if (data == null || data.roomConfig == null) return;
            PersistenceData save = PersistenceManager.GetInstance() != null ? PersistenceManager.GetInstance().currentData : null;
            RoomInfo info = new RoomInfo
            {
                Run = runId, Seq = ++hostSeq, RoomId = data.roomConfig.uniqueRoomId, Door = data.namedSpawnDoor,
                RoomKey = save != null ? save.GetKey("room") : 0, FloorKey = save != null ? save.GetKey("floor") : 0, Seed = seeds.Next()
            };
            lastRoom = info;
            if (info.Seq == 1) Broadcast(FullStatePacket());
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
            ApplyPendingState();
            Log("Following host into room " + info.Seq + ": " + info.RoomId + " (" + config.sceneName + ")");
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
