using System;
using System.Collections.Generic;
using MphRead.Entities.FirstHunt;
using MphRead.Sound;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: First Hunt's player rules (docs/FIRST_HUNT_PORT.md P1), on MphRead's player while a First Hunt level runs
    // (HostFirstHunt; values and weapons: FirstHuntRules). Read from the cart's arm9 (docs/first_hunt/gameplay.md):
    //   - spawn (0x2009dc8/0x200a0c8): energy 99 of 599, ammo 99, only the Power Beam, no charge shots, double damage off
    //   - one ammo pool for all three weapons (player +0xe0; the Power Beam costs 1 too) = MphRead's missile pool (1);
    //     at 0 it trickles back 1 every 15 frames (0x2007a08), and Electro Lob / Missile switch to the Power Beam when
    //     the pool is under their cost
    //   - pickups (0x200c300, switch 0x200c3fc): a 1.35 sphere around the item, never refused; a weapon's second pickup
    //     enables its charge shot (+0x438)
    // Sounds are MPH's nearest until First Hunt's (P4); HUD messages come with the HUD (P3).
    public partial class PlayerEntity
    {
        public static bool HostFirstHunt { get; set; }

        private int _fhTrickle = 0;

        // for a host or a test: the one ammo pool, and whether a weapon's charge shot is on
        public int FirstHuntAmmoPool { get => _ammo[Missiles]; set => _ammo[Missiles] = value; }
        public bool FirstHuntCharge(BeamType beam) => _availableCharges[beam];
        public ushort DoubleDamageTimer => _doubleDmgTimer;
        // the HUD's (FirstHuntHud): double damage on, bombs ready, the boost gauge's frame (0 = not charging, 1-4)
        public bool FirstHuntDoubleDamage => _doubleDmgTimer > 0;
        public int FirstHuntBombs => _bombAmmo;
        public int FirstHuntBoostFrame => _boostCharge == 0 ? 0 : 1 + Math.Min(3, _boostCharge * 4 / (Values.BoostChargeMax * 2 + 1));

        // the MPH HUD parts First Hunt's HUD keeps (FirstHuntHud draws the rest): the reticle (the cart's sprite isn't
        // found yet) and our weapon menu
        public void DrawFirstHuntHudObjects()
        {
            if (Health == 0 || FirstHuntMission.Ending || GameState.DialogPause)
            {
                return;
            }
            if (Flags1.TestFlag(PlayerFlags1.WeaponMenuOpen))
            {
                if (HostWeaponRing)
                {
                    DrawWeaponRing();
                }
                else if (WeaponSlotCount == 7)
                {
                    DrawWeaponArc7();
                }
                else
                {
                    for (int i = 0; i < 6; i++)
                    {
                        _scene.DrawHudObject(_selectBoxInsts[i], mode: 1);
                        _scene.DrawHudObject(_weaponSelectInsts[i], mode: 1);
                    }
                }
            }
            else if (!IsAltForm && !IsMorphing && !IsUnmorphing && !ScanVisor && !HostHideReticle)
            {
                _targetCircleInst.Alpha = Features.ReticleOpacity;
                _scene.DrawHudObject(_targetCircleInst);
            }
        }

        // the cart's arm cannon, not MPH's SamusGun: gun_idle + gun_Anim, set up at player +0xe8 (0x200a4b8) with clip 3
        private static ModelInstance FirstHuntGunModel() => Read.GetModelInstance("gun_idle", firstHunt: true);

        // the cart's body, not MPH's Samus: samus_hi_* (and samus_low_* far away; the slot's colour is a recolor) on
        // samus_Anim, legs and torso layers split at Spine_2 as MPH's at Spine_1 (0x200b2ac). Its 28 clips are put in
        // MPH's PlayerAnimation order once per loaded animation set, so the shared player code picks them by MPH's
        // numbers (the cart's clip picker 0x200f728 is MPH's ancestor): 26 the cart's death collapse (0x200bc58) and 27
        // its other turn direction, which MPH has no use for. The cart turns right on clip 23 (0x200f924, yaw speed going
        // down) and left on 22 (0x200f9d8); MPH has one Turn for both, the same swing as the cart's 23 (-fhgame turn:
        // spine 93 -> 69 -> 93), so 23 takes Turn's slot. todo: the left turn on 22 (MPH plays Turn reversed there)
        private static readonly int[] _fhBodyClips =
        {
            1, 16, 17, 24, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 18, 19, 20, 21, 23, 25, 26, 27, 0, 22
        };
        private static readonly HashSet<object> _fhBodyOrdered = new HashSet<object>();

        private static ModelInstance FirstHuntBodyModel(int lod)
        {
            ModelInstance inst = Read.GetModelInstance(lod == 0 ? "samus_hi_yellow" : "samus_low_yellow", firstHunt: true);
            AnimationGroups groups = inst.Model.AnimationGroups;
            InMphOrder(groups.Node);
            InMphOrder(groups.Material);
            InMphOrder(groups.Texcoord);
            InMphOrder(groups.Texture);
            return inst;
        }

        private static void InMphOrder<T>(IReadOnlyList<T> clips)
        {
            if (clips is List<T> list && list.Count == _fhBodyClips.Length && _fhBodyOrdered.Add(list))
            {
                var cart = new List<T>(list);
                for (int i = 0; i < list.Count; i++)
                {
                    list[i] = cart[_fhBodyClips[i]];
                }
            }
        }

        // the cart's Morph Ball, not MPH's SamusAlt: morphBall, its four colours (yellow, green, white, blue) the recolors
        // a player's slot picks, as the colours of samus_hi_* (0x200ad28 loads all four of each)
        private static ModelInstance FirstHuntBallModel()
        {
            FhEffectEntity.Preload("spawnEffect"); // (FirstHuntSpawn's; the cart loads it with the level's effects, 0x2021c78)
            return Read.GetModelInstance("morphBall", firstHunt: true);
        }

        // its 12 clips, which the gun update (0x20124c8) sets by index through 0x2012470: MPH's GunAnimation order without
        // Switch (0-3 FullCharge ChargeShot Charging Idle, 4-9 the missile clips, 10 UpDown, 11 Shot; 4-9 set the open-gun
        // flag like MPH's 5-10). The cart doesn't animate a beam switch: Switch keeps or restarts Idle
        private static int FirstHuntGunAnimId(GunAnimation anim)
        {
            return anim == GunAnimation.Switch ? 3 : anim < GunAnimation.Switch ? (int)anim : (int)anim - 1;
        }

        // MphRead Spawn's end (every spawn)
        private void FirstHuntSpawn()
        {
            if (!HostFirstHunt)
            {
                return;
            }
            int tank = Values.EnergyTank;
            _healthMax = 6 * tank - 1; // (0x200a9a4; no tanks in First Hunt)
            // 3 tanks for a player with the cart's +0x42c flag (0x200a814), which the player setup (0x200b270) gives only
            // slots above 0 (and below the player count) while the match block's mode 0x209b92c is 1, the Regulator; never
            // in Survivor (2), the Morph Ball course (0) or multiplayer (3): the main player starts every mode on one tank
            // (Regulator: 99 of 599, read from the cart in BizHawk)
            bool threeTanks = FirstHuntMission.Mode == FhMode.Regulator && SlotIndex > 0;
            _health = (threeTanks ? 3 : 1) * tank - 1;
            if (threeTanks)
            {
                FirstHuntMission.MusicCue(4); // (0x200a830)
            }
            _availableWeapons.ClearAll();
            _availableCharges.ClearAll();
            _availableWeapons[BeamType.PowerBeam] = true;
            _weaponSlots[0] = BeamType.PowerBeam;
            _weaponSlots[1] = BeamType.Missile;
            _weaponSlots[2] = FirstHuntRules.ElectroLob;
            _ammo[UA] = 0;
            _ammoMax[Missiles] = Values.MpAmmoCap;
            _ammo[Missiles] = Values.Field106 - 1;
            _fhTrickle = 0;
            _doubleDmgTimer = 0;
            PreviousWeapon = BeamType.PowerBeam;
            TryEquipWeapon(BeamType.PowerBeam, silent: true);
            // the spawn point (Spawn puts the player 1 above it, as the cart does: both fall to the floor from there)
            Vector3 point = Position.AddY(-1);
            bool ball = FirstHuntMission.Mode == FhMode.Morphball;
            if (ball)
            {
                // the cart's spawn (0x200a678): in the Morph Ball course (mode byte 0x209b92c = 0) the player starts as the
                // ball (+0x430 bit 4) 2 lower (it then settles half a unit, as the cart's), no morph clip, sound or furl:
                // EnterAltForm's camera and roll set-up, then its end at once as when the Morph clip ends (Process),
                // without UpdateForm's lift
                Vector3 start = Position.AddY(-2);
                _altRollFbX = _field70;
                _altRollFbZ = _field74;
                _altRollLrX = _gunVec2.X;
                _altRollLrZ = _gunVec2.Z;
                SwitchCamera(Values.AltFormStrafe != 0 ? CameraType.Third2 : CameraType.Third1, new Vector3(_field70, 0, _field74));
                _modelTransform.Row3.Xyz = Vector3.Zero;
                UpdateForm(altForm: true);
                Position = PrevPosition = IdlePosition = start;
            }
            // the cart's spawn effect (0x200a834, effect 0x16): the spawnEffect model at the spawn point on every spawn, the
            // first included, 2 lower in the Morph Ball course; not MPH's spawnEffect particles
            _scene.AddEntity(FhEffectEntity.Create(_scene, ball ? point.AddY(-2) : point, "spawnEffect"));
        }

        // MphRead ProcessPlayer's respawn: the cart has no "press FIRE": outside multiplayer it spawns at once; in a
        // deathmatch the first spawn too, and a respawn 90 ticks after the death (player +0x3f4, 0x200bc1c / 0x200780c)
        private bool FirstHuntSpawnNow => HostFirstHunt && (FirstHuntMission.Mode != FhMode.Multiplayer
            || !LoadFlags.TestFlag(LoadFlags.Spawned) || _timeSinceDead >= 90 * 2); // todo: FPS stuff

        // the respawn point (cart 0x2009dc8): the first 20 spawn points; active, not cooling down, and for a player
        // availability 0 or 1 (2 is bots' only; on the level's first frame only 0); valid if no living player is within
        // 10, else the farthest; among the valid ones, frame % count. The level's first spawn is the cart's frame 0, so
        // the first valid one: the level's start (Regulator's 68, not its end points 61 / 75).
        private PlayerSpawnEntity? FirstHuntRespawnPoint()
        {
            ulong frame = FirstHuntMission.Begun ? _scene.FrameCount / 2 : 0; // todo: FPS stuff
            var valid = new List<PlayerSpawnEntity>();
            PlayerSpawnEntity? best = null;
            float bestDistance = 0;
            int count = 0;
            foreach (PlayerSpawnEntity candidate in _scene.GetPlayerSpawnEntities())
            {
                if (count++ == 20)
                {
                    break;
                }
                byte availability = candidate.Data.Availability;
                if (!candidate.IsActive || candidate.Cooldown > 0 || frame == 0 && availability != 0 || availability >= 2)
                {
                    continue;
                }
                float minDistSqr = 100;
                foreach (PlayerEntity player in _scene.GetPlayerEntities())
                {
                    if (player.Health > 0)
                    {
                        minDistSqr = Math.Min(minDistSqr, Vector3.DistanceSquared(candidate.Position, player.Position));
                    }
                }
                if (minDistSqr >= 100)
                {
                    valid.Add(candidate);
                }
                else if (minDistSqr > bestDistance)
                {
                    bestDistance = minDistSqr;
                    best = candidate;
                }
            }
            PlayerSpawnEntity? chosen = valid.Count > 0 ? valid[(int)(frame % (ulong)valid.Count)] : best;
            if (chosen != null)
            {
                chosen.Cooldown = 2 * 2; // todo: FPS stuff
            }
            return chosen;
        }

        // MphRead Process, each frame alive
        private void FirstHuntAmmo()
        {
            if (!HostFirstHunt || _health == 0)
            {
                return;
            }
            if (_ammo[Missiles] == 0)
            {
                if (++_fhTrickle >= Values.AmmoRecharge * 2) // todo: FPS stuff
                {
                    _ammo[Missiles] = 1;
                    _fhTrickle = 0;
                }
            }
            else
            {
                _fhTrickle = 0;
            }
            if (CurrentWeapon != BeamType.PowerBeam && _ammo[Missiles] < EquipWeapon.AmmoCost)
            {
                TryEquipWeapon(BeamType.PowerBeam, silent: true);
            }
        }

        // MphRead ProcessPlayer, every frame (alive or not): the match clock and rules (P2), run by the main player
        private void FirstHuntFrame()
        {
            if (HostFirstHunt && IsMainPlayer)
            {
                FirstHuntMission.Step(this);
                // the runs that end on a death: no respawn after it
                // (only ever set here: the host's own hold before the first spawn stays the host's)
                if (FirstHuntMission.Ending || FirstHuntMission.Deaths[SlotIndex] > 0 && FirstHuntMission.Mode is FhMode.Regulator or FhMode.Survivor)
                {
                    HostHoldMainSpawn = true;
                }
            }
        }

        // MphRead PickUpItems (the same checks before it): the First Hunt items out (each spawner's one), and (P2) the
        // closed doors and the point modules
        private void FirstHuntPickUps()
        {
            if (!HostFirstHunt)
            {
                return;
            }
            FirstHuntDoors();
            if (FirstHuntMission.Mode == FhMode.Morphball)
            {
                FirstHuntPointModules();
            }
            float radius = Fixed.ToFloat(Values.BipedColRadius) + 0.85f; // player +0xcc (0.5 both forms) + 0x0d99
            Vector3 center = IsAltForm ? _volume.SpherePosition : Position;
            foreach (FhItemSpawnEntity spawner in _scene.GetFhItemSpawnEntities())
            {
                FhItemEntity? item = spawner.Item;
                if (item == null || item.PickedUp || Vector3.DistanceSquared(item.ItemPosition, center) > radius * radius)
                {
                    continue;
                }
                item.PickedUp = true;
                _timeSincePickup = 0;
                FirstHuntPickUp(item.ItemType);
            }
        }

        // a closed door stops the player at its plane (0x200d530)
        private void FirstHuntDoors()
        {
            Vector3 position = Position;
            float radius = Fixed.ToFloat(Values.BipedColRadius);
            foreach (EntityBase entity in _scene.Entities)
            {
                if (entity is FhDoorEntity door)
                {
                    door.BlockPlayer(this, ref position, radius);
                }
            }
            if (position != Position)
            {
                Position = position;
            }
        }

        private Vector3 _fhPrevCenter;
        private bool _fhPrevCenterSet = false;

        // the Morphball game's point modules (0x200c15c): the player's swept path touching (radius 1.0) one of the
        // 5 modules after the current one (along next and branch) makes it current; a module that skips some breaks the
        // combo; the last one completes the game
        private void FirstHuntPointModules()
        {
            Vector3 center = IsAltForm ? _volume.SpherePosition : Position;
            Vector3 from = _fhPrevCenterSet ? _fhPrevCenter : center;
            _fhPrevCenter = center;
            _fhPrevCenterSet = true;
            PointModuleEntity? current = PointModuleEntity.Current;
            if (current == null)
            {
                return;
            }
            PointModuleEntity? hit = null;

            void Search(PointModuleEntity? module, int depth)
            {
                if (module == null || depth == 0 || hit != null)
                {
                    return;
                }
                if (SegmentNear(from, center, module.Position, 1.0f))
                {
                    hit = module;
                    return;
                }
                Search(module.Next, depth - 1);
                Search(module.Prev, depth - 1);
            }

            Search(current.Next, 5);
            Search(current.Prev, 5);
            if (hit == null)
            {
                return;
            }
            bool skipped = hit != current.Next && hit != current.Prev;
            bool last = hit.Next == null && hit.Prev == null;
            hit.SetCurrent();
            if (IsMainPlayer)
            {
                // First Hunt's 0x43 next, 0x44 skipped, 0x39 last
                _soundSource.PlayFreeSfx(last ? FirstHuntSounds.Sfx(0x39, SfxId.SCORE)
                    : FirstHuntSounds.Sfx(skipped ? 0x44 : 0x43, SfxId.AMMO_POWER_UP1));
            }
            FirstHuntMission.PointModule(this, skipped, last);
        }

        private static bool SegmentNear(Vector3 a, Vector3 b, Vector3 point, float radius)
        {
            Vector3 ab = b - a;
            float lengthSqr = ab.LengthSquared;
            float t = lengthSqr > 0 ? Math.Clamp(Vector3.Dot(point - a, ab) / lengthSqr, 0, 1) : 0;
            return Vector3.DistanceSquared(a + ab * t, point) < radius * radius;
        }

        private void FirstHuntPickUp(FhItemType type)
        {
            // the cart's pickup sounds (switch 0x200c2e0): ammo 36/37, health 30/23, double damage 39, weapon 38 (new) / 70
            void Play(int index, SfxId standIn)
            {
                if (IsMainPlayer && Sfx.TimedSfxMute == 0)
                {
                    _soundSource.PlayFreeSfx(FirstHuntSounds.Sfx(index, standIn));
                }
            }

            void AddAmmo(int amount)
            {
                _ammo[Missiles] = Math.Min(_ammo[Missiles] + amount, _ammoMax[Missiles]);
            }

            void Weapon(BeamType beam)
            {
                if (!_availableWeapons[beam])
                {
                    _availableWeapons[beam] = true;
                    Play(38, SfxId.WEAPON_POWER_UP);
                }
                else if (FirstHuntRules.Weapons?[(int)beam].Flags.TestFlag(WeaponFlags.CanCharge) == true)
                {
                    _availableCharges[beam] = true;
                    Play(70, SfxId.WEAPON_POWER_UP);
                }
                else
                {
                    Play(70, SfxId.AMMO_POWER_UP1);
                }
                AddAmmo(60);
            }

            switch (type)
            {
            case FhItemType.AmmoSmall:
                AddAmmo(60);
                Play(36, SfxId.AMMO_POWER_UP1);
                break;
            case FhItemType.AmmoBig:
                AddAmmo(100);
                Play(37, SfxId.AMMO_POWER_UP2);
                break;
            case FhItemType.HealthSmall:
            case FhItemType.HealthBig:
                _timeSinceHeal = 0;
                if (!NetClient)
                {
                    _health = Math.Min(_health + (type == FhItemType.HealthSmall ? 60 : 100), _healthMax);
                }
                Play(type == FhItemType.HealthSmall ? 30 : 23, type == FhItemType.HealthSmall ? SfxId.POWER_UP1 : SfxId.POWER_UP2);
                break;
            case FhItemType.DoubleDamage:
                _doubleDmgTimer = (ushort)(FirstHuntRules.DoubleDamageTime * 2); // todo: FPS stuff
                if (IsMainPlayer)
                {
                    _soundSource.PlayFreeSfx(FirstHuntSounds.Sfx(39, SfxId.DOUBLE_DAMAGE_POWER_UP));
                    UpdateDoubleDamageSfx(index: 0, play: true);
                    UpdateDoubleDamageSpeed(1);
                }
                break;
            case FhItemType.PickMorphBall: // (owned bit 0 = the Power Beam slot)
                Weapon(BeamType.PowerBeam);
                break;
            case FhItemType.ElectroLob:
                Weapon(FirstHuntRules.ElectroLob);
                break;
            case FhItemType.Missile:
                Weapon(BeamType.Missile);
                break;
            }
        }
    }
}
