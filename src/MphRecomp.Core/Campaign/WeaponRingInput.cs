using System.Numerics;
using MphRead;
using MphRead.Entities;

namespace MphRecomp.Campaign
{
    // The weapon ring's stick handling (owner queue #9, 2026-10-03; built 2026-10-09). The owner's design: hold the weapon
    // menu button (pad L2) -> a full-circle ring (MphRead PlayerWeaponRing.cs draws it) -> point the right stick at a
    // weapon -> it shows as selected and STAYS selected when the stick goes back to rest -> let go of L2 to equip it. A
    // direction still held when L2 is let go must not turn the camera; moving works all the while. Input-side only: what
    // reaches CampaignInput.WeaponWheel is the remembered slot's direction, so the sim (MphRead's own pick, from
    // CampaignHost.SetWheelPointer) and replays work as before. The pad (CampaignRenderer.BuildInput) and the touch
    // controls' WEAPON button (which plays the pad) both go through it.
    public sealed class WeaponRingInput
    {
        public const float PickRadius = 0.5f; // how far the stick leans to point at a slot (SetWheelPointer's 0.25 squared)
        public const float RestRadius = 0.2f; // how close to the middle counts as back at rest (stick aim comes back)
        // with a weapon that zooms equipped (the Imperialist) the button has to be held this many sim steps (0.25 s; 10
        // was "a touch" short, owner 2026-10-09) before the ring opens; a shorter press is a tap = zoom, so zoom and ring
        // share L2 (owner 2026-10-09). With any other weapon the ring opens on the press
        public const int HoldFrames = 15;
        private int _sector = -1; // the slot picked since the ring opened, -1 none yet
        private bool _open;
        private int _heldFrames;

        // the ring closed with the stick still leaning: the stick doesn't aim until it has been back at rest
        public bool StickAimHeld { get; private set; }

        // T4 N15: the step the ring closes with no slot picked while it shows the Omega Cannon in its middle (MphRead
        // PlayerEntity.HostRingOmegaShown), the Omega Cannon: send it as CampaignInput.SelectWeapon (its own key, as the
        // DS's touch icon equips it). BeamType.None otherwise
        public BeamType CentrePick { get; private set; } = BeamType.None;

        // one sim step of the button itself (before Step): true while the ring is open (held HoldFrames or longer).
        // `zoom` is true on the step a tap is let go with a weapon that zooms equipped: send CampaignButtons.Zoom for that
        // one step (MphRead toggles the zoom on its press)
        public bool Hold(bool held, PlayerEntity player, out bool zoom)
        {
            zoom = false;
            bool canZoom = player.EquipInfo.Weapon != null && (player.EquipInfo.Weapon.Flags & MphRead.WeaponFlags.CanZoom) != 0;
            if (held)
            {
                // without a weapon that zooms there's nothing to tap for: the ring opens at once (owner 2026-10-09)
                if (!canZoom)
                {
                    _heldFrames = HoldFrames;
                }
                else if (_heldFrames < HoldFrames)
                {
                    _heldFrames++;
                }
                return _heldFrames >= HoldFrames;
            }
            zoom = _heldFrames > 0 && _heldFrames < HoldFrames && canZoom;
            _heldFrames = 0;
            return false;
        }

        // one sim step: `open` = the weapon menu button is held, `stick` = the right stick (+X right, +Y up), `tap` = a slot
        // tapped on the ring this step (the touch controls; -1 = its middle, which drops the pick; -2 = none). Returns the
        // direction to send as CampaignInput.WeaponWheel (zero = nothing picked).
        public Vector2 Step(bool open, Vector2 stick, PlayerEntity player, int tap = -2)
        {
            bool leaning = stick.LengthSquared() > RestRadius * RestRadius;
            CentrePick = BeamType.None;
            if (open)
            {
                if (!_open)
                {
                    _sector = -1;
                }
                _open = true;
                StickAimHeld = false;
                if (stick.LengthSquared() > PickRadius * PickRadius)
                {
                    int sector = CampaignHost.WheelSector(stick);
                    // a slot you have no weapon for (MphRead greys it) doesn't take the pick away from one you have
                    if (player.AvailableWeapons[CampaignHost.WheelWeapon(sector)])
                    {
                        _sector = sector;
                    }
                }
                if (tap == -1)
                {
                    _sector = -1;
                }
                else if (tap >= 0 && player.AvailableWeapons[CampaignHost.WheelWeapon(tap)])
                {
                    _sector = tap;
                }
                return _sector < 0 ? Vector2.Zero : CampaignHost.WheelDirection(_sector);
            }
            if (_open)
            {
                _open = false;
                StickAimHeld = leaning;
                if (_sector < 0 && player.HostRingOmegaShown && player.CurrentWeapon != BeamType.OmegaCannon)
                {
                    CentrePick = BeamType.OmegaCannon;
                }
            }
            else if (!leaning)
            {
                StickAimHeld = false;
            }
            _sector = -1;
            return Vector2.Zero;
        }

        // everything let go (a dialog, the pause menu, a new room's player)
        public void Reset()
        {
            _sector = -1;
            _open = false;
            _heldFrames = 0;
            CentrePick = BeamType.None;
            StickAimHeld = false;
        }
    }
}
