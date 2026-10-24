using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpookyTogether
{
    // Specimen 1 cut-outs. Which side they face is seeded per room (SeedPrefix on Start). Whether one pops, and which
    // picture and sound it uses, is decided once by the host: the first player to walk into its trigger makes it pop for
    // everyone, with the host's pool picks. A client that triggers it first asks the host.
    public sealed partial class TogetherRuntime
    {
        public static bool ApplyingCutout;
        static string forcedFront, forcedBack, forcedClip;   // call-scoped: set by ClientCutoutState, read during its Deploy, cleared in its finally
        static FieldInfo cutoutDeployed;   // process-scoped: reflection cache
        readonly Dictionary<string, Material> materialCache = new Dictionary<string, Material>();
        float materialCacheAt = -100;
        public int CutoutsApplied;

        static bool Deployed(SpecimenOneController c)
        {
            if (cutoutDeployed == null) cutoutDeployed = AccessTools.Field(typeof(SpecimenOneController), "deployed");
            return cutoutDeployed != null && (bool)cutoutDeployed.GetValue(c);
        }

        static void SetDeployed(SpecimenOneController c)
        {
            if (cutoutDeployed == null) cutoutDeployed = AccessTools.Field(typeof(SpecimenOneController), "deployed");
            if (cutoutDeployed != null) cutoutDeployed.SetValue(c, true);
        }

        // RandomDeploy (a room trigger) and Deploy (scripted, e.g. a present box) on a client become requests to the host.
        public static bool BeforeCutoutTrigger(SpecimenOneController cutout, bool forced)
        {
            TogetherRuntime rt = Instance;
            if (ApplyingCutout || rt == null || !rt.IsClient || !rt.Connected || !rt.RunActive) return true;
            if (Deployed(cutout)) return false;
            string key = ObjectKey(cutout);
            rt.SendToHost(Wire.Packet(Op.CutoutRequest, w => { Wire.Text(w, key); w.Write(forced); }));
            return false;
        }

        // Host: after its own decision, tell everyone what happened.
        public static void AfterCutoutRandomDeploy(SpecimenOneController cutout, bool wasDeployed)
        {
            TogetherRuntime rt = Instance;
            if (ApplyingCutout || rt == null || !rt.Hosting || !rt.Connected || !rt.RunActive || wasDeployed) return;
            if (!cutout.geoContainer.activeSelf) rt.BroadcastCutout(cutout, false);   // rolled "no pop"
        }

        public static void AfterCutoutDeploy(SpecimenOneController cutout)
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.Connected || !rt.RunActive) return;
            if (rt.IsClient)
            {
                if (ApplyingCutout) rt.ApplyCutoutLook(cutout);
                return;
            }
            rt.BroadcastCutout(cutout, true);
        }

        void BroadcastCutout(SpecimenOneController cutout, bool popped)
        {
            string key = ObjectKey(cutout);
            string front = popped && cutout.frontFace != null && cutout.frontFace.sharedMaterial != null ? MaterialName(cutout.frontFace.sharedMaterial) : "";
            string back = popped && cutout.backFace != null && cutout.backFace.sharedMaterial != null ? MaterialName(cutout.backFace.sharedMaterial) : "";
            string clip = popped && cutout.audioSource != null && lastCutoutClip != null ? lastCutoutClip : "";
            lastCutoutClip = null;   // a later pop without a sound must not reuse this one
            bool flipped = cutout.didFlip;
            Broadcast(Wire.Packet(Op.CutoutState, w => { Wire.Text(w, key); w.Write(popped); Wire.Text(w, front); Wire.Text(w, back); Wire.Text(w, clip); w.Write(flipped); }));
        }

        static string MaterialName(Material m) { string n = m.name; return n.EndsWith(" (Instance)", StringComparison.Ordinal) ? n.Substring(0, n.Length - 11) : n; }

        // The cut-out's pop sound goes through AudioSource.PlayOneShot; remember the last one played by a cut-out.
        static string lastCutoutClip;   // call-scoped: noted while a cut-out pops on the host, consumed by BroadcastCutout
        public static void NoteCutoutSound(AudioSource source, AudioClip clip)
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.Hosting || !rt.Connected) return;
            if (clip != null && source != null && source.GetComponentInParent<SpecimenOneController>() != null) lastCutoutClip = clip.name;
        }

        void HostCutoutRequest(BinaryReader reader)
        {
            string key = Wire.Text(reader, 600); bool forced = reader.ReadBoolean();
            if (!RunActive) return;
            SpecimenOneController cutout = FindByKey(typeof(SpecimenOneController), key) as SpecimenOneController;
            if (cutout == null) return;
            if (Deployed(cutout)) { BroadcastCutout(cutout, cutout.geoContainer.activeSelf); return; }   // already decided: resend
            if (forced) cutout.Deploy(); else cutout.RandomDeploy();
        }

        void ClientCutoutState(BinaryReader reader)
        {
            string key = Wire.Text(reader, 600); bool popped = reader.ReadBoolean();
            forcedFront = Wire.Text(reader, 200); forcedBack = Wire.Text(reader, 200); forcedClip = Wire.Text(reader, 200);
            bool hostFlipped = reader.ReadBoolean();
            if (!RunActive) return;
            SpecimenOneController cutout = FindByKey(typeof(SpecimenOneController), key) as SpecimenOneController;
            if (cutout != null && LabMode) Check("cutout.side", cutout.didFlip == hostFlipped, "host flipped=" + hostFlipped + " mine=" + cutout.didFlip + " " + cutout.name);
            if (cutout == null || Deployed(cutout)) return;
            if (!popped) { SetDeployed(cutout); return; }
            ApplyingCutout = true;
            try { cutout.Deploy(); }
            catch (Exception ex) { Log("Cut-out pop failed: " + ex.Message); }
            finally { ApplyingCutout = false; forcedFront = forcedBack = forcedClip = null; }
        }

        void ApplyCutoutLook(SpecimenOneController cutout)
        {
            Material front = FindMaterial(forcedFront), back = FindMaterial(forcedBack);
            if (front != null && cutout.frontFace != null) cutout.frontFace.sharedMaterial = front;
            if (back != null && cutout.backFace != null) cutout.backFace.sharedMaterial = back;
            AudioClip clip = string.IsNullOrEmpty(forcedClip) ? null : FindClip(forcedClip);
            if (clip != null && cutout.audioSource != null)
            {
                cutout.audioSource.Stop();
                ApplyingSound = true;
                try { cutout.audioSource.PlayOneShot(clip); }
                finally { ApplyingSound = false; }
            }
            CutoutsApplied++;
            if (LabMode) Check("cutout.look", front != null && cutout.frontFace != null && MaterialName(cutout.frontFace.sharedMaterial) == forcedFront, "host picture=" + forcedFront + " mine=" + (cutout.frontFace != null && cutout.frontFace.sharedMaterial != null ? MaterialName(cutout.frontFace.sharedMaterial) : "none"));
        }

        Material FindMaterial(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Material m;
            if (materialCache.TryGetValue(name, out m) && m != null) return m;
            if (Time.realtimeSinceStartup - materialCacheAt < 5) return null;
            materialCacheAt = Time.realtimeSinceStartup;
            materialCache.Clear();
            foreach (Material each in Resources.FindObjectsOfTypeAll<Material>()) if (each != null && !materialCache.ContainsKey(each.name)) materialCache[each.name] = each;
            return materialCache.TryGetValue(name, out m) ? m : null;
        }
    }
}
