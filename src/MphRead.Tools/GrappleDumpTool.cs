using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Arenas;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRead
{
    // recomp: Echoes grapple points (docs/GRAPPLE.md G0/G1).
    //   -grappledump [disc]          every arena's GRAP objects + GrappleInfo, and for each point what's around it in the
    //                                 arena's own collision (Echoes units): floor below, open space around, floor reachable
    //                                 for a grab (within GrappleDistance, in plain sight), and the floor heights a swing
    //                                 off it could reach that a jump from under it can't. Settles which points are real.
    //   -grappledump sim [arena] [point] [disc]
    //                                 the faithful-port check in a real match (CampaignHost, docs/GRAPPLE.md section 7): from
    //                                 the floor beside the point, aim at it, hold grapple; the catch, the pull to under the
    //                                 point, the fixed swing (bottoms half a period apart at Echoes' top speed, the rope
    //                                 length), the turn input swinging the plane round, the release jump and the landing;
    //                                 then the range rule (no grab past 25 Echoes units, a grab at 20).
    internal static class GrappleDumpTool
    {
        static readonly string DefaultDisc = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "brawl_extract", "prime", "echoes_widescreen.iso"));
        static readonly string[] Arenas = { "Sidehopper Station", "Shooting Gallery" };

        public static void Run(string[] args)
        {
            if (args.Length > 1 && args[1] == "sim")
            {
                Sim(args.Length > 2 ? args[2] : "Shooting Gallery", args.Length > 3 ? Convert.ToUInt32(args[3], 16) : 0x193u,
                    args.Length > 4 ? args[4] : DefaultDisc);
                return;
            }
            if (args.Length > 1 && args[1] == "assets")
            {
                Assets(args.Length > 2 ? args[2] : DefaultDisc);
                return;
            }
            Dump(args.Length > 1 ? args[1] : DefaultDisc);
        }

        sealed class Tris
        {
            public readonly List<(Vector3 A, Vector3 B, Vector3 C)> List = new();

            public Tris(Mrea.Collision col)
            {
                Vector3 V(int i) => new Vector3(col.Vertices[3 * i], col.Vertices[3 * i + 1], col.Vertices[3 * i + 2]);
                for (int t = 0; t < col.TriangleCount; t++)
                {
                    List.Add((V(col.Triangles[3 * t]), V(col.Triangles[3 * t + 1]), V(col.Triangles[3 * t + 2])));
                }
            }

            // nearest hit along dir (unit) within max, both faces
            public float Cast(Vector3 o, Vector3 dir, float max)
            {
                float best = max;
                foreach ((Vector3 a, Vector3 b, Vector3 c) in List)
                {
                    Vector3 e1 = b - a, e2 = c - a;
                    Vector3 p = Vector3.Cross(dir, e2);
                    float det = Vector3.Dot(e1, p);
                    if (MathF.Abs(det) < 1e-8f) continue;
                    float inv = 1 / det;
                    Vector3 s = o - a;
                    float u = Vector3.Dot(s, p) * inv;
                    if (u < 0 || u > 1) continue;
                    Vector3 q = Vector3.Cross(s, e1);
                    float v = Vector3.Dot(dir, q) * inv;
                    if (v < 0 || u + v > 1) continue;
                    float t = Vector3.Dot(e2, q) * inv;
                    if (t > 1e-4f && t < best) best = t;
                }
                return best;
            }

            // how far short of b the sight line from a stops (0 = clear)
            public float Gap(Vector3 a, Vector3 b)
            {
                Vector3 d = b - a;
                float len = d.Length;
                return len - Cast(a, d / len, len);
            }

            public bool Clear(Vector3 a, Vector3 b)
            {
                Vector3 d = b - a;
                float len = d.Length;
                return Cast(a, d / len, len) >= len - 0.05f;
            }
        }

        // which of the disc's paks hold the grapple beam's effects, sounds and arm models (G2)
        static void Assets(string disc)
        {
            using FileStream fs = File.OpenRead(disc);
            var gc = new GcDisc(fs);
            uint[] ids = { 0xd5a18910, 0x9d58a04f, 0xcfc222b0, 0x2cc7f7f5, 0x87c0bdb2, 0xed0f768f, 0x67fb1c2e, 0x670abe2a, 0x434d4fc0, 0x463d7a4e, 0xd770ea7f };
            foreach (string f in gc.Files.Keys.Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
            {
                byte[] raw = gc.ReadFile(f);
                var pak = new Pak(raw, f);
                var has = ids.Where(pak.Contains).ToList();
                if (has.Count > 0) Console.WriteLine($"{f} (sha1 {Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(raw)).ToLowerInvariant()}): " + string.Join(" ", has.Select(i => i.ToString("x8"))));
            }
        }

        static void Dump(string disc)
        {
            using FileStream fs = File.OpenRead(disc);
            var gc = new GcDisc(fs);
            var pak = new Pak(gc.ReadFile("Metroid6.pak"), "Metroid6.pak");
            float scale = EchoesArena.DefaultScale;
            Vector3 Map(float x, float y, float z) => new Vector3(x, z, -y) * scale;
            foreach (EchoesArena.Def def in EchoesArena.All)
            {
                Mrea area = Mrea.Read(pak.Get(def.Mrea));
                List<Mrea.ScriptObject> scripts = area.ReadScripts();
                List<GrappleSpot> spots = EchoesGrapple.Read(scripts, Map);
                if (spots.Count == 0) continue;
                var tris = new Tris(area.ReadCollision());
                Console.WriteLine($"== {def.InGameName} ({def.Key}): {spots.Count} grapple point(s), {tris.List.Count} collision triangles");
                foreach (GrappleSpot s in spots)
                {
                    var p = new Vector3(s.Echoes[0], s.Echoes[1], s.Echoes[2]);
                    Console.WriteLine($"  GRAP {s.Id:x8} '{s.Name}' active={s.Active} layer={s.Layer} echoes=({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}) mph=({s.Pos[0]:0.00}, {s.Pos[1]:0.00}, {s.Pos[2]:0.00})");
                    Console.WriteLine($"    ring={(s.RingId is uint r ? r.ToString("x8") : "none")} length={s.Length} attach={s.AttachLength} spring={s.SpringConstant}/{s.SpringLength}/{s.SpringTardis} swing={s.SwingForce}/{s.SwingMaxForce} arc={s.ArcAngle} turn={s.TurnAngle} axis={s.ConstrainToAxis}");
                    // connections: what scripts drive it (a real point is usually wired to the layer/relays; a leftover isn't)
                    Mrea.ScriptObject obj = scripts.First(o => o.Id == s.Id);
                    var into = scripts.Where(o => o.Connections.Any(c => c.Target == s.Id)).Select(o => $"{o.Type} {o.Id:x8}").ToList();
                    Console.WriteLine($"    links out={obj.Connections.Count} in={into.Count} {string.Join(", ", into.Take(6))}");
                    if (s.RingId is uint rid && scripts.FirstOrDefault(o => o.Id == rid) is Mrea.ScriptObject ring)
                    {
                        var ringIn = scripts.Where(o => o.Connections.Any(c => c.Target == rid)).Select(o => $"{o.Type} {o.Id:x8} '{o.Name}'").ToList();
                        Console.WriteLine($"    ring object {ring.Type} '{ring.Name}' active={ring.Active} layer={ring.Layer} at=({ring.Transform[0]:0.00}, {ring.Transform[1]:0.00}, {ring.Transform[2]:0.00}) "
                            + $"out={string.Join(", ", ring.Connections.Select(c => $"{c.State}/{c.Message}->{c.Target:x8}"))} in={string.Join(", ", ringIn)}");
                    }
                    float down = tris.Cast(p, -Vector3.UnitZ, 200);
                    float up = tris.Cast(p, Vector3.UnitZ, 200);
                    // open space: 26 directions, distance to the first wall (cap 30)
                    var dists = new List<float>();
                    for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                    {
                        if (x == 0 && y == 0 && z == 0) continue;
                        var dir = Vector3.Normalize(new Vector3(x, y, z));
                        dists.Add(tris.Cast(p, dir, 30));
                    }
                    Console.WriteLine($"    floor below {down:0.00} (z {p.Z - down:0.00}), roof above {(up >= 200 ? "none" : up.ToString("0.00"))}, "
                        + $"open space: min {dists.Min():0.00} median {dists.OrderBy(d => d).ElementAt(13):0.00} max {dists.Max():0.00} (30 = open)");
                    // floor cells (1 unit grid) within the grab distance with a clear line to the point
                    const float Grab = 25;
                    int cells = 0, inSight = 0;
                    var heights = new SortedDictionary<int, int>();
                    var blockAt = new SortedDictionary<int, int>();
                    float floorZ = p.Z - down;
                    for (float gx = -Grab; gx <= Grab; gx += 1)
                    for (float gy = -Grab; gy <= Grab; gy += 1)
                    {
                        if (gx * gx + gy * gy > Grab * Grab) continue;
                        var top = new Vector3(p.X + gx, p.Y + gy, p.Z + MathF.Min(10, up - 0.1f));
                        float hit = tris.Cast(top, -Vector3.UnitZ, 60);
                        if (hit >= 60) continue;
                        Vector3 floor = top - Vector3.UnitZ * hit;
                        Vector3 eye = floor + Vector3.UnitZ * 1.5f;
                        if ((eye - p).Length > Grab) continue;
                        cells++;
                        float gap = tris.Gap(eye, p);
                        blockAt[(int)MathF.Min(gap, 30)] = blockAt.GetValueOrDefault((int)MathF.Min(gap, 30)) + 1;
                        if (gap > 0.05f) continue;
                        inSight++;
                        int h = (int)MathF.Round(floor.Z - floorZ);
                        heights[h] = heights.GetValueOrDefault(h) + 1;
                    }
                    Console.WriteLine($"    grab floor: {inSight} of {cells} 1x1 cells within {Grab} in sight; heights vs the floor below (z rel: cells): "
                        + string.Join(" ", heights.Select(kv => $"{kv.Key:+0;-0;0}:{kv.Value}")));
                    Console.WriteLine($"    first wall on the sight line, distance short of the point (units: cells): "
                        + string.Join(" ", blockAt.Select(kv => $"{kv.Key}:{kv.Value}")));
                    // a swing off a 10 rope hanging to z-10: floors in reach up to the point's height that sit higher than
                    // a ground jump from the floor below gets (Echoes MP jump ~ 2.4 m with the double jump ~4.8)
                    float reachTop = p.Z - floorZ;
                    var swingOnly = heights.Where(kv => kv.Key > 5 && kv.Key <= reachTop + 1).Sum(kv => kv.Value);
                    Console.WriteLine($"    swing-only floor (>5 above the floor below, <= the point): {swingOnly} cells");
                }
            }
        }

        // an arena's points straight off the Echoes disc (also -replaygrapple: the PC arena.json may predate G0)
        internal static List<GrappleSpot> DiscSpots(EchoesArena.Def def, string disc)
        {
            using FileStream fs = File.OpenRead(disc);
            var gc = new GcDisc(fs);
            var pak = new Pak(gc.ReadFile("Metroid6.pak"), "Metroid6.pak");
            float scale = EchoesArena.DefaultScale;
            Vector3 Map(float x, float y, float z) => new Vector3(x, z, -y) * scale;
            return EchoesGrapple.Read(Mrea.Read(pak.Get(def.Mrea)).ReadScripts(), Map);
        }

        static void Sim(string arena, uint pointId, string disc)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                Console.WriteLine("no desktop extraction");
                return;
            }
            EchoesArena.Register(Paths.FileSystem);
            EchoesArena.Def def = EchoesArena.Find(arena) ?? throw new InvalidOperationException($"no arena {arena}");
            // the disc's points (the PC arena.json may predate G0)
            List<GrappleSpot> spots = DiscSpots(def, disc);
            GrappleSpot spot = spots.First(s => s.Id == pointId);
            EchoesGrapple.SetRoomSpots(def.Id, new[] { spot });
            var anchor = new Vector3(spot.Pos[0], spot.Pos[1], spot.Pos[2]);
            Console.WriteLine($"{def.InGameName} point {pointId:x8} at mph ({anchor.X:0.00}, {anchor.Y:0.00}, {anchor.Z:0.00})");
            const float S = EchoesArena.DefaultScale;
            float rope = PlayerEntity.GrappleRopeDefault;
            float maxVel = 23 * S / 30;
            float jumpVel = 10000f * 13 / 200 / 60 * S / 30;
            int half = (int)(PlayerEntity.GrappleSwingSteps / 2);
            int fails = 0;
            void Fail(string why)
            {
                Console.WriteLine("    FAIL: " + why);
                fails++;
            }

            // a fresh match with her standing still at the spawn; null when the room has no point
            MphRecomp.Campaign.CampaignHost? Start(out PlayerEntity me, MphRecomp.Campaign.CampaignInput input)
            {
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, Hunter.Samus, 1, 0);
                settings.TimeLimitSeconds = 300;
                var host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                me = PlayerEntity.Main;
                for (int f = 0; f < 600 && !me.LoadFlags.TestFlag(LoadFlags.Spawned); f++) host.Step(input);
                // as the pad tests: let her land at the spawn first (a teleport before that is undone by the spawn)
                for (int f = 0; f < 240 && !me.Flags1.TestFlag(PlayerFlags1.Standing); f++) host.Step(input);
                for (int f = 0; f < 30; f++) host.Step(input);
                if (PlayerEntity.HostGrapplePoints is not { Count: 1 })
                {
                    Console.WriteLine($"  no grapple point in the room (EchoesGrapple.Extra didn't run): multiplayer={GameState.Multiplayer} enabled={EchoesGrapple.Enabled} "
                        + $"room spots={EchoesGrapple.Has(def.Id)} def.Id={def.Id} points={PlayerEntity.HostGrapplePoints?.Count}");
                    host.Dispose();
                    return null;
                }
                me.GrappleInput = false;
                me.GrappleTurnInput = 0;
                return host;
            }

            // the floor under (x, z) near the point's height, or null
            float? Floor(MphRecomp.Campaign.CampaignHost host, float x, float z)
            {
                var top = new Vector3(x, anchor.Y + 2, z);
                var res = new MphRead.Formats.CollisionResult();
                if (!MphRead.Formats.CollisionDetection.CheckBetweenPoints(top, top.AddY(-40), MphRead.Formats.TestFlags.Players, host.Scene, ref res))
                {
                    return null;
                }
                return res.Position.Y + 0.05f;
            }

            Vector3 Eye(PlayerEntity me) => me.Position.AddY(Fixed.ToFloat(me.Values.AimYOffset));

            // how far off her view the point is (tan of the angle; Echoes' cone is 0.418)
            float Off(PlayerEntity me)
            {
                Vector3 look = anchor - Eye(me);
                Vector3 aim = me.FacingVector;
                float forward = Vector3.Dot(look, aim);
                return forward <= 0 ? float.MaxValue : (look - aim * forward).Length / forward;
            }

            // place her (standing, facing level toward the point) and pitch the view onto it with the stick, as a player would
            bool PlaceAndAim(MphRecomp.Campaign.CampaignHost host, PlayerEntity me, MphRecomp.Campaign.CampaignInput input, Vector3 start)
            {
                host.PlacePlayer(start, (anchor - start).WithY(0).Normalized());
                for (int f = 0; f < 30; f++) host.Step(input);
                for (int f = 0; f < 60 && Off(me) > 0.05f; f++)
                {
                    // as the scan test: turn the camera by the angle left to the point (mouse units, 2 a degree)
                    Vector3 dir = (anchor - me.CameraInfo.Position).Normalized();
                    Vector3 facing = me.CameraInfo.Facing.Normalized();
                    Vector3 right = Vector3.Cross(facing, Vector3.UnitY).Normalized();
                    var level = new Vector3(facing.X, 0, facing.Z).Normalized();
                    float yaw = MathF.Atan2(Vector3.Dot(dir, right), Vector3.Dot(dir.WithY(0), level)) * 180 / MathF.PI;
                    float pitch = (MathF.Asin(Math.Clamp(dir.Y, -1, 1)) - MathF.Asin(Math.Clamp(facing.Y, -1, 1))) * 180 / MathF.PI;
                    input.AimDelta = new System.Numerics.Vector2(Math.Clamp(yaw * 2, -40, 40), Math.Clamp(-pitch * 2, -40, 40));
                    host.Step(input);
                }
                input.AimDelta = default;
                host.Step(input);
                return Off(me) < 0.3f;
            }

            // side offsets (MPH units) from under the point; turn = the sim's left/right input during the swing
            foreach ((float ox, float oz, int turn) in new[] { (5f, 0f, 0), (-5f, 0f, 0), (0f, 5f, 0), (0f, -5f, 1), (4f, 4f, -1) })
            {
                var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                using MphRecomp.Campaign.CampaignHost? host = Start(out PlayerEntity me, input);
                if (host == null)
                {
                    fails++;
                    break;
                }
                Console.WriteLine($"  offset ({ox}, {oz}){(turn != 0 ? $" turn {turn}" : "")}:");
                float? fy = Floor(host, anchor.X + ox, anchor.Z + oz);
                if (fy == null)
                {
                    Fail("no floor there");
                    continue;
                }
                if (!PlaceAndAim(host, me, input, new Vector3(anchor.X + ox, fy.Value, anchor.Z + oz)))
                {
                    Fail($"couldn't aim at the point (off {Off(me):0.00})");
                    continue;
                }
                // one tap: the grab is a toggle, the rope holds with the button up
                me.GrappleInput = true;
                int fireStep = -1, pullStep = -1, swingStep = -1, step = 0;
                Vector3 swingStart = default, viewStart = default, viewTop = default;
                var bottoms = new List<(int Step, float Speed)>();
                var tops = new List<(int Step, Vector3 Flat)>();
                float ropeErr = 0;
                float y2 = float.NaN, y1 = float.NaN;
                // the aim pushed hard right or left, then down, mid-swing: it stays inside the view (25 degrees either
                // side, 15 below); held against the side edge the view and the swing plane turn together at the swing's
                // 35 degrees/s (owner 10-31), never faster
                float aimSide = 0, aimPitch = 0, viewRate = 0;
                Vector3 viewPrev = default;
                int aimSign = ox + oz >= 0 ? 1 : -1;
                for (; step < 700 && bottoms.Count < 4; step++)
                {
                    host.Step(input);
                    me.GrappleInput = false;
                    PlayerEntity.GrappleState g = me.Grapple;
                    if (fireStep < 0 && g == PlayerEntity.GrappleState.Firing) fireStep = step;
                    if (pullStep < 0 && g == PlayerEntity.GrappleState.Pull) pullStep = step;
                    if (g == PlayerEntity.GrappleState.Swing)
                    {
                        if (swingStep < 0)
                        {
                            swingStep = step;
                            swingStart = me.Position;
                            viewStart = me.CameraInfo.Facing.WithY(0).Normalized();
                            viewPrev = viewStart;
                            me.GrappleTurnInput = turn;
                        }
                        Vector3 viewNow = me.CameraInfo.Facing.WithY(0).Normalized();
                        viewRate = MathF.Max(viewRate, MathF.Acos(Math.Clamp(Vector3.Dot(viewPrev, viewNow), -1, 1)) * 180 / MathF.PI);
                        viewPrev = viewNow;
                        int into = step - swingStep;
                        input.AimDelta = into >= 10 && into < 60 ? new System.Numerics.Vector2(40 * aimSign, 0)
                            : into >= 60 && into < 90 ? new System.Numerics.Vector2(0, 30) : default;
                        if (into > 10)
                        {
                            Vector3 aim = me.NetAimVector, view = me.CameraInfo.Facing.Normalized();
                            Vector3 af = aim.WithY(0).Normalized(), vf = view.WithY(0).Normalized();
                            aimSide = MathF.Max(aimSide, MathF.Acos(Math.Clamp(Vector3.Dot(af, vf), -1, 1)) * 180 / MathF.PI);
                            float ap = MathF.Asin(Math.Clamp(aim.Y, -1, 1)), vp = MathF.Asin(Math.Clamp(view.Y, -1, 1));
                            aimPitch = MathF.Max(aimPitch, MathF.Abs(ap - vp) * 180 / MathF.PI);
                        }
                        Vector3 d = me.Position - anchor;
                        if (step - swingStep > 4) ropeErr = MathF.Max(ropeErr, MathF.Abs(d.Length - rope));
                        // the lowest and highest points of each pass (the step before the turn round)
                        float y = me.Position.Y;
                        if (!float.IsNaN(y2))
                        {
                            if (y1 < y2 && y1 <= y) bottoms.Add((step - 1, me.Speed.Length));
                            if (y1 > y2 && y1 >= y)
                            {
                                tops.Add((step - 1, d.WithY(0)));
                                viewTop = me.CameraInfo.Facing.WithY(0).Normalized();
                            }
                        }
                        y2 = y1;
                        y1 = y;
                    }
                    else if (swingStep >= 0)
                    {
                        break;
                    }
                }
                if (swingStep < 0)
                {
                    var los = new MphRead.Formats.CollisionResult();
                    bool blocked = MphRead.Formats.CollisionDetection.CheckBetweenPoints(Eye(me), anchor, MphRead.Formats.TestFlags.Players, host.Scene, ref los);
                    Fail($"never swung (state {me.Grapple}, fired {fireStep}, pull {pullStep}) her ({me.Position.X:0.00}, {me.Position.Y:0.00}, {me.Position.Z:0.00}) "
                        + $"dist {(anchor - me.Position).Length / S:0.0}e off {Off(me):0.00} blocked {blocked} hp {me.Health}");
                    continue;
                }
                Vector3 under = anchor.AddY(-rope);
                Console.WriteLine($"    fired {fireStep}, caught {pullStep} ({pullStep - fireStep} steps), swing {swingStep} at {(swingStart - under).Length:0.00} from under the point");
                if ((swingStart - under).Length > 0.5f * S + 0.05f) Fail("the pull didn't end under the point");
                if (me.Grapple != PlayerEntity.GrappleState.Swing)
                {
                    Fail($"let go by itself at step {step} (state {me.Grapple})");
                    continue;
                }
                string Gaps<T>(List<(int Step, T)> l) => string.Join(" ", l.Zip(l.Skip(1), (a, b) => b.Step - a.Step));
                Console.WriteLine($"    bottoms at {string.Join(" ", bottoms.Select(b => b.Step - swingStep))} (gaps {Gaps(bottoms)}), "
                    + $"speed {string.Join(" ", bottoms.Select(b => b.Speed.ToString("0.000")))} (Echoes {maxVel:0.000}); ends gaps {Gaps(tops)}; rope off by up to {ropeErr:0.000}");
                // the period from the ends (where the clock turns her round, to the step); the lowest point is only good to a few
                // steps (near the bottom her height moves less a step than the rope spring's wobble)
                for (int i = 1; i < tops.Count; i++)
                {
                    if (Math.Abs(tops[i].Step - tops[i - 1].Step - half) > 1) Fail($"ends {tops[i - 1].Step}..{tops[i].Step} not {half} steps apart");
                }
                for (int i = 1; i < bottoms.Count; i++)
                {
                    if (Math.Abs(bottoms[i].Step - bottoms[i - 1].Step - half) > 4) Fail($"bottoms {bottoms[i - 1].Step}..{bottoms[i].Step} not about {half} steps apart");
                }
                if (bottoms.Count < 3) Fail($"only {bottoms.Count} bottoms");
                if (bottoms.Any(b => MathF.Abs(b.Speed - maxVel) > 0.05f * maxVel)) Fail("bottom speed isn't Echoes' max");
                if (ropeErr > 0.15f) Fail("the rope stretched");
                if (tops.Count >= 2)
                {
                    Vector3 a = tops[^2].Flat.Normalized(), b = -tops[^1].Flat.Normalized();
                    float deg = MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1, 1)) * 180 / MathF.PI;
                    float want = turn == 0 ? 0 : 35f * (tops[^1].Step - tops[^2].Step) / 60;
                    Console.WriteLine($"    swing plane turned {deg:0.0} deg between the last two tops (want {want:0.0})");
                    if (MathF.Abs(deg - want) > 8) Fail("the swing plane turned wrong");
                }
                else
                {
                    Fail("fewer than two tops");
                }
                // the sideways push turned the view (unless the move stick already turns it the same way: the two
                // don't add), at most 35 degrees/s, and the swing plane kept with the view (as a line)
                {
                    Vector3 a = viewStart, b = me.CameraInfo.Facing.WithY(0).Normalized();
                    float deg = -MathF.Atan2(a.Z * b.X - a.X * b.Z, Vector3.Dot(a, b)) * 180 / MathF.PI;
                    float stick = 35f * turn * (step - swingStep) / 60;
                    // past half a turn the angle wraps: compare on the circle
                    float diff = MathF.Abs(((deg - stick) % 360 + 540) % 360 - 180);
                    Console.WriteLine($"    view turned {deg:0.0} deg over the swing ({diff:0.0} past the move stick's {stick:0.0})");
                    if (turn != aimSign && diff < 15) Fail("pushing the aim to the side didn't turn the view");
                    Console.WriteLine($"    view turned at most {viewRate * 60:0.0} deg/s (swing turn 35)");
                    if (viewRate * 60 > 36) Fail("the view turned faster than the swing");
                    if (tops.Count >= 1)
                    {
                        Vector3 pl = tops[^1].Flat.Normalized();
                        float off = MathF.Acos(Math.Clamp(MathF.Abs(Vector3.Dot(pl, viewTop)), 0, 1)) * 180 / MathF.PI;
                        Console.WriteLine($"    swing plane off the view by {off:0.0} deg at the last top");
                        if (off > 4) Fail("the swing plane didn't follow the view");
                    }
                }
                input.AimDelta = default;
                Console.WriteLine($"    aim off the view at most {aimSide:0.0} deg sideways (want 25), {aimPitch:0.0} up/down (want 15)");
                if (aimSide < 24 || aimSide > 25.5f) Fail("the aim isn't held inside the view sideways");
                if (aimPitch < 14 || aimPitch > 15.5f) Fail("the aim isn't held inside the view up/down");
                // let go just past the bottom: the release jump along the swing (a second tap, or jump)
                me.GrappleTurnInput = 0;
                bool byJump = turn != 0;
                if (byJump) me.GrappleJumpInput = true;
                else me.GrappleInput = true;
                host.Step(input);
                me.GrappleJumpInput = false;
                me.GrappleInput = false;
                Console.WriteLine($"    let go by {(byJump ? "jump" : "a second tap")}");
                float relSpeed = me.Speed.Length;
                Vector3 relPos = me.Position;
                Console.WriteLine($"    let go: state {me.Grapple}, speed {relSpeed:0.000} (swing {maxVel:0.000} + jump {jumpVel:0.000} = {maxVel + jumpVel:0.000})");
                if (me.Grapple != PlayerEntity.GrappleState.None) Fail("didn't let go");
                if (relSpeed < maxVel + 0.6f * jumpVel || relSpeed > maxVel + jumpVel + 0.05f) Fail("release speed off");
                bool landed = false;
                for (int f = 0; f < 600; f++)
                {
                    host.Step(input);
                    if (f == 40)
                    {
                        // after letting go the view has eased back onto the aim
                        float back = MathF.Acos(Math.Clamp(Vector3.Dot(me.NetAimVector, me.CameraInfo.Facing.Normalized()), -1, 1)) * 180 / MathF.PI;
                        Console.WriteLine($"    view {back:0.0} deg off the aim 40 steps after letting go");
                        if (back > 2) Fail("the view didn't ease back onto the aim");
                    }
                    if (me.Flags1.TestFlag(PlayerFlags1.Standing))
                    {
                        landed = true;
                        break;
                    }
                }
                Console.WriteLine($"    landed {landed} at ({me.Position.X:0.00}, {me.Position.Y:0.00}, {me.Position.Z:0.00}), {(me.Position - relPos).Xz.Length:0.00} from the release");
                if (!landed) Fail("didn't land");
            }

            // range: from the floor, a point in view and in sight but over 25 Echoes units away can't be caught; at 20 it can
            foreach (float range in new[] { 26f, 20f })
            {
                var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                using MphRecomp.Campaign.CampaignHost? host = Start(out PlayerEntity me, input);
                if (host == null)
                {
                    fails++;
                    break;
                }
                bool tried = false;
                for (int dir = 0; dir < 16 && !tried; dir++)
                {
                    float ang = dir * MathF.PI / 8;
                    var way = new Vector3(MathF.Cos(ang), 0, MathF.Sin(ang));
                    // the flat distance that puts her (her position, not her eye) range units from the point off that floor
                    float flat = range * S;
                    float? fy = null;
                    for (int it = 0; it < 4; it++)
                    {
                        fy = Floor(host, anchor.X + way.X * flat, anchor.Z + way.Z * flat);
                        if (fy == null) break;
                        float dy = anchor.Y - fy.Value;
                        float f2 = range * S * range * S - dy * dy;
                        if (f2 <= 0)
                        {
                            fy = null;
                            break;
                        }
                        flat = MathF.Sqrt(f2);
                    }
                    if (fy == null) continue;
                    var start = new Vector3(anchor.X + way.X * flat, fy.Value, anchor.Z + way.Z * flat);
                    if (!PlaceAndAim(host, me, input, start)) continue;
                    var los = new MphRead.Formats.CollisionResult();
                    if (MphRead.Formats.CollisionDetection.CheckBetweenPoints(Eye(me), anchor, MphRead.Formats.TestFlags.Players, host.Scene, ref los)) continue;
                    float dist = (anchor - me.Position).Length / S;
                    if (MathF.Abs(dist - range) > 0.5f || !me.Flags1.TestFlag(PlayerFlags1.Standing)) continue;
                    tried = true;
                    me.GrappleInput = true;
                    bool fired = false;
                    for (int f = 0; f < 30 && !fired; f++)
                    {
                        host.Step(input);
                        fired = me.Grapple != PlayerEntity.GrappleState.None;
                    }
                    bool want = range < 25;
                    Console.WriteLine($"  range {dist:0.0} Echoes units (dir {dir}): {(fired ? "fired" : "no grapple")} (want {(want ? "fired" : "no grapple")})");
                    if (fired != want) Fail("range rule");
                }
                if (!tried) Fail($"range {range}: no clear floor spot found in 16 directions");
            }

            // steep: from the floor nearly under the point (more than 70 deg up, Echoes' OrbitUpperAngle) a press doesn't
            // grab, the lock taking only a point it would keep (it used to fire the beam and drop it the next step)
            {
                var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                using MphRecomp.Campaign.CampaignHost? host = Start(out PlayerEntity me, input);
                bool tried = false;
                for (int dir = 0; host != null && dir < 16 && !tried; dir++)
                {
                    float ang = dir * MathF.PI / 8;
                    float flat = 2.0f;
                    float? fy = Floor(host, anchor.X + MathF.Cos(ang) * flat, anchor.Z + MathF.Sin(ang) * flat);
                    if (fy == null || anchor.Y - fy.Value < flat * 2.75f * 1.2f) continue;
                    var start = new Vector3(anchor.X + MathF.Cos(ang) * flat, fy.Value, anchor.Z + MathF.Sin(ang) * flat);
                    if (!PlaceAndAim(host, me, input, start)) continue;
                    tried = true;
                    me.GrappleInput = true;
                    bool fired = false;
                    for (int f = 0; f < 30 && !fired; f++)
                    {
                        host.Step(input);
                        fired = me.Grapple != PlayerEntity.GrappleState.None;
                    }
                    Console.WriteLine($"  steep (dir {dir}, {anchor.Y - fy.Value:0.00} up, {flat:0.00} out): {(fired ? "fired" : "no grapple")} (want no grapple)");
                    Console.WriteLine($"    {me.HostGrappleWhy()}");
                    if (fired) Fail("steep pick");
                }
                if (!tried) Console.WriteLine("  steep: no floor spot under the point (skipped)");
            }
            EchoesGrapple.SetRoomSpots(def.Id, null);
            Console.WriteLine(fails == 0 ? "PASS" : $"FAIL ({fails})");
        }
    }
}
