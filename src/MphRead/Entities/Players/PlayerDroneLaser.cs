using System;
using System.Buffers;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: the Scout Drone's second attack, the Prime 1 drone's cutting laser (owner 2026-10-08: L2 and a touch button,
    // Prime's own sweep). Host-guarded as the rest of the drone (PlayerScoutDrone.cs).
    //   - press the laser button (Zoom: pad L2, the touch LASER button) in drone form, folded and done waiting, and it runs
    //     its whole clip, 134 frames (A_cuttinglaser_drone, 2.233 s): it winds up, the beam comes on at 0.95 s, then sweeps
    //     for 1 s and stays where it ended until 2.05 s
    //   - the sweep is after Prime's (CDrone::UpdateLaser): the beam's aim point slides along a line through the target
    //     (picked at random of four: left to right or right to left, rising or falling), 10 Prime units long at
    //     _droneLaserUnit MPH units per Prime unit, how far out set when it starts (the first player along the aim, else the
    //     first wall, 4..40 units). Owner 2026-10-08 22:55, after trying Prime's: the sweep is around where he aims NOW
    //     (he steers it, turns and moves with it; Prime's stays where it started) and flatter (about 15 degrees, Prime's
    //     37-53)
    //   - damage while it slides: every 4 frames (15 ticks) the body the beam stops on takes 10 (owner 2026-10-08 23:00:
    //     "more devastating"; was 50 shared out over the 15 ticks, about 13 for a sweep across a body, now about 40, 150
    //     held on it the whole slide); Double Damage and the Prime Hunter's 150% apply as to his other shots; no
    //     after-hit invulnerability
    //   - then its own 10 s wait (owner 23:00: "unmix the cool down so they can be chained but make the laser cooldown
    //     much longer"; was 5 s shared with the burst): the burst can start the moment the laser ends and the laser the
    //     moment a burst folds, each when its own wait is over; never both at once
    //   - bots: a press with their target alive within 12 units fires the laser if its wait is over and their last attack
    //     was a burst
    //   - looks: a plain red stand-in line (switched off when the app draws Prime's beam, HostOwnDroneLaser); the native
    //     drone body plays the clip (DroneBody.Animator, DroneLaserFrame)
    public partial class PlayerEntity
    {
        // per seat, set by the app when it draws / plays the drone's Prime look itself (Scout Drone MP1 beam session):
        // no stand-in line, no stand-in laser sound (frame 57), no Power Beam sound per burst volley (PlayerScoutDrone)
        public bool HostOwnDroneLaser { get; set; }
        public bool HostOwnDroneLaserSfx { get; set; }
        public bool HostOwnDroneShotSfx { get; set; }
        public bool HostOwnDroneSpawnSfx { get; set; }

        // set while the laser deals its damage (TakeDamage's source is the drone's player, as any direct hit)
        private static bool _droneLaserApplying;

        private const int _droneLaserFrames = 134; // A_cuttinglaser_drone, 2.233 s
        private const int _droneLaserOnAt = 57; // its DamageOn, 0.95 s
        private const int _droneLaserSlideFrames = 60; // Prime's sweep timer, 1 s from DamageOn
        private const int _droneLaserOffAt = 123; // its DamageOff, 2.05 s: the beam is gone
        private const int _droneLaserTickEvery = 4;
        private const int _droneLaserTicks = 15; // frames 57, 61 ... 113
        private const int _droneLaserTickDamage = 10; // each tick on a body: 150 if all 15 land
        // recomp host hook (MatchSettings.DroneLaserNoPush, set per match by MatchStart; older replays: off): the ticks push nothing
        public static bool HostDroneLaserNoPush { get; set; }
        private const int _droneLaserCooldownFrames = 600; // 10 s, its own (the burst has its 2 s)
        private const float _droneLaserUnit = 0.5f; // MPH units per Prime unit (the sweep's size)
        private const float _droneLaserTargetMin = 4f;
        private const float _droneLaserTargetMax = 40f;
        private const float _droneLaserReach = 60f;
        private const float _droneLaserRadius = 0.1f; // the beam's thickness in the hit test
        private const float _droneLaserBotRange = 12f;
        private const float _droneLaserDrawHalf = 0.04f; // the stand-in line's half width
        // the four sweeps (start right/up, end right/up, in Prime units around the target): Prime's are (3, 4) / (4, 3)
        // diagonals; ours are flatter (owner: "more of a horizontal but still angled sweep")
        private static readonly float[] _droneLaserPatterns =
        {
            4.8f, -1.3f, -4.8f, 1.3f, 4.8f, 1.3f, -4.8f, -1.3f, -4.8f, -1.3f, 4.8f, 1.3f, -4.8f, 1.3f, 4.8f, -1.3f,
        };

        private int _droneLaserFrame = -1; // frames into the laser (-1: off)
        private int _droneLaserWait = 0; // frames before the next laser
        private Vector3 _droneLaserAim = Vector3.UnitZ; // where he aims, this frame
        private Vector3 _droneLaserUp = Vector3.UnitY;
        private float _droneLaserDist = 10; // the target's distance along the aim (set at the start)
        private int _droneLaserPattern = 0;
        private Vector3 _droneLaserOrigin = Vector3.Zero; // this frame's beam (when on)
        private Vector3 _droneLaserEnd = Vector3.Zero;
        private bool _droneLaserHitting = false;
        private Vector3 _droneLaserNormal = Vector3.Zero; // what the beam ends on faces this way (zero: nothing, in the air)
        private bool _droneBotLaser = false; // a bot's press chose the laser
        private bool _droneBotLaserLast = false;
        private ModelInstance? _droneLaserTrail = null;
        private int _droneLaserBinding = 0;

        // for tests (-dronetest) and the draw side (DroneBody, the beam session)
        public int DroneLasers { get; private set; }
        public int DroneLaserDealt { get; private set; } // damage this laser handed out (before Double Damage)
        public int DroneLaserCooldown => _droneLaserWait;
        // (a drone that dies mid-laser keeps its state until the respawn resets it, as the burst: never shown)
        public int DroneLaserFrame => _droneLaserFrame >= 0 && _health > 0 && IsAltForm ? _droneLaserFrame : -1;
        public bool DroneLaserOn => DroneLaserFrame >= _droneLaserOnAt && _droneLaserFrame < _droneLaserOffAt;
        public bool DroneLaserDamaging => DroneLaserFrame >= _droneLaserOnAt
            && _droneLaserFrame < _droneLaserOnAt + _droneLaserSlideFrames;
        public Vector3 DroneLaserOrigin => _droneLaserOrigin;
        public Vector3 DroneLaserEnd => _droneLaserEnd;
        public bool DroneLaserHitting => _droneLaserHitting;
        public Vector3 DroneLaserNormal => _droneLaserNormal;

        // where the drone body points: the laser's aim while it runs, else his facing (draw only)
        public Vector3 DroneDrawAim => DroneLaserFrame >= 0 ? _droneLaserAim : FacingVector;

        // ScoutDroneInit, a drone seat: the stand-in line's texture
        private void ScoutDroneLaserInit()
        {
            if (_droneLaserTrail == null)
            {
                _droneLaserTrail = Read.GetModelInstance("trail");
                Material material = _droneLaserTrail.Model.Materials[0];
                _droneLaserBinding = _scene.BindGetTexture(_droneLaserTrail.Model, material.TextureId, material.PaletteId, 0);
            }
        }

        // a bot's attack press, folded: the laser when its wait is over, its target is close and its last attack was a burst
        private bool ScoutDroneBotWantsLaser()
        {
            if (_droneLaserWait > 0 || _droneBotLaserLast || AiData.HostTargetPlayer is not PlayerEntity target || target.Health == 0)
            {
                return false;
            }
            return Vector3.DistanceSquared(target.Position, DroneDrawCenter) <= _droneLaserBotRange * _droneLaserBotRange;
        }

        private static Vector3 DroneLaserBodyCenter(PlayerEntity player)
        {
            if (player.IsAltForm)
            {
                return player.Volume.SpherePosition;
            }
            float min = Fixed.ToFloat(player.Values.MinPickupHeight);
            float max = Fixed.ToFloat(player.Values.MaxPickupHeight);
            return player.Position.AddY((min + max) / 2);
        }

        // where the laser aims this frame (bots: at their target's body) and its right / up; true for a bot with a target
        private bool DroneLaserAimNow(out Vector3 aim, out Vector3 right, out Vector3 up, out float targetDist)
        {
            Vector3 center = DroneDrawCenter;
            aim = _gunVec1;
            targetDist = -1;
            bool bot = false;
            if (IsBot && AiData.HostTargetPlayer is PlayerEntity botTarget && botTarget.Health > 0)
            {
                aim = DroneLaserBodyCenter(botTarget) - center;
                targetDist = aim.Length;
                bot = true;
            }
            if (aim.LengthSquared < 1e-6f)
            {
                aim = new Vector3(_field70, 0, _field74);
            }
            aim = aim.LengthSquared < 1e-6f ? Vector3.UnitZ : aim.Normalized();
            DroneLaserBasis(aim, new Vector3(_field74, 0, -_field70), out right, out up);
            return bot;
        }

        private void ScoutDroneLaserStart()
        {
            Vector3 center = DroneDrawCenter;
            DroneLaserAimNow(out Vector3 aim, out _, out Vector3 up, out float targetDist);
            if (targetDist < 0)
            {
                // Prime aims at the player in front of it: the first body along the aim, else the first wall
                Vector3 far = center + aim * _droneLaserTargetMax;
                var col = new CollisionResult();
                if (CollisionDetection.CheckBetweenPoints(center, far, TestFlags.Beams, _scene, ref col))
                {
                    far = col.Position;
                }
                targetDist = Vector3.Distance(center, far);
                if (DroneLaserBodyHit(center, far, 0.5f, out float bodyAt, out _, out _))
                {
                    targetDist *= bodyAt;
                }
            }
            _droneLaserDist = Math.Clamp(targetDist, _droneLaserTargetMin, _droneLaserTargetMax);
            _droneLaserPattern = (int)Rng.GetRandomInt2(4);
            _droneLaserAim = aim;
            _droneLaserUp = up;
            _droneLaserFrame = 0;
            _droneLaserHitting = false;
            _droneBotLaserLast = true;
            DroneLaserDealt = 0;
            DroneLasers++;
        }

        // every frame while it runs (ScoutDroneFire); able: alive, not morphing, not frozen (else it stops, and waits)
        private void ScoutDroneLaserStep(bool able)
        {
            if (!able)
            {
                ScoutDroneLaserEnd();
                return;
            }
            int f = _droneLaserFrame;
            _droneLaserHitting = false;
            if (f >= _droneLaserOnAt && f < _droneLaserOffAt)
            {
                // around where he aims now: he steers it, and it turns and moves with him
                Vector3 center = DroneDrawCenter;
                DroneLaserAimNow(out Vector3 aim, out Vector3 right, out Vector3 up, out _);
                _droneLaserAim = aim;
                _droneLaserUp = up;
                DroneLaserSweep(center + aim * _droneLaserDist, right, up, _droneLaserPattern, out Vector3 from,
                    out Vector3 to);
                DroneLaserRay(center, aim, up, from, to, f, out Vector3 origin, out Vector3 dir);
                Vector3 end = origin + dir * _droneLaserReach;
                var col = new CollisionResult();
                _droneLaserNormal = Vector3.Zero;
                if (CollisionDetection.CheckBetweenPoints(origin, end, TestFlags.Beams, _scene, ref col))
                {
                    end = col.Position;
                    _droneLaserNormal = col.Plane.Xyz.LengthSquared > 1e-6f ? col.Plane.Xyz.Normalized() : -dir;
                }
                if (DroneLaserBodyHit(origin, end, _droneLaserRadius, out float at, out PlayerEntity? hit, out bool half))
                {
                    end = origin + (end - origin) * at;
                    _droneLaserHitting = true;
                    _droneLaserNormal = -dir;
                    int tick = f - _droneLaserOnAt;
                    if (hit != null && tick % _droneLaserTickEvery == 0 && tick / _droneLaserTickEvery < _droneLaserTicks)
                    {
                        int damage = _droneLaserTickDamage;
                        DroneLaserDealt += damage;
                        // as his other shots: Double Damage (TakeDamage doubles it for a player source), else the Prime
                        // Hunter's 150%
                        if (_doubleDmgTimer == 0 && IsPrimeHunter)
                        {
                            damage = damage * 150 / 100;
                        }
                        DamageFlags flags = DamageFlags.NoDmgInvuln | (half ? DamageFlags.Halfturret : DamageFlags.None);
                        _droneLaserApplying = true; // its kill message: (SENTRY DRONE) (PlayerScoutDrone.DroneHit)
                        try
                        {
                            // (recomp, HostDroneLaserNoPush, 2026-10-19: it rarely sent a target flying): no push,
                            // as the Shock Coil (a continuous beam: dmgDirType 3, magnitude 0); the hit still flinches them away
                            // from him. The laser's unit direction went into the target's speed on every tick, 15 a laser.
                            hit.TakeDamage((uint)damage, flags, HostDroneLaserNoPush ? null : dir, this);
                        }
                        finally
                        {
                            _droneLaserApplying = false;
                        }
                    }
                }
                _droneLaserOrigin = origin;
                _droneLaserEnd = end;
                if (f == _droneLaserOnAt && !HostOwnDroneLaserSfx)
                {
                    // stand-in until the beam session plays Prime's laser sound
                    _soundSource.PlaySfx(Metadata.BeamSfx[(int)BeamType.Imperialist, (int)BeamSfx.Shot]);
                }
            }
            if (++_droneLaserFrame >= _droneLaserFrames)
            {
                ScoutDroneLaserEnd();
            }
        }

        // the laser is over (ended, stopped, or merged mid-laser): its own wait starts; the burst's is left as it was
        private void ScoutDroneLaserEnd()
        {
            ScoutDroneBurstReset(_droneCooldown);
            _droneLaserWait = _droneLaserCooldownFrames;
        }

        // the nearest body on the segment (players and standing halves, never his own), as a beam's hit test
        // (BeamProjectileEntity): at = 0..1 along it; half: a Weavel/Trooper standing half took it
        private bool DroneLaserBodyHit(Vector3 from, Vector3 to, float radius, out float at, out PlayerEntity? hit,
            out bool half)
        {
            at = 2;
            hit = null;
            half = false;
            foreach (PlayerEntity player in _scene.GetPlayerEntities())
            {
                if (player.Health == 0 || player == this)
                {
                    continue;
                }
                // recomp (HostLagFix): an online host tests the sweep against where this Trooper saw the player (his aim steers it)
                Vector3 lag = HostLagFix ? NetLagShift(this, player) : Vector3.Zero;
                if (DroneLaserHitsPlayer(player, from - lag, to - lag, radius, out float res) && res < at)
                {
                    at = res;
                    hit = player;
                    half = false;
                }
                if (player.Hunter == Hunter.Weavel && player.Flags2.TestFlag(PlayerFlags2.Halfturret))
                {
                    CollisionResult turretRes = default;
                    if ((player.HostScoutDrone // a Trooper's standing body: the whole biped (HalfturretEntity.HostBodyHit)
                        ? player.Halfturret.HostBodyHit(from, to, radius, ref turretRes)
                        : CollisionDetection.CheckCylinderOverlapSphere(from, to, player.Halfturret.Position, radius + 0.45f,
                        ref turretRes)) && turretRes.Distance < at)
                    {
                        at = turretRes.Distance;
                        hit = player;
                        half = true;
                    }
                }
            }
            return hit != null;
        }

        // a player's body (not a standing half) on the segment, as a beam's hit test: at = 0..1 along it
        private static bool DroneLaserHitsPlayer(PlayerEntity player, Vector3 from, Vector3 to, float radius, out float at)
        {
            CollisionResult res = default;
            float radii = player.Volume.SphereRadius + radius;
            bool overlap;
            if (player.IsAltForm)
            {
                overlap = CollisionDetection.CheckCylinderOverlapSphere(from, to, player.Volume.SpherePosition, radii, ref res);
            }
            else
            {
                float minY = Fixed.ToFloat(player.Values.MinPickupHeight);
                float height = Fixed.ToFloat(player.Values.MaxPickupHeight) - minY;
                overlap = CollisionDetection.CheckCylindersOverlap(from, to, player.Position.AddY(minY), Vector3.UnitY,
                    height, radii, ref res);
            }
            at = res.Distance;
            return overlap;
        }

        // right (level) and up of an aim
        private static void DroneLaserBasis(Vector3 aim, Vector3 fallbackRight, out Vector3 right, out Vector3 up)
        {
            float hLen = MathF.Sqrt(aim.X * aim.X + aim.Z * aim.Z);
            right = hLen < 1e-4f ? fallbackRight : new Vector3(aim.Z / hLen, 0, -aim.X / hLen);
            up = Vector3.Cross(aim, right).Normalized();
        }

        // one of Prime's four sweeps through the target: where the beam's aim point starts and ends
        private static void DroneLaserSweep(Vector3 target, Vector3 right, Vector3 up, int pattern, out Vector3 from,
            out Vector3 to)
        {
            int p = pattern * 4;
            from = target + (right * _droneLaserPatterns[p] + up * _droneLaserPatterns[p + 1]) * _droneLaserUnit;
            to = target + (right * _droneLaserPatterns[p + 2] + up * _droneLaserPatterns[p + 3]) * _droneLaserUnit;
        }

        // the beam at laser frame f: from the drone's Beacon_LCTR toward the sliding aim point
        private static void DroneLaserRay(Vector3 center, Vector3 aim, Vector3 up, Vector3 from, Vector3 to, int f,
            out Vector3 origin, out Vector3 dir)
        {
            float t = Math.Clamp((f - _droneLaserOnAt) / (float)_droneLaserSlideFrames, 0, 1);
            Vector3 point = from + (to - from) * t;
            origin = center + aim * 0.16f - up * 0.17f;
            dir = point - origin;
            dir = dir.LengthSquared < 1e-6f ? aim : dir.Normalized();
        }


        // -dronetest: what one laser would deal `target` standing still where it is now, fired by a drone at `center` aimed
        // at it (Prime's rule: the target is the body), with sweep `pattern` (0..3); no walls
        public static int DroneLaserStillTarget(PlayerEntity target, Vector3 center, int pattern)
        {
            Vector3 aim = DroneLaserBodyCenter(target) - center;
            float dist = Math.Clamp(aim.Length, _droneLaserTargetMin, _droneLaserTargetMax);
            aim = aim.Normalized();
            DroneLaserBasis(aim, Vector3.UnitX, out Vector3 right, out Vector3 up);
            DroneLaserSweep(center + aim * dist, right, up, pattern, out Vector3 from, out Vector3 to);
            int dealt = 0;
            for (int i = 0; i < _droneLaserTicks; i++)
            {
                DroneLaserRay(center, aim, up, from, to, _droneLaserOnAt + i * _droneLaserTickEvery, out Vector3 origin,
                    out Vector3 dir);
                if (DroneLaserHitsPlayer(target, origin, origin + dir * _droneLaserReach, _droneLaserRadius, out _))
                {
                    dealt += _droneLaserTickDamage;
                }
            }
            return dealt;
        }

        // DrawScoutDrone: the stand-in beam, a red line seen from any side (two crossed quads, as a beam's trail)
        private void DrawDroneLaser()
        {
            if (HostOwnDroneLaser || !DroneLaserOn || _droneLaserTrail == null)
            {
                return;
            }
            Vector3 span = _droneLaserEnd - _droneLaserOrigin;
            if (span.LengthSquared < 1e-6f)
            {
                return;
            }
            Vector3 dir = span.Normalized();
            Vector3 side = Vector3.Cross(dir, _droneLaserUp);
            side = side.LengthSquared < 1e-6f ? Vector3.UnitX : side.Normalized();
            Vector3 up = Vector3.Cross(side, dir).Normalized();
            Texture texture = _droneLaserTrail.Model.Recolors[0].Textures[0];
            float uvS = (texture.Width - (1 / 16f)) / texture.Width;
            float uvT = (texture.Height - (1 / 16f)) / texture.Height;
            Material material = _droneLaserTrail.Model.Materials[0];
            var color = new Vector3(1f, 0.15f, 0.1f);
            for (int q = 0; q < 2; q++)
            {
                Vector3 off = (q == 0 ? up : side) * _droneLaserDrawHalf;
                Vector3[] uvsAndVerts = ArrayPool<Vector3>.Shared.Rent(8);
                uvsAndVerts[0] = Vector3.Zero;
                uvsAndVerts[1] = span - off;
                uvsAndVerts[2] = new Vector3(0, uvT, 0);
                uvsAndVerts[3] = span + off;
                uvsAndVerts[4] = new Vector3(uvS, 0, 0);
                uvsAndVerts[5] = -off;
                uvsAndVerts[6] = new Vector3(uvS, uvT, 0);
                uvsAndVerts[7] = off;
                _scene.AddRenderItem(RenderItemType.TrailSingle, 1f, _scene.GetNextPolygonId(), color, material.XRepeat,
                    material.YRepeat, material.ScaleS, material.ScaleT, Matrix4.CreateTranslation(_droneLaserOrigin),
                    uvsAndVerts, _droneLaserBinding);
            }
        }
    }
}
