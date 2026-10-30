using System;
using System.Collections.Generic;
using MphRead.Formats;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: bots break the Echoes arenas' crates (2026-10-23, bug-fix batch A4; owner: "thats the only way to get
    // pickups on echoes maps"). The game's bots have no crates to break, so this is ours, done as a player would: a bot
    // with no target player in sight that sees an upright crate (the line a beam takes, so grates let it through) within
    // 32 units turns to it and taps Shoot once its aim is on it, walking closer while it's past 20 units; when the crate
    // breaks (its shots or anyone's), it walks to the weapon that dropped, if it's within 32 units with floor on the way,
    // then its own moves pick up again. A target player in sight ends all of it: the fight comes first.
    // Host-guarded (MatchSettings.BotCrates; replays before format 58 play without it). No draws from the game's random
    // numbers.
    public partial class PlayerEntity
    {
        public static bool HostBotCrates { get; set; }
        // the arena's crates (Core: EchoesCrates.Extra); null = none
        public static IReadOnlyList<EnemyInstanceEntity>? HostCrateList { get; set; }

        public partial class PlayerAiData
        {
            private const float CrateSeeRange = 32;   // a crate further off isn't looked at
            private const float CrateShootRange = 20; // past this it walks closer as it shoots
            private const float CrateDropReach = 32;  // a drop further off is left to its own item search
            private const int CrateLookEvery = 10;    // frames between looks for a crate
            private const int CrateMaxFrames = 6 * 60;
            private const int CrateDropMaxFrames = 5 * 60;
            private const float CrateAimDot = 0.9945f; // within ~6 degrees across
            private const float CrateAimPitch = 6;     // and up or down

            private int _crateTick = 0;
            private EnemyInstanceEntity? _crate = null;
            private int _crateFrames = 0;
            private int _crateFire = 0;
            private Vector3 _crateAt = Vector3.Zero;
            private int _dropFrames = 0;               // > 0: on the way to a crate's drop
            private ItemInstanceEntity? _dropItem = null;
            private bool _crateAim = false;            // this frame's aim is the crate step's (sight hold / roam look leave it)
            private bool _errandOn = false;            // the turret errand has the bot this step (PlayerEchoesSeat.cs)

            // counts for -botprobe
            public static readonly int[] HostCrateCounts = new int[11]; // crates picked, shots at crates, crates seen break, drops reached,
                                                                       // given up: time, out of sight, fight; drops: too far, no floor, timed out
            public bool HostCrateAim => _crateAim; // -botprobe

            // Process, after the turret errand, before the unstick (its climbs and drifts win)
            private void CrateStep()
            {
                _crateAim = false;
                if (!HostBotCrates || HostCrateList == null || HostCrateList.Count == 0 || HostIdleBots || _player.Health == 0
                    || _player.IsAltForm || _player.IsMorphing || _player.IsUnmorphing || _player.HostSeatShell != null
                    || _errandOn || _player.WallClinging || _perchFrames > 0
                    || _targetPlayer != null && _targetPlayer.Health > 0 && Flags2.TestFlag(AiFlags2.TargetPlayer)
                        && IsPlayerVisible(_player, _targetPlayer))
                {
                    if (_crate != null)
                    {
                        HostCrateCounts[6]++;
                    }
                    _crate = null;
                    _dropFrames = 0;
                    _dropItem = null;
                    return;
                }
                _crateTick++;
                if (_dropFrames > 0)
                {
                    DropWalk();
                    return;
                }
                if (_crate != null)
                {
                    if (!_crate.GetTargetable())
                    {
                        // broken: its weapon drops at its foot
                        HostCrateCounts[2]++;
                        _crate = null;
                        if (Vector3.DistanceSquared(_crateAt, _player.Position) <= CrateDropReach * CrateDropReach)
                        {
                            _dropFrames = 1;
                            _dropItem = null;
                        }
                        else
                        {
                            HostCrateCounts[7]++;
                        }
                        return;
                    }
                    if (++_crateFrames > CrateMaxFrames || !CrateInSight(_crate, out _))
                    {
                        HostCrateCounts[_crateFrames > CrateMaxFrames ? 4 : 5]++;
                        _crate = null;
                    }
                }
                if (_crate == null && (_crateTick + _player.SlotIndex * 3) % CrateLookEvery == 0
                    && _player.CurrentWeapon != BeamType.OmegaCannon)
                {
                    float best = CrateSeeRange * CrateSeeRange;
                    for (int i = 0; i < HostCrateList.Count; i++)
                    {
                        EnemyInstanceEntity crate = HostCrateList[i];
                        if (crate.GetTargetable() && CrateInSight(crate, out float dist) && dist < best)
                        {
                            best = dist;
                            _crate = crate;
                        }
                    }
                    if (_crate != null)
                    {
                        HostCrateCounts[0]++;
                        _crateFrames = 0;
                        _crateFire = 0;
                    }
                }
                if (_crate == null)
                {
                    return;
                }
                _crate.GetPosition(out Vector3 center);
                _crateAt = center;
                _crateAim = true;
                // yaw (as the AI turns to a point) and pitch (as its aim at a player does: Func21447E8)
                Func2145C14(center);
                Vector3 to = center - _player.CameraInfo.Position;
                to = to != Vector3.Zero ? to.Normalized() : _player.CameraInfo.Facing;
                float pitch = 90 - MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(_player.CameraInfo.Facing.Y, -1, 1)));
                float want = 90 - MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(to.Y, -1, 1)));
                _buttonAimY = Math.Clamp(want - pitch, -AimStep, AimStep);
                var face = new Vector2(_player.CameraInfo.Field48, _player.CameraInfo.Field4C);
                var flat = new Vector2(to.X, to.Z);
                bool aimed = face != Vector2.Zero && flat.LengthSquared > 0.0001f
                    && Vector2.Dot(face.Normalized(), flat.Normalized()) > CrateAimDot && MathF.Abs(want - pitch) < CrateAimPitch;
                // taps: 2 frames down, 6 up (an uncharged shot each; the Shock Coil's beam in bursts)
                bool fire = aimed && _crateFire++ % 8 < 2;
                _buttons.R.IsDown = fire;
                if (fire && _crateFire % 8 == 1)
                {
                    HostCrateCounts[1]++;
                }
                if (Vector3.DistanceSquared(center, _player.Position) > CrateShootRange * CrateShootRange)
                {
                    WalkTo(center);
                }
            }

            // eye to the crate's centre, the line a beam takes (grates and other players don't hide it)
            private bool CrateInSight(EnemyInstanceEntity crate, out float distSquared)
            {
                crate.GetPosition(out Vector3 center);
                Vector3 eye = _player.CameraInfo.Position;
                distSquared = Vector3.DistanceSquared(center, eye);
                if (distSquared > CrateSeeRange * CrateSeeRange)
                {
                    return false;
                }
                CollisionResult result = default;
                return !CollisionDetection.CheckBetweenPoints(eye, center, TestFlags.Beams, _scene, ref result)
                    || result.EntityCollision?.Entity == crate;
            }

            // to the item that dropped where the crate broke
            private void DropWalk()
            {
                if (++_dropFrames > CrateDropMaxFrames)
                {
                    HostCrateCounts[9]++;
                    _dropFrames = 0;
                    _dropItem = null;
                    return;
                }
                if (_dropItem == null)
                {
                    foreach (ItemInstanceEntity item in _scene.GetItemInstanceEntities())
                    {
                        if (item.DespawnTimer != 0 && (item.Position - _crateAt).Xz.LengthSquared < 1.5f * 1.5f
                            && MathF.Abs(item.Position.Y - _crateAt.Y) < 3)
                        {
                            _dropItem = item;
                            break;
                        }
                    }
                    if (_dropItem == null)
                    {
                        // (the drop appears a frame or two after the break)
                        if (_dropFrames > 20)
                        {
                            _dropFrames = 0;
                        }
                        return;
                    }
                }
                if (_dropItem.DespawnTimer == 0)
                {
                    // picked up (by it, most likely) or timed out
                    if (Vector3.DistanceSquared(_dropItem.Position, _player.Position) < 2.5f * 2.5f)
                    {
                        HostCrateCounts[3]++;
                    }
                    _dropFrames = 0;
                    _dropItem = null;
                    return;
                }
                Func2145C14(_dropItem.Position);
                if (!WalkTo(_dropItem.Position))
                {
                    HostCrateCounts[8]++;
                    _dropFrames = 0;
                    _dropItem = null;
                }
            }

            // a straight walk toward pos, while there's floor a step ahead (no walking off a ledge for it)
            private bool WalkTo(Vector3 pos)
            {
                Vector3 to = (pos - _player.Position).WithY(0);
                if (to.LengthSquared < 0.25f)
                {
                    return true;
                }
                Vector3 ahead = _player.Position + to.Normalized() * 1.2f;
                CollisionResult floor = default;
                if (!CollisionDetection.CheckBetweenPoints(ahead.AddY(0.5f), ahead.AddY(-2.5f), TestFlags.Players, _scene, ref floor))
                {
                    return false;
                }
                _buttons.X.IsDown = false;
                _buttons.B.IsDown = false;
                _buttons.Y.IsDown = false;
                _buttons.A.IsDown = false;
                Func2142AE8(pos);
                return true;
            }
        }
    }
}
