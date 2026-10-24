using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpookyTogether
{
    // Endings and credits use HSMGameRootState.GotoKillscreen. The host decides: its own call is broadcast and every
    // client follows; a client's call (from a room script it also runs) is sent to the host instead of ending alone.
    public sealed partial class TogetherRuntime
    {
        public static bool AllowEnding;
        string endingSent, lastEnding;
        public int BossReflects;
        float endingAt = -100;

        public static bool BeforeGotoKillscreen(string killscreen)
        {
            TogetherRuntime rt = Instance;
            if (AllowEnding || rt == null || !rt.Connected || !rt.RunActive || string.IsNullOrEmpty(killscreen)) return true;
            if (rt.IsClient)
            {
                rt.SendToHost(Wire.Packet(Op.Ending, w => Wire.Text(w, killscreen)));
                return false;
            }
            rt.BroadcastEnding(killscreen);
            return true;
        }

        void BroadcastEnding(string killscreen)
        {
            if (endingSent == killscreen && Time.realtimeSinceStartup - endingAt < 30) return;
            endingSent = killscreen; endingAt = Time.realtimeSinceStartup; lastEnding = killscreen;
            gameOverSent = true;
            Broadcast(Wire.Packet(Op.Ending, w => Wire.Text(w, killscreen)));
            Tell("The team reached " + killscreen + ".");
        }

        void HostEndingRequest(BinaryReader reader)
        {
            string killscreen = Wire.Text(reader, 200);
            HSMGameRootState root = Closest<HSMGameRootState>();
            if (root == null || !RunActive || Closest<HSMKillscreenState>() != null) return;
            if (endingSent == killscreen && Time.realtimeSinceStartup - endingAt < 30) return;
            BroadcastEnding(killscreen);
            AllowEnding = true;
            try { root.GotoKillscreen(killscreen); }
            finally { AllowEnding = false; }
        }

        void ClientEnding(BinaryReader reader)
        {
            string killscreen = Wire.Text(reader, 200);
            endingSent = killscreen; lastEnding = killscreen;
            HSMGameRootState root = Closest<HSMGameRootState>();
            pendingStartMansion = null; pendingRoom = null;
            if (root == null || !RunActive) return;
            if (downedLocks) ReleaseDownedLocks();
            LocallyDowned = false;
            AllowEnding = true;
            try { root.GotoKillscreen(killscreen); }
            finally { AllowEnding = false; }
            Tell("The team reached " + killscreen + ".");
        }

        // The Taker boss can only be hurt after a player reflects one of its fireballs. Damage counts on the host's boss,
        // so a client's reflect is forwarded there, and the host's reflect is mirrored on every client's copy.
        public static bool BeforeFireballReflect(TakerBossControl boss)
        {
            TogetherRuntime rt = Instance;
            if (ApplyingState || rt == null || !rt.Connected || !rt.RunActive) return true;
            rt.Broadcast(Wire.Packet(Op.BossReflect, null));
            return true;
        }

        void ApplyBossReflect(int from)
        {
            if (Hosting) Broadcast(Wire.Packet(Op.BossReflect, null), from);
            TakerBossControl boss = Object.FindObjectOfType<TakerBossControl>();
            if (boss == null || !RunActive) return;
            BossReflects++;
            ApplyingState = true;
            try { boss.FireballReflect(); }
            catch (System.Exception ex) { Log("Boss reflect failed: " + ex.Message); }
            finally { ApplyingState = false; }
        }

        // The scare chair kills whoever sat in it; in co-op that is that player's knock-down, not everyone's ending.
        public static bool BeforeChairKill(ScareChairController chair)
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.Connected || !rt.RunActive) return true;
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (player == null) return true;
            AllowHostHurt = true;
            try { player.Hurt(99999, new SimpleSpecStruct { _killscreenName = chair.killscreenName }, null); }
            finally { AllowHostHurt = false; }
            return false;
        }

        // The endless-mode skip TV rewrites the floor, counters and chasers; only the host may use it.
        public static bool BeforeSkipRoomTv()
        {
            TogetherRuntime rt = Instance;
            if (rt == null || !rt.IsClient || !rt.Connected) return true;
            rt.Tell("Only the host can use the skip-room TV.");
            return false;
        }
    }
}
