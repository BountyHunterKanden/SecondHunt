using MphRead.Entities;
using MphRecomp.Arenas;

namespace MphRecomp.App;

// Mining Plaza's sand crates regrowing (thread 3d373d stage 3, owner 2026-10-31: "build both a grow-up-from-the-ground
// look and a fade-in look, switchable live"): a tap on its line in the gun-adjust pause (L3 + R3) flips
// MiningPlazaCrates.FadeIn. Touch only, like the bot switches (BotStuckToggle.cs). This device's look only.
internal sealed partial class CampaignRenderer
{
    const string SandCrateLine = "SAND CRATE REGROW";
    int _sandCrateTap; // UI thread -> SandCrateInput

    // BotSwitchTap: true when the tapped line is the crates' switch
    bool SandCrateTap(string l)
    {
        if (!l.StartsWith(SandCrateLine)) return false;
        System.Threading.Volatile.Write(ref _sandCrateTap, 1);
        return true;
    }

    // PausedInput, every frame while paused
    void SandCrateInput()
    {
        if (System.Threading.Interlocked.Exchange(ref _sandCrateTap, 0) == 0) return;
        MiningPlazaCrates.FadeIn = !MiningPlazaCrates.FadeIn;
        _statusCountdown = 0;
        Log.Info("MPHMatch", "Sand crate regrow: " + (MiningPlazaCrates.FadeIn ? "fade in" : "grow up"));
    }

    bool HasSandCrates()
    {
        if (_host == null) return false;
        foreach (EntityBase e in _host.Scene.Entities)
        {
            if (e is MiningPlazaCrateEntity) return true;
        }
        return false;
    }

    string SandCratePauseText() => HasSandCrates()
        ? $"\n{SandCrateLine}  [{(MiningPlazaCrates.FadeIn ? " FADE IN " : " GROW UP ")}]  tap to switch"
        : "";
}
