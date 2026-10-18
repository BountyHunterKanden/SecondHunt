using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRead.Formats.Culling;
using MphRead.Utility;
using MphRecomp.Import;
using MphRecomp.Import.Retro;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;
using M = MphRead.DetMathF;

namespace MphRecomp.Arenas
{
    using Anim = MphRecomp.Import.Retro.Anim;

    // Shooting Gallery's manned turret and bomb slot (docs/SHOOTING_GALLERY_TURRET.md): Echoes' PlayerTurret, 1:1 with
    // the retail GameCube disc (owner 2026-10-15/16). The import reads every value the disc has by property id (the
    // turret's limits, speeds, fire rate, damage, the platform's health and collision mesh, the trigger and slot boxes)
    // and the lift's motion from the turret's own animations (its pivot over turret_ball_up / turret_ball_down); the
    // sequence timings (SequenceTimer data, decoded with seq.py) and the turret module's own constants are code
    // constants here, as EchoesBreakables' timers are.
    //   - any living biped who walks into the enter box gets in: every other player within 5 m (horizontally) of the
    //     turret dies, credited to them (and within 4 m x 5 m takes 1000), the lift rises (1.77 s), control at 2.0 s
    //   - seated (PlayerEchoesSeat.cs): aims the barrel (yaw +-90 at 120 deg/s, pitch +-45 at 60 deg/s), Shoot fires
    //     (fresh presses, 1 s apart), Jump gets out (pushed back and up at 30 m/s); no moving, morphing or own weapons
    //   - the shot: 75 (Echoes' 75 of a 99 life = MPH's 75 of 99, owner D1), 10 m splash falling off to 0, 60 m/s, 51 m
    //   - the turret: 100 Echoes health (600 here: EchoesBreakables' points), beams only; at 0 it blows up and the
    //     occupant dies, credited to whoever hit it last; back 7 s later
    //   - the bomb slot, armed while someone is in the turret: the first alt-form player in its box is held at its
    //     centre (no unmorphing). Any bomb whose blast reaches its trigger while it holds a ball destroys the turret and
    //     kills the occupant, credited to the bomber: Samus's and Dark Samus's own (owner D3), and the Samus bomb every
    //     other hunter lays there in place of their alt-form attack (owner 2026-10-16; PlayerEntity.SlotBombInput). Unarmed, its kick-out pushes balls in it out (Echoes' TRGO 'Kick Out Ball': 500 along Echoes +Y)
    // Host-authoritative like the breakables. LAN and online (T5): clients run it as a mirror (EchoesTurret.Mirror) of the
    // host's NetTurret in each snapshot (Net/NetWorld.cs): its state, timer, aim, occupant, held ball, health, shots and
    // blow-ups; no entering, slot, hits or shots of its own, and its seat and slot hold the players here as the host's
    // do. Off in replays from before it (MatchSettings.EchoesTurret).
    public static class EchoesTurret
    {
        static readonly Dictionary<int, TurretSpot> _rooms = new();

        // this match has it (set by MatchStart before the arena loads)
        public static bool Enabled { get; set; }

        // on unless the settings turn it off (LAN and online too: null on every device)
        public static bool For(MatchSettings settings) => settings.EchoesTurret ?? true;

        // a match client: the turret follows the host's (NetTurret) instead of running its own (set by MatchStart;
        // cleared when this device takes over as host)
        public static bool Mirror { get; set; }

        // Echoes' sequences (seconds; the arena's SequenceTimers, the same on both discs)
        public const float ControlAt = 2.0f;          // Enter: STRT to the turret
        public const float HealthResetAt = 0.2f;      // Enter: the platform's health back to full
        public const float LeaveSlotOffAt = 0.039f;   // Leave: the slot disarmed
        public const float LeaveOffAt = 1.5f;         // Leave: the turret deactivated (its collision gone)
        public const float LeaveBackAt = 4.0f;        // Leave: the enter trigger and hologram back
        public const float DestroyedKillAt = 0.01f;   // Destroyed: 'Player Kill' on (TRGR 0x3a: kill on enter, 100 damage)
        public const float DestroyedKillUntil = 0.11f; // ... and off
        public const float DestroyedSlotOffAt = 0.028f;
        // the slot's CameraHints (CAMH 0x31 / 0x2ce): their InterpolateOnTime / InterpolateOffTime
        public const float SlotCamIn = 0.75f, SlotCamOut = 2.0f;
        public const float DestroyedBackAt = 7.0f;
        // the turret module (ScriptPlayerTurret.rel / RSO_ScriptPlayerTurret.rso): the entry kill and damage reach (m)
        public const float EntryKillRadius = 5.0f;
        public const float EntryHitRadius = 4.0f, EntryHitHeight = 5.0f;
        public const float EntryHitDamage = 1000;
        // the player's exit velocity (m/s): back along the barrel's way, and up (CPlayer, Trilogy DOL constant 30.0)
        public const float ExitSpeed = 30;
        // the turret's camera: 3 m ahead of the seat locator along the barrel (CPlayer 0x8014d330); the exit's eye goes there
        public const float SeatCamAhead = 3;
        // Echoes' player mass (the kick-out's impulse is momentum; CPlayer's 200 kg)
        public const float PlayerMass = 200;

        internal static void SetRoom(int roomId, EchoesArena.Info? info)
        {
            lock (_rooms)
            {
                if (info?.Turret == null) _rooms.Remove(roomId);
                else _rooms[roomId] = info.Turret;
            }
        }

        public static TurretSpot? For(int roomId)
        {
            lock (_rooms) return _rooms.TryGetValue(roomId, out TurretSpot? t) ? t : null;
        }

        // SceneSetup.HostExtraEntities: the arena's turret, ids past the entities before it
        public static IReadOnlyList<EntityBase> Extra(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            EchoesTurretBots.Live = null;
            PlayerEntity.HostBotErrand = null;
            PlayerEntity.HostSeatBotBrain = false;
            if (!Enabled || !GameState.Multiplayer) return entities;
            TurretSpot? spot = For(roomId);
            if (spot == null) return entities;
            var list = new List<EntityBase>(entities);
            int id = entities.Count == 0 ? 0 : entities.Max(e => e.Id) + 1;
            var turret = new EchoesTurretEntity(id, spot, scene);
            list.Add(turret);
            if (EchoesTurretBots.On)
            {
                EchoesTurretBots.Start(turret);
                PlayerEntity.HostBotErrand = EchoesTurretBots.Errand;
                PlayerEntity.HostSeatBotBrain = true;
            }
            if (spot.BaseMin != null && spot.BaseMax != null) list.Add(new EchoesTurretBaseEntity(id + 1, spot, turret, scene));
            scene.LoadEffect(EchoesTurretEntity.ExplodeEffect, persistent: false);
            return list;
        }

        // ---- import ----

        // the turret's objects in an arena's scripts (Shooting Gallery's); null when it has none
        internal static TurretSpot? Import(Pak pak, List<Mrea.ScriptObject> scripts, Func<float, float, float, Vector3> map, float scale, List<string> notes)
        {
            Mrea.ScriptObject? plrt = scripts.FirstOrDefault(o => o.Type == "PLRT");
            if (plrt == null) return null;
            Mrea.ScriptObject? Named(string type, string name) => scripts.FirstOrDefault(o => o.Type == type && o.Name == name);
            Mrea.ScriptObject? enter = Named("TRGR", "Enter man turret"), plat = Named("PLAT", "Turret Collision"), actr = Named("ACTR", "Animating Turret");
            // the slot: its bomb trigger, and the ball trigger there (the arena has others, by its launchers)
            Mrea.ScriptObject? dtrg = Named("DTRG", "Bombslot Right") ?? scripts.FirstOrDefault(o => o.Type == "DTRG");
            static float D2(Mrea.ScriptObject a, Mrea.ScriptObject b) =>
                (a.Transform[0] - b.Transform[0]) * (a.Transform[0] - b.Transform[0]) + (a.Transform[1] - b.Transform[1]) * (a.Transform[1] - b.Transform[1])
                + (a.Transform[2] - b.Transform[2]) * (a.Transform[2] - b.Transform[2]);
            Mrea.ScriptObject? balt = dtrg == null ? null : scripts.Where(o => o.Type == "BALT").OrderBy(o => D2(o, dtrg)).FirstOrDefault();
            Mrea.ScriptObject? kick = Named("TRGO", "Kick Out Ball"), reset = Named("TIMR", "Bomb Slot Reset");
            if (enter == null || plat == null || actr == null || balt == null || dtrg == null || kick == null)
            {
                notes.Add("turret: PLRT found but not its enter trigger, platform, animated actor or bomb slot; left out");
                return null;
            }
            static float F(Mrea.Property? p, float fallback) => p is { Data.Length: >= 4 } ? p.F32 : fallback;
            static float[] A(Vector3 v) => new[] { R(v.X), R(v.Y), R(v.Z) };
            static float R(float v) => MathF.Round(v, 4);
            (Vector3 Min, Vector3 Max) Box(float[] x, float sx = 1, float sy = 1, float sz = 1)
            {
                Vector3 a = map(x[0] - x[6] * sx / 2, x[1] - x[7] * sy / 2, x[2] - x[8] * sz / 2), b = map(x[0] + x[6] * sx / 2, x[1] + x[7] * sy / 2, x[2] + x[8] * sz / 2);
                return (Vector3.ComponentMin(a, b), Vector3.ComponentMax(a, b));
            }
            var t = new TurretSpot { Name = $"{plrt.Name} {plrt.Id:x8}" };
            float[] px = plrt.Transform;
            t.Base = A(map(px[0], px[1], px[2]));
            // the barrel's centre way: Echoes +Y turned by the actor's Z rotation
            float rz = MathHelper.DegreesToRadians(px[5]);
            Vector3 fwd = map(-MathF.Sin(rz), MathF.Cos(rz), 0).Normalized();
            t.CentreHeading = R(MathHelper.RadiansToDegrees(MathF.Atan2(fwd.X, fwd.Z)));
            Mrea.Property r = plrt.Root;
            t.YawLeft = F(r.Find(0x17cd8b2a), 90);
            t.YawRight = F(r.Find(0x1473dad2), 90);
            t.PitchUp = F(r.Find(0x3650ce75), 45);
            t.PitchDown = F(r.Find(0x78520e6e), 45);
            t.YawRate = F(r.Find(0xfb2e32db), 120);
            t.PitchRate = F(r.Find(0x1b3c8683), 60);
            t.FireRate = F(r.Find(0xc6e48f18), 1);
            Mrea.Property? wd = r.Find(0x8e5f7e96);
            t.Damage = (int)MathF.Round(F(wd?.Find(0xf2d02613), 75));       // Echoes' 99-point life = MPH's 99 (owner D1)
            t.SplashRadius = R(F(wd?.Find(0xee1be914), 10) * scale);
            t.Knockback = F(wd?.Find(0x555ff80a), 20);
            // the shot (WPSC eea4cf05, multiplayer): 128 frames, 1 m a frame (60 m/s); 51 m is how far it's heard, not its
            // reach (the DOL's projectile sound setup); Wpsc can't read Echoes' tags yet, so these are the earlier RE's values
            t.ShotSpeed = R(60 * scale);
            t.ShotRange = R(51 * scale);
            t.ShotLife = 128 / 60f;
            // the platform: health and collision mesh (DCLN), centred on the turret's pivot, the barrel along Echoes +Y
            t.Health = (int)MathF.Round(F(plat.Root.Find(0xcf90d15e)?.Find(0xf0668919), 100)) * EchoesBreakables.Unit;
            uint dcln = plat.Root.Find(0x0fc966dc)?.U32 ?? uint.MaxValue;
            if (pak.Contains(dcln) && ReadDcln(pak.Get(dcln)) is (float[] v, int[] tri))
            {
                t.ShapeVerts = new float[v.Length];
                for (int i = 0; i + 2 < v.Length; i += 3)
                {
                    Vector3 m = map(v[i], v[i + 1], v[i + 2]);
                    t.ShapeVerts[i] = R(m.X); t.ShapeVerts[i + 1] = R(m.Y); t.ShapeVerts[i + 2] = R(m.Z);
                }
                t.ShapeTris = tri;
            }
            else
            {
                notes.Add($"turret: platform collision {dcln:x8} not read; the turret takes no hits");
            }
            // the lift: the pivot (turret_SDK) and the seat (upwardballsnap_LCTR) over the rise and fall, from the animated
            // actor's own animation set; the muzzle's reach (muzzleend_LCTR) in the raised pose
            float[] ax = actr.Transform;
            Mrea.Property? animSet = actr.Root.Find(0xe25fb08c);
            uint ancsId = animSet is { Data.Length: >= 4 } ? animSet.U32 : uint.MaxValue;
            if (!pak.Contains(ancsId))
            {
                notes.Add($"turret: animation set {ancsId:x8} not found; left out");
                return null;
            }
            Ancs ancs = Ancs.Parse(pak.Get(ancsId));
            Ancs.Character ch = ancs.Characters[0];
            Cinf sk = Cinf.Load(pak.Get(ch.Cinf));
            uint Bone(string n) => sk.Bones.Values.First(b => b.Name == n).Id;
            uint pivotBone = Bone("turret_SDK"), seatBone = Bone("upwardballsnap_LCTR"), muzzleBone = Bone("muzzleend_LCTR");
            Anim? Clip(string n) => ancs.Set?.Anims.FirstOrDefault(a => a.Name == n).Anim is { } m && pak.Contains(m.AnimId) ? Anim.Load(pak.Get(m.AnimId), echoes: true) : null;
            Anim? up = Clip("turret_ball_up"), down = Clip("turret_ball_down");
            if (up == null || down == null)
            {
                notes.Add("turret: turret_ball_up/down not found; left out");
                return null;
            }
            Vector3 World(Anim a, double time, uint bone)
            {
                double[] p = Pose(sk, a, time)[bone].P;
                Vector3 e = ArenaHdWriter.Place(ax, new Vector3((float)p[0], (float)p[1], (float)p[2]));
                return map(e.X, e.Y, e.Z);
            }
            const int samples = 24;
            float[] Curve(Anim a) => Enumerable.Range(0, samples + 1).Select(i => R(World(a, a.Duration * i / samples, pivotBone).Y)).ToArray();
            t.RiseTime = (float)up.Duration;
            t.LowerTime = (float)down.Duration;
            t.RiseCurve = Curve(up);
            t.LowerCurve = Curve(down);
            Vector3 pivot = World(up, up.Duration, pivotBone), seat = World(up, up.Duration, seatBone), muzzle = World(up, up.Duration, muzzleBone);
            t.Pivot = A(pivot);
            t.SeatOffset = A(seat - pivot);
            t.Barrel = R((muzzle - pivot).Length);
            // the trigger boxes
            (Vector3 emin, Vector3 emax) = Box(enter.Transform);
            t.EnterMin = A(emin); t.EnterMax = A(emax);
            (Vector3 smin, Vector3 smax) = Box(balt.Transform);
            t.SlotMin = A(smin); t.SlotMax = A(smax);
            (Vector3 bmin, Vector3 bmax) = Box(dtrg.Transform);
            t.BombMin = A(bmin); t.BombMax = A(bmax);
            (Vector3 kmin, Vector3 kmax) = Box(kick.Transform);
            t.KickMin = A(kmin); t.KickMax = A(kmax);
            // the slot's fixed cameras: the CameraHints its ball trigger adds as a ball enters (ENTR INCR), each a fixed
            // transform (HintFixedTransform) looking along its +Y
            var cams = new List<float>();
            foreach ((string st, string msg, uint target) in balt.Connections)
            {
                if (st != "ENTR" || msg != "INCR") continue;
                Mrea.ScriptObject? c = scripts.FirstOrDefault(o => o.Type == "CAMH" && (o.Id & 0xffff) == (target & 0xffff));
                if (c == null) continue;
                float[] cx = c.Transform;
                Vector3 at = ArenaHdWriter.Place(cx, Vector3.Zero), ahead = ArenaHdWriter.Place(cx, Vector3.UnitY) - at;
                Vector3 pos = map(at.X, at.Y, at.Z), dir = (map(ahead.X, ahead.Y, ahead.Z) / scale).Normalized();
                cams.AddRange(new[] { R(pos.X), R(pos.Y), R(pos.Z), R(dir.X), R(dir.Y), R(dir.Z) });
            }
            if (cams.Count > 0) t.SlotCams = cams.ToArray();
            else notes.Add("turret: the bomb slot's camera hints not found");
            if (Named("TRGR", "Player Kill") is { } kill)
            {
                (Vector3 lmin, Vector3 lmax) = Box(kill.Transform);
                t.KillMin = A(lmin); t.KillMax = A(lmax);
            }
            // the solid column around the seat while manned (ACTR 0x10c 'Dumb Collision Box': its CollisionBox 0xf344c0b0 about
            // CollisionOffset 0x2e686c2a, times the actor's scale; solid to players, shots pass)
            static Mrea.Property? Deep(Mrea.Property p, uint id)
            {
                if (p.Children == null) return null;
                foreach (Mrea.Property c in p.Children)
                {
                    if (c.Id == id) return c;
                    if (Deep(c, id) is { } d) return d;
                }
                return null;
            }
            if (Named("ACTR", "Dumb Collision Box") is { } dumb && Deep(dumb.Root, 0xf344c0b0) is { Data.Length: >= 12 } cb)
            {
                float[] x = (float[])dumb.Transform.Clone();
                if (Deep(dumb.Root, 0x2e686c2a) is { Data.Length: >= 12 } co)
                {
                    for (int k = 0; k < 3; k++) x[k] += Be.F32(co.Data, 4 * k) * x[6 + k];
                }
                (Vector3 dmin, Vector3 dmax) = Box(x, Be.F32(cb.Data, 0), Be.F32(cb.Data, 4), Be.F32(cb.Data, 8));
                t.ColumnMin = A(dmin); t.ColumnMax = A(dmax);
            }
            else
            {
                notes.Add("turret: the seat's column (ACTR 'Dumb Collision Box') not found");
            }
            // the lift's base: the model's bounds (CMDL header: magic, version, flags, then min and max) about the platform,
            // between its bottom (the platform's place) and the top waypoint
            if (Named("PLAT", "Dummy Base") is { } dbase && Named("WAYP", "Top waypoint") is { } top
                && Deep(dbase.Root, 0xc27ffa8f) is { Data.Length: >= 4 } bm && pak.Contains(bm.U32) && pak.Get(bm.U32) is { Length: >= 36 } cmdl)
            {
                float[] bx = dbase.Transform;
                Vector3 lo = map(bx[0] + Be.F32(cmdl, 12) * bx[6], bx[1] + Be.F32(cmdl, 16) * bx[7], bx[2] + Be.F32(cmdl, 20) * bx[8]);
                Vector3 hi = map(bx[0] + Be.F32(cmdl, 24) * bx[6], bx[1] + Be.F32(cmdl, 28) * bx[7], bx[2] + Be.F32(cmdl, 32) * bx[8]);
                t.BaseMin = A(Vector3.ComponentMin(lo, hi)); t.BaseMax = A(Vector3.ComponentMax(lo, hi));
                t.BaseRise = R((map(0, 0, top.Transform[2]) - map(0, 0, bx[2])).Y);
                t.BaseTime = 2;   // its MotionSplineDuration
            }
            else
            {
                notes.Add("turret: the lift's base (PLAT 'Dummy Base') not found");
            }
            Mrea.Property? force = kick.Root.Find(0x77a27411)?.Find(0x20927e9b);
            if (force is { Data.Length: >= 12 })
            {
                Vector3 f = map(Be.F32(force.Data, 0), Be.F32(force.Data, 4), Be.F32(force.Data, 8)) / scale;   // (a force: the axes only)
                t.KickForce = A(f);
            }
            t.SlotReset = F(reset?.Root.Find(0x44335aff), 3);
            notes.Add($"turret '{plrt.Name}': pivot raised {pivot.Y - t.RiseCurve[0]:0.00} units in {t.RiseTime:0.00} s, yaw +-{t.YawLeft:0}/{t.YawRight:0} at "
                + $"{t.YawRate:0}/s, pitch +{t.PitchUp:0}/-{t.PitchDown:0} at {t.PitchRate:0}/s, shot {t.Damage} (splash {t.SplashRadius:0.00} u) every {t.FireRate:0.0} s, "
                + $"health {t.Health}, shape {t.ShapeTris?.Length / 3 ?? 0} triangles, barrel {t.Barrel:0.00} u");
            return t;
        }

        // an Echoes DCLN's first mesh: its vertices (Echoes metres, as stored) and triangles (vertex indices). The collision
        // index layout is the area collision's (Mrea.ReadCollision) after its own 12-byte header
        static (float[] Verts, int[] Tris)? ReadDcln(byte[] s)
        {
            if (s.Length < 16 || Be.U32(s, 0) == 0 || Be.U32(s, 4) != 0xDEAFBABE) return null;
            int o = 16;
            int mc = (int)Be.U32(s, o); o += 4 + 8 * mc;
            o += 4 + (int)Be.U32(s, o);     // vertex materials
            o += 4 + (int)Be.U32(s, o);     // edge materials
            o += 4 + (int)Be.U32(s, o);     // triangle materials
            int ec = (int)Be.U32(s, o); int edges = o + 4; o += 4 + 4 * ec;
            int ti = (int)Be.U32(s, o); int tris = o + 4; o += 4 + 2 * ti;
            int unk = (int)Be.U32(s, o); o += 4 + 2 * unk;
            int vc = (int)Be.U32(s, o); o += 4;
            var v = new float[vc * 3];
            for (int i = 0; i < vc * 3; i++) v[i] = Be.F32(s, o + 4 * i);
            int n = ti / 3;
            var t = new int[n * 3];
            for (int k = 0; k < n; k++)
            {
                int e0 = Be.U16(s, tris + 6 * k), e1 = Be.U16(s, tris + 6 * k + 2);
                int v0 = Be.U16(s, edges + 4 * e0), v1 = Be.U16(s, edges + 4 * e0 + 2);
                int w0 = Be.U16(s, edges + 4 * e1), w1 = Be.U16(s, edges + 4 * e1 + 2);
                t[3 * k] = v0; t[3 * k + 1] = v1; t[3 * k + 2] = w0 != v0 && w0 != v1 ? w0 : w1;
            }
            return (v, t);
        }

        // ---- the look (T4): hd/turret.json beside arena.bin ----

        public const string LookFile = "turret.json";
        const int LookSamples = 24;

        // the turret's model (the animated actor's character: skinned, so split into its bones' rigid parts, each its own
        // placed pass, about the bone's bind position), the hologram's the same way, and the bomb slot's ring (placed as
        // the area has it); each part's model matrix (MPH, row vectors) over the lift's rise and fall, the shot's recoil
        // and the hologram's spin, LookSamples + 1 poses each, evenly in time. light: Echoes' lighting at a point (Echoes
        // world space). Null when the arena has no turret
        internal static TurretLook? ImportLook(Pak pak, List<Mrea.ScriptObject> scripts, Func<float, float, float, Vector3> map, float scale,
            List<ArenaHdWriter.Placed> placed, Func<Vector3, (float[] Ambient, List<float[]> Lights)> light, List<string> notes)
        {
            if (!scripts.Any(o => o.Type == "PLRT")) return null;
            Mrea.ScriptObject? Named(string type, string name) => scripts.FirstOrDefault(o => o.Type == type && o.Name == name);
            Mrea.ScriptObject? actr = Named("ACTR", "Animating Turret"), holo = Named("ACTR", "Turret Hologram"), ring = Named("ACTR", "Bomb Slot Ring");
            if (actr == null) return null;
            var look = new TurretLook { Samples = LookSamples };
            static float R(float v) => MathF.Round(v, 5);
            Vector3 Unmap(Vector3 v) => new Vector3(v.X / scale, -v.Z / scale, v.Y / scale);
            // a part's model matrix: its vertices (MPH axes, about the bone's bind position) to the world
            float[] Matrix(float[] ax, double[] q, double[] p)
            {
                Vector3 F(Vector3 local)
                {
                    Vector3 e = Unmap(local);
                    double[] r = Rotate(q, new double[] { e.X, e.Y, e.Z });
                    Vector3 w = ArenaHdWriter.Place(ax, new Vector3((float)(r[0] + p[0]), (float)(r[1] + p[1]), (float)(r[2] + p[2])));
                    return map(w.X, w.Y, w.Z);
                }
                Vector3 o = F(Vector3.Zero), x = F(Vector3.UnitX) - o, y = F(Vector3.UnitY) - o, z = F(Vector3.UnitZ) - o;
                return new[] { R(x.X), R(x.Y), R(x.Z), R(y.X), R(y.Y), R(y.Z), R(z.X), R(z.Y), R(z.Z), R(o.X), R(o.Y), R(o.Z) };
            }
            // a bone's effect space (Echoes metres about the bone, Z up) to the world
            float[] FxMatrix(float[] ax, double[] q, double[] p)
            {
                Vector3 F(Vector3 e)
                {
                    double[] r = Rotate(q, new double[] { e.X, e.Y, e.Z });
                    Vector3 w = ArenaHdWriter.Place(ax, new Vector3((float)(r[0] + p[0]), (float)(r[1] + p[1]), (float)(r[2] + p[2])));
                    return map(w.X, w.Y, w.Z);
                }
                Vector3 o = F(Vector3.Zero), x = F(Vector3.UnitX) - o, y = F(Vector3.UnitY) - o, z = F(Vector3.UnitZ) - o;
                return new[] { R(x.X), R(x.Y), R(x.Z), R(y.X), R(y.Y), R(y.Z), R(z.X), R(z.Y), R(z.Z), R(o.X), R(o.Y), R(o.Z) };
            }
            // one actor's character: its parts (placed passes) and their poses over each clip, and its effect bones'
            // (locators, Pass -1)
            bool Character(Mrea.ScriptObject a, bool isHolo, string[] locators, params (string Clip, Action<TurretPiece, float[]> Add, Action<float> Time)[] clips)
            {
                Mrea.Property? set = a.Root.Find(0xe25fb08c);
                if (set is not { Data.Length: >= 8 } || !pak.Contains(Be.U32(set.Data, 0))) return false;
                Ancs ancs = Ancs.Parse(pak.Get(Be.U32(set.Data, 0)));
                int ci = (int)Be.U32(set.Data, 4);
                if (ci >= ancs.Characters.Count) return false;
                Ancs.Character ch = ancs.Characters[ci];
                if (!pak.Contains(ch.Cmdl) || !pak.Contains(ch.Cskr) || !pak.Contains(ch.Cinf)) return false;
                Cinf sk = Cinf.Load(pak.Get(ch.Cinf));
                Cmdl g = Cmdl.Load(pak.Get, ch.Cmdl, ch.Cskr);
                uint turns = sk.Bones.Values.FirstOrDefault(b => b.Name == "turretMain_SDK")?.Id ?? uint.MaxValue;
                bool Under(uint id, uint top)
                {
                    for (int n = 0; n < 64 && sk.Bones.TryGetValue(id, out Cinf.Bone? b); n++, id = b.Parent) if (id == top) return true;
                    return false;
                }
                var bones = Enumerable.Range(0, g.TriangleCount).Select(t => ArenaHdWriter.TriangleBone(g, t))
                    .Where(b => sk.Bones.ContainsKey(b)).GroupBy(b => b).OrderBy(b => b.Key).ToList();
                var pieces = new List<(TurretPiece Piece, uint Bone)>();
                foreach (var b in bones)
                {
                    Cinf.Bone bone = sk.Bones[b.Key];
                    int pass = ArenaHdWriter.FirstPlacedPass + placed.Count;
                    placed.Add(new ArenaHdWriter.Placed(ch.Cmdl, a.Transform, pass) { Skin = ch.Cskr, Bone = b.Key, Origin = new Vector3((float)bone.X, (float)bone.Y, (float)bone.Z) });
                    var piece = new TurretPiece { Bone = bone.Name ?? "", Pass = pass, Holo = isHolo, Turns = !isHolo && Under(b.Key, turns), Triangles = b.Count() };
                    look.Pieces.Add(piece);
                    pieces.Add((piece, b.Key));
                }
                int drawn = pieces.Count;
                foreach (string name in locators)
                {
                    if (sk.Bones.Values.FirstOrDefault(b => b.Name == name) is not Cinf.Bone lb) { notes.Add($"turret look: no bone {name}"); continue; }
                    var piece = new TurretPiece { Bone = name, Pass = -1, Turns = !isHolo };
                    (look.Locators ??= new()).Add(piece);
                    pieces.Add((piece, lb.Id));
                }
                foreach ((string clip, Action<TurretPiece, float[]> add, Action<float> time) in clips)
                {
                    Ancs.MetaAnim? m = ancs.Set?.Anims.FirstOrDefault(x => x.Name == clip).Anim;
                    if (m == null || !pak.Contains(m.AnimId)) { notes.Add($"turret look: clip {clip} not found"); continue; }
                    Anim an = Anim.Load(pak.Get(m.AnimId), echoes: true);
                    time((float)an.Duration);
                    var poses = new List<float>[pieces.Count];
                    for (int k = 0; k < pieces.Count; k++) poses[k] = new List<float>();
                    for (int i = 0; i <= LookSamples; i++)
                    {
                        Dictionary<uint, (double[] Q, double[] P)> pose = Pose(sk, an, an.Duration * i / LookSamples);
                        for (int k = 0; k < pieces.Count; k++)
                        {
                            (double[] q, double[] p) = pose[pieces[k].Bone];
                            poses[k].AddRange(k < drawn ? Matrix(a.Transform, q, p) : FxMatrix(a.Transform, q, p));
                        }
                    }
                    for (int k = 0; k < pieces.Count; k++) add(pieces[k].Piece, poses[k].ToArray());
                }
                return true;
            }
            float[] ax = actr.Transform;
            if (!Character(actr, false, EventFx.Select(e => e.Bone).Distinct().ToArray(), ("turret_ball_up", (p, m) => p.Up = m, t => look.UpTime = t), ("turret_ball_down", (p, m) => p.Down = m, t => look.DownTime = t),
                ("turret_shoot", (p, m) => p.Shoot = m, t => look.ShootTime = t)))
            {
                notes.Add("turret look: the animated actor's character not read; not drawn");
                return null;
            }
            (float[] amb, List<float[]> lit) = light(ArenaHdWriter.Place(ax, new Vector3(0, 0, 8)));
            look.Ambient = amb; look.Lights = lit;
            if (holo != null && Character(holo, true, Array.Empty<string>(), ("holo_spin", (p, m) => p.Spin = m, t => look.HoloTime = t)))
            {
                (look.HoloAmbient, look.HoloLights) = light(new Vector3(holo.Transform[0], holo.Transform[1], holo.Transform[2]));
            }
            if (ring != null && ring.Root.Find(0xC27FFA8F) is { Data.Length: >= 4 } rm && pak.Contains(rm.U32))
            {
                look.RingPass = ArenaHdWriter.FirstPlacedPass + placed.Count;
                placed.Add(new ArenaHdWriter.Placed(rm.U32, ring.Transform, look.RingPass));
                (look.RingAmbient, look.RingLights) = light(new Vector3(ring.Transform[0], ring.Transform[1], ring.Transform[2]));
            }
            // the hologram's glow (ACTR 0x109 'Glow for hologram', CMDL d87334fa): placed, shown with the hologram
            Mrea.ScriptObject? glow = Named("ACTR", "Glow for hologram") ?? scripts.FirstOrDefault(o => o.Type == "ACTR" && (o.Id & 0xffff) == 0x109);
            if (glow != null && glow.Root.Find(0xC27FFA8F) is { Data.Length: >= 4 } gm && pak.Contains(gm.U32))
            {
                look.GlowPass = ArenaHdWriter.FirstPlacedPass + placed.Count;
                placed.Add(new ArenaHdWriter.Placed(gm.U32, glow.Transform, look.GlowPass));
            }
            else
            {
                notes.Add("turret look: the hologram's glow (ACTR 'Glow for hologram') not found");
            }
            notes.Add($"turret look: {look.Pieces.Count(p => !p.Holo)} turret parts ({string.Join(", ", look.Pieces.Where(p => !p.Holo).Select(p => $"{p.Bone} {p.Triangles}{(p.Turns ? " turns" : "")}"))}), "
                + $"{look.Pieces.Count(p => p.Holo)} hologram parts{(look.GlowPass >= 0 ? " + glow" : "")}, ring {(look.RingPass >= 0 ? "placed" : "NOT found")}; clips up {look.UpTime:0.00} s, down {look.DownTime:0.00} s, "
                + $"shoot {look.ShootTime:0.00} s, hologram spin {look.HoloTime:0.00} s");
            return look;
        }

        public static TurretLook? LoadLook(string hdDir)
        {
            string f = Path.Combine(hdDir, LookFile);
            if (!File.Exists(f)) return null;
            try
            {
                return JsonSerializer.Deserialize<TurretLook>(File.ReadAllText(f));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- the sounds: hd/turret_<role>.wav beside turret.json, listed in its Sounds ----

        // Echoes' game sound ids go through AudioGrp.pak's ATBL to MusyX sfx ids in these audio groups (MannedTurret,
        // MannedTurret2 and PickupsSpecial in Metroid6.pak; SamusElectricHit, the slot's arc, only in AudioGrp.pak)
        const uint SoundLookup = 0xb7c1b3a7;
        static readonly uint[] SoundGroups = { 0x3b13ca9f, 0x6e01e6a4, 0x00b10d9c, 0xa2f353cc };
        // the ids no area object holds: the turret actor's animation events (set e749cb49: turret_ball_up's at 0.067 s,
        // turret_ball_down's at 0) and its multiplayer shot's (WPSC eea4cf05's flight sound, its CRSC 9cb622b7's default
        // hit sound)
        const uint EnterSound = 0x268f, ExitSound = 0x268e, ShotFlySound = 0x256d, ShotHitSound = 0x2573;
        public const float EnterSoundAt = 0.0667f;
        // Echoes metres: the turret SONDs' max distance, for the sounds without one of their own
        const float SoundRange = 150;

        public static string SoundFile(string role) => $"turret_{role}.wav";

        // each sound rendered from the disc's MusyX data (Agsc.Render; a loop as one turn of it): the SOND objects' own
        // volume, range, loop and ambient (not placed) flags, the PLRT's rotation / multiplayer shot / multiplayer
        // impact sounds. Null without the turret or AudioGrp.pak. gratesOnly: an arena with grates and no turret (Sidehopper
        // Station): the grates' break sound alone (scripts with the generated layer: its sound is a generated copy)
        internal static List<TurretSound>? ImportSounds(Pak pak, Pak? audio, List<Mrea.ScriptObject> scripts, float scale, string dir, List<string> notes,
            bool gratesOnly = false)
        {
            Mrea.ScriptObject? plrt = scripts.FirstOrDefault(o => o.Type == "PLRT");
            if (plrt == null && !gratesOnly) return null;
            if (audio == null || !audio.Contains(SoundLookup))
            {
                notes.Add("turret sounds: no AudioGrp.pak (its sound lookup); none written");
                return null;
            }
            short[] lookup = MusyX.Lookup(audio.Get(SoundLookup));
            var groups = new List<Agsc>();
            foreach (uint g in SoundGroups)
            {
                if (pak.Contains(g)) groups.Add(Agsc.Parse(pak.Get(g)));
                else if (audio.Contains(g)) groups.Add(Agsc.Parse(audio.Get(g)));
            }
            static uint U(Mrea.Property? p) => p is { Data.Length: >= 4 } ? Be.U32(p.Data, 0) : UInt32.MaxValue;
            static bool B(Mrea.Property? p) => p is { Data.Length: >= 1 } && p.Data[0] != 0;
            var want = plrt == null || gratesOnly ? new List<TurretSound>() : new List<TurretSound>
            {
                new() { Role = "enter", Id = EnterSound, Range = SoundRange },
                new() { Role = "exit", Id = ExitSound, Range = SoundRange },
                new() { Role = "rotate", Id = U(plrt.Root.Find(0xe7234f72)), Loop = true, Range = SoundRange },   // SFXTurretRotation
                new() { Role = "fire", Id = U(plrt.Root.Find(0x035459fd)), Range = SoundRange },                  // SFXMultiPlayerProjectile
                new() { Role = "hit", Id = U(plrt.Root.Find(0x7cabd1f1)), Ambient = true, Range = SoundRange },   // SFXMultiPlayerImpact
                new() { Role = "fly", Id = ShotFlySound, Loop = true, Range = SoundRange },
                new() { Role = "impact", Id = ShotHitSound, Range = SoundRange },
            };
            foreach ((string role, string name) in gratesOnly ? Array.Empty<(string, string)>() : new[] { ("hud", "Sound - Turret Hud Loop"), ("holo", "Sound - Turret Hologram"),
                ("explode", "Sound - Turret Explode MP Sound"), ("arc", "Electrical Arc") })
            {
                Mrea.ScriptObject? o = scripts.FirstOrDefault(o => o.Type == "SOND" && o.Name == name);
                if (o == null)
                {
                    notes.Add($"turret sounds: no SOND '{name}'");
                    continue;
                }
                want.Add(Sond(role, o));
            }
            // the grates' break: their death generator's PLAY target (Shooting Gallery's 'Sound 002') or the SOND it makes a
            // copy of (ACTV: Sidehopper Station's 'Crate Explode')
            if (GrateGenerator(scripts) is { } genr)
            {
                foreach ((string _, string msg, uint target) in genr.Connections)
                {
                    if (msg is "PLAY" or "ACTV" && scripts.FirstOrDefault(o => o.Id == target && o.Type == "SOND") is { } gs)
                    {
                        want.Add(Sond("grate", gs));
                        break;
                    }
                }
            }
            static TurretSound Sond(string role, Mrea.ScriptObject o)
            {
                Mrea.Property r = o.Root;
                return new TurretSound
                {
                    Role = role, Id = U(r.Find(0x5f7c352e)),
                    Volume = MathF.Round(Math.Clamp((int)U(r.Find(0xc712847c)), 0, 127) / 127f, 4),           // MaxVolume
                    Range = r.Find(0x214e48a0) is { Data.Length: >= 4 } md ? md.F32 : SoundRange,               // MaxAudibleDistance
                    Loop = B(r.Find(0xeda47ff6)), Ambient = B(r.Find(0x8971b7a7)),
                };
            }
            var done = new List<TurretSound>();
            foreach (TurretSound s in want)
            {
                int m = s.Id == UInt32.MaxValue ? -1 : MusyX.Translate(lookup, s.Id);
                Agsc.Rendered? got = null;
                foreach (Agsc g in groups)
                {
                    if (m >= 0 && g.Render((ushort)m) is Agsc.Rendered x && (s.Loop ? x.Loop : x.Pcm).Length > 0)
                    {
                        got = x;
                        break;
                    }
                }
                // else any of the area pak's groups that holds it (the grates' break: MetalDebrisMed 47c6d0c0)
                if (got == null && m >= 0)
                {
                    foreach (uint id in pak.Order)
                    {
                        if (pak.TypeOf(id) != "AGSC" || SoundGroups.Contains(id)) continue;
                        Agsc g;
                        try
                        {
                            g = Agsc.Parse(pak.Get(id));
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            continue;
                        }
                        if (!g.Sounds.ContainsKey((ushort)m)) continue;
                        groups.Add(g);
                        if (g.Render((ushort)m) is Agsc.Rendered x && (s.Loop ? x.Loop : x.Pcm).Length > 0) got = x;
                        break;
                    }
                }
                if (got == null)
                {
                    notes.Add($"turret sounds: {s.Role} ({s.Id:x4}, sfx {m}) not found in the audio groups");
                    continue;
                }
                s.Range = MathF.Round(s.Range * scale, 3);
                MusyX.WriteWav(Path.Combine(dir, SoundFile(s.Role)), s.Loop ? got.Loop : got.Pcm, got.Rate);
                done.Add(s);
            }
            notes.Add($"turret sounds: {done.Count} of {want.Count} ({string.Join(", ", done.Select(s => s.Role))})");
            return done;
        }

        // ---- the effects: hd/turret_fx/ beside turret.json, listed in its Fx ----

        public const string FxDir = "turret_fx";
        // the actor's animation events (set e749cb49's particle points; Echoes' layout after the base point: flags, duration
        // in frames (0 = until the next clip turns it off), PART, bone id, scale, mode 2 = on the bone; the point's name ends
        // "-<duration>"): turret_shoot's muzzle flash at 0 (2 frames) and its smoke at 0.067 s (10) on muzzleend_LCTR; the
        // ball glow on ball_SDK at 0 in every raised clip (ball_up, the pitch additives, spins, still: duration 0, so on the
        // whole time the turret is up) and turret_ball_down's own (15 frames, flags bit 0: its particles wiped as it ends,
        // Retro CParticleDatabase). A clip's start stops every effect's emission (CAnimData SuspendAllActiveEffects)
        static readonly (string Role, uint Part, string Bone, float Time, int Frames, bool Wipe)[] EventFx =
        {
            ("muzzle", 0x795c46cc, "muzzleend_LCTR", 0, 2, false), ("smoke", 0xdb867cd9, "muzzleend_LCTR", 0.0667f, 10, false),
            ("ballglow", 0xa007a9a9, "ball_SDK", 0, 0, false), ("ballglowdown", 0xa007a9a9, "ball_SDK", 0, 15, true),
        };
        // the multiplayer shot's hit table (WPSC eea4cf05's CRSC): its default hit effect
        const uint ShotHits = 0x9cb622b7;
        const uint ShotWpsc = 0xeea4cf05;
        // the area's effect objects (EFCT, by name): explode (each blow-up), lift (the lift's glow from Enter until Leave or
        // Destroyed), lower (from Leave), held (the slot's arcs while it holds a ball: BALT ENTR / EXIT), bombarc (the slot's
        // arc as a bomb takes the turret: DTRG DEAD, off 0.5 s later by TIMR 0x272)
        static readonly (string Role, string Name)[] PlacedFx =
        {
            ("explode", "Effect_TurretExplode"), ("lift", "Effect_LiftFX5"), ("lower", "Effect_LowerTurret"),
            ("held", "BombSlotElectric"), ("bombarc", "Arc"),
        };
        public const float BombArcTime = 0.5f;

        // each effect's PART / ELSC and everything it reaches (child systems, swooshes, textures as <id>.png), raw as
        // <id>.part / .elsc / .swhc (Echoes' layout: read with echoes: true); the placed ones' effect space (Echoes metres,
        // Z up, the EFCT's turn and scale) to the world in MPH. Null without the turret
        // (placed/look: each model a particle draws (PMDL) is placed in the arena's HD look at its own pass, model space
        // = Echoes' at the origin, and look.ModelPasses maps its CMDL to that pass; call before ArenaHdWriter.Write)
        // gratesOnly: an arena with grates and no turret (Sidehopper Station): the grates' break alone
        internal static List<TurretFx>? ImportFx(Pak pak, List<Mrea.ScriptObject> scripts, Func<float, float, float, Vector3> map, string hdDir, List<string> notes,
            List<ArenaHdWriter.Placed>? placed = null, TurretLook? look = null, bool gratesOnly = false)
        {
            if (gratesOnly ? GrateGenerator(scripts) == null : !scripts.Any(o => o.Type == "PLRT")) return null;
            string dir = Path.Combine(hdDir, FxDir);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var list = new List<TurretFx>();
            static float R(float v) => MathF.Round(v, 5);
            // the effect's asset: the first id in its properties the pak holds as a PART or ELSC
            (uint Id, string Kind)? Asset(Mrea.Property p)
            {
                if (p.Children == null)
                {
                    if (p.Data.Length == 4 && pak.Contains(p.U32) && pak.TypeOf(p.U32) is "PART" or "ELSC") return (p.U32, pak.TypeOf(p.U32).ToLowerInvariant());
                    return null;
                }
                foreach (Mrea.Property c in p.Children)
                {
                    if (Asset(c) is { } a) return a;
                }
                return null;
            }
            foreach ((string role, string name) in gratesOnly ? Array.Empty<(string, string)>() : PlacedFx)
            {
                Mrea.ScriptObject? o = scripts.FirstOrDefault(o => o.Type == "EFCT" && o.Name == name);
                if (o == null || Asset(o.Root) is not { } a)
                {
                    notes.Add($"turret fx: no EFCT '{name}' (or its effect)");
                    continue;
                }
                float[] x = o.Transform;
                Vector3 F(Vector3 e)
                {
                    Vector3 w = ArenaHdWriter.Place(x, e);
                    return map(w.X, w.Y, w.Z);
                }
                Vector3 at = F(Vector3.Zero), ex = F(Vector3.UnitX) - at, ey = F(Vector3.UnitY) - at, ez = F(Vector3.UnitZ) - at;
                list.Add(new TurretFx
                {
                    Role = role, Kind = a.Kind, Id = a.Id,
                    At = new[] { R(ex.X), R(ex.Y), R(ex.Z), R(ey.X), R(ey.Y), R(ey.Z), R(ez.X), R(ez.Y), R(ez.Z), R(at.X), R(at.Y), R(at.Z) },
                });
            }
            foreach ((string role, uint part, string bone, float time, int frames, bool wipe) in gratesOnly ? Array.Empty<(string, uint, string, float, int, bool)>() : EventFx)
            {
                if (pak.Contains(part)) list.Add(new TurretFx { Role = role, Kind = "part", Id = part, Bone = bone, Time = time, Frames = frames, Wipe = wipe });
                else notes.Add($"turret fx: {role} ({part:x8}) not in the pak");
            }
            if (!gratesOnly && pak.Contains(ShotHits) && Crsc.Load(pak.Get(ShotHits)).DefaultEffect is uint hit and not 0 and not UInt32.MaxValue && pak.Contains(hit))
            {
                list.Add(new TurretFx { Role = "impact", Kind = "part", Id = hit, Frames = 1 });
            }
            // the grates' break (ACTR 'Grating' DEAD -> ZERO its GENR): Echoes' own debris, not MPH's mechanical death.
            // CScriptGenerator makes a fresh copy of each of its ACTV targets (DebrisExtended, generated layer) at the
            // grating (no FOLW: the sender), its own turn kept (UseOriginatorTransform off), up to RandomCount of them;
            // CScriptDebris (extended) then flies it on its own (GrateDebris; drawn by the app's CampaignRenderer.TurretFx.cs)
            if (GrateGenerator(scripts) is { } genr)
            {
                var pieces = genr.Connections.Where(c => c.Message == "ACTV").Select(c => scripts.FirstOrDefault(o => o.Id == c.Target && o.Type == "DBR2"))
                    .OfType<Mrea.ScriptObject>().ToList();
                int count = genr.Root.Find(0x3fa67145) is { Data.Length: >= 4 } rc ? (int)rc.U32 : pieces.Count;   // RandomCount
                int added = 0;
                foreach (Mrea.ScriptObject o in pieces)
                {
                    if (GrateDebris(o) is not { } d || !pak.Contains(d.Id) || pak.TypeOf(d.Id) != "CMDL") continue;
                    list.Add(d);
                    added++;
                    if (placed != null && look != null && !look.ModelPasses.ContainsKey(d.Id))
                    {
                        look.ModelPasses[d.Id] = ArenaHdWriter.FirstPlacedPass + placed.Count;
                        placed.Add(new ArenaHdWriter.Placed(d.Id, new float[] { 0, 0, 0, 0, 0, 0, 1, 1, 1 }, look.ModelPasses[d.Id]));
                    }
                }
                int slots = genr.Connections.Count(c => c.Message == "ACTV");
                if (look != null && added > 0)
                {
                    look.GrateCount = count;
                    look.GrateSlots = slots;
                }
                notes.Add($"turret fx: grate break '{genr.Name}' {added} of {pieces.Count} debris pieces, {count} picks of its {slots} followers");
            }
            // the shot's own look (WPSC eea4cf05): its two billboards, B1 (an animated tile texture) and B2, at the shot
            var shotTextures = new List<uint>();
            if (!gratesOnly && pak.Contains(ShotWpsc))
            {
                try
                {
                    Wpsc w = Wpsc.Load(pak.Get(ShotWpsc), echoes: true);
                    foreach (string b in new[] { "B1", "B2" })
                    {
                        if (w[b + "TX"] is not Part.Elem tx || tx.Id is 0 or UInt32.MaxValue || !pak.Contains(tx.Id)) continue;
                        float[]? color = w[b + "CL"] is { Kind: "CNST" } cl && cl.Kids.Length == 4 ? cl.Kids.Select(k => R(k?.F ?? 0)).ToArray() : null;
                        float[]? size = w[b + "SE"] switch
                        {
                            { Kind: "CNST" } c => new[] { R(c.F), R(c.F) },
                            { Kind: "RAND" } r when r.Kids.Length == 2 => new[] { R(r.Kids[0]?.F ?? 0), R(r.Kids[1]?.F ?? 0) },
                            _ => null,
                        };
                        int[]? tiles = tx.Kind == "ATEX" && tx.Kids.Length == 6
                            ? new[] { tx.Kids[0]?.I ?? 0, tx.Kids[1]?.I ?? 0, tx.Kids[2]?.I ?? 0, tx.Kids[3]?.I ?? 0, tx.Kids[4]?.I ?? 0, tx.Kids[5]?.B == true ? 1 : 0 }
                            : null;
                        list.Add(new TurretFx { Role = "shot" + b[1], Kind = "board", Id = tx.Id, Color = color, Size = size, Tiles = tiles, Additive = w.Flag("R" + b + "A") });
                        shotTextures.Add(tx.Id);
                    }
                }
                catch (InvalidDataException x)
                {
                    notes.Add($"turret fx: shot {ShotWpsc:x8} not read ({x.Message})");
                }
            }
            // the files
            var done = new HashSet<uint>();
            var textures = new SortedSet<uint>(shotTextures);
            var todo = new Queue<uint>(list.Where(f => f.Kind is not ("board" or "debris")).Select(f => f.Id)
                .Concat(list.Where(f => f.Death != 0).Select(f => f.Death)));
            int models = 0;
            while (todo.Count > 0)
            {
                uint id = todo.Dequeue();
                if (!done.Add(id) || !pak.Contains(id)) continue;
                string type = pak.TypeOf(id);
                byte[] b = pak.Get(id);
                IEnumerable<(string Type, uint Id)> deps = type switch
                {
                    "PART" => Part.Load(b, echoes: true).Dependencies(),
                    "ELSC" => Elsc.Load(b).Dependencies(),
                    "SWHC" => Part.Swoosh.Load(b).Texture is uint t and not 0 and not UInt32.MaxValue ? new[] { ("TXTR", t) } : Array.Empty<(string, uint)>(),
                    _ => Array.Empty<(string, uint)>(),
                };
                if (type is not ("PART" or "ELSC" or "SWHC")) continue;
                File.WriteAllBytes(Path.Combine(dir, $"{id:x8}.{type.ToLowerInvariant()}"), b);
                foreach ((string t, uint dep) in deps)
                {
                    if (dep == 0 || dep == UInt32.MaxValue) continue;
                    if (t == "TXTR") textures.Add(dep);
                    else if (t is "PART" or "ELSC" or "SWHC") todo.Enqueue(dep);
                    else if (t == "CMDL")
                    {
                        models++;
                        if (placed != null && look != null && pak.Contains(dep) && !look.ModelPasses.ContainsKey(dep))
                        {
                            look.ModelPasses[dep] = ArenaHdWriter.FirstPlacedPass + placed.Count;
                            placed.Add(new ArenaHdWriter.Placed(dep, new float[] { 0, 0, 0, 0, 0, 0, 1, 1, 1 }, look.ModelPasses[dep]));
                        }
                    }
                }
            }
            foreach (uint t in textures)
            {
                if (!pak.Contains(t)) continue;
                byte[] tb = pak.Get(t);
                byte[]? rgba = Txtr.Header(tb).Format == Txtr.RGBA8 ? MphRecomp.Import.Effects.HyperFx.Rgba8(tb, out int w, out int h) : Txtr.Decode(tb, out _, out w, out h);
                if (rgba == null)
                {
                    notes.Add($"turret fx: texture {t:x8} (format {Txtr.Header(tb).Format}) not decoded");
                    continue;
                }
                PngOut.Write(Path.Combine(dir, $"{t:x8}.png"), rgba, w, h);
            }
            notes.Add($"turret fx: {list.Count} effects ({string.Join(", ", list.Select(f => f.Role))}), {done.Count} files, {textures.Count} textures"
                + (models > 0 ? $"; {models} model-particle uses (PMDL), {look?.ModelPasses.Count ?? 0} models placed" : ""));
            return list;
        }

        // the grates' death generator: the GENR each ACTR 'Grating' sets to zero as it dies (Shooting Gallery: all 8 share
        // 'Generator 003')
        internal static Mrea.ScriptObject? GrateGenerator(List<Mrea.ScriptObject> scripts)
        {
            foreach (Mrea.ScriptObject a in scripts)
            {
                if (a.Type != "ACTR" || a.Name != "Grating") continue;
                foreach ((string state, string msg, uint target) in a.Connections)
                {
                    if (state == "DEAD" && msg == "ZERO" && scripts.FirstOrDefault(o => o.Id == target && o.Type == "GENR") is { } g) return g;
                }
            }
            return null;
        }

        // a DebrisExtended as a "debris" effect (MP2 DebrisExtended.xml's properties by id): its model (Id), its own turn
        // into our axes (At: x, y, z axes then a zero translation; unscaled: the placed model is already at the arena's
        // scale), Debris (EchoesTurret.Debris*), its death particle (Death, a PART)
        static TurretFx? GrateDebris(Mrea.ScriptObject o)
        {
            Mrea.Property r = o.Root;
            static float F(Mrea.Property? p, float or) => p is { Data.Length: >= 4 } ? p.F32 : or;
            static Vector3 V(Mrea.Property? p, Vector3 or) => p is { Data.Length: >= 12 } ? new Vector3(Be.F32(p.Data, 0), Be.F32(p.Data, 4), Be.F32(p.Data, 8)) : or;
            static Vector4 C(Mrea.Property? p, Vector4 or) => p is { Data.Length: >= 16 } ? new Vector4(Be.F32(p.Data, 0), Be.F32(p.Data, 4), Be.F32(p.Data, 8), Be.F32(p.Data, 12)) : or;
            static float R(float v) => MathF.Round(v, 5);
            uint model = r.Find(0xc27ffa8f) is { Data.Length: >= 4 } mp ? mp.U32 : UInt32.MaxValue;
            if (model is 0 or UInt32.MaxValue) return null;
            uint death = r.Find(0x979042c8) is { Data.Length: >= 4 } dp && dp.U32 is not (0 or UInt32.MaxValue) ? dp.U32 : 0;
            float[] x = o.Transform;
            Vector3 Ax(Vector3 e)
            {
                Vector3 w = ArenaHdWriter.Place(x, e) - ArenaHdWriter.Place(x, Vector3.Zero);
                return new Vector3(w.X, w.Z, -w.Y);
            }
            Vector3 ex = Ax(Vector3.UnitX), ey = Ax(Vector3.UnitY), ez = Ax(Vector3.UnitZ);
            Vector3 dir = V(r.Find(0x8075f8f8), Vector3.UnitZ);
            Vector3 fin = V(r.Find(0x80c22a0a), Vector3.One);
            Vector4 end = C(r.Find(0x5af5867d), new Vector4(1, 1, 1, 0));
            float[] d = new float[DebrisLength];
            d[DebrisCone] = F(r.Find(0x8d6fc391), 0);
            d[DebrisDir] = dir.X; d[DebrisDir + 1] = dir.Y; d[DebrisDir + 2] = dir.Z;
            d[DebrisSpeed] = F(r.Find(0x0185263e), 0); d[DebrisSpeed + 1] = F(r.Find(0x140ef2cc), 0);
            d[DebrisSpin] = F(r.Find(0x863ebb76), 0); d[DebrisSpin + 1] = F(r.Find(0x957b5ddd), 0);
            d[DebrisLife] = F(r.Find(0x54a8c481), 1); d[DebrisLife + 1] = F(r.Find(0x7dd63999), 1);
            d[DebrisFadeIn] = F(r.Find(0x50051a17), 0) / 100; d[DebrisFadeOut] = F(r.Find(0x6353c409), 100) / 100;
            d[DebrisEndColor] = end.X; d[DebrisEndColor + 1] = end.Y; d[DebrisEndColor + 2] = end.Z; d[DebrisEndColor + 3] = end.W;
            d[DebrisScaleOut] = F(r.Find(0x886e7c9f), 100) / 100;
            d[DebrisEndScale] = fin.X; d[DebrisEndScale + 1] = fin.Y; d[DebrisEndScale + 2] = fin.Z;
            d[DebrisGravity] = F(r.Find(0x2f2ae3e5), 0);
            return new TurretFx
            {
                Role = "grate", Kind = "debris", Id = model, Death = death,
                At = new[] { R(ex.X), R(ex.Y), R(ex.Z), R(ey.X), R(ey.Y), R(ey.Z), R(ez.X), R(ez.Y), R(ez.Z), 0, 0, 0 },
                Debris = d.Select(R).ToArray(),
            };
        }

        // a "debris" effect's Debris: cone (degrees: the whole spread about its direction), direction (its own axes,
        // Echoes'), speed min/max (Echoes m/s), spin min/max (rad/s), life min/max (s), colour fade in end / out start
        // (shares of its life), end colour (RGBA), scale-out start (share), end scale (times its own), gravity (Echoes
        // m/s^2, down). It doesn't collide (IsCollider off: CScriptDebris filters out solid without it).
        public const int DebrisCone = 0, DebrisDir = 1, DebrisSpeed = 4, DebrisSpin = 6, DebrisLife = 8, DebrisFadeIn = 10, DebrisFadeOut = 11,
            DebrisEndColor = 12, DebrisScaleOut = 16, DebrisEndScale = 17, DebrisGravity = 20, DebrisLength = 21;

        // the seated player's own HUD (Echoes FRME_TurretHud4Combat, NoARAM.pak, GameCube only): its three textures
        // (reticle quarter, frame half, energy tile) into FxDir as <id>.png. Call after ImportFx (which clears FxDir).
        public const uint HudReticle = 0x394d7ef2, HudFrame = 0xf024e1fa, HudBar = 0xfd1a3780;

        public static TurretHud? ImportHud(Pak? hud, string hdDir, List<string> notes)
        {
            if (hud == null)
            {
                notes.Add("turret hud: no NoARAM.pak given, the seat HUD is left out");
                return null;
            }
            string dir = Path.Combine(hdDir, FxDir);
            Directory.CreateDirectory(dir);
            foreach (uint t in new[] { HudReticle, HudFrame, HudBar })
            {
                if (!hud.Contains(t))
                {
                    notes.Add($"turret hud: texture {t:x8} missing from NoARAM.pak");
                    return null;
                }
                byte[] tb = hud.Get(t);
                byte[]? rgba = Txtr.Header(tb).Format == Txtr.RGBA8 ? MphRecomp.Import.Effects.HyperFx.Rgba8(tb, out int w, out int h) : Txtr.Decode(tb, out _, out w, out h);
                if (rgba == null)
                {
                    notes.Add($"turret hud: texture {t:x8} (format {Txtr.Header(tb).Format}) not decoded");
                    return null;
                }
                PngOut.Write(Path.Combine(dir, $"{t:x8}.png"), rgba, w, h);
            }
            var look = new TurretHud { Reticle = HudReticle, Frame = HudFrame, Bar = HudBar };
            if (BakeTitle(hud, Path.Combine(dir, HudTitleFile), out int tw, out int th, notes))
            {
                look.TitleWidth = tw;
                look.TitleHeight = th;
            }
            notes.Add($"turret hud: 3 textures, title {(look.TitleWidth > 0 ? $"{tw}x{th} px" : "left out")}");
            return look;
        }

        // the title pane's text (STRG b4590ac3, English) in its font (FONT 0d940479 "Deface": a C4 texture, one glyph
        // layer per bit), laid out as Retro's CRasterFont does (left padding, glyph, advance + right padding, kerning
        // left out), white with the glyph as alpha
        public const string HudTitle = "Turret", HudTitleFile = "title.png";
        const uint HudFont = 0x0d940479;

        static bool BakeTitle(Pak hud, string path, out int width, out int height, List<string> notes)
        {
            width = height = 0;
            if (!hud.Contains(HudFont))
            {
                notes.Add($"turret hud: font {HudFont:x8} missing");
                return false;
            }
            byte[] f = hud.Get(HudFont);
            static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
            if (U32(f, 0) != 0x464f4e54 || U32(f, 4) != 4)
            {
                notes.Add("turret hud: font not version 4");
                return false;
            }
            int lineHeight = (int)U32(f, 12), baseline = (int)U32(f, 16);
            int o = 4 + 4 + 16 + 2 + 4 + 4;
            while (f[o] != 0) o++;
            o++;
            uint texId = U32(f, o);
            int count = (int)U32(f, o + 8);
            o += 12;
            var glyphs = new Dictionary<char, (float U0, float V0, int Layer, int Left, int Advance, int Right, int W, int H, int Base)>();
            for (int i = 0; i < count; i++, o += 27)
            {
                char c = (char)(f[o] << 8 | f[o + 1]);
                float u0 = BitConverter.Int32BitsToSingle((int)U32(f, o + 2)), v0 = BitConverter.Int32BitsToSingle((int)U32(f, o + 6));
                glyphs[c] = (u0, v0, f[o + 18], (sbyte)f[o + 19], (sbyte)f[o + 20], (sbyte)f[o + 21], f[o + 22], f[o + 23], f[o + 24]);
            }
            if (!hud.Contains(texId) || HudTitle.Any(c => !glyphs.ContainsKey(c)))
            {
                notes.Add("turret hud: the title font's texture or glyphs are missing");
                return false;
            }
            byte[] t = hud.Get(texId);
            var th0 = Txtr.Header(t);
            if (th0.Format != 4 || U32(t, 12) != 2)
            {
                notes.Add($"turret hud: font texture format {th0.Format} not C4");
                return false;
            }
            int tw = th0.Width, tH = th0.Height, colors = (t[16] << 8 | t[17]) * (t[18] << 8 | t[19]);
            int data = 12 + 8 + colors * 2;
            int Index(int x, int y)
            {
                int tile = (y / 8) * (tw / 8) + x / 8, b = data + tile * 32 + (y % 8) * 4 + (x % 8) / 2;
                return x % 2 == 0 ? t[b] >> 4 : t[b] & 15;
            }
            int pen = 0;
            foreach (char c in HudTitle)
            {
                var g = glyphs[c];
                pen += g.Left + g.Advance + g.Right;
            }
            width = pen;
            height = lineHeight;
            var rgba = new byte[width * height * 4];
            pen = 0;
            foreach (char c in HudTitle)
            {
                var g = glyphs[c];
                pen += g.Left;
                int gx = (int)MathF.Round(g.U0 * tw), gy = (int)MathF.Round(g.V0 * tH), top = baseline - g.Base;
                for (int y = 0; y < g.H; y++)
                {
                    for (int x = 0; x < g.W; x++)
                    {
                        int px = pen + x, py = top + y;
                        if (px < 0 || px >= width || py < 0 || py >= height || (Index(gx + x, gy + y) >> g.Layer & 1) == 0) continue;
                        int i = (py * width + px) * 4;
                        rgba[i] = rgba[i + 1] = rgba[i + 2] = rgba[i + 3] = 255;
                    }
                }
                pen += g.Advance + g.Right;
            }
            PngOut.Write(path, rgba, width, height);
            return true;
        }

        // a part's model matrix in a clip at fraction f (0..1), the poses on either side mixed
        public static Matrix4 PieceMatrix(float[]? poses, float f)
        {
            if (poses == null || poses.Length < 12) return Matrix4.Identity;
            int n = poses.Length / 12;
            float x = Math.Clamp(f, 0, 1) * (n - 1);
            int i = Math.Min((int)x, Math.Max(n - 2, 0));
            float u = n == 1 ? 0 : x - i;
            int a = 12 * i, b = 12 * Math.Min(i + 1, n - 1);
            float P(int k) => poses[a + k] + (poses[b + k] - poses[a + k]) * u;
            return new Matrix4(P(0), P(1), P(2), 0, P(3), P(4), P(5), 0, P(6), P(7), P(8), 0, P(9), P(10), P(11), 1);
        }

        // a turret part's (or locator's) model matrix now: its clip by the turret's state (none: free, lowered), then the
        // aim. Echoes turns the actor, and its pitch clips (90_up/90_down_additive) pitch turretMain_SDK and its sibling
        // ball_SDK (the base, rings and top) about the same point: the whole head turns and pitches with the aim
        public static Matrix4 PieceNow(TurretLook look, TurretPiece p, EchoesTurretEntity? t)
        {
            EchoesTurretEntity.TurretState state = t?.State ?? EchoesTurretEntity.TurretState.Free;
            float time = t?.StateTime ?? 0;
            bool shooting = t != null && state == EchoesTurretEntity.TurretState.Active && t.SinceShot < look.ShootTime;
            (float[]? poses, float f) = state switch
            {
                EchoesTurretEntity.TurretState.Free => (p.Up, 0f),
                EchoesTurretEntity.TurretState.Entering => (p.Up, look.UpTime > 0 ? time / look.UpTime : 1),
                EchoesTurretEntity.TurretState.Active => shooting ? (p.Shoot, t!.SinceShot / look.ShootTime) : (p.Up, 1f),
                EchoesTurretEntity.TurretState.Destroyed => (p.Up, 0f), // 'rest' at once (no turret_ball_down), as Free
                _ => (p.Down, look.DownTime > 0 ? time / look.DownTime : 1),
            };
            Matrix4 m = PieceMatrix(poses ?? p.Up, f);
            if (t != null && !p.Holo) m = m * Matrix4.CreateTranslation(-t.PivotNow) * t.DrawTurn * Matrix4.CreateTranslation(t.PivotNow);
            return m;
        }

        public static CrateLight? Light(float[]? ambient, List<float[]>? lights)
        {
            if (ambient == null || ambient.Length < 3) return null;
            List<float[]> ls = lights ?? new();
            return new CrateLight(new Vector3(ambient[0], ambient[1], ambient[2]), ls.Select(l => new Vector3(l[0], l[1], l[2])).ToArray(),
                ls.Select(l => new Vector3(l[3], l[4], l[5])).ToArray());
        }

        // forward kinematics: each bone's world rotation (x y z w) and position from the animation's local keys at t (a
        // bone without keys keeps its bind offset from its parent); Echoes model space, metres
        public static Dictionary<uint, (double[] Q, double[] P)> Pose(Cinf sk, Anim a, double t)
        {
            a.Sample(t, out Dictionary<uint, double[]> rot, out Dictionary<uint, double[]> trans);
            var world = new Dictionary<uint, (double[], double[])>();
            (double[], double[]) Of(uint id)
            {
                if (world.TryGetValue(id, out var w)) return w;
                Cinf.Bone b = sk.Bones[id];
                double[] q = rot.TryGetValue(id, out double[]? rq) ? rq : new double[] { 0, 0, 0, 1 };
                double[] l;
                if (trans.TryGetValue(id, out double[]? tr)) l = tr;
                else
                {
                    sk.Bones.TryGetValue(b.Parent, out Cinf.Bone? par);
                    l = new[] { b.X - (par?.X ?? 0), b.Y - (par?.Y ?? 0), b.Z - (par?.Z ?? 0) };
                }
                if (!sk.Bones.ContainsKey(b.Parent)) return world[id] = (q, l);
                (double[] pq, double[] pp) = Of(b.Parent);
                double[] rp = Rotate(pq, l);
                return world[id] = (Mul(pq, q), new[] { pp[0] + rp[0], pp[1] + rp[1], pp[2] + rp[2] });
            }
            foreach (uint id in sk.Bones.Keys) Of(id);
            return world;
        }

        public static double[] Mul(double[] a, double[] b) => new[]
        {
            a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
            a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
            a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
            a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
        };

        public static double[] Rotate(double[] q, double[] v)
        {
            double[] r = Mul(Mul(q, new[] { v[0], v[1], v[2], 0 }), new[] { -q[0], -q[1], -q[2], q[3] });
            return new[] { r[0], r[1], r[2] };
        }

        // the turret's shot: an MPH projectile with Echoes' numbers (75 at any range: MPH's 'binary' damage falloff; splash
        // the full 75 anywhere inside the radius, binary too: ApplyRadiusDamage 0x80243a70 deals the radius damage flat; 60 m/s
        // for its 128-frame life and no distance limit: the 51 m is the shot's sound range, 0x803bc964; no gravity or homing)
        public const string ShotName = BeamProjectileEntity.TurretWeaponName; // (its kill message: HEAVY TURRET)
        public static bool IsShotWeapon(WeaponInfo w) => w.Description == ShotName;

        static readonly ConditionalWeakTable<WeaponInfo, TurretSpot> _shotSpots = new();

        // a turret shot's knockback (BeamProjectileEntity.HostHitPush, after its damage; ratio -1 = a direct hit, else the
        // splash's distance / radius), as Echoes: the power is the shot's Knockback (20) on a direct hit, (1 - ratio) of it
        // in the splash (ApplyRadiusDamage, Trilogy DOL 0x80243a70); pushed along the shot's flight on a direct hit, from
        // the shooter in the splash, flat. ApplyPlayerKnockBackForce (0x8015c210): an impulse of 500 a point on the
        // player's mass 200 = 2.5 m/s a point (a third and a half of it for a body in the air), at most 40 m/s on foot and
        // 35 in the ball (PlayerEntity.HostKnock: the speed kept, the side factor on foot), then a body's own steering cut
        // for power / 50 s (0.1 to 0.6). The host's: a client's mirror turret pushes nobody (the host's positions come in).
        public static void Knock(BeamProjectileEntity beam, PlayerEntity player, float ratio)
        {
            if (Mirror || beam.Equip?.Weapon is not { } w || !_shotSpots.TryGetValue(w, out TurretSpot? t) || player.Health == 0) return;
            float power = t.Knockback * (ratio < 0 ? 1 : 1 - Math.Clamp(ratio, 0, 1));
            if (power <= 0) return;
            Vector3 dir = ratio < 0 ? beam.Velocity : player.Position - (beam.Owner?.Position ?? beam.Position);
            float dv = power * 500 / 200;
            if (!player.IsAltForm && !player.Flags1.TestFlag(PlayerFlags1.Standing)) dv /= 3.5f;
            float unit = (t.ShotRange > 0 ? t.ShotRange / 51 : EchoesArena.DefaultScale) / 30;   // m/s -> units a 30 Hz frame
            player.HostKnock(dir, dv * unit, (player.IsAltForm ? 35 : 40) * unit, Math.Clamp(power / 50, 0.1f, 0.6f));
        }

        public static WeaponInfo ShotWeapon(TurretSpot t)
        {
            WeaponInfo w = NewShotWeapon(t);
            _shotSpots.AddOrUpdate(w, t);
            BeamProjectileEntity.HostSplashDistance = SplashDistance;
            BeamProjectileEntity.HostSplashHit = SplashHit;
            return w;
        }

        static bool IsShot(BeamProjectileEntity beam) => beam.Equip?.Weapon is { } w && _shotSpots.TryGetValue(w, out _);

        // a turret shot's splash reach (BeamProjectileEntity.HostSplashDistance), as Echoes' ApplyRadiusDamage (Trilogy DOL
        // 0x80243a70): a target is in when its position is inside the radius OR its touch bounds reach into the sphere
        // (Sphere_AABox), with a clear line to it (CanDamage 0x80243710); flat damage. Here the distance to its bounds:
        // a biped's column (its sphere's width, 1.6 up from the sphere's bottom, as EchoesBreakableEntity.Occupied), an
        // alt form's sphere, a crate's cylinder, a breakable's box. Gratings and weapon crates take it (every
        // vulnerability that covers it is 100 %). Other shots, other targets: MphRead's own (null).
        public static float? SplashDistance(BeamProjectileEntity beam, EntityBase target, Vector3 at)
        {
            if (!IsShot(beam)) return null;
            Vector3 p = beam.Position;
            float centre = Vector3.Distance(p, at);
            float? bounds = target switch
            {
                PlayerEntity pl => PlayerReach(pl, at, p),
                EchoesBreakableEntity br => br.DistanceTo(p),
                EchoesCrateEntity c => CylinderReach(c.HurtVolume, p),
                _ => null,
            };
            return bounds is float b ? MathF.Min(centre, b) : null;
        }

        static float PlayerReach(PlayerEntity pl, Vector3 at, Vector3 p)
        {
            Vector3 c = pl.Volume.SpherePosition + (at - pl.Position);   // (where the shooter saw it: NetLagShift)
            float r = pl.Volume.SphereRadius;
            if (pl.IsAltForm) return MathF.Max(0, Vector3.Distance(p, c) - r);
            float y0 = c.Y - r, y1 = y0 + 1.6f;
            float dy = p.Y < y0 ? y0 - p.Y : p.Y > y1 ? p.Y - y1 : 0;
            float dxz = MathF.Max(0, (p.Xz - c.Xz).Length - r);
            return MathF.Sqrt(dxz * dxz + dy * dy);
        }

        static float CylinderReach(CollisionVolume v, Vector3 p)
        {
            if (v.Type != VolumeType.Cylinder) return Vector3.Distance(p, v.SpherePosition);
            float y0 = v.CylinderPosition.Y, y1 = y0 + v.CylinderDot;
            float dy = p.Y < y0 ? y0 - p.Y : p.Y > y1 ? p.Y - y1 : 0;
            float dxz = MathF.Max(0, (p.Xz - v.CylinderPosition.Xz).Length - v.CylinderRadius);
            return MathF.Sqrt(dxz * dxz + dy * dy);
        }

        // a turret shot's splash on a breakable (BeamProjectileEntity.HostSplashHit): Echoes' damage points in breakable
        // health (75 against a grate's 5: it breaks)
        public static bool SplashHit(BeamProjectileEntity beam, EntityBase target, int damage)
        {
            if (target is not EchoesBreakableEntity br || !IsShot(beam)) return false;
            br.Hit(damage * EchoesBreakables.Unit);
            return true;
        }

        static WeaponInfo NewShotWeapon(TurretSpot t)
        {
            int Fx(float v) => (int)MathF.Round(v * 4096);
            ushort dmg = (ushort)Math.Clamp(t.Damage, 0, ushort.MaxValue);
            int speed = Fx(t.ShotSpeed / 30);               // per 30 Hz frame
            const int dist = 0;                                 // no limit: it flies until it hits or its life ends
            ushort life = (ushort)Math.Clamp((int)MathF.Ceiling(t.ShotLife * 30), 1, 255);
            int splash = Fx(t.SplashRadius);
            return new WeaponInfo(description: ShotName, beam: BeamType.PowerBeam, beamKind: BeamType.PowerBeam,
                drawFuncIds: new byte[] { 0, 0 }, colors: new ushort[] { 4895, 17311 }, priority: 2, flags: WeaponFlags.SurfaceCollision,
                splashDamage: dmg, minChargeSplashDamage: dmg, chargedSplashDamage: dmg, splashDmgTypes: new byte[] { 3, 3 },
                shotCooldown: 60, autofireCooldown: 60, ammoType: 0, colEffects: new byte[] { 4, 95 }, muzzleEffects: new byte[] { 65, 65 },
                dmgDirTypes: new byte[] { 0, 0 }, dmgInterp: new byte[] { 3, 3 }, afflictions: new Affliction[] { Affliction.None, Affliction.None },
                padding21: 0, minCharge: 18, fullCharge: 30, ammoCost: 0, minChargeCost: 0, chargeCost: 0,
                unchargedDamage: dmg, minChargeDamage: dmg, chargedDamage: dmg, headshotDamage: dmg, minChargeHeadshotDamage: dmg, chargedHeadshotDamage: dmg,
                unchargedLifespan: life, minChargeLifespan: life, chargedLifespan: life, speedDecay: new ushort[] { 0, 0 }, padding42: 0,
                speedInterp: new ushort[] { 0, 0 }, unchargedDmgDirMag: 0, minChargeDmgDirMag: 0, chargedDmgDirMag: 0, zoomFov: 40960,
                unchargedCylRadius: 409, minChargeCylRadius: 409, chargedCylRadius: 409, unchargedSpeed: speed, minChargeSpeed: speed, chargedSpeed: speed,
                unchargedFinalSpeed: 0, minChargeFinalSpeed: 0, chargedFinalSpeed: 0, unchargedGravity: 0, minChargeGravity: 0, chargedGravity: 0,
                unchargedHoming: 0, minChargeHoming: 0, chargedHoming: 0, homingRange: 409600, homingTolerance: 3896,
                unchargedSplashRadius: splash, minChargeSplashRadius: splash, chargedSplashRadius: splash,
                unchargedDistance: dist, minChargeDistance: dist, chargedDistance: dist, unchargedSpread: 0, minChargeSpread: 0, chargedSpread: 0,
                unRicoLossH: 3686, minRicoLossH: 3686, chRicoLossH: 3686, unRicoLossV: 3686, minRicoLossV: 3686, chRicoLossV: 3686,
                projectileCount: 1, minChargedProjectileCount: 1, chargeProjectileCount: 1, smokeStart: 75, smokeMinimum: 25, smokeDrain: 5,
                smokeShotAmount: 0, smokeChargeAmount: 0);
        }
    }

    // The host's turret in a snapshot (Net/NetWorld.cs carries it after the crates + breakables): 13 bytes. The shot and
    // blow-up counters wrap (a change = it happened since the last snapshot: the client plays it); seats are
    // PlayerEntity.Players indices (255: none).
    public sealed class NetTurret
    {
        public const int Size = 13;
        public const byte NoSeat = 255;
        public byte State;        // EchoesTurretEntity.TurretState
        public bool Armed, Flash;
        public ushort Frames;     // 60 Hz frames into the state
        public short Yaw, Pitch;  // degrees x 100
        public byte Occupant = NoSeat, Held = NoSeat;
        public byte Shots, Destructions;
        public ushort Hp;

        public void Write(Span<byte> d)
        {
            d[0] = (byte)(State & 7 | (Armed ? 8 : 0) | (Flash ? 16 : 0));
            BitConverter.TryWriteBytes(d[1..], Frames);
            BitConverter.TryWriteBytes(d[3..], Yaw);
            BitConverter.TryWriteBytes(d[5..], Pitch);
            d[7] = Occupant;
            d[8] = Held;
            d[9] = Shots;
            d[10] = Destructions;
            BitConverter.TryWriteBytes(d[11..], Hp);
        }

        public static NetTurret Read(ReadOnlySpan<byte> s) => new NetTurret
        {
            State = (byte)(s[0] & 7),
            Armed = (s[0] & 8) != 0,
            Flash = (s[0] & 16) != 0,
            Frames = BitConverter.ToUInt16(s[1..]),
            Yaw = BitConverter.ToInt16(s[3..]),
            Pitch = BitConverter.ToInt16(s[5..]),
            Occupant = s[7],
            Held = s[8],
            Shots = s[9],
            Destructions = s[10],
            Hp = BitConverter.ToUInt16(s[11..])
        };
    }

    // hd/turret.json (EchoesTurret.ImportLook): the turret's, the hologram's and the slot ring's draw passes and poses
    public sealed class TurretLook
    {
        public int Samples { get; set; }
        public float UpTime { get; set; }
        public float DownTime { get; set; }
        public float ShootTime { get; set; }
        public float HoloTime { get; set; }
        public List<TurretPiece> Pieces { get; set; } = new();
        public int RingPass { get; set; } = -1;
        public int GlowPass { get; set; } = -1;   // the hologram's glow, placed (drawn with the hologram's light)
        // the models the effects' particles draw (PMDL): CMDL -> its placed pass (EchoesTurret.ImportFx)
        public Dictionary<uint, int> ModelPasses { get; set; } = new();
        public float[]? Ambient { get; set; }
        public List<float[]>? Lights { get; set; }
        public float[]? HoloAmbient { get; set; }
        public List<float[]>? HoloLights { get; set; }
        public float[]? RingAmbient { get; set; }
        public List<float[]>? RingLights { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TurretSound>? Sounds { get; set; }   // EchoesTurret.ImportSounds (null: none imported)
        // the effects' bones (no pass, Pass -1): their effect space (Echoes metres, Z up) to the world per pose
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TurretPiece>? Locators { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TurretFx>? Fx { get; set; }           // EchoesTurret.ImportFx (null: none imported)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TurretHud? Hud { get; set; }               // EchoesTurret.ImportHud (null: none imported)
        // the grates' generator (CScriptGenerator, NoReuseFollowers off in both arenas): GrateCount picks, each a random one
        // of its GrateSlots ACTV followers, repeats allowed (a follower that isn't a debris piece, Sidehopper's sound, makes
        // none). 0: one of each piece
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int GrateCount { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int GrateSlots { get; set; }
    }

    // the seat HUD's textures (EchoesTurret.FxDir/<id>.png): reticle quarter, frame half, energy tile
    public sealed class TurretHud
    {
        public uint Reticle { get; set; }
        public uint Frame { get; set; }
        public uint Bar { get; set; }
        public int TitleWidth { get; set; }   // EchoesTurret.HudTitleFile in font pixels (0: none)
        public int TitleHeight { get; set; }
    }

    // one of the turret's effects (EchoesTurret.FxDir/<Id>.<Kind>): placed (At: effect space to the world, 12 floats, MPH
    // row vectors) or on a bone of Locators (its pose now), started Time into its clip, emitting for Frames 60 Hz frames
    // (0: until stopped)
    public sealed class TurretFx
    {
        public string Role { get; set; } = "";
        public string Kind { get; set; } = "part";      // part / elsc
        public uint Id { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? At { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Bone { get; set; }
        public float Time { get; set; }
        public int Frames { get; set; }
        // its particles removed as its Frames end (the event's flags bit 0), not left to fade
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Wipe { get; set; }
        // Kind "board" (the shot's billboards, Role shot1 / shot2; Id a texture): colour (RGBA), size (low, high: a
        // random pick a frame), an animated texture's tiles (width, height, stride x, stride y in pixels, frames a cycle,
        // loops), additive
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Color { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Size { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[]? Tiles { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Additive { get; set; }
        // Kind "debris" (Role grate: a grate's break pieces; Id a CMDL placed in the look, TurretLook.ModelPasses): its
        // flight (EchoesTurret.Debris*) and the PART it leaves where its life ends (0: none)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Debris { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint Death { get; set; }
    }

    // one of the turret's sounds (EchoesTurret.SoundFile(Role) beside turret.json): Echoes' game sound id, the SOND's
    // volume (0-1), its range (MPH units: heard to there, fading linearly), a loop (the file is one turn of it), ambient
    // (not placed: heard at the listener)
    public sealed class TurretSound
    {
        public string Role { get; set; } = "";
        public uint Id { get; set; }
        public float Volume { get; set; } = 1;
        public float Range { get; set; }
        public bool Loop { get; set; }
        public bool Ambient { get; set; }
    }

    // one rigid part: its pass, and its model matrix (12 floats: rows 0-3, xyz) per pose of each clip
    public sealed class TurretPiece
    {
        public string Bone { get; set; } = "";
        public int Pass { get; set; }
        public int Triangles { get; set; }
        public bool Holo { get; set; }      // the hologram's (drawn while the turret is free)
        public bool Turns { get; set; }     // turns with the barrel's aim (the turretMain_SDK bone's)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Up { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Down { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Shoot { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Spin { get; set; }
    }

    // arena.json's "Turret": MPH units and axes (the importer's Map), degrees, seconds, MPH health
    public sealed class TurretSpot
    {
        public string Name { get; set; } = "";
        public float[] Base { get; set; } = new float[3];        // the turret actor (the telefrag's centre)
        public float[] Pivot { get; set; } = new float[3];       // the barrel's pivot, raised
        public float[] SeatOffset { get; set; } = new float[3];  // the seat (the player's feet) from the pivot
        public float CentreHeading { get; set; }                  // the barrel's centre way (atan2(x, z), degrees)
        public float YawLeft { get; set; }
        public float YawRight { get; set; }
        public float PitchUp { get; set; }
        public float PitchDown { get; set; }
        public float YawRate { get; set; }
        public float PitchRate { get; set; }
        public float FireRate { get; set; }
        public float Barrel { get; set; }                         // the pivot to the muzzle's end
        public int Damage { get; set; }
        public float SplashRadius { get; set; }
        public float Knockback { get; set; }                      // Echoes' knockback power (EchoesTurret.Knock)
        public float ShotSpeed { get; set; }                      // units a second
        public float ShotRange { get; set; }                       // the shot's sound range (Echoes: not a reach limit)
        public float ShotLife { get; set; }                       // seconds
        public int Health { get; set; }
        // the platform's collision about the pivot, centred and unturned: x y z per vertex, 3 vertex indices per triangle
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? ShapeVerts { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[]? ShapeTris { get; set; }
        // the pivot's height over the rise and the fall (evenly in time over RiseTime / LowerTime)
        public float RiseTime { get; set; }
        public float LowerTime { get; set; }
        public float[] RiseCurve { get; set; } = Array.Empty<float>();
        public float[] LowerCurve { get; set; } = Array.Empty<float>();
        public float[] EnterMin { get; set; } = new float[3];
        public float[] EnterMax { get; set; } = new float[3];
        public float[] SlotMin { get; set; } = new float[3];      // the bomb slot's hold box (BALT)
        public float[] SlotMax { get; set; } = new float[3];
        public float[] BombMin { get; set; } = new float[3];      // its trigger (DTRG 'Bombslot Right')
        public float[] BombMax { get; set; } = new float[3];
        public float[] KickMin { get; set; } = new float[3];      // the kick-out (TRGO 'Kick Out Ball')
        public float[] KickMax { get; set; } = new float[3];
        // the bomb slot's fixed cameras (MPH): position xyz and look xyz per camera (null: an older import)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? SlotCams { get; set; }
        // the blow-up's kill box (TRGR 'Player Kill', on DestroyedKillAt to DestroyedKillUntil; null: an older import)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? KillMin { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? KillMax { get; set; }
        // the solid column around the seat from Enter until Leave / 0.2 s into Destroyed (players only: shots pass), MPH
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? ColumnMin { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? ColumnMax { get; set; }
        // the lift's solid base ('Dummy Base' PLAT 0x10e: no box or DCLN, so Echoes' loader takes its model's bounds,
        // 0x8009f964-0x8009fa30): its box at the bottom, MPH; it rises BaseRise (MPH, +Y) over BaseTime from Enter, lowers
        // from Leave and snaps back down on Destroyed (RSET); solid to players and shots
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? BaseMin { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? BaseMax { get; set; }
        public float BaseRise { get; set; }
        public float BaseTime { get; set; }
        public float[] KickForce { get; set; } = { 0, 500, 0 };   // MPH axes, Echoes' magnitude
        public float SlotReset { get; set; }
    }

    public sealed class EchoesTurretEntity : EnemyInstanceEntity
    {
        public enum TurretState { Free, Entering, Active, Leaving, Destroyed }

        public const int ExplodeEffect = 77; // deathMech1 (Echoes' own explosion not imported)

        readonly TurretSpot _t;
        readonly (Vector3 Min, Vector3 Max)? _kill;
        readonly Vector3 _base, _pivotUp, _seatOffset, _enterMin, _enterMax, _slotMin, _slotMax, _slotCentre, _bombMin, _bombMax, _kickMin, _kickMax, _kickForce;
        readonly float _unit;
        readonly WeaponInfo _weapon;
        EquipInfo _equip = null!;
        float _time;          // seconds into the current state
        float _cooldown;      // seconds until the next shot
        int _hp;
        PlayerEntity? _lastHit;
        bool _armed, _shape;
        // the player let out with a push: Echoes sets the eye at its camera point, inside the shell, and lets them fly out
        // of it (Retro's moves don't stop a box already in a mesh); MPH's would catch them, so the shell lets them be
        // until they're past its reach
        PlayerEntity? _leaver;
        readonly HashSet<BombEntity> _bombs = new();
        readonly List<BombEntity> _gone = new();

        public TurretState State { get; private set; } = TurretState.Free;
        public PlayerEntity? Occupant { get; private set; }
        public PlayerEntity? Held { get; private set; }
        public int Hp => _hp;
        public int FullHp => _t.Health;
        public bool Armed => _armed;
        public int Shots { get; private set; }
        public int Entries { get; private set; }
        public int Destructions { get; private set; }
        // 60 Hz steps since the match started (the bot brain's clock)
        public int Frames { get; private set; }
        // the seat's enter box centre and the bomb slot's (the bot brain's errands)
        public Vector3 EnterCentre => (_enterMin + _enterMax) / 2;
        public Vector3 SlotCentre => _slotCentre;
        // the seated bot's pick (null: none in sight)
        public PlayerEntity? BotTarget => _botTarget;
        // the pivot now (the lift's height) and the barrel's turn and pitch (degrees from its centre)
        public Vector3 PivotNow { get; private set; }
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }
        public bool HologramShown => State == TurretState.Free;
        // for the look (TurretLook): seconds into the state, since the last shot and since the last hit, and the barrel's
        // turn from its centre (a rotation, row vectors, about PivotNow)
        public float StateTime => _time;
        public float SinceShot { get; private set; } = 99;
        public bool Flash => _sinceHit < 10 / 60f;
        public Matrix4 AimTurn { get; private set; } = Matrix4.Identity;
        // the turn drawn (PieceNow): this device's own occupant's aim as it is now (the turret takes it a step later, so
        // the shell and the barrel's effects round their camera shook as they turned), else AimTurn
        public Matrix4 DrawTurn => Occupant is { } o && o == PlayerEntity.Main && State is TurretState.Entering or TurretState.Active
            ? Turn(_t.CentreHeading, o.SeatYaw, o.SeatPitch) : AimTurn;
        float _sinceHit = 99;
        // one of the turret's own shots (its sounds, CampaignRenderer.TurretSfx.cs)
        public bool IsOwnShot(BeamProjectileEntity beam) => _equip != null && beam.Equip == _equip;

        public EchoesTurretEntity(int id, TurretSpot t, Scene scene)
            : base(new EnemyInstanceEntityData(EnemyType.HitZone, null!), NodeRef.None, scene)
        {
            Id = id;
            _t = t;
            static Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
            _base = V(t.Base);
            _pivotUp = V(t.Pivot);
            _seatOffset = V(t.SeatOffset);
            _enterMin = V(t.EnterMin); _enterMax = V(t.EnterMax);
            _slotMin = V(t.SlotMin); _slotMax = V(t.SlotMax);
            _slotCentre = (_slotMin + _slotMax) / 2;
            _bombMin = V(t.BombMin); _bombMax = V(t.BombMax);
            _kickMin = V(t.KickMin); _kickMax = V(t.KickMax);
            if (t.KillMin is { Length: 3 } && t.KillMax is { Length: 3 }) _kill = (V(t.KillMin), V(t.KillMax));
            _kickForce = V(t.KickForce);
            _unit = t.SplashRadius > 0 ? t.ShotRange / 51 : EchoesArena.DefaultScale;
            _weapon = EchoesTurret.ShotWeapon(t);
            _hp = t.Health;
            PivotNow = Lift(t.RiseCurve, 0);
            Position = _base;
        }

        protected override void EnemyInitialize()
        {
            NodeRef = _scene.GetNodeRefByName(ArenaModelWriter.RoomNode);
            Flags = EnemyFlags.NoMaxDistance | EnemyFlags.Static | EnemyFlags.Invincible;
            _hurtVolumeInit = _hurtVolume = new CollisionVolume(Position.AddY(-10000), 0);
            _equip = new EquipInfo(_weapon, _beams) { GetAmmo = () => 1000, SetAmmo = _ => { } };
            if (_t.ShapeVerts != null && _t.ShapeTris != null)
            {
                SetCollision(Shape(_t.ShapeVerts, _t.ShapeTris));
                EntityCollision[0]!.Collision.Active = false;
            }
            // the seat's column: the lift base's second slot (EchoesTurretBaseEntity), solid to shots too; an import
            // without the base: here, world space, players only (slot 1)
            if (_t.BaseMin == null && _t.ColumnMin is { Length: 3 } cmin && _t.ColumnMax is { Length: 3 } cmax)
            {
                SetCollision(BoxShape(cmin, cmax, beams: false), slot: 1);
                UpdateCollisionTransform(1, Matrix4.Identity);
                UpdateLinkedInverse(1);
                EntityCollision[1]!.Collision.Active = false;
            }
            PlaceShape();
        }

        // Echoes' 'Dumb Collision Box': solid from Enter until Leave, and 0.2 s into Destroyed
        public const float ColumnDestroyedOff = 0.2f;

        // the column's actor active (its model, CMDL 3801de98, draws nothing: the green column is the lift effect)
        public bool ColumnOn => State is TurretState.Entering or TurretState.Active || State == TurretState.Destroyed && _time < ColumnDestroyedOff;

        internal EchoesTurretBaseEntity? BaseEntity;

        void Column()
        {
            if (BaseEntity != null) BaseEntity.Column(ColumnOn);
            else if (EntityCollision[1]?.Collision is { } c) c.Active = ColumnOn;
        }

        // a box (world space) as entity collision: 12 triangles facing out
        internal static CollisionInstance BoxShape(float[] min, float[] max, bool beams)
        {
            var v = new float[24];
            for (int i = 0; i < 8; i++)
            {
                v[3 * i] = (i & 1) != 0 ? max[0] : min[0];
                v[3 * i + 1] = (i & 2) != 0 ? max[1] : min[1];
                v[3 * i + 2] = (i & 4) != 0 ? max[2] : min[2];
            }
            int[] tris = { 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6 };
            return Shape(v, tris, beams);
        }

        // the platform's mesh as MPH entity collision about the pivot
        static CollisionInstance Shape(float[] v, int[] tris, bool beams = true) => Pack(Editors(v, tris, beams, players: true));

        static CollisionInstance Pack(List<CollisionDataEditor> editors)
        {
            byte[] bytes = RepackCollision.PackMphCollision(editors);
            CollisionHeader header = Read.ReadStruct<CollisionHeader>(bytes);
            return new CollisionInstance("echoes_turret", Collision.ReadMphCollision(header, bytes, roomLayerMask: -1), isEntity: true);
        }

        static List<CollisionDataEditor> Editors(float[] v, int[] tris, bool beams, bool players)
        {
            var editors = new List<CollisionDataEditor>();
            Vector3 P(int i) => new Vector3(v[3 * i], v[3 * i + 1], v[3 * i + 2]);
            // Echoes' DCLN winds its triangles either way (half of the turret's face in) and Retro's ray tests take both
            // sides; MPH's collision takes the front only, so each faces out from the mesh's centre (shots went through
            // half the shell)
            Vector3 mid = Vector3.Zero;
            for (int i = 0; i < v.Length / 3; i++) mid += P(i);
            mid /= Math.Max(1, v.Length / 3);
            for (int k = 0; k + 2 < tris.Length; k += 3)
            {
                Vector3 a = P(tris[k]), b = P(tris[k + 1]), c = P(tris[k + 2]);
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared < 1e-10f) continue;
                if (Vector3.Dot(n, (a + b + c) / 3 - mid) < 0)
                {
                    (b, c) = (c, b);
                    n = -n;
                }
                n.Normalize();
                float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
                int axis = ay > ax && ay >= az ? 1 : az > ax && az > ay ? 2 : 0;
                var ed = new CollisionDataEditor { Plane = new Vector4(n, Vector3.Dot(n, a)), LayerMask = (ushort)(4 | axis), Terrain = Terrain.Metal, Beams = beams, Players = players };
                ed.Points.Add(a); ed.Points.Add(b); ed.Points.Add(c);
                editors.Add(ed);
            }
            return editors;
        }

        static (float[] V, int[] Tris) Box(float[] min, float[] max)
        {
            var v = new float[24];
            for (int i = 0; i < 8; i++)
            {
                v[3 * i] = (i & 1) != 0 ? max[0] : min[0];
                v[3 * i + 1] = (i & 2) != 0 ? max[1] : min[1];
                v[3 * i + 2] = (i & 4) != 0 ? max[2] : min[2];
            }
            int[] tris = { 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6 };
            return (v, tris);
        }

        // the seat's column: solid to players its whole height (it holds the occupant), to shots only below the raised
        // turret's underside (beamTop). Echoes' box (ColumnMin/Max) reaches past the pivot and nearly the ball's width, so
        // as one box it stood out through the ball's lower half and sides and shots there stopped on it, the turret
        // unhurt (owner 10-17: 'several zones where shooting the cannon doesn't hit it'); in Echoes the ball takes them
        internal static CollisionInstance ColumnShape(float[] min, float[] max, float beamTop)
        {
            (float[] v, int[] tris) = Box(min, max);
            List<CollisionDataEditor> eds = Editors(v, tris, beams: false, players: true);
            if (beamTop > min[1])
            {
                (float[] bv, int[] btris) = Box(min, new[] { max[0], Math.Min(max[1], beamTop), max[2] });
                eds.AddRange(Editors(bv, btris, beams: true, players: false));
            }
            return Pack(eds);
        }

        // the lowest point of the turret's shape at rest, raised (the column stops shots below it)
        public static float RestBottom(TurretSpot t)
        {
            if (t.ShapeVerts is not { Length: >= 3 } v) return float.MaxValue;
            Matrix4 m = Frame(Way(t.CentreHeading, 0, 0));
            float low = float.MaxValue;
            for (int i = 0; i + 2 < v.Length; i += 3) low = Math.Min(low, Vector3.TransformPosition(new Vector3(v[i], v[i + 1], v[i + 2]), m).Y);
            return t.Pivot[1] + low;
        }

        public override bool GetTargetable()
        {
            return false;
        }

        // the lift's pivot at a fraction of a rise or fall
        Vector3 Lift(float[] curve, float f)
        {
            if (curve.Length == 0) return _pivotUp;
            float x = Math.Clamp(f, 0, 1) * (curve.Length - 1);
            int i = Math.Min((int)x, curve.Length - 2);
            float y = curve.Length == 1 ? curve[0] : curve[i] + (curve[i + 1] - curve[i]) * (x - i);
            return new Vector3(_pivotUp.X, y, _pivotUp.Z);
        }

        // the barrel's way at its turn and pitch
        Vector3 Way(float yaw, float pitch) => Way(_t.CentreHeading, yaw, pitch);

        static Vector3 Way(float heading, float yaw, float pitch)
        {
            float h = MathHelper.DegreesToRadians(heading + yaw), p = MathHelper.DegreesToRadians(pitch);
            float cp = M.Cos(p);
            return new Vector3(cp * M.Sin(h), M.Sin(p), cp * M.Cos(h)).Normalized();
        }

        static Matrix4 Frame(Vector3 f)
        {
            Vector3 s = Vector3.Cross(f, Vector3.UnitY).Normalized();
            Vector3 u = Vector3.Cross(s, f);
            var m = Matrix4.Identity;
            m.Row0.Xyz = s;
            m.Row1.Xyz = u;
            m.Row2.Xyz = -f;
            return m;
        }

        // the barrel's turn from its centre way (heading, degrees) to yaw and pitch: a rotation, row vectors
        public static Matrix4 Turn(float heading, float yaw, float pitch)
        {
            Matrix4 centre = Frame(Way(heading, 0, 0));
            centre.Transpose();
            return centre * Frame(Way(heading, yaw, pitch));
        }

        void PlaceShape()
        {
            Matrix4 m = Frame(Way(Yaw, Pitch));
            AimTurn = Turn(_t.CentreHeading, Yaw, Pitch);
            m.Row3.Xyz = PivotNow;
            Transform = m;
            if (EntityCollision[0] != null)
            {
                UpdateLinkedInverse(0);
                UpdateCollisionTransform(0, m);
            }
        }

        void SetShape(bool on)
        {
            _shape = on;
            if (EntityCollision[0] != null) EntityCollision[0]!.Collision.Active = on && _leaver == null;
        }

        // the leaver clear of the shell (or the turret on to another state): its collision back
        void LeaverOut()
        {
            if (_leaver == null) return;
            float reach = EntityCollision[0]?.MaxDistance ?? 0;
            if (State == TurretState.Leaving && _leaver.Health > 0 && (_leaver.Position - PivotNow).Length < reach + Fixed.ToFloat(_leaver.Values.MaxPickupHeight)) return;
            _leaver = null;
            SetShape(_shape);
        }

        public override bool Process()
        {
            Column();
            if (EchoesTurret.Mirror)
            {
                MirrorProcess();
                return base.Process();
            }
            const float step = 1 / 60f;
            _time += step;
            Frames++;
            if (_cooldown > 0 && State == TurretState.Active) _cooldown -= step;   // Echoes counts it down only while active (0x26cc)
            if (SinceShot < 99) SinceShot += step;
            if (_sinceHit < 99) _sinceHit += step;
            LeaverOut();
            switch (State)
            {
            case TurretState.Free:
                PivotNow = Lift(_t.RiseCurve, 0);
                TryEnter();
                break;
            case TurretState.Entering:
                PivotNow = Lift(_t.RiseCurve, _time / _t.RiseTime);
                if (_time >= EchoesTurret.HealthResetAt && _time - step < EchoesTurret.HealthResetAt) _hp = _t.Health;
                if (Occupant != null && _time >= EchoesTurret.ControlAt)
                {
                    State = TurretState.Active;
                    _time = 0;
                    Occupant.HostSeatControl = true;
                    Occupant.HostSeatFire = false;
                    Occupant.HostSeatExit = false;
                }
                break;
            case TurretState.Active:
                PivotNow = _pivotUp;
                if (Occupant != null)
                {
                    // (a practice target, PlayerEntity.HostIdleBots: sits still, never aims, fires or gets out)
                    if (Occupant.IsBot && PlayerEntity.HostSeatBotBrain && !PlayerEntity.HostIdleBots) BotSeat(Occupant);
                    if (Occupant.HostSeatExit)
                    {
                        Leave(push: true);
                        break;
                    }
                    if (Occupant.HostSeatFire)
                    {
                        Occupant.HostSeatFire = false;
                        if (_cooldown <= 0) Fire();
                    }
                }
                break;
            case TurretState.Leaving:
                PivotNow = Lift(_t.LowerCurve, _time / _t.LowerTime);
                if (_armed && _time >= EchoesTurret.LeaveSlotOffAt) Disarm();
                if (_shape && _time >= EchoesTurret.LeaveOffAt) SetShape(false);
                if (_time >= EchoesTurret.LeaveBackAt) Free();
                break;
            case TurretState.Destroyed:
                // Echoes: the Destroyed sequence RSETs the lift and STOP sets the 'rest' clip with no transition (rso
                // 0x18ec-0x1934, SetAnimation noTrans 1; PAS state 5, 0x2368): down at once inside the explosion, not lowered
                PivotNow = Lift(_t.RiseCurve, 0);
                if (_armed && _time >= EchoesTurret.DestroyedSlotOffAt) Disarm();
                if (_kill is (Vector3 kmin, Vector3 kmax) && _time >= EchoesTurret.DestroyedKillAt && _time - step < EchoesTurret.DestroyedKillUntil)
                {
                    // the blow-up's 'Player Kill': anyone in it dies (a trigger's: nobody's kill)
                    foreach (PlayerEntity p in _scene.GetPlayerEntities())
                    {
                        if (p.Health == 0 || !p.LoadFlags.TestFlag(LoadFlags.Active) || !InBox(p.Volume.SpherePosition, kmin, kmax)) continue;
                        p.TakeDamage(p.Health + 1000, DamageFlags.Death | DamageFlags.IgnoreInvuln, null, null);
                    }
                }
                if (_time >= EchoesTurret.DestroyedBackAt) Free();
                break;
            }
            // the occupant gone (died, left the match) without the turret's doing: out, the lift lowers
            if (Occupant != null && (State == TurretState.Entering || State == TurretState.Active)
                && (Occupant.Health == 0 || !Occupant.LoadFlags.TestFlag(LoadFlags.Active) || !Occupant.HostSeated))
            {
                Leave(push: false);
            }
            if (Occupant != null)
            {
                Yaw = Occupant.SeatYaw;
                Pitch = Occupant.SeatPitch;
                Occupant.HostSeatPosition = PivotNow + _seatOffset;
            }
            else
            {
                Yaw = MoveTo(Yaw, 0, 4 * _t.YawRate * step);
                Pitch = MoveTo(Pitch, 0, 4 * _t.PitchRate * step);
            }
            PlaceShape();
            Slot();
            return base.Process();
        }

        static float MoveTo(float value, float target, float amount) =>
            MathF.Abs(target - value) <= amount ? target : value + (target > value ? amount : -amount);

        // ---- a seated bot (owner D7/D8, 2026-10-16: Echoes has no bots, so this is ours) ----
        // Every pick (reaction: 0.5 / 0.25 / 0.13 s by bot level) she takes the closest enemy the barrel can reach (inside
        // its turn and pitch limits and the shot's range) with nothing between the muzzle and them, aims where they will
        // be when the shot gets there (their speed x the flight time), off by a little (5 / 2.5 / 1 degrees by level, new
        // each pick), and fires when the barrel is within 6 / 4 / 2.5 degrees of that. With nobody to shoot for 5 s (or
        // after 30 s in all) she gets out.
        PlayerEntity? _botTarget;
        int _botLook, _botIdle, _botSeated;
        Vector2 _botError;
        public const int BotIdleOut = 5 * 60, BotStayMost = 30 * 60;

        void BotSeat(PlayerEntity bot)
        {
            int level = Math.Clamp(bot.BotLevel, 0, 2);
            if (_botSeated++ > BotStayMost)
            {
                bot.HostSeatExit = true;
                return;
            }
            if (--_botLook <= 0 || _botTarget != null && (_botTarget.Health == 0 || !_botTarget.LoadFlags.TestFlag(LoadFlags.Active)))
            {
                _botLook = level == 0 ? 30 : level == 1 ? 15 : 8;
                _botTarget = BotPick(bot);
                float err = level == 0 ? 5 : level == 1 ? 2.5f : 1;
                _botError = new Vector2(EchoesTurretBots.Signed(bot.SlotIndex, Frames) * err, EchoesTurretBots.Signed(bot.SlotIndex + 8, Frames) * err);
            }
            if (_botTarget == null || BotAim(_botTarget) is not Vector2 goal)
            {
                _botTarget = null;
                bot.HostSeatBotTarget = null;
                if (++_botIdle > BotIdleOut) bot.HostSeatExit = true;
                return;
            }
            _botIdle = 0;
            goal += _botError;
            goal.X = Math.Clamp(goal.X, -_t.YawRight, _t.YawLeft);
            goal.Y = Math.Clamp(goal.Y, -_t.PitchDown, _t.PitchUp);
            bot.HostSeatBotTarget = goal;
            float near = level == 0 ? 6 : level == 1 ? 4 : 2.5f;
            if (_cooldown <= 0 && MathF.Abs(goal.X - Yaw) < near && MathF.Abs(goal.Y - Pitch) < near) bot.HostSeatFire = true;
        }

        PlayerEntity? BotPick(PlayerEntity bot)
        {
            PlayerEntity? best = null;
            float bestD = Single.MaxValue;
            foreach (PlayerEntity p in _scene.GetPlayerEntities())
            {
                if (p == bot || p.Health == 0 || !p.LoadFlags.TestFlag(LoadFlags.Active)) continue;
                if (GameState.Teams && p.TeamIndex == bot.TeamIndex) continue;
                float d = (p.Volume.SpherePosition - PivotNow).LengthSquared;
                if (d >= bestD || BotAim(p) == null) continue;
                best = p;
                bestD = d;
            }
            return best;
        }

        // the barrel's turn and pitch (degrees) onto where p will be when a shot gets there; null: out of reach or sight
        Vector2? BotAim(PlayerEntity p)
        {
            Vector3 at = p.Volume.SpherePosition;
            float dist = (at - PivotNow).Length;
            if (dist > _t.ShotRange) return null;
            at += p.Speed * 30 * (Math.Max(0, dist - _t.Barrel) / Math.Max(1, _t.ShotSpeed));   // (Speed: per 30 Hz tick)
            Vector3 d = at - PivotNow;
            float flat = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
            if (flat < 0.01f) return null;
            float yaw = MathHelper.RadiansToDegrees(M.Atan2(d.X, d.Z)) - _t.CentreHeading;
            while (yaw > 180) yaw -= 360;
            while (yaw < -180) yaw += 360;
            float pitch = MathHelper.RadiansToDegrees(M.Atan2(d.Y, flat));
            if (yaw < -_t.YawRight || yaw > _t.YawLeft || pitch < -_t.PitchDown || pitch > _t.PitchUp) return null;
            Vector3 muzzle = PivotNow + d.Normalized() * _t.Barrel;
            if (Blocked(muzzle, p.Volume.SpherePosition, TestFlags.Beams)) return null;
            return new Vector2(yaw, pitch);
        }

        // the room's bot nav graph (the bot brain's routes)
        internal NodeData? NavNodes => _scene.Room?.NodeData;

        // something between a and b (the room, or another entity's collision)
        internal bool Blocked(Vector3 a, Vector3 b, TestFlags flags)
        {
            var res = new CollisionResult();
            return CollisionDetection.CheckBetweenPoints(a, b, flags, _scene, ref res) && res.EntityCollision?.Entity != this;
        }

        // dev (EchoesTurretBots.ForceSeat): p dropped on the enter box, TryEnter seats her next step
        internal void PutIn(PlayerEntity p) => p.Teleport(EnterCentre.WithY(_enterMax.Y), -Vector3.UnitZ, _scene.GetNodeRefContaining(EnterCentre));

        void TryEnter()
        {
            foreach (PlayerEntity p in _scene.GetPlayerEntities())
            {
                if (p.Health == 0 || !p.LoadFlags.TestFlag(LoadFlags.Active) || p.IsAltForm || p.IsMorphing || p.HostSeated || p.HostSlotHeld) continue;
                Vector3 c = p.Position;
                if (c.X < _enterMin.X || c.X > _enterMax.X || c.Z < _enterMin.Z || c.Z > _enterMax.Z || c.Y < _enterMin.Y - 0.5f || c.Y > _enterMax.Y) continue;
                Enter(p);
                return;
            }
        }

        void Enter(PlayerEntity p)
        {
            Occupant = p;
            State = TurretState.Entering;
            _time = 0;
            _lastHit = null;              // (the cooldown carries over: Echoes doesn't reset it on entry)
            _botTarget = null;
            _botLook = _botIdle = _botSeated = 0;
            Entries++;
            Yaw = Pitch = 0;
            PivotNow = Lift(_t.RiseCurve, 0);
            p.HostSeatCamAhead = EchoesTurret.SeatCamAhead * _unit;
            p.HostSeatOn(PivotNow + _seatOffset, _t.CentreHeading, _t.YawLeft, _t.YawRight, _t.PitchUp, _t.PitchDown, _t.YawRate, _t.PitchRate);
            p.HostSeatShell = this;
            _armed = true;
            SetShape(true);
            // the entry kill: everyone else close to the turret (credited to the occupant), then Echoes' multiplayer hit
            float kill = EchoesTurret.EntryKillRadius * _unit, hitR = EchoesTurret.EntryHitRadius * _unit, hitH = EchoesTurret.EntryHitHeight * _unit;
            foreach (PlayerEntity other in _scene.GetPlayerEntities())
            {
                if (other == p || other.Health == 0) continue;
                Vector3 d = other.Position - _base;
                float h2 = d.X * d.X + d.Z * d.Z;
                if (h2 <= kill * kill)
                {
                    PlayerEntity.HostTurretKillApplying = true; // its kill message: (HEAVY TURRET) (PlayerEchoesSeat.cs)
                    try { other.TakeDamage(other.Health + 1000, DamageFlags.Death | DamageFlags.IgnoreInvuln, null, p); }
                    finally { PlayerEntity.HostTurretKillApplying = false; }
                }
                else if (h2 <= hitR * hitR && MathF.Abs(d.Y) <= hitH)
                {
                    PlayerEntity.HostTurretKillApplying = true;
                    try { other.TakeDamage((int)EchoesTurret.EntryHitDamage, DamageFlags.IgnoreInvuln, null, p); }
                    finally { PlayerEntity.HostTurretKillApplying = false; }
                }
            }
        }

        void Fire()
        {
            if (Occupant == null) return;
            Vector3 way = Way(Yaw, Pitch);
            Vector3 muzzle = PivotNow + way * _t.Barrel;
            BeamProjectileEntity.Spawn(Occupant, _equip, muzzle, way, BeamSpawnFlags.None, NodeRef, _scene);
            _cooldown = _t.FireRate;
            SinceShot = 0;
            Shots++;
        }

        // out of the seat (Trilogy DOL CPlayer 0x8014cc24-0x8014cd74): Echoes puts the eye where the turret's camera was
        // (SeatCamAhead along the barrel from the seat) and sets the world velocity back along the barrel (flat) and up,
        // 30 m/s each (push); otherwise (the occupant died) just where the seat is
        void Leave(bool push)
        {
            PlayerEntity? p = Occupant;
            Occupant = null;
            State = TurretState.Leaving;
            _time = 0;
            Column();   // the seat's column off before the player's next move (it held the push back at once)
            if (p != null) SeatOff(p, push);
        }

        void SeatOff(PlayerEntity p, bool push)
        {
            Vector3 seat = PivotNow + _seatOffset;
            Vector3 speed = Vector3.Zero;
            if (push)
            {
                Vector3 way = Way(Yaw, Pitch);
                seat += way * (EchoesTurret.SeatCamAhead * _unit);
                seat.Y -= Fixed.ToFloat(p.Values.AimYOffset);
                Vector3 back = new Vector3(-way.X, 0, -way.Z);
                float v = EchoesTurret.ExitSpeed * _unit / 30;   // m/s -> units per 30 Hz tick
                speed = (back * v).WithY(v);
                _leaver = p;
                SetShape(_shape);
            }
            p.HostSeatOff(seat, speed);
        }

        void Free()
        {
            State = TurretState.Free;
            _time = 0;
            _hp = _t.Health;
            SetShape(false);
        }

        // recomp: the app draws Echoes' own blow-up (the arena import's turret_fx, CampaignRenderer.TurretFx.cs): MPH's
        // stand-in still runs but isn't drawn
        public static bool HostOwnExplode { get; set; }
        // the enemies' shot pool the turret fires from (its shots: BeamProjectileEntity.HostWeaponLook when the app draws them)
        public static IReadOnlyList<BeamProjectileEntity>? ShotPool => _beams;

        void Explode()
        {
            _scene.HostSpawnHidden = HostOwnExplode;
            try
            {
                _scene.SpawnEffect(ExplodeEffect, GetTransformMatrix(Vector3.UnitZ, Vector3.UnitY, PivotNow));
            }
            finally
            {
                _scene.HostSpawnHidden = false;
            }
        }

        void Destroy(PlayerEntity? by)
        {
            if (State is not (TurretState.Entering or TurretState.Active)) return;
            PlayerEntity? p = Occupant;
            Occupant = null;
            State = TurretState.Destroyed;
            _time = 0;
            _hp = 0;
            Yaw = Pitch = 0;          // Echoes resets the turret's transform and both pitch clips (rso 0x1888-0x18cc), then snaps it to rest
            Destructions++;
            SetShape(false);
            Explode();
            if (p != null)
            {
                SeatOff(p, push: false);
                // Echoes credits the last damager; a teammate's (no friendly fire) or none is the arena's kill
                bool credit = by != null && by != p && !(GameState.Teams && !GameState.FriendlyFire && by.TeamIndex == p.TeamIndex);
                p.TakeDamage(p.Health + 1000, DamageFlags.Death | DamageFlags.IgnoreInvuln, null, credit ? by : null);
            }
        }

        public override void HandleMessage(MessageInfo info)
        {
            if (EchoesTurret.Mirror) return;
            if (info.Message != Message.BeamCollideWith || !_shape || State is not (TurretState.Entering or TurretState.Active)) return;
            if (info.Sender is not BeamProjectileEntity beam || beam.Owner?.Type == EntityType.EnemyInstance) return;
            // its own shots pass the turret (Echoes: the shot ignores its platform)
            if (beam.Equip == _equip) return;
            Hit(EchoesBreakables.Damage(beam.Beam, beam.Damage, beam.Flags.TestFlag(BeamFlags.Charged)), beam.Owner as PlayerEntity);
        }

        // damage (EchoesBreakables' points) to the turret while it's up, by a player or not
        public void Hit(int damage, PlayerEntity? by)
        {
            if (EchoesTurret.Mirror || damage <= 0 || !_shape || State is not (TurretState.Entering or TurretState.Active)) return;
            if (by != null) _lastHit = by;
            _sinceHit = 0;
            _hp -= damage;
            if (_hp <= 0) Destroy(_lastHit);
        }

        // ---- the bomb slot ----

        void Disarm()
        {
            _armed = false;
            if (Held != null)
            {
                Held.HostSlotHold = null;
                Held = null;
            }
        }

        static bool InBox(Vector3 p, Vector3 min, Vector3 max) => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;

        // a ball just taken: of the slot's cameras, the one whose look at the slot is nearest the player's own (Echoes
        // picks among equal-priority hints by the camera's direction to the ball, once, as they're added)
        void SlotCamera(PlayerEntity p)
        {
            p.HostSlotCamera = null;
            if (_t.SlotCams is not { Length: >= 6 } c) return;
            Vector3 now = p.CameraInfo.Target - p.CameraInfo.Position;
            now = now.LengthSquared > 1e-6f ? now.Normalized() : Vector3.UnitZ;
            float best = Single.MinValue;
            for (int i = 0; i + 6 <= c.Length; i += 6)
            {
                var pos = new Vector3(c[i], c[i + 1], c[i + 2]);
                Vector3 toBall = _slotCentre - pos;
                float dot = toBall.LengthSquared > 1e-6f ? Vector3.Dot(toBall.Normalized(), now) : -1;
                if (dot <= best) continue;
                best = dot;
                p.HostSlotCamera = (pos, new Vector3(c[i + 3], c[i + 4], c[i + 5]), EchoesTurret.SlotCamIn, EchoesTurret.SlotCamOut);
            }
        }

        void Slot()
        {
            // a held player gone (dead, unmorphed by the game itself, left): let go
            if (Held != null && (Held.Health == 0 || !Held.IsAltForm || !Held.LoadFlags.TestFlag(LoadFlags.Active)))
            {
                Held.HostSlotHold = null;
                Held = null;
            }
            if (_armed)
            {
                if (Held == null)
                {
                    foreach (PlayerEntity p in _scene.GetPlayerEntities())
                    {
                        if (p.Health == 0 || !p.IsAltForm || p.HostSeated || p.HostSlotHeld || !p.LoadFlags.TestFlag(LoadFlags.Active)) continue;
                        if (!InBox(p.Volume.SpherePosition, _slotMin, _slotMax)) continue;
                        Held = p;
                        p.HostSlotHold = _slotCentre;
                        SlotCamera(p);
                        break;
                    }
                }
                // a bomb's blast reaching the slot's trigger while a ball sits there (DTRG: bombs only)
                foreach (BombEntity bomb in _scene.GetBombEntities())
                {
                    if (!bomb.Flags.TestFlag(BombFlags.Exploded) || _bombs.Contains(bomb)) continue;
                    _bombs.Add(bomb);
                    if (Held == null) continue;
                    Vector3 near = Vector3.Clamp(bomb.Position, _bombMin, _bombMax);
                    if ((near - bomb.Position).LengthSquared <= bomb.Radius * bomb.Radius)
                    {
                        Destroy(bomb.Owner);
                        break;
                    }
                }
            }
            else
            {
                foreach (PlayerEntity p in _scene.GetPlayerEntities()) Kick(p);
            }
            if (_bombs.Count > 0)
            {
                _gone.Clear();
                foreach (BombEntity b in _bombs) if (!b.Flags.TestFlag(BombFlags.Exploded)) _gone.Add(b);
                foreach (BombEntity b in _gone) _bombs.Remove(b);
            }
        }

        // the kick-out: Echoes' impulse (momentum) each step on a player in its box, by how much of them is in it, as a
        // speed change in MPH units per tick
        void Kick(PlayerEntity p)
        {
            if (p.Health == 0 || p.HostSeated) return;
            Vector3 c = p.Volume.SpherePosition;
            float r = p.Volume.SphereRadius;
            Vector3 lo = Vector3.ComponentMax(c - new Vector3(r), _kickMin), hi = Vector3.ComponentMin(c + new Vector3(r), _kickMax);
            if (lo.X >= hi.X || lo.Y >= hi.Y || lo.Z >= hi.Z) return;
            float overlap = (hi.X - lo.X) * (hi.Y - lo.Y) * (hi.Z - lo.Z) / (8 * r * r * r);
            Vector3 dv = _kickForce / EchoesTurret.PlayerMass * _unit / 30 * overlap;   // m/s -> units per tick
            p.Speed += dv;
        }

        // ---- network play (T5) ----

        int _netShots = -1, _netDestructions;   // the last snapshot's counters (-1: none yet)
        float _netYaw, _netPitch;

        // host: the turret for the snapshot
        public NetTurret NetCapture() => new NetTurret
        {
            State = (byte)State,
            Armed = _armed,
            Flash = Flash,
            Frames = (ushort)Math.Clamp((int)MathF.Round(_time * 60), 0, ushort.MaxValue),
            Yaw = (short)MathF.Round(Yaw * 100),
            Pitch = (short)MathF.Round(Pitch * 100),
            Occupant = Occupant != null ? (byte)Occupant.SlotIndex : NetTurret.NoSeat,
            Held = Held != null ? (byte)Held.SlotIndex : NetTurret.NoSeat,
            Shots = (byte)Shots,
            Destructions = (byte)Destructions,
            Hp = (ushort)Math.Max(_hp, 0)
        };

        static PlayerEntity? NetSeat(byte seat) => seat < PlayerEntity.MaxPlayers ? PlayerEntity.Players[seat] : null;

        // the pivot at a time into a state
        Vector3 PivotAt(TurretState state, float time) => state switch
        {
            TurretState.Entering => Lift(_t.RiseCurve, time / _t.RiseTime),
            TurretState.Active => _pivotUp,
            TurretState.Leaving => Lift(_t.LowerCurve, time / _t.LowerTime),
            _ => Lift(_t.RiseCurve, 0)
        };

        // client: the host's turret. Its occupant is seated here and let out as the host does (the push included), the
        // held ball held; a new shot is fired here (a look: a client's beams do no damage) and a blow-up plays.
        public void NetApply(NetTurret n)
        {
            bool first = _netShots < 0;
            State = (TurretState)Math.Min((int)n.State, (int)TurretState.Destroyed);
            _time = n.Frames / 60f;
            _netYaw = n.Yaw / 100f;
            _netPitch = n.Pitch / 100f;
            _hp = n.Hp;
            _armed = n.Armed;
            if (n.Flash) _sinceHit = 0;
            PivotNow = PivotAt(State, _time);
            PlayerEntity? occupant = NetSeat(n.Occupant);
            if (occupant != Occupant)
            {
                // out: pushed as the host pushes a player who gets out (not one who died or was blown up)
                if (Occupant != null) SeatOff(Occupant, push: State == TurretState.Leaving && Occupant.Health > 0);
                Occupant = occupant;
            }
            if (Occupant != null && !Occupant.HostSeated && Occupant.Health > 0)
            {
                Yaw = Pitch = 0;
                Occupant.HostSeatCamAhead = EchoesTurret.SeatCamAhead * _unit;
                Occupant.HostSeatOn(PivotNow + _seatOffset, _t.CentreHeading, _t.YawLeft, _t.YawRight, _t.PitchUp, _t.PitchDown, _t.YawRate, _t.PitchRate);
                Occupant.HostSeatShell = this;
            }
            PlayerEntity? held = NetSeat(n.Held);
            if (held != Held)
            {
                if (Held != null) Held.HostSlotHold = null;
                Held = held;
                if (held != null) SlotCamera(held);
            }
            if (Held != null) Held.HostSlotHold = _slotCentre;
            if (!first && n.Destructions != _netDestructions)
            {
                Destructions++;
                SinceShot = 99;
                Explode();
            }
            if (!first && n.Shots != _netShots && Occupant != null)
            {
                bool own = Occupant == PlayerEntity.Main;
                Yaw = own ? Occupant.SeatYaw : _netYaw;
                Pitch = own ? Occupant.SeatPitch : _netPitch;
                Fire();
            }
            _netShots = n.Shots;
            _netDestructions = n.Destructions;
        }

        // client, each step: the host's turret moved on by this device's clock until the next snapshot; the local
        // occupant's own aim turns the barrel at once (the host has it a round trip later), anyone else's is the host's
        void MirrorProcess()
        {
            const float step = 1 / 60f;
            _time += step;
            if (SinceShot < 99) SinceShot += step;
            if (_sinceHit < 99) _sinceHit += step;
            PivotNow = PivotAt(State, _time);
            bool shape = State is TurretState.Entering or TurretState.Active || State == TurretState.Leaving && _time < EchoesTurret.LeaveOffAt;
            if (shape != _shape) SetShape(shape);
            LeaverOut();
            if (Occupant != null && Occupant.HostSeated)
            {
                Occupant.HostSeatControl = State == TurretState.Active;
                Occupant.HostSeatFire = false;   // (the presses reach the host as buttons)
                Occupant.HostSeatExit = false;
                Occupant.HostSeatPosition = PivotNow + _seatOffset;
            }
            bool own = Occupant != null && Occupant == PlayerEntity.Main && Occupant.HostSeated;
            Yaw = own ? Occupant!.SeatYaw : _netYaw;
            Pitch = own ? Occupant!.SeatPitch : _netPitch;
            PlaceShape();
            if (Held != null) Held.HostSlotHold = _slotCentre;
            // the kick-out on this device's own player (its movement is predicted here; the others' poses are the host's)
            if (!_armed) Kick(PlayerEntity.Main);
        }

        // MphRead draws nothing for it; the device draws its Echoes model (T4)
        protected override bool EnemyGetDrawInfo()
        {
            return true;
        }
    }

    // the turret's bot brain on foot (owner D7 'Sometimes' man a free turret, D8 'Shoot + bomb slot', 2026-10-16; Echoes
    // has no bots, so this is ours). Host only, through PlayerEntity.HostBotErrand (her own AI step, then this):
    //   - the seat: a bot within 30 units of a free turret gets in 30% of the time (else she leaves it alone for 10 s): she
    //     goes there along the arena's nav graph (its next-hop table, jumping its jump links), walks into the enter box
    //     (out of the ball first), and gives up after 20 s. One bot at a time; after a try she leaves it 20 s
    //   - the slot: with an enemy in the turret, a bot within 30 units of the bomb slot heads for it 50% of the time (else
    //     8 s), rolls in once she is close and lays bombs once it holds her, giving up after 20 s; after a try she leaves
    //     it 15 s. Shooting the turret is her own AI's (the occupant is a player in sight)
    // Each bot's clock and dice go by the turret's own step count and her slot (no RNG drawn: the rest of the match plays
    // as before), so replays play the same.
    public static class EchoesTurretBots
    {
        // replays recorded before it: off (MatchStart, Settings.EchoesTurretBots)
        public static bool On { get; set; } = true;
        // dev only (--es turrettest 1, MatchSession): a bot is put in the turret 3 s after it's free, so it shoots you from it
        // and its entry kills whoever stands by the turret then (owner 2026-10-17: "can you set up a test environment for
        // c41", the HEAVY TURRET text). Walking there from across the arena took minutes (-echoesturret botprobe force=1)
        public static bool ForceSeat { get; set; }
        static int _forceAt;
        internal static EchoesTurretEntity? Live;
        // since the match started: seat dice rolled, seat errands, slot dice, slot errands, hops taken, jump-asking steps
        // (Tools botprobe prints them)
        public static readonly int[] Counts = new int[6];

        enum Job { None, Seat, Slot }
        const int Slots = 16;
        const float Reach = 30, Close = 4, Level = 1.5f, HopReached = 1.2f, JumpFrom = 2.5f;
        static readonly Job[] _job = new Job[Slots];
        static readonly int[] _until = new int[Slots], _rest = new int[Slots];
        static readonly bool[] _rolling = new bool[Slots];
        static readonly NodeData3?[] _hop = new NodeData3?[Slots], _prev = new NodeData3?[Slots];
        static IReadOnlyList<NodeData3>? _nodes;
        static NodeData3? _seatNode, _slotNode;
        static bool _graph;

        internal static void Start(EchoesTurretEntity turret)
        {
            Live = turret;
            Array.Clear(_job);
            Array.Clear(_until);
            Array.Clear(_rest);
            Array.Clear(_rolling);
            Array.Clear(_hop);
            Array.Clear(_prev);
            Array.Clear(Counts);
            _forceAt = 0;
            _nodes = null;
            _seatNode = _slotNode = null;
            _graph = false;
        }

        // a die of 100 for this bot at this step (0..99), and one of -1..1
        internal static int Roll(int slot, int frame) => (int)(Hash(slot, frame) % 100);
        internal static float Signed(int slot, int frame) => (Hash(slot, frame) % 2001) / 1000f - 1;

        static uint Hash(int slot, int frame)
        {
            uint h = (uint)frame * 2654435761u ^ (uint)(slot + 1) * 2246822519u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return h;
        }

        public static PlayerEntity.BotErrand? Errand(PlayerEntity p)
        {
            EchoesTurretEntity? t = Live;
            int s = p.SlotIndex;
            if (t == null || EchoesTurret.Mirror || !p.IsBot || s < 0 || s >= Slots) return null;
            Graph(t);
            int now = t.Frames;
            if (p.Health == 0 || p.HostSeated)
            {
                if (_job[s] != Job.None) End(s, now, 20);
                return null;
            }
            switch (_job[s])
            {
            case Job.Seat:
                if (t.State != EchoesTurretEntity.TurretState.Free || now > _until[s]) break;
                return Go(t, p, s, t.EnterCentre, _seatNode, slot: false);
            case Job.Slot:
                if (t.State != EchoesTurretEntity.TurretState.Active || !Enemy(t.Occupant, p) || now > _until[s]) break;
                return Go(t, p, s, t.SlotCentre, _slotNode, slot: true);
            default:
                if (ForceSeat && t.State == EchoesTurretEntity.TurretState.Free)
                {
                    if (_forceAt == 0) _forceAt = now + 3 * 60;
                    else if (now >= _forceAt)
                    {
                        _forceAt = 0;
                        t.PutIn(p);
                        return null;
                    }
                }
                else if (ForceSeat) _forceAt = 0;
                if (now < _rest[s]) return null;
                if (t.State == EchoesTurretEntity.TurretState.Free && !Taken(Job.Seat) && Within(p, t.EnterCentre))
                {
                    return Try(s, now, Job.Seat, 30, 20, 10) ? Errand(p) : null;
                }
                if (t.State == EchoesTurretEntity.TurretState.Active && Enemy(t.Occupant, p) && !Taken(Job.Slot) && Within(p, t.SlotCentre))
                {
                    return Try(s, now, Job.Slot, 50, 20, 8) ? Errand(p) : null;
                }
                return null;
            }
            End(s, now, _job[s] == Job.Seat ? 20 : 15);
            return null;
        }

        // toward goal: straight in once close, level and in sight; before that along the nav graph, a hop at a time. A
        // slot run that has rolled stays in the ball while within twice that (the ball's centre sits lower than her
        // feet and the morph moves her: the close test alone flipped every few steps, morphing her in and out)
        static PlayerEntity.BotErrand Go(EchoesTurretEntity t, PlayerEntity p, int s, Vector3 goal, NodeData3? goalNode, bool slot)
        {
            Vector3 d = goal - p.Position;
            float flat = d.X * d.X + d.Z * d.Z;
            bool close = flat < Close * Close && MathF.Abs(d.Y) < Level
                && !t.Blocked(p.Position.AddY(0.5f), goal.WithY(p.Position.Y + 0.5f), TestFlags.Players);
            _rolling[s] = slot && (close || _rolling[s] && flat < 4 * Close * Close && MathF.Abs(d.Y) < 2 * Level);
            if (close || _rolling[s] || _nodes == null || goalNode == null)
            {
                return new PlayerEntity.BotErrand(goal.WithY(p.Position.Y), ball: slot, bomb: slot && p.HostSlotHeld, jump: 0);
            }
            NodeData3 hop = _hop[s] ?? NearestSeen(t, p.Position);
            if (Reached(p, hop))
            {
                _prev[s] = hop;
                if (hop != goalNode)
                {
                    hop = NextHop(hop, goalNode);
                    Counts[4]++;
                }
            }
            _hop[s] = hop;
            // a jump link (or a node well above her) from her last node: jump as she reaches its foot
            int kind = 0;
            if (_prev[s] is NodeData3 from && PlayerEntity.HostNavLinks?.TryGetValue((from.Id, hop.Id), out int k) == true) kind = k;
            Vector3 h = hop.Position - p.Position;
            int jump = 0;
            if (h.X * h.X + h.Z * h.Z < JumpFrom * JumpFrom && (kind is 1 or 2 or 3 || h.Y > 1))
            {
                jump = kind == 1 && h.Y < 1.5f ? 1 : 2;
                Counts[5]++;
            }
            return new PlayerEntity.BotErrand(hop.Position, ball: false, bomb: false, jump);
        }

        static bool Reached(PlayerEntity p, NodeData3 n)
        {
            Vector3 d = n.Position - p.Position;
            return d.X * d.X + d.Z * d.Z < HopReached * HopReached && MathF.Abs(d.Y) < 2;
        }

        // MphRead's next hop (PlayerAi.Func213A1A8): the node's run-length table over destination ids
        static NodeData3 NextHop(NodeData3 from, NodeData3 dest)
        {
            int index = 0;
            int total = 0;
            if (from.Values.Count <= from.Index1 + 1) return dest;
            if (from.Values[from.Index1] <= dest.Id)
            {
                int next;
                do
                {
                    total += from.Values[from.Index1 + index];
                    index += 2;
                    if (from.Index1 + index + 1 >= from.Values.Count) return dest;
                    next = total + from.Values[from.Index1 + index];
                }
                while (next <= dest.Id);
            }
            int i = from.Values[from.Index1 + index + 1];
            return i < _nodes!.Count ? _nodes[i] : dest;
        }

        // the closest node with nothing between (half a unit up at both ends), else the closest
        static NodeData3 NearestSeen(EchoesTurretEntity t, Vector3 at)
        {
            NodeData3? best = null, seen = null;
            float bestD = Single.MaxValue, seenD = Single.MaxValue;
            foreach (NodeData3 n in _nodes!)
            {
                float d = (n.Position - at).LengthSquared;
                if (d < bestD)
                {
                    best = n;
                    bestD = d;
                }
                if (d < seenD && d < 15 * 15 && !t.Blocked(at.AddY(0.5f), n.Position.AddY(0.5f), TestFlags.Players))
                {
                    seen = n;
                    seenD = d;
                }
            }
            return seen ?? best!;
        }

        // the room's nav graph (an Echoes import's: one set, id = index), first asked
        static void Graph(EchoesTurretEntity t)
        {
            if (_graph) return;
            _graph = true;
            if (t.NavNodes is not { } data || data.Data.Count == 0 || data.Data[0].Count == 0) return;
            _nodes = data.Data[0][0];
            NodeData3? Nearest(Vector3 at) => _nodes.OrderBy(n => (n.Position - at).LengthSquared).ThenBy(n => n.Id).FirstOrDefault();
            _seatNode = Nearest(t.EnterCentre);
            _slotNode = Nearest(t.SlotCentre);
        }

        static bool Try(int s, int now, Job job, int chance, int seconds, int restSeconds)
        {
            int k = job == Job.Seat ? 0 : 2;
            Counts[k]++;
            if (Roll(s, now) < chance)
            {
                Counts[k + 1]++;
                _job[s] = job;
                _until[s] = now + seconds * 60;
                _hop[s] = _prev[s] = null;
                return true;
            }
            _rest[s] = now + restSeconds * 60;
            return false;
        }

        static void End(int s, int now, int restSeconds)
        {
            _job[s] = Job.None;
            _rolling[s] = false;
            _rest[s] = now + restSeconds * 60;
            _hop[s] = _prev[s] = null;
        }

        static bool Taken(Job job) => Array.IndexOf(_job, job) >= 0;

        static bool Enemy(PlayerEntity? occupant, PlayerEntity p) =>
            occupant != null && occupant != p && !(GameState.Teams && occupant.TeamIndex == p.TeamIndex);

        static bool Within(PlayerEntity p, Vector3 to) => (to - p.Position).LengthSquared < Reach * Reach;
    }

    // The lift's solid base (Echoes' 'Dummy Base' PLAT, TurretSpot.BaseMin/BaseMax): a box under the turret that rides up
    // BaseRise over BaseTime from the turret's Enter (Raise Turret starts it to the top waypoint), back down from Leave
    // (Lower Turret), snapped to the bottom when the turret is destroyed (RSET); it carries whoever stands on it, as
    // MphRead's platforms do (the old and new transforms). Its own entity: an entity has two collision slots and the
    // turret's are its hit shape and the seat's column. Its position follows the turret's state, so a client's mirror
    // turret moves it too.
    public sealed class EchoesTurretBaseEntity : EnemyInstanceEntity
    {
        readonly TurretSpot _t;
        readonly EchoesTurretEntity _turret;
        float _f;   // 0 bottom .. 1 top

        public float Height => _f * _t.BaseRise;

        public EchoesTurretBaseEntity(int id, TurretSpot t, EchoesTurretEntity turret, Scene scene)
            : base(new EnemyInstanceEntityData(EnemyType.HitZone, null!), NodeRef.None, scene)
        {
            Id = id;
            _t = t;
            _turret = turret;
            turret.BaseEntity = this;
            Position = new Vector3((t.BaseMin![0] + t.BaseMax![0]) / 2, t.BaseMin[1], (t.BaseMin[2] + t.BaseMax[2]) / 2);
        }

        // the seat's column (Echoes' 'Dumb Collision Box', TurretSpot.ColumnMin/ColumnMax, world space) in the second slot:
        // solid to players and shots (its Vulnerability is all zero but Screw Attack: a shot stops on it and does nothing,
        // the turret's own shape takes damage only where it's hit itself; owner 10-16), to shots only below the turret's
        // underside (ColumnShape). On with EchoesTurretEntity.ColumnOn
        internal void Column(bool on)
        {
            if (EntityCollision[1]?.Collision is { } c) c.Active = on;
        }

        protected override void EnemyInitialize()
        {
            NodeRef = _scene.GetNodeRefByName(ArenaModelWriter.RoomNode);
            Flags = EnemyFlags.NoMaxDistance | EnemyFlags.Static | EnemyFlags.Invincible;
            _hurtVolumeInit = _hurtVolume = new CollisionVolume(Position.AddY(-10000), 0);
            SetCollision(EchoesTurretEntity.BoxShape(_t.BaseMin!, _t.BaseMax!, beams: true));
            UpdateCollisionTransform(0, Matrix4.Identity);
            UpdateLinkedInverse(0);
            if (_t.ColumnMin is { Length: 3 } cmin && _t.ColumnMax is { Length: 3 } cmax)
            {
                SetCollision(EchoesTurretEntity.ColumnShape(cmin, cmax, EchoesTurretEntity.RestBottom(_t)), slot: 1);
                UpdateCollisionTransform(1, Matrix4.Identity);
                UpdateLinkedInverse(1);
                Column(_turret.ColumnOn);
            }
        }

        public override bool GetTargetable()
        {
            return false;
        }

        public override bool Process()
        {
            const float step = 1 / 60f;
            float rate = _t.BaseTime > 0 ? step / _t.BaseTime : 1;
            _f = _turret.State switch
            {
                EchoesTurretEntity.TurretState.Destroyed => 0,
                EchoesTurretEntity.TurretState.Entering or EchoesTurretEntity.TurretState.Active => Math.Min(1, _f + rate),
                _ => Math.Max(0, _f - rate),
            };
            UpdateLinkedInverse(0);
            UpdateCollisionTransform(0, Matrix4.CreateTranslation(0, Height, 0));
            return base.Process();
        }

        protected override bool EnemyGetDrawInfo()
        {
            return true;
        }
    }
}
