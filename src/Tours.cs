using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpookyTogether
{
    // Scripted lab tours (-st-lab -st-tour <name>). The host drives each step; clients check what arrives. Every result is
    // an ST-CHECK line that tests/verify-lab.py turns into pass/fail. Tours: enemies, state, axe, ending, clientending.
    public sealed partial class TogetherRuntime
    {
        int tourStep;
        float tourAt;
        float tourWaitFrom = -1;   // lab: when the state tour began waiting for the team
        GameObject tourSpec;
        float tourHealthBefore;
        int hurtReceived;
        float firstSpawnSeenAt = -1;
        bool tourDone;
        readonly Dictionary<string, bool> tourFlags = new Dictionary<string, bool>();
        public static int TestHits;   // lab-only

        bool Once(string name) { if (tourFlags.ContainsKey(name)) return false; tourFlags[name] = true; return true; }

        void TourUpdate()
        {
            if (string.IsNullOrEmpty(labTour) || tourDone || !Connected || !RunActive || CurrentRoomSeq < 2) return;
            if (Closest<HSMGameplayState>() == null) return;
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (player == null || !player.ready) return;
            float now = Time.realtimeSinceStartup;
            if (tourAt == 0) tourAt = now + 4;
            switch (labTour)
            {
                case "enemies": if (Hosting) HostEnemyTour(player, now); else ClientEnemyTour(now); break;
                case "state": StateTour(player, now); break;
                case "axe": AxeTour(player, now); break;
                case "ending": if (Hosting && now >= tourAt && Once("ending")) { Log("Tour: host goes to Credits"); Closest<HSMGameRootState>().GotoKillscreen("Credits"); } break;
                case "reconnect": ReconnectTour(now); break;
                case "dollprops": DollPropsTour(now); break;
                case "cutouts": CutoutTour(now); break;
                case "allspecs": if (Hosting) HostAllSpecs(player, now); else ClientAllSpecs(now); break;
                case "elevator": ElevatorTour(now); break;
                case "stuck": StuckTour(now); break;
                case "pairing": PairingTour(player, now); break;
                case "furnace": FurnaceTour(now); break;
                case "puzzles": PuzzlesTour(now); break;
                case "chase": if (Hosting) ChaseTour(player, now); break;
                case "latelook": if (Hosting && Once("latelook")) LateLookTour(player); break;
                case "look": LookTour(); break;
                case "spec6": Spec6Tour(now); break;
                case "debug": DebugTour(now); break;
                case "takerbail": case "takerbad": case "takergood": TakerTour(player, now); break;
                case "lateending": if (Hosting && now >= tourAt && Once("ending")) { Log("Tour: host goes to Credits"); Closest<HSMGameRootState>().GotoKillscreen("Credits"); } break;
                case "loadending": LoadEndingTour(now); break;
                case "clientending": if (SelfId == 1 && now >= tourAt && Once("ending")) { Log("Tour: client asks for Credits"); Closest<HSMGameRootState>().GotoKillscreen("Credits"); } break;
            }
        }

        // The host moves the team to a third room and (Rooms.cs) sends Credits as it announces it; the host itself follows
        // once its room is ready. Clients get the ending mid-load and must still reach it.
        void LoadEndingTour(float now)
        {
            if (!Hosting || now < tourAt) return;
            HSMFloorState floor = Closest<HSMFloorState>();
            bool together = true;
            foreach (Peer peer in peers.Values) if (peer.Accepted && (peer.RoomSeq != 2 || !peer.HasPose)) together = false;
            if (CurrentRoomSeq == 2 && floor != null && together && Once("loadending.go")) { Log("Tour: moving the team on"); floor.TransferRequest(null, "End_05", true); }
            else if (CurrentRoomSeq >= 3 && Closest<HSMRoomReadyState>() != null && labEndingAt < 0 && endingSent == "Credits" && Once("loadending.host")) Closest<HSMGameRootState>().GotoKillscreen("Credits");
        }

        // Clients and host both confirm an ending by being in a killscreen state.
        void TourEndingWatch()
        {
            if (labTour != "ending" && labTour != "clientending" && labTour != "lateending" && labTour != "loadending" && !labTour.StartsWith("taker", StringComparison.Ordinal)) return;
            if (Closest<HSMKillscreenState>() != null && Once("ending.reached"))
            {
                Check("ending.reached", true, "role=" + (Hosting ? "host" : "client") + " ending=" + (lastEnding ?? "unknown"));
                Check("ending." + (lastEnding ?? "unknown"), true, "role=" + (Hosting ? "host" : "client"));
            }
        }

        // --- Enemies --------------------------------------------------------------------------------------
        void HostEnemyTour(SpookyCharacterController player, float now)
        {
            if (now < tourAt) return;
            SpecimenManager manager = Specimens;
            if (tourStep == 0)
            {
                CheckCachedRetarget();
                string id = PickSpecimen();
                if (id == null) { Check("enemy.spawned", false, "no specimen configs"); tourDone = true; return; }
                GameObject anchor = new GameObject("SpookyTogether TourAnchor");
                anchor.transform.position = player.transform.position + player.transform.forward * 5f;
                anchor.transform.rotation = player.transform.rotation;
                tourSpec = manager.StartSpecimen(id, anchor.transform, false);
                Object.Destroy(anchor);
                Check("enemy.spawned", tourSpec != null, "spec=" + id);
                if (tourSpec == null) { tourDone = true; return; }
                AddTestHitTarget(tourSpec);
                SpawnLegacyAnimated(manager, player);
                tourStep = 1; tourAt = now + 8;
            }
            else if (tourStep == 1)
            {
                if (tourSpec == null) { Check("enemy.alive", false, "specimen vanished"); tourDone = true; return; }
                // Retarget: put the specimen next to a client and ask who it should chase.
                Peer peer = null; foreach (Peer p in peers.Values) if (p.Accepted && p.HasPose && p.RoomSeq == CurrentRoomSeq) { peer = p; break; }
                Component spec = tourSpec.GetComponent(typeof(SpecimenInterface));
                if (peer != null && spec != null)
                {
                    tourSpec.transform.position = peer.ShownPosition + Vector3.right * 0.5f;
                    targetOf.Remove(spec.GetInstanceID());
                    Transform chosen = Target(spec);
                    Check("enemy.retarget", chosen != player.transform, "chose " + (chosen != null ? chosen.name : "null") + " for " + peer.Name);
                    // Hit routing: a hit by a specimen chasing a client lands on that client, not the host.
                    tourHealthBefore = player.currentHealth.currentValue;
                    player.Hurt(5f, (SpecimenInterface)spec, null, false, 0f);
                    Check("enemy.hurt.routed", Mathf.Approximately(player.currentHealth.currentValue, tourHealthBefore), "host health " + tourHealthBefore + " -> " + player.currentHealth.currentValue);
                    Check("enemy.hit.host", TestHits >= 1, "hits on the host's specimen=" + TestHits);
                }
                else Check("enemy.retarget", false, "no client in the room");
                tourStep = 2; tourAt = now + 10;
            }
            else if (tourStep == 2)
            {
                if (tourSpec != null) { AllowEndSpecimen = true; try { manager.EndSpecimen(tourSpec); } finally { AllowEndSpecimen = false; } }
                if (legacySpec != null) { AllowEndSpecimen = true; try { manager.EndSpecimen(legacySpec); } finally { AllowEndSpecimen = false; } }
                Check("enemy.removed", true, "host ended the specimen");
                tourStep = 3; tourAt = now + 15;
            }
            else if (tourStep == 3)
            {
                // Churn: several spawns and removals in a row must not leave anything behind.
                for (int i = 0; i < 3; i++)
                {
                    string id = PickSpecimen();
                    GameObject anchor = new GameObject("SpookyTogether TourAnchor");
                    anchor.transform.position = player.transform.position + Vector3.right * (3 + i * 2);
                    GameObject s = manager.StartSpecimen(id, anchor.transform, false);
                    Object.Destroy(anchor);
                    if (s != null) { AllowEndSpecimen = true; try { manager.EndSpecimen(s); } finally { AllowEndSpecimen = false; } }
                }
                tourStep = 4; tourAt = now + 8;
            }
            else if (tourStep == 4)
            {
                int managed = 0; foreach (int id in hostSpecs.Keys) if (!hostSceneKeys.ContainsKey(id)) managed++;
                Check("enemy.churn.clean", managed == manager.activeSpecimens.Count, "tracked=" + managed + " active=" + manager.activeSpecimens.Count);
                Log("Tour: host specimen sounds sent=" + SoundsSent);
                tourDone = true;
            }
        }

        GameObject legacySpec;

        // Like the game's scripted spawns (e.g. Crowd Control's chaser): a spawned specimen gets the room's door.
        void GiveRoomDoor(GameObject spec)
        {
            HSMRoomReadyState room = Closest<HSMRoomReadyState>();
            SpecimenInterface si = spec != null ? spec.GetComponent<SpecimenInterface>() : null;
            if (si == null || room == null || room.roomRuntimeData == null || room.roomRuntimeData.playerSpawn == null) return;
            try { si.StartRoom(room.roomRuntimeData.playerSpawn); } catch (System.Exception ex) { Log("Tour: StartRoom failed: " + ex.Message); }
        }

        // A second specimen whose prefab animates with the legacy Animation component, to test that path too. Prefabs are
        // inspected without spawning, then the chosen one is spawned the way the game's scripted chaser spawns do.
        void SpawnLegacyAnimated(SpecimenManager manager, SpookyCharacterController player)
        {
            AssetDataManager assets = AssetDataManager.GetInstance();
            string chosen = null;
            for (int n = 1; n < 40 && chosen == null; n++)
            {
                string id = PickSpecimen(n);
                if (id == null || id == PickSpecimen(0) || !assets.specConfigs.ContainsKey(id)) continue;
                GameObject prefab = assets.GetPrefab(assets.specConfigs[id].prefabPath);
                if (prefab != null && prefab.GetComponentInChildren<Animation>(true) != null && prefab.GetComponentInChildren<Animator>(true) == null) chosen = id;
            }
            if (chosen != null)
            {
                GameObject anchor = new GameObject("SpookyTogether TourAnchor");
                anchor.transform.position = player.transform.position + player.transform.right * 6f;
                legacySpec = manager.StartSpecimen(chosen, anchor.transform, true);
                Object.Destroy(anchor);
                GiveRoomDoor(legacySpec);
                Log("Tour: legacy-animated specimen " + chosen);
            }
            Check("enemy.legacy.spawned", legacySpec != null, legacySpec != null ? legacySpec.name : "none found");
        }

        string PickSpecimen() { return PickSpecimen(0); }

        // The n-th rechasable specimen (wrapping), so soak runs cycle through every chaser type.
        string PickSpecimen(int n)
        {
            SpecimenManager manager = Specimens;
            AssetDataManager assets = AssetDataManager.GetInstance();
            if (manager == null || assets == null || assets.specConfigs == null) return null;
            List<string> ids = new List<string>();
            if (manager.allRechasableSpecimen != null) foreach (string id in manager.allRechasableSpecimen) if (assets.specConfigs.ContainsKey(id)) ids.Add(id);
            if (ids.Count == 0) foreach (string id in assets.specConfigs.Keys) ids.Add(id);
            return ids.Count == 0 ? null : ids[n % ids.Count];
        }

        void ClientEnemyTour(float now)
        {
            int bound = BoundPuppets;
            if (firstSpawnSeenAt < 0 && puppets.Count > 0) firstSpawnSeenAt = now;
            if (firstSpawnSeenAt < 0) return;
            if (now - firstSpawnSeenAt > 5 && Once("enemy.bound"))
            {
                Check("enemy.bound", bound >= 1 && bound == hostSpecCount, "bound=" + bound + " host=" + hostSpecCount);
                // C1 swings at its puppet: only the host's copy may take the hit.
                if (SelfId == 1)
                    foreach (Puppet p in puppets.Values)
                        if (p.Obj != null) { SendMessageOnHit target = AddTestHitTarget(p.Obj); target.TakeHit(); hitSent = true; break; }
            }
            if (hitSent && now - firstSpawnSeenAt > 6 && Once("enemy.hit.local")) Check("enemy.hit.local.skipped", TestHits == 0, "local hits=" + TestHits);
            if (now - firstSpawnSeenAt > 7 && Once("enemy.tracking")) Check("enemy.tracking", lastDesync < 1.5f, "desync=" + lastDesync.ToString("0.00"));
            if (now - firstSpawnSeenAt > 45 && Once("enemy.cleanup"))
            {
                Check("enemy.cleanup", bound == hostSpecCount && unpaired.Count == 0, "bound=" + bound + " host=" + hostSpecCount + " unpaired=" + unpaired.Count);
                if (SelfId == 1) Check("enemy.hurt.received", hurtReceived > 0, "hits=" + hurtReceived);
                // Specimens without any animation component have nothing to mirror.
                Check("enemy.anim", AnimCapable == 0 || AnimSynced > 0, "animated puppets=" + AnimCapable + " states applied=" + AnimSynced);
                Check("enemy.anim.legacy", LegacySynced > 0, "legacy clips applied=" + LegacySynced);
                Log("Tour: sounds played from host=" + SoundsPlayed + " local one-shots muted=" + SoundsMuted);
                tourDone = true;
            }
        }

        // --- Spawn pairing ---------------------------------------------------------------------------------
        // A client's room script spawning its own copy around the time the host's spawn arrives. The host spawns the same
        // chaser twice; the client spawns its own copy before the first one has a stand-in (it must be adopted) and after
        // the second one has one (it must replace it). Either way exactly one copy per host specimen remains.
        int pairNetA = -1, pairNetB = -1;
        GameObject pairOwnA, pairOwnB, pairStandIn;
        string pairSpec;

        float pairTogetherAt = -1;
        void PairingTour(SpookyCharacterController player, float now)
        {
            if (now < tourAt) return;
            SpecimenManager manager = Specimens;
            if (manager == null) return;
            if (Hosting)
            {
                if (tourStep == 0)
                {
                    if (tourWaitFrom < 0) tourWaitFrom = now;
                    bool together = true;
                    foreach (Peer peer in peers.Values) if (peer.Accepted && peer.RoomSeq != CurrentRoomSeq) together = false;
                    if (!together && now - tourWaitFrom < 40) return;
                    // The last client to arrive starts its own tour 4 s after its room is ready: give it that time.
                    if (pairTogetherAt < 0) pairTogetherAt = now;
                    if (now - pairTogetherAt < 5) return;
                }
                if (tourStep == 0 || tourStep == 1)
                {
                    string id = PickSpecimen();
                    GameObject anchor = new GameObject("SpookyTogether TourAnchor");
                    anchor.transform.position = player.transform.position + player.transform.forward * (6f + tourStep * 3f);
                    GameObject s = manager.StartSpecimen(id, anchor.transform, false);
                    Object.Destroy(anchor);
                    Log("Tour: host spawned " + id + " for pairing (" + (tourStep == 0 ? "before" : "after") + " the stand-in)");
                    tourStep++; tourAt = now + 15;
                    return;
                }
                if (tourStep == 2)
                {
                    foreach (GameObject s in manager.activeSpecimens.ToArray()) { AllowEndSpecimen = true; try { manager.EndSpecimen(s); } finally { AllowEndSpecimen = false; } }
                    Check("pairing.host", true, "host spawned and ended two chasers");
                    tourDone = true;
                }
                return;
            }
            // Client: copies of host specimens, in arrival order.
            foreach (Puppet p in puppets.Values)
            {
                if (p.SceneKey != null) continue;
                if (pairNetA < 0 && p.Obj == null)
                {
                    pairNetA = p.NetId; pairSpec = p.SpecId;
                    pairOwnA = SpawnOwnCopy(manager, p.SpecId, player);
                    Log("Tour: own copy of " + p.SpecId + " spawned before any stand-in (host specimen " + p.NetId + ")");
                    tourAt = now + 0.2f;
                }
                else if (pairNetA >= 0 && p.NetId != pairNetA && pairNetB < 0 && p.Obj != null && p.StandIn)
                {
                    pairNetB = p.NetId; pairStandIn = p.Obj;
                    pairOwnB = SpawnOwnCopy(manager, p.SpecId, player);
                    Log("Tour: own copy of " + p.SpecId + " spawned after the stand-in (host specimen " + p.NetId + ")");
                    firstSpawnSeenAt = now;
                }
            }
            if (pairNetB >= 0 && now - firstSpawnSeenAt > 8 && Once("pairing.checks"))
            {
                Puppet a, b;
                bool adopted = puppets.TryGetValue(pairNetA, out a) && pairOwnA != null && a.Obj == pairOwnA;
                bool replaced = puppets.TryGetValue(pairNetB, out b) && pairOwnB != null && b.Obj == pairOwnB && pairStandIn == null;
                int copies = 0, hostCopies = 0;
                foreach (GameObject s in manager.activeSpecimens)
                {
                    SpecimenInterface si = s != null ? s.GetComponent<SpecimenInterface>() : null;
                    string sid = null; try { sid = si != null ? si.spawnedFromSpecId : null; } catch (Exception) { }   // a destroyed specimen's getter throws
                    if (sid == pairSpec) copies++;
                }
                foreach (Puppet p in puppets.Values) if (p.SceneKey == null && p.SpecId == pairSpec) hostCopies++;
                Check("pairing.adopted", adopted, "own copy is the host's specimen " + pairNetA + ": " + adopted + " (adopted=" + OwnSpawnsAdopted + ")");
                Check("pairing.replaced", replaced, "own copy replaced the stand-in for " + pairNetB + ": " + replaced + " (replaced=" + StandInsReplaced + ")");
                Check("pairing.single", copies == hostCopies && hostCopies == 2, "copies of " + pairSpec + "=" + copies + " host specimens=" + hostCopies);
                tourDone = true;
            }
        }

        // What a room script does: SpecimenManager.StartSpecimen outside the mod's own spawns.
        static GameObject SpawnOwnCopy(SpecimenManager manager, string specId, SpookyCharacterController player)
        {
            GameObject anchor = new GameObject("SpookyTogether TourAnchor");
            anchor.transform.position = player.transform.position + player.transform.forward * 6f;
            try { return manager.StartSpecimen(specId, anchor.transform, false); }
            finally { Object.Destroy(anchor); }
        }

        // --- Shared state ----------------------------------------------------------------------------------
        void StateTour(SpookyCharacterController player, float now)
        {
            PersistenceData data = CurrentData;
            if (data == null || now < tourAt) return;
            if (tourStep == 0)
            {
                // Everyone in this room first (up to 40 s): on a loaded PC a teammate can arrive seconds late, and the
                // check then measured load time, not sync.
                if (tourWaitFrom < 0) tourWaitFrom = now;
                bool together = true;
                foreach (Peer peer in peers.Values) if (peer.Accepted && peer.RoomSeq != CurrentRoomSeq) together = false;
                if (!together && now - tourWaitFrom < 40) return;
                // Every player writes its own key and item; everyone must end up with all of them.
                data.SetKey("st_key_" + SelfId, 10 + SelfId);
                data.AddToInventory(TourItem(SelfId));
                tourStep = 1; tourAt = now + 8;
            }
            else if (tourStep == 1)
            {
                int expected = 1 + AcceptedCount + (Hosting ? 0 : 0);
                int keys = 0, items = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (data.GetKey("st_key_" + i) == 10 + i) keys++;
                    if (TourItem(i) != null && data.HasInventoryItem(TourItem(i))) items++;
                }
                int players = Hosting ? 1 + AcceptedCount : 1 + peers.Count;
                Check("state.keys", keys == players, "have " + keys + "/" + players);
                Check("state.items", items == players, "have " + items + "/" + players);
                // The host's removal next must not reach a slower teammate before it has counted.
                tourStep = 2; tourAt = now + (Hosting ? 12 : 0);
            }
            else if (tourStep == 2)
            {
                if (Hosting) { data.RemoveFromInventory(TourItem(0)); data.SetKey("violence", 21); }
                tourStep = 3; tourAt = now + 6; tourWaitFrom = now;
            }
            else if (tourStep == 3)
            {
                // A client waits for the host's change itself (up to 20 s), not a fixed time that a slow PC outruns.
                bool arrived = !data.HasInventoryItem(TourItem(0)) && data.GetKey("violence") == 21;
                if (!Hosting && !arrived && now - tourWaitFrom < 20) return;
                Check("state.remove", !data.HasInventoryItem(TourItem(0)), "host removed " + TourItem(0));
                Check("state.violence", data.GetKey("violence") == 21, "violence=" + data.GetKey("violence"));
                Check("props.refresh", PropRefreshes > 0 && PropRefreshFailures == 0, "refreshes=" + PropRefreshes + " failures=" + PropRefreshFailures);
                tourDone = true;
            }
        }

        // --- Axe --------------------------------------------------------------------------------------------
        void AxeTour(SpookyCharacterController player, float now)
        {
            if (now < tourAt) return;
            if (tourStep == 0)
            {
                // A shared hit target with the same scene path on every machine.
                Scene();
                if (Hosting) { GrantWeapon("axe"); Check("axe.granted", LocalWeapon() == "axe", "weapon=" + LocalWeapon()); }
                tourStep = 1; tourAt = now + 6;
            }
            else if (tourStep == 1)
            {
                if (SelfId == 1)
                {
                    GameObject target = GameObject.Find("SpookyTogetherHitTarget");
                    SendMessageOnHit hit = target != null ? target.GetComponent<SendMessageOnHit>() : null;
                    if (hit != null) hit.TakeHit();
                    AfterSwing();
                }
                tourStep = 2; tourAt = now + 6;
            }
            else if (tourStep == 2)
            {
                Check("axe.hit.relayed", TestHits == 1, "hits=" + TestHits);
                if (!Hosting) Check("axe.host.armed", PeerWeapon(0) == "axe", "host weapon seen=" + PeerWeapon(0));
                if (SelfId != 1) { Peer swinger; Check("axe.swing.seen", peers.TryGetValue(1, out swinger) && Time.realtimeSinceStartup - swinger.SwingAt < 15, "swing from C1"); }
                tourDone = true;
            }
        }

        // Real item ids (the HUD rejects unknown ones), one per player slot.
        static string TourItem(int slot)
        {
            AssetDataManager assets = AssetDataManager.GetInstance();
            if (assets == null || assets.allInventoryItems == null || assets.allInventoryItems.Count == 0) return null;
            List<string> ids = new List<string>(assets.allInventoryItems.Keys); ids.Sort(string.CompareOrdinal);
            return ids[slot % ids.Count];
        }

        string PeerWeapon(int id) { Peer p; return peers.TryGetValue(id, out p) ? p.Weapon : null; }

        void Scene()
        {
            if (GameObject.Find("SpookyTogetherHitTarget") != null) return;
            GameObject go = new GameObject("SpookyTogetherHitTarget");
            SendMessageOnHit hit = go.AddComponent<SendMessageOnHit>();
            hit.onHit = new UnityEngine.Events.UnityEvent();
            hit.onHit.AddListener(() => TestHits++);
        }

        bool hitSent;

        // --- Every specimen type, one at a time, in its own campaign (-st-specs base|em|kh|doll) -------------------------
        List<string> specQueue;
        GameObject sweepSpec;
        string sweepId;
        int sweepErrorsAt;
        public static string SpecFilter = "base";   // lab-only
        int sweepRoomSeq = -1;

        static string SpecHome(string id)
        {
            if (id.StartsWith("EM", StringComparison.Ordinal) || id.StartsWith("Endless", StringComparison.Ordinal)) return "em";
            if (id.StartsWith("KH", StringComparison.Ordinal)) return "kh";
            if (id.IndexOf("Doll", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("Hooked", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("Woormy", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("Kiragen", StringComparison.OrdinalIgnoreCase) >= 0) return "doll";
            return "base";
        }

        void HostAllSpecs(SpookyCharacterController player, float now)
        {
            SpecimenManager manager = Specimens;
            AssetDataManager assets = AssetDataManager.GetInstance();
            if (manager == null || assets == null || now < tourAt) return;
            // Same room every run (a fixed corridor), so a multiplayer sweep and a host-only control run compare like for like.
            if (SpecFilter == "base" && sweepRoomSeq < 0)
            {
                if (Once("sweep.goto")) { HSMFloorState floor = Closest<HSMFloorState>(); if (floor != null) floor.TransferRequest(null, "Hall_01", true); sweepRoomSeq = CurrentRoomSeq; }
                return;
            }
            if (SpecFilter == "base" && CurrentRoomSeq <= sweepRoomSeq) return;
            if (specQueue == null)
            {
                specQueue = new List<string>();
                // Not chasers: Specimen 1 is the room cut-out (synced on its own), Specimen 9 is the per-player AFK Taker,
                // and the Secret ones are hidden specials; the game never spawns these through StartSpecimen.
                foreach (string id in assets.specConfigs.Keys)
                    if (SpecHome(id) == SpecFilter && id != "SpecimenOne" && !id.StartsWith("SpecimenNine", StringComparison.Ordinal) && !id.StartsWith("Secret", StringComparison.Ordinal)) specQueue.Add(id);
                specQueue.Sort(string.CompareOrdinal);
                Log("Tour: " + specQueue.Count + " specimen types for " + SpecFilter + ": " + string.Join(", ", specQueue.ToArray()));
                Check("specs.listed", specQueue.Count > 0, SpecFilter + " count=" + specQueue.Count);
            }
            if (sweepId != null)
            {
                // Judge the one that just ran, then remove it the game's way.
                Check("spec." + sweepId, sweepSpec != null && unexpectedErrorCount == sweepErrorsAt, "alive=" + (sweepSpec != null) + " errors while active=" + (unexpectedErrorCount - sweepErrorsAt));
                if (sweepSpec != null) { AllowEndSpecimen = true; try { manager.EndSpecimen(sweepSpec); } catch (Exception ex) { Log("End failed: " + ex.Message); } finally { AllowEndSpecimen = false; } }
                sweepSpec = null; sweepId = null; tourAt = now + 4; return;
            }
            if (specQueue.Count == 0) { if (Once("specs.done")) Check("specs.done", true, "every " + SpecFilter + " specimen type was spawned and judged"); return; }
            sweepId = specQueue[0]; specQueue.RemoveAt(0);
            sweepErrorsAt = unexpectedErrorCount;
            HSMRoomReadyState room = Closest<HSMRoomReadyState>();
            Transform at = room != null && room.roomRuntimeData != null && room.roomRuntimeData.playerSpawn != null ? room.roomRuntimeData.playerSpawn.transform : player.transform;
            try
            {
                // As Crowd Control's chaser spawn: StartSpecimen(rechase) then StartRoom(player door).
                sweepSpec = manager.StartSpecimen(sweepId, at, true);
                if (sweepSpec != null) GiveRoomDoor(sweepSpec);
            }
            catch (Exception ex) { Log("Spawn of " + sweepId + " threw: " + ex.Message); }
            Log("Tour: sweeping " + sweepId + " -> " + (sweepSpec != null));
            tourAt = now + 12;
        }

        readonly Dictionary<int, float> clientSweepSeen = new Dictionary<int, float>();
        readonly Dictionary<int, int> clientSweepErrors = new Dictionary<int, int>();

        void ClientAllSpecs(float now)
        {
            foreach (Puppet p in puppets.Values)
            {
                if (p.SpecId == null) continue;
                if (!clientSweepSeen.ContainsKey(p.NetId)) { clientSweepSeen[p.NetId] = now; clientSweepErrors[p.NetId] = unexpectedErrorCount; continue; }
                if (now - clientSweepSeen[p.NetId] > 8 && Once("cspec." + p.NetId))
                {
                    float error = p.Obj != null && p.HasSnapshot ? Vector3.Distance(p.Obj.transform.position, p.Target) : -1;
                    Check("cspec." + p.SpecId, p.Obj != null && error >= 0 && error < 3.5f && unexpectedErrorCount == clientSweepErrors[p.NetId],
                        "bound=" + (p.Obj != null) + " offset=" + error.ToString("0.00") + " errors=" + (unexpectedErrorCount - clientSweepErrors[p.NetId]));
                }
            }
        }

        // --- A player busy at a machine (locked in place) while a teammate's door moves the team on -----------------------
        int stuckSeq = -1;
        void StuckTour(float now)
        {
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (SelfId == 1 && Once("stuck.lock"))
            {
                // What an arcade machine, terminal or puzzle does when you start using it.
                player.AddMoveLock(); player.AddLookLock(); player.AddInteractionLock();
                stuckSeq = CurrentRoomSeq;
                Log("Tour: C1 is busy at a machine in room seq " + stuckSeq);
                return;
            }
            if (SelfId == 1 && stuckSeq >= 0 && CurrentRoomSeq > stuckSeq && Closest<HSMGameplayState>() != null && Once("stuck.check"))
                Check("locks.released", player.CanMove(), "can move after a teammate's door: " + player.CanMove() + " released=" + StaleLocksReleased);
            if (SelfId == 2 && now >= tourAt + 8 && Once("stuck.door")) { Log("Tour: C2 opens a door while C1 is busy"); LabUseDoor(); }
        }

        // --- A client whose floor state drifted (another floor's config pushed) must still paint rooms like the host -------
        void LookTour()
        {
            if (SelfId != 1 || !Once("look.drift")) return;
            HSMFloorState floor = Closest<HSMFloorState>();
            HSMMansionState mansion = Closest<HSMMansionState>();
            if (floor == null || mansion == null || mansion.mansion.floors == null || mansion.mansion.floors.Length < 2) { Log("Tour: no other floor to drift to"); return; }
            FloorConfig other = mansion.mansion.floors[mansion.mansion.floors.Length - 1];
            floor.AddFloor(other);
            Log("Tour: C1 drifted its floor state to " + other.name);
        }

        // --- Specimen 6 whose chase ends at the next room; then every client's screen must not be black ------------------
        GameObject spec6;
        int spec6Seq = -1;
        bool brightnessPending;

        void Spec6Tour(float now)
        {
            if (now < tourAt) return;
            if (Hosting && spec6Seq < 0)
            {
                AssetDataManager assets = AssetDataManager.GetInstance();
                string id = null;
                foreach (string key in assets.specConfigs.Keys) if (key.IndexOf("Six", StringComparison.OrdinalIgnoreCase) >= 0 && !key.StartsWith("EM", StringComparison.Ordinal)) id = key;
                if (id == null) { Check("spec6.found", false, "no Specimen 6 config"); tourDone = true; return; }
                HSMRoomReadyState room = Closest<HSMRoomReadyState>();
                spec6 = Specimens.StartSpecimen(id, room.roomRuntimeData.playerSpawn.transform, true);
                if (spec6 != null) { GiveRoomDoor(spec6); spec6.GetComponent<SpecimenInterface>().hp = 1; }
                spec6Seq = CurrentRoomSeq;
                Log("Tour: Specimen 6 (" + id + ") chases with one room left, seq " + spec6Seq);
                tourAt = now + 15;
                return;
            }
            if (Hosting && spec6Seq == CurrentRoomSeq && Once("spec6.door")) { Log("Tour: moving on; Specimen 6 ends"); LabUseDoor(); return; }
            if (!Hosting && Once("spec6.control")) { brightnessPending = true; StartCoroutine(MeasureScreen("screen.control")); }
            if (!Hosting && spec6Seq < 0 && puppets.Count > 0) spec6Seq = CurrentRoomSeq;
            if (!Hosting && spec6Seq >= 0 && CurrentRoomSeq > spec6Seq && Closest<HSMGameplayState>() != null && !brightnessPending && Once("spec6.shot"))
            {
                brightnessPending = true;
                StartCoroutine(MeasureScreen("spec6.screen"));
            }
        }

        // Average brightness of a real frame from the game camera; a black screen scores ~0.
        System.Collections.IEnumerator MeasureScreen(string check)
        {
            yield return new WaitForSeconds(4f);
            yield return new WaitForEndOfFrame();
            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
            float sum = 0; int n = 0;
            for (int y = shot.height / 4; y < shot.height * 3 / 4; y += 8)
                for (int x = shot.width / 4; x < shot.width * 3 / 4; x += 8) { Color c = shot.GetPixel(x, y); sum += c.r * 0.3f + c.g * 0.59f + c.b * 0.11f; n++; }
            Object.Destroy(shot);
            float brightness = n > 0 ? sum / n : 0;
            Check(check, brightness > 0.03f, "screen brightness=" + brightness.ToString("0.000") + " room seq " + CurrentRoomSeq);
            brightnessPending = false;
        }

        // --- F6 debug menu: a client's room jump moves the team; a host spawn binds; menu locks balance; report saves -------
        int debugStep, debugErrorsAt;
        void DebugTour(float now)
        {
            if (now < tourAt) return;
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            switch (debugStep)
            {
                case 0:
                    // Everyone opens and closes the menu; the player must be free to move afterwards.
                    if (Hosting) { SetDebug(true); SetDebug(false); }
                    Check("debug.locks", player.CanMove(), "can move after opening and closing F6");
                    // The host opens the menu and steps through all five tabs (each one drawn for a second).
                    if (Hosting) SetDebug(true);
                    debugTab = 0; debugErrorsAt = unexpectedErrorCount;
                    debugStep = 10; tourAt = now + 1;
                    break;
                case 10: case 11: case 12: case 13:
                    debugTab = debugStep - 9; debugStep++; tourAt = now + 1;
                    break;
                case 14:
                    SetDebug(false);
                    Check("debug.tabs", unexpectedErrorCount == debugErrorsAt && player.CanMove(), "errors while drawing the tabs=" + (unexpectedErrorCount - debugErrorsAt) + " can move=" + player.CanMove());
                    debugStep = 1; tourAt = now + 2;
                    break;
                case 1:
                    if (Hosting) { Log("Tour: host uses F6 to go to Hall_05"); DebugTravel("Hall_05", true); }
                    debugStep = 2; tourAt = now + 14;
                    break;
                case 2:
                    Check("debug.travel", CurrentRoomId() == "Hall_05", "room now " + CurrentRoomId());
                    if (Hosting) DebugSpawn(PickSpecimen(0));
                    debugStep = 3; tourAt = now + 8;
                    break;
                case 3:
                    if (!Hosting) Check("debug.spawn.bound", BoundPuppets >= 1, "host-driven copies " + BoundPuppets);
                    if (Hosting) { SaveDebugReport(); Check("debug.report", debugStatus.StartsWith("Saved", StringComparison.Ordinal), debugStatus); }
                    tourDone = true;
                    break;
            }
        }

        System.Collections.IEnumerator DebugShot()
        {
            yield return new WaitForSeconds(1f);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Root, "debug-menu.png"));
        }

        // --- Elevators: C1 presses the button; every copy must start the same ride ----------------------------------------
        void ElevatorTour(float now)
        {
            if (now < tourAt) return;
            tourAt = now + 2;
            ElevatorButtonControl button = Object.FindObjectOfType<ElevatorButtonControl>();
            KHElevatorButton kh = Object.FindObjectOfType<KHElevatorButton>();
            if (button == null && kh == null)
            {
                // The base mansion's floor-change room comes late in a floor; go there the way a floor end does.
                if (Hosting && Once("elevator.goto")) { Log("Tour: taking the team to Elevator_01"); HSMFloorState floor = Closest<HSMFloorState>(); if (floor != null) floor.TransferRequest(null, "Elevator_01", true); }
                return;
            }
            if (SelfId == 1 && Once("elevator.press")) { Log("Tour: pressing the elevator button"); if (button != null) button.OnInteract(); else kh.OnInteract(); return; }
            if (SelfId != 1 && ElevatorRides > 0 && Once("elevator.ride")) Check("elevator.ride", true, "rides started by a teammate's press=" + ElevatorRides);
        }

        // The first room with a Specimen 1 cut-out: C1 walks into it (Deploy, as a trigger or present box would); every
        // client then checks side and picture against the host's (ST-CHECK cutout.side / cutout.look in Cutouts.cs).
        void CutoutTour(float now)
        {
            if (now < tourAt) return;
            tourAt = now + 2;
            List<SpecimenOneController> cutouts = new List<SpecimenOneController>(Object.FindObjectsOfType<SpecimenOneController>());
            if (cutouts.Count == 0) return;
            if (Once("cutout.found")) Log("Tour: room seq " + CurrentRoomSeq + " has " + cutouts.Count + " cut-out(s)");
            cutouts.Sort((a, b) => string.CompareOrdinal(ObjectKey(a), ObjectKey(b)));
            if (SelfId == 1 && Once("cutout.trigger")) { Log("Tour: triggering " + cutouts[0].name); cutouts[0].Deploy(); }
        }

        // Dollhouse: the host flips the piano and furnace keys of props that exist in the room; clients must refresh them live.
        void DollPropsTour(float now)
        {
            PersistenceData data = CurrentData;
            if (data == null || now < tourAt) return;
            tourAt = now + 12;
            if (Hosting)
            {
                foreach (DollhousePianoControl piano in Object.FindObjectsOfType<DollhousePianoControl>())
                {
                    string key = Field(typeof(DollhousePianoControl), "puzzlePersistenceStateKey").GetValue(piano) as string;
                    if (key != null) { data.SetKey(key, 1); Log("Tour: piano key " + key + " = 1"); }
                }
                foreach (DollhouseFurnaceControl furnace in Object.FindObjectsOfType<DollhouseFurnaceControl>())
                {
                    string key = Field(typeof(DollhouseFurnaceControl), "furnaceDoorKey").GetValue(furnace) as string;
                    if (key != null) { data.SetKey(key, data.GetKey(key) == 1 ? 0 : 1); Log("Tour: furnace key " + key + " toggled"); }
                }
                return;
            }
            if (PropsTouched > 0 && Once("props.real")) Check("props.real", PropRefreshFailures == 0, "props refreshed=" + PropsTouched + " failures=" + PropRefreshFailures);
        }
        // Specimen 8 keeps the player in a variable before steering its NavMesh agent, so its agent destination shows
        // whom it really chases. The host walks away from the team, Specimen 8 spawns next to the client farthest from
        // the host, and its destination must settle on that client, not on the host.
        GameObject chaseSpec;
        void ChaseTour(SpookyCharacterController player, float now)
        {
            if (now < tourAt) return;
            SpecimenManager manager = Specimens;
            if (manager == null) return;
            if (tourStep == 0)
            {
                if (tourWaitFrom < 0) tourWaitFrom = now;
                bool together = true;
                foreach (Peer peer in peers.Values) if (peer.Accepted && (peer.RoomSeq != CurrentRoomSeq || !peer.HasPose)) together = false;
                if (!together && now - tourWaitFrom < 40) return;
                CharacterController body = player.GetComponent<CharacterController>();
                if (body != null) { body.Move(player.transform.forward * 8f); if (FarthestPeerDistance(player) < 4f) body.Move(-player.transform.forward * 16f); }
                tourStep = 1; tourAt = now + 3;
                return;
            }
            if (tourStep == 1)
            {
                Peer target = null; float far = 0;
                foreach (Peer peer in peers.Values)
                {
                    if (!peer.Accepted || !peer.HasPose || peer.RoomSeq != CurrentRoomSeq) continue;
                    float d = Vector3.Distance(peer.ShownPosition, player.transform.position);
                    if (d > far) { far = d; target = peer; }
                }
                if ((target == null || far < 4f) && ++chaseMoves < 6)
                {
                    // Walls can stop the move: try another direction until a client is far enough away.
                    CharacterController body = player.GetComponent<CharacterController>();
                    if (body != null) body.Move(Quaternion.Euler(0, 60f * chaseMoves, 0) * player.transform.forward * 8f);
                    tourAt = now + 2;
                    return;
                }
                if (target == null || far < 4f) { Check("chase.target", false, "no client 4 m from the host (farthest " + far.ToString("0.0") + " m)"); tourDone = true; return; }
                Vector3 at = target.ShownPosition + (target.ShownPosition - player.transform.position).normalized * 1.5f;
                UnityEngine.AI.NavMeshHit hit;
                if (UnityEngine.AI.NavMesh.SamplePosition(at, out hit, 3f, UnityEngine.AI.NavMesh.AllAreas)) at = hit.position;
                GameObject anchor = new GameObject("SpookyTogether TourAnchor");
                anchor.transform.position = at;
                try { chaseSpec = manager.StartSpecimen("SpecimenEight", anchor.transform, false); }
                finally { Object.Destroy(anchor); }
                Log("Tour: Specimen 8 spawned " + Vector3.Distance(at, target.ShownPosition).ToString("0.0") + " m from " + target.Name + ", " + Vector3.Distance(at, player.transform.position).ToString("0.0") + " m from the host");
                chaseTarget = target.Id;
                tourStep = 2; tourAt = now + 4;
                return;
            }
            if (tourStep == 2)
            {
                Peer target; peers.TryGetValue(chaseTarget, out target);
                UnityEngine.AI.NavMeshAgent agent = chaseSpec != null ? chaseSpec.GetComponent<UnityEngine.AI.NavMeshAgent>() : null;
                if (agent == null || target == null) { Check("chase.target", false, "Specimen 8 or its client is gone (spec=" + (chaseSpec != null) + ")"); }
                else
                {
                    float toClient = Vector3.Distance(agent.destination, target.ShownPosition), toHost = Vector3.Distance(agent.destination, player.transform.position);
                    Check("chase.target", toClient < 2.5f && toHost > toClient + 2f, "destination " + toClient.ToString("0.0") + " m from " + target.Name + ", " + toHost.ToString("0.0") + " m from the host");
                }
                if (chaseSpec != null) { AllowEndSpecimen = true; try { manager.EndSpecimen(chaseSpec); } finally { AllowEndSpecimen = false; } }
                tourDone = true;
            }
        }
        int chaseTarget = -1, chaseMoves;

        // EMKHMonster5 re-skins the room with materials that load with its prefab. The host spawns it while C3 is still
        // held out of the run (Rooms.cs), so C3 paints the room before its own copy exists; its walls must still end up
        // with the host's look (room.look@c3).
        void LateLookTour(SpookyCharacterController player)
        {
            SpecimenManager manager = Specimens;
            if (manager == null) return;
            GameObject anchor = new GameObject("SpookyTogether TourAnchor");
            anchor.transform.position = player.transform.position + player.transform.right * 6f;
            GameObject spec;
            try { spec = manager.StartSpecimen("EMKHMonster5", anchor.transform, true); }
            finally { Object.Destroy(anchor); }
            GiveRoomDoor(spec);
            Log("Tour: EMKHMonster5 spawned to re-skin the room (" + (spec != null) + ")");
        }

        float FarthestPeerDistance(SpookyCharacterController player)
        {
            float far = 0;
            foreach (Peer peer in peers.Values) if (peer.Accepted && peer.HasPose) far = Mathf.Max(far, Vector3.Distance(peer.ShownPosition, player.transform.position));
            return far;
        }

        // A puzzle segment whose event adds 1 to a key. The host meets its condition and runs it itself; every other
        // player's copy is met by the relayed key and replayed by PropRefresh. The key must end at 1 everywhere.
        PuzzleStateExister tourPuzzle;
        void PuzzlesTour(float now)
        {
            PersistenceData data = CurrentData;
            if (data == null || now < tourAt) return;
            if (tourStep == 0)
            {
                GameObject go = new GameObject("SpookyTogether TestPuzzle");
                go.SetActive(false);
                PuzzleStateExister puzzle = go.AddComponent<PuzzleStateExister>();
                puzzle.runOnRoomReady = false; puzzle.persistenceKey = "st_puzzle_count";
                PuzzleStateExister.PuzzleSegment segment = new PuzzleStateExister.PuzzleSegment
                {
                    condition = new PersistentValueConditional { key = "st_puzzle_gate", operation = PersistentValueConditional.PersistentOperation.EQUALS, targetValue = 1, item = PersistentValueConditional.ItemCondition.NOP },
                    onConditionEvents = new UnityEngine.Events.UnityEvent()
                };
                segment.onConditionEvents.AddListener(() => puzzle.AddKey(1));
                puzzle.segments = new[] { segment };
                go.SetActive(true);
                tourPuzzle = puzzle;
                tourStep = 1; tourAt = now + 8;
            }
            else if (tourStep == 1)
            {
                if (Hosting) { data.SetKey("st_puzzle_gate", 1); tourPuzzle.RunConditions(); Log("Tour: host met the test puzzle and ran it"); }
                tourStep = 2; tourAt = now + 10;
            }
            else if (tourStep == 2)
            {
                Check("puzzle.once", data.GetKey("st_puzzle_count") == 1, "count=" + data.GetKey("st_puzzle_count") + " replays here=" + PuzzleReplays);
                if (tourPuzzle != null) Object.Destroy(tourPuzzle.gameObject);
                tourDone = true;
            }
        }

        // Two clients and the host take the furnace's key in the same moment: exactly one key may result, everywhere.
        // The host first takes the team through the Dollhouse rooms (the F6 room jump) until one has the furnace, then
        // opens it with the key ready and sets a "go" key: the host takes the key at once, and each client (but C3) the
        // moment that key reaches it, which is as close together as the network allows. A lab player standing on a door
        // trigger can move the team on meanwhile; the host then goes back and starts over.
        DollhouseFurnaceControl tourFurnace;
        List<string> furnaceRooms;
        int furnaceRoomIndex, furnaceTries;
        string furnaceDoor, furnacePresence, furnaceItem, furnaceRoom, furnaceSearchRoom;
        float furnaceArrivedAt = -1, furnaceTravelAt;

        void FurnaceTour(float now)
        {
            PersistenceData data = CurrentData;
            if (data == null || now < tourAt) return;
            if (tourFurnace == null && tourStep < 2) tourFurnace = Object.FindObjectOfType<DollhouseFurnaceControl>();
            if (Hosting && tourFurnace == null && tourStep < 2) { FurnaceSearch(now); return; }
            if (tourFurnace == null && furnaceItem == null) return;   // a client waits for the host to bring it there
            if (furnaceItem == null)
            {
                furnaceDoor = Field(typeof(DollhouseFurnaceControl), "furnaceDoorKey").GetValue(tourFurnace) as string;
                furnacePresence = Field(typeof(DollhouseFurnaceControl), "keyPresenceKey").GetValue(tourFurnace) as string;
                furnaceItem = Field(typeof(DollhouseFurnaceControl), "keyInventoryId").GetValue(tourFurnace) as string;
            }
            if (Hosting && furnaceRoom == null) furnaceRoom = CurrentRoomId();
            bool go = data.GetKey("st_furnace_go") == 1;
            if (Hosting && tourStep == 0)
            {
                if (tourWaitFrom < 0) tourWaitFrom = now;
                bool together = true;
                foreach (Peer peer in peers.Values) if (peer.Accepted && peer.RoomSeq != CurrentRoomSeq) together = false;
                if (!together && now - tourWaitFrom < 40) return;
                data.SetKey("st_furnace_go", 0); data.SetKey(furnaceDoor, 1); data.SetKey(furnacePresence, 2);
                Log("Tour: furnace open with the key ready (" + furnaceItem + ") in " + CurrentRoomId());
                tourStep = 1; tourAt = now + 8;
            }
            else if ((Hosting && tourStep == 1) || (!Hosting && tourStep < 2 && go && tourFurnace != null))
            {
                if (Hosting)
                {
                    Log("Tour: before the take, door=" + data.GetKey(furnaceDoor) + " key=" + data.GetKey(furnacePresence));
                    data.SetKey("st_furnace_go", 1);
                }
                if (SelfId != 3)
                {
                    Log("Tour: taking the furnace key");
                    typeof(DollhouseFurnaceControl).GetMethod("TakeKey", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(tourFurnace, null);
                }
                tourStep = 2; tourAt = now + 4;
            }
            else if (tourStep == 2)
            {
                int copies = 0; if (data.inventory != null) foreach (string i in data.inventory) if (i == furnaceItem) copies++;
                Check("furnace.onekey", copies == 1, furnaceItem + " copies=" + copies + " refused=" + FurnaceRefused + " forwarded=" + FurnaceForwarded);
                Check("furnace.state", data.GetKey(furnacePresence) == 3 && data.GetKey(furnaceDoor) == 1, "key presence=" + data.GetKey(furnacePresence) + " door=" + data.GetKey(furnaceDoor));
                tourDone = true;
            }
        }

        void FurnaceSearch(float now)
        {
            if (furnaceRoom != null)
            {
                if (++furnaceTries > 4) { Check("furnace.found", false, "the team kept leaving " + furnaceRoom); tourDone = true; return; }
                Log("Tour: the team left the furnace room; back to " + furnaceRoom);
                DebugTravel(furnaceRoom, true); tourStep = 0; tourWaitFrom = -1; tourAt = now + 6;
                return;
            }
            if (furnaceRooms == null)
            {
                // DOLL_NW first (it had the furnace in every run so far); never the game-over or test rooms.
                furnaceRooms = new List<string>();
                AssetDataManager assets = AssetDataManager.GetInstance();
                if (assets != null && assets.allRoomDefs != null)
                    foreach (string id in assets.allRoomDefs.Keys)
                        if (id.StartsWith("DOLL_", StringComparison.OrdinalIgnoreCase) && id.IndexOf("END", StringComparison.OrdinalIgnoreCase) < 0) furnaceRooms.Add(id);
                furnaceRooms.Sort(string.CompareOrdinal);
                if (furnaceRooms.Remove("DOLL_NW")) furnaceRooms.Insert(0, "DOLL_NW");
                Log("Tour: Dollhouse rooms to search for the furnace: " + string.Join(", ", furnaceRooms.ToArray()));
            }
            // Up to 20 s for the trip, then up to 6 s in the room for a furnace. A room left before that is visited again.
            if (furnaceSearchRoom != null)
            {
                if (CurrentRoomId() == furnaceSearchRoom)
                {
                    if (furnaceArrivedAt < 0) furnaceArrivedAt = now;
                    if (now - furnaceArrivedAt < 6) { tourAt = now + 1; return; }
                }
                else if (furnaceArrivedAt >= 0 && now - furnaceArrivedAt < 6) { Log("Tour: left " + furnaceSearchRoom + " early; going back"); DebugTravel(furnaceSearchRoom, true); furnaceArrivedAt = -1; furnaceTravelAt = now; tourAt = now + 1; return; }
                else if (furnaceArrivedAt < 0 && now - furnaceTravelAt < 20) { tourAt = now + 1; return; }
            }
            if (furnaceRoomIndex >= furnaceRooms.Count) { Check("furnace.found", false, "no Dollhouse room had a furnace"); tourDone = true; return; }
            furnaceSearchRoom = furnaceRooms[furnaceRoomIndex++]; furnaceArrivedAt = -1; furnaceTravelAt = now;
            Log("Tour: looking for the furnace in " + furnaceSearchRoom);
            DebugTravel(furnaceSearchRoom, true);
            tourAt = now + 1;
        }

        float nextReflect;
        int bossHits;

        // The real Taker ending scripts: End04Control's transfer to End_05, then either the room's own BailToCredits
        // (specimens off, the host's option) or the fight: C1 reflects a fireball and chops the boss with relayed hits,
        // and the shared "violence" key picks BadEnd or GoodEnd on the host.
        void TakerTour(SpookyCharacterController player, float now)
        {
            if (now < tourAt) return;
            TakerBossControl boss = Object.FindObjectOfType<TakerBossControl>();
            if (Hosting && boss == null && Once("taker.go"))
            {
                PersistenceData data = CurrentData;
                OptionsManager options = ServiceLocator.GetService<OptionsManager>();
                if (labTour == "takerbail" && options != null) options.specimen = false;
                if (data != null) data.SetKey("violence", labTour == "takerbad" ? 25 : 0);
                Broadcast(FullStatePacket());
                Log("Tour: taking the team to the Taker (" + labTour + ")");
                HSMFloorState floor = Closest<HSMFloorState>();
                if (floor != null) floor.TransferRequest(null, "End_05", true);
                return;
            }
            if (boss == null || SelfId != 1 || labTour == "takerbail") return;
            if (now >= nextReflect)
            {
                nextReflect = now + 7;
                Log("Tour: reflecting a fireball (boss hits so far " + bossHits + ")");
                boss.FireballReflect();
                tourAt = now + 0.6f;
                return;
            }
            SendMessageOnHit body = null;
            foreach (SendMessageOnHit hit in boss.GetComponentsInChildren<SendMessageOnHit>(true))
                for (int i = 0; i < hit.onHit.GetPersistentEventCount(); i++)
                    if (hit.onHit.GetPersistentMethodName(i) == "GotHit") body = hit;
            if (body == null) { if (Once("taker.nobody")) Log("Tour: no boss hit target found"); return; }
            if (now < nextReflect - 2) { body.TakeHit(); AfterSwing(); bossHits++; tourAt = now + 0.12f; }
        }
        int drops;
        float nextDrop;

        // C2 and C3 drop their connection every 30 s and the lab rejoins them; the host must end with clean avatars.
        void ReconnectTour(float now)
        {
            if (nextDrop == 0) nextDrop = now + 20;
            if (!Hosting && SelfId >= 2 && now >= nextDrop && drops < 3) { drops++; nextDrop = now + 25; Log("Tour: dropping the connection (" + drops + ")"); Shutdown(); return; }
            if (Hosting && now >= tourAt + 110 && Once("reconnect.host"))
            {
                int avatars = 0; foreach (Peer p in peers.Values) if (p.Avatar != null) avatars++;
                Check("reconnect.peers", AcceptedCount == 3, "accepted=" + AcceptedCount);
                Check("reconnect.avatars", avatars <= AcceptedCount, "avatars=" + avatars + " peers=" + AcceptedCount);
            }
        }

        static SendMessageOnHit AddTestHitTarget(GameObject go)
        {
            SendMessageOnHit hit = go.GetComponent<SendMessageOnHit>();
            if (hit != null) return hit;
            hit = go.AddComponent<SendMessageOnHit>();
            hit.onHit = new UnityEngine.Events.UnityEvent();
            hit.onHit.AddListener(() => TestHits++);
            return hit;
        }

        public void NoteHurtReceived() { hurtReceived++; }
    }
}
