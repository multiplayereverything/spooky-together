using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpookyTogether
{
    // A monster's one-shot sounds (attacks, hits, growls, screams) are cues from its own logic. On the host they are sent
    // to clients and played on the same AudioSource of the client's copy; the copy's own one-shots are muted, so everyone
    // hears what the host's monster actually did. Sounds a specimen plays through the player (2D stings) stay local.
    public sealed partial class TogetherRuntime
    {
        public static bool ApplyingSound;
        readonly Dictionary<int, int> hostObjectNet = new Dictionary<int, int>();   // tracked GameObject id -> net id
        static readonly Dictionary<string, AudioClip> clipCache = new Dictionary<string, AudioClip>();
        static float clipCacheBuiltAt = -100;
        int soundsThisSecond; float soundWindow;
        public int SoundsSent, SoundsPlayed, SoundsMuted;

        void TrackSoundOwner(int netId, GameObject go) { if (go != null) hostObjectNet[go.GetInstanceID()] = netId; }

        void ForgetSoundOwner(GameObject go) { if (go != null) hostObjectNet.Remove(go.GetInstanceID()); }

        public static bool BeforePlayOneShot(AudioSource source, AudioClip clip, float volume)
        {
            TogetherRuntime rt = Instance;
            if (ApplyingSound || rt == null || clip == null || source == null || !rt.Connected || !rt.RunActive) return true;
            Transform t = source.transform;
            if (rt.Hosting)
            {
                if (rt.hostObjectNet.Count == 0 || rt.AcceptedCount == 0) return true;
                for (Transform node = t; node != null; node = node.parent)
                {
                    int netId;
                    if (!rt.hostObjectNet.TryGetValue(node.gameObject.GetInstanceID(), out netId)) continue;
                    float now = Time.realtimeSinceStartup;
                    if (now - rt.soundWindow > 1) { rt.soundWindow = now; rt.soundsThisSecond = 0; }
                    if (++rt.soundsThisSecond > 30) return true;
                    string path = PathBelow(node, t), name = clip.name;
                    rt.Broadcast(Wire.Packet(Op.SpecSound, w => { w.Write(netId); Wire.Text(w, path); Wire.Text(w, name); w.Write(volume); }));
                    rt.SoundsSent++;
                    return true;
                }
                return true;
            }
            for (Transform node = t; node != null; node = node.parent)
                if (rt.puppetObjects.Contains(node.gameObject.GetInstanceID())) { rt.SoundsMuted++; return false; }
            return true;
        }

        static string PathBelow(Transform root, Transform target)
        {
            if (root == target) return "";
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            for (Transform t = target; t != null && t != root; t = t.parent) b.Insert(0, "/" + t.GetSiblingIndex());
            return b.ToString();
        }

        void ClientSound(BinaryReader reader)
        {
            int id = reader.ReadInt32(); string path = Wire.Text(reader, 400); string name = Wire.Text(reader, 200); float volume = reader.ReadSingle();
            Puppet puppet;
            if (!puppets.TryGetValue(id, out puppet) || puppet.Obj == null || !puppet.Obj.activeInHierarchy || snapshotSeq != CurrentRoomSeq) return;
            Transform node = puppet.Obj.transform;
            foreach (string part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int index;
                if (!int.TryParse(part, out index) || index >= node.childCount) return;
                node = node.GetChild(index);
            }
            AudioSource source = node.GetComponent<AudioSource>();
            AudioClip clip = FindClip(name);
            if (source == null || clip == null || !source.isActiveAndEnabled) return;
            ApplyingSound = true;
            try { source.PlayOneShot(clip, Mathf.Clamp(volume, 0, 4)); SoundsPlayed++; }
            finally { ApplyingSound = false; }
        }

        static AudioClip FindClip(string name)
        {
            AudioClip clip;
            if (clipCache.TryGetValue(name, out clip) && clip != null) return clip;
            if (Time.realtimeSinceStartup - clipCacheBuiltAt < 5) return null;
            clipCacheBuiltAt = Time.realtimeSinceStartup;
            clipCache.Clear();
            foreach (AudioClip c in Resources.FindObjectsOfTypeAll<AudioClip>()) if (c != null && !clipCache.ContainsKey(c.name)) clipCache[c.name] = c;
            return clipCache.TryGetValue(name, out clip) ? clip : null;
        }
    }
}
