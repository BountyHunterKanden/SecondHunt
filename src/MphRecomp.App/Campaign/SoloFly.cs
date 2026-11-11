using System;
using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Dev solo run: fly mode (owner 2026-10-31, "so i can try placing a landing spot up high where you cant normally get").
// Hold L3 and press jump to switch it on / off. Flying: the left stick moves along where you aim, jump held = up, morph
// held = down, zoom held = 3x as fast; walls and floors don't stop you (the sim's step runs, then the player is put back
// where the flight says). Off again, you drop as usual. The placer's D-up places at your position up there (no floor
// within 4 units below), Select where you aim.
internal sealed partial class CampaignRenderer
{
    bool _soloFly, _soloFlyJumpWas;
    Vector3 _flyPos;

    const float FlySpeed = 9f, FlyFast = 3f; // units a second; x zoom held

    // BuildInput, last: the toggle, and while flying the move / jump / morph / zoom are the flight's, not the game's
    void SoloFlyInput(PadState pad, ref CampaignInput input, PlayerEntity p)
    {
        if (!_solo) { _soloFly = false; return; }
        bool jump = (input.Buttons & CampaignButtons.Jump) != 0;
        if (pad[PadButton.ThumbL] && jump && !_soloFlyJumpWas)
        {
            _soloFly = !_soloFly;
            _gunComboUsed = true; // (L3 let go doesn't also cycle the status text)
            _flyPos = p.Position;
            _statusCountdown = 0;
            Log.Info("MPHMatch", $"solo fly: {(_soloFly ? "on" : "off")} at {p.Position.X:0.00} {p.Position.Y:0.00} {p.Position.Z:0.00}");
            if (!_soloFly) p.Speed = Vector3.Zero;
        }
        _soloFlyJumpWas = jump;
        if (!_soloFly || p.Health == 0) return;
        Vector3 fwd = p.FacingVector;
        if (fwd.LengthSquared < 1e-6f) fwd = -Vector3.UnitZ;
        fwd = fwd.Normalized();
        Vector3 flat = new Vector3(fwd.X, 0, fwd.Z);
        flat = flat.LengthSquared > 1e-6f ? flat.Normalized() : -Vector3.UnitZ;
        Vector3 right = Vector3.Cross(flat, Vector3.UnitY);
        Vector3 vel = fwd * input.Move.Y + right * input.Move.X;
        if (jump && !pad[PadButton.ThumbL]) vel.Y += 1;
        if ((input.Buttons & CampaignButtons.Morph) != 0) vel.Y -= 1;
        float speed = FlySpeed * ((input.Buttons & CampaignButtons.Zoom) != 0 ? FlyFast : 1);
        _flyPos += vel * speed / 60f;
        input.Move = System.Numerics.Vector2.Zero;
        input.Buttons &= ~(CampaignButtons.Jump | CampaignButtons.Boost | CampaignButtons.Morph | CampaignButtons.Zoom);
    }

    // after each sim step: the player put where the flight is, still (no fall, no wall in the way)
    void SoloFlyStep()
    {
        if (!_soloFly || _host == null) return;
        PlayerEntity p = _host.Player;
        if (p.Health == 0 || !p.LoadFlags.TestFlag(LoadFlags.Spawned)) { _flyPos = p.Position; return; }
        p.Reposition(_flyPos, p.FacingVector, _host.Scene.GetNodeRefContaining(_flyPos));
        p.PrevPosition = _flyPos;
        p.Speed = Vector3.Zero;
    }

    string SoloFlyStatusLine() => _soloFly ? $"\nFLY  stick: move  jump: up  morph: down  zoom: fast  (L3+jump: land)  y {_flyPos.Y:0.0}" : "  (L3+jump: fly)";
}
