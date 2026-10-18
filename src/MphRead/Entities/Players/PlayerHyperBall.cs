using System;
using System.Buffers;
using System.Diagnostics;
using MphRead.Formats;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: Dark Samus's morph ball (docs/DARK_SAMUS_HYPER.md H1, owner 2026-10-12). Host-guarded: off unless the match
    // host sets the flags for her seat (MatchStart).
    // - HostBallJump: the jump button hops the ball off the floor as high as a bomb jump (Values.BombJumpSpeed, the speed
    //   a bomb gives its owner in CheckHitByBomb), at once and without a blast (newer Prime games' morph ball jump; the
    //   owner wants it in the campaign too, so it's its own flag).
    // - HostHyperBall: no bombs; holding the bomb button is Corruption's Hyper Ball: phazon spikes lock on to the nearest
    //   enemies in range and hurt them while it lasts, paid from Universal Ammo (a start cost, then every frame).
    // Sim: both change how a match plays (replay format 32, Settings.HyperBall). The stand-in look is drawn here (phazon
    // lightning from MPH's trail texture); the host's own look (Corruption's PhazonBallSpikes, H4) sets HostOwnHyperBallLook.
    public partial class PlayerEntity
    {
        public bool HostBallJump { get; set; }
        public bool HostHyperBall { get; set; }
        // the host draws the Hyper Ball itself (Corruption's effects): no stand-in lightning
        public bool HostOwnHyperBallLook { get; set; }
        // ... and Corruption's sounds (its hum, hits): MphRead's stand-in sounds are then not played
        public bool HostOwnHyperBallSfx { get; set; }
        // the dev launcher's solo run (MatchSettings.Solo: alone, no UA pickups to grab first): every life starts with full
        // UA, and it fills up again _hyperSoloRefillFrames after the Hyper Ball stops (for judging the drain)
        public bool HostSoloUa { get; set; }
        // Corruption's own rules (owner 2026-10-12 "Match corruption", read from MP3 main.dol + TweakBall,
        // docs/MP3_HYPER_EFFECTS_FORMATS.md section 3; replay format 34, Settings.HyperBallCorruption): every enemy in
        // reach is zapped in turn (one a frame, each then waits _corZapWait), and arcs to the surfaces around the ball kick
        // it about (every third one). Off = the starting rules above (older replays)
        public bool HostHyperBallCorruption { get; set; }

        // starting numbers (owner 2026-10-12 "start with your numbers"; 60 Hz frames)
        private const int _hyperStartCost = 30;     // UA to start
        private const int _hyperFrameCost = 4;      // UA per frame held (a full 599 lasts ~2.4 s)
        private const float _hyperRange = 5f;       // lock-on reach from the ball's centre
        private const int _hyperMaxTargets = 2;
        private const int _hyperTickFrames = 15;    // a target is hurt every this many frames...
        private const int _hyperTickDamage = 12;    // ...for this much (~48 per second per target)
        private const int _hyperEmptyCooldown = 60; // ran out of UA: 1 s before it starts again
        private const int _hyperReleaseCooldown = 10;
        private const int _hyperBotReserve = 60;    // a bot starts it only with this much UA
        private const int _hyperSoloRefillFrames = 90; // HostSoloUa: full UA again 1.5 s after it stops
        // Corruption's numbers, its units turned into MPH's by Dark Samus's height (3 there, 1.7 here)
        private const float _corUnit = 1.7f / 3f;
        private const float _corRange = 7.5f * _corUnit;   // TweakBall's red-arc DamageInfo radius
        private const float _corZapWait = 0.032f;           // seconds a zapped target waits (TweakBall +0x270)
        private const int _corZapDamage = 2;                // that DamageInfo's damage (no knockback)
        private const float _corArcBox = 2 * _corUnit;      // the surfaces: triangles touching a box this far around the ball
        private const int _corArcCap = 16;                  // PhazonBallSpikes SCNT: the most arcs alive
        private const float _corArcAreaPer = 60;            // surface (Corruption units squared) a try costs: 1 / (GRAT 1 x dt)

        private bool _hyperBallHeld;      // the bomb button held this frame in the ball (HyperBallInput)
        private bool _hyperBallActive;
        private ushort _hyperBallFrames;  // frames active (the stand-in's flicker)
        private ushort _hyperBallCooldown;
        private ushort _hyperSoloRefill;
        private readonly PlayerEntity?[] _hyperTargets = new PlayerEntity?[_hyperMaxTargets];
        private readonly byte[] _hyperTargetTicks = new byte[PlayerEntity.SlotCapacity];
        private byte _hyperNetFrames;     // a client's puppet: frames the host's state still holds (NetApplyHyperBall)
        private bool _hyperBotHeld;
        private bool _hyperBotJump;
        private readonly float[] _hyperZapWait = new float[PlayerEntity.SlotCapacity];
        private readonly PlayerEntity?[] _hyperInReach = new PlayerEntity?[PlayerEntity.SlotCapacity];
        private readonly byte[] _hyperArcLife = new byte[_corArcCap];
        private readonly HyperArc[] _hyperNewArcs = new HyperArc[_corArcCap];
        private readonly System.Collections.Generic.HashSet<(MphCollisionInfo, int)> _hyperSeenTris = new();
        private int _hyperArcsLive;
        private int _hyperKickCount = 3;
        private float _hyperArea;
        private uint _hyperRng = 1;
        private ModelInstance? _hyperTrail = null;
        private int _hyperBinding = 0;

        // for tests and the draw side (H4)
        public bool HyperBallActive => _hyperBallActive;
        public PlayerEntity? HyperBallTarget(int i) => i >= 0 && i < _hyperMaxTargets ? _hyperTargets[i] : null;
        public int HyperBallDealt { get; private set; }
        // Corruption's rules, this frame (the draw side's arcs; HyperBallSimFx = the simulation chose them here): the
        // target zapped, and the arcs added to the surfaces (End and Normal in the world, Vel = the arc's start velocity
        // in Corruption units a second, world axes; Life in frames)
        public readonly record struct HyperArc(Vector3 End, Vector3 Normal, Vector3 Vel, int Life);
        public PlayerEntity? HyperBallZapped { get; private set; }
        public int HyperBallNewArcs { get; private set; }
        public HyperArc HyperBallNewArc(int i) => _hyperNewArcs[i];
        public bool HyperBallSimFx => HostHyperBallCorruption; // (a client's puppet runs them too, for its look: NetApplyHyperBall)
        public const int HyperBallTickFrames = _hyperTickFrames; // (the draw side times its lock-on arcs by it)
        public int BallJumps { get; private set; }
        public int HyperBallUa { get => _ammo[UA]; set => _ammo[UA] = value; } // (tests)
        public int HyperBallBombAmmo => _bombAmmo;

        // the alt form's input (PlayerInput, Samus's branch): the hop and the button held
        private void HyperBallInput()
        {
            if (HostBallJump && (Controls.Jump.IsPressed || TouchJumpPressed) && Flags1.TestFlag(PlayerFlags1.Standing))
            {
                float ySpeed = Fixed.ToFloat(Values.BombJumpSpeed);
                if (Speed.Y < ySpeed)
                {
                    Speed = Speed.WithY(ySpeed);
                    BallJumps++;
                    _soundSource.PlaySfx(Metadata.HunterSfx[(int)Hunter, (int)HunterSfx.Bounce]);
                }
            }
            if (HostHyperBall)
            {
                _hyperBallHeld = Controls.AltAttack.IsDown;
            }
        }

        // every frame of an active player (ProcessPlayer)
        private void HyperBallProcess()
        {
            if (!HostHyperBall)
            {
                return;
            }
            bool held = _hyperBallHeld;
            _hyperBallHeld = false;
            HyperBallZapped = null;
            HyperBallNewArcs = 0;
            if (_hyperNetFrames > 0)
            {
                // a client's puppet shows the host's state (NetApplyHyperBall): on or off as the host says, and by
                // Corruption's rules its arcs and zaps chosen here as on the host, for the look only (the host hurts, and
                // its pose carries the kicks): every target in reach on this screen, not just the two the host names
                _hyperNetFrames--;
                if (_hyperBallActive)
                {
                    _hyperBallFrames++;
                    if (HostHyperBallCorruption)
                    {
                        HyperBallZap(hurt: false);
                        HyperBallSurfaceArcs(kick: false);
                    }
                }
                return;
            }
            if (HostSoloUa)
            {
                HyperBallSoloRefill();
            }
            if (_hyperBallCooldown > 0)
            {
                _hyperBallCooldown--;
            }
            bool can = _health > 0 && IsAltForm && !IsMorphing;
            if (!_hyperBallActive)
            {
                if (held && can && _hyperBallCooldown == 0 && HyperBallPay(_hyperStartCost))
                {
                    HyperBallStart();
                }
                else
                {
                    return;
                }
            }
            else if (!held || !can)
            {
                HyperBallStop(_hyperReleaseCooldown);
                return;
            }
            else if (!HyperBallPay(_hyperFrameCost))
            {
                HyperBallStop(_hyperEmptyCooldown);
                return;
            }
            _hyperBallFrames++;
            HyperBallFindTargets(_hyperTargets);
            if (HostHyperBallCorruption)
            {
                HyperBallZap(hurt: true);
                HyperBallSurfaceArcs(kick: true);
                return;
            }
            for (int i = 0; i < _hyperTargets.Length; i++)
            {
                PlayerEntity? target = _hyperTargets[i];
                if (target == null)
                {
                    continue;
                }
                // each target's own clock: hurt on lock-on, then every _hyperTickFrames while it stays locked
                ref byte tick = ref _hyperTargetTicks[target.SlotIndex];
                if (tick == 0)
                {
                    int damage = _hyperTickDamage * (_doubleDmgTimer > 0 ? 2 : 1);
                    Vector3 dir = target.Volume.SpherePosition - Volume.SpherePosition;
                    dir = dir.LengthSquared < 1e-6f ? Vector3.Zero : dir.Normalized() * 0.1f;
                    target.TakeDamage(damage, DamageFlags.NoDmgInvuln, dir, this);
                    HyperBallDealt += damage;
                    if (!HostOwnHyperBallSfx)
                    {
                        _soundSource.PlaySfx(SfxId.ELECTRICITY);
                    }
                    tick = _hyperTickFrames;
                }
                tick--;
            }
            for (int s = 0; s < _hyperTargetTicks.Length; s++)
            {
                if (!HyperBallLocked(s))
                {
                    _hyperTargetTicks[s] = 0; // (a target that comes back is hurt at once)
                }
            }
        }

        private bool HyperBallLocked(int slot)
        {
            for (int i = 0; i < _hyperTargets.Length; i++)
            {
                if (_hyperTargets[i]?.SlotIndex == slot)
                {
                    return true;
                }
            }
            return false;
        }

        // UA: the start cost or a frame's; false (nothing paid) when there isn't enough. -1 = unlimited
        private bool HyperBallPay(int cost)
        {
            if (_ammo[UA] == -1)
            {
                return true;
            }
            if (_ammo[UA] < cost)
            {
                return false;
            }
            _ammo[UA] -= cost;
            return true;
        }

        private void HyperBallStart()
        {
            _hyperBallActive = true;
            _hyperBallFrames = 0;
            Array.Clear(_hyperTargetTicks);
            if (!HostOwnHyperBallSfx)
            {
                _soundSource.PlaySfx(SfxId.DOUBLE_DAMAGE_POWER_UP); // stand-ins without Corruption's
                _soundSource.PlaySfx(SfxId.ENERGY_BALL, loop: true);
            }
        }

        private void HyperBallStop(int cooldown)
        {
            _hyperBallActive = false;
            _hyperBallCooldown = (ushort)cooldown;
            Array.Clear(_hyperTargets);
            Array.Clear(_hyperTargetTicks);
            Array.Clear(_hyperZapWait);
            _hyperArea = 0;
            if (!HostOwnHyperBallSfx)
            {
                _soundSource.StopSfx(SfxId.ENERGY_BALL);
                _soundSource.PlaySfx(SfxId.CLOAK_OFF);
            }
        }

        // every spawn: off, no wait
        private void HyperBallReset()
        {
            if (_hyperBallActive && !HostOwnHyperBallSfx)
            {
                _soundSource.StopSfx(SfxId.ENERGY_BALL);
            }
            _hyperBallActive = false;
            _hyperBallHeld = false;
            _hyperBallCooldown = 0;
            _hyperNetFrames = 0;
            _hyperSoloRefill = 0;
            if (HostSoloUa && _ammoMax[UA] > 0)
            {
                _ammo[UA] = _ammoMax[UA];
            }
            Array.Clear(_hyperTargets);
            Array.Clear(_hyperTargetTicks);
            Array.Clear(_hyperZapWait);
            Array.Clear(_hyperArcLife);
            _hyperArcsLive = 0;
            _hyperKickCount = 3;
            _hyperArea = 0;
            _hyperRng = 0x9E3779B9u * (uint)(SlotIndex + 1); // its own stream: the match's RNG isn't touched
        }

        private void HyperBallSoloRefill()
        {
            if (_hyperBallActive || _ammo[UA] == -1 || _ammo[UA] >= _ammoMax[UA])
            {
                _hyperSoloRefill = 0;
                return;
            }
            if (++_hyperSoloRefill >= _hyperSoloRefillFrames)
            {
                _ammo[UA] = _ammoMax[UA];
                _hyperSoloRefill = 0;
            }
        }

        // the nearest enemies (alive, not a teammate) within reach with nothing solid in between (Corruption's rules: walls
        // or not); nearest first, ties by slot
        private void HyperBallFindTargets(PlayerEntity?[] into)
        {
            Array.Clear(into);
            Span<float> dist = stackalloc float[into.Length];
            Vector3 center = Volume.SpherePosition;
            float range = HostHyperBallCorruption ? _corRange : _hyperRange;
            foreach (PlayerEntity player in _scene.GetPlayerEntities())
            {
                if (player == this || player.Health == 0 || !player.LoadFlags.TestFlag(LoadFlags.Active)
                    || GameState.Teams && player.TeamIndex == TeamIndex)
                {
                    continue;
                }
                Vector3 to = player.Volume.SpherePosition;
                float d;
                if (HostHyperBallCorruption)
                {
                    // Corruption (its red pick, 0x80101b10 / 0x80317008): in reach when the target's touch box comes within
                    // the reach of the ball's centre, and nearest = the smallest gap between the ball's box and the
                    // target's (0 when they overlap; ties keep the players' order). A player's volume here is a sphere,
                    // so its box is the sphere's
                    var half = new Vector3(player.Volume.SphereRadius);
                    if (HyperBoxOut(center, to - half, to + half).LengthSquared > range * range)
                    {
                        continue;
                    }
                    var mine = new Vector3(Volume.SphereRadius);
                    d = HyperBoxGap(center - mine, center + mine, to - half, to + half);
                }
                else
                {
                    d = Vector3.DistanceSquared(center, to);
                    if (d > range * range)
                    {
                        continue;
                    }
                }
                CollisionResult res = default;
                // (Corruption's own picks no line of sight: walls don't shield a target)
                if (!HostHyperBallCorruption && CollisionDetection.CheckBetweenPoints(center, to, TestFlags.Beams, _scene, ref res))
                {
                    continue;
                }
                for (int i = 0; i < into.Length; i++)
                {
                    if (into[i] == null || d < dist[i])
                    {
                        for (int j = into.Length - 1; j > i; j--)
                        {
                            into[j] = into[j - 1];
                            dist[j] = dist[j - 1];
                        }
                        into[i] = player;
                        dist[i] = d;
                        break;
                    }
                }
            }
        }

        // how far a point is outside a box, per axis (0 inside)
        private static Vector3 HyperBoxOut(Vector3 p, Vector3 min, Vector3 max)
        {
            return Vector3.ComponentMax(Vector3.ComponentMax(min - p, p - max), Vector3.Zero);
        }

        // the gap between two boxes (Corruption's 0x80317008: the length of the per-axis gaps, 0 when they overlap)
        private static float HyperBoxGap(Vector3 minA, Vector3 maxA, Vector3 minB, Vector3 maxB)
        {
            Vector3 gap = Vector3.ComponentMax(Vector3.ComponentMax(minB - maxA, minA - maxB), Vector3.Zero);
            return gap.Length;
        }

        // Corruption's red: each frame every target's wait runs down by the frame, then the nearest enemy in reach that
        // isn't waiting is zapped (one a frame), and waits _corZapWait: a lone target about every 2 frames
        private void HyperBallZap(bool hurt)
        {
            for (int s = 0; s < _hyperZapWait.Length; s++)
            {
                _hyperZapWait[s] -= 1 / 60f;
            }
            HyperBallFindTargets(_hyperInReach);
            for (int i = 0; i < _hyperInReach.Length; i++)
            {
                PlayerEntity? target = _hyperInReach[i];
                if (target == null || _hyperZapWait[target.SlotIndex] > 0)
                {
                    continue;
                }
                if (hurt)
                {
                    int damage = _corZapDamage * (_doubleDmgTimer > 0 ? 2 : 1);
                    target.TakeDamage(damage, DamageFlags.NoDmgInvuln, Vector3.Zero, this);
                    HyperBallDealt += damage;
                }
                if (!HostOwnHyperBallSfx)
                {
                    _soundSource.PlaySfx(SfxId.ELECTRICITY);
                }
                _hyperZapWait[target.SlotIndex] = _corZapWait;
                HyperBallZapped = target;
                break;
            }
        }

        // Corruption's blue arcs (its SpawnSurfaceArcs): every surface triangle touching a box around the ball adds its area
        // to a running sum, which buys tries at an arc to a random point on it, each kept with the odds for its distance
        // (HyperArcOdds). Every third arc kicks the ball away from the arc's start velocity (the morph ball's jitter:
        // CPhysicsActor::ApplyImpulseWR, -800 x life x velocity on a mass of 200). Its random numbers start over each frame
        // from the seat and the frames the ball has been on, so a client's own player rolls the same kicks as the host's
        // copy of it (a client that rejoined, or a puppet, too)
        private void HyperBallSurfaceArcs(bool kick)
        {
            uint h = (uint)(SlotIndex + 1) * 0x9E3779B9u ^ (uint)_hyperBallFrames * 0x85EBCA6Bu;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            _hyperRng = h == 0 ? 1 : h;
            int live = 0;
            for (int k = 0; k < _hyperArcsLive; k++)
            {
                if (--_hyperArcLife[k] > 0)
                {
                    _hyperArcLife[live++] = _hyperArcLife[k];
                }
            }
            _hyperArcsLive = live;
            Vector3 center = Volume.SpherePosition;
            var box = new Vector3(_corArcBox);
            Vector3 lo = center - box, hi = center + box;
            System.Collections.Generic.IReadOnlyList<CollisionCandidate> cands
                = CollisionDetection.GetCandidatesForLimits(null, Vector3.Zero, 0, lo, hi, includeEntities: false, _scene);
            _hyperSeenTris.Clear();
            foreach (CollisionCandidate cand in cands)
            {
                if (cand.Collision.Info is not MphCollisionInfo info || cand.EntityCollision != null)
                {
                    continue;
                }
                Vector3 off = cand.Collision.Translation;
                for (int j = 0; j < cand.Entry.DataCount; j++)
                {
                    int index = info.DataIndices[cand.Entry.DataStartIndex + j];
                    if (!_hyperSeenTris.Add((info, index)))
                    {
                        continue;
                    }
                    CollisionData data = info.Data[index];
                    Vector3 normal = info.Planes[data.PlaneIndex].Xyz;
                    Vector3 a = info.Points[info.PointIndices[data.PointStartIndex]] + off;
                    for (int k = 1; k + 1 < data.PointIndexCount; k++)
                    {
                        Vector3 b = info.Points[info.PointIndices[data.PointStartIndex + k]] + off;
                        Vector3 c = info.Points[info.PointIndices[data.PointStartIndex + k + 1]] + off;
                        Vector3 max = Vector3.ComponentMax(Vector3.ComponentMax(a, b), c);
                        Vector3 min = Vector3.ComponentMin(Vector3.ComponentMin(a, b), c);
                        if (max.X < lo.X || min.X > hi.X || max.Y < lo.Y || min.Y > hi.Y || max.Z < lo.Z || min.Z > hi.Z)
                        {
                            continue;
                        }
                        _hyperArea += Vector3.Cross(b - a, c - a).Length / 2 / (_corUnit * _corUnit);
                        int n = (int)(HyperRand() * 2 * _hyperArea / _corArcAreaPer);
                        for (int m = 0; m < n; m++)
                        {
                            if (_hyperArcsLive >= _corArcCap)
                            {
                                _hyperArea = 0;
                                return;
                            }
                            _hyperArea -= _corArcAreaPer;
                            float u = HyperRand(), v = HyperRand();
                            Vector3 at = a * u + b * ((1 - u) * v) + c * ((1 - u) * (1 - v));
                            if (HyperRand() > HyperArcOdds((at - center).Length / _corUnit))
                            {
                                continue;
                            }
                            HyperBallAddArc(center, at, normal, kick);
                        }
                    }
                }
            }
        }

        // one arc: its life (PhazonBallSpikes SLIF 3-16 frames) and start velocity (IEMT: a direction from two random
        // angles, -4 to 4 units a second, turned to face the arc's end), then every third kicks the ball
        private void HyperBallAddArc(Vector3 center, Vector3 at, Vector3 normal, bool kick)
        {
            int life = 3 + (int)(HyperRand() * 14);
            float x = HyperRand() * MathF.Tau, y = HyperRand() * MathF.Tau;
            (float sx, float cx) = DetMathF.SinCos(x);
            (float sy, float cy) = DetMathF.SinCos(y);
            // Retro's look-at frame: +Y toward the end, Z up
            Vector3 fwd = at - center;
            fwd = fwd.LengthSquared > 1e-8f ? fwd.Normalized() : Vector3.UnitZ;
            Vector3 right = Vector3.Cross(fwd, Vector3.UnitY);
            right = right.LengthSquared > 1e-8f ? right.Normalized() : Vector3.UnitX;
            Vector3 up = Vector3.Cross(right, fwd);
            float speed = -4 + 8 * HyperRand();
            Vector3 vel = (right * (-sy * cx) + fwd * sx + up * (cx * cy)) * speed;
            _hyperArcLife[_hyperArcsLive++] = (byte)life;
            _hyperNewArcs[HyperBallNewArcs++] = new HyperArc(at, normal, vel, life);
            if (--_hyperKickCount == 0)
            {
                _hyperKickCount = 3;
                if (!kick)
                {
                    return;
                }
                // dv = -800 x life (s) x vel / mass 200, Corruption units a second -> MPH units a 30 Hz frame
                Speed += vel * (-4f * life / 60f * _corUnit / 30f);
            }
        }

        // PhazonBallParams (Corruption's curve by distance, a key a tenth of a unit; 1 and over = always)
        private static float HyperArcOdds(float dist)
        {
            int i = (int)(dist * 10);
            if (i <= 0 || i >= 50)
            {
                return 0;
            }
            return i switch
            {
                1 => 0.364f,
                2 => 0.753f,
                < 30 => 1,
                30 => 0.966f,
                31 => 0.678f,
                32 => 0.304f,
                _ => 0.195f - 0.01148f * (i - 33),
            };
        }

        // the Hyper Ball's own random numbers (xorshift, reset each spawn), so the match's RNG streams stay as they were
        private float HyperRand()
        {
            uint r = _hyperRng;
            r ^= r << 13;
            r ^= r >> 17;
            r ^= r << 5;
            _hyperRng = r;
            return (r >> 8) * (1f / 16777216f);
        }

        // a bot with the Hyper Ball (after its AI picked its buttons): no bombs, so its bomb press (an attack or a bomb jump)
        // becomes a hop when nothing is in reach, and it holds the bomb button while an enemy is
        public void HyperBallBotInput()
        {
            if (!HostHyperBall || !IsAltForm || IsMorphing)
            {
                _hyperBotHeld = false;
                _hyperBotJump = false;
                return;
            }
            bool bomb = Controls.AltAttack.IsDown;
            bool target = HyperBallAnyTarget();
            int ua = _ammo[UA];
            bool hold = target && (ua == -1 || (_hyperBallActive ? ua >= _hyperFrameCost : ua >= _hyperBotReserve));
            Keybind alt = Controls.AltAttack;
            alt.IsPressed = hold && !_hyperBotHeld;
            alt.IsReleased = !hold && _hyperBotHeld;
            alt.IsDown = hold;
            _hyperBotHeld = hold;
            bool jump = HostBallJump && bomb && !target;
            Keybind j = Controls.Jump;
            j.IsPressed = jump && !_hyperBotJump;
            j.IsReleased = !jump && _hyperBotJump;
            j.IsDown = jump;
            _hyperBotJump = jump;
        }

        private readonly PlayerEntity?[] _hyperBotProbe = new PlayerEntity?[1];

        private bool HyperBallAnyTarget()
        {
            HyperBallFindTargets(_hyperBotProbe);
            return _hyperBotProbe[0] != null;
        }

        // network: active + the two targets' seats (+1, 0 = none), one byte each (Net/SeatAbilities)
        public byte NetHyperBall => (byte)(_hyperBallActive ? 1 : 0);
        public byte NetHyperTargets => (byte)(((_hyperTargets[0]?.SlotIndex + 1) ?? 0) | (((_hyperTargets[1]?.SlotIndex + 1) ?? 0) << 4));

        // a client: a puppet takes the host's state at once (and runs none of its own for a while); the local seat runs its
        // own (its UA and hurt come from the host anyway)
        public void NetApplyHyperBall(byte active, byte targets, bool pose)
        {
            if (!HostHyperBall || !pose)
            {
                return;
            }
            bool on = active != 0;
            if (on && !_hyperBallActive && !HostOwnHyperBallSfx)
            {
                _soundSource.PlaySfx(SfxId.ENERGY_BALL, loop: true);
            }
            else if (!on && _hyperBallActive && !HostOwnHyperBallSfx)
            {
                _soundSource.StopSfx(SfxId.ENERGY_BALL);
            }
            _hyperBallActive = on;
            int a = (targets & 0xF) - 1;
            int b = (targets >> 4) - 1;
            _hyperTargets[0] = on && a >= 0 && a < Players.Count ? Players[a] : null;
            _hyperTargets[1] = on && b >= 0 && b < Players.Count ? Players[b] : null;
            _hyperNetFrames = 30;
        }

        // Draw (alt form): the stand-in, jagged phazon lightning to each target and short spikes around the ball
        private void DrawHyperBall()
        {
            if (!_hyperBallActive || HostOwnHyperBallLook)
            {
                return;
            }
            if (_hyperTrail == null)
            {
                _hyperTrail = Read.GetModelInstance("trail");
                Material m = _hyperTrail.Model.Materials[0];
                _hyperBinding = _scene.BindGetTexture(_hyperTrail.Model, m.TextureId, m.PaletteId, 0);
            }
            Vector3 center = Volume.SpherePosition;
            uint seed = (uint)(SlotIndex * 7919 + _hyperBallFrames / 2); // a new shape every other frame (drawing only)
            for (int i = 0; i < _hyperTargets.Length; i++)
            {
                if (_hyperTargets[i] is PlayerEntity target)
                {
                    DrawHyperBolt(center, target.Volume.SpherePosition, 6, 0.25f, ref seed);
                }
            }
            for (int k = 0; k < 4; k++)
            {
                Vector3 dir = new Vector3(HyperRand(ref seed) * 2 - 1, HyperRand(ref seed) * 1.6f - 0.4f, HyperRand(ref seed) * 2 - 1);
                dir = dir.LengthSquared < 1e-4f ? Vector3.UnitY : dir.Normalized();
                DrawHyperBolt(center, center + dir * (0.6f + HyperRand(ref seed) * 0.5f), 3, 0.12f, ref seed);
            }
        }

        private void DrawHyperBolt(Vector3 from, Vector3 to, int segments, float jag, ref uint seed)
        {
            Vector3 prev = from;
            for (int s = 1; s <= segments; s++)
            {
                Vector3 next = from + (to - from) * (s / (float)segments);
                if (s < segments)
                {
                    next += new Vector3(HyperRand(ref seed) - 0.5f, HyperRand(ref seed) - 0.5f, HyperRand(ref seed) - 0.5f) * jag * 2;
                }
                DrawHyperSegment(prev, next);
                prev = next;
            }
        }

        // one segment, two crossed quads (as the Trooper's stand-in laser, PlayerDroneLaser.DrawDroneLaser)
        private void DrawHyperSegment(Vector3 from, Vector3 to)
        {
            Debug.Assert(_hyperTrail != null);
            Vector3 span = to - from;
            if (span.LengthSquared < 1e-6f)
            {
                return;
            }
            Vector3 dir = span.Normalized();
            Vector3 side = Vector3.Cross(dir, Vector3.UnitY);
            side = side.LengthSquared < 1e-6f ? Vector3.UnitX : side.Normalized();
            Vector3 up = Vector3.Cross(side, dir).Normalized();
            Texture texture = _hyperTrail.Model.Recolors[0].Textures[0];
            float uvS = (texture.Width - (1 / 16f)) / texture.Width;
            float uvT = (texture.Height - (1 / 16f)) / texture.Height;
            Material material = _hyperTrail.Model.Materials[0];
            var color = new Vector3(0.35f, 0.75f, 1f); // phazon blue
            for (int q = 0; q < 2; q++)
            {
                Vector3 off = (q == 0 ? up : side) * 0.035f;
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
                    material.YRepeat, material.ScaleS, material.ScaleT, Matrix4.CreateTranslation(from), uvsAndVerts,
                    _hyperBinding);
            }
        }

        // drawing only: never the game's RNG (a draw must not change the sim)
        private static float HyperRand(ref uint seed)
        {
            seed = seed * 1664525 + 1013904223;
            return (seed >> 8) / 16777216f;
        }
    }
}
