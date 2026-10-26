using System;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpookyTogether
{
    // Weapon hits (axe, sword, M1) all end in SendMessageOnHit.TakeHit, a scene-wired event: boarded doors, webs, walls,
    // specimens and the Taker boss. A local hit applies locally at once and is relayed (through the host) so every other
    // copy of that object takes the same hit; the host's copy is the authority for specimens.
    // Teammates' held weapon and swings are shown on their avatars.
    public sealed partial class TogetherRuntime
    {
        public static bool ApplyingHit;
        static string remoteWeapon;
        float nextWeaponAnnounce;
        string announcedWeapon;

        static string HitKey(SendMessageOnHit hit)
        {
            TogetherRuntime rt = Instance;
            // Inside a networked specimen: its net id plus the path below it (puppets and host copies share prefabs).
            if (rt != null)
            {
                Transform t = hit.transform;
                for (Transform node = t; node != null; node = node.parent)
                {
                    int id = rt.NetIdOf(node.gameObject);
                    if (id > 0)
                    {
                        System.Text.StringBuilder b = new System.Text.StringBuilder();
                        for (Transform up = t; up != node; up = up.parent) b.Insert(0, "/" + up.name + "#" + up.GetSiblingIndex());
                        return "N:" + id + b;
                    }
                }
            }
            return "P:" + ObjectKey(hit);
        }

        int NetIdOf(GameObject go)
        {
            if (Hosting) { foreach (System.Collections.Generic.KeyValuePair<int, GameObject> pair in hostSpecs) if (pair.Value == go) return pair.Key; return 0; }
            foreach (Puppet puppet in puppets.Values) if (puppet.Obj == go) return puppet.NetId;
            return 0;
        }

        SendMessageOnHit ResolveHit(string key)
        {
            if (key.StartsWith("N:", StringComparison.Ordinal))
            {
                int slash = key.IndexOf('/', 2);
                int id; if (!int.TryParse(slash < 0 ? key.Substring(2) : key.Substring(2, slash - 2), out id)) return null;
                GameObject root = null;
                if (Hosting) hostSpecs.TryGetValue(id, out root);
                else { Puppet p; if (puppets.TryGetValue(id, out p)) root = p.Obj; }
                if (root == null) return null;
                Transform node = root.transform;
                if (slash >= 0)
                {
                    foreach (string part in key.Substring(slash + 1).Split('/'))
                    {
                        int hash = part.LastIndexOf('#'); int index;
                        if (hash < 0 || !int.TryParse(part.Substring(hash + 1), out index) || index >= node.childCount) return null;
                        node = node.GetChild(index);
                    }
                }
                return node.GetComponent<SendMessageOnHit>();
            }
            return key.StartsWith("P:", StringComparison.Ordinal) ? FindByKey(typeof(SendMessageOnHit), key.Substring(2)) as SendMessageOnHit : null;
        }

        public static bool BeforeTakeHit(SendMessageOnHit hit)
        {
            TogetherRuntime rt = Instance;
            if (ApplyingHit || rt == null || hit == null || !rt.Connected || !rt.RunActive || Mutant("hits")) return true;
            string key = HitKey(hit);
            string weapon = rt.LocalWeapon();
            rt.Broadcast(Wire.Packet(Op.Hit, w => { Wire.Text(w, key); Wire.Text(w, weapon ?? ""); }));
            // Specimens (including the Taker boss, whose hp picks the ending) take hits only on the host.
            return !(rt.IsClient && IsSpecimenHit(key));
        }

        static bool IsSpecimenHit(string key) { return key.StartsWith("N:", StringComparison.Ordinal); }

        void ApplyHit(BinaryReader reader, int from)
        {
            string key = Wire.Text(reader, 700); string weapon = Wire.Text(reader, 64);
            if (Hosting) Broadcast(Wire.Packet(Op.Hit, w => { Wire.Text(w, key); Wire.Text(w, weapon); }), from);
            if (!RunActive || (IsClient && IsSpecimenHit(key))) return;
            SendMessageOnHit hit = ResolveHit(key);
            if (hit == null) { Log("Hit target not found: " + key); return; }
            ApplyingHit = true; remoteWeapon = weapon;
            try { hit.TakeHit(); }
            catch (Exception ex) { Log("Replicated hit failed on " + key + ": " + ex.Message); }
            finally { ApplyingHit = false; remoteWeapon = null; }
        }

        // Hit handlers check the local player's weapon (e.g. KH monsters need the right one); a replicated hit uses the attacker's.
        public static bool BeforeCurrentWeapon(string weapon, ref bool result)
        {
            if (!ApplyingHit || remoteWeapon == null) return true;
            result = remoteWeapon == weapon;
            return false;
        }

        // --- Weapon display on avatars ---------------------------------------------------------------------
        string LocalWeapon()
        {
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            WeaponController weapons = player != null ? player.GetComponent<WeaponController>() : null;
            return weapons != null && weapons.currentWeapon != null ? weapons.currentWeapon.identifier : "";
        }

        public static void AfterSwing()
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.Connected) return;
            int self = rt.SelfId;
            rt.Broadcast(Wire.Packet(Op.Swing, w => w.Write(self)));
        }

        void WeaponTick()
        {
            if (!Connected) return;
            string weapon = LocalWeapon();
            float now = Time.realtimeSinceStartup;
            if (weapon == announcedWeapon && now < nextWeaponAnnounce) return;
            announcedWeapon = weapon; nextWeaponAnnounce = now + 5;
            int self = SelfId;
            Broadcast(Wire.Packet(Op.Weapon, w => { w.Write(self); Wire.Text(w, weapon); }));
        }

        void ApplyWeapon(BinaryReader reader, int from)
        {
            int id = reader.ReadInt32(); string weapon = Wire.Text(reader, 64);
            if (Hosting) { id = from; Broadcast(Wire.Packet(Op.Weapon, w => { w.Write(from); Wire.Text(w, weapon); }), from); }
            Peer peer; if (peers.TryGetValue(id, out peer)) peer.Weapon = weapon;
        }

        void ApplySwing(BinaryReader reader, int from)
        {
            int id = reader.ReadInt32();
            if (Hosting) { id = from; Broadcast(Wire.Packet(Op.Swing, w => w.Write(from)), from); }
            Peer peer; if (peers.TryGetValue(id, out peer)) peer.SwingAt = Time.realtimeSinceStartup;
        }
    }
}
