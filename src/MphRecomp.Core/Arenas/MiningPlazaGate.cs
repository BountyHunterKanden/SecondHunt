using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRead.Formats.Collision;
using MphRead.Formats.Culling;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Mining Plaza's Luminoth gate (thread 3d373d, owner 2026-10-31: "open exactly how it does in echoes but only on two
    // conditions. the time limit is half way up for the match, or 2/3 of the needed score ... whichever condition is met
    // first"). Echoes' own gate platform (PLAT 000202ea, its DCLN) and its two spinning holograms (ACTR 000202ed/e6, AROT
    // 000202dd), opened on Echoes' 'Gate Control' sequence (MiningPlaza.Gate reads it). The open rule runs in the host's
    // sim each step (deterministic: replays open it on the same frame); LAN/online guests mirror the host (Net/NetWorld.cs).
    // In the dev solo run it has no collision, so the owner can walk through to place pickups behind it. Bots route around
    // it shut and through it open: its bit in the node data sets (after the pillars': ArenaNav.BuildSets).
    public static class MiningPlazaGate
    {
        public sealed class GateSpot
        {
            public uint Id { get; set; }
            // the gate's Echoes transform (x y z, rotation x y z in degrees, scale x y z) and its open place (Gate (Opened))
            public float[] Xf { get; set; } = Array.Empty<float>();
            public float[] End { get; set; } = Array.Empty<float>();
            public float Scale { get; set; }
            public int Pass { get; set; } = -1;
            // the holograms: each one's pass and Echoes transform (spun about its own Z by the AROT)
            public List<int> HoloPasses { get; set; } = new();
            public List<float[]> HoloXf { get; set; } = new();
            // the glow cards under the holograms (still; off with them)
            public List<int> GlowPasses { get; set; } = new();
            // MPH spots: the gate's middle (its sounds), the two dust effects
            public float[] Center { get; set; } = Array.Empty<float>();
            public float[] DustFront { get; set; } = Array.Empty<float>();
            public float[] DustBack { get; set; } = Array.Empty<float>();
            public float[]? Ambient { get; set; }
            public List<float[]>? Lights { get; set; }
            public float[]? HoloAmbient { get; set; }
            public List<float[]>? HoloLights { get; set; }
            // collision, MPH world triangles facing out (9 floats each), shut
            public float[] Tris { get; set; } = Array.Empty<float>();
            // the sequence (seconds from the open; MiningPlaza.Gate, from SQTR 'Gate Control' 000202e8): the holograms off,
            // the gate's move start/end, its sounds' starts
            public float HolosOff { get; set; }
            public float MoveStart { get; set; }
            public float MoveEnd { get; set; }
            public List<GateCue> Cues { get; set; } = new();
            // the holograms' spin: seconds per turn (AROT 000202dd: 4 s loop) and the Z spline's keys (t 0-1, degrees)
            public float SpinPeriod { get; set; } = 4;
            public List<float[]> Spin { get; set; } = new();
            // the move's ease (t 0-1 -> 0-1 of the way), Echoes' platform speed curve (empty: linear)
            public List<float[]> Ease { get; set; } = new();
        }

        public sealed class GateCue
        {
            public string Role { get; set; } = "";
            public float At { get; set; }
        }

        // the open rule's score half: the leading team (or player) has 2/3 of the mode's goal. Counted goals (points,
        // Survival deaths) round to the nearest whole one, at least 1 (owner 2026-11-02: goal 2 opens at 1, not never;
        // Survival with 1 spare life opens on the first death); timed goals (Defender, Prime Hunter) are a straight 2/3
        public static bool ScoreReached()
        {
            int n = PlayerEntity.SlotCapacity;
            switch (GameState.Mode)
            {
                case GameMode.Battle or GameMode.BattleTeams or GameMode.Bounty or GameMode.BountyTeams or GameMode.Capture
                    or GameMode.Nodes or GameMode.NodesTeams:
                    if (GameState.PointGoal <= 0) return false;
                    for (int i = 0; i < n; i++)
                    {
                        if (GameState.TeamPoints[i] >= Share(GameState.PointGoal)) return true;
                    }
                    return false;
                case GameMode.Defender or GameMode.DefenderTeams:
                    if (GameState.TimeGoal <= 0) return false;
                    for (int i = 0; i < n; i++)
                    {
                        if (GameState.TeamTime[i] / GameState.TimeGoal >= ScoreShare) return true;
                    }
                    return false;
                case GameMode.PrimeHunter:
                    if (GameState.TimeGoal <= 0) return false;
                    for (int i = 0; i < n; i++)
                    {
                        if (GameState.Time[i] / GameState.TimeGoal >= ScoreShare) return true;
                    }
                    return false;
                case GameMode.Survival or GameMode.SurvivalTeams:
                    // last standing wins: a team (or player) down 2/3 of its lives (its spare lives + its last one)
                    for (int i = 0; i < n; i++)
                    {
                        if (GameState.TeamDeaths[i] >= Share(GameState.TeamLives(i) + 1)) return true;
                    }
                    return false;
            }
            return false;
        }

        // 2/3 of a counted goal, to the nearest whole one, at least 1
        public static int Share(int goal) => Math.Max(1, (2 * goal + 1) / 3);

        public const float ScoreShare = 2f / 3, TimeShare = 0.5f;

        // ---- the room registry and the entity ----

        static readonly Dictionary<int, GateSpot> _rooms = new();

        internal static void SetRoom(int roomId, EchoesArena.Info? info)
        {
            lock (_rooms)
            {
                if (info?.Gate is { } g) _rooms[roomId] = g;
                else _rooms.Remove(roomId);
            }
        }

        // the dev solo run: no collision (PlacedPickups.Setup)
        public static bool Solo { get; set; }

        internal static CrateLight? Light(float[]? amb, List<float[]>? lights)
        {
            if (amb is not { Length: 3 } a || lights == null) return null;
            float[][] ls = lights.Where(l => l.Length == 6).Take(4).ToArray();
            return new CrateLight(new Vector3(a[0], a[1], a[2]), ls.Select(l => new Vector3(l[0], l[1], l[2])).ToArray(), ls.Select(l => new Vector3(l[3], l[4], l[5])).ToArray());
        }

        // SceneSetup.HostExtraEntities: the gate after every other entity (any multiplayer match in the room)
        public static IReadOnlyList<EntityBase> Extra(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            GateSpot? spot;
            PlayerEntity.HostBotNoJumpAt = null;
            lock (_rooms)
            {
                if (!GameState.Multiplayer || !_rooms.TryGetValue(roomId, out spot)) return entities;
            }
            var list = new List<EntityBase>(entities);
            int id = entities.Count == 0 ? 0 : entities.Max(e => e.Id) + 1;
            // its nav bit follows the pillars' (EchoesArena: the state rays)
            int index = entities.Count(e => e is MiningPlazaPillarEntity);
            var gate = new MiningPlazaGateEntity(id, index, spot, Light(spot.Ambient, spot.Lights), Light(spot.HoloAmbient, spot.HoloLights), scene);
            list.Add(gate);
            if (!Solo && spot.Tris.Length > 0)
            {
                (Vector3 lo, Vector3 hi) = Box(spot);
                PlayerEntity.HostBotNoJumpAt = p => !gate.Opened && JumpsAtBox(lo, hi, p.Position, p.Speed, p.AiData.HostNodePos);
            }
            return list;
        }

        static float Curve(List<float[]> keys, float t)
        {
            if (keys.Count == 0) return t;
            if (t <= keys[0][0]) return keys[0][1];
            for (int i = 1; i < keys.Count; i++)
            {
                if (t <= keys[i][0])
                {
                    float[] a = keys[i - 1], b = keys[i];
                    return b[0] > a[0] ? a[1] + (b[1] - a[1]) * (t - a[0]) / (b[0] - a[0]) : b[1];
                }
            }
            return keys[^1][1];
        }

        // the gate's Echoes transform t seconds after the open
        static float[] MoveXf(GateSpot s, float t)
        {
            var x = (float[])s.Xf.Clone();
            if (s.End.Length < 3 || t <= s.MoveStart) return x;
            float k = s.MoveEnd > s.MoveStart ? Math.Clamp((t - s.MoveStart) / (s.MoveEnd - s.MoveStart), 0, 1) : 1;
            k = Curve(s.Ease, k);
            for (int i = 0; i < 3; i++) x[i] = s.Xf[i] + (s.End[i] - s.Xf[i]) * k;
            return x;
        }

        internal static Matrix4 Frame(float[] xf, float scale)
        {
            Vector3 M(Vector3 e) => new Vector3(e.X, e.Z, -e.Y) * scale;
            Vector3 o = M(ArenaHdWriter.Place(xf, Vector3.Zero));
            Vector4 R(Vector3 e) => new Vector4(M(ArenaHdWriter.Place(xf, e)) - o, 0);
            return new Matrix4(R(Vector3.UnitX), R(Vector3.UnitY), R(Vector3.UnitZ), new Vector4(o, 1));
        }

        // the gate's motion t seconds in: its shut world place to where it is (Identity before the open)
        public static Matrix4 Model(GateSpot s, float t)
        {
            if (t <= 0 || s.Xf.Length < 9) return Matrix4.Identity;
            return Frame(s.Xf, s.Scale).Inverted() * Frame(MoveXf(s, t), s.Scale);
        }

        // a hologram's spin at a clock time (seconds): its placed (unspun) world place to the turned one
        public static Matrix4 HoloModel(GateSpot s, int k, double clock)
        {
            if (k >= s.HoloXf.Count || s.HoloXf[k].Length < 9) return Matrix4.Identity;
            float[] x = (float[])s.HoloXf[k].Clone();
            float p = s.SpinPeriod > 0 ? (float)(clock / s.SpinPeriod % 1.0) : 0;
            x[5] += s.Spin.Count > 0 ? Curve(s.Spin, p) : 360 * p;
            return Frame(s.HoloXf[k], s.Scale).Inverted() * Frame(x, s.Scale);
        }

        // the gate's collision in one state (open: none left in the way), for the bots' nav graph
        public static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> StateTris(GateSpot s, bool open)
        {
            Matrix4 m = open ? Model(s, Done(s)) : Matrix4.Identity;
            float[] t = s.Tris;
            for (int k = 0; k + 8 < t.Length; k += 9)
            {
                yield return (Vector3.TransformPosition(new Vector3(t[k], t[k + 1], t[k + 2]), m),
                    Vector3.TransformPosition(new Vector3(t[k + 3], t[k + 4], t[k + 5]), m),
                    Vector3.TransformPosition(new Vector3(t[k + 6], t[k + 7], t[k + 8]), m));
            }
        }

        // bots never jump this close to the shut gate (PlayerEntity.HostBotNoJumpAt): across from its box, and from a
        // unit under it to its top (Tools -botprobe counts a jump at it within 0.8)
        public const float NoJumpNear = 2f;
        public const float NoJumpReach = 8f, NoJumpRun = 30f; // (JumpsAtBox)

        // the shut gate's box (its collision triangles' bounds)
        public static (Vector3 Lo, Vector3 Hi) Box(GateSpot s)
        {
            float[] tr = s.Tris;
            Vector3 lo = new(Single.MaxValue), hi = new(Single.MinValue);
            for (int k = 0; k + 2 < tr.Length; k += 3)
            {
                var v = new Vector3(tr[k], tr[k + 1], tr[k + 2]);
                lo = Vector3.ComponentMin(lo, v);
                hi = Vector3.ComponentMax(hi, v);
            }
            return (lo, hi);
        }

        // a jump from q that ends up at the box: q next to it, or the line to the bot's node or along its run (speed over
        // NoJumpRun frames) passing next to it, either capped at NoJumpReach across. Owner 11-05 (R2 1:34): Dark Samus
        // jumped from 4.6 off, running at a node on the gate's face, and hung against the gate
        public static bool JumpsAtBox(Vector3 lo, Vector3 hi, Vector3 q, Vector3 speed, Vector3? node)
        {
            if (NearBox(lo, hi, q, NoJumpNear))
            {
                return true;
            }
            var run = new Vector3(speed.X, 0, speed.Z) * NoJumpRun;
            return (node is Vector3 n && LineNearBox(lo, hi, q, new Vector3(n.X, q.Y, n.Z) - q))
                || (run.LengthSquared > 0.0001f && LineNearBox(lo, hi, q, run));
        }

        static bool LineNearBox(Vector3 lo, Vector3 hi, Vector3 q, Vector3 d)
        {
            float len = d.Length;
            if (len < 0.0001f)
            {
                return false;
            }
            d *= Math.Min(len, NoJumpReach) / len;
            for (int i = 1; i <= 16; i++)
            {
                if (NearBox(lo, hi, q + d * (i / 16f), NoJumpNear))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool NearBox(Vector3 lo, Vector3 hi, Vector3 q, float near)
        {
            float dx = Math.Max(0, Math.Max(lo.X - q.X, q.X - hi.X)), dz = Math.Max(0, Math.Max(lo.Z - q.Z, q.Z - hi.Z));
            return dx * dx + dz * dz < near * near && q.Y > lo.Y - 1 && q.Y < hi.Y;
        }

        public static float Done(GateSpot s) => Math.Max(s.MoveEnd, s.Cues.Count == 0 ? 0 : s.Cues.Max(c => c.At)) + 0.5f;
    }

    public sealed class MiningPlazaGateEntity : EnemyInstanceEntity
    {
        public MiningPlazaGate.GateSpot Spot { get; }
        readonly CrateLight? _light, _holoLight;
        // its bit in the node data's set selector (after the pillars')
        readonly int _index;
        // steps (60 Hz) since the open; -1 shut
        int _open = -1;
        readonly int _doneSteps;
        // the match's starting time limit (the most MatchTime has shown in the match; -1 none)
        float _startTime = -1;

        public bool Opened => _open >= 0;
        // seconds since the open (-1 shut): the app's sounds follow it
        public float OpenTime => _open < 0 ? -1 : _open / 60f;

        public MiningPlazaGateEntity(int id, int index, MiningPlazaGate.GateSpot spot, CrateLight? light, CrateLight? holoLight, Scene scene)
            : base(new EnemyInstanceEntityData(EnemyType.HitZone, null!), NodeRef.None, scene)
        {
            Id = id;
            _index = index;
            Spot = spot;
            _light = light;
            _holoLight = holoLight ?? light;
            _doneSteps = (int)MathF.Ceiling(MiningPlazaGate.Done(spot) * 60);
            float[] x = spot.Xf;
            Position = x.Length >= 3 ? new Vector3(x[0], x[2], -x[1]) * spot.Scale : Vector3.Zero;
        }

        protected override void EnemyInitialize()
        {
            NodeRef = _scene.GetNodeRefByName(ArenaModelWriter.RoomNode);
            Flags = EnemyFlags.NoMaxDistance | EnemyFlags.Static | EnemyFlags.Invincible;
            _hurtVolumeInit = _hurtVolume = new CollisionVolume(Position.AddY(-10000), 0);
            Transform = Matrix4.Identity;
            // the dev solo run walks through it (owner: to place a pickup behind it)
            if (MiningPlazaGate.Solo || Spot.Tris.Length == 0) return;
            SetCollision(MiningPlazaPillars.Shape(Spot.Tris, $"plaza_gate{Spot.Id:x8}"), 0);
            UpdateCollisionTransform(0, Matrix4.Identity);
            UpdateLinkedInverse(0);
        }

        public override bool GetTargetable() => false;

        protected override bool EnemyGetDrawInfo() => true;

        // steps (60 Hz) since the match began: the holograms' spin
        int _steps;

        public override bool Process()
        {
            _steps++;
            if (_open < 0 && !EchoesBreakables.Mirror && GameState.MatchState == MatchState.InProgress && ShouldOpen()) Open();
            if (_open >= 0)
            {
                if (_open < _doneSteps) _open++;
                Step();
            }
            return base.Process();
        }

        bool ShouldOpen()
        {
            float now = GameState.MatchTime;
            if (now > _startTime) _startTime = now;
            if (_startTime > 0 && now >= 0 && now <= _startTime * MiningPlazaGate.TimeShare) return true;
            return MiningPlazaGate.ScoreReached();
        }

        void Step()
        {
            if (EntityCollision[0] != null)
            {
                UpdateLinkedInverse(0);
                UpdateCollisionTransform(0, MiningPlazaGate.Model(Spot, OpenTime));
            }
            if (_open >= _doneSteps)
            {
                if (EntityCollision[0]?.Collision is { } c) c.Active = false;
                if (_scene.Room?.NodeData is { } nd && _index < nd.SetIndices.Count) nd.SetSelector[_index] = true;
            }
        }

        public void Open()
        {
            if (_open >= 0) return;
            _open = 0;
            Step();
        }

        // a network client's gate: the host's state (Net/NetWorld.cs). Open in the first snapshot: already open there
        bool _netSeen;
        public void NetApply(bool up, bool hit, int breaks)
        {
            if (!up && !Opened)
            {
                if (!_netSeen) { _open = _doneSteps; Step(); }
                else Open();
            }
            _netSeen = true;
        }

        // what to draw now: the gate's motion, the holograms spinning until the sequence turns them off
        // (added to the caller's list, fade 1: no iterator garbage, frame dips 2026-11-01)
        public void AddPieces(List<(Matrix4 Model, int Pass, CrateLight? Light, float Fade)> into)
        {
            double clock = _steps / 60.0;
            float t = OpenTime;
            if (Spot.Pass >= 0) into.Add((MiningPlazaGate.Model(Spot, t), Spot.Pass, _light, 1));
            if (t >= 0 && t >= Spot.HolosOff) return;
            for (int k = 0; k < Spot.HoloPasses.Count; k++)
            {
                if (Spot.HoloPasses[k] >= 0) into.Add((MiningPlazaGate.HoloModel(Spot, k, clock), Spot.HoloPasses[k], _holoLight, 1));
            }
            foreach (int p in Spot.GlowPasses)
            {
                if (p >= 0) into.Add((Matrix4.Identity, p, _holoLight, 1));
            }
        }
    }
}
