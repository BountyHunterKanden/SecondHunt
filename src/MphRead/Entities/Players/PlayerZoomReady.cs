namespace MphRead.Entities
{
    // recomp: ZOOM STEP's target zoom cue on the reticle itself (owner 2026-10-17: in place of a separate corner icon, "make
    // the regular imperialist reticle transparent by default and become solid when the player can zoom again"). In the
    // first zoom the reticle is faint; it turns solid as soon as HostZoomTargetReady (vanilla would zoom past the first zoom
    // now, so zoom in takes the target zoom) and stays solid in the target zoom. Unzoomed it's the game's own. Draw only: it
    // reads the same check the sim uses (PlayerInput.ZoomTargetHalfFov), so it shows what a press would do.
    public partial class PlayerEntity
    {
        // the host's ZOOM STEP is on with its reticle cue (MphRecomp Input/ZoomAim.cs, from RecompSettings)
        public static bool HostZoomReadyIcon { get; set; }

        private const float _zoomReadyFaint = 0.4f; // the reticle's opacity while the target zoom isn't ready

        // PlayerHud.DrawHudObjects: the reticle's opacity, times Features.ReticleOpacity
        private float ZoomReadyReticleAlpha()
        {
            if (!HostZoomReadyIcon || !EquipInfo.Zoomed || _hostZoomTarget || HostZoomTargetReady)
            {
                return 1;
            }
            return _zoomReadyFaint;
        }
    }
}
