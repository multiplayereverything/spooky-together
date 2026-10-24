using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SpookyTogether
{
    public static class ModHooks
    {
        static HarmonyLib.Harmony harmony;   // process-scoped: patches are applied once per process
        static int applied, failed;   // process-scoped: patches are applied once per process

        public static void InstallPatches()
        {
            if (harmony != null) return;
            harmony = new HarmonyLib.Harmony("local.spookytogether.multiplayer");
            Patch(typeof(HSMLoadRoomState), "RunRoutine", null, "LoadRoomPostfix");
            Patch(typeof(HSMRoomReadyState), "EnterStateInternal", "RoomReadyPrefix", "RoomReadyPostfix");
            Patch(typeof(HSMFloorState), "TransferRequest", "TransferPrefix", null, typeof(string), typeof(string), typeof(bool));
            Patch(typeof(HSMMenuState), "StartMansion", "StartMansionPrefix", "StartMansionPostfix");
            Patch(typeof(HSMGameRootState), "KillPlayer", "KillPlayerPrefix", null);
            Patch(typeof(SpookyCharacterController), "OnRoomReady", null, "OnRoomReadyPostfix");
            Patch(typeof(HSMBaseState), "ExitState", null, "ExitStatePostfix");
            Patch(typeof(GameManager), "OnApplicationFocus", "FocusPrefix", null);
            Patch(typeof(LockedDoorGroup), "Start", "SeedPrefix", "SeedPostfix");
            Patch(typeof(SelectiveExister), "ExistenceCheck", "SeedPrefix", "SeedPostfix");
            Patch(typeof(RoomController), "DoRoomRandom", "SeedPrefix", "SeedPostfix");
            Patch(typeof(RandomFlipZ), "DoFlip", "SeedPrefix", "SeedPostfix");
            Patch(typeof(RandomYRot), "Start", "SeedPrefix", "SeedPostfix");
            Patch(typeof(SpecimenOneController), "Start", "SeedPrefix", "SeedPostfix");
            Patch(typeof(UnknownSpec5Spec1Controller), "Start", "SeedPrefix", "SeedPostfix");
            Patch(typeof(SpecimenOneController), "RandomDeploy", "CutoutRandomPrefix", "CutoutRandomPostfix");
            Patch(typeof(SpecimenOneController), "Deploy", "CutoutDeployPrefix", "CutoutDeployPostfix");
            Patch(typeof(SpecimenManager), "InstantiateSpecimenFromPrefab", null, "SpecimenCreatedPostfix");
            Patch(typeof(SpecimenManager), "StartSpecimenRechase", "RechasePrefix", null);
            Patch(typeof(SpookyCharacterController), "Hurt", "Hurt5Prefix", null, typeof(float), typeof(SpecimenInterface), typeof(AudioClip), typeof(bool), typeof(float));
            Patch(typeof(SpookyCharacterController), "Hurt", "Hurt3Prefix", null, typeof(float), typeof(SpecimenInterface), typeof(AudioClip));
            Patch(typeof(SpecimenManager), "EndSpecimen", "EndSpecimenPrefix", null);
            Patch(typeof(EmergenceUtil), "TweenToPlayerSpawn", "TweenPrefix", null);
            Patch(typeof(EmergenceUtil), "FinishSpawn", "TweenPrefix", null);
            Patch(typeof(UnknownSpecimen1Controller), "DoHit", "UnknownOneHitPrefix", null);
            Patch(typeof(GameManager), "ExitGame", "ExitGamePrefix", null);
            Patch(typeof(LocalRoomControl), "InitializeActive", "SeedPrefix", "SeedPostfix");
            Patch(typeof(PersistenceData), "SetKey", "SetKeyPrefix", "SetKeyPostfix");
            Patch(typeof(PersistenceData), "ClearKey", "ClearKeyPrefix", "ClearKeyPostfix");
            Patch(typeof(PersistenceData), "AddToInventory", null, "AddItemPostfix");
            Patch(typeof(PersistenceData), "RemoveFromInventory", null, "RemoveItemPostfix");
            Patch(typeof(PersistenceData), "RemoveFromInventorySubvalues", null, "RemoveItemsPostfix");
            Patch(typeof(PickUpItem), "OnInteract", null, "PickUpItemPostfix");
            Patch(typeof(PickupComponent), "OnInteract", null, "PickupPostfix");
            Patch(typeof(WeaponPickupComponent), "OnInteract", "WeaponPickupPrefix", "WeaponPickupPostfix");
            Patch(typeof(DoorComponent), "Unlock", null, "DoorUnlockPostfix");
            Patch(typeof(LocalDoorComponent), "Unlock", null, "LocalDoorUnlockPostfix");
            Patch(typeof(ElevatorButtonControl), "OnInteract", "ElevatorPrefix", "ElevatorPostfix");
            Patch(typeof(MinigameBaseControl), "StartGame", null, "MinigameStartPostfix");
            Patch(typeof(HSMFloorState), "GetWallMaterial", "WallMatPrefix", null);
            Patch(typeof(HSMFloorState), "GetFloorMaterial", "FloorMatPrefix", null);
            Patch(typeof(HSMFloorState), "GetCeilingMaterial", "CeilingMatPrefix", null);
            Patch(typeof(SpecimenManager), "SpecimenOverrideFloorMaterials", null, "LookOverridePostfix");
            Patch(typeof(SpecimenManager), "SpecimenClearOverriddenFloorMaterials", null, "LookOverridePostfix");
            Patch(typeof(MinigameBaseControl), "EndGame", null, "MinigameEndPostfix");
            Patch(typeof(KHElevatorButton), "OnInteract", "KHElevatorPrefix", "KHElevatorPostfix");
            Patch(typeof(SendMessageOnHit), "TakeHit", "TakeHitPrefix", null);
            Patch(typeof(SpookyCharacterController), "CurrentWeapon", "CurrentWeaponPrefix", null);
            Patch(typeof(SwingableWeaponConfig), "DoHitTest", "SwingPrefix", null);
            Patch(typeof(M1WeaponConfig), "DoHitTest", "SwingPrefix", null);
            Patch(typeof(HSMGameRootState), "GotoKillscreen", "KillscreenPrefix", null);
            Patch(typeof(ScareChairController), "ChairKill", "ChairKillPrefix", null);
            Patch(typeof(TakerBossControl), "FireballReflect", "ReflectPrefix", null);
            Patch(typeof(AudioSource), "PlayOneShot", "OneShotPrefix", null, typeof(AudioClip), typeof(float));
            Patch(typeof(AudioSource), "PlayOneShot", "OneShot1Prefix", null, typeof(AudioClip));
            Patch(typeof(EndlessSkipRoomUIControl), "StartInteraction", "SkipTvPrefix", null);
            int retargeted = TogetherRuntime.PatchSpecimenTargets(harmony);
            TogetherRuntime.Log("Specimen methods retargeted: " + retargeted);
            TogetherRuntime.Log("Harmony patches applied: " + applied + ", failed: " + failed);
        }

        public static void Install() { TogetherRuntime.Boot(); }

        static void Patch(Type type, string name, string prefix, string postfix, params Type[] args)
        {
            try
            {
                MethodInfo target = args.Length > 0 ? AccessTools.Method(type, name, args) : AccessTools.Method(type, name);
                if (target == null) throw new MissingMethodException(type.Name, name);
                harmony.Patch(target,
                    prefix == null ? null : new HarmonyMethod(typeof(ModHooks), prefix),
                    postfix == null ? null : new HarmonyMethod(typeof(ModHooks), postfix));
                applied++;
            }
            catch (Exception ex) { failed++; TogetherRuntime.Log("Patch failed " + type.Name + "." + name + ": " + ex.Message); }
        }

        static void LoadRoomPostfix(HSMLoadRoomState __instance, ref IEnumerator<HSMBaseState> __result)
        {
            __result = TogetherRuntime.WrapLoad(__instance, __result);
        }

        static void RoomReadyPrefix(HSMRoomReadyState __instance, out bool __state)
        {
            __state = __instance.roomRuntimeData != null && string.IsNullOrEmpty(__instance.roomRuntimeData.namedSpawnDoor);
        }

        static void RoomReadyPostfix(HSMRoomReadyState __instance, bool __state)
        {
            try { TogetherRuntime.AfterRoomReadyEnter(__instance, __state); }
            catch (Exception ex) { TogetherRuntime.Log("Spawn sync failed: " + ex.Message); }
        }

        static bool TransferPrefix(string specificNextDoor, string specificNextRoom, bool noIncreaseRoomCount)
        {
            return TogetherRuntime.BeforeTransferRequest(specificNextDoor, specificNextRoom, noIncreaseRoomCount);
        }

        static bool StartMansionPrefix(MansionConfig mansionConfig) { return TogetherRuntime.BeforeStartMansion(mansionConfig); }

        static void StartMansionPostfix(MansionConfig mansionConfig) { TogetherRuntime.AfterStartMansion(mansionConfig); }

        static bool KillPlayerPrefix(SpecimenInterface source) { return TogetherRuntime.BeforeKillPlayer(source); }

        static void OnRoomReadyPostfix() { TogetherRuntime.AfterRoomReady(); }

        // Losing focus pauses the single-player game; in a shared run that would freeze one player on every Alt+Tab.
        static bool FocusPrefix() { TogetherRuntime rt = TogetherRuntime.Instance; return rt == null || !rt.Connected; }

        static void ExitStatePostfix(HSMBaseState __instance) { if (__instance is HSMMansionState) TogetherRuntime.MansionExited(); }

        static void SpecimenCreatedPostfix(GameObject __result, SpecimenConfig specimenConfig, bool rechase)
        {
            try { TogetherRuntime.AfterSpecimenCreated(__result, specimenConfig, rechase); }
            catch (Exception ex) { TogetherRuntime.Log("Specimen tracking failed: " + ex.Message); }
        }

        static void SetKeyPrefix(PersistenceData __instance, string key, out int __state) { __state = __instance.liveData != null && __instance.liveData.ContainsKey(key) ? __instance.GetKey(key) : int.MinValue; }
        static void SetKeyPostfix(PersistenceData __instance, string key, int value, int __state) { if (__state != value) TogetherRuntime.AfterKeyChange(__instance, key, value, false); }
        static void ClearKeyPrefix(PersistenceData __instance, string key, out bool __state) { __state = __instance.liveData != null && __instance.liveData.ContainsKey(key); }
        static void ClearKeyPostfix(PersistenceData __instance, string key, bool __state) { if (__state) TogetherRuntime.AfterKeyChange(__instance, key, 0, true); }
        static void AddItemPostfix(PersistenceData __instance, string newItem) { TogetherRuntime.AfterInventoryChange(__instance, newItem, 0); }
        static void RemoveItemPostfix(PersistenceData __instance, string item) { TogetherRuntime.AfterInventoryChange(__instance, item, 1); }
        static void RemoveItemsPostfix(PersistenceData __instance, string itemPrefix) { TogetherRuntime.AfterInventoryChange(__instance, itemPrefix, 2); }
        static void PickUpItemPostfix(PickUpItem __instance) { TogetherRuntime.AfterObjectUsed(__instance, 0, null); }
        static void PickupPostfix(PickupComponent __instance) { TogetherRuntime.AfterObjectUsed(__instance, 1, null); }
        static void WeaponPickupPrefix(WeaponPickupComponent __instance, out string __state) { __state = __instance.newWeapon; }
        static void WeaponPickupPostfix(WeaponPickupComponent __instance, string __state) { TogetherRuntime.AfterObjectUsed(__instance, 2, __state); }
        static void DoorUnlockPostfix(DoorComponent __instance) { TogetherRuntime.AfterObjectUsed(__instance, 3, null); }
        // Only a press that actually starts the ride (button not yet pushed) is relayed.
        static void ElevatorPrefix(bool ___isPushed, out bool __state) { __state = !___isPushed; }
        static void ElevatorPostfix(ElevatorButtonControl __instance, bool ___isPushed, bool __state) { if (__state && ___isPushed) TogetherRuntime.AfterObjectUsed(__instance, 5, null); }
        static void KHElevatorPrefix(bool ___isPushed, out bool __state) { __state = !___isPushed; }
        static void KHElevatorPostfix(KHElevatorButton __instance, bool ___isPushed, bool __state) { if (__state && ___isPushed) TogetherRuntime.AfterObjectUsed(__instance, 6, null); }
        static bool WallMatPrefix(bool lit, ref Material __result) { return TogetherRuntime.BeforeGetMaterial(lit ? 1 : 0, ref __result); }
        static bool FloorMatPrefix(bool lit, ref Material __result) { return TogetherRuntime.BeforeGetMaterial(lit ? 3 : 2, ref __result); }
        static bool CeilingMatPrefix(ref Material __result) { return TogetherRuntime.BeforeGetMaterial(4, ref __result); }
        static void LookOverridePostfix() { TogetherRuntime.AfterLookOverride(); }
        static void MinigameStartPostfix(MinigameBaseControl __instance) { TogetherRuntime.AfterMinigameStart(__instance); }
        static void MinigameEndPostfix(MinigameBaseControl __instance) { TogetherRuntime.AfterMinigameEnd(__instance); }
        static void LocalDoorUnlockPostfix(LocalDoorComponent __instance) { TogetherRuntime.AfterObjectUsed(__instance, 4, null); }
        static bool TakeHitPrefix(SendMessageOnHit __instance) { return TogetherRuntime.BeforeTakeHit(__instance); }
        static bool CurrentWeaponPrefix(string weapon, ref bool __result) { return TogetherRuntime.BeforeCurrentWeapon(weapon, ref __result); }
        static void SwingPrefix() { TogetherRuntime.AfterSwing(); }
        static bool KillscreenPrefix(string killscreenName) { return TogetherRuntime.BeforeGotoKillscreen(killscreenName); }
        static bool OneShotPrefix(AudioSource __instance, AudioClip clip, float volumeScale) { TogetherRuntime.NoteCutoutSound(__instance, clip); return TogetherRuntime.BeforePlayOneShot(__instance, clip, volumeScale); }
        static bool OneShot1Prefix(AudioSource __instance, AudioClip clip) { TogetherRuntime.NoteCutoutSound(__instance, clip); return TogetherRuntime.BeforePlayOneShot(__instance, clip, 1f); }
        static bool CutoutRandomPrefix(SpecimenOneController __instance, out bool __state)
        {
            __state = __instance.geoContainer != null && __instance.geoContainer.activeSelf;
            return TogetherRuntime.BeforeCutoutTrigger(__instance, false);
        }
        static void CutoutRandomPostfix(SpecimenOneController __instance, bool __state) { TogetherRuntime.AfterCutoutRandomDeploy(__instance, __state); }
        static bool CutoutDeployPrefix(SpecimenOneController __instance) { return TogetherRuntime.BeforeCutoutTrigger(__instance, true); }
        static void CutoutDeployPostfix(SpecimenOneController __instance) { TogetherRuntime.AfterCutoutDeploy(__instance); }
        static bool ReflectPrefix(TakerBossControl __instance) { return TogetherRuntime.BeforeFireballReflect(__instance); }
        static bool ChairKillPrefix(ScareChairController __instance) { return TogetherRuntime.BeforeChairKill(__instance); }
        static bool SkipTvPrefix() { return TogetherRuntime.BeforeSkipRoomTv(); }

        static bool TweenPrefix(SpawnDoor spawnFrom) { return TogetherRuntime.BeforeTweenToSpawn(spawnFrom); }
        static bool EndSpecimenPrefix(GameObject spec) { return TogetherRuntime.BeforeEndSpecimen(spec); }
        static bool UnknownOneHitPrefix(UnknownSpecimen1Controller __instance) { return TogetherRuntime.BeforeUnknownOneHit(__instance); }
        static bool ExitGamePrefix() { return TogetherRuntime.BeforeExitGame(); }

        static bool RechasePrefix() { return TogetherRuntime.BeforeRechase(); }

        static bool Hurt5Prefix(SpookyCharacterController __instance, float dmg, SpecimenInterface source, bool doFx, float fxDuration)
        {
            return TogetherRuntime.BeforeHurt(__instance, dmg, source, doFx, fxDuration);
        }

        static bool Hurt3Prefix(SpookyCharacterController __instance, float dmg, SpecimenInterface source)
        {
            return TogetherRuntime.BeforeHurt(__instance, dmg, source, true, 0.5f);
        }

        static void SeedPrefix(Component __instance, out UnityEngine.Random.State __state) { __state = TogetherRuntime.BeginSeeded(__instance); }

        static void SeedPostfix(UnityEngine.Random.State __state) { TogetherRuntime.EndSeeded(__state); }
    }
}
