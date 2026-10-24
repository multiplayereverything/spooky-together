using System.Collections.Generic;
using UnityEngine;

namespace SpookyTogether
{
    // A lethal hit (HSMGameRootState.KillPlayer) downs the player while a teammate is still standing: they follow a
    // teammate's view and revive at the next room's spawn. When nobody is left standing, everyone runs the native death.
    public sealed partial class TogetherRuntime
    {
        public bool LocallyDowned;
        Vector3 downedBody;
        string downedKillscreen;
        int spectating = -1;
        bool downedLocks, gameOverSent;
        public static bool AllowDeath;

        void ResetDowned()
        {
            if (downedLocks) ReleaseDownedLocks();
            LocallyDowned = false; downedKillscreen = null; spectating = -1; gameOverSent = false;
        }

        bool TeammateStanding()
        {
            foreach (Peer peer in peers.Values)
                if (peer.Accepted && !peer.Downed && peer.RoomSeq >= 0 && peer.RoomSeq == CurrentRoomSeq) return true;
            return false;
        }

        public static bool BeforeKillPlayer(SpecimenInterface source)
        {
            TogetherRuntime rt = Instance;
            if (AllowDeath || rt == null || !rt.Connected || !rt.RunActive) return true;
            if (rt.LocallyDowned) return false;
            if (!rt.TeammateStanding())
            {
                // Last one standing: the whole team loses together.
                if (rt.Hosting) { rt.gameOverSent = true; rt.Broadcast(Wire.Packet(Op.GameOver, null)); }
                else rt.SendStatus(true);
                return true;
            }
            rt.GoDown(source);
            return false;
        }

        void GoDown(SpecimenInterface source)
        {
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            downedKillscreen = null;
            try { if (source != null) downedKillscreen = source.killscreenName; } catch { }   // a destroyed specimen's getter throws: no killscreen name
            LocallyDowned = true;
            downedBody = player != null ? player.transform.position : Vector3.zero;
            if (player != null && !downedLocks)
            {
                player.AddMoveLock(); player.AddLookLock(); player.AddInteractionLock(); player.AddInvulnerability(); player.weaponLock++;
                player.canPause = true;
                downedLocks = true;
            }
            spectating = -1;
            Tell("You're down! Watching a teammate - you'll be back in the next room. Click to switch view.");
            SendStatus(true);
            if (Hosting) CheckAllDowned();
        }

        void ReleaseDownedLocks()
        {
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (player != null)
            {
                player.DecMoveLock(); player.DecLookLock(); player.DecInteractionLock(); player.DecInvulnerability();
                if (player.weaponLock > 0) player.weaponLock--;
            }
            downedLocks = false;
        }

        public static void AfterRoomReady()
        {
            TogetherRuntime rt = Instance;
            if (rt == null) return;
            rt.SpreadFromSpawn();
            rt.SeedPuzzleState();
            // Materials can change while the room loads (a specimen's floor override); resend the final look.
            if (rt.Hosting && rt.Connected) rt.HostSendLook(rt.CurrentRoomSeq);
            if (!rt.LocallyDowned) return;
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            rt.ReleaseDownedLocks();
            rt.LocallyDowned = false; rt.spectating = -1;
            if (player != null)
            {
                player.currentHealth.Fill();
                player.canPause = true; player.canSprint = true; player.hideTopHud = false;
                CharacterController body = player.GetComponent<CharacterController>();
                if (body != null) body.enabled = true;
                player.myCamera.transform.localRotation = Quaternion.identity;
            }
            rt.Tell("You're back on your feet.");
            rt.SendStatus(false);
        }

        // Everyone shares one spawn door, so step each player sideways by their slot (through the CharacterController,
        // which stops at walls) instead of stacking them inside each other.
        void SpreadFromSpawn()
        {
            if (!Connected || SelfId <= 0) return;
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            CharacterController body = player != null ? player.GetComponent<CharacterController>() : null;
            if (body == null || !body.enabled) return;
            Vector3 right = player.transform.right, back = -player.transform.forward;
            Vector3[] offsets = { Vector3.zero, right * 1.1f, -right * 1.1f, back * 1.1f };
            body.Move(offsets[Mathf.Clamp(SelfId, 0, offsets.Length - 1)] + back * 0.4f);
        }

        void SendStatus(bool down)
        {
            if (!Connected) return;
            if (Hosting) Broadcast(Wire.Packet(Op.Status, w => { w.Write(0); w.Write(down); }));
            else SendToHost(Wire.Packet(Op.Status, w => w.Write(down)));
        }

        void SetDowned(Peer peer, bool down)
        {
            if (down && !peer.Downed) Tell(peer.Name + " is down! They return in the next room.");
            peer.Downed = down;
            Broadcast(Wire.Packet(Op.Status, w => { w.Write(peer.Id); w.Write(down); }), peer.Id);
            if (down) CheckAllDowned();
        }

        void CheckAllDowned()
        {
            if (!Hosting || !RunActive || gameOverSent) return;
            if (!LocallyDowned) return;
            foreach (Peer peer in peers.Values) if (peer.Accepted && !peer.Downed && peer.RoomSeq >= 0) return;
            gameOverSent = true;
            Broadcast(Wire.Packet(Op.GameOver, null));
            Tell("Everyone is down. Game over.");
            ForceDeath();
        }

        void ClientGameOver()
        {
            Tell("Everyone is down. Game over.");
            pendingStartMansion = null; pendingRoom = null;
            if (RunActive) ForceDeath();
        }

        void ForceDeath()
        {
            HSMGameRootState root = Closest<HSMGameRootState>();
            if (root == null) return;
            string killscreen = string.IsNullOrEmpty(downedKillscreen) ? "GenericGameover" : downedKillscreen;
            if (downedLocks) ReleaseDownedLocks();
            LocallyDowned = false;
            AllowDeath = true;
            try { root.KillPlayer(new SimpleSpecStruct { _killscreenName = killscreen }); }
            catch (System.Exception ex) { Log("Native death failed: " + ex.Message); }
            finally { AllowDeath = false; }
        }

        void UpdateDowned()
        {
            if (!LocallyDowned) return;
            if (!Connected) { ForceDeath(); return; }
            if (Input.GetMouseButtonDown(0) && !show) spectating = NextSpectateTarget(spectating);
        }

        int NextSpectateTarget(int after)
        {
            List<int> ids = new List<int>();
            foreach (Peer peer in peers.Values) if (peer.Accepted && peer.HasPose && !peer.Downed && peer.RoomSeq == CurrentRoomSeq) ids.Add(peer.Id);
            if (ids.Count == 0) return -1;
            ids.Sort();
            foreach (int id in ids) if (id > after) return id;
            return ids[0];
        }

        public int SpectatingId { get { return LocallyDowned ? spectating : -1; } }

        void LateUpdateDowned()
        {
            if (!LocallyDowned) return;
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (player == null || !player.ready) return;
            Peer target;
            if (spectating < 0 || !peers.TryGetValue(spectating, out target) || target.Downed || target.RoomSeq != CurrentRoomSeq)
            {
                spectating = NextSpectateTarget(-1);
                if (spectating < 0 || !peers.TryGetValue(spectating, out target)) return;
            }
            CharacterController body = player.GetComponent<CharacterController>();
            if (body != null && body.enabled) body.enabled = false;
            player.transform.position = target.ShownPosition;
            player.transform.rotation = Quaternion.Euler(0, target.ShownYaw, 0);
            if (player.myCamera != null) player.myCamera.transform.localRotation = Quaternion.Euler(target.Pitch, 0, 0);
        }
    }
}
