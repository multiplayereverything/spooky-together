using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace SpookyTogether
{
    // Host-authoritative specimens.
    // Host: specimens created by SpecimenManager.InstantiateSpecimenFromPrefab get a network id and are announced with their
    //   spec id; specimens outside SpecimenManager (scene-placed, Dollhouse local rooms, husks) are found by a scan and
    //   announced by a key both machines can compute. A transpiler swaps "SpookyCharacterController.GetInstance().transform"
    //   inside specimen code for Target(this), the nearest standing player, and hits aimed at a remote player are sent to it.
    // Client: its own spawns are paired with the host's (or spawned on demand), driven from host snapshots, removed only
    //   when the host removes them, and cannot damage the local player; that damage arrives from the host.
    public sealed partial class TogetherRuntime
    {
        sealed class Puppet
        {
            public int NetId; public string SpecId; public GameObject Obj; public NavMeshAgent Agent;
            public Vector3 Target; public Quaternion TargetRot; public bool HasSnapshot, Active = true;
            public Vector3 SpawnPos; public Quaternion SpawnRot; public bool Rechase; public float RespawnAt;
            public int Hp; public string SceneKey; public float BindDeadline;
            public Vector3 Velocity; public float TargetAt; public bool DoorPending;
            // A copy this client spawned only because the host's arrived first; the client's own copy replaces it.
            public bool StandIn; public float StandInAt;
            public Animator Anim; public Animation Legacy; public bool AnimLooked; public int AnimState; public float AnimTime; public bool AnimDirty;
        }

        sealed class Unpaired { public GameObject Obj; public string SpecId; public float At; public int Seq; }

        // Host state.
        readonly Dictionary<int, GameObject> hostSpecs = new Dictionary<int, GameObject>();
        readonly Dictionary<int, string> hostSpecIds = new Dictionary<int, string>();
        readonly Dictionary<int, string> hostSceneKeys = new Dictionary<int, string>();
        readonly HashSet<int> hostTrackedObjects = new HashSet<int>();
        readonly Dictionary<int, int> targetOf = new Dictionary<int, int>();   // specimen component id -> peer id (-1 = host)
        readonly Dictionary<int, Transform> proxies = new Dictionary<int, Transform>();
        readonly Dictionary<int, Animator> hostAnimators = new Dictionary<int, Animator>();
        readonly Dictionary<int, Animation> hostLegacy = new Dictionary<int, Animation>();
        int nextNetId = 1;
        float nextSnapshot, nextPrune, nextScan;
        // Client state.
        readonly Dictionary<int, Puppet> puppets = new Dictionary<int, Puppet>();
        readonly List<Unpaired> unpaired = new List<Unpaired>();
        readonly HashSet<int> puppetObjects = new HashSet<int>();   // host-driven GameObjects; their hits never land locally
        int snapshotSeq = -1, hostSpecCount;
        float lastDesync, nextClientPrune;
        static int spawningForNet;
        static Transform spawnAnchor;   // process-scoped: one DontDestroyOnLoad helper object
        static Type[] specimenTypes;   // process-scoped: reflection cache
        public static bool AllowHostHurt, AllowEndSpecimen;
        // The specimen whose Target() call ran last, and in which frame: a husk measures its distance, then hurts with
        // its swarm as the source, so the husk is the attacker the swarm's hit belongs to.
        static Component lastTargetSpecimen; static int lastTargetFrame = -1;
        static readonly List<int> scratchIds = new List<int>();   // process-scoped: scratch buffer

        void ResetEnemies()
        {
            // A client that drops mid-run keeps its specimens; on reconnect they pair with the host's again (or are removed).
            if (keptRunId > 0 && RunActive)
                foreach (Puppet p in puppets.Values)
                    if (p.Obj != null && p.SceneKey == null) unpaired.Add(new Unpaired { Obj = p.Obj, SpecId = p.SpecId, At = float.MaxValue, Seq = int.MaxValue });
            List<Unpaired> kept = keptRunId > 0 && RunActive ? new List<Unpaired>(unpaired) : null;
            lastTargetSpecimen = null; lastTargetFrame = -1;
            hostSpecs.Clear(); hostSpecIds.Clear(); hostSceneKeys.Clear(); hostTrackedObjects.Clear(); targetOf.Clear(); hostAnimators.Clear(); hostLegacy.Clear(); hostObjectNet.Clear(); nextNetId = 1;
            puppets.Clear(); unpaired.Clear(); puppetObjects.Clear(); snapshotSeq = -1; hostSpecCount = 0;
            if (kept != null) unpaired.AddRange(kept);
            foreach (Transform proxy in proxies.Values) if (proxy != null) Object.Destroy(proxy.gameObject);
            proxies.Clear();
        }

        static SpecimenManager Specimens { get { try { return ServiceLocator.GetService<SpecimenManager>(); } catch { return null; } } }   // not registered outside a run

        // --- Retargeting (host) -----------------------------------------------------------------------
        static readonly Dictionary<Type, FieldInfo> ownerFields = new Dictionary<Type, FieldInfo>();   // process-scoped: reflection cache

        static Component OwnerOf(object owner)
        {
            for (int depth = 0; depth < 4 && owner != null; depth++)
            {
                Component component = owner as Component;
                if (component != null) return component;
                Type type = owner.GetType();
                FieldInfo field;
                if (!ownerFields.TryGetValue(type, out field))
                {
                    field = null;
                    foreach (FieldInfo candidate in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        if (candidate.Name.EndsWith("__this", StringComparison.Ordinal)) { field = candidate; break; }
                    ownerFields[type] = field;
                }
                if (field == null) return null;
                owner = field.GetValue(owner);
            }
            return null;
        }

        public static Transform Target(object owner)
        {
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            Transform local = player != null ? player.transform : null;
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.Hosting || rt.peers.Count == 0 || local == null || Mutant("retarget")) return local;
            Component specimen = OwnerOf(owner);
            if (specimen == null) return local;
            lastTargetSpecimen = specimen; lastTargetFrame = Time.frameCount;
            return rt.ChooseTarget(specimen, player);
        }

        Transform ChooseTarget(Component specimen, SpookyCharacterController player)
        {
            Vector3 from = specimen.transform.position;
            int key = specimen.GetInstanceID();
            int current; bool known = targetOf.TryGetValue(key, out current); if (!known) current = -1;
            int best = -2; float bestDistance = float.MaxValue, currentDistance = float.MaxValue;
            if (!LocallyDowned && player.ready)
            {
                float d = (player.transform.position - from).sqrMagnitude;
                best = -1; bestDistance = d; if (current == -1) currentDistance = d;
            }
            float now = Time.realtimeSinceStartup;
            foreach (Peer peer in peers.Values)
            {
                if (!peer.Accepted || !peer.HasPose || peer.Downed || peer.RoomSeq != CurrentRoomSeq || now - peer.PoseAt > 3f) continue;
                float d = (peer.ShownPosition - from).sqrMagnitude;
                if (peer.Id == current) currentDistance = d;
                if (d < bestDistance) { bestDistance = d; best = peer.Id; }
            }
            if (best == -2) best = -1;
            // Hysteresis: keep the current target unless another player is clearly (2 m) closer.
            if (known && best != current && currentDistance < float.MaxValue && Mathf.Sqrt(currentDistance) - Mathf.Sqrt(bestDistance) < 2f) best = current;
            targetOf[key] = best;
            return best < 0 ? player.transform : Proxy(best);
        }

        Transform Proxy(int peerId)
        {
            Transform proxy;
            if (!proxies.TryGetValue(peerId, out proxy) || proxy == null)
            {
                GameObject go = new GameObject("SpookyTogether Target " + peerId);
                Object.DontDestroyOnLoad(go);
                proxy = go.transform;
                proxies[peerId] = proxy;
            }
            Peer peer;
            if (peers.TryGetValue(peerId, out peer)) { proxy.position = peer.ShownPosition; proxy.rotation = Quaternion.Euler(0, peer.ShownYaw, 0); }
            return proxy;
        }

        // Rewritten reads of a kept player per specimen type, for the lab's retarget.cached check.
        static readonly Dictionary<string, int> retargetCached = new Dictionary<string, int>();   // process-scoped: filled once while patching

        static IEnumerable<CodeInstruction> RetargetTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo getInstance = AccessTools.Method(typeof(SpookyCharacterController), "GetInstance");
            MethodInfo getTransform = AccessTools.PropertyGetter(typeof(Component), "transform");
            MethodInfo target = AccessTools.Method(typeof(TogetherRuntime), "Target");
            IList<LocalVariableInfo> locals = null;
            try { MethodBody body = original.GetMethodBody(); if (body != null) locals = body.LocalVariables; } catch (Exception) { }   // no body: only the direct pattern applies
            bool cached = !MutantArg("cachedplayer");
            int rewritten = 0, cachedReads = 0;
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            for (int i = 0; i < list.Count - 1; i++)
            {
                bool readsTransform = (list[i + 1].opcode == OpCodes.Callvirt || list[i + 1].opcode == OpCodes.Call) && Equals(list[i + 1].operand, getTransform);
                if (!readsTransform) continue;
                if (list[i].opcode == OpCodes.Call && Equals(list[i].operand, getInstance))
                {
                    CodeInstruction self = new CodeInstruction(OpCodes.Ldarg_0);
                    self.labels.AddRange(list[i].labels); self.blocks.AddRange(list[i].blocks);
                    list[i] = self;
                    list[i + 1] = new CodeInstruction(OpCodes.Call, target);
                    i++; rewritten++;
                }
                else if (cached && LoadsPlayer(list[i], locals))
                {
                    // The player kept in a local or field (Spec6, Spec8, HookedDoll, Frenzy, Nightmare keep it before
                    // measuring): leave the load (it may be a branch target), drop its value, and ask Target instead.
                    list[i + 1] = new CodeInstruction(OpCodes.Call, target);
                    list.Insert(i + 1, new CodeInstruction(OpCodes.Pop));
                    list.Insert(i + 2, new CodeInstruction(original.IsStatic ? OpCodes.Ldnull : OpCodes.Ldarg_0));
                    i += 3; rewritten++; cachedReads++;
                }
            }
            if (cachedReads > 0)
            {
                Type owner = original.DeclaringType;
                while (owner != null && owner.DeclaringType != null) owner = owner.DeclaringType;
                string name = owner != null ? owner.Name : "?";
                int count; retargetCached.TryGetValue(name, out count); retargetCached[name] = count + cachedReads;
            }
            return list;
        }

        static bool LoadsPlayer(CodeInstruction ci, IList<LocalVariableInfo> locals)
        {
            OpCode op = ci.opcode;
            if (op == OpCodes.Ldfld || op == OpCodes.Ldsfld) { FieldInfo f = ci.operand as FieldInfo; return f != null && f.FieldType == typeof(SpookyCharacterController); }
            int index = -1;
            if (op == OpCodes.Ldloc_0) index = 0; else if (op == OpCodes.Ldloc_1) index = 1; else if (op == OpCodes.Ldloc_2) index = 2; else if (op == OpCodes.Ldloc_3) index = 3;
            else if (op == OpCodes.Ldloc_S || op == OpCodes.Ldloc)
            {
                LocalBuilder builder = ci.operand as LocalBuilder;
                if (builder != null) return builder.LocalType == typeof(SpookyCharacterController);
                if (ci.operand is byte) index = (byte)ci.operand; else if (ci.operand is int) index = (int)ci.operand; else if (ci.operand is short) index = (short)ci.operand;
            }
            return index >= 0 && locals != null && index < locals.Count && locals[index].LocalType == typeof(SpookyCharacterController);
        }

        // Patching runs before the lab reads its arguments, so a mutation switch for a transpiler reads them itself.
        static bool MutantArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            bool lab = false, hit = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-st-lab") lab = true;
                if (args[i] == "-st-mutate" && i + 1 < args.Length && Array.IndexOf(args[i + 1].Split(','), name) >= 0) hit = true;
            }
            return lab && hit;
        }

        // Lab: every specimen that keeps the player before measuring must have had those reads rewritten.
        void CheckCachedRetarget()
        {
            string[] cachedPlayer = { "Spec6Controller", "Spec8Controller", "HookedDollControl", "FrenzyControl", "NightmareController" };
            foreach (string type in cachedPlayer)
            {
                int reads; retargetCached.TryGetValue(type, out reads);
                Check("retarget.cached." + type, reads > 0, "rewritten reads of the kept player=" + reads);
            }
        }

        // Patches every instance method of specimen code (and its iterator/closure classes) that reads the player transform.
        public static int PatchSpecimenTargets(HarmonyLib.Harmony harmony)
        {
            MethodInfo getInstance = AccessTools.Method(typeof(SpookyCharacterController), "GetInstance");
            int token = getInstance.MetadataToken, patched = 0;
            HarmonyMethod transpiler = new HarmonyMethod(typeof(TogetherRuntime), "RetargetTranspiler");
            List<Type> roots = new List<Type>();
            string[] extras = { "ClownSpecControl", "HookedDollControl", "FrenzyControl", "EndlessIndividualHuskController", "EndlessHuskSwarmController", "NightmareController" };
            foreach (Type type in typeof(SpecimenManager).Assembly.GetTypes())
            {
                if (type.IsInterface || type.IsValueType || type.DeclaringType != null || type.IsAbstract) continue;
                // The AFK Taker belongs to one idle player; it stays local.
                if (type == typeof(Spec9Controller) || !typeof(Component).IsAssignableFrom(type)) continue;
                if (typeof(SpecimenInterface).IsAssignableFrom(type) || typeof(LocalSpecimenInterface).IsAssignableFrom(type) || Array.IndexOf(extras, type.Name) >= 0) roots.Add(type);
            }
            specimenTypes = roots.ToArray();
            List<Type> all = new List<Type>();
            // The Taker stays host-bound (hp, reflects, ending) but is not retargeted: each player's copy attacks its player.
            foreach (Type root in roots) if (root != typeof(TakerBossControl)) Collect(root, all);
            foreach (Type type in all)
            {
                if (type.IsValueType || type.ContainsGenericParameters) continue;
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters) continue;
                    MethodBody body;
                    try { body = method.GetMethodBody(); } catch { continue; }   // methods without IL (extern, runtime) are skipped
                    if (body == null || !CallsToken(body.GetILAsByteArray(), token)) continue;
                    try { harmony.Patch(method, transpiler: transpiler); patched++; }
                    catch (Exception ex) { Log("Retarget patch failed " + type.Name + "." + method.Name + ": " + ex.Message); }
                }
            }
            return patched;
        }

        static void Collect(Type type, List<Type> into)
        {
            into.Add(type);
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) Collect(nested, into);
        }

        static bool CallsToken(byte[] il, int token)
        {
            if (il == null) return false;
            byte b0 = (byte)token, b1 = (byte)(token >> 8), b2 = (byte)(token >> 16), b3 = (byte)(token >> 24);
            for (int i = 0; i + 4 < il.Length; i++)
                if (il[i] == 0x28 && il[i + 1] == b0 && il[i + 2] == b1 && il[i + 3] == b2 && il[i + 4] == b3) return true;
            return false;
        }

        // --- Damage routing ---------------------------------------------------------------------------
        bool IsPuppet(Component c)
        {
            return c != null && (puppetObjects.Contains(c.gameObject.GetInstanceID()) || puppetObjects.Contains(c.transform.root.gameObject.GetInstanceID()));
        }

        public static bool BeforeHurt(SpookyCharacterController player, float dmg, SpecimenInterface source, bool doFx, float fxDuration)
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.Connected || !rt.RunActive) return true;
            Component specimen = source as Component;
            // The Taker's attacks (fireballs, hands, minions, pillars) are spawned by each player's own copy of the boss
            // and aim at that player, so they hurt locally everywhere; only the fight's outcome is the host's.
            if (specimen is TakerBossControl) return true;
            if (rt.IsClient)
            {
                // A host-driven specimen never damages a client directly; the host decides those hits.
                if (AllowHostHurt || specimen == null) return true;
                return !rt.IsPuppet(specimen);
            }
            if (specimen == null || Mutant("hurtrouting")) return true;
            int peerId;
            bool known = rt.targetOf.TryGetValue(specimen.GetInstanceID(), out peerId);
            if (!known && specimen is EndlessHuskSwarmController && lastTargetSpecimen != null && lastTargetFrame == Time.frameCount)
                known = rt.targetOf.TryGetValue(lastTargetSpecimen.GetInstanceID(), out peerId);
            if (!known || peerId < 0) return true;
            string killscreen = null;
            try { killscreen = source.killscreenName; } catch { }   // a destroyed specimen's getter throws
            rt.SendHurt(peerId, dmg, killscreen, doFx, fxDuration);
            return false;
        }

        void SendHurt(int peerId, float dmg, string killscreen, bool doFx, float fxDuration)
        {
            Peer peer;
            if (!peers.TryGetValue(peerId, out peer) || peer.Downed) return;
            Network.Send(peerId, Wire.Packet(Op.HurtPeer, w => { w.Write(dmg); Wire.Text(w, killscreen ?? ""); w.Write(doFx); w.Write(fxDuration); }));
        }

        // Unknown Specimen 1 bypasses Hurt: a lethal hit fakes a death by moving rooms, and its second pursuit quits the game.
        public static bool BeforeUnknownOneHit(UnknownSpecimen1Controller spec)
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.Connected || !rt.RunActive) return true;
            if (rt.IsClient) return !rt.IsPuppet(spec);
            int peerId;
            if (!rt.targetOf.TryGetValue(spec.GetInstanceID(), out peerId) || peerId < 0) return true;
            string killscreen = null; try { killscreen = ((SpecimenInterface)spec).killscreenName; } catch { }   // a destroyed specimen's getter throws
            rt.SendHurt(peerId, spec.damage, killscreen, true, 0.5f);
            return false;
        }

        public static bool BeforeExitGame()
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.Connected || !rt.RunActive) return true;
            // In a shared run "the game quits" becomes a normal knock-down instead of closing a player's game.
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (player != null && !rt.LocallyDowned)
            {
                AllowHostHurt = true;
                try { player.Hurt(99999, new SimpleSpecStruct { _killscreenName = "GenericGameover" }, null); }
                finally { AllowHostHurt = false; }
            }
            return false;
        }

        void ClientHurt(BinaryReader reader)
        {
            float dmg = reader.ReadSingle(); string killscreen = Wire.Text(reader, 200); bool doFx = reader.ReadBoolean(); float fx = reader.ReadSingle();
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (player == null || !player.ready || LocallyDowned || !RunActive || float.IsNaN(dmg) || dmg < 0) return;
            NoteHurtReceived();
            AllowHostHurt = true;
            try { player.Hurt(dmg, new SimpleSpecStruct { _killscreenName = string.IsNullOrEmpty(killscreen) ? "GenericGameover" : killscreen }, null, doFx, Mathf.Clamp(fx, 0, 5)); }
            finally { AllowHostHurt = false; }
        }

        // --- SpecimenManager spawns -------------------------------------------------------------------
        public static bool BeforeRechase()
        {
            TogetherRuntime rt = Instance;
            return rt == null || !rt.IsClient || !rt.Connected;
        }

        public static void AfterSpecimenCreated(GameObject spec, SpecimenConfig config, bool rechase)
        {
            TogetherRuntime rt = Instance;
            if (rt == null || spec == null || config == null || !rt.Connected) return;
            if (rt.Hosting)
            {
                int id = rt.nextNetId++;
                rt.hostSpecs[id] = spec; rt.hostSpecIds[id] = config.uniqueId; rt.hostTrackedObjects.Add(spec.GetInstanceID());
                rt.TrackSoundOwner(id, spec);
                rt.Broadcast(SpawnPacket(id, config.uniqueId, spec.transform.position, spec.transform.rotation, rechase, HpOf(spec)));
                Log("Specimen " + id + " spawned: " + config.uniqueId);
                return;
            }
            rt.puppetObjects.Add(spec.GetInstanceID());
            if (spawningForNet > 0)
            {
                Puppet puppet;
                if (rt.puppets.TryGetValue(spawningForNet, out puppet)) { rt.Bind(puppet, spec); puppet.StandIn = true; puppet.StandInAt = Time.realtimeSinceStartup; }
                return;
            }
            if (!Mutant("pairing") && rt.AdoptOwnSpawn(spec, config.uniqueId)) return;
            rt.unpaired.Add(new Unpaired { Obj = spec, SpecId = config.uniqueId, At = Time.realtimeSinceStartup, Seq = rt.CurrentRoomSeq });
        }

        static int HpOf(GameObject spec)
        {
            SpecimenInterface si = spec != null ? spec.GetComponent<SpecimenInterface>() : null;
            try { return si != null ? si.hp : 0; } catch { return 0; }   // specimens without hp throw from the interface
        }

        static byte[] SpawnPacket(int id, string specId, Vector3 pos, Quaternion rot, bool rechase, int hp)
        {
            return Wire.Packet(Op.SpecSpawn, w =>
            {
                w.Write(id); Wire.Text(w, specId); w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
                w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w); w.Write(rechase); w.Write(hp);
            });
        }

        static byte[] BindPacket(int id, string key) { return Wire.Packet(Op.SpecBind, w => { w.Write(id); Wire.Text(w, key); }); }

        void SendSpecimensTo(int peer)
        {
            foreach (KeyValuePair<int, GameObject> pair in hostSpecs)
            {
                if (pair.Value == null) continue;
                string key;
                if (hostSceneKeys.TryGetValue(pair.Key, out key)) Network.Send(peer, BindPacket(pair.Key, key));
                else Network.Send(peer, SpawnPacket(pair.Key, hostSpecIds[pair.Key], pair.Value.transform.position, pair.Value.transform.rotation, true, HpOf(pair.Value)));
            }
        }

        void ClientSpawn(BinaryReader reader)
        {
            int id = reader.ReadInt32(); string specId = Wire.Text(reader, 200);
            Vector3 pos = new Vector3(Wire.Number(reader), Wire.Number(reader), Wire.Number(reader));
            Quaternion rot = Normalised(new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
            bool rechase = reader.ReadBoolean(); int hp = reader.ReadInt32();
            if (puppets.ContainsKey(id)) return;
            Puppet puppet = new Puppet { NetId = id, SpecId = specId, SpawnPos = pos, SpawnRot = rot, Rechase = rechase, Target = pos, TargetRot = rot, Hp = hp };
            puppets[id] = puppet;
            for (int i = 0; i < unpaired.Count; i++)
            {
                if (unpaired[i].Obj != null && unpaired[i].SpecId == specId)
                {
                    Bind(puppet, unpaired[i].Obj);
                    unpaired.RemoveAt(i);
                    return;
                }
            }
            // No copy yet: the room script here may still spawn its own (AdoptOwnSpawn). The update loop spawns a stand-in
            // once this client is in the host's room and the room has been ready a moment.
            puppet.RespawnAt = Time.realtimeSinceStartup + (Mutant("pairing") ? 0f : StandInGrace);
        }

        const float StandInGrace = 1.5f;
        float roomReadyAt = -100;
        int roomReadySeq = -1;
        public void NoteRoomReady() { roomReadyAt = Time.realtimeSinceStartup; roomReadySeq = CurrentRoomSeq; }

        // Stand-ins wait for this game's own room to be ready: a copy spawned while the room loads has no spawn door yet,
        // and chasers such as EMKHMonster5 then emerge from a missing door every frame.
        bool StandInWaits(float now) { return (roomReadySeq != CurrentRoomSeq && !Mutant("earlypuppet")) || now - roomReadyAt < StandInGrace; }

        public int OwnSpawnsAdopted, StandInsReplaced;   // lab counters

        // The host's spawn often arrives before this client's room script spawns its own copy. That own copy is the one
        // the room script holds, so it becomes the host's specimen: bound to a host specimen still waiting for a copy, or
        // replacing a stand-in spawned for it in the last 10 s.
        bool AdoptOwnSpawn(GameObject spec, string specId)
        {
            Puppet waiting = null, standIn = null;
            float now = Time.realtimeSinceStartup;
            foreach (Puppet p in puppets.Values)
            {
                if (p.SceneKey != null || p.SpecId != specId) continue;
                if (p.Obj == null && waiting == null) waiting = p;
                else if (p.Obj != null && p.StandIn && now - p.StandInAt < 10f && standIn == null) standIn = p;
            }
            if (waiting != null)
            {
                Bind(waiting, spec);
                OwnSpawnsAdopted++;
                Log("Own " + specId + " is the host's specimen " + waiting.NetId);
                return true;
            }
            if (standIn == null) return false;
            GameObject old = standIn.Obj;
            puppetObjects.Remove(old.GetInstanceID());
            RemoveLocalSpecimen(old);
            standIn.StandIn = false;
            Bind(standIn, spec);
            StandInsReplaced++;
            Log("Own " + specId + " replaces the stand-in for the host's specimen " + standIn.NetId);
            return true;
        }

        static Quaternion Normalised(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m < 0.0001f || float.IsNaN(m) ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }

        void SpawnPuppet(Puppet puppet)
        {
            SpecimenManager manager = Specimens;
            if (manager == null) return;
            puppet.RespawnAt = Time.realtimeSinceStartup + 2f;
            if (LabMode) Check("puppet.roomready", roomReadySeq == CurrentRoomSeq, puppet.SpecId + " in room " + CurrentRoomSeq + ", ready room " + roomReadySeq);
            if (spawnAnchor == null) { GameObject anchor = new GameObject("SpookyTogether SpawnAnchor"); Object.DontDestroyOnLoad(anchor); spawnAnchor = anchor.transform; }
            spawnAnchor.position = puppet.HasSnapshot ? puppet.Target : puppet.SpawnPos;
            spawnAnchor.rotation = puppet.HasSnapshot ? puppet.TargetRot : puppet.SpawnRot;
            spawningForNet = puppet.NetId;
            try { manager.StartSpecimen(puppet.SpecId, spawnAnchor, puppet.Rechase); }
            catch (Exception ex) { Log("Could not spawn host specimen " + puppet.SpecId + ": " + ex.Message); }
            finally { spawningForNet = 0; }
        }

        void Bind(Puppet puppet, GameObject obj)
        {
            puppet.Obj = obj;
            puppetObjects.Add(obj.GetInstanceID());
            if (puppet.SceneKey == null)
            {
                // Chasers count their remaining rooms in hp; matching it keeps room-end behaviour in step with the host.
                SpecimenInterface si = obj.GetComponent<SpecimenInterface>();
                try { if (si != null) si.hp = puppet.Hp; } catch { }   // specimens without hp throw from the interface
            }
            // A chaser that reaches a client after that client's room is ready never got the room's spawn door from
            // SpecimenManager.StartRoom; hand it the current door so its emergence logic has one.
            if (puppet.SceneKey == null && puppet.Rechase && Closest<HSMRoomReadyState>() != null) puppet.DoorPending = true;
            puppet.Agent = obj.GetComponent<NavMeshAgent>();
            if (puppet.Agent != null) { puppet.Agent.updatePosition = false; puppet.Agent.updateRotation = false; }
            if (puppet.HasSnapshot) obj.transform.SetPositionAndRotation(puppet.Target, puppet.TargetRot);
        }

        // StartRoom starts coroutines, which Unity refuses on an inactive object; wait until the copy is active.
        void GiveLateDoor(Puppet puppet)
        {
            puppet.DoorPending = false;
            HSMRoomReadyState room = Closest<HSMRoomReadyState>();
            SpecimenInterface chaser = puppet.Obj.GetComponent<SpecimenInterface>();
            if (room == null || room.roomRuntimeData == null || room.roomRuntimeData.playerSpawn == null || chaser == null) return;
            try { chaser.StartRoom(room.roomRuntimeData.playerSpawn); }
            catch (Exception ex) { Log("Late chaser setup failed for " + puppet.SpecId + ": " + ex.Message); }
        }

        void ClientDespawn(BinaryReader reader)
        {
            int id = reader.ReadInt32();
            Puppet puppet;
            if (!puppets.TryGetValue(id, out puppet)) return;
            puppets.Remove(id);
            if (puppet.Obj == null) return;
            puppetObjects.Remove(puppet.Obj.GetInstanceID());
            RemoveLocalSpecimen(puppet.Obj);
        }

        static void RemoveLocalSpecimen(GameObject obj)
        {
            if (obj == null) return;
            SpecimenManager manager = Specimens;
            AllowEndSpecimen = true;
            try
            {
                if (manager != null && manager.activeSpecimens.Contains(obj)) manager.EndSpecimen(obj);
                else Object.Destroy(obj);
            }
            catch (Exception ex) { Log("Specimen cleanup failed: " + ex.Message); Object.Destroy(obj); }
            finally { AllowEndSpecimen = false; }
        }

        // Specimens this client spawned while it had no host are nobody's copy: pair them with the host's spawns that follow
        // the rejoin, or remove them like any other unconfirmed spawn.
        void AdoptStraySpecimens()
        {
            SpecimenManager manager = Specimens;
            if (manager == null || manager.activeSpecimens == null) return;
            int adopted = 0;
            foreach (GameObject spec in manager.activeSpecimens)
            {
                if (spec == null || puppetObjects.Contains(spec.GetInstanceID())) continue;
                bool known = false;
                foreach (Unpaired u in unpaired) if (u.Obj == spec) { known = true; break; }
                if (known) continue;
                SpecimenInterface si = spec.GetComponent<SpecimenInterface>();
                string specId = null;
                try { specId = si != null ? si.spawnedFromSpecId : null; } catch (Exception) { }   // a destroyed specimen's getter throws
                if (string.IsNullOrEmpty(specId)) continue;
                puppetObjects.Add(spec.GetInstanceID());
                unpaired.Add(new Unpaired { Obj = spec, SpecId = specId, At = Time.realtimeSinceStartup, Seq = CurrentRoomSeq });
                adopted++;
            }
            if (adopted > 0) Log("Rejoined with " + adopted + " specimen(s) spawned alone; pairing them with the host's.");
        }

        // At run end the game's ClearSpecimens only hid the host-driven copies (see BeforeEndSpecimen); end them for real
        // so none stays in SpecimenManager.activeSpecimens (several are DontDestroyOnLoad) into the next run.
        void EndPuppets()
        {
            if (!IsClient) return;
            foreach (Puppet puppet in puppets.Values) if (puppet.Obj != null) { puppetObjects.Remove(puppet.Obj.GetInstanceID()); RemoveLocalSpecimen(puppet.Obj); }
            puppets.Clear();
        }

        // Clients keep host-driven specimens until the host removes them; their local room logic may disagree.
        public static bool BeforeEndSpecimen(GameObject spec)
        {
            TogetherRuntime rt = Instance;
            if (rt == null || AllowEndSpecimen || !rt.IsClient || !rt.Connected || spec == null) return true;
            foreach (Puppet puppet in rt.puppets.Values) if (puppet.Obj == spec) { spec.SetActive(false); return false; }
            return true;
        }

        // A chaser queued at room load updates for a few frames before the room hands it a door; the game then throws
        // every frame in TweenToPlayerSpawn. Skipping the tween until there is a door changes nothing else.
        public static bool BeforeTweenToSpawn(SpawnDoor spawnFrom) { return spawnFrom != null; }

        // --- Specimens outside SpecimenManager -----------------------------------------------------------
        void HostScan()
        {
            if (specimenTypes == null || Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + 2f;
            foreach (Type type in specimenTypes)
            {
                foreach (Object found in Object.FindObjectsOfType(type))
                {
                    Component c = found as Component;
                    if (c == null || hostTrackedObjects.Contains(c.gameObject.GetInstanceID())) continue;
                    string key = SceneKey(c);
                    if (key == null) continue;
                    hostTrackedObjects.Add(c.gameObject.GetInstanceID());
                    int id = nextNetId++;
                    hostSpecs[id] = c.gameObject; hostSpecIds[id] = type.Name; hostSceneKeys[id] = key;
                    TrackSoundOwner(id, c.gameObject);
                    Broadcast(BindPacket(id, key));
                }
            }
        }

        static readonly System.Text.StringBuilder keyBuilder = new System.Text.StringBuilder();   // process-scoped: scratch buffer
        static FieldInfo huskList;   // process-scoped: reflection cache

        // The same key on every machine for the same object: husks by swarm index, everything else by hierarchy path.
        static string SceneKey(Component c)
        {
            if (c is EndlessIndividualHuskController)
            {
                if (huskList == null) huskList = AccessTools.Field(typeof(EndlessHuskSwarmController), "activeHusks");
                foreach (EndlessHuskSwarmController swarm in Object.FindObjectsOfType<EndlessHuskSwarmController>())
                {
                    System.Collections.IList list = huskList != null ? huskList.GetValue(swarm) as System.Collections.IList : null;
                    int index = list != null ? list.IndexOf(c) : -1;
                    if (index >= 0) return "husk/" + index;
                }
                return null;
            }
            keyBuilder.Length = 0;
            for (Transform t = c.transform; t != null; t = t.parent)
                keyBuilder.Insert(0, "/" + t.name + (t.parent != null ? "#" + t.GetSiblingIndex() : ""));
            keyBuilder.Insert(0, c.gameObject.scene.name + ":" + c.GetType().Name);
            return keyBuilder.ToString();
        }

        void ClientBind(BinaryReader reader)
        {
            int id = reader.ReadInt32(); string key = Wire.Text(reader, 600);
            if (puppets.ContainsKey(id)) return;
            Puppet puppet = new Puppet { NetId = id, SceneKey = key, BindDeadline = Time.realtimeSinceStartup + 15 };
            puppets[id] = puppet;
            TryBindScene(puppet);
        }

        void TryBindScene(Puppet puppet)
        {
            if (specimenTypes == null) return;
            bool husk = puppet.SceneKey.StartsWith("husk/", StringComparison.Ordinal);
            foreach (Type type in specimenTypes)
            {
                if (husk ? type != typeof(EndlessIndividualHuskController) : puppet.SceneKey.IndexOf(":" + type.Name + "/", StringComparison.Ordinal) < 0) continue;
                foreach (Object found in Object.FindObjectsOfType(type))
                {
                    Component c = found as Component;
                    if (c != null && SceneKey(c) == puppet.SceneKey) { Bind(puppet, c.gameObject); return; }
                }
            }
        }

        // --- Snapshots ----------------------------------------------------------------------------------
        void HostEnemyTick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextSnapshot) return;
            nextSnapshot = now + 1f / 15f;
            scratchIds.Clear();
            foreach (KeyValuePair<int, GameObject> pair in hostSpecs) if (pair.Value == null) scratchIds.Add(pair.Key);
            foreach (int id in scratchIds)
            {
                hostSpecs.Remove(id); hostSpecIds.Remove(id); hostSceneKeys.Remove(id); hostAnimators.Remove(id); hostLegacy.Remove(id);
                int gone = id;
                Broadcast(Wire.Packet(Op.SpecDespawn, w => w.Write(gone)));
            }
            if (now >= nextPrune)
            {
                nextPrune = now + 10; PruneTargets(); hostTrackedObjects.RemoveWhere(DeadObject);
                scratchIds.Clear();
                foreach (KeyValuePair<int, int> pair in hostObjectNet) if (!hostSpecs.ContainsKey(pair.Value)) scratchIds.Add(pair.Key);
                foreach (int key in scratchIds) hostObjectNet.Remove(key);
            }
            HostScan();
            if (AcceptedCount == 0) return;
            int seq = CurrentRoomSeq;
            Broadcast(Wire.Packet(Op.SpecSnapshot, w =>
            {
                w.Write(seq); w.Write((ushort)hostSpecs.Count);
                foreach (KeyValuePair<int, GameObject> pair in hostSpecs)
                {
                    Transform t = pair.Value.transform; Quaternion r = t.rotation;
                    w.Write(pair.Key); w.Write(t.position.x); w.Write(t.position.y); w.Write(t.position.z);
                    w.Write(r.x); w.Write(r.y); w.Write(r.z); w.Write(r.w); w.Write(pair.Value.activeSelf);
                    // The animation the host's specimen is playing, so client copies show the same attack or stun.
                    // Animator: its state hash. Legacy Animation: -(clip index + 1), the clip order being the same prefab's.
                    int animState = 0; float animTime = 0;
                    Animator anim = HostAnimator(pair.Key, pair.Value);
                    if (anim != null && anim.isActiveAndEnabled && anim.runtimeAnimatorController != null)
                    {
                        AnimatorStateInfo info = anim.GetCurrentAnimatorStateInfo(0);
                        animState = info.fullPathHash; animTime = info.normalizedTime;
                    }
                    else
                    {
                        Animation legacy = HostLegacy(pair.Key, pair.Value);
                        if (legacy != null && legacy.isActiveAndEnabled && legacy.isPlaying)
                        {
                            int index = 0;
                            foreach (AnimationState clip in legacy)
                            {
                                if (clip.enabled && clip.weight > 0.5f) { animState = -(index + 1); animTime = clip.normalizedTime; break; }
                                index++;
                            }
                        }
                    }
                    w.Write(animState); w.Write(animTime);
                    w.Write(HpOf(pair.Value));
                }
            }));
        }

        Animator HostAnimator(int id, GameObject spec)
        {
            Animator anim;
            if (!hostAnimators.TryGetValue(id, out anim)) { anim = spec.GetComponentInChildren<Animator>(true); hostAnimators[id] = anim; }
            return anim;
        }

        Animation HostLegacy(int id, GameObject spec)
        {
            Animation legacy;
            if (!hostLegacy.TryGetValue(id, out legacy)) { legacy = spec.GetComponentInChildren<Animation>(true); hostLegacy[id] = legacy; }
            return legacy;
        }

        static bool DeadObject(int instanceId) { return !(Resources.InstanceIDToObject(instanceId) is GameObject); }

        void PruneTargets()
        {
            HashSet<int> alive = new HashSet<int>();
            foreach (GameObject spec in hostSpecs.Values) if (spec != null) foreach (Component c in spec.GetComponentsInChildren<MonoBehaviour>(true)) alive.Add(c.GetInstanceID());
            scratchIds.Clear();
            foreach (int key in targetOf.Keys) if (!alive.Contains(key)) scratchIds.Add(key);
            foreach (int key in scratchIds) targetOf.Remove(key);
            scratchIds.Clear();
            foreach (KeyValuePair<int, Transform> pair in proxies) if (!peers.ContainsKey(pair.Key)) scratchIds.Add(pair.Key);
            foreach (int key in scratchIds) { if (proxies[key] != null) Object.Destroy(proxies[key].gameObject); proxies.Remove(key); }
        }

        void ClientSnapshot(BinaryReader reader)
        {
            int seq = reader.ReadInt32(); int count = reader.ReadUInt16();
            snapshotSeq = seq; hostSpecCount = count;
            for (int i = 0; i < count; i++)
            {
                int id = reader.ReadInt32();
                Vector3 pos = new Vector3(Wire.Number(reader), Wire.Number(reader), Wire.Number(reader));
                Quaternion rot = Normalised(new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
                bool active = reader.ReadBoolean();
                int animState = reader.ReadInt32(); float animTime = reader.ReadSingle();
                int hp = reader.ReadInt32();
                Puppet puppet;
                if (!puppets.TryGetValue(id, out puppet)) continue;
                // Health / rooms-left follows the host (e.g. the Taker's phase changes at 100 hp).
                if (puppet.Obj != null && hp != puppet.Hp)
                {
                    puppet.Hp = hp;
                    SpecimenInterface si = puppet.Obj.GetComponent<SpecimenInterface>();
                    try { if (si != null) si.hp = hp; } catch { }   // specimens without hp throw from the interface
                }
                // Re-applied on every snapshot: the client copy's own logic may switch animations, the host's wins.
                if (animState != 0) { puppet.AnimState = animState; puppet.AnimTime = animTime; puppet.AnimDirty = true; }
                else if (!puppet.AnimLooked) puppet.AnimDirty = true;
                if (!puppet.HasSnapshot && puppet.Obj != null) puppet.Obj.transform.SetPositionAndRotation(pos, rot);
                float stamp = Time.realtimeSinceStartup;
                if (puppet.HasSnapshot && stamp - puppet.TargetAt > 0.01f)
                {
                    Vector3 v = (pos - puppet.Target) / (stamp - puppet.TargetAt);
                    puppet.Velocity = v.sqrMagnitude > 900f ? Vector3.zero : v;   // a jump (teleport) is not motion
                }
                puppet.TargetAt = stamp;
                puppet.Target = pos; puppet.TargetRot = rot; puppet.HasSnapshot = true; puppet.Active = active;
            }
        }

        void ClientEnemyLateUpdate()
        {
            float now = Time.realtimeSinceStartup;
            bool sameRoom = snapshotSeq == CurrentRoomSeq && RunActive;
            float blend = 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime);
            float worst = 0;
            foreach (Puppet puppet in puppets.Values)
            {
                if (puppet.Obj == null)
                {
                    if (!sameRoom || now < puppet.RespawnAt || (puppet.SceneKey == null && !Mutant("pairing") && StandInWaits(now))) continue;
                    if (puppet.SceneKey != null) { puppet.RespawnAt = now + 1; if (now < puppet.BindDeadline) TryBindScene(puppet); }
                    // Lost locally while the host still has it: respawn, at most every 2 s.
                    else SpawnPuppet(puppet);
                    continue;
                }
                if (!puppet.HasSnapshot || !sameRoom) continue;
                Transform t = puppet.Obj.transform;
                // Lead the last snapshot by the specimen's velocity (at most 150 ms) so fast movers do not trail behind.
                Vector3 aim = puppet.Target + puppet.Velocity * Mathf.Min(now - puppet.TargetAt, 0.15f);
                Vector3 next = (t.position - aim).sqrMagnitude > 25f ? aim : Vector3.Lerp(t.position, aim, blend);
                t.SetPositionAndRotation(next, Quaternion.Slerp(t.rotation, puppet.TargetRot, blend));
                if (puppet.Agent != null && puppet.Agent.isActiveAndEnabled) puppet.Agent.nextPosition = next;
                if (puppet.Obj.activeSelf != puppet.Active) puppet.Obj.SetActive(puppet.Active);
                if (puppet.DoorPending && puppet.Obj.activeInHierarchy) GiveLateDoor(puppet);
                if (puppet.AnimDirty)
                {
                    if (!puppet.AnimLooked)
                    {
                        puppet.AnimLooked = true;
                        puppet.Anim = puppet.Obj.GetComponentInChildren<Animator>(true);
                        puppet.Legacy = puppet.Obj.GetComponentInChildren<Animation>(true);
                        if (puppet.Anim != null || puppet.Legacy != null) AnimCapable++;
                    }
                    if (puppet.AnimState > 0) ApplyAnimator(puppet);
                    else if (puppet.AnimState < 0) ApplyLegacy(puppet);
                    puppet.AnimDirty = false;
                }
                worst = Mathf.Max(worst, Vector3.Distance(next, aim));
            }
            // Local spawns the host never confirmed are removed, so clients never keep extra specimens.
            for (int i = unpaired.Count - 1; i >= 0; i--)
            {
                Unpaired u = unpaired[i];
                if (u.Obj == null) { unpaired.RemoveAt(i); continue; }
                if (u.At == float.MaxValue && snapshotSeq >= 0) { u.At = now; u.Seq = snapshotSeq; }
                if (now - u.At > 6f && snapshotSeq >= u.Seq) { Log("Removing unconfirmed local specimen " + u.SpecId); puppetObjects.Remove(u.Obj.GetInstanceID()); RemoveLocalSpecimen(u.Obj); unpaired.RemoveAt(i); }
            }
            lastDesync = worst;
            if (now >= nextClientPrune) { nextClientPrune = now + 10; puppetObjects.RemoveWhere(DeadObject); }
        }

        public int AnimSynced, AnimCapable, LegacySynced;

        void ApplyAnimator(Puppet puppet)
        {
            Animator anim = puppet.Anim;
            if (anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null) return;
            if (anim.HasState(0, puppet.AnimState) && anim.GetCurrentAnimatorStateInfo(0).fullPathHash != puppet.AnimState)
                anim.Play(puppet.AnimState, 0, Mathf.Repeat(puppet.AnimTime, 1f));
            AnimSynced++;
        }

        void ApplyLegacy(Puppet puppet)
        {
            Animation legacy = puppet.Legacy;
            if (legacy == null || !legacy.isActiveAndEnabled) return;
            int wanted = -puppet.AnimState - 1, index = 0;
            foreach (AnimationState clip in legacy)
            {
                if (index++ != wanted) continue;
                if (!legacy.IsPlaying(clip.name)) { legacy.Play(clip.name); clip.normalizedTime = Mathf.Repeat(puppet.AnimTime, 1f); }
                AnimSynced++; LegacySynced++;
                break;
            }
        }

        int BoundPuppets { get { int bound = 0; foreach (Puppet p in puppets.Values) if (p.Obj != null) bound++; return bound; } }

        string EntityMetric()
        {
            if (Hosting) return "specs=" + hostSpecs.Count;
            int bound = BoundPuppets;
            float desync = snapshotSeq == CurrentRoomSeq ? lastDesync + 5f * Math.Abs(hostSpecCount - bound) : -1;
            return "specs=" + bound + " hostspecs=" + hostSpecCount + " unpaired=" + unpaired.Count + " desync=" + desync.ToString("0.00");
        }
    }
}
