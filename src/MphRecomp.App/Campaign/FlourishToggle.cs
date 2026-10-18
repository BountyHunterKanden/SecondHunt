using MphRecomp.Campaign;

namespace MphRecomp.App;

// The Trooper's and Dark Samus's idle flourish (Core Campaign/BodyFlourish.cs, owner 2026-10-15): in the gun-adjust pause
// (L3 + R3), L2 steps through the Trooper's candidates, Select through Echoes Dark Samus's and D-pad right through
// Corruption Dark Samus's (owner 2026-10-17: a separate toggle for her), so the owner can judge each by eye. The flourish
// plays after ~10 s with no input, as the vanilla hunters', and once more (BodyFlourish.PlayNow) on every pick and every
// time this pause opens: L3 + R3, Start = see the current pick again.
internal sealed partial class CampaignRenderer
{
    bool _pPrevFlL2, _pPrevFlSelect;
    long _flPausedAt;

    // PausedInput, every frame while paused (dpadRight: D-pad right was just pressed)
    void FlourishInput(PadState pad, bool dpadRight)
    {
        long now = System.Environment.TickCount64;
        if (now - _flPausedAt > 500) BodyFlourish.PlayNow++; // the pause just opened
        _flPausedAt = now;
        bool l2 = pad[PadButton.L2] || pad.L2 >= 0.5f, select = pad[PadButton.Select];
        if (l2 && !_pPrevFlL2) Next(BodyFlourish.Trooper);
        if (select && !_pPrevFlSelect) Next(BodyFlourish.Echoes);
        if (dpadRight) Next(BodyFlourish.Corruption);
        _pPrevFlL2 = l2;
        _pPrevFlSelect = select;

        void Next(BodyFlourish.Set set)
        {
            set.Next();
            BodyFlourish.PlayNow++;
            _statusCountdown = 0;
            Log.Info("MPHMatch", $"{set.Name} flourish: {set.Current.Label}");
        }
    }

    static string FlourishPauseText() =>
        $"\nTrooper flourish: {BodyFlourish.Trooper.Current.Label}  -- L2: next"
        + $"\nEchoes Dark Samus flourish: {BodyFlourish.Echoes.Current.Label}  -- Select: next"
        + $"\nCorruption Dark Samus flourish: {BodyFlourish.Corruption.Current.Label}  -- D-pad right: next  (each plays on resume)";
}
