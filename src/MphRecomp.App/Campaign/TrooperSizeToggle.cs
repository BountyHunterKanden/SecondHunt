using MphRecomp.Campaign;

namespace MphRecomp.App;

// The Trooper's drawn size (Core NativeBody.TrooperSize, owner 2026-10-15: his Prime 1 size, 1.5x): in the gun-adjust pause
// (L3 + R3), R2 steps it live through 1x (the old fit) .. 1.5x (owner 10-16: 1.5x "oppressively bigger" than the others,
// find a middle ground), so the owner can pick by eye. Draw only: his hitbox is the same cylinder at every size.
internal sealed partial class CampaignRenderer
{
    bool _pPrevR2;
    static readonly float[] TrooperSizes = [1f, 1.1f, 1.2f, 1.25f, 1.3f, 1.4f, 1.5f];

    // PausedInput, every frame while paused
    void TrooperSizeInput(PadState pad)
    {
        bool r2 = pad[PadButton.R2] || pad.R2 >= 0.5f;
        if (r2 && !_pPrevR2)
        {
            int next = Array.FindIndex(TrooperSizes, s => s > NativeBody.TrooperSize + 0.001f);
            NativeBody.TrooperSize = TrooperSizes[next < 0 ? 0 : next];
            _statusCountdown = 0;
            Log.Info("MPHMatch", $"Trooper size: {NativeBody.TrooperSize:0.##}x");
        }
        _pPrevR2 = r2;
    }

    static string TrooperSizePauseText() =>
        $"\nTrooper size: {NativeBody.TrooperSize:0.##}x (1x old, 1.5x his Prime 1 size)  -- R2: next size";
}
