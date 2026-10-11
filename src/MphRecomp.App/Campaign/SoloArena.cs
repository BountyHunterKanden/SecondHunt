using System;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Dev launcher "Solo arena" (owner 2026-10-08): any character -- a hunter, the Trooper, Dark Samus -- alone in any
// multiplayer arena, in full control, seen in the third-person view. R3 switches the right stick between aiming (the
// over-the-shoulder view, CampaignRenderer.UpdateThirdPerson) and an ORBIT camera that circles the player
// (Scene.HostView: render only, the game's own camera, aim and logic stay where they were). A Battle with no bots and
// no time limit (alone, nobody reaches the point goal), not recorded as a replay. Dev builds only.
//   --es solo 1  (with --es match Battle --arena <name> --es hunter / gun / skins as a match)
//   --es orbit <degrees>  start in the orbit camera, that far round from behind the player (180 = in front; PC checks)
//   --es selfbody 1  the Trooper's drone shots hit his own standing body (MphRead PlayerTrooperDev.cs; T5, the body's
//                    hitbox check), each hit shown on the status line
internal sealed partial class CampaignRenderer
{
    bool _solo, _soloOrbit, _prevSoloR3, _soloR3Combo;
    float _orbitYaw, _orbitPitch, _orbitDist;
    float? _orbitStart; // --es orbit: the first orbit, in degrees from behind the player
    bool _orbitPlaced;

    const float OrbitSpeed = 2.6f; // radians a second at full stick
    const float OrbitPitchMin = -1.05f, OrbitPitchMax = 1.35f; // about 60 degrees below to 77 above the player's middle
    const float OrbitDistBody = 3.2f, OrbitDistBall = 2.4f, OrbitMargin = 0.3f;

    // ConfigureMatch, before the settings are checked: one player, Battle, no time limit (the point goal stays: alone,
    // nobody scores)
    void ConfigureSolo(LaunchArgs args, MatchSettings settings)
    {
        PlayerEntity.HostSelfBodyHits = false; // (static: only the solo run that asks for it)
        if (args.Get("solo") != "1" || MphRecomp.Config.RecompSettings.PublicBuild) return;
        _solo = true;
        PlayerEntity.HostSelfBodyHits = args.Get("selfbody") == "1";
        settings.Solo = true;
        settings.Mode = GameMode.Battle;
        // vanilla never plays the expanded Transfer Lock in Battle (MultiplayerMenu's arena table: Bounty, Capture,
        // Defender only), and its Battle layer has no low gravity out in space: the Bounty layer, with a goal nobody
        // reaches (owner 2026-10-10)
        if (settings.Arena == "AD1 TRANSFER LOCK BT")
        {
            settings.Mode = GameMode.Bounty;
            settings.PointGoal = 99;
        }
        if (settings.Players.Count > 1) settings.Players.RemoveRange(1, settings.Players.Count - 1);
        settings.TimeLimitSeconds = -1; // MphRead: a negative match time never runs out (and the HUD leaves it off)
        if (float.TryParse(args.Get("orbit"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float deg))
        {
            _orbitStart = deg;
            _soloOrbit = true;
        }
        SetThirdPerson(true); // whatever RECOMP SETTINGS' camera says (ReloadAimFromSettings keeps it on)
        ConfigurePlacer(args); // --es place 1: the pickup placement editor (PickupPlacer.cs)
        Log.Info("MPHMatch", $"solo run in {settings.Arena} as {settings.Players[0].Hunter}");
    }

    // BuildInput, every sim step: R3 let go (and not half of L3 + R3, the gun-adjust pause) switches orbit / aim.
    // True while the orbit camera has the right stick (no aim from it)
    bool SoloStick(PadState pad)
    {
        if (!_solo) return false;
        bool r3 = pad[PadButton.ThumbR];
        if (r3 && pad[PadButton.ThumbL]) _soloR3Combo = true;
        if (!r3 && _prevSoloR3)
        {
            if (!_soloR3Combo && !_gunComboUsed) // (L3 + R3 can open the pause between two steps: _gunComboUsed)
            {
                _soloOrbit = !_soloOrbit;
                _orbitPlaced = false; // starts from where the view is now
                _statusCountdown = 0;
                Log.Info("MPHMatch", "solo camera: " + (_soloOrbit ? "orbit" : "aim"));
            }
            _soloR3Combo = false;
        }
        _prevSoloR3 = r3;
        if (_soloOrbit)
        {
            float v = _aimInvertY ? -pad.Ry : pad.Ry;
            _orbitYaw -= pad.Rx * OrbitSpeed / 60f;
            _orbitPitch = Math.Clamp(_orbitPitch + v * OrbitSpeed / 60f, OrbitPitchMin, OrbitPitchMax);
        }
        return _soloOrbit;
    }

    // R3 is the camera switch here, not zoom
    bool SoloTakesR3(PadState pad) => _solo && pad[PadButton.ThumbR];

    // UpdateThirdPerson, after each sim step: true when the orbit camera placed the view (Scene.HostView); false hands
    // the view back to the over-the-shoulder camera
    bool UpdateSoloOrbit(Scene scene, PlayerEntity p)
    {
        if (!_solo) return false;
        if (!_soloOrbit || CameraSequence.Current != null || !(MathF.Abs(p.CameraInfo.ViewMatrix.Determinant) > 1e-3f))
        {
            // (the match intro's fly-over and a player without a camera yet keep the game's own view)
            scene.HostView = null;
            _orbitPlaced = false;
            return false;
        }
        Vector3 center = p.Position;
        if (!p.IsAltForm)
        {
            center.Y += (Fixed.ToFloat(p.Values.MinPickupHeight) + Fixed.ToFloat(p.Values.MaxPickupHeight)) / 2 + 0.2f;
        }
        float len = p.IsAltForm ? OrbitDistBall : OrbitDistBody;
        if (!_orbitPlaced)
        {
            // from where the view is now, so the switch doesn't jump (or --es orbit's angle round from behind)
            Vector3 from = scene.CameraPosition - center;
            if (_orbitStart is float deg)
            {
                _orbitStart = null;
                var back = new Vector3(-p.FacingVector.X, 0, -p.FacingVector.Z);
                from = back.LengthSquared > 1e-6f ? back.Normalized() : Vector3.UnitZ;
                _orbitYaw = MathF.Atan2(from.X, from.Z) + MathHelper.DegreesToRadians(deg);
                _orbitPitch = 0.25f;
            }
            else if (from.LengthSquared > 1e-4f)
            {
                from.Normalize();
                _orbitYaw = MathF.Atan2(from.X, from.Z);
                _orbitPitch = Math.Clamp(MathF.Asin(Math.Clamp(from.Y, -1, 1)), OrbitPitchMin, OrbitPitchMax);
            }
            _orbitDist = len;
            _orbitPlaced = true;
        }
        float cp = MathF.Cos(_orbitPitch);
        var dir = new Vector3(MathF.Sin(_orbitYaw) * cp, MathF.Sin(_orbitPitch), MathF.Cos(_orbitYaw) * cp);
        // pulled in where the room's collision is closer (as the over-the-shoulder view): snap in, ease back out
        float allowed = len;
        CollisionResult res = default;
        if (CollisionDetection.CheckBetweenPoints(center, center + dir * (len + OrbitMargin), TestFlags.Players, scene, ref res))
        {
            allowed = Math.Clamp(res.Distance * (len + OrbitMargin) - OrbitMargin, 0.2f, len);
        }
        _orbitDist = allowed < _orbitDist ? allowed : _orbitDist + (allowed - _orbitDist) * 0.15f;
        scene.HostCameraOffset = Vector3.Zero;
        scene.HostView = new HostView(center + dir * _orbitDist, center, Vector3.UnitY, 0);
        return true;
    }

    // the match's status line (MatchStatusText): which camera has the right stick
    string SoloStatusLine() => !_solo ? ""
        : (_soloOrbit ? "\nSOLO  ORBIT CAMERA: right stick circles you  (R3: aim)"
        : "\nSOLO  right stick aims  (R3: orbit camera)") + SelfBodyStatusLine() + PlacerStatusLine();

    // --es selfbody 1: the body's hits so far, how many in the head band, the last one's place, the body's health
    static string SelfBodyStatusLine()
    {
        if (!PlayerEntity.HostSelfBodyHits || PlayerEntity.Players.Count == 0) return "";
        PlayerEntity p = PlayerEntity.Players[0];
        string body = p.Flags2.TestFlag(PlayerFlags2.Halfturret) ? $"body {p.Halfturret.Health} hp" : "drone in (Morph: send it out, shoot the body)";
        return $"\nSELF HITS {p.BodyHits} (head band {p.BodyHeadHits})  last: {(p.BodyLastHit == "" ? "-" : p.BodyLastHit)}  {body}";
    }
}
