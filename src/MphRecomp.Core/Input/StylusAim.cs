using System;
using System.Numerics;

// The DS's stylus aim, for the touch controls' TOUCH AIM: STYLUS (owner 2026-10-16: "the original vanilla stylus aiming as
// an option for touch mobile users ... much more snappy and responsive than a virtual control stick"). Read from US rev 1
// arm9 by disassembly only (not emulator-checked):
//   biped aim 0x02024308: while the controls' stylus-aim flag is set (control types 0/1, stylus) and the stylus has been
//     held for more than AimMinTouchTime ticks (5 for every hunter; the hold counter is 1 on the press tick):
//     yaw in = dx4 * -2211/4096, pitch in = dy4 * -1138/4096, dx4/dy4 = the stylus movement summed over the last 4 ticks
//     (touch block 0x02029778, zeroed while not held past the press tick; MphRead PlayerTouch.cs)
//   UpdateAimX/Y 0x02027E40 / 0x020277BC: amount (degrees) = in * sensitivity, the OPTIONS slider's 0.6 + 0.1 x index
//     (overlay 0 0x2136e6c, index 0-14, default 6 = 1.2); invert flips pitch only; the zoom factor applies inside, as
//     for every input (MphRead ZoomAimFactor)
//   no dead zone, acceleration or clamp: a pixel of travel turns the view 4 x factor x sensitivity in all (2.59 degrees
//   of yaw, 1.33 of pitch at the default), spread over the 4 ticks it stays in the sum.
// At 60 Hz the 4 ticks are 8 frames (each frame's sum counts half) and the hold has to pass 10 frames. The stylus here is
// the finger's drag in DS touch-screen pixels by physical size (MatchTouchOverlay.DsPerPixel), kept unrounded: the same
// rule, finer steps than the DS's whole pixels.
// Direct (the default since 2026-10-18, from a player's report: "a pretty notable input delay with touch screen stylus
// aim ... feels like I'm back to playing melonDS" and "quick sweeps ... won't register. Gotta hold down on the touch
// screen for a moment"): both are the DS's rule, the 10-frame hold and the 8-frame sum. Direct turns each frame's drag
// at once by the same angle per pixel in all, with no hold, and keeps the drag of a lift between steps. Vanilla = true
// is the DS's rule as above.
namespace MphRecomp.Input
{
    public sealed class StylusAim
    {
        private const int Window = 4 * 2; // dx4/dy4's 4 ticks
        private const int MinHoldFrames = 5 * 2; // AimMinTouchTime: aim once the hold counter passes 5 ticks
        public const float YawFactor = 2211 / 4096f;
        public const float PitchFactor = 1138 / 4096f;

        private readonly Vector2[] _ring = new Vector2[Window];
        private int _index;
        private int _holdFrames;

        // the DS's rule (hold + 4-tick sum) instead of the direct turn
        public bool Vanilla { get; set; }

        // TOUCH AIM Y SPEED (owner 2026-10-26: "up and down aim isnt as sensitive"): the pitch's speed as a share of the
        // yaw's, RecompSettings.TouchPitchPercent; 0 = the DS's own (PitchFactor / YawFactor, 51%), as before
        public static readonly int[] PitchPercents = [0, 60, 70, 80, 90, 100, 110, 125, 150];
        public float PitchScale { get; set; } = 1f;
        public static float PitchScaleFor(int percent) => percent <= 0 ? 1f : Math.Clamp(percent, 10, 300) / 100f * YawFactor / PitchFactor;
        public static string PitchText(int percent) => percent <= 0 ? "DS" : percent + "%";
        // a stick's or the mouse's Y SPEED (RecompSettings.AimPitchPercent / MousePitchPercent, and TouchPitchPercent on
        // the touch stick, where 0 = 100%): up/down as a percent of left/right, 100 = the same
        public static readonly int[] YPercents = [50, 60, 70, 80, 90, 100, 110, 125, 150, 175, 200];
        public static float YScale(int percent) => percent <= 0 ? 1f : Math.Clamp(percent, 10, 300) / 100f;

        // the OPTIONS slider's sensitivity for our level 1-15 (RecompSettings.GameSensitivity; the DS's index + 1)
        public static float Sensitivity(int level) => 0.6f + 0.1f * (Math.Clamp(level, 1, 15) - 1);

        // TOUCH AIM SPEED (Config/TouchScale.cs, thread D 10-24) as the slider's multiplier: a DS pixel here is 61.4 / 256 mm
        // of the finger's travel (MatchTouchOverlay.DsPerPixel) and turns 4 x YawFactor x sensitivity degrees of yaw, so
        // speed 1 = TouchScale.DegreesPerCm a cm (pitch keeps the DS's ratio)
        public const float DsPixelMm = 61.4f / 256;
        public static float SpeedSensitivity(float speed) =>
            MphRecomp.Config.TouchScale.Clamp(speed) * MphRecomp.Config.TouchScale.DegreesPerCm / 10 * DsPixelMm / (4 * YawFactor);

        // one simulation step: the stylus is down, and how far it moved since the last step (DS pixels, down = +y). Returns
        // the aim in CampaignInput.AimDelta's units (MphRead's mouse units: 4 a degree; +x turns right, +y looks down).
        public Vector2 Step(bool down, Vector2 moved, float sensitivity, bool invertY)
        {
            if (!Vanilla)
            {
                // the whole angle a pixel earns (4 ticks in the sum) on the frame it's dragged; 4 mouse units a degree
                if (!down)
                {
                    Reset();
                }
                float dx = moved.X * 4 * YawFactor * sensitivity * 4;
                float dy = moved.Y * 4 * PitchFactor * PitchScale * sensitivity * 4;
                return new Vector2(dx, invertY ? -dy : dy);
            }
            if (!down)
            {
                Reset();
                return Vector2.Zero;
            }
            _holdFrames++;
            if (_holdFrames == 1)
            {
                moved = Vector2.Zero; // the press frame adds nothing (the game zeroes the sum until held past it)
            }
            _ring[_index] = moved;
            _index = (_index + 1) % Window;
            if (_holdFrames <= MinHoldFrames)
            {
                return Vector2.Zero;
            }
            Vector2 sum = Vector2.Zero;
            foreach (Vector2 v in _ring)
            {
                sum += v;
            }
            // degrees per 30 Hz tick = sum x factor x sensitivity; half that each 60 Hz frame; 4 mouse units a degree
            float x = sum.X * YawFactor * sensitivity / 2 * 4;
            float y = sum.Y * PitchFactor * PitchScale * sensitivity / 2 * 4;
            return new Vector2(x, invertY ? -y : y);
        }

        public void Reset()
        {
            Array.Clear(_ring);
            _index = 0;
            _holdFrames = 0;
        }
    }
}
