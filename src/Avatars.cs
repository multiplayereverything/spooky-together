using UnityEngine;
using Object = UnityEngine.Object;

namespace SpookyTogether
{
    // A teammate is drawn as an unlit capsule with a head, a lantern glow (the mansion is dark) and a name tag.
    // Avatars carry no colliders, so they never block the player or trigger room scripts.
    public sealed class RemoteAvatar
    {
        public GameObject Root;
        readonly Transform body, head;
        readonly Material material;
        readonly Light glow;
        readonly Transform axe;
        readonly System.Collections.Generic.List<Material> materials = new System.Collections.Generic.List<Material>();

        public RemoteAvatar(Color colour)
        {
            Root = new GameObject("SpookyTogether Teammate");
            Object.DontDestroyOnLoad(Root);
            Shader shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            material = new Material(shader); materials.Add(material);
            body = Part(PrimitiveType.Capsule, new Vector3(0, 0.85f, 0), new Vector3(0.55f, 0.85f, 0.55f));
            head = Part(PrimitiveType.Sphere, new Vector3(0, 1.55f, 0), new Vector3(0.42f, 0.42f, 0.42f));
            Transform eyes = Part(PrimitiveType.Cube, new Vector3(0, 1.58f, 0.19f), new Vector3(0.3f, 0.07f, 0.06f));
            Renderer eyeRenderer = eyes.GetComponent<Renderer>();
            eyeRenderer.sharedMaterial = new Material(shader) { color = Color.black }; materials.Add(eyeRenderer.sharedMaterial);
            eyes.SetParent(head, true);
            GameObject lightObject = new GameObject("Glow");
            lightObject.transform.SetParent(Root.transform, false);
            lightObject.transform.localPosition = new Vector3(0, 1.9f, 0);
            glow = lightObject.AddComponent<Light>();
            glow.type = LightType.Point; glow.range = 3.5f; glow.intensity = 0.7f; glow.shadows = LightShadows.None;
            // A simple held axe: handle plus head, shown while the teammate carries a weapon.
            axe = new GameObject("Axe").transform;
            axe.SetParent(Root.transform, false);
            axe.localPosition = new Vector3(0.38f, 1.05f, 0.15f);
            Material wood = new Material(shader) { color = new Color(0.45f, 0.28f, 0.12f) };
            Material steel = new Material(shader) { color = new Color(0.75f, 0.75f, 0.8f) };
            materials.Add(wood); materials.Add(steel);
            Transform handle = Part(PrimitiveType.Cube, new Vector3(0, 0.3f, 0), new Vector3(0.05f, 0.7f, 0.05f)); handle.SetParent(axe, false);
            handle.GetComponent<Renderer>().sharedMaterial = wood;
            Transform blade = Part(PrimitiveType.Cube, new Vector3(0.09f, 0.6f, 0), new Vector3(0.18f, 0.14f, 0.03f)); blade.SetParent(axe, false);
            blade.GetComponent<Renderer>().sharedMaterial = steel;
            axe.gameObject.SetActive(false);
            SetColour(colour);
        }

        public void Weapon(bool armed, float swingAge)
        {
            if (axe.gameObject.activeSelf != armed) axe.gameObject.SetActive(armed);
            if (!armed) return;
            // Swing: a quick chop forward over 0.35 s, then back to resting.
            float chop = swingAge >= 0 && swingAge < 0.35f ? Mathf.Sin(swingAge / 0.35f * Mathf.PI) : 0;
            axe.localRotation = Quaternion.Euler(-20 + chop * 110, 0, -10);
        }

        Transform Part(PrimitiveType type, Vector3 position, Vector3 scale)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            Collider collider = part.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);
            part.transform.SetParent(Root.transform, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            Renderer renderer = part.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return part.transform;
        }

        public void SetColour(Color colour)
        {
            material.color = colour;
            if (glow != null) glow.color = Color.Lerp(colour, Color.white, 0.5f);
        }

        public void Pose(Vector3 position, float yaw, float pitch, bool crawling, bool downed)
        {
            Root.transform.position = position;
            if (downed)
            {
                // Lying on the floor where they fell.
                Root.transform.rotation = Quaternion.Euler(0, yaw, 0) * Quaternion.Euler(-90, 0, 0);
                Root.transform.position = position + Vector3.up * 0.3f;
                Root.transform.localScale = Vector3.one;
                glow.intensity = 0.25f;
                return;
            }
            Root.transform.rotation = Quaternion.Euler(0, yaw, 0);
            Root.transform.localScale = crawling ? new Vector3(1, 0.55f, 1) : Vector3.one;
            head.localRotation = Quaternion.Euler(Mathf.Clamp(pitch > 180 ? pitch - 360 : pitch, -60, 60), 0, 0);
            glow.intensity = 0.7f;
        }

        // Materials are not owned by the GameObject; free them with it so join/leave cycles do not leak.
        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root);
            foreach (Material m in materials) if (m != null) Object.Destroy(m);
            materials.Clear();
        }
    }

    public sealed partial class TogetherRuntime
    {
        void DestroyAvatar(Peer peer)
        {
            if (peer.Avatar != null) peer.Avatar.Destroy();
            peer.Avatar = null;
        }

        bool Visible(Peer peer, SpookyCharacterController player)
        {
            return peer.Accepted && peer.HasPose && player != null && player.ready && RunActive && CurrentRoomSeq >= 0
                && peer.RoomSeq == CurrentRoomSeq && Time.realtimeSinceStartup - peer.PoseAt < 3f;
        }

        void UpdateAvatars()
        {
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            float blend = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            foreach (Peer peer in peers.Values)
            {
                if (!peer.HasPose) continue;
                peer.ShownPosition = Vector3.Lerp(peer.ShownPosition, peer.Position, blend);
                peer.ShownYaw = Mathf.LerpAngle(peer.ShownYaw, peer.Yaw, blend);
                bool visible = Visible(peer, player) && SpectatingId != peer.Id;
                if (!visible) { if (peer.Avatar != null) peer.Avatar.Root.SetActive(false); continue; }
                if (peer.Avatar == null) peer.Avatar = new RemoteAvatar(Colours[peer.Colour]);
                if (!peer.Avatar.Root.activeSelf) peer.Avatar.Root.SetActive(true);
                peer.Avatar.Pose(peer.ShownPosition, peer.ShownYaw, peer.Pitch, peer.Crawling, peer.Downed);
                peer.Avatar.Weapon(!peer.Downed && !string.IsNullOrEmpty(peer.Weapon), Time.realtimeSinceStartup - peer.SwingAt);
            }
        }

        void DrawNameTags(GUIStyle style)
        {
            if (Event.current.type != EventType.Repaint) return;
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (player == null || player.myCamera == null) return;
            Camera camera = player.myCamera;
            foreach (Peer peer in peers.Values)
            {
                if (!Visible(peer, player) || SpectatingId == peer.Id) continue;
                Vector3 anchor = peer.ShownPosition + Vector3.up * (peer.Downed ? 0.9f : 2.1f);
                Vector3 screen = camera.WorldToScreenPoint(anchor);
                if (screen.z <= 0.2f || screen.z > 40f) continue;
                string text = peer.Name + (peer.Downed ? " (down)" : "");
                Rect rect = new Rect(screen.x - 120, Screen.height - screen.y - 14, 240, 28);
                Color previous = GUI.color;
                GUI.color = new Color(0, 0, 0, 0.8f);
                GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, style);
                GUI.color = peer.Downed ? Color.gray : Colours[peer.Colour];
                GUI.Label(rect, text, style);
                GUI.color = previous;
            }
            if (SpectatingId >= 0)
            {
                Peer watched;
                if (peers.TryGetValue(SpectatingId, out watched))
                    GUI.Label(new Rect(Screen.width / 2 - 200, Screen.height - 70, 400, 30), "Watching " + watched.Name + " - click to switch", style);
            }
        }
    }
}
