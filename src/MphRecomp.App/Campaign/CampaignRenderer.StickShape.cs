using MphRecomp.Input;

namespace MphRecomp.App;

// CONTROLS > AIM > STICKS and the BUTTONS tab's "... mode" rows (owner 2026-11-16; Core Input/StickShape.cs): the stick dead
// zones (cut by the hosts as they read the pad), the controller's aim curve (StickAim) and the scoreboard and spider ball
// buttons as hold or toggle (BuildInput, MatchScoreboardHeld). All of it shapes the input before CampaignInput, so the sim
// and replays see a plain stick and held buttons; the defaults (15%, LINEAR, HOLD) are the old input exactly
internal sealed partial class CampaignRenderer
{
    bool _scoreboardToggle, _spiderToggle;
    HoldToggle _scoreboardLatch, _spiderLatch;

    void LoadStickShape(MphRecomp.Config.RecompSettings s)
    {
        StickShape.Load(s);
        _scoreboardToggle = s.ScoreboardToggle;
        _spiderToggle = s.SpiderToggle;
    }

    // the controller's aim stick on the aim curve (never the touch stick); LINEAR leaves it as it is
    static (float X, float Y) ShapedAim(PadState pad, bool touch)
    {
        float x = pad.Rx, y = pad.Ry;
        if (!touch) StickShape.ShapeAim(ref x, ref y);
        return (x, y);
    }

    // the spider ball's button this step: held, or toggled on by a press while in a spider-track alt form; leaving the
    // form turns a toggle off
    bool SpiderButton(bool spiderForm, bool held)
    {
        if (!spiderForm)
        {
            _spiderLatch.Clear();
            return false;
        }
        return _spiderLatch.Next(held, _spiderToggle);
    }
}
