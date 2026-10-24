using System;
using System.IO;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpookyTogether
{
    // A room's walls, floor and ceiling take their materials from the floor state (HSMFloorState.Get*Material): the
    // active floor and any temporary floorlets, or a specimen's override. A client's floor state can differ from the host's
    // (floorlets pushed by room events or specimens), so the same room would look different. The host sends the materials
    // it uses for every room; a client's getters return those, and it repaints if they change mid-room.
    public sealed partial class TogetherRuntime
    {
        int lookSeq = -1;
        string[] look;          // wall, wallLit, floor, floorLit, ceiling
        public int LookChecks;

        static string[] CurrentLook()
        {
            HSMFloorState floor = Closest<HSMFloorState>();
            if (floor == null) return null;
            string[] names = new string[5];
            try
            {
                names[0] = MatName(floor.GetWallMaterial(false)); names[1] = MatName(floor.GetWallMaterial(true));
                names[2] = MatName(floor.GetFloorMaterial(false)); names[3] = MatName(floor.GetFloorMaterial(true));
                names[4] = MatName(floor.GetCeilingMaterial());
            }
            catch (Exception ex) { Log("Room look unavailable: " + ex.Message); return null; }
            return names;
        }

        static string MatName(Material m) { return m != null ? MaterialName(m) : ""; }

        void HostSendLook(int seq)
        {
            string[] names = CurrentLook();
            if (names == null) return;
            Broadcast(Wire.Packet(Op.RoomLook, w => { w.Write(seq); foreach (string n in names) Wire.Text(w, n); }));
        }

        // Specimens that re-skin the floor (e.g. Unknown Specimen 1 and 3) change the look mid-room.
        public static void AfterLookOverride()
        {
            TogetherRuntime rt = Instance;
            if (rt != null && rt.Hosting && rt.Connected && rt.RunActive && rt.CurrentRoomSeq > 0) rt.HostSendLook(rt.CurrentRoomSeq);
        }

        void ClientLook(BinaryReader reader)
        {
            int seq = reader.ReadInt32();
            string[] names = new string[5];
            for (int i = 0; i < 5; i++) names[i] = Wire.Text(reader, 200);
            bool changed = seq == lookSeq && look != null && string.Join("|", look) != string.Join("|", names);
            lookSeq = seq; look = names;
            // Already standing in that room: repaint now.
            if ((changed || seq == CurrentRoomSeq) && Closest<HSMRoomReadyState>() != null) RepaintRoom();
        }

        public static bool BeforeGetMaterial(int slot, ref Material result)
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.IsClient || !rt.Connected || rt.look == null || rt.lookSeq != rt.CurrentRoomSeq) return true;
            string name = rt.look[slot];
            if (string.IsNullOrEmpty(name)) { result = null; return false; }
            Material m = rt.FindMaterial(name);
            if (m == null) return true;
            result = m;
            return false;
        }

        void RepaintRoom()
        {
            foreach (UseWallMaterial c in Object.FindObjectsOfType<UseWallMaterial>()) { UseWallMaterial t = c; Guard(delegate { t.UpdateMaterial(); }); }
            foreach (UseFloorMaterial c in Object.FindObjectsOfType<UseFloorMaterial>()) { UseFloorMaterial t = c; Guard(delegate { t.UpdateMaterial(); }); }
            foreach (UseRoofMaterial c in Object.FindObjectsOfType<UseRoofMaterial>()) { UseRoofMaterial t = c; Guard(delegate { t.UpdateMaterial(); }); }
        }

        // Lab: a second after each room is ready, the client's painted walls must carry the host's wall material.
        float lookCheckAt = -1;
        int lookCheckedSeq = -1;

        void LabLookTick()
        {
            if (!LabMode || !IsClient || look == null || lookSeq != CurrentRoomSeq || lookCheckedSeq == CurrentRoomSeq) return;
            if (Closest<HSMRoomReadyState>() == null) { lookCheckAt = -1; return; }
            if (lookCheckAt < 0) { lookCheckAt = Time.realtimeSinceStartup + 1.5f; return; }
            if (Time.realtimeSinceStartup < lookCheckAt) return;
            lookCheckedSeq = CurrentRoomSeq; lookCheckAt = -1;
            foreach (UseWallMaterial wall in Object.FindObjectsOfType<UseWallMaterial>())
            {
                if (!wall.useMansionMaterial) continue;
                MeshRenderer r = wall.GetComponent<MeshRenderer>();
                if (r == null || wall.materialIdx >= r.sharedMaterials.Length || r.sharedMaterials[wall.materialIdx] == null) continue;
                string expected = look[wall.useLitVariant ? 1 : 0], actual = MaterialName(r.sharedMaterials[wall.materialIdx]);
                LookChecks++;
                Check("room.look", expected == actual || expected.Length == 0, "room " + CurrentRoomSeq + " host wall=" + expected + " mine=" + actual);
                return;
            }
        }
    }
}
