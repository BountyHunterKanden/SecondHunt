namespace MphRead.Entities
{
    // recomp: the muzzle flash and the charge glow belong to their player (EffectEntry.SetHostOwner -> RenderItem.HostOwner),
    // so the app's smooth frames (CampaignSmoothFrames.cs) draws them with the player's view, the same as the gun and the
    // arm cannon. Without it they stayed at the 60 Hz step's place while the gun was carried on, and trailed behind it while
    // moving (2026-10-19). Draw only; called after Draw (Scene.GetDrawItems). Every effect that follows a hunter works the
    // same way.
    public partial class PlayerEntity
    {
        public void HostTagFx()
        {
            _muzzleEffect?.SetHostOwner(this);
            _chargeEffect?.SetHostOwner(this);
            // the ones that follow the player each step too (Transform in PlayerProcess/PlayerInput): the double damage glow at
            // the muzzle, the boost ball dash, the morph ball furl, the burn after a Magmaul hit, the alt form death
            _doubleDmgEffect?.SetHostOwner(this);
            _boostEffect?.SetHostOwner(this);
            _furlEffect?.SetHostOwner(this);
            _burnEffect?.SetHostOwner(this);
            _deathaltEffect?.SetHostOwner(this);
        }
    }
}
