using System;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: a Trooper bot uses his Wall Cling as a player does (2026-10-23, bug-fix batch A1; owner: "i want them to do
    // everything players do"). The game's bots have no wall cling (no hunter of theirs has one), so this is ours, built on
    // the player's own rules in PlayerWallCling.cs: the bot only presses Jump, the cling itself starts, holds and ends
    // exactly as a player's does (hold rule, tall walls, a different wall each time, the chain limit).
    //   - in a fight (its target player in sight, standing), now and then it picks a tall wall within a few steps (rays at
    //     its feet, waist and above its head; a floor on the way), jumps and steers at it, and holds Jump after the hold
    //     rule's wait so the cling catches; it keeps aiming and firing at its target all the while
    //   - in the air in a fight, moving into a wall it could cling to: holds Jump, as a player would
    //   - on the wall: Jump released, aims and fires; wall-jumps off after 0.6-1.9 s, or 0.4 s after it loses sight of its
    //     target, or it lets the cling time out (as a player may) when its target is far below
    // Roaming bots (no target in sight) never cling: on a nav jump up to a ledge a cling would hold them off it.
    // Host-guarded (MatchSettings.BotWallCling; replays before format 58 play without it). No draws from the game's random
    // numbers: its picks come from a counter of its own, so with the rule on everything else the bots do draws the same.
    public partial class PlayerEntity
    {
        public static bool HostBotWallCling { get; set; }

        public partial class PlayerAiData
        {
            private const float PerchReach = 2.5f;  // how far a wall may be for a perch jump
            private const int PerchCheck = 90;      // frames between perch chances in a fight (1 in 3 each)
            private const int PerchMaxFrames = 60;  // a perch jump that hasn't clung by then is over
            private int _wcTick = 0;
            private int _perchFrames = 0;           // > 0: a perch jump at _perchDir is on
            private Vector2 _perchDir = Vector2.Zero;
            private int _clingStay = 0;             // frames this cling lasts before the wall jump (0: not chosen)
            private bool _clingNoJump = false;      // this cling times out instead of a wall jump

            // counts for -botprobe
            public static readonly int[] HostWallClingCounts = new int[3]; // perch jumps, clings, wall jumps

            private uint WallClingHash(int salt)
            {
                uint h = (uint)(_wcTick * 2654435761u) ^ (uint)((_player.SlotIndex + 1) * 40503 + salt * 977);
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                return h;
            }

            // Process, after the unstick step (its climbs and drifts win)
            private void BotWallCling()
            {
                if (!HostBotWallCling || !_player.HostWallCling || HostIdleBots || _player.Health == 0 || _player.IsAltForm
                    || _player.IsMorphing || _player.IsUnmorphing || _player.HostSeatShell != null)
                {
                    _perchFrames = 0;
                    _clingStay = 0;
                    return;
                }
                _wcTick++;
                PlayerEntity? target = _targetPlayer != null && _targetPlayer.Health > 0 ? _targetPlayer : null;
                bool sees = target != null && Flags2.TestFlag(AiFlags2.TargetPlayer) && IsPlayerVisible(_player, target);
                if (_player.WallClinging)
                {
                    _perchFrames = 0;
                    int held = WallClingTime - _player.WallClingTimer;
                    if (_clingStay == 0)
                    {
                        HostWallClingCounts[1]++;
                        _clingStay = 36 + (int)(WallClingHash(1) % 78);
                        // a target well below: stay and shoot down until the cling ends by itself (1 in 4)
                        _clingNoJump = target != null && target.Position.Y < _player.Position.Y - 3 && WallClingHash(2) % 4 == 0;
                    }
                    bool jump = !_clingNoJump && (held >= _clingStay || !sees && held >= 24);
                    // the release frames before make it a press (WallClingInput: a press = the wall jump)
                    _buttons.L.IsDown = jump && held >= 3;
                    if (_buttons.L.IsDown)
                    {
                        HostWallClingCounts[2]++;
                    }
                    return;
                }
                _clingStay = 0;
                bool standing = _player.Flags1.TestFlag(PlayerFlags1.Standing);
                if (_perchFrames > 0)
                {
                    _perchFrames++;
                    if (!sees && _perchFrames > 30 || _perchFrames > PerchMaxFrames || standing && _perchFrames > 8)
                    {
                        _perchFrames = 0;
                        return;
                    }
                    _buttons.X.IsDown = false;
                    _buttons.B.IsDown = false;
                    _buttons.Y.IsDown = false;
                    _buttons.A.IsDown = false;
                    Func2142AE8(_player.Position + new Vector3(_perchDir.X, 0, _perchDir.Y) * 4);
                    // the jump press, then Jump held once the hold rule's wait is over
                    _buttons.L.IsDown = _perchFrames <= 4
                        || !standing && _player.WallClingSinceJump >= WallClingHoldDelay - 1;
                    return;
                }
                if (!sees || _unstickClimb > 0 || _unstickDrift > 0 || _unstickPadFlight > 0)
                {
                    return;
                }
                if (standing)
                {
                    if ((_wcTick + _player.SlotIndex * 17) % PerchCheck == 0 && WallClingHash(3) % 3 == 0
                        && _buttons.L.FramesUp > 4 && !_errandOn && PerchWall(out _perchDir))
                    {
                        _perchFrames = 1;
                        HostWallClingCounts[0]++;
                        _buttons.L.IsDown = true;
                    }
                    return;
                }
                // in the air in a fight: Jump held while it moves into a wall it could cling to
                var speed = new Vector2(_player.Speed.X, _player.Speed.Z);
                if (_player.WallClingCanStart && _player.WallClingSinceJump >= WallClingHoldDelay - 1
                    && speed.LengthSquared > 0.03f * 0.03f && BotWallAhead(speed.Normalized()))
                {
                    _buttons.L.IsDown = true;
                }
            }

            // the nearest wall in 8 directions, tall from its feet to above its head, standing within the cling's angles,
            // with floor on the way to it
            private bool PerchWall(out Vector2 dir)
            {
                dir = Vector2.Zero;
                float best = PerchReach + 1;
                uint start = WallClingHash(4) % 8;
                for (int i = 0; i < 8; i++)
                {
                    Vector2 d = _perchDirs[(int)((start + i) % 8)];
                    float dist = 0;
                    bool ok = true;
                    foreach (float y in _perchHeights)
                    {
                        Vector3 from = _player.Position.AddY(y);
                        var result = new CollisionResult();
                        if (!CollisionDetection.CheckBetweenPoints(from, from + new Vector3(d.X, 0, d.Y) * PerchReach,
                            TestFlags.Players, _scene, ref result)
                            || result.Plane.Y < WallClingMinNormalY || result.Plane.Y > WallClingMaxNormalY
                            || result.Plane.X * d.X + result.Plane.Z * d.Y > -0.9f)
                        {
                            ok = false;
                            break;
                        }
                        dist = MathF.Max(dist, result.Distance * PerchReach);
                    }
                    if (!ok || dist >= best)
                    {
                        continue;
                    }
                    var floor = new CollisionResult();
                    Vector3 mid = _player.Position + new Vector3(d.X, 0, d.Y) * (dist / 2);
                    if (!CollisionDetection.CheckBetweenPoints(mid.AddY(0.5f), mid.AddY(-1.5f), TestFlags.Players, _scene, ref floor))
                    {
                        continue;
                    }
                    best = dist;
                    dir = d;
                }
                return best <= PerchReach;
            }

            private static readonly float[] _perchHeights = [0.5f, 1.8f, 3.2f];
            private const float _diag = 0.70710677f;
            private static readonly Vector2[] _perchDirs = // (no trig in sim code)
            [
                new Vector2(1, 0), new Vector2(_diag, _diag), new Vector2(0, 1), new Vector2(-_diag, _diag),
                new Vector2(-1, 0), new Vector2(-_diag, -_diag), new Vector2(0, -1), new Vector2(_diag, -_diag)
            ];
        }
    }
}
