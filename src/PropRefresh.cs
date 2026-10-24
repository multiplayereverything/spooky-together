using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpookyTogether
{
    // Room props read the shared keys only when a room becomes ready. When a teammate changes one (plays the piano,
    // opens the furnace, cuts the wire), the props in the room you are standing in are refreshed right away.
    // Only refreshes that just set visibility from keys are re-run; puzzle conditions fire only when newly met.
    public sealed partial class TogetherRuntime
    {
        float propsDirtyAt = -1;
        public int PropRefreshes, PropRefreshFailures, PropsTouched;
        readonly Dictionary<int, HashSet<int>> puzzleSegmentsMet = new Dictionary<int, HashSet<int>>();
        static readonly Dictionary<string, MethodInfo> propMethods = new Dictionary<string, MethodInfo>();
        static readonly Dictionary<string, FieldInfo> propFields = new Dictionary<string, FieldInfo>();

        void MarkPropsDirty() { if (propsDirtyAt < 0) propsDirtyAt = Time.realtimeSinceStartup + 0.25f; }

        void PropRefreshTick()
        {
            if (propsDirtyAt < 0 || Time.realtimeSinceStartup < propsDirtyAt) return;
            propsDirtyAt = -1;
            HSMRoomReadyState room = Closest<HSMRoomReadyState>();
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (room == null || player == null || !player.ready) return;
            PropRefreshes++;
            Refresh<DollhousePianoControl>("RoomReady", room);
            Refresh<DollhouseFurnaceControl>("RoomReady", room);
            Refresh<DollhouseBarbedWireDoor>("RoomReady", room);
            HSMSystemsLoadState load = Closest<HSMSystemsLoadState>();
            if (load != null) Refresh<PersistentValueExister>("SystemsLoad", load);
            foreach (DollhouseDollPillar pillar in Object.FindObjectsOfType<DollhouseDollPillar>())
            {
                // Its refresh instantiates the doll; drop the old one first so dolls never pile up.
                Guard(delegate
                {
                    GameObject doll = Field(typeof(DollhouseDollPillar), "dollInstance").GetValue(pillar) as GameObject;
                    if (doll != null) Object.Destroy(doll);
                    Method(typeof(DollhouseDollPillar), "RoomReady").Invoke(pillar, new object[] { room });
                });
            }
            foreach (DollhouseHellroomControl hellroom in Object.FindObjectsOfType<DollhouseHellroomControl>())
            {
                // Candle icons only: RoomEntered can start the ritual, which belongs to whoever is in the hellroom.
                Guard(delegate
                {
                    string key = Field(typeof(DollhouseHellroomControl), "candleStateKey").GetValue(hellroom) as string;
                    PersistenceData data = CurrentData;
                    if (key != null && data != null) Field(typeof(DollhouseHellroomControl), "currentStateValue").SetValue(hellroom, data.GetKey(key));
                    Method(typeof(DollhouseHellroomControl), "UpdateIcons").Invoke(hellroom, null);
                });
            }
            foreach (PuzzleStateExister puzzle in Object.FindObjectsOfType<PuzzleStateExister>())
            {
                Guard(delegate
                {
                    HashSet<int> met;
                    if (!puzzleSegmentsMet.TryGetValue(puzzle.GetInstanceID(), out met)) puzzleSegmentsMet[puzzle.GetInstanceID()] = met = new HashSet<int>();
                    for (int i = 0; i < puzzle.segments.Length; i++)
                        if (!met.Contains(i) && puzzle.segments[i].condition.Evaluate()) { met.Add(i); puzzle.segments[i].onConditionEvents.Invoke(); }
                });
            }
        }

        // Seeds the "already met" puzzle segments when a room loads, so a refresh only fires what a teammate changed.
        void SeedPuzzleState()
        {
            puzzleSegmentsMet.Clear();
            foreach (PuzzleStateExister puzzle in Object.FindObjectsOfType<PuzzleStateExister>())
            {
                HashSet<int> met = new HashSet<int>();
                for (int i = 0; i < puzzle.segments.Length; i++)
                    try { if (puzzle.segments[i].condition.Evaluate()) met.Add(i); } catch { }
                puzzleSegmentsMet[puzzle.GetInstanceID()] = met;
            }
        }

        public static void AfterRoomReadyProps() { TogetherRuntime rt = Instance; if (rt != null && rt.Connected) rt.SeedPuzzleState(); }

        void Refresh<T>(string method, object state) where T : Component
        {
            foreach (T prop in Object.FindObjectsOfType<T>())
            {
                T target = prop;
                PropsTouched++;
                Guard(delegate { Method(typeof(T), method).Invoke(target, new object[] { state }); });
            }
        }

        void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { PropRefreshFailures++; Log("Prop refresh failed: " + (ex.InnerException ?? ex).Message); }
        }

        static MethodInfo Method(Type type, string name)
        {
            string key = type.FullName + "." + name;
            MethodInfo m;
            if (!propMethods.TryGetValue(key, out m)) propMethods[key] = m = AccessTools.Method(type, name);
            if (m == null) throw new MissingMethodException(type.Name, name);
            return m;
        }

        static FieldInfo Field(Type type, string name)
        {
            string key = type.FullName + "." + name;
            FieldInfo f;
            if (!propFields.TryGetValue(key, out f)) propFields[key] = f = AccessTools.Field(type, name);
            if (f == null) throw new MissingFieldException(type.Name, name);
            return f;
        }
    }
}
