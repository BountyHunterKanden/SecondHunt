using MphRecomp.Campaign;

namespace MphRecomp.App;

// Dark Samus's Corruption idle (Core Campaign/DarkSamusFloat.cs, owner 2026-10-11): in the gun-adjust pause (L3 + R3), Y
// switches it live between our float and her planted b_ready, so the owner can judge the two by eye
internal sealed partial class CampaignRenderer
{
    bool _pPrevY;

    // PausedInput, every frame while paused
    void DarkSamusFloatInput(PadState pad)
    {
        bool y = pad[PadButton.Y];
        if (y && !_pPrevY && DarkSamusFloat.Made)
        {
            DarkSamusFloat.Set(!DarkSamusFloat.On);
            _statusCountdown = 0;
            Log.Info("MPHMatch", "Dark Samus (Corruption) idle: " + (DarkSamusFloat.On ? "float" : "planted"));
        }
        _pPrevY = y;
    }

    static string DarkSamusFloatPauseText() => DarkSamusFloat.Made
        ? $"\nDark Samus (Corruption) idle: {(DarkSamusFloat.On ? "FLOAT" : "planted (her own ready)")}  -- Y: switch"
        : "";
}
