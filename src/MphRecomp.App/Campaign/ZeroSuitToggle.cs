using MphRecomp.Campaign;

namespace MphRecomp.App;

// Brawl Zero Suit (Core Campaign/BrawlZeroSuitBody.cs + BrawlZeroSuitRig.cs, owner 2026-10-15): in the gun-adjust pause
// (L3 + R3), live, so the owner can judge them by eye -- X: her moves, round Samus's with the armoured spacing taken in,
// Samus's as they are, the human set (the CC0 clips, Samus's where the set has none); B: her hair and chest springs on / off;
// A: her walks straightened (hips square, knees and feet straight ahead) or Samus's taken in; L1: her torso upright in the
// idle and the walks or as the clips turn it; R1: her ponytail kept off her back and neck or not
internal sealed partial class CampaignRenderer
{
    bool _pPrevX, _pPrevB, _pPrevA, _pPrevL1, _pPrevR1, _zsLogged;

    // PausedInput, every frame while paused
    void ZeroSuitInput(PadState pad)
    {
        if (BrawlZeroSuitBody.Info.Length == 0) return;
        if (!_zsLogged)
        {
            _zsLogged = true;
            Log.Info("MPHMatch", "Brawl Zero Suit: " + BrawlZeroSuitBody.Info);
        }
        bool x = pad[PadButton.X], b = pad[PadButton.B], a = pad[PadButton.A], l1 = pad[PadButton.L1], r1 = pad[PadButton.R1];
        if (x && !_pPrevX)
        {
            BrawlZeroSuitBody.SetMoves((BrawlZeroSuitBody.Moves)(((int)BrawlZeroSuitBody.Set + 1) % 3));
            _statusCountdown = 0;
            Log.Info("MPHMatch", "Brawl Zero Suit moves: " + ZeroSuitMovesName());
        }
        if (b && !_pPrevB)
        {
            BrawlZeroSuitRig.Springs = !BrawlZeroSuitRig.Springs;
            _statusCountdown = 0;
            Log.Info("MPHMatch", "Brawl Zero Suit springs: " + (BrawlZeroSuitRig.Springs ? "on" : "off"));
        }
        if (a && !_pPrevA)
        {
            BrawlZeroSuitRig.Straight = !BrawlZeroSuitRig.Straight;
            BrawlZeroSuitBody.SetMoves(BrawlZeroSuitBody.Set);
            _statusCountdown = 0;
            Log.Info("MPHMatch", "Brawl Zero Suit legs: " + ZeroSuitLegsName());
        }
        if (l1 && !_pPrevL1)
        {
            BrawlZeroSuitRig.Upright = !BrawlZeroSuitRig.Upright;
            BrawlZeroSuitBody.SetMoves(BrawlZeroSuitBody.Set);
            _statusCountdown = 0;
            Log.Info("MPHMatch", "Brawl Zero Suit torso: " + (BrawlZeroSuitRig.Upright ? "upright" : "as the clips turn it"));
        }
        if (r1 && !_pPrevR1)
        {
            BrawlZeroSuitRig.Hair = !BrawlZeroSuitRig.Hair;
            _statusCountdown = 0;
            Log.Info("MPHMatch", "Brawl Zero Suit ponytail off her back: " + (BrawlZeroSuitRig.Hair ? "on" : "off"));
        }
        _pPrevX = x;
        _pPrevB = b;
        _pPrevA = a;
        _pPrevL1 = l1;
        _pPrevR1 = r1;
    }

    static string ZeroSuitMovesName() => BrawlZeroSuitBody.Set switch
    {
        BrawlZeroSuitBody.Moves.Samus => "SAMUS'S, limbs taken in",
        BrawlZeroSuitBody.Moves.SamusAsIs => "Samus's as they are",
        _ => "human set (Samus's for back/sides)",
    };

    static string ZeroSuitLegsName() => BrawlZeroSuitRig.Straight
        ? "STRAIGHT (hips square, knees + feet ahead)"
        : "Samus's, taken in";

    static string ZeroSuitPauseText() => BrawlZeroSuitBody.Info.Length > 0
        ? $"\nBrawl Zero Suit moves: {ZeroSuitMovesName()}  -- X: next"
          + $"\nBrawl Zero Suit hair + chest springs: {(BrawlZeroSuitRig.Springs ? "ON" : "off")}  -- B: switch"
          + $"\nBrawl Zero Suit legs: {ZeroSuitLegsName()}  -- A: switch"
          + $"\nBrawl Zero Suit torso: {(BrawlZeroSuitRig.Upright ? "UPRIGHT (idle + walks)" : "as the clips turn it")}  -- L1: switch"
          + $"\nBrawl Zero Suit ponytail kept off her back: {(BrawlZeroSuitRig.Hair ? "ON" : "off")}  -- R1: switch"
        : "";
}
