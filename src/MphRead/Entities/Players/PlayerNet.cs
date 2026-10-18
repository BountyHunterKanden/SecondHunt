using System;
using MphRead.Formats.Culling;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // Network play (MphRecomp's multiplayer host). A remote human seat is fed by the host every frame -- its buttons
    // through its own Controls.*.HostDown, its view through an ABSOLUTE yaw/pitch target that ProcessHostInput turns
    // into this frame's aim delta. Absolute targets can't drift when a packet is lost: any error is gone next frame.
    // Nothing here does anything unless the host marks a seat HostRemote.
    public partial class PlayerEntity
    {
        public bool HostRemote { get; set; }

        // the remote player's view: yaw in degrees (the gun vector's heading, atan2(x, z)) and pitch in the game's own
        // aim units (_aimY, degrees, clamped by the game); only applied while the epoch matches (see NetAimEpoch)
        public float HostRemoteYaw { get; set; }
        public float HostRemotePitch { get; set; }
        public int HostRemoteAimEpoch { get; set; } = -1;

        // bumps whenever the game re-aims the player itself (spawn, respawn, teleport): a client's target from before
        // that is stale and is ignored until the client adopts the new view and echoes the new epoch
        public int NetAimEpoch { get; private set; }

        public float NetYaw => MathHelper.RadiansToDegrees(MathF.Atan2(_gunVec1.X, _gunVec1.Z));
        public float NetPitch => _aimY;
        public Vector3 NetAimVector => _gunVec1;

        // host aim deltas (mouse units, as HostAimX/Y) that move the current aim onto the remote target this frame;
        // mirrors UpdateAimX/Y: degrees = -delta / 4 * sensitivity (zoomed sensitivity scales with the FOV: ZoomAimFactor)
        private (float X, float Y) HostRemoteAimDelta()
        {
            if (HostRemoteAimEpoch != NetAimEpoch)
            {
                return (0, 0);
            }
            float sensitivity = ZoomAimFactor();
            float yaw = HostRemoteYaw - NetYaw;
            yaw -= 360 * MathF.Floor((yaw + 180) / 360); // shortest way round
            float pitch = HostRemotePitch - _aimY;
            // the wire quantises the target (~0.006 deg yaw, 0.01 deg pitch): once there, send nothing, or the
            // residual would count as input every frame and keep the idle sway from ever starting
            if (MathF.Abs(yaw) < 0.02f)
            {
                yaw = 0;
            }
            if (MathF.Abs(pitch) < 0.02f)
            {
                pitch = 0;
            }
            if (Controls.InvertAimX)
            {
                yaw = -yaw;
            }
            if (Controls.InvertAimY)
            {
                pitch = -pitch;
            }
            return (-4 * yaw / sensitivity, -4 * pitch / sensitivity);
        }

        // ---- match client: this scene mirrors a host ----

        // Set on a match client: damage only ever comes from the host (NetApply), never from this scene's own beams,
        // bombs or hazards.
        public static bool NetClient { get; set; }
        // recomp: set by the client's match loop while a switch made on this device waits for the host (0.2.0)
        public static bool NetWeaponPending { get; set; }
        private bool _netPrevAltForm; // recomp: the last applied snapshot's alt form (Weavel's split cost, NetApply)
        private int _netMergeAheadFrames; // recomp: snapshots in a row still showing Weavel's drone after this device merged
        private static bool _netApplyingDamage;
        public static bool NetDamageAllowed => !NetClient || _netApplyingDamage;

        // the buttons that were down this frame, the same for bots and humans (bots set their Controls too)
        public ushort NetControlBits()
        {
            PlayerControls c = Controls;
            int bits = 0;
            if (c.MoveUp.IsDown || c.RollUp.IsDown) bits |= NetControls.Up;
            if (c.MoveDown.IsDown || c.RollDown.IsDown) bits |= NetControls.Down;
            if (c.MoveLeft.IsDown || c.RolltLeft.IsDown) bits |= NetControls.Left;
            if (c.MoveRight.IsDown || c.RollRight.IsDown) bits |= NetControls.Right;
            if (c.Shoot.IsDown) bits |= NetControls.Shoot;
            if (c.Jump.IsDown) bits |= NetControls.Jump;
            if (c.Morph.IsDown) bits |= NetControls.Morph;
            if (c.Boost.IsDown) bits |= NetControls.Boost;
            if (c.AltAttack.IsDown) bits |= NetControls.AltAttack;
            if (c.Zoom.IsDown) bits |= NetControls.Zoom;
            return (ushort)bits;
        }

        public PlayerNetState NetCapture()
        {
            int weapons = 0;
            for (int i = 0; i < 14; i++) // recomp: 0-8 + bit 11 (the Pirate Blaster) + bit 13 (the Paralyzer); 9-10 are never held
            {
                if (_availableWeapons[i])
                {
                    weapons |= 1 << i;
                }
            }
            return new PlayerNetState
            {
                Flags = (byte)((LoadFlags.TestFlag(LoadFlags.Active) ? PlayerNetState.Active : 0)
                    | (LoadFlags.TestFlag(LoadFlags.Spawned) ? PlayerNetState.Spawned : 0)
                    | (IsAltForm ? PlayerNetState.AltForm : 0)),
                Position = Position,
                Speed = Speed,
                Facing = _facingVector,
                Up = _upVector,
                Yaw = NetYaw,
                Pitch = NetPitch,
                AimEpoch = (byte)NetAimEpoch,
                Health = (ushort)Math.Max(_health, 0),
                Weapon = (sbyte)CurrentWeapon,
                Weapons = (ushort)weapons,
                Ammo0 = (short)_ammo[0],
                Ammo1 = (short)_ammo[1],
                Controls = NetControlBits(),
                LastAttacker = NetLastAttacker,
                LastBeam = (sbyte)((int)NetLastBeam | (NetLastDrone ? NetDroneBeamBit : 0) | (NetLastTurret ? NetTurretBeamBit : 0)),
                LastFlags = (byte)NetLastFlags
            };
        }

        // the last hit this player took (host): who, with what, how -- a client's kill messages need them
        public sbyte NetLastAttacker { get; private set; } = -1;
        public BeamType NetLastBeam { get; private set; } = BeamType.Platform;
        public DamageFlags NetLastFlags { get; private set; }
        // the hit was the Scout Drone's (its blaster shots or laser): the kill message says (SENTRY DRONE). It rides in
        // LastBeam's 0x40 bit (beams are 0..13, Platform = no beam, -1 none) so the snapshot layout stays as it was
        public bool NetLastDrone { get; private set; }
        private const int NetDroneBeamBit = 0x40;
        private static bool _netDamageDrone;
        // the hit was Shooting Gallery's turret's (shots or entry kill): (HEAVY TURRET), in LastBeam's 0x20 bit the same way
        public bool NetLastTurret { get; private set; }
        private const int NetTurretBeamBit = 0x20;
        private static bool _netDamageTurret;
        private static PlayerEntity? _netDamageAttacker;
        private static BeamType _netDamageBeam = BeamType.Platform;

        private void NetRecordHit(PlayerEntity? attacker, BeamProjectileEntity? beam, DamageFlags flags)
        {
            NetLastAttacker = (sbyte)(attacker?.SlotIndex ?? -1);
            NetLastBeam = beam?.Beam ?? BeamType.Platform;
            NetLastDrone = DroneHit(attacker, beam);
            NetLastTurret = TurretHit(beam);
            NetLastFlags = flags & (DamageFlags.Headshot | DamageFlags.Burn | DamageFlags.Deathalt | DamageFlags.Halfturret);
        }

        private int _netFormMismatch;
        private int _netWeaponMismatch;

        // Brings this (client-side) player to the host's state. Damage, death and respawn go through the game's own
        // TakeDamage / Spawn so the flash, sounds, death and spawn effects play; then the host's exact numbers win.
        // pose: false for the local player unless it drifted past poseTolerance (it runs on its own input).
        // returns true when the pose was moved onto the host's
        public bool NetApply(in PlayerNetState s, bool pose, float poseTolerance = 0)
        {
            if ((s.Flags & PlayerNetState.Active) == 0 || !LoadFlags.TestFlag(LoadFlags.Active))
            {
                return false;
            }
            // recomp: Weavel's split (the Trooper's Scout Drone) takes half the health as a cost, not a hit: the snapshot it
            // first shows in alt form in isn't replayed as damage (no flinch, hurt sound or flash; 0.2.0 owner report)
            bool altNow = (s.Flags & PlayerNetState.AltForm) != 0;
            bool split = altNow && !_netPrevAltForm && Hunter == Hunter.Weavel && s.Health > 0 && _health - s.Health <= _health / 2 + 1;
            _netPrevAltForm = altNow;
            // recomp: and the merge: a seat that merged here ahead of the host (its own input) has its health back while the
            // host's snapshots still show the drone for a round trip: not a hit either (no hurt flash or sound; 0.2.0 owner
            // report); at most ~0.5 s, so a host that never merged still wins
            // (merged here: ExitAltForm took the half back and cleared Halfturret; the unmorph still counts as alt form)
            bool mergedHere = Hunter == Hunter.Weavel && !Flags2.TestFlag(PlayerFlags2.Halfturret)
                && (!IsAltForm || Flags1.TestFlag(PlayerFlags1.Unmorphing));
            bool mergedAhead = altNow && mergedHere && s.Health < _health && _netMergeAheadFrames < 30;
            _netMergeAheadFrames = altNow && mergedHere ? _netMergeAheadFrames + 1 : 0;
            if (split || mergedAhead)
            {
            }
            else if (s.Health < _health && (s.Flags & PlayerNetState.Spawned) == 0 && s.Health == 0)
            {
                // the host set this seat up afresh (it rejoined: no health, not spawned yet) while it still lives here
                // (the local seat isn't taken out and back in): no death, no kill message; it waits for the host's
                // spawn like the seat there (release 0.2.0 F1: "X killed you" on a rejoin)
                _health = 0;
                LoadFlags &= ~LoadFlags.Spawned;
            }
            else if (s.Health < _health)
            {
                _netApplyingDamage = true;
                _netDamageAttacker = s.LastAttacker >= 0 && s.LastAttacker < MaxPlayers ? Players[s.LastAttacker] : null;
                _netDamageDrone = s.LastBeam >= 0 && (s.LastBeam & NetDroneBeamBit) != 0;
                _netDamageTurret = s.LastBeam >= 0 && (s.LastBeam & NetTurretBeamBit) != 0;
                _netDamageBeam = (BeamType)(s.LastBeam >= 0 ? s.LastBeam & ~(NetDroneBeamBit | NetTurretBeamBit) : s.LastBeam);
                try
                {
                    TakeDamage((uint)(_health - s.Health), DamageFlags.IgnoreInvuln | (DamageFlags)s.LastFlags,
                        direction: null, source: null);
                }
                finally
                {
                    _netApplyingDamage = false;
                    _netDamageAttacker = null;
                    _netDamageDrone = false;
                    _netDamageTurret = false;
                }
            }
            else if (s.Health > 0 && _health == 0 && (s.Flags & PlayerNetState.Spawned) != 0)
            {
                Spawn(s.Position, s.Facing, s.Up, NetSpawnNodeRef(_scene, s.Position), respawn: true);
                pose = true;
            }
            // recomp: (the local seat split ahead of the host: its older snapshot doesn't put the cost back)
            if ((_health > 0 || s.Health == 0) && !(s.Health > _health && !altNow && Flags2.TestFlag(PlayerFlags2.Halfturret)) && !mergedAhead)
            {
                _health = s.Health;
            }
            for (int i = 0; i < 14; i++) // recomp: + the Pirate Blaster (bit 11) + the Paralyzer (bit 13)
            {
                _availableWeapons[i] = (s.Weapons & (1 << i)) != 0;
            }
            _ammo[0] = s.Ammo0;
            _ammo[1] = s.Ammo1;
            // a puppet takes the host's weapon at once; the local player's own switch reaches the host a round trip
            // later, so only a mismatch that outlasts that is corrected
            if (s.Weapon >= 0 && s.Weapon != (sbyte)CurrentWeapon && _health > 0)
            {
                // recomp: nothing asked of the host from here: its weapon (pickup auto-equip, empty switch) is taken at once
                if (pose || !NetWeaponPending || ++_netWeaponMismatch > 30)
                {
                    TryEquipWeapon((BeamType)s.Weapon, silent: true);
                    _netWeaponMismatch = 0;
                }
            }
            else
            {
                _netWeaponMismatch = 0;
            }
            // the relayed Morph press normally switches forms here too; correct only a mismatch that persists
            bool altForm = (s.Flags & PlayerNetState.AltForm) != 0;
            if (altForm != IsAltForm && !IsMorphing && !IsUnmorphing && _health > 0)
            {
                if (++_netFormMismatch > 10 && TrySwitchForms(force: true))
                {
                    _netFormMismatch = 0;
                }
            }
            else
            {
                _netFormMismatch = 0;
            }
            // a seat that hasn't spawned on the host yet (or here) has no pose to take
            bool spawned = (s.Flags & PlayerNetState.Spawned) != 0 && LoadFlags.TestFlag(LoadFlags.Spawned);
            if (spawned && (pose || (poseTolerance > 0
                && (s.Position - Position).LengthSquared > poseTolerance * poseTolerance)))
            {
                Vector3 prev = Position;
                Position = s.Position;
                PrevPosition = s.Position;
                Speed = s.Speed;
                _facingVector = s.Facing;
                _upVector = s.Up;
                NodeRef = NetSnapNodeRef(_scene, NodeRef, prev, s.Position);
                return true;
            }
            return false;
        }
    }

    public partial class PlayerEntity
    {
        // online match client (O5 reconciliation, MphRecomp Net/ClientPrediction): moves the local player by part of the
        // error the host measured against this device's own prediction (and its speed by the speed error); nothing else
        public void NetCorrect(Vector3 delta, Vector3 speedDelta)
        {
            if (!LoadFlags.TestFlag(LoadFlags.Spawned) || _health == 0)
            {
                return;
            }
            Vector3 prev = Position;
            Position = prev + delta;
            PrevPosition += delta;
            Speed += speedDelta;
            NodeRef = NetSnapNodeRef(_scene, NodeRef, prev, Position);
        }

        // The room part after a network move from prev to cur. A step walks the portals it crossed, as the game does; a
        // jump (a joiner's first snap onto the host's spawn, a big correction) can pass through walls and miss the portal,
        // keeping the old part, and then the room draws only what that part sees (Council Chamber black on a joiner,
        // 10-10): it takes the part the spot is in (NetSpawnNodeRef: a spawn point's own part, else the geometry
        // there; GetNodeRefByPosition's planes gave the wrong part at 10 of Council Chamber's 14 spawns). -noderefsnaptest
        // A jump that isn't onto a spawn keeps the part it walked into while that part has geometry there: the lookup
        // alone was wrong on 6.9% of real play frames in Council Chamber (overlapping parts), so a lag correction there
        // drew the room black for that player (online Bounty, 10-15). -noderefsnaptest replay=<file>
        public static NodeRef NetSnapNodeRef(Scene scene, NodeRef current, Vector3 prev, Vector3 cur)
        {
            if (current.PartIndex != -1 && (cur - prev).LengthSquared <= 1)
            {
                return scene.UpdateNodeRef(current, prev, cur);
            }
            if (NearestSpawnNodeRef(scene, cur) is { PartIndex: not -1 } spawn)
            {
                return spawn;
            }
            if (current.PartIndex != -1)
            {
                NodeRef walked = scene.UpdateNodeRef(current, prev, cur);
                if (scene.PartHasGeometryAt(walked.PartIndex, cur))
                {
                    return walked;
                }
            }
            NodeRef at = scene.GetNodeRefContaining(cur);
            return at.PartIndex != -1 || current.PartIndex == -1 ? at : scene.UpdateNodeRef(current, prev, cur);
        }

        // The room part for a spawn the host placed: the host spawns at a spawn point with that point's own (authored)
        // part, so the nearest one within a few units gives the same; elsewhere the part whose geometry is there
        public static NodeRef NetSpawnNodeRef(Scene scene, Vector3 position)
        {
            NodeRef best = NearestSpawnNodeRef(scene, position);
            return best.PartIndex != -1 ? best : scene.GetNodeRefContaining(position);
        }

        // the authored part of the spawn point within a few units of a spot, else None
        private static NodeRef NearestSpawnNodeRef(Scene scene, Vector3 position)
        {
            NodeRef best = NodeRef.None;
            float bestDist = 3 * 3;
            foreach (PlayerSpawnEntity spawn in scene.GetPlayerSpawnEntities())
            {
                float dist = (spawn.Position - position).LengthSquared;
                if (spawn.NodeRef.PartIndex != -1 && dist < bestDist)
                {
                    bestDist = dist;
                    best = spawn.NodeRef;
                }
            }
            return best;
        }

        // online match host (O5 lag compensation, MphRecomp Net/LagCompensation): how many frames behind the host this
        // remote player's view was when its latest input was sent (0: LAN, bots, the host's own player)
        public int HostLagRewind { get; set; }
        // shooter, target -> where the target stood shooter.HostLagRewind frames ago minus where it stands now; null = off
        public static Func<PlayerEntity, PlayerEntity, Vector3>? HostLagShift { get; set; }

        // a shot by `owner` (a player or Weavel's turret) is tested against `target` this far from where it stands now:
        // where that shooter saw it. Zero unless the host rewinds for that shooter.
        public static Vector3 NetLagShift(EntityBase? owner, PlayerEntity target)
        {
            if (HostLagShift == null)
            {
                return Vector3.Zero;
            }
            PlayerEntity? shooter = owner as PlayerEntity ?? (owner as HalfturretEntity)?.Owner;
            return shooter != null && shooter.HostRemote && shooter.HostLagRewind > 0 && shooter != target
                ? HostLagShift(shooter, target) : Vector3.Zero;
        }

        // ---- online extras (O5, MphRecomp Net/SeatExtras): what a client can't work out from the host's hits ----

        public PlayerNetExtras NetCaptureExtras()
        {
            return new PlayerNetExtras
            {
                Frozen = _frozenTimer,
                Disrupted = _disruptedTimer,
                Burn = _burnTimer,
                BurnedBy = (sbyte)((_burnedBy as PlayerEntity)?.SlotIndex ?? -1),
                DoubleDamage = _doubleDmgTimer,
                Cloak = Flags2.TestFlag(PlayerFlags2.Cloaking) ? _cloakTimer : (ushort)0, // (else Trace's stand-still count)
                Deathalt = _deathaltTimer,
                Turret = Flags2.TestFlag(PlayerFlags2.Halfturret) ? (ushort)Math.Clamp(_halfturret.Health, 0, 65535) : (ushort)0
            };
        }

        // Starts each the way the hit or pickup would have here (sound, ice layer, HUD static, burn effect), ends it by
        // leaving one frame on the timer so the game's own countdown ends it (thaw, cloak off), and otherwise keeps the
        // timers within a few frames of the host's. A turret's health follows the host's while both have one up; its death
        // comes from the host's hit replayed on the same health.
        public void NetApplyExtras(in PlayerNetExtras e)
        {
            if (!LoadFlags.TestFlag(LoadFlags.Active) || !LoadFlags.TestFlag(LoadFlags.Spawned) || _health == 0)
            {
                return;
            }
            const int drift = 4;
            if (e.Frozen > 0 && _frozenTimer == 0)
            {
                _soundSource.PlaySfx(SfxId.SHOTGUN_FREEZE);
                if (IsMainPlayer)
                {
                    ResetCombatVisor();
                    _drawIceLayer = true;
                }
                _frozenTimer = e.Frozen;
                _frozenGfxTimer = (ushort)(e.Frozen + 5 * 2);
                EndAltAttack();
            }
            else if (e.Frozen == 0 && _frozenTimer > 1)
            {
                _frozenTimer = 1;
            }
            else if (e.Frozen > 0 && Math.Abs(e.Frozen - _frozenTimer) > drift)
            {
                _frozenTimer = e.Frozen;
            }
            if (e.Disrupted > 0 && _disruptedTimer == 0)
            {
                _disruptedTimer = e.Disrupted;
                if (IsMainPlayer)
                {
                    HudOnDisrupted();
                    _soundSource.PlaySfx(SfxId.LOB_DISRUPT);
                }
            }
            else if (e.Disrupted == 0 || Math.Abs(e.Disrupted - _disruptedTimer) > drift)
            {
                _disruptedTimer = e.Disrupted;
            }
            if (e.Burn > 0 && _burnTimer == 0)
            {
                _burnedBy = e.BurnedBy >= 0 && e.BurnedBy < MaxPlayers ? Players[e.BurnedBy] : null;
                _burnTimer = e.Burn;
                CreateBurnEffect();
            }
            else if (e.Burn == 0 && _burnTimer > 1)
            {
                _burnTimer = 1;
            }
            else if (e.Burn > 0 && Math.Abs(e.Burn - _burnTimer) > drift)
            {
                _burnTimer = e.Burn;
            }
            if (e.DoubleDamage > 0 && _doubleDmgTimer == 0)
            {
                _doubleDmgTimer = e.DoubleDamage;
                if (IsMainPlayer)
                {
                    _soundSource.PlayFreeSfx(SfxId.DOUBLE_DAMAGE_POWER_UP);
                    UpdateDoubleDamageSfx(index: 0, play: true);
                    UpdateDoubleDamageSpeed(1);
                }
            }
            else if (e.DoubleDamage == 0 && _doubleDmgTimer > 1)
            {
                _doubleDmgTimer = 1;
            }
            else if (e.DoubleDamage > 0 && Math.Abs(e.DoubleDamage - _doubleDmgTimer) > drift)
            {
                _doubleDmgTimer = e.DoubleDamage;
            }
            bool cloaking = Flags2.TestFlag(PlayerFlags2.Cloaking);
            if (e.Cloak > 0 && !cloaking)
            {
                _cloakTimer = e.Cloak;
                Flags2 |= PlayerFlags2.Cloaking;
                if (IsMainPlayer)
                {
                    _soundSource.PlayFreeSfx(SfxId.CLOAK_POWER_UP);
                    UpdateCloakSfx(index: 0, play: true);
                }
            }
            else if (e.Cloak == 0 && cloaking && _cloakTimer > 1)
            {
                _cloakTimer = 1;
            }
            else if (e.Cloak > 0 && Math.Abs(e.Cloak - _cloakTimer) > drift)
            {
                _cloakTimer = e.Cloak;
            }
            if (e.Deathalt > 0 && _deathaltTimer == 0)
            {
                _deathaltTimer = e.Deathalt;
                if (IsMainPlayer)
                {
                    _soundSource.PlayFreeSfx(SfxId.DOUBLE_DAMAGE_POWER_UP);
                }
            }
            else if (e.Deathalt == 0 && _deathaltTimer > 1)
            {
                _deathaltTimer = 1;
            }
            else if (e.Deathalt > 0 && Math.Abs(e.Deathalt - _deathaltTimer) > drift)
            {
                _deathaltTimer = e.Deathalt;
            }
            if (e.Turret > 0 && Flags2.TestFlag(PlayerFlags2.Halfturret) && _halfturret.Health > 0)
            {
                _halfturret.Health = e.Turret;
            }
        }

        // ---- network play: a seat leaving and coming back mid-match (O5 rejoin) ----

        // A player who dropped out of the match comes back (its app restarted, or it joined again). The game took the
        // player out (ProcessPlayer: Disconnected, not Active, then out of the scene): it's back in the scene, set up
        // afresh with no health, and spawns as after a death (the host's respawn; a client takes the host's spawn). Not
        // processed out yet (the same frame): only the seat comes back, as it was.
        public void NetReconnect()
        {
            bool inScene = false;
            foreach (PlayerEntity p in _scene.GetPlayerEntities())
            {
                if (p == this)
                {
                    inScene = true;
                    break;
                }
            }
            LoadFlags |= LoadFlags.Connected | LoadFlags.Active;
            LoadFlags &= ~LoadFlags.Disconnected;
            if (!inScene)
            {
                LoadFlags &= ~LoadFlags.Spawned;
                _scene.AddEntity(this); // (Initialize: health 0, respawn timers reset)
                // the hit before it left isn't its last hit any more (a client would credit it again)
                NetLastAttacker = -1;
                NetLastBeam = BeamType.Platform;
                NetLastDrone = false;
                NetLastTurret = false;
                NetLastFlags = 0;
            }
        }

        // online host migration (MphRecomp O5b): a match client that becomes the host turns the old host's bots, puppets
        // here until now, into real bots: the game's own AI from this frame on (fresh targets and paths; the personality the
        // mode gives every bot), at the old host's level. Its buttons are the AI's now, and it isn't a network seat.
        public void NetBecomeBot(int level)
        {
            HostRemote = false;
            HostRemoteAimEpoch = -1;
            HostLagRewind = 0;
            IsBot = true;
            BotLevel = Math.Clamp(level, 0, 2);
            LoadFlags &= ~(LoadFlags.Connected | LoadFlags.WasConnected);
            Formats.AiPersonality.Load(this, GameState.Mode, SlotIndex);
            AiData.InitializeAtLoad();
        }

        // online match client: a seat follows the host's -- out when the host took it out (its player left), back when
        // the host brought it back; only while the match runs (after it, players leaving must stay in the scene: the match
        // end camera may follow one of them as the winner)
        public void NetFollowSeat(bool hostActive)
        {
            if (GameState.MatchState != MatchState.InProgress)
            {
                return;
            }
            if (!hostActive && LoadFlags.TestFlag(LoadFlags.Active) && LoadFlags.TestFlag(LoadFlags.Connected))
            {
                LoadFlags &= ~LoadFlags.Connected;
            }
            else if (hostActive && !LoadFlags.TestFlag(LoadFlags.Active) && LoadFlags.TestFlag(LoadFlags.Disconnected))
            {
                NetReconnect();
            }
        }

        // beams that hit another player, by shooter seat, in this process: on a match client they deal nothing (the
        // host decides), so a client's own count against the host's says how often a hit seen here counted there
        public static int[] NetBeamHits { get; } = new int[16];

        public static void NetCountBeamHit(EntityBase? owner, PlayerEntity target)
        {
            PlayerEntity? shooter = owner as PlayerEntity ?? (owner as HalfturretEntity)?.Owner;
            if (shooter != null && shooter != target && shooter.SlotIndex >= 0 && shooter.SlotIndex < NetBeamHits.Length)
            {
                NetBeamHits[shooter.SlotIndex]++;
            }
        }
    }

    public static class NetControls
    {
        public const int Up = 1, Down = 2, Left = 4, Right = 8, Shoot = 16, Jump = 32, Morph = 64, Boost = 128,
            AltAttack = 256, Zoom = 512;
    }

    // The timers an online client can't start itself (MphRecomp Net/SeatExtras), in frames; 0 = off. Turret = the health of
    // Weavel's turret while it's up.
    public struct PlayerNetExtras
    {
        public ushort Frozen, Disrupted, Burn, DoubleDamage, Cloak, Deathalt, Turret;
        public sbyte BurnedBy;
    }

    // One player as the host sees it after a frame (MphRecomp's snapshot; serialised by MphRecomp.Core Net/).
    public struct PlayerNetState
    {
        public const byte Active = 1, Spawned = 2, AltForm = 4;
        public byte Flags;
        public Vector3 Position;
        public Vector3 Speed;
        public Vector3 Facing;
        public Vector3 Up;
        public float Yaw;
        public float Pitch;
        public byte AimEpoch;
        public ushort Health;
        public sbyte Weapon;
        public ushort Weapons;
        public short Ammo0;
        public short Ammo1;
        public ushort Controls;
        public sbyte LastAttacker; // seat, -1 none
        public sbyte LastBeam;
        public byte LastFlags; // DamageFlags: headshot / burn / deathalt / halfturret
    }
}
