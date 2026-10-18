using System;
using MphRecomp.Config;

// The zoom's aim speed and look (the Imperialist; owner 2026-10-16: "make it more fluid and smooth", a zoom sensitivity
// of its own paired with the aim sensitivity by default, and the zoom to behave like vanilla).
//   Speed: MphRead's PlayerInput.ZoomAimFactor is now the game's own (rev 1 arm9 0x020277F4): aim x 1 + Field70 x (half
//   FOV - 39), 0.30 at the Imperialist's zoom and lower as a target pulls the zoom in. That is within ~15% of the
//   focal-length scaling modern shooters use (things under the reticle move across the screen at the same speed zoomed
//   or not), so it's the "more precise at any setting" feel the owner asked for. The host's own speed sits on top of it:
//   paired = the aim sensitivity (RecompSettings.GameSensitivity / MouseSens), unpaired = ZoomSensitivity / ZoomMouseSens.
//   Gyro aim keeps its own sensitivity (GyroMph.ZoomScale).
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

        // CampaignRenderer.ReloadAimFromSettings
        public static void Load(RecompSettings s)
        {
            Paired = s.ZoomSensPaired;
            PadLevel = Math.Clamp(s.ZoomSensitivity, 1, 15);
            MouseSens = MouseScale.Clamp(s.ZoomMouseSens);
            Step = s.ZoomStep;
            MphRead.Entities.PlayerEntity.HostZoomReadyIcon = s.ZoomStep && s.ZoomReadyIcon;
        }

        // the controller's aim scale for an aim sensitivity level 1-15 (CampaignRenderer: 8 = 1.0)
        public static float LevelScale(int level) => 0.5f + Math.Clamp(level, 1, 15) / 16f;

        // the stick's / mouse's scale this frame, from the aim's own (CampaignRenderer._aimScale / _mouseScale)
        public static float PadScale(float aimScale, bool zoomed) => zoomed && !Paired ? LevelScale(PadLevel) : aimScale;
        public static float MouseScaleNow(float mouseScale, bool zoomed) =>
            zoomed && !Paired ? MouseScale.UnitsPerCount(MouseSens) : mouseScale;
    }
}
