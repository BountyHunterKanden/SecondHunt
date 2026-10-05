namespace MphRecomp.App;

// Public builds vs the dev builds on the owner's devices. A public build (docs/RELEASE.md: -p:MphReleaseSign=true)
// defines MPH_PUBLIC in MphRead.Android.csproj.
internal static class BuildFlags
{
#if MPH_PUBLIC
    public const bool Public = true;
#else
    public const bool Public = false;
#endif

    // Ship-ready S13/S19/S34 (public beta audit 5.1 R4): the dev-only entry points (CampaignActivity, RenderActivity,
    // MusicTestActivity) are reachable from other apps only in dev builds, where `adb shell am start -n
    // com.mphrecomp.app/...` shortcuts use them (e.g. `--es shipmenu 1`). A public build starts them only from its own
    // screens (explicit intents inside the app work either way).
    public const bool ExportDevActivities = !Public;
}
