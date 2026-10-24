using BepInEx;

namespace SpookyTogether
{
    [BepInPlugin("local.spookytogether.multiplayer", "Spooky Together", TogetherRuntime.ModVersion)]
    public sealed class BepInExEntry : BaseUnityPlugin
    {
        void Awake()
        {
            TogetherRuntime.Log = message => Logger.LogInfo(message);
            ModHooks.InstallPatches();
            ModHooks.Install();
        }
    }
}
