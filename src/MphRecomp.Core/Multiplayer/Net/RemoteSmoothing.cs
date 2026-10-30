using System;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer.Net
{
    // Other players on an online guest, drawn a little in the past (2026-10-19; an idea both other MphRead online forks use,
    // built our own way): each seat's poses from the host's snapshots are kept by host tick, and every step the seat is put
    // on the pose blended between the two snapshots around RenderTick = the host's tick now less a few frames, instead of
    // snapping to the newest snapshot and running on from it (a guess: a seat that turned showed somewhere it never was).
    // The guest's inputs then name that same frame as their view (NetInput.ViewTick), so the host's lag compensation
    // rewinds the targets to exactly what was on screen. The delay follows how evenly snapshots arrive: the snapshot
    // interval plus the worst lateness of the last few seconds plus a frame, 2-20 frames, eased a frame at a time. No blend
    // across a respawn or teleport (the aim epoch), a form change or a seat out of play: those take the newer pose. Past the
    // newest snapshot (one late) the seat holds the newest pose (since 2026-10-19; it ran on from its last pose, a
    // guess that the PC test measured further off: delay 150 jitter 80, 5 % of seat-steps late, ran on max 1.73 vs held 0.63).
    // The top was 8 frames until 2026-10-19 (match 76572J: a phone on mobile data, 788 ms and 21 frames of jitter, its
    // snapshots in bursts at 20 Hz: past the newest snapshot over and over, so the others stood and jumped). Now 20: drawn up to
    // a third of a second back on such a link, still inside the host's 45-frame rewind with the travel time on top. It grows a
    // frame every 10 steps (a burst means late snapshots now) and shrinks a frame every 30.
    public sealed class RemoteSmoothing
    {
        public static bool Enabled { get; set; } = true; // (the PC test compares: -onlinetest smooth=0)

        // A respawn (2026-10-26, musicgoat vs Darkeno777 on Combat Hall: "the player appearing one place for a frame then being
        // somewhere else"): NetApply spawns a seat off the newest snapshot, but the read point is still a few frames back, on the
        // host's poses from before the spawn (dead, where it died), and the seat stood there alive until the read point caught
        // up, then jumped to its spawn. A seat alive here is never drawn before the first pose of its current life: it holds
        // that pose (the host's spawn) until the read point reaches it. The host's recording never has it (no read point there).
        public static bool RespawnHold { get; set; } = true; // (-onlinetest respawnhold=0 compares)
        private long _beforeLife, _beforeLifeFar;

        // Fruity Prime's read point (MIT, Mods/Network/NetSmoothing.cs, copied 2026-10-19; -onlinetest smoothfp=0 = the arrival-clock
        // read point below): the point the others are drawn at moves +1 a step, is pulled 5 % of the way toward the newest
        // snapshot less the delay, snaps when more than 10 frames off, and holds the newest pose when it would pass it. The
        // delay stays 2-8 frames: up a frame when the point ran out of snapshots (at most every 30 steps), down after 240 clean.
        private static bool _fruityRead = false; // off 10-20 with the other Fruity pieces
        public static bool FruityRead { get => _fruityRead || FruitySync.On; set => _fruityRead = value; }
        private const double FpCorrection = 0.05, FpSnap = 10;
        private const int FpMinDelay = 2, FpMaxDelay = 8, FpGrowGap = 30, FpShrinkAfter = 240;
        private double _fpRead;
        private bool _fpInit;
        private int _fpDelay = FruitySync.On ? FpMinDelay : 3, _fpSinceGrow = FpGrowGap, _fpClean;

        // the steady walk (a FruitySync match, 2026-10-20): a seat's place in the host's snapshots steps unevenly when its device's
        // inputs reach the host in bunches (mobile data: the host places it at each report and runs it on between; phone
        // test, a phone on mobile data drawn jittery on the other), so on top of the read point each seat is drawn on a
        // tracking filter of that place (alpha-beta: the place it was heading for, pulled SteadyPull of the way to the
        // snapshots' place, its speed SteadySpeed): a steady walk stays steady, a turn or stop is caught up in a few frames.
        // More than SteadyFar off (or a respawn, teleport, form change) = the snapshots' place itself.
        public static bool SteadyOn { get; set; } = true; // (-onlinetest steady=0 compares)
        private const float SteadyPull = 0.4f, SteadySpeed = 0.1f, SteadyFar = 3f;
        private readonly Vector3[] _steadyAt = new Vector3[16], _steadySpeed = new Vector3[16];
        private readonly bool[] _steadyOn = new bool[16];
        private double _steadySum;
        private float _steadyMax;
        private int _steadyCount, _steadyOver;

        // (Window: the arrivals kept, WindowSteps: the ones that count, ~4 s at any snapshot rate: 60 Hz since 0.3.6)
        private const int Ring = 64, MinDelay = 2, MaxDelay = 20, Window = 256, WindowSteps = 240;

        private struct Pose
        {
            public uint Tick;
            public Vector3 Position, Speed, Facing, Up;
            public float Yaw, Pitch;
            public byte Epoch, Flags;
            public bool Alive; // (spawned on the host with health)
        }

        private readonly Pose[,] _poses = new Pose[16, Ring];
        private readonly int[] _count = new int[16], _head = new int[16];
        private readonly long[] _offsets = new long[Window], _offsetSteps = new long[Window];
        private int _offsetCount, _offsetAt;
        private uint _lastTick;
        private int _interval = 2;
        private int _delay = 3, _delayWant = 3, _delayEase;

        public int DelayFrames => FruityRead ? _fpDelay : _delay;

        // (the PC test's measure of the steps past the newest snapshot: where the seat was drawn, and the newest pose, each
        // against the seat's real pose at that tick once the snapshots around it are in)
        private struct Late
        {
            public int Seat;
            public long Tick;
            public Vector3 RanOn, Held;
        }
        private readonly System.Collections.Generic.List<Late> _late = new();
        private long _seatSteps, _lateSteps, _judged;
        private double _ranOnSum, _heldSum;
        private float _ranOnMax, _heldMax;

        // (the PC test: how the drawn tick moved step to step; anything but +1 shows the others stepping back, standing or skipping)
        private long _lastRender = long.MinValue, _renderSteps, _renderBack, _renderBackFrames, _renderStill, _renderSkip;
        private double _lastRenderExact = double.NaN, _renderDevSum, _renderDevMax;

        public string Summary() => $"smoothing ({(FruityRead ? "fruity read point" : "arrival clock")}): delay {DelayFrames} f, "
            + (_steadyCount == 0 ? "" : $"steady walk off the snapshots' place avg {_steadySum / _steadyCount:0.000} max {_steadyMax:0.00} u, over 0.5 u {_steadyOver * 100.0 / _steadyCount:0.0}%; ")
            + (_renderSteps == 0 ? "" : $"drawn tick off +1 avg {_renderDevSum / _renderSteps:0.000} max {_renderDevMax:0.00}; ") + $"drawn tick of {_renderSteps} steps: back {_renderBack} ({_renderBackFrames} f), "
            + $"still {_renderStill}, skipped ahead {_renderSkip}; before a seat's life {_beforeLife} seat-steps ({_beforeLifeFar} over 2 u off its spawn"
            + (RespawnHold ? ", held there" : ", drawn there") + $"); past the newest snapshot {_lateSteps} of {_seatSteps} seat-steps"
            + (_judged == 0 ? "" : $" ({_judged} judged: drawn off avg {_ranOnSum / _judged:0.00} max {_ranOnMax:0.00}, "
            + $"newest pose off avg {_heldSum / _judged:0.00} max {_heldMax:0.00})");

        // the host's tick now as this device's steady clock puts it (the earliest arrival seen lately = no lateness), and the
        // tick the other seats are drawn at
        private long _clockOffset = long.MinValue;
        public long RenderTick(long step) => FruityRead ? (_fpInit ? (long)Math.Floor(_fpRead) : 0)
            : _clockOffset == long.MinValue ? 0 : step - _clockOffset - _delay;

        // a snapshot taken at this device's step `step`: every other seat's pose, and how late it came
        public void Add(Snapshot snap, int local, long step)
        {
            if (snap.Tick <= _lastTick && _lastTick != 0) return;
            if (_lastTick != 0) _interval = (int)Math.Clamp(snap.Tick - _lastTick, 1, 6);
            _lastTick = snap.Tick;
            for (int i = 0; i < snap.Seats.Length && i < 16; i++)
            {
                if (i == local) continue;
                ref PlayerNetState s = ref snap.Seats[i];
                int at = (_head[i] + _count[i]) % Ring;
                if (_count[i] == Ring) _head[i] = (_head[i] + 1) % Ring;
                else _count[i]++;
                _poses[i, at] = new Pose
                {
                    Tick = snap.Tick, Position = s.Position, Speed = s.Speed, Facing = s.Facing, Up = s.Up,
                    Yaw = s.Yaw, Pitch = s.Pitch, Epoch = s.AimEpoch, Flags = s.Flags,
                    Alive = s.Health > 0 && (s.Flags & PlayerNetState.Spawned) != 0
                };
            }
            _offsets[_offsetAt] = step - snap.Tick;
            _offsetSteps[_offsetAt] = step;
            _offsetAt = (_offsetAt + 1) % Window;
            _offsetCount = Math.Min(_offsetCount + 1, Window);
            long earliest = long.MaxValue, latest = long.MinValue;
            for (int k = 0; k < _offsetCount; k++)
            {
                if (step - _offsetSteps[k] >= WindowSteps) continue;
                earliest = Math.Min(earliest, _offsets[k]);
                latest = Math.Max(latest, _offsets[k]);
            }
            _clockOffset = earliest;
            _delayWant = (int)Math.Clamp(_interval + (latest - earliest) + 1, MinDelay, MaxDelay);
        }

        // before each step: every other seat on its blended pose at RenderTick
        public void Apply(long step, int local)
        {
            if (_clockOffset == long.MinValue) return;
            double render;
            if (FruityRead) render = FruitySteps();
            else render = Legacy(step);
            long renderFloor = (long)Math.Floor(render);
            if (_lastRender != long.MinValue)
            {
                _renderSteps++;
                long d = renderFloor - _lastRender;
                if (d < 0) { _renderBack++; _renderBackFrames -= d; }
                else if (d == 0) _renderStill++;
                else if (d > 1) _renderSkip++;
                double dev = Math.Abs(render - _lastRenderExact - 1);
                _renderDevSum += dev;
                _renderDevMax = Math.Max(_renderDevMax, dev);
            }
            _lastRender = renderFloor;
            _lastRenderExact = render;
            Judge();
            ApplyPoses(render, local);
        }

        // (Fruity's read point; one call per step)
        private double FruitySteps()
        {
            double newest = _lastTick, target = newest - _fpDelay;
            if (!_fpInit) { _fpInit = true; _fpRead = target; }
            else
            {
                _fpRead += 1;
                double err = target - _fpRead;
                if (Math.Abs(err) > FpSnap) _fpRead = target;
                else _fpRead += err * FpCorrection;
            }
            _fpSinceGrow++;
            if (_fpRead >= newest)
            {
                _fpRead = newest; // (out of snapshots: hold the newest pose, no running on)
                _fpClean = 0;
                if (_fpSinceGrow >= FpGrowGap && _fpDelay < FpMaxDelay) { _fpDelay++; _fpSinceGrow = 0; }
            }
            else if (++_fpClean >= FpShrinkAfter && _fpDelay > FpMinDelay) { _fpDelay--; _fpClean = 0; }
            return _fpRead;
        }

        private long Legacy(long step)
        {
            // (the delay eases toward what the arrivals ask for: never a jump; up a frame every 10 steps, down every 30)
            if (_delay != _delayWant && ++_delayEase >= (_delayWant > _delay ? 10 : 30))
            {
                _delayEase = 0;
                _delay += Math.Sign(_delayWant - _delay);
            }
            return RenderTick(step);
        }

        private void ApplyPoses(double render, int local)
        {
            for (int i = 0; i < 16 && i < PlayerEntity.MaxPlayers; i++)
            {
                if (i == local || _count[i] == 0) continue;
                PlayerEntity p = PlayerEntity.Players[i];
                if (!p.LoadFlags.TestFlag(LoadFlags.Active)) continue;
                ref Pose newest = ref _poses[i, (_head[i] + _count[i] - 1) % Ring];
                _seatSteps++;
                if (render >= newest.Tick)
                {
                    _lateSteps++;
                    if (_late.Count < 4096) _late.Add(new Late { Seat = i, Tick = (long)Math.Floor(render), RanOn = p.Position, Held = newest.Position });
                    p.NetSetPose(Steady(i, p, newest.Position, true), newest.Speed, newest.Facing, newest.Up, newest.Yaw,
                        newest.Pitch); // (no snapshot that new yet: it holds the newest pose)
                    continue;
                }
                if (p.Health > 0 && newest.Alive)
                {
                    // (the first pose of the seat's current life: the newest run of alive poses; all alive = no respawn kept)
                    int life = _count[i] - 1;
                    while (life > 0 && _poses[i, (_head[i] + life - 1) % Ring].Alive) life--;
                    ref Pose first = ref _poses[i, (_head[i] + life) % Ring];
                    if (life > 0 && render < first.Tick)
                    {
                        _beforeLife++;
                        int g = life - 1; // (the pose the read point is on: before the spawn, where the seat died)
                        while (g > 0 && _poses[i, (_head[i] + g) % Ring].Tick > render) g--;
                        if ((_poses[i, (_head[i] + g) % Ring].Position - first.Position).LengthSquared > 4) _beforeLifeFar++;
                        if (RespawnHold)
                        {
                            Set(p, first);
                            _steadyOn[i] = false;
                            continue;
                        }
                    }
                }
                int k = _count[i] - 1;
                while (k > 0 && _poses[i, (_head[i] + k - 1) % Ring].Tick > render) k--;
                ref Pose b = ref _poses[i, (_head[i] + k) % Ring];
                if (k == 0)
                {
                    Set(p, b); // (older than everything kept: the oldest)
                    _steadyOn[i] = false;
                    continue;
                }
                ref Pose a = ref _poses[i, (_head[i] + k - 1) % Ring];
                const byte kept = PlayerNetState.Spawned | PlayerNetState.AltForm | PlayerNetState.Active;
                if (a.Epoch != b.Epoch || (a.Flags & kept) != (b.Flags & kept) || b.Tick <= a.Tick)
                {
                    Set(p, render - a.Tick < b.Tick - render ? a : b); // (no blend across a respawn, teleport or form change)
                    _steadyOn[i] = false;
                    continue;
                }
                float t = Math.Clamp((float)((render - a.Tick) / (b.Tick - a.Tick)), 0, 1);
                Vector3 facing = Vector3.Lerp(a.Facing, b.Facing, t);
                Vector3 up = Vector3.Lerp(a.Up, b.Up, t);
                float yaw = a.Yaw + Wrap(b.Yaw - a.Yaw) * t;
                p.NetSetPose(Steady(i, p, Vector3.Lerp(a.Position, b.Position, t), false), Vector3.Lerp(a.Speed, b.Speed, t),
                    facing.LengthSquared > 1e-6f ? facing.Normalized() : b.Facing, up.LengthSquared > 1e-6f ? up.Normalized() : b.Up,
                    yaw, a.Pitch + (b.Pitch - a.Pitch) * t);
            }
        }

        // (the steady walk above; held = past the newest snapshot: the filter's speed isn't taught a stop that isn't one)
        private Vector3 Steady(int i, PlayerEntity p, Vector3 raw, bool held)
        {
            if (!SteadyOn || !FruitySync.On || p.Health == 0)
            {
                _steadyOn[i] = false;
                return raw;
            }
            Vector3 guess = _steadyAt[i] + _steadySpeed[i];
            Vector3 off = raw - guess;
            if (!_steadyOn[i] || !(off.Length <= SteadyFar))
            {
                _steadyOn[i] = true;
                _steadyAt[i] = raw;
                _steadySpeed[i] = Vector3.Zero;
                return raw;
            }
            _steadyAt[i] = guess + off * SteadyPull;
            if (!held) _steadySpeed[i] += off * SteadySpeed;
            float moved = (_steadyAt[i] - raw).Length; // (how far the steady walk draws it from the snapshots' place)
            _steadySum += moved;
            _steadyMax = MathF.Max(_steadyMax, moved);
            _steadyCount++;
            if (moved > 0.5f) _steadyOver++;
            return _steadyAt[i];
        }

        private void Judge()
        {
            for (int n = _late.Count - 1; n >= 0; n--)
            {
                Late l = _late[n];
                int i = l.Seat, k = _count[i] - 1;
                if (k < 1 || _poses[i, (_head[i] + k) % Ring].Tick <= l.Tick) continue; // (not bracketed yet)
                _late.RemoveAt(n);
                while (k > 0 && _poses[i, (_head[i] + k - 1) % Ring].Tick > l.Tick) k--;
                if (k == 0) continue;
                ref Pose a = ref _poses[i, (_head[i] + k - 1) % Ring];
                ref Pose b = ref _poses[i, (_head[i] + k) % Ring];
                if (a.Epoch != b.Epoch || b.Tick <= a.Tick) continue;
                Vector3 real = Vector3.Lerp(a.Position, b.Position, (float)(l.Tick - a.Tick) / (b.Tick - a.Tick));
                float ranOn = (l.RanOn - real).Length, held = (l.Held - real).Length;
                _judged++;
                _ranOnSum += ranOn;
                _heldSum += held;
                _ranOnMax = Math.Max(_ranOnMax, ranOn);
                _heldMax = Math.Max(_heldMax, held);
            }
        }

        private static void Set(PlayerEntity p, in Pose pose) => p.NetSetPose(pose.Position, pose.Speed, pose.Facing, pose.Up, pose.Yaw, pose.Pitch);

        private static float Wrap(float degrees) => degrees - 360 * MathF.Floor((degrees + 180) / 360);
    }
}
