using System;
using System.Collections.Generic;
using MphRead.Formats;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // a breakable wall a bot can shoot down when it stands across its route (Core: EchoesBreakableEntity)
    public interface IBotBreakable
    {
        // standing, and a wall (not a grate: those are floor to walk over)
        bool BotStanding { get; }
        EntityBase BotEntity { get; }
        // where the segment a -> b first comes within pad (sideways) of the box: the fraction along it, or -1 when it doesn't
        float BotCross(Vector3 a, Vector3 b, float pad);
        // the point of the box closest to p
        Vector3 BotNearest(Vector3 p);
    }

    // recomp: bots break a breakable wall standing across their route (owner 2026-10-26: bots never get stuck and never give
    // up a path; teach them what blocks their way and their shots as a player knows it). Crossfire Chaos's 4 walls stand
    // until shot down and the arena's generated route graph (ArenaNav) runs through them, so a bot walked into one, gave up
    // its node, picked the closest one again and looped (seed 3: bot 1 on 130 -> 112 at the wall at x 10.6). A bot following
    // nodes whose way to its next node crosses a standing wall within 16 units shoots it as it does a crate (aim on it, taps
    // of Shoot), stops 3 units short of it, and walks on once it breaks: the same link, nothing given up. Its travel clock
    // (Field118) doesn't run while it shoots, so the shooting isn't counted as failing to get there. A foe in sight comes
    // first. With HostBotVanillaParity (MatchSettings.BotBreakWalls; replays before format 66 play without it). No draws
    // from the game's random numbers.
    public partial class PlayerEntity
    {
        public static bool HostBotBreakWalls { get; set; }
        // the arena's breakable walls and grates (Core: EchoesBreakables.Extra); null = none
        public static IReadOnlyList<IBotBreakable>? HostWallList { get; set; }

        public partial class PlayerAiData
        {
            private const float WallSeeRange = 16;    // a wall further along the way than this is left until it's closer
            private const float WallStandOff = 3;     // closer than this it stops walking and shoots
            private const float WallBody = 0.45f;     // a way passing this close to the box runs into it (the body's radius)
            private const int WallLookEvery = 10;     // frames between looks along the way
            private const int WallMaxFrames = 10 * 60;
            private const int WallRestFrames = 5 * 60; // after giving one up on time, before looking again

            private IBotBreakable? _wall = null;
            private int _wallTick = 0;
            private int _wallFrames = 0;
            private int _wallFire = 0;
            private int _wallRest = 0;
            private int _wallClock = 0;               // Field118 as it was when the shooting started

            // counts for -botprobe: walls picked, shots at walls, walls seen break, given up: time, way changed, fight
            public static readonly int[] HostWallCounts = new int[6];
            public bool HostWallOn => _wall != null; // -botprobe

            // Process, after the crate step, before the unstick (which waits while it shoots)
            private void WallStep()
            {
                if (_wallRest > 0)
                {
                    _wallRest--;
                }
                bool travel = (long)_scene.FrameCount - _unstickTravelAt <= 2; // (the tree may run its travel every other frame)
                if (!HostBotVanillaParity || !HostBotBreakWalls || HostWallList == null || HostWallList.Count == 0 || HostIdleBots
                    || _player.Health == 0 || _player.IsAltForm || _player.IsMorphing || _player.IsUnmorphing
                    || _player.HostSeatShell != null || _errandOn || _player.WallClinging || _perchFrames > 0
                    || _crateAim || _dropFrames > 0 || !travel || _node40 == null
                    || _targetPlayer != null && _targetPlayer.Health > 0 && Flags2.TestFlag(AiFlags2.TargetPlayer)
                        && IsPlayerVisible(_player, _targetPlayer))
                {
                    if (_wall != null)
                    {
                        HostWallCounts[5]++;
                    }
                    _wall = null;
                    return;
                }
                Vector3 from = _player.Position.AddY(0.8f), to = _node40.Position.AddY(0.8f);
                if (_wall != null)
                {
                    if (!_wall.BotStanding)
                    {
                        HostWallCounts[2]++;
                        _wall = null;
                        return;
                    }
                    if (_wall.BotCross(from, to, WallBody) < 0)
                    {
                        HostWallCounts[4]++;
                        _wall = null;
                        return;
                    }
                    if (++_wallFrames > WallMaxFrames)
                    {
                        HostWallCounts[3]++;
                        _wall = null;
                        _wallRest = WallRestFrames;
                        return;
                    }
                }
                else
                {
                    if (_wallRest > 0 || (++_wallTick + _player.SlotIndex * 3) % WallLookEvery != 0
                        || _player.CurrentWeapon == BeamType.OmegaCannon)
                    {
                        return;
                    }
                    float best = 2;
                    foreach (IBotBreakable wall in HostWallList)
                    {
                        if (!wall.BotStanding)
                        {
                            continue;
                        }
                        float t = wall.BotCross(from, to, WallBody);
                        if (t >= 0 && t < best && (to - from).Length * t <= WallSeeRange)
                        {
                            best = t;
                            _wall = wall;
                        }
                    }
                    if (_wall == null)
                    {
                        return;
                    }
                    HostWallCounts[0]++;
                    _wallFrames = 0;
                    _wallFire = 0;
                    _wallClock = Field118;
                }
                // its travel clock waits (the tree's give-up checks read it)
                Field118 = _wallClock;
                float cross = Math.Max(_wall.BotCross(from, to, WallBody), 0);
                Vector3 at = _wall.BotNearest(from + (to - from) * cross);
                _crateAim = true; // (the aim is this step's: the sight hold and roam look leave it, as for a crate)
                Func2145C14(at);
                Vector3 eye = _player.CameraInfo.Position;
                Vector3 dir = at - eye;
                dir = dir != Vector3.Zero ? dir.Normalized() : _player.CameraInfo.Facing;
                float pitch = 90 - MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(_player.CameraInfo.Facing.Y, -1, 1)));
                float want = 90 - MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(dir.Y, -1, 1)));
                _buttonAimY = Math.Clamp(want - pitch, -AimStep, AimStep);
                var face = new Vector2(_player.CameraInfo.Field48, _player.CameraInfo.Field4C);
                var flat = new Vector2(dir.X, dir.Z);
                bool aimed = face != Vector2.Zero && flat.LengthSquared > 0.0001f
                    && Vector2.Dot(face.Normalized(), flat.Normalized()) > CrateAimDot && MathF.Abs(want - pitch) < CrateAimPitch;
                // the line a beam takes to it (a little past its face, so the box itself is what's hit)
                CollisionResult result = default;
                bool seen = !CollisionDetection.CheckBetweenPoints(eye, at + dir * 0.3f, TestFlags.Beams, _scene, ref result)
                    || result.EntityCollision?.Entity == _wall.BotEntity;
                // taps: 2 frames down, 6 up, as at a crate
                bool fire = aimed && seen && _wallFire++ % 8 < 2;
                _buttons.R.IsDown = fire;
                if (fire && _wallFire % 8 == 1)
                {
                    HostWallCounts[1]++;
                }
                if ((at - _player.Position).Xz.LengthSquared < WallStandOff * WallStandOff)
                {
                    // close enough: stand and shoot
                    _buttons.X.IsDown = false;
                    _buttons.B.IsDown = false;
                    _buttons.Y.IsDown = false;
                    _buttons.A.IsDown = false;
                }
            }
        }
    }
}
