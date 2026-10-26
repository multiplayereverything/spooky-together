using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SpookyTogether
{
    // In single player nobody can leave a room while you play an arcade machine, read a terminal or sit in a puzzle. In
    // co-op a teammate's door moves everyone on, the old room (and the thing that locked you) is unloaded, and its
    // movement/look locks would never be released: you would be stuck. When a room change is forced by a teammate, a
    // running minigame is ended properly, and any lock still held when the next room starts loading is released.
    public sealed partial class TogetherRuntime
    {
        bool forcedTransfer;
        MinigameBaseControl activeMinigame;
        public int StaleLocksReleased;
        static FieldInfo moveLockField, lookLockField, interactionLockField;   // process-scoped: reflection cache
        static MethodInfo endGameMethod;   // process-scoped: reflection cache

        public static void AfterMinigameStart(MinigameBaseControl game) { if (Instance != null) Instance.activeMinigame = game; }

        public static void AfterMinigameEnd(MinigameBaseControl game) { if (Instance != null && Instance.activeMinigame == game) Instance.activeMinigame = null; }

        // Called right before a teammate's door moves this player on.
        void PrepareForcedTransfer()
        {
            forcedTransfer = true;
            if (activeMinigame == null) return;
            MinigameBaseControl game = activeMinigame;
            activeMinigame = null;
            try
            {
                if (endGameMethod == null) endGameMethod = AccessTools.Method(typeof(MinigameBaseControl), "EndGame");
                endGameMethod.Invoke(game, null);
                Log("Ended the minigame because a teammate moved the team on.");
            }
            catch (Exception ex) { Log("Ending the minigame failed: " + (ex.InnerException ?? ex).Message); }
        }

        // At the start of the next room's load: the old room is gone, so its locks are stale.
        public void ReleaseStaleLocks()
        {
            if (!forcedTransfer) return;
            forcedTransfer = false;
            if (Mutant("stalelocks")) return;
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (player == null) return;
            int held = (downedLocks ? 1 : 0) + (panelLockedPlayer ? 1 : 0);
            int released = Clamp(player, ref moveLockField, "movementLocked", held, false)
                + Clamp(player, ref lookLockField, "lookLocked", held, false)
                + Clamp(player, ref interactionLockField, "interactionLock", held, false);
            if (player.weaponLock > (downedLocks ? 1 : 0)) { released += player.weaponLock - (downedLocks ? 1 : 0); player.weaponLock = downedLocks ? 1 : 0; }
            if (released > 0)
            {
                StaleLocksReleased += released;
                player.canPause = true;
                player.ShowLookTarget();
                Log("Released " + released + " lock(s) left by the previous room.");
            }
        }

        // A counter below zero also freezes the player (movement needs exactly 0); never leave one negative.
        void ClampNegativeLocks()
        {
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            if (player == null) return;
            Clamp(player, ref moveLockField, "movementLocked", 0, true);
            Clamp(player, ref lookLockField, "lookLocked", 0, true);
            Clamp(player, ref interactionLockField, "interactionLock", 0, true);
            if (player.weaponLock < 0) player.weaponLock = 0;
        }

        static int Clamp(SpookyCharacterController player, ref FieldInfo field, string name, int to, bool onlyNegative)
        {
            if (field == null) field = AccessTools.Field(typeof(SpookyCharacterController), name);
            if (field == null) return 0;
            int value = (int)field.GetValue(player);
            if (onlyNegative ? value >= 0 : value <= to) return 0;
            field.SetValue(player, onlyNegative ? 0 : to);
            return onlyNegative ? 0 : value - to;
        }
    }
}
