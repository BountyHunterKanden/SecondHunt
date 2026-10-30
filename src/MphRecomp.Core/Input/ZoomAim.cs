using System;
using MphRecomp.Config;

// The zoom's aim speed and look (the Imperialist; owner 2026-10-16: "make it more fluid and smooth", a zoom sensitivity
// of its own paired with the aim sensitivity by default, and the zoom to behave like vanilla).
//   Speed: MphRead's PlayerInput.ZoomAimFactor is now the game's own (rev 1 arm9 0x020277F4): aim x 1 + Field70 x (half
//   FOV - 39), 0.30 at the Imperialist's zoom and lower as a target pulls the zoom in. That is within ~15% of the
//   focal-length scaling modern shooters use (things under the reticle move across the screen at the same speed zoomed
//   or not), so it's the "more precise at any setting" feel the owner asked for. The host's own speed sits on top of it:
//   paired = the aim sensitivity (RecompSettings.GameSensitivity / MouseSens), unpaired = ZoomSensitivity / ZoomMouseSens.
//   Gyro aim: the zoom's focal-length correction (GyroMph.ZoomScale), and on top of it paired = the gyro speed,
//   unpaired = GyroZoomSensitivity (finRatman 10-26: faster zoomed gyro than 500% gives, with normal aim unchanged).
//   Touch aim (owner 2026-10-26: "the imperialist sensitivity split ... for all control schemes"): paired = as before (the
//   stylus at TOUCH AIM SPEED, the touch stick at the controller's aim; both follow the controller's imperialist
//   sensitivity while that is set apart), unpaired = TouchZoomSens: the stylus's speed while zoomed, and the touch
//   stick's zoomed speed over TOUCH AIM SPEED (unpairing starts at the same speed either way).
//   Look: the zoom is drawn exactly as the game steps it (2 degrees a frame). An eased "smooth" picture was tried and
//   removed (owner 2026-10-17): it drew the view up to ~50 ms behind the game, i.e. added lag.
namespace MphRecomp.Input
{
    public static class ZoomAim
    {
        public static bool Paired { get; private set; } = true;
        public static int PadLevel { get; private set; } = 8;
        public static float MouseSens { get; private set; } = MouseScale.Default;
        // ZOOM STEP: the player steps the zoom's tiers (CampaignRenderer.BuildInput sends CampaignButtons.ZoomStep)
        public static bool Step { get; private set; }
        // unpaired gyro: the zoomed gyro speed over the normal one (1 when paired)
        public static float GyroRatio { get; private set; } = 1f;
        // the touch controls' own: paired, and the zoomed speed when not (TouchScale's units)
        public static bool TouchPaired { get; private set; } = true;
        public static float TouchZoomSpeed { get; private set; } = TouchScale.Default;

        // CampaignRenderer.ReloadAimFromSettings
        public static void Load(RecompSettings s)
        {
            Paired = s.ZoomSensPaired;
            PadLevel = Math.Clamp(s.ZoomSensitivity, 1, 15);
            MouseSens = MouseScale.Clamp(s.ZoomMouseSens);
            Step = s.ZoomStep;
            GyroRatio = s.GyroZoomPaired || s.GyroSensitivity <= 0 ? 1f
                : Math.Clamp(s.GyroZoomSensitivity, 1, 10000) / (float)s.GyroSensitivity;
            TouchPaired = s.TouchZoomPaired;
            TouchZoomSpeed = TouchScale.Clamp(s.TouchZoomSens);
            MphRead.Entities.PlayerEntity.HostZoomReadyIcon = s.ZoomStep && s.ZoomReadyIcon;
        }

        // the controller's aim scale for an aim sensitivity level 1-15 (CampaignRenderer: 8 = 1.0)
        public static float LevelScale(int level) => 0.5f + Math.Clamp(level, 1, 15) / 16f;

        // the stick's / mouse's scale this frame, from the aim's own (CampaignRenderer._aimScale / _mouseScale)
        public static float PadScale(float aimScale, bool zoomed) => zoomed && !Paired ? LevelScale(PadLevel) : aimScale;
        public static float MouseScaleNow(float mouseScale, bool zoomed) =>
            zoomed && !Paired ? MouseScale.UnitsPerCount(MouseSens) : mouseScale;
        // the gyro's extra scale this frame (CampaignGyroAim; the gyro's degrees already carry the normal gyro speed)
        public static float GyroScale(bool zoomed) => zoomed ? GyroRatio : 1f;

        // the stylus's sensitivity this frame (StylusAim.Step), from TOUCH AIM SPEED and the aim's level 1-15
        public static float StylusSens(float touchSens, int aimLevel, bool zoomed)
        {
            if (zoomed && !TouchPaired) return StylusAim.SpeedSensitivity(TouchZoomSpeed);
            float sens = StylusAim.SpeedSensitivity(touchSens);
            if (zoomed && !Paired) sens *= StylusAim.Sensitivity(PadLevel) / StylusAim.Sensitivity(aimLevel); // as before 10-26
            return sens;
        }

        // the touch stick's scale this frame, from the controller's aim scale (PadScale while paired)
        public static float TouchStickScale(float aimScale, float touchSens, bool zoomed) =>
            zoomed && !TouchPaired ? aimScale * TouchZoomSpeed / TouchScale.Clamp(touchSens) : PadScale(aimScale, zoomed);

        // where an unpaired touch zoom starts: the speed it zoomed at while paired (the stylus's)
        public static float TouchUnpairStart(RecompSettings s)
        {
            float speed = TouchScale.Of(s);
            if (!s.ZoomSensPaired)
            {
                speed *= StylusAim.Sensitivity(s.ZoomSensitivity) / StylusAim.Sensitivity(s.GameSensitivity);
            }
            return TouchScale.Step(speed, 0);
        }
    }
}
