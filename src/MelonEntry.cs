using MelonLoader;
using SpookyTogether;

[assembly: MelonInfo(typeof(MelonEntry), "Spooky Together", TogetherRuntime.ModVersion, "Local mod project")]
[assembly: MelonGame("Albino Moose Games", "SpookyHD")]
[assembly: HarmonyDontPatchAll]

namespace SpookyTogether
{
    public sealed class MelonEntry : MelonMod
    {
        public override void OnInitializeMelon()
        {
            TogetherRuntime.Log = message => LoggerInstance.Msg(message);
            ModHooks.InstallPatches();
        }
        // Unity objects become available at application start, after loader initialization.
        public override void OnLateInitializeMelon() { ModHooks.Install(); }
    }
}
