using System;
using System.Collections.Generic;
using MphRead.Sound;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp (match client, 2026-10-19 online reports): the local player waits for the host before two things it used
    // to do on its own. Only ever used with NetClient on, so a local match (and every replay) is unchanged.
    //   Forms: a form the host's snapshots disagree with is taken from the host only once this device's own last switch has
    //   had a round trip to reach it (NetFormGraceFrames, set from the ping) and the disagreement has lasted NetFormHoldFrames;
    //   it used to be 11 snapshots, so a lag spike after an unmorph put the player back in the ball ("I morphed without
    //   pressing morph"). Each one is reported (NetFormForced) for the device log.
    //   Pickups: an item is taken here only once the host took it for this player (ItemSync puts it in NetConfirmedPickups);
    //   taken here first, a pickup the host's copy of the player was out of range for played its sound and hid the item
    //   while the host kept it and took the gain back ("crates don't have pickups", "pickups differ between screens").
    //   Weapons: the host's snapshot showing a weapon it just took for this player reaches NetApply before the confirmed
    //   pickup runs here, so that pickup saw the weapon as held already: no charge for it, no third slot, the ammo sound
    //   (0.3.7 owner report: "charge beam didnt work for several beams"). A weapon the snapshots turn on gets its charge
    //   there, as every weapon pickup gives it, and for a second still counts as new to this player's confirmed pickup.
    public partial class PlayerEntity
    {
        public static int NetFormGraceFrames { get; set; } = 45;
        public const int NetFormHoldFrames = 30;
        public static Action<PlayerEntity, bool>? NetFormForced { get; set; }
        private long _netFormSwitchFrame = Int64.MinValue / 2;
        private long _netFormMismatchSince = -1;

        public static readonly HashSet<ItemInstanceEntity> NetConfirmedPickups = new(ReferenceEqualityComparer.Instance);

        // recomp (Fruity Prime's pickup claims, docs/FRUITY_PORT.md P3, 2026-11-13; MphRecomp Net/PickupClaims.cs): on a served
        // match the device claims each item its own player touched and the host checks the claim against its own history of
        // that player (the host's copy can jump past an item when the reports come in bursts). One the host took is in here
        // for its next step: that seat's PickUpItems takes it as if in reach (the pickup's own rules still apply)
        public static readonly Dictionary<ItemInstanceEntity, int> HostClaimedPickups = new(ReferenceEqualityComparer.Instance);
        // a device: the items its own player touched this step and where it stood (PickUpItems -> PickupClaims.GuestStep)
        public readonly record struct NetPickupTouch(ItemInstanceEntity Item, Vector3 Position);
        public static readonly List<NetPickupTouch> NetPickupTouches = new();
        public static bool NetPickupClaims { get; set; } // (a served match's device: MatchNetDriver)
        // (and its host honours them, FruitySync.HostPickups: an uncontested touch's sound plays and the item hides at once)
        public static bool NetPickupPredict { get; set; }
        private const int NetPredictHideSteps = 150; // (past a claim's lapse, Net/PickupClaims.cs: the host's word has come by then)
        private const float NetPredictContest = 4; // (another player in play this near the item: it waits for the host)
        private bool _netPickupQuiet; // (PickUpItems: a confirmed pickup whose sound the touch played already)

        // PickUpItems, a served match's device: its own player touched the item and claimed it. Fruity predicts what it hands
        // out locally; the host here still hands out every item (its snapshots bring the gain), so this plays the pickup's
        // sound and hides it at once, and the confirmation gives the gain quietly. Not health (Fruity: the server's alone), and
        // not an item someone else might reach first: those wait for the host, as before
        private void NetPredictPickup(ItemInstanceEntity item)
        {
            SfxId sfx;
            switch (item.ItemType)
            {
            case ItemType.UASmall:
            case ItemType.MissileSmall:
                sfx = SfxId.AMMO_POWER_UP1;
                break;
            case ItemType.UABig:
            case ItemType.MissileBig:
                sfx = SfxId.AMMO_POWER_UP2;
                break;
            case ItemType.DoubleDamage:
            case ItemType.Deathalt:
                sfx = SfxId.DOUBLE_DAMAGE_POWER_UP;
                break;
            case ItemType.Cloak:
                sfx = SfxId.CLOAK_POWER_UP;
                break;
            default:
                BeamType weapon = NetPickupWeapon(item.ItemType);
                if (weapon == BeamType.None && item.ItemType != ItemType.AffinityWeapon)
                {
                    return; // (health, and the campaign's items)
                }
                sfx = weapon != BeamType.None && !_availableWeapons[weapon] ? SfxId.WEAPON_POWER_UP : SfxId.AMMO_POWER_UP1;
                break;
            }
            for (int i = 0; i < PlayerCount; i++)
            {
                PlayerEntity other = Players[i];
                if (other != null && other != this && other.Health != 0 && other.LoadFlags.TestFlag(LoadFlags.Active)
                    && (other.Position - item.Position).LengthSquared < NetPredictContest * NetPredictContest)
                {
                    return;
                }
            }
            if (SoundViewer && Sfx.TimedSfxMute == 0)
            {
                _soundSource.PlayFreeSfx(sfx);
            }
            item.NetPredictTake(NetPredictHideSteps);
        }

        private int _netNewWeapons;
        private long _netNewWeaponsFrame;

        // NetApply: the weapons the host's snapshot has and this player didn't (any seat; every weapon pickup charges)
        private void NetWeaponsGained(int gained)
        {
            for (int i = 0; i < 14; i++)
            {
                if ((gained & (1 << i)) != 0 && !HostFirstHunt) // (First Hunt: the charge is a second pickup, FirstHuntPickUp)
                {
                    _availableCharges[i] = true;
                }
            }
            if (gained != 0 && NetClient && this == Main)
            {
                // (bits older than the second don't come back as new with this one: 11-13)
                if ((long)_scene.FrameCount - _netNewWeaponsFrame >= NetNewWeaponFrames) _netNewWeapons = 0;
                _netNewWeapons |= gained;
                _netNewWeaponsFrame = (long)_scene.FrameCount;
            }
        }

        private const int NetNewWeaponFrames = 60;

        // the weapon pickup, a confirmed one: the weapon came with the snapshot just before it (taken as new once)
        private bool NetNewWeapon(BeamType weapon)
        {
            int bit = 1 << (int)weapon;
            if (!NetClient || (_netNewWeapons & bit) == 0)
            {
                return false;
            }
            _netNewWeapons &= ~bit;
            return (long)_scene.FrameCount - _netNewWeaponsFrame < NetNewWeaponFrames;
        }

        // recomp (post-0.5.1 Omega Cannon reports, 11-13): whose a weapon item gone on the host was. ItemSync gave it to this
        // player by position alone (TakenByLocal: the predicted self against the puppets' older poses), so a contested
        // pickup someone else got played the sound here with no weapon ("I touched it, got the sound and it's not there"),
        // and one this player got could be missed. A weapon settles it by the host's snapshot: it just came (this
        // player's), this player still hasn't got it (someone else's); held already (the host took it as ammo) or no
        // weapon: null, the position decides
        public bool? NetWeaponPickupMine(ItemType itemType)
        {
            BeamType weapon = NetPickupWeapon(itemType);
            if (weapon == BeamType.None)
            {
                return null;
            }
            if ((_netNewWeapons & (1 << (int)weapon)) != 0 && (long)_scene.FrameCount - _netNewWeaponsFrame < NetNewWeaponFrames)
            {
                return true;
            }
            return _availableWeapons[weapon] ? null : false;
        }

        // the weapon a pickup gives this player (PickUpWeapon's), else None (the Samus / Guardian affinity pickup: ammo)
        private BeamType NetPickupWeapon(ItemType itemType)
        {
            if (itemType == ItemType.AffinityWeapon)
            {
                return HostAffinityBeam == null && (Hunter == Hunter.Samus || Hunter == Hunter.Guardian) ? BeamType.None : AffinityBeam;
            }
            return itemType switch
            {
                ItemType.VoltDriver => BeamType.VoltDriver,
                ItemType.Battlehammer => BeamType.Battlehammer,
                ItemType.Imperialist => BeamType.Imperialist,
                ItemType.Judicator => BeamType.Judicator,
                ItemType.Magmaul => BeamType.Magmaul,
                ItemType.ShockCoil => BeamType.ShockCoil,
                ItemType.OmegaCannon => BeamType.OmegaCannon,
                ItemType.PirateBlaster => BeamType.PirateBlaster,
                ItemType.Paralyzer => BeamType.Paralyzer,
                ItemType.HyperBeam => BeamType.HyperBeam,
                _ => BeamType.None
            };
        }

        // recomp (11-13): a weapon item the host spawned and this player took in the same step is in no snapshot (the host
        // sends the items lying there), so the weapon came here with no pickup and no sound ("it won't spawn even if they're
        // on top of the pickup alone"; 5 of 25 Omega Cannons in one served Oubliette match). A new weapon with this player
        // at a spawner of it that has nothing lying there plays the spawn and the pickup's sound; the host's snapshots bring
        // the weapon, its charge and the equip (no switch here: it would be pushed to the host as a press), shown as the
        // pickup's own (NetShowEquip). ItemSync.Apply, once the items match the host's
        public void NetUnseenWeaponPickups()
        {
            int bits = _netNewWeapons & ~(1 << (int)BeamType.PowerBeam | 1 << (int)BeamType.Missile);
            if (!NetClient || this != Main || bits == 0 || HostFirstHunt || _health == 0 || !LoadFlags.TestFlag(LoadFlags.Active)
                || (long)_scene.FrameCount - _netNewWeaponsFrame >= NetNewWeaponFrames)
            {
                return;
            }
            const float reach = 3; // (ItemSync.ConfirmReach)
            foreach (ItemSpawnEntity spawner in _scene.GetItemSpawnEntities())
            {
                BeamType weapon = NetPickupWeapon(spawner.Data.ItemType);
                int bit = weapon == BeamType.None ? 0 : 1 << (int)weapon;
                Vector3 at = spawner.Position.AddY(0.65f); // (where it puts its item)
                if ((bits & bit) == 0 || (Position - at).LengthSquared > reach * reach)
                {
                    continue;
                }
                bool lying = false;
                foreach (ItemInstanceEntity item in _scene.GetItemInstanceEntities())
                {
                    if (item.DespawnTimer != 0 && item.ItemType == spawner.Data.ItemType && (item.Position - at).LengthSquared < reach * reach)
                    {
                        lying = true; // (taken there: its confirmation is still to come)
                        break;
                    }
                }
                if (lying)
                {
                    continue;
                }
                _netNewWeapons &= ~bit;
                bits &= ~bit;
                spawner.NetSpawnSfx();
                if (SoundViewer && Sfx.TimedSfxMute == 0)
                {
                    _soundSource.PlayFreeSfx(SfxId.WEAPON_POWER_UP);
                }
                NetShowEquip(weapon);
            }
        }

        // recomp (owner 11-13: "it switches beam but there's no arm cannon animation, at least for the Omega Cannon, when you
        // first step onto it"): the host's equip of an unseen pickup came with its snapshot, silent (PlayerNet.cs NetApply);
        // the pickup's own (TryEquipWeapon not silent) plays the hunter's switch sound and the gun's switch. Shown here as
        // that: nothing changes and nothing is sent
        private void NetShowEquip(BeamType weapon)
        {
            if (CurrentWeapon != weapon)
            {
                return; // (not equipped yet: the local player's own select is on its way to the host)
            }
            if (SoundViewer && !IsAltForm && weapon != BeamType.Missile)
            {
                int sfx = Metadata.HunterSfx[(int)Hunter, (int)HunterSfx.BeamSwitch];
                if (sfx != -1)
                {
                    _soundSource.PlayFreeSfx(sfx);
                }
            }
            if (weapon == BeamType.Missile)
            {
                SetGunAnimation(GunAnimation.MissileOpen, AnimFlags.NoLoop);
            }
            else if (PreviousWeapon == BeamType.Missile)
            {
                SetGunAnimation(GunAnimation.MissileClose, AnimFlags.NoLoop);
            }
            else
            {
                CurrentWeapon = WeaponSelection = PreviousWeapon;
                SetGunAnimation(GunAnimation.Switch, AnimFlags.NoLoop);
                CurrentWeapon = WeaponSelection = weapon;
            }
        }

        // recomp (11-13): a puppet's Omega Cannon shot. The host's snapshot of the shot drops the cannon and brings the FIRE
        // press, which runs here a step later; NetApply unequipped it first, so the shot here was the next weapon's ("the
        // shooter sees the Omega Cannon but the victims don't"). And the press alone wasn't the host's shot: this copy's own
        // cooldown, gun and timing aren't the host's, so under lag a press the host fired on was refused here, or one it
        // refused fired here. A puppet's cannon fires only when, and as soon as, the host's snapshot drops it: NetApply
        // keeps it for that shot (at most half a second; PlayerInput.cs fires it past its cooldown, the host's call), and a
        // shot that killed its own shooter (the blast hurts everyone in it) is fired before the death that came with it
        private long _netOmegaSpentFrame = -1;
        private const int NetOmegaHoldFrames = 30;

        // NetApply, a puppet's cannon gone on the host: whether it keeps it for the shot still to run here
        private bool NetOmegaHold()
        {
            long now = (long)_scene.FrameCount;
            if (_netOmegaSpentFrame < 0)
            {
                _netOmegaSpentFrame = now;
            }
            if (now - _netOmegaSpentFrame < NetOmegaHoldFrames)
            {
                return true;
            }
            _netOmegaSpentFrame = -1; // (the shot couldn't run here)
            return false;
        }

        // the fire check (PlayerInput.cs), a network client's puppet with the cannon out: whether the host's shot is due
        // here (null: not one, its press decides)
        private bool? NetOmegaPuppetShot()
        {
            if (!NetClient || this == Main || CurrentWeapon != BeamType.OmegaCannon)
            {
                return null;
            }
            return _netOmegaSpentFrame >= 0;
        }

        // the shot's end (PlayerInput.cs), a network client: whether this seat unequips its cannon now (a held puppet's)
        private bool NetOmegaShotHere()
        {
            if (this == Main || CurrentWeapon != BeamType.OmegaCannon || _netOmegaSpentFrame < 0)
            {
                return false;
            }
            _netOmegaSpentFrame = -1;
            return true;
        }

        // NetApply, before the snapshot's damage: a puppet alive here with its cannon out that the snapshot shows dead
        // without it (death keeps the cannon until the respawn, so it fired) fires it now
        private void NetOmegaShotBeforeDeath(in PlayerNetState s, int heldBits)
        {
            if (!NetClient || this == Main || CurrentWeapon != BeamType.OmegaCannon || !_availableWeapons[BeamType.OmegaCannon]
                || _health == 0 || s.Health > 0 || (s.Flags & PlayerNetState.Spawned) == 0
                || (heldBits & (1 << (int)BeamType.OmegaCannon)) != 0)
            {
                return;
            }
            WeaponInfo omega = Weapons.Current[(int)BeamType.OmegaCannon];
            _ammo[omega.AmmoType] = Math.Max(_ammo[omega.AmmoType], omega.AmmoCost); // (the snapshot's is after the shot)
            _netOmegaSpentFrame = (long)_scene.FrameCount;
            if (!TryFireWeapon())
            {
                _netOmegaSpentFrame = -1;
            }
        }

        // recomp (11-22, owner: "when the samsung respawned and was walking there was a white flash on the screen
        // like someone had fired omega cannon"): a match client runs each Omega Cannon shot as its own copy (a puppet's
        // above, its own player's as pressed), and the copy flies on by itself (8.5 s at a slow 6-15 units/s). The host's
        // blast killed this player, the copy here passed through the dead body and blew up seconds later, after the
        // respawn, and vanilla flashes the screen white for any Omega blast away from you (BeamProjectileEntity
        // OmegaCannonFlash). Now the host's word of a hit by a seat's Omega Cannon sets it off here: that seat's shot in
        // flight, or one it fires within a second (the cannon's drop and the hit in one snapshot), blows up where it is.
        // Damage stays the host's (NetDamageAllowed); replays (the host's own simulation) never run this.
        private long _netOmegaBlastFrame = -1;
        private const int NetOmegaBlastFrames = 60;
        public static bool NetOmegaBlastOff; // (Tools -onlinetest omegablast=0: the old way, for the before/after check)
        public static int NetOmegaBlasts; // (how many shots blew up at the host's word here; Tools)

        // NetApply, a match client: the host says this seat's Omega Cannon hit someone
        private void NetOmegaBlastDue()
        {
            if (NetClient && !NetOmegaBlastOff) _netOmegaBlastFrame = (long)_scene.FrameCount;
        }

        // BeamProjectileEntity.Process, a match client: whether this seat's Omega shot here blows up now (the host's blast
        // came for it; once)
        public bool NetOmegaBlastNow()
        {
            if (!NetClient || _netOmegaBlastFrame < 0)
            {
                return false;
            }
            bool due = (long)_scene.FrameCount - _netOmegaBlastFrame <= NetOmegaBlastFrames;
            _netOmegaBlastFrame = -1;
            if (due) NetOmegaBlasts++;
            return due;
        }

        // TrySwitchForms, a switch made: the local seat's grace starts again (its own press, or a forced one)
        private void NetFormSwitched()
        {
            if (NetClient && this == Main) _netFormSwitchFrame = (long)_scene.FrameCount;
        }

        // NetApply, the local seat in another form than the host's: whether to take the host's now
        private bool NetFormCorrectionDue()
        {
            long now = (long)_scene.FrameCount;
            if (_netFormMismatchSince < 0) _netFormMismatchSince = now;
            return now - _netFormSwitchFrame >= NetFormGraceFrames && now - _netFormMismatchSince >= NetFormHoldFrames;
        }
    }
}
