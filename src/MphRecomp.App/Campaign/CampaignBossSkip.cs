using MphRecomp.Campaign;

namespace MphRecomp.App;

// Dev launcher "Escape tests" (owner 2026-10-03, to test the escape sounds): --es boss down, on top of the boss-fight
// start (--es loadout all: a fresh story, all weapons, nothing saved), beats the boss for the player through the game's
// own damage path (Core Campaign/BossSkip.cs) once its intro is over. The death movie, the Octolith, its pickup movie and
// dialogs, and the escape are then the game's own: pick up the Octolith, go back out the way the boss's portal brought
// you in, and the room before the boss (its escape layer) starts the escape timer. Tools -bossskip runs it on the PC.
// CampaignActivity.cs only has the two call sites (UseBossSkip in OnCreate, StepBossSkip in the sim step loop).
internal sealed partial class CampaignRenderer
{
    BossSkip? _bossSkip;

    public void UseBossSkip()
    {
        _bossSkip = new BossSkip();
        Log.Info("MPHCampaign", "boss skip: the boss will be beaten once its intro is over (--es boss down)");
    }

    // after every sim step. While the hits land, the sim runs at the step loop's cap (4 steps a frame, ~4x) with Samus
    // kept at full energy: the fights take 15-80 s of game time (owner 2026-10-03: "it just loads the boss battle"
    // after ~12 s of the VDO 2 Slench, an 80 s one). The accumulator is only topped up to one step, so nothing is left
    // over once the boss is down.
    void StepBossSkip()
    {
        if (_bossSkip == null || _bossSkip.Done || _host == null)
        {
            return;
        }
        bool wasFighting = _bossSkip.Fighting;
        _bossSkip.Step(_host.Scene);
        if (_bossSkip.Fighting)
        {
            if (!wasFighting)
            {
                Log.Info("MPHCampaign", "boss skip: intro over, beating the boss (sim at 4 steps a frame)");
            }
            _host.Player.Health = _host.Player.HealthMax;
            if (_accum < 1 / 60.0)
            {
                _accum += 1 / 60.0;
            }
        }
        if (_bossSkip.Done)
        {
            Log.Info("MPHCampaign", $"boss skip: boss down ({_bossSkip.Report})");
        }
    }
}
