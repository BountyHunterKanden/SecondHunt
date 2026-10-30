using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using MphRead;
using MphRead.Editor;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Import.Retro;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Echoes' Kinetic Orb Cannons -> MPH jump pads (docs/MP2_MULTIPLAYER_IMPORT.md section 3; owner: "jump pads for
    // cannons"). Each cannon is a ball trigger plus a SpecialFunction LaunchPlayer (0x21, ValueParm = launch speed) that
    // activates a "Null" marker in the air. The launch itself is game code, so the landing is read from the arena:
    //   1. a walkable ledge near the marker (within 30 m across, 15 m under, 10 m over): the cannon lifts you onto it
    //      (Sidehopper's walkways, Shooting Gallery's balconies, the top of Spider Complex's spider-ball sphere);
    //   2. else the marker is over a gap: fly the ball at the marker at the launch speed under Echoes' ball gravity
    //      (TweakBall 76 m/s2) and take where it comes down, or the floor it passes closest over (Spires' islands,
    //      Sidehopper's pods).
    // The MPH pad's launch is then solved against MphRead's own player physics (Tune): arcs are checked against the
    // collision first (a high arc, or a shot at the marker like Echoes'), then a hunter is put on the pad, the pad fires,
    // and speed and direction are corrected until the landing is on the target. Where no arc can leave the cannon's spot
    // (a low ceiling over it), the pad moves out toward the landing, up to 12 units. A cannon with no working launch
    // is dropped (noted) rather than shipped broken.
    public static class ArenaCannons
    {
        public sealed class Cannon
        {
            public string Name { get; set; } = "";
            public string Rule { get; set; } = "";
            public float[] Slot { get; set; } = new float[3];     // the floor under Echoes' ball slot
            public float[] Pad { get; set; } = new float[3];      // where the MPH pad sits (the slot, or moved out)
            public float[] Marker { get; set; } = new float[3];
            public float[] Target { get; set; } = new float[3];
            public float[] Launch { get; set; } = new float[3];   // world velocity the pad gives (BeamVector * Speed), per frame
            public string Plan { get; set; } = "";
            public ushort LockFrames { get; set; }               // ControlLockTime (30 fps frames)
            public float[] Landed { get; set; } = new float[3];   // biped landing in the tuning run
            public float Miss { get; set; } = -1;               // biped landing distance from the target
            public float AltMiss { get; set; } = -1;            // alt-form landing distance across from the target (-1: not run)
            public float[] AltLanded { get; set; } = new float[3];
            public bool AltOk { get; set; } = true;
            public bool Dropped { get; set; }
            public short EntityId { get; set; }
            // targets Tune falls back to, in order, when no launch lands on Target (R7b: the far side can be out of reach)
            [JsonIgnore] public List<float[]> Fallbacks { get; set; } = new();

            [JsonIgnore] public Vector3 PadV { get => V(Pad); set => Pad = F(value); }
            [JsonIgnore] public Vector3 SlotV => V(Slot);
            [JsonIgnore] public Vector3 MarkerV => V(Marker);
            [JsonIgnore] public Vector3 TargetV => V(Target);
            [JsonIgnore] public Vector3 LaunchV => V(Launch);
            [JsonIgnore] public Vector3 LandedV => V(Landed);
        }

        const uint SpecialFunctionId = 0xB8AFCF21, SpecialFunctionChoiceId = 0x95F8D644, ValueParmId = 0x19028099;
        const int LaunchPlayer = 0x21;
        const float BallGravity = 76;            // TweakBall BallGravity (Standard.ntwk), m/s2
        public const float PadRadius = 1.5f;     // retail pads are 1; Echoes' cannon slots are wider
        const float GoodMiss = 1.5f, KeepMiss = 3f;
        // Echoes pads fly every form on the biped's arc (owner, 2026-10-07: "bounce every form on the same arc"; MphRead
        // JumpPadEntity.HostSameArcBit, set by EchoesArena); the bit sits in the pad's Unused28, which vanilla leaves 0 or 2
        public const uint SameArcBit = 0x80000000;
        // R7: a jump this long (pad to landing, across) is ~6x MPH's longest, so MPH's stick influence has ~6x the way to
        // act on. Such a jump also gets plans with MPH's own control lock on the climb (no stick, no gravity, as vanilla,
        // measured 2026-10-07), then the fall you steer; the plan the stick can upset least is kept (lock 0 on a tie)
        const float LongJump = 60;
        // R7b (owner 10-08 00:3x): no lock on long jumps any more, Echoes' arcs like Alinos Gateway's pad 9 (lock 0, 95
        // units); the locked plans stay for comparisons
        const bool LongJumpLock = false;
        const ushort MaxLock = 54;               // 30 Hz ticks (1.8 s); MPH's own multiplayer pads go to 45

        static float[] F(Vector3 v) => new[] { v.X, v.Y, v.Z };
        static Vector3 V(float[] a) => new(a[0], a[1], a[2]);

        // the cannons of an arena, with their pad (MPH units, on the floor) and landing target; notes for what's skipped
        public static List<Cannon> Find(List<Mrea.ScriptObject> scripts, ArenaRaycaster ray, Func<float, float, float, Vector3> map,
            float scale, float killHeight, List<string> notes)
        {
            var res = new List<Cannon>();
            Mrea.ScriptObject? ById(uint id) => scripts.FirstOrDefault(o => o.Id == id) ?? scripts.FirstOrDefault(o => (o.Id & 0xFFFF) == (id & 0xFFFF));
            Vector3 Pos(Mrea.ScriptObject o) { float[] x = o.Transform; return map(x[0], x[1], x[2]); }
            foreach (Mrea.ScriptObject fn in scripts.Where(o => o.Type == "SPFN" && o.Active))
            {
                Mrea.Property? kind = fn.Root.Find(SpecialFunctionId) ?? fn.Root.Find(SpecialFunctionChoiceId);
                if (kind == null || kind.I32 != LaunchPlayer) continue;
                float speed = fn.Root.Find(ValueParmId)?.F32 ?? 0;
                Mrea.ScriptObject? marker = fn.Connections.Where(c => c.Message == "ACTV").Select(c => ById(c.Target)).FirstOrDefault(o => o != null);
                Vector3 at = Pos(fn);
                // the cannon's ball slot: the nearest ball trigger
                Mrea.ScriptObject? slot = scripts.Where(o => o.Type == "BALT").OrderBy(o => (Pos(o) - at).LengthSquared).FirstOrDefault();
                if (marker == null || slot == null || (Pos(slot) - at).Length > 12 * scale)
                {
                    notes.Add($"cannon '{fn.Name}' skipped: no target marker or ball slot");
                    continue;
                }
                Vector3 slotPos = Pos(slot);
                Vector3? pad = ray.FloorBelow(slotPos + Vector3.UnitY * 1.5f, 5);
                if (pad == null)
                {
                    notes.Add($"cannon '{fn.Name}' skipped: no floor under its ball slot");
                    continue;
                }
                Vector3 mark = Pos(marker);
                string rule = "ledge by the marker";
                // a ceiling between the slot and the marker is the design (owner: Sidehopper's side-room cannons "should
                // hit the ceiling"): the ball hits it and drops onto the ledge under it, not onto the deck above the marker
                Vector3 ball = slotPos + Vector3.UnitY * (0.75f * scale);
                float? under = null;
                if (ray.Cast(ball, mark) is { } roof && roof.Normal.Y < -0.7f)
                {
                    under = ball.Y + (mark.Y - ball.Y) * roof.T - 1.6f;
                    rule = CeilingRule;
                }
                Vector3? target = Landing(ray, mark, 30 * scale, 15 * scale, 10 * scale, killHeight, exclude: pad.Value, maxY: under);
                if (target == null)
                {
                    rule = "ballistic over the gap";
                    target = Ballistic(ray, slotPos, mark, speed * scale, BallGravity * scale, killHeight, pad.Value);
                }
                if (target == null || (target.Value - pad.Value).Length < 4)
                {
                    notes.Add($"cannon '{fn.Name}' skipped: no landing found");
                    continue;
                }
                // (long jumps only: the short ones land where the owner tried them)
                float reach = 0, back = 0;
                Vector3 far = (target.Value.Xz - pad.Value.Xz).Length > LongJump ? FarSide(ray, target.Value, pad.Value, out reach, out back) : target.Value;
                if ((target.Value.Xz - pad.Value.Xz).Length > LongJump)
                {
                    notes.Add($"cannon '{fn.Name.Trim()}': floor runs {back:0.0} back toward the pad and {reach:0.0} on past Echoes' landing");
                }
                var fallbacks = new List<float[]>();
                if (far != target.Value)
                {
                    Vector3 mid = Middle(ray, target.Value, pad.Value);
                    if (mid != far && mid != target.Value) fallbacks.Add(F(mid));
                    fallbacks.Add(F(target.Value));
                    notes.Add($"cannon '{fn.Name.Trim()}': landing moved {(far - target.Value).Length:0.0} on along its floor "
                        + $"({FarMargin} units short of the far edge)");
                    target = far;
                }
                res.Add(new Cannon { Name = fn.Name.Trim(), Rule = rule, Slot = F(pad.Value), Pad = F(pad.Value), Marker = F(mark), Target = F(target.Value),
                    Fallbacks = fallbacks });
            }
            return res;
        }

        const string CeilingRule = "ledge under the ceiling";

        // the walkable spot nearest to p (with room to stand and floor around it), searched in a cylinder around p; none
        // higher than maxY
        static Vector3? Landing(ArenaRaycaster ray, Vector3 p, float radius, float below, float above, float killHeight, Vector3? exclude = null,
            float? maxY = null)
        {
            const float step = 0.75f;
            Vector3? best = null; float bestD = float.MaxValue;
            for (float dx = -radius; dx <= radius; dx += step)
            {
                for (float dz = -radius; dz <= radius; dz += step)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    float top = p.Y + above, bottom = p.Y - below;
                    while (top > bottom)
                    {
                        var from = new Vector3(p.X + dx, top, p.Z + dz);
                        (float T, Vector3 Normal)? hit = ray.Cast(from, new Vector3(from.X, bottom, from.Z));
                        if (hit is not { } h) break;
                        var floor = new Vector3(from.X, top - (top - bottom) * h.T, from.Z);
                        top = floor.Y - 0.05f;
                        if (h.Normal.Y < 0.7f || floor.Y < killHeight + 0.5f || floor.Y > maxY || !Standable(ray, floor)) continue;
                        if (exclude is Vector3 ex && (floor - ex).Length < 6) continue;
                        float d = (floor - p).Length;
                        if (d < bestD) { bestD = d; best = floor; }
                    }
                }
            }
            return best;
        }

        // the middle of the floor a landing is on: the spot within 20 units with the most floor all round at the same
        // height, over unbroken floor from the landing; the landing itself when it already has room (R7: Spires' pair
        // came down 1.5 units from an edge, so a little stick sent the hunter off it), with a clear way in from the pad's
        // side (Spires' middle island has a 10-unit post on the side the pads come from)
        const float RoomEnough = 6, RoomLimit = 20;
        static Vector3 Middle(ArenaRaycaster ray, Vector3 t, Vector3 pad)
        {
            (float Room, Vector3 At) best = (Room(ray, t), t);
            if (best.Room >= RoomEnough) return t;
            for (float dx = -RoomLimit; dx <= RoomLimit; dx += 1)
            {
                for (float dz = -RoomLimit; dz <= RoomLimit; dz += 1)
                {
                    if (dx * dx + dz * dz > RoomLimit * RoomLimit || FloorAt(ray, t + new Vector3(dx, 0, dz), t.Y) is not Vector3 q) continue;
                    float r = Room(ray, q);
                    if (r > best.Room + 0.01f && Standable(ray, q) && Connected(ray, t, q) && WayIn(ray, q, pad)) best = (r, q);
                }
            }
            return best.At;
        }

        // R7b (owner 10-08 00:3x, "like Alinos Gateway's pad 9": Echoes' arcs, no lock, MPH's own stick): a long jump comes
        // down on the far side of its floor along the flight, so a fall a little short (a tap of stick back) still lands
        // on it: the straight floor line on from Echoes' landing (same height, no wall), less FarMargin; never nearer the
        // pad than Echoes' landing (Sidehopper's station pads: theirs is already by the far wall, as the owner liked).
        // Replaces R7's Middle, which pulled those 9 units back toward the pad
        const float FarMargin = 5, ShortFloor = 15;
        static Vector3 FarSide(ArenaRaycaster ray, Vector3 t, Vector3 pad, out float reach, out float back)
        {
            reach = back = 0;
            var d = new Vector3(t.X - pad.X, 0, t.Z - pad.Z);
            if (d.LengthSquared < 1) return t;
            d = d.Normalized();
            reach = FloorRun(ray, t, d);
            back = FloorRun(ray, t, -d);
            if (back + reach < ShortFloor)
            {
                // Echoes' landing sits on a sliver of floor along the flight (Spires' pair: 8 units): first the middle of
                // the roomiest floor near it (R7's rule), then its far side along the new line
                Vector3 m = Middle(ray, t, pad);
                if (m != t) return FarSide(ray, m, pad, out reach, out back);
            }
            // the farthest spot FarMargin short of the edge that a hunter can glide down onto from the pad's side
            for (float x = reach - FarMargin; x > 0.5f; x -= 0.5f)
            {
                if (FloorAt(ray, t + d * x, t.Y) is Vector3 v && Standable(ray, v) && WayIn(ray, v, pad)) return v;
            }
            return t;
        }

        // how far the floor at t's height runs from t along d (no gap, no knee-high wall), up to 40
        static float FloorRun(ArenaRaycaster ray, Vector3 t, Vector3 d)
        {
            float run = 0;
            Vector3 prev = t;
            for (float x = 0.5f; x <= 40; x += 0.5f)
            {
                if (FloorAt(ray, t + d * x, t.Y) is not Vector3 q || ray.Cast(prev + Vector3.UnitY * 0.8f, q + Vector3.UnitY * 0.8f) != null) break;
                prev = q;
                run = x;
            }
            return run;
        }

        // a hunter gliding in from the pad's side, 30 deg down over the last 14 units, misses the collision (feet and head)
        static bool WayIn(ArenaRaycaster ray, Vector3 q, Vector3 pad)
        {
            var back = new Vector3(pad.X - q.X, 0, pad.Z - q.Z);
            if (back.LengthSquared < 1) return true;
            back = back.Normalized() * 14 + Vector3.UnitY * 8;
            Vector3 feet = q + Vector3.UnitY * 0.6f;
            return ray.Cast(feet + back, feet) == null && ray.Cast(feet + back + Vector3.UnitY, feet + Vector3.UnitY) == null;
        }

        // how far the floor at p's height reaches from p, the least of 16 directions (a step of more than 0.6 ends it)
        public static float Room(ArenaRaycaster ray, Vector3 p)
        {
            float min = RoomLimit;
            for (int k = 0; k < 16 && min > 0; k++)
            {
                float a = k * MathF.PI / 8, r = 0;
                var d = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                for (float x = 0.5f; x <= min && FloorAt(ray, p + d * x, p.Y) != null; x += 0.5f) r = x;
                min = MathF.Min(min, r);
            }
            return min;
        }

        // floor along the straight line from a to b, all at a's height
        static bool Connected(ArenaRaycaster ray, Vector3 a, Vector3 b)
        {
            float len = (b.Xz - a.Xz).Length;
            for (float x = 0.5f; x < len; x += 0.5f)
            {
                if (FloorAt(ray, a + (b - a) * (x / len), a.Y) == null) return false;
            }
            return true;
        }

        static Vector3? FloorAt(ArenaRaycaster ray, Vector3 p, float y) =>
            ray.FloorBelow(new Vector3(p.X, y + 0.6f, p.Z), 1.2f) is Vector3 f && MathF.Abs(f.Y - y) <= 0.6f ? f : null;

        // headroom for a hunter, and floor at the same height a little way off in every direction
        static bool Standable(ArenaRaycaster ray, Vector3 f)
        {
            if (ray.Cast(f + Vector3.UnitY * 0.05f, f + Vector3.UnitY * 1.9f) != null) return false;
            foreach (Vector3 d in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
            {
                Vector3? g = ray.FloorBelow(f + d * 1.5f + Vector3.UnitY * 0.6f, 1.2f);
                if (g is not { } v || MathF.Abs(v.Y - f.Y) > 0.4f) return false;
            }
            return true;
        }

        // the ball fired at the marker: where it comes down (a wall hit drops to the floor under it), or, when that's
        // not a place to stand, the floor its descent passes closest over
        static Vector3? Ballistic(ArenaRaycaster ray, Vector3 from, Vector3 marker, float speed, float gravity, float killHeight, Vector3 pad)
        {
            Vector3 v = (marker - from).Normalized() * speed, p = from + Vector3.UnitY * 0.5f;
            const float dt = 1 / 120f;
            Vector3? closest = null; float closestDrop = float.MaxValue;
            for (int i = 0; i < 120 * 8; i++)
            {
                Vector3 q = p + v * dt;
                v.Y -= gravity * dt;
                if (i > 12 && ray.Cast(p, q) is { } h)
                {
                    Vector3 at = p + (q - p) * h.T;
                    Vector3 back = at - new Vector3(v.X, 0, v.Z).Normalized() * 1.5f;
                    Vector3? drop = h.Normal.Y >= 0.7f ? at : ray.FloorBelow(back + Vector3.UnitY * 0.5f, 200);
                    if (drop is Vector3 d && d.Y > killHeight + 0.5f && Landing(ray, d, 8, 3, 3, killHeight) is Vector3 land) return land;
                    return closest;
                }
                if (v.Y < 0 && i % 6 == 0 && (p.Xz - pad.Xz).Length > 15 && ray.FloorBelow(p, 80) is Vector3 f
                    && f.Y > killHeight + 0.5f && p.Y - f.Y < closestDrop && Standable(ray, f))
                {
                    closestDrop = p.Y - f.Y;
                    closest = f;
                }
                p = q;
                if (p.Y < ray.Min.Y - 10) return closest;
            }
            return closest;
        }

        public static JumpPadEntityEditor Editor(Cannon c, short id) => new JumpPadEntityEditor
        {
            Id = id, LayerMask = 0xFFFF, NodeName = ArenaModelWriter.RoomNode,
            Position = c.PadV, Up = Vector3.UnitY, Facing = Vector3.UnitZ,
            ParentId = 0xFFFF, Unused28 = SameArcBit,
            Volume = new CollisionVolume(Vector3.UnitY, Vector3.Zero, PadRadius, 1),
            BeamVector = c.LaunchV.LengthSquared > 0 ? c.LaunchV.Normalized() * 4 : Vector3.UnitY * 4,
            Speed = MathF.Max(c.LaunchV.Length, 0.1f),
            ControlLockTime = c.LockFrames, CooldownTime = 0, Active = true, ModelId = 0, BeamType = 0,
            TriggerFlags = TriggerFlags.PlayerBiped | (c.AltOk ? TriggerFlags.PlayerAlt : 0)
        };

        // ---- tuning against MphRead's player physics ----

        static readonly FieldInfo BeamField = typeof(JumpPadEntity).GetField("_beamVector", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(JumpPadEntity), "_beamVector");

        // Solves each cannon's launch (biped), then checks the alt form with the same launch. The arena's files must be
        // written and registered with these pads (ids = EntityId). Returns a log line per cannon.
        public static List<string> Tune(string room, IReadOnlyList<Cannon> cannons, float killHeight, ArenaRaycaster ray)
        {
            var log = new List<string>();
            if (cannons.Count == 0) return log;
            MatchSettings settings = MatchSettings.Quick(GameMode.Battle, room, Hunter.Samus, 1, 0);
            settings.TimeLimitSeconds = 36000;   // tuning takes many minutes of game time
            settings.PointGoal = 999;
            CampaignHost host = CampaignHost.StartMatch(settings);
            try
            {
                var idle = new CampaignInput { SelectWeapon = BeamType.None };
                for (int f = 0; f < 600 && !(PlayerEntity.Main.Health > 0 && PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Active)); f++) host.Step(idle);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;   // the other seat stands still
                // gravity per frame, measured: a straight-up launch from every pad (the highest rise had no ceiling in the way)
                const float up = 0.6f;
                float apex = cannons.Max(c => Fly(host, Pad(host, c), c.SlotV, Vector3.UnitY * up, false, killHeight).Apex);
                float g = apex > up ? up * up / (2 * (apex - up / 2)) : 0.0185f;
                log.Add($"  pad gravity {g:0.0000} units/frame2 (rise {apex:0.00} for a {up} launch)");
                foreach (Cannon c in cannons)
                {
                    JumpPadEntity pad = Pad(host, c);
                    var candidates = new List<Vector3> { c.TargetV };
                    candidates.AddRange(c.Fallbacks.Select(V));
                    Vector3 t = c.TargetV;
                    (Vector3 V, Vector3 Land, int Frames, float Miss, Vector3 At, string Plan) best = (default, default, 0, float.MaxValue, c.SlotV, "none");
                    int tries = 0;
                    (Vector3 V, Vector3 Land, int Frames, float Miss, Vector3 At, string Plan)? bipedOnly = null, bothOnly = null;
                    bool all = false;
                    for (int ci = 0; ci < candidates.Count; ci++)
                    {
                    t = candidates[ci];
                    best = (default, default, 0, float.MaxValue, c.SlotV, "none");
                    bipedOnly = bothOnly = null;
                    all = false;
                    // the first plan that lands the biped, the alt form and the stick held toward the target (owner: every
                    // form bounces; holding the stick that way must work: R7, Spider Complex's pad under the ledge hung on
                    // the ledge wall); else the first that lands both forms, else the first that lands the biped, else the
                    // nearest miss
                    foreach (Vector3 at in PadSpots(ray, c))
                    {
                        Vector3 edge = BackEdge(at, t);
                        var line = new Vector3(t.X - edge.X, 0, t.Z - edge.Z);
                        Vector3? face = line.LengthSquared > 0.01f ? line.Normalized() : null;
                        foreach ((Vector3 v0, string plan) in Plans(ray, edge, t, c.MarkerV, g, c.Rule == CeilingRule))
                        {
                            tries++;
                            var r = Refine(host, pad, at, edge, t, v0, g, killHeight);
                            if (r.Miss < GoodMiss)
                            {
                                (Vector3 a, _, _) = Fly(host, pad, at, r.V, true, killHeight, edge);
                                if (AltOn(a, t))
                                {
                                    // walked on from 3 units back, the stick kept held (padtest's case; skipped where that
                                    // spot isn't floor: Sidehopper's ceiling pads)
                                    (Vector3 h, _, int hf) = Fly(host, pad, at, r.V, false, killHeight, at - (face ?? Vector3.Zero) * 3, 0, true, face, true);
                                    if (Environment.GetEnvironmentVariable("PAD_DEBUG") == "1") log.Add($"    {plan} at {Fmt(at)}: held walk-on {(hf < 0 ? "skipped" : $"lands {Fmt(h)}")}");
                                    if (hf < 0 || OnFloor(ray, h, t, killHeight))
                                    {
                                        best = (r.V, r.Land, r.Frames, r.Miss, at, plan);
                                        all = true;
                                        break;
                                    }
                                    bothOnly ??= (r.V, r.Land, r.Frames, r.Miss, at, plan);
                                }
                                bipedOnly ??= (r.V, r.Land, r.Frames, r.Miss, at, plan);
                            }
                            else if (r.Miss < best.Miss) best = (r.V, r.Land, r.Frames, r.Miss, at, plan);
                        }
                        if (all) break;
                    }
                    if (!all && (bothOnly ?? bipedOnly) is { } bo) best = bo;
                    if (best.Miss < GoodMiss || ci == candidates.Count - 1) break;
                    log.Add($"    no launch lands on {Fmt(t)} (nearest miss {best.Miss:0.0}): trying {Fmt(candidates[ci + 1])}");
                    }
                    c.Target = F(t);
                    if (!all && bothOnly != null) log.Add("    no plan lands with the stick held toward the target (walked on): first plan that lands both forms kept");
                    c.PadV = best.At;
                    c.Launch = F(best.V);
                    c.Landed = F(best.Land);
                    c.Miss = best.Miss;
                    c.Plan = best.Plan + ((best.At - c.SlotV).Length > 0.1f ? $", pad moved {(best.At - c.SlotV).Length:0.0} out" : "");
                    // lock 0 unless a long jump's locked plan wins below. Every flight here flies the lock the file will
                    // hold (Fly sets it on the pad): in MphRead the lock also holds gravity off, and a lock the tuning
                    // didn't fly sent the hunter straight along the launch line, far past the map (owner, Odin 10-03 18:05)
                    c.LockFrames = 0;
                    c.Dropped = best.Miss > KeepMiss;
                    if (LongJumpLock && (t.Xz - best.At.Xz).Length > LongJump)
                    {
                        Vector3 from = BackEdge(best.At, t);
                        int top = -1;
                        bool steady = false;
                        if (!c.Dropped) log.Add($"    lock  0 {c.Plan}: {Score(host, pad, best.At, from, t, best.V, 0, killHeight, ray, out top, out steady)}");
                        // MPH's lock-0 flight stays where a little stick (forward held up to 10 deg off the line) still lands
                        // (Sidehopper's pods, Spires' single pad: owner, "works"); the lock is for where it doesn't (R7)
                        IEnumerable<(Vector3, ushort, Vector3, float, string)> plans = steady ? Array.Empty<(Vector3, ushort, Vector3, float, string)>()
                            : LockPlans(host, pad, best.At, from, t, g, killHeight, ray, log);
                        if (steady) log.Add("    lock 0 kept: lands with the aim up to 10 deg off the line");
                        foreach ((Vector3 v, ushort lk, Vector3 land, float miss, string plan) in plans)
                        {
                            string s = Score(host, pad, best.At, from, t, v, lk, killHeight, ray, out int n, out _);
                            log.Add($"    lock {lk,2} {plan} (miss {miss:0.0}): {s}");
                            if (n <= top) continue;
                            top = n;
                            c.Launch = F(v); c.Landed = F(land); c.Miss = miss; c.LockFrames = lk; c.Dropped = false;
                            c.Plan = $"{plan}, lock {lk} ({lk / 30f:0.00} s)" + ((best.At - c.SlotV).Length > 0.1f ? $", pad moved {(best.At - c.SlotV).Length:0.0} out" : "");
                        }
                        best.V = c.LaunchV;
                    }
                    string alt = "";
                    if (!c.Dropped)
                    {
                        (Vector3 a, _, _) = Fly(host, pad, best.At, best.V, true, killHeight, BackEdge(best.At, t), c.LockFrames);
                        c.AltLanded = F(a);
                        c.AltMiss = (a.Xz - t.Xz).Length;
                        c.AltOk = AltOn(a, t);
                        alt = $", alt lands {Fmt(a)} ({c.AltMiss:0.0} across){(c.AltOk ? "" : " -> alt form left off this pad")}";
                    }
                    log.Add($"  cannon '{c.Name}' ({c.Rule}; {c.Plan}; {tries} plans): pad {Fmt(best.At)} -> target {Fmt(t)}, launch {Fmt(best.V)} "
                        + $"({best.V.Length:0.00}/f, lock {c.LockFrames}), biped lands {Fmt(c.LandedV)} miss {c.Miss:0.0}{alt}{(c.Dropped ? " -> NO PAD (no launch lands there)" : "")}");
                }
            }
            finally
            {
                host.Dispose();
            }
            return log;
        }

        // the alt forms come down a little differently (MPH's own pads accept that): fine when the ball ends up on the same level
        static bool AltOn(Vector3 a, Vector3 t) => (a.Xz - t.Xz).Length < 8 && MathF.Abs(a.Y - t.Y) < 1.5f && PlayerEntity.Main.Health > 0;

        // a standing hunter came down on the target's floor (same level, a straight floor line to the target: Score's test)
        static bool OnFloor(ArenaRaycaster ray, Vector3 land, Vector3 t, float killHeight)
        {
            Vector3 floor = land - Vector3.UnitY * 0.5f;
            return PlayerEntity.Main.Health > 0 && land.Y >= killHeight && MathF.Abs(floor.Y - t.Y) < 1 && (floor.Xz - t.Xz).Length < 30
                && Connected(ray, t, floor);
        }

        // the cannon's spot, then spots out toward the landing (same floor level, room to stand)
        static IEnumerable<Vector3> PadSpots(ArenaRaycaster ray, Cannon c)
        {
            Vector3 s = c.SlotV;
            yield return s;
            var dir = new Vector3(c.Target[0] - s.X, 0, c.Target[2] - s.Z);
            if (dir.LengthSquared < 1) yield break;
            dir.Normalize();
            for (int k = 1; k <= 8; k++)
            {
                Vector3? f = ray.FloorBelow(s + dir * (1.5f * k) + Vector3.UnitY * 0.6f, 1.2f);
                if (f is not Vector3 p || MathF.Abs(p.Y - s.Y) > 0.6f) break;
                if (k % 2 == 0 && Standable(ray, p)) yield return p;
            }
        }

        // launch candidates whose drag-free arc clears the collision: high arcs at rising clearance, and shots at the
        // marker (Echoes' own line) at the speed that reaches the target's distance. Under a ceiling hit by design, also
        // Echoes' shot: up at the marker hard enough to reach its height, into the ceiling, then down onto the ledge (the
        // flight decides; Refine corrects the across speed)
        static IEnumerable<(Vector3 V, string Plan)> Plans(ArenaRaycaster ray, Vector3 p, Vector3 t, Vector3 marker, float g, bool ceiling)
        {
            if (ceiling)
            {
                Vector3 h = new Vector3(t.X - p.X, 0, t.Z - p.Z);
                float vy = MathF.Sqrt(2 * g * MathF.Max(marker.Y - p.Y, 1));
                float frames = 2 * vy / g;   // rough: up to the ceiling and down again
                yield return (h / frames + Vector3.UnitY * vy, "into the ceiling");
            }
            Vector3 d = t - p;
            var dir = new Vector3(d.X, 0, d.Z);
            float dist = dir.Length;
            dir /= dist;
            float clear = 1.5f + 0.12f * dist;
            for (int i = 0; i < 6; i++, clear *= 1.5f)
            {
                float top = MathF.Max(p.Y, t.Y) + clear;
                float vy = MathF.Sqrt(2 * g * (top - p.Y));
                float frames = vy / g + MathF.Sqrt(2 * (top - t.Y) / g);
                Vector3 v = dir * (dist / frames) + Vector3.UnitY * vy;
                if (ArcClear(ray, p, v, g, t)) yield return (v, $"arc +{clear:0.0}");
            }
            Vector3 m = (marker - p).Normalized();
            if (m.Y > 0.05f && new Vector3(m.X, 0, m.Z).Length > 0.05f)
            {
                // landing distance grows with speed: bisect for the target's
                float lo = 0.1f, hi = 8;
                for (int i = 0; i < 40; i++)
                {
                    float mid = (lo + hi) / 2;
                    if (Reach(p, m * mid, g, t.Y) < dist) lo = mid; else hi = mid;
                }
                Vector3 v = m * lo;
                if (ArcClear(ray, p, v, g, t)) yield return (v, "at the marker");
            }
        }

        // drag-free flight: horizontal distance covered when coming back down to height y
        static float Reach(Vector3 p, Vector3 v, float g, float y)
        {
            Vector3 q = p + Vector3.UnitY * 0.5f;
            for (int f = 0; f < 2000; f++)
            {
                q += v;
                v.Y -= g;
                if (v.Y < 0 && q.Y - 0.5f <= y) break;
            }
            return (q.Xz - p.Xz).Length;
        }

        // the hunter's feet and head miss the collision until it's over the target and coming down to it
        static bool ArcClear(ArenaRaycaster ray, Vector3 p, Vector3 v, float g, Vector3 t)
        {
            Vector3 q = p + Vector3.UnitY * 0.6f;
            float dist = (t.Xz - p.Xz).Length;
            for (int f = 0; f < 2000; f++)
            {
                Vector3 n = q + v;
                v.Y -= g;
                bool near = (n.Xz - t.Xz).Length < 2.5f;
                if (f > 3 && !near)
                {
                    if (ray.Cast(q, n) != null || ray.Cast(q + Vector3.UnitY * 1.0f, n + Vector3.UnitY * 1.0f) != null) return false;
                }
                q = n;
                if (v.Y < 0 && q.Y - 0.6f <= t.Y) return (q.Xz - p.Xz).Length > dist - 3;
            }
            return false;
        }

        // MPH-style plans for a long jump: the lock (no stick, no gravity) carries the hunter in a straight line up at a
        // climb angle, then the arc you steer comes down on the target. Per lock length and angle, the speed is bisected
        // (drag-free) for the target's distance; a path into the collision is skipped (Spires' spires stand in the way of
        // the flat ones); each plan is then flown and corrected (Refine)
        static IEnumerable<(Vector3 V, ushort Lock, Vector3 Land, float Miss, string Plan)> LockPlans(CampaignHost host, JumpPadEntity pad,
            Vector3 at, Vector3 from, Vector3 t, float g, float killHeight, ArenaRaycaster ray, List<string> log)
        {
            Vector3 start = from + Vector3.UnitY * 0.5f, end = Stand(t);
            var u = new Vector3(end.X - start.X, 0, end.Z - start.Z);
            float dist = u.Length;
            u /= dist;
            int tried = 0;
            foreach (int lk in new[] { 15, 25, 35, 45, MaxLock })
            {
                foreach (float deg in new[] { 4f, 7f, 10f, 14f, 19f, 25f })
                {
                    float rad = MathHelper.DegreesToRadians(deg);
                    Vector3 dir = u * MathF.Cos(rad) + Vector3.UnitY * MathF.Sin(rad);
                    float lo = 0.3f, hi = 4f;
                    if (LockReach(start, dir * hi, lk, g, end.Y) < dist) continue;   // too far even at the top speed
                    for (int i = 0; i < 30; i++)
                    {
                        float mid = (lo + hi) / 2;
                        if (LockReach(start, dir * mid, lk, g, end.Y) < dist) lo = mid; else hi = mid;
                    }
                    if (hi > 3.2f || hi * lk > dist) continue;   // faster than a cannon shot, or the lock alone flies past the target
                    Vector3 v0 = dir * hi;
                    if (LockClear(ray, start, v0, lk, g, t) is Vector3 hit)
                    {
                        if (Environment.GetEnvironmentVariable("PAD_DEBUG") == "1") log.Add($"    lock {lk,2} {deg:0} deg at {hi:0.00}/tick: path hits the collision at {Fmt(hit)} ({(hit.Xz - start.Xz).Length:0} out; top there {Top(ray, hit):0.0})");
                        continue;
                    }
                    tried++;
                    var r = Refine(host, pad, at, from, t, v0, g, killHeight, (ushort)lk);
                    string plan = $"lock climb {deg:0} deg at {hi:0.00}/tick";
                    if (r.Miss > GoodMiss)
                    {
                        log.Add($"    lock {lk,2} {plan}: no landing (miss {r.Miss:0.0}, came down at {Fmt(r.Land)} after {r.Frames} f)");
                        continue;
                    }
                    yield return (r.V, (ushort)lk, r.Land, r.Miss, plan);
                }
            }
            if (tried == 0) log.Add("    no lock plan clears the collision");
        }

        static float Top(ArenaRaycaster ray, Vector3 p) =>
            ray.Cast(new Vector3(p.X, ray.Max.Y + 1, p.Z), new Vector3(p.X, ray.Min.Y - 1, p.Z)) is { } h ? ray.Max.Y + 1 - (ray.Max.Y - ray.Min.Y + 2) * h.T : float.NaN;

        // drag-free: the lock's straight line, then the arc; across distance when coming back down to height y
        static float LockReach(Vector3 p, Vector3 v, int lock30, float g, float y)
        {
            Vector3 q = p + v * lock30;
            for (int f = 0; f < 2000; f++)
            {
                q += v;
                v.Y -= g;
                if (v.Y < 0 && q.Y <= y) break;
            }
            return (q.Xz - p.Xz).Length;
        }

        // the hunter's feet and head miss the collision along the lock's line and the arc, until over the target: null,
        // else where it hits
        static Vector3? LockClear(ArenaRaycaster ray, Vector3 p, Vector3 v, int lock30, float g, Vector3 t)
        {
            Vector3 q = p + Vector3.UnitY * 0.1f;
            for (int f = 0; f < 2000; f++)
            {
                Vector3 n = q + v;
                if (f >= lock30) v.Y -= g;
                if (f > 3 && (n.Xz - t.Xz).Length >= 2.5f)
                {
                    if (ray.Cast(q, n) != null || ray.Cast(q + Vector3.UnitY * 1.0f, n + Vector3.UnitY * 1.0f) != null) return n;
                }
                q = n;
                if (v.Y < 0 && q.Y - 0.1f <= t.Y + 0.5f) return null;
            }
            return q;
        }

        static readonly float[] Aims = { 0, 5, -5, 10, -10, 20, -20, 45, -45 };

        // how much stick a plan takes: forward held from the launch with the aim N deg off the pad's line (MPH steers the
        // flight toward where the stick points: owner, "you can really mess up your trajectory ... by design"), the stick
        // let go, the morph ball let go; rank = the cases that come down on the target's floor, a landing ball first (owner:
        // every form bounces on these pads); steady = forward held up to 10 deg off the line lands
        static string Score(CampaignHost host, JumpPadEntity pad, Vector3 at, Vector3 from, Vector3 t, Vector3 v, ushort lock30, float killHeight,
            ArenaRaycaster ray, out int rank, out bool steady)
        {
            var line = new Vector3(t.X - from.X, 0, t.Z - from.Z).Normalized();
            var parts = new List<string>();
            int held = 0;
            string Case(bool alt, bool hold, float aim, out bool on)
            {
                (Vector3 land, _, _) = Fly(host, pad, at, v, alt, killHeight, from, lock30, hold, Matrix3.CreateRotationY(MathHelper.DegreesToRadians(aim)) * line);
                bool dead = PlayerEntity.Main.Health == 0 || land.Y < killHeight;
                Vector3 floor = land - Vector3.UnitY * 0.5f;
                on = !dead && MathF.Abs(floor.Y - t.Y) < (alt ? 1.5f : 1) && (floor.Xz - t.Xz).Length < 30 && Connected(ray, t, floor);
                return on ? "ok" : dead ? "fell" : $"off{(land.Xz - t.Xz).Length:0}";
            }
            steady = true;
            foreach (float a in Aims)
            {
                parts.Add($"{a:+0;-0;0}:{Case(false, true, a, out bool on)}");
                if (on) held++;
                else if (MathF.Abs(a) <= 10) steady = false;
            }
            string go = Case(false, false, 0, out bool goOn), ball = Case(true, false, 0, out bool ballOn);
            int n = held + (goOn ? 1 : 0) + (ballOn ? 1 : 0);
            rank = n + (ballOn ? 100 : 0);
            return $"{n}/{Aims.Length + 2}: held {held}/{Aims.Length} [{string.Join(" ", parts)}], let go {go}, ball {ball}";
        }

        // flies a launch from `from` (on the pad at `at`) and corrects its across speed and heading toward the target (a few runs)
        static (Vector3 V, Vector3 Land, int Frames, float Miss) Refine(CampaignHost host, JumpPadEntity pad, Vector3 at, Vector3 from, Vector3 t,
            Vector3 v0, float g, float killHeight, ushort lock30 = 0)
        {
            var dir = new Vector3(t.X - from.X, 0, t.Z - from.Z);
            float dist = dir.Length;
            dir /= dist;
            var side = Vector3.Cross(Vector3.UnitY, dir);
            Vector3 v = v0;
            (Vector3 V, Vector3 Land, int Frames, float Miss) best = (v0, default, 0, float.MaxValue);
            for (int iter = 0; iter < 6; iter++)
            {
                (Vector3 land, _, int frames) = Fly(host, pad, at, v, false, killHeight, from, lock30);
                float miss = (land - Stand(t)).Length;
                if (miss < best.Miss) best = (v, land, frames, miss);
                if (miss < 0.6f) break;
                Vector3 got = land - from;
                float along = Vector3.Dot(new Vector3(got.X, 0, got.Z), dir);
                if (MathF.Abs(land.Y - Stand(t).Y) > 1.0f && along < dist - 1) break;   // stopped short on another level: this plan is blocked
                var h = new Vector3(v.X, 0, v.Z);
                if (along > 0.5f) h *= dist / along;
                float yaw = -MathF.Atan2(Vector3.Dot(new Vector3(got.X, 0, got.Z), side), MathF.Max(along, 1));
                h = Matrix3.CreateRotationY(yaw) * h;
                v = new Vector3(h.X, v.Y, h.Z);
            }
            return best;
        }

        // where a standing hunter's position is over a floor point (MPH: -0.5..1.1 around the position)
        static Vector3 Stand(Vector3 floor) => floor + Vector3.UnitY * 0.5f;

        static string Fmt(Vector3 v) => $"({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})";

        static JumpPadEntity Pad(CampaignHost host, Cannon c) =>
            host.Scene.TryGetEntity(c.EntityId, out EntityBase? e) && e is JumpPadEntity jp ? jp
                : throw new InvalidOperationException($"jump pad {c.EntityId} for cannon '{c.Name}' isn't in the scene");

        // where walking onto the pad toward its target sets it off: the back edge of its volume. Launches are solved from
        // there; from the centre, 1.5 units further on, Spider Complex's pad under the ledge clipped the lip when walked onto
        static Vector3 BackEdge(Vector3 at, Vector3 t)
        {
            var d = new Vector3(t.X - at.X, 0, t.Z - at.Z);
            return d.LengthSquared > 0.01f ? at - d.Normalized() * (PadRadius - 0.15f) : at;
        }

        static readonly FieldInfo DataField = typeof(JumpPadEntity).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(JumpPadEntity), "_data");
        static readonly FieldInfo LockField = typeof(JumpPadEntityData).GetField(nameof(JumpPadEntityData.ControlLockTime))
            ?? throw new MissingFieldException(nameof(JumpPadEntityData), nameof(JumpPadEntityData.ControlLockTime));

        // the pad's ControlLockTime as the file will hold it (the rest of its data, same-arc bit included, kept)
        static void SetLock(JumpPadEntity pad, ushort lock30)
        {
            object data = DataField.GetValue(pad)!;
            if ((ushort)LockField.GetValue(data)! == lock30) return;
            LockField.SetValue(data, lock30);
            DataField.SetValue(pad, data);
        }

        // puts the main player on the pad at `at` (standing at `stand`, default the centre; morphed or not; facing `face`)
        // with the given launch and control lock, the stick held forward or let go; returns the landing, the highest rise
        // above the pad and the frames in the air. walkOn: `stand` is a run-up spot off the pad, walked onto the pad with
        // the stick held (Frames -1 when that spot isn't floor at the pad's height or the walk never set the pad off)
        static (Vector3 Land, float Apex, int Frames) Fly(CampaignHost host, JumpPadEntity pad, Vector3 at, Vector3 launch, bool alt, float killHeight,
            Vector3? stand = null, ushort lock30 = 0, bool hold = false, Vector3? face = null, bool walkOn = false)
        {
            Vector3 from = stand ?? at;
            PlayerEntity me = PlayerEntity.Main;
            var idle = new CampaignInput { SelectWeapon = BeamType.None };
            var walk = new CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(0, 1) };
            var morph = new CampaignInput { SelectWeapon = BeamType.None, Buttons = CampaignButtons.Morph };
            var press = new CampaignInput { SelectWeapon = BeamType.None, Buttons = CampaignButtons.Shoot | CampaignButtons.Jump };
            for (int f = 0; f < 900 && me.Health == 0; f++) host.Step(f % 2 == 0 ? press : idle);   // respawn after a fall
            BeamField.SetValue(pad, Vector3.Zero);
            SetLock(pad, lock30);
            pad.Position = at;
            for (int f = 0; f < 40 && me.IsAltForm != alt; f++) host.Step(f % 2 == 0 ? morph : idle);
            me.Reposition(from + Vector3.UnitY * 0.2f - me.Position, me.NodeRef);
            if (face is Vector3 fd) me.Reposition(me.Position, fd, me.NodeRef);
            me.Speed = Vector3.Zero;
            for (int f = 0; f < 20; f++) host.Step(idle);
            for (int f = 0; f < 40 && me.IsAltForm != alt; f++) host.Step(f % 2 == 0 ? morph : idle);
            if (face is Vector3 fd2) me.Reposition(me.Position, fd2, me.NodeRef);
            BeamField.SetValue(pad, launch);
            if (walkOn)
            {
                for (int f = 0; f < 60 && me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad); f++) host.Step(idle);   // the last push ends on landing
                if (MathF.Abs(me.Position.Y - at.Y) > 1 || me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad))
                {
                    BeamField.SetValue(pad, Vector3.Zero);
                    return (me.Position, 0, -1);
                }
                for (int f = 0; f < 120 && !me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad); f++) host.Step(walk);
                if (!me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad))
                {
                    BeamField.SetValue(pad, Vector3.Zero);
                    return (me.Position, 0, -1);
                }
            }
            Vector3 start = me.Position;
            float apex = 0;
            int frames = 0;
            bool left = false;
            for (; frames < 1500; frames++)
            {
                host.Step(hold ? walk : idle);
                apex = MathF.Max(apex, me.Position.Y - start.Y);
                if (me.Health == 0 || me.Position.Y < killHeight) break;
                if (!left)
                {
                    left = (me.Position - start).Length > 0.3f;
                    continue;
                }
                // (the pad sets Standing; a locked climb can be flat enough to look like a landing until the lock is over)
                if (frames > 8 + lock30 * 2 && me.Flags1.TestFlag(PlayerFlags1.Standing) && MathF.Abs(me.Speed.Y) < 0.05f) break;
            }
            Vector3 land = me.Position;
            BeamField.SetValue(pad, Vector3.Zero);
            return (land, apex, frames);
        }
    }
}
