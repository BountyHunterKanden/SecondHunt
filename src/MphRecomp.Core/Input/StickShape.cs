using System;
using MphRecomp.Config;

namespace MphRecomp.Input
{
    // STICKS (CONTROLS > AIM, owner GO 2026-11-16, docs/IDEAS_SURVEY.md): each stick's dead zone and the aim stick's
    // response curve. The hosts (MphRead.Android CampaignActivity, SecondHunt.Desktop DesktopInput) cut each axis below its
    // stick's dead zone as they read the pad, where both used to cut at a fixed 0.15; StickCurve's radial dead zone (the
    // MODERN move ramp) follows the left one. The aim curve shapes the controller's right stick in CampaignRenderer.StickAim
    // (never the touch stick). All of it lands before CampaignInput, so the sim, NetInput and replays see the shaped input
    // as they always saw the pad's (ReplayFile records CampaignInput). The defaults (15%, LINEAR) are the old numbers
    // exactly: the same per-axis cut, no rescale, and LINEAR leaves the stick alone.
    public static class StickShape
    {
        public static readonly int[] DeadZonePercents = { 0, 3, 5, 8, 10, 12, 15, 18, 20, 25, 30, 35, 40 };
        public const int DefaultDeadZone = 15;

        public const string Linear = "linear", Smooth = "smooth", Precise = "precise", Aggressive = "aggressive";
        public static readonly string[] AimCurves = { Linear, Smooth, Precise, Aggressive };

        // read on the hosts' input threads, set by the game thread (CampaignRenderer.ReloadAimFromSettings)
        private static volatile float _left = DefaultDeadZone / 100f, _right = DefaultDeadZone / 100f, _aimPower = 1;

        public static float LeftDeadZone => _left;
        public static float RightDeadZone => _right;
        public static float AimPower => _aimPower;

        // one axis of each stick, as the hosts read it: under the dead zone it is at rest (the old Dead(), at 0.15)
        public static float Left(float v) => Math.Abs(v) < _left ? 0f : v;
        public static float Right(float v) => Math.Abs(v) < _right ? 0f : v;

        public static void Load(RecompSettings s)
        {
            _left = DeadZonePercent(s.LeftStickDeadZone) / 100f;
            _right = DeadZonePercent(s.RightStickDeadZone) / 100f;
            _aimPower = Power(AimCurveName(s.AimCurve));
        }

        // a hand-edited value plays as the default
        public static int DeadZonePercent(int v) => Array.IndexOf(DeadZonePercents, v) >= 0 ? v : DefaultDeadZone;
        public static string AimCurveName(string? v) => v != null && Array.IndexOf(AimCurves, v) >= 0 ? v : Linear;

        // each curve's exponent on the stick's lean: SMOOTH and PRECISE slow the first part of a tilt for fine aim,
        // AGGRESSIVE turns quicker off centre; every curve reaches the same speed at full tilt
        public static float Power(string curve) => curve switch
        {
            Smooth => 1.5f,
            Precise => 2.2f,
            Aggressive => 0.7f,
            _ => 1
        };

        // the aim stick's (x, y) on the curve: the lean (its length) to the power, the direction kept. LINEAR returns it
        // untouched; a lean of 1 or more (full tilt, a square gate's corners) is left as it is
        public static void ShapeAim(ref float x, ref float y) => ShapeAim(ref x, ref y, _aimPower);

        public static void ShapeAim(ref float x, ref float y, float power)
        {
            if (power == 1) return;
            float m = MathF.Sqrt(x * x + y * y);
            if (m <= 0 || m >= 1) return;
            float k = MathF.Pow(m, power) / m;
            x *= k;
            y *= k;
        }
    }

    // a held function as HOLD or TOGGLE (CONTROLS > BUTTONS' "... mode" rows, like "chat mode"): HOLD passes the button
    // through as it is; TOGGLE flips on each press and stays. Turned into the held button before CampaignInput, so the
    // sim and replays see a plain held button either way
    public struct HoldToggle
    {
        private bool _on, _prev;

        public bool Next(bool down, bool toggle)
        {
            if (!toggle)
            {
                _on = false;
                _prev = down;
                return down;
            }
            if (down && !_prev) _on = !_on;
            _prev = down;
            return _on;
        }

        // off; a button still down must be let go and pressed again to turn it back on
        public void Clear()
        {
            _on = false;
            _prev = true;
        }
    }
}
