using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpookyTogether
{
    // The run's progression lives in one PersistenceData (keys such as "violence", puzzle and ending flags, and the
    // inventory). Every change any player makes goes through the host, which applies it and relays it, so endings and
    // puzzles see one shared state. A 10-second checksum from the host repairs any drift with a full snapshot.
    // Pickups, door unlocks and the axe are object state, so they are replicated by scene key.
    public sealed partial class TogetherRuntime
    {
        public static bool ApplyingState;
        public int ElevatorRides;
        byte[] pendingFullState;
        float nextStateHash;
        bool savedOptions, oldSpecimen, oldJumpScares;

        static PersistenceData CurrentData { get { PersistenceManager pm = PersistenceManager.GetInstance(); return pm != null ? pm.currentData : null; } }

        // Room and floor counters follow the room sync; mood and Crowd Control counters belong to one player.
        static bool SharedKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key == "room" || key == "floor") return false;
            if (key.StartsWith("CC_", StringComparison.Ordinal) || key.IndexOf("mood", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        bool StateSyncActive { get { return Connected && RunActive && AcceptedCount + (Hosting ? 0 : 1) > 0; } }

        // A client's floor fires the same room events as the host's, and the host's copy relays those keys: the client's
        // stay local, so a client whose floor count is off cannot push its keys onto everyone.
        public static bool BeginLocalFloorEvent()
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.IsClient || !rt.Connected || ApplyingState) return false;
            ApplyingState = true;
            return true;
        }

        public static void AfterKeyChange(PersistenceData data, string key, int value, bool cleared)
        {
            TogetherRuntime rt = Instance;
            if (ApplyingState || rt == null || data == null || data != CurrentData || !rt.StateSyncActive || !SharedKey(key) || Mutant("keys")) return;
            byte[] packet = Wire.Packet(Op.StateKey, w => { Wire.Text(w, key); w.Write(cleared); w.Write(value); });
            rt.Broadcast(packet);
        }

        public static void AfterInventoryChange(PersistenceData data, string item, byte kind)
        {
            TogetherRuntime rt = Instance;
            if (ApplyingState || rt == null || data == null || data != CurrentData || !rt.StateSyncActive || string.IsNullOrEmpty(item)) return;
            if (kind == 0 && !KnownItem(item)) return;
            rt.Broadcast(Wire.Packet(Op.StateItem, w => { w.Write(kind); Wire.Text(w, item); }));
        }

        void ApplyKey(BinaryReader reader, int from)
        {
            string key = Wire.Text(reader, 200); bool cleared = reader.ReadBoolean(); int value = reader.ReadInt32();
            PersistenceData data = CurrentData;
            if (!SharedKey(key) || data == null || !RunActive) return;
            ApplyingState = true;
            try { if (cleared) data.ClearKey(key); else data.SetKey(key, value); }
            finally { ApplyingState = false; }
            MarkPropsDirty();
            if (Hosting) Broadcast(Wire.Packet(Op.StateKey, w => { Wire.Text(w, key); w.Write(cleared); w.Write(value); }), from);
        }

        void ApplyItem(BinaryReader reader, int from)
        {
            byte kind = reader.ReadByte(); string item = Wire.Text(reader, 200);
            PersistenceData data = CurrentData;
            if (data == null || !RunActive) return;
            ApplyingState = true;
            try
            {
                if (kind == 0) { if (KnownItem(item)) data.AddToInventory(item); }
                else if (kind == 1) data.RemoveFromInventory(item);
                else data.RemoveFromInventorySubvalues(item);
                RefreshInventory();
            }
            finally { ApplyingState = false; }
            MarkPropsDirty();
            if (Hosting) Broadcast(Wire.Packet(Op.StateItem, w => { w.Write(kind); Wire.Text(w, item); }), from);
        }

        // --- Full snapshot and checksum -----------------------------------------------------------------
        static List<KeyValuePair<string, int>> SharedKeys(PersistenceData data)
        {
            List<KeyValuePair<string, int>> keys = new List<KeyValuePair<string, int>>();
            if (data == null || data.liveData == null) return keys;
            foreach (DictionaryEntry entry in data.liveData)
            {
                string key = entry.Key as string;
                if (key == null || !SharedKey(key) || !(entry.Value is int)) continue;
                keys.Add(new KeyValuePair<string, int>(key, (int)entry.Value));
            }
            keys.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return keys;
        }

        static int StateHash(PersistenceData data)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (KeyValuePair<string, int> pair in SharedKeys(data))
                {
                    foreach (char c in pair.Key) { hash ^= c; hash *= 16777619; }
                    hash ^= (uint)pair.Value; hash *= 16777619;
                }
                if (data != null && data.inventory != null)
                {
                    List<string> items = new List<string>(data.inventory); items.Sort(string.CompareOrdinal);
                    foreach (string item in items) { foreach (char c in item) { hash ^= c; hash *= 16777619; } hash ^= 0xFF; hash *= 16777619; }
                }
                return (int)hash;
            }
        }

        byte[] FullStatePacket()
        {
            PersistenceData data = CurrentData;
            List<KeyValuePair<string, int>> keys = SharedKeys(data);
            List<string> items = data != null && data.inventory != null ? data.inventory : new List<string>();
            OptionsManager options = ServiceLocator.GetService<OptionsManager>();
            return Wire.Packet(Op.StateFull, w =>
            {
                w.Write(options == null || options.specimen); w.Write(options == null || options.jumpScares);
                w.Write((ushort)keys.Count); foreach (KeyValuePair<string, int> pair in keys) { Wire.Text(w, pair.Key); w.Write(pair.Value); }
                w.Write((ushort)items.Count); foreach (string item in items) Wire.Text(w, item);
            });
        }

        void HostStateTick()
        {
            if (!RunActive || AcceptedCount == 0 || Time.realtimeSinceStartup < nextStateHash) return;
            nextStateHash = Time.realtimeSinceStartup + 10;
            int hash = StateHash(CurrentData);
            Broadcast(Wire.Packet(Op.StateHash, w => w.Write(hash)));
        }

        void ClientStateHash(BinaryReader reader)
        {
            int hash = reader.ReadInt32();
            if (!RunActive || CurrentRoomSeq < 0) return;
            int mine = StateHash(CurrentData);
            if (mine != hash) { Log("State checksum differs from the host; requesting a full copy."); SendToHost(Wire.Packet(Op.StateRequest, null)); }
            if (LabMode) Check("state.hash", mine == hash, "host=" + hash + " client=" + mine);
        }

        void ClientFullState(BinaryReader reader)
        {
            byte[] copy = new byte[reader.BaseStream.Length];
            reader.BaseStream.Position = 0; reader.BaseStream.Read(copy, 0, copy.Length);
            pendingFullState = copy;
            if (RunActive && CurrentRoomSeq >= 0) ApplyPendingState();
        }

        // Called once the client's run data exists (room load) and whenever a snapshot arrives mid-run.
        void ApplyPendingState()
        {
            byte[] packet = pendingFullState;
            PersistenceData data = CurrentData;
            if (packet == null || data == null) return;
            pendingFullState = null;
            using (BinaryReader reader = new BinaryReader(new MemoryStream(packet)))
            {
                reader.ReadByte();
                bool specimen = reader.ReadBoolean(), jumpScares = reader.ReadBoolean();
                ApplyHostOptions(specimen, jumpScares);
                Dictionary<string, int> keys = new Dictionary<string, int>();
                int count = reader.ReadUInt16();
                for (int i = 0; i < count; i++) { string key = Wire.Text(reader, 200); keys[key] = reader.ReadInt32(); }
                List<string> items = new List<string>();
                int itemCount = reader.ReadUInt16();
                for (int i = 0; i < itemCount; i++) { string item = Wire.Text(reader, 200); if (KnownItem(item)) items.Add(item); }
                ApplyingState = true;
                try
                {
                    foreach (KeyValuePair<string, int> pair in SharedKeys(data)) if (!keys.ContainsKey(pair.Key)) data.ClearKey(pair.Key);
                    foreach (KeyValuePair<string, int> pair in keys) if (SharedKey(pair.Key)) data.SetKey(pair.Key, pair.Value);
                    if (data.inventory == null) data.inventory = new List<string>();
                    data.inventory.Clear(); data.inventory.AddRange(items);
                    if (data.inventoryHistory == null) data.inventoryHistory = new List<string>();
                    foreach (string item in items) if (!data.inventoryHistory.Contains(item)) data.inventoryHistory.Add(item);
                    RefreshInventory();
                }
                finally { ApplyingState = false; }
                MarkPropsDirty();
                Log("Applied the host's run state: " + keys.Count + " keys, " + items.Count + " items.");
            }
        }

        // The HUD indexes allInventoryItems for every held item and throws on an unknown id inside a room-ready task,
        // which would stall the game's state machine; never mirror an id this copy of the game does not know.
        static bool KnownItem(string item)
        {
            AssetDataManager assets = AssetDataManager.GetInstance();
            return assets != null && assets.allInventoryItems != null && item != null && assets.allInventoryItems.ContainsKey(item);
        }

        static void RefreshInventory()
        {
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (player == null || !player.ready) return;
            try { player.InventoryChanged(); }
            catch (Exception ex) { Log("Inventory display refresh failed: " + ex.Message); }
        }

        // Specimens on/off and jump scares change which ending branch a room takes, so a client uses the host's.
        void ApplyHostOptions(bool specimen, bool jumpScares)
        {
            OptionsManager options = ServiceLocator.GetService<OptionsManager>();
            if (options == null) return;
            if (!savedOptions) { savedOptions = true; oldSpecimen = options.specimen; oldJumpScares = options.jumpScares; }
            options.specimen = specimen; options.jumpScares = jumpScares;
        }

        void RestoreOptions()
        {
            if (!savedOptions) return;
            OptionsManager options = ServiceLocator.GetService<OptionsManager>();
            if (options != null) { options.specimen = oldSpecimen; options.jumpScares = oldJumpScares; }
            savedOptions = false;
        }

        // --- Object state: pickups and unlocked doors ---------------------------------------------------
        static string ObjectKey(Component c)
        {
            if (c == null) return null;
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            for (Transform t = c.transform; t != null; t = t.parent) b.Insert(0, "/" + t.name + (t.parent != null ? "#" + t.GetSiblingIndex() : ""));
            b.Insert(0, c.gameObject.scene.name + ":" + c.GetType().Name);
            return b.ToString();
        }

        static Component FindByKey(Type type, string key)
        {
            foreach (Object found in Object.FindObjectsOfType(type, true))
            {
                Component c = found as Component;
                if (c != null && ObjectKey(c) == key) return c;
            }
            return null;
        }

        public static void AfterObjectUsed(Component c, byte kind, string extra)
        {
            TogetherRuntime rt = Instance;
            if (ApplyingState || rt == null || c == null || !rt.StateSyncActive) return;
            if ((kind == 5 || kind == 6) && Mutant("elevator")) return;
            string key = ObjectKey(c);
            rt.Broadcast(Wire.Packet(Op.ObjectUsed, w => { w.Write(kind); Wire.Text(w, key); Wire.Text(w, extra ?? ""); }));
        }

        void ApplyObjectUsed(BinaryReader reader, int from)
        {
            byte kind = reader.ReadByte(); string key = Wire.Text(reader, 600); string extra = Wire.Text(reader, 200);
            if (Hosting) Broadcast(Wire.Packet(Op.ObjectUsed, w => { w.Write(kind); Wire.Text(w, key); Wire.Text(w, extra); }), from);
            if (!RunActive) return;
            ApplyingState = true;
            try
            {
                switch (kind)
                {
                    case 0: { Component c = FindByKey(typeof(PickUpItem), key); if (c != null) c.gameObject.SetActive(false); break; }
                    case 1: { Component c = FindByKey(typeof(PickupComponent), key); if (c != null) Object.Destroy(c.gameObject); break; }
                    case 2:
                        {
                            // The axe (or any weapon) one player picks up arms the whole team.
                            Component c = FindByKey(typeof(WeaponPickupComponent), key);
                            if (c != null) c.gameObject.SetActive(false);
                            GrantWeapon(extra);
                            break;
                        }
                    case 3: { DoorComponent d = FindByKey(typeof(DoorComponent), key) as DoorComponent; if (d != null) d.Unlock(); break; }
                    case 4: { LocalDoorComponent d = FindByKey(typeof(LocalDoorComponent), key) as LocalDoorComponent; if (d != null) d.Unlock(); break; }
                    // Elevator buttons: the ride (car animation, moving shaft, sounds) starts for everyone together.
                    case 5: { ElevatorButtonControl e = FindByKey(typeof(ElevatorButtonControl), key) as ElevatorButtonControl; if (e != null) { e.OnInteract(); ElevatorRides++; } break; }
                    case 6: { KHElevatorButton e = FindByKey(typeof(KHElevatorButton), key) as KHElevatorButton; if (e != null) { e.OnInteract(); ElevatorRides++; } break; }
                }
            }
            catch (Exception ex) { Log("Replicating object " + key + " failed: " + ex.Message); }
            finally { ApplyingState = false; }
        }

        void GrantWeapon(string weapon)
        {
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            PersistenceData data = CurrentData;
            if (player == null || data == null || string.IsNullOrEmpty(weapon)) return;
            WeaponController weapons = player.GetComponent<WeaponController>();
            if (weapons == null || weapons.HasWeapon(weapon)) return;
            AssetDataManager assets = AssetDataManager.GetInstance();
            if (assets == null || assets.allWeapons == null || !assets.allWeapons.ContainsKey(weapon)) return;
            weapons.ResetState();
            weapons.SetCurrentWeapon(weapon, data);
            Tell("Your team found the " + weapon + ".");
        }
    }
}
