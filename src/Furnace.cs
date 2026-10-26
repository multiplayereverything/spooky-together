using System;
using System.Reflection;
using UnityEngine;

namespace SpookyTogether
{
    // The Dollhouse furnace keeps its own copy of three keys (door, lever, key presence) and writes all three back after
    // every action. A copy refreshed late writes stale values over a teammate's change, and two players taking the key
    // at once each add it to the shared inventory. So every action first re-reads the shared keys and is refused when it
    // no longer applies, and a client's actions run on the host's furnace (one place decides, in order).
    public sealed partial class TogetherRuntime
    {
        public int FurnaceRefused, FurnaceForwarded;   // lab counters
        static FieldInfo furnaceDoorKey, furnaceLeverKey, furnacePresenceKey, furnaceHandsId;   // process-scoped: reflection cache

        static string FurnaceKey(DollhouseFurnaceControl furnace, ref FieldInfo field, string name)
        {
            if (field == null) field = typeof(DollhouseFurnaceControl).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null ? field.GetValue(furnace) as string : null;
        }

        // Re-read the shared door, lever and key values into the furnace's own copy.
        static void RefreshFurnace(DollhouseFurnaceControl furnace, PersistenceData data)
        {
            string door = FurnaceKey(furnace, ref furnaceDoorKey, "furnaceDoorKey"), lever = FurnaceKey(furnace, ref furnaceLeverKey, "furnaceLeverKey"), presence = FurnaceKey(furnace, ref furnacePresenceKey, "keyPresenceKey");
            if (door != null) furnace.currentDoorValue = data.GetKey(door);
            if (lever != null) furnace.currentLeverValue = data.GetKey(lever);
            if (presence != null) furnace.currentKeyValue = data.GetKey(presence);
        }

        // Does this action still apply to the furnace as it is now? (The game checked when it opened the prompt.)
        static bool FurnaceActionApplies(DollhouseFurnaceControl furnace, string action, PersistenceData data)
        {
            switch (action)
            {
                case "PutHandsIn":
                    string hands = FurnaceKey(furnace, ref furnaceHandsId, "dollHandsInventoryId");
                    return furnace.currentDoorValue == 1 && furnace.currentKeyValue == 0 && (hands == null || data.HasInventoryItem(hands));
                case "CloseFurnace": return furnace.currentDoorValue == 1;
                case "OpenFurnaceOff": return furnace.currentDoorValue == 0;
                case "TakeKey": return furnace.currentDoorValue == 1 && furnace.currentKeyValue == 2;
            }
            return true;   // LeverInteract handles every state itself; FurnaceInteract only opens a prompt
        }

        public static bool BeforeFurnaceAction(DollhouseFurnaceControl furnace, string action)
        {
            TogetherRuntime rt = Instance;
            PersistenceData data = CurrentData;
            if (rt == null || furnace == null || data == null || !rt.Connected || !rt.RunActive || Mutant("furnace")) return true;
            RefreshFurnace(furnace, data);
            if (action == "FurnaceInteract") return true;   // the prompt now matches the shared state
            if (rt.IsClient)
            {
                rt.SendToHost(Wire.Packet(Op.ObjectUsed, w => { w.Write((byte)7); Wire.Text(w, ObjectKey(furnace)); Wire.Text(w, action); }));
                rt.FurnaceForwarded++;
                return false;
            }
            if (FurnaceActionApplies(furnace, action, data)) return true;
            rt.FurnaceRefused++;
            Log("Furnace: " + action + " no longer applies (door=" + furnace.currentDoorValue + " key=" + furnace.currentKeyValue + "); refused");
            return false;
        }

        // Host: a client's furnace action, run on the host's furnace. Its key and inventory writes reach everyone.
        void ApplyFurnaceRequest(string key, string action)
        {
            if (!Hosting || Array.IndexOf(new[] { "LeverInteract", "PutHandsIn", "CloseFurnace", "OpenFurnaceOff", "TakeKey" }, action) < 0) return;
            DollhouseFurnaceControl furnace = FindByKey(typeof(DollhouseFurnaceControl), key) as DollhouseFurnaceControl;
            // A furnace spawned into the room can sit at a different path on each machine; a room has one furnace.
            if (furnace == null) { DollhouseFurnaceControl[] all = UnityEngine.Object.FindObjectsOfType<DollhouseFurnaceControl>(); if (all.Length == 1) furnace = all[0]; }
            if (furnace == null) { Log("Furnace request: no furnace at " + key); return; }
            MethodInfo method = typeof(DollhouseFurnaceControl).GetMethod(action, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) return;
            try { method.Invoke(furnace, null); }
            catch (Exception ex) { Log("Furnace request " + action + " failed: " + (ex.InnerException ?? ex).Message); }
        }
    }
}
