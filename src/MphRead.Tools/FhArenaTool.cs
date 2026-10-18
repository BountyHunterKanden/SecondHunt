using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Editor;
using MphRead.Entities;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using MphRecomp.Import;
using MphRecomp.Multiplayer;
using Vec3 = OpenTK.Mathematics.Vector3;

namespace MphRead
{
    // First Hunt arenas on the PC (FirstHuntArena; owner 2026-10-13: FH's arenas as MPH-native maps):
    //   -fharena convert nds=<cart dump .nds> root=<import root> [arena]
    //       writes <root>/fharenas/<Key>/ for each arena (as the in-app import will)
    //   -fharena check root=<dir> [arena]     match start per arena x Battle/Survival/Prime Hunter, 300 frames each
    //   -fharena pads root=<dir> [arena]      Samus stood on each jump pad: where MphRead's physics lands her vs the
    //                                         converter's estimate (FirstHuntArena.PadLanding, the bots' map)
    //   -fharena fall root=<dir> [arena]      Samus dropped onto every nav node: any that falls through the floor
    //   -fharena bots root=<dir> [arena] [secs=90] [bots=3]
    //       a Battle with bots (the human seat stands still): falls out of the arena, stuck 2 s windows, kills, pickups;
    //       [arena] may be a native room name (e.g. "MP3 PROVING GROUND") for a baseline
    //   -fharena liftjump root=<dir> [arena] [pad=29]   Samus jumps at a lift pad from the floor around it: from where it fires
    //   -fharena padinfo / padenv / probe / column at=x,z / spot at=x,y,z / dump out=<dir>   pads, floors and the room as data
    internal static class FhArenaTool
    {
        public static void Run(string[] args)
        {
            string verb = args.Length > 1 ? args[1] : "";
            string? root = Arg(args, "root=");
            string? only = args.Skip(2).FirstOrDefault(a => !a.Contains('='));
            Paths.UpdatePaths();
            if (verb == "convert")
            {
                string nds = Arg(args, "nds=") ?? throw new ArgumentException("nds=<First Hunt .nds> needed");
                if (root == null) throw new ArgumentException("root=<import root> needed");
                NdsRom rom = new(File.ReadAllBytes(nds));
                Console.WriteLine($"{rom.Title} {rom.GameCode} v{rom.Version}: {rom.Files.Count} files, First Hunt: {FirstHuntArena.IsFirstHunt(rom)}");
                foreach (FirstHuntArena.Def d in Pick(only)) FirstHuntArena.Import(rom, d, FirstHuntArena.ItemDir(root, d), Console.WriteLine);
                return;
            }
            if (root == null) throw new ArgumentException("root=<import root> needed");
            EchoesArena.ImportRoot = root;
            int n = MatchArenas.All.Count(r => FirstHuntArena.IsFirstHuntRoom(r.Name));
            Console.WriteLine($"{n} First Hunt arenas registered");
            switch (verb)
            {
            case "check": Check(only); break;
            case "pads": Pads(only, root); break;
            case "ballpad": BallPads(only, root, args.Contains("boost")); break;
            case "ballmaze": BallMaze(); break;
            case "ballsize": BallSize(); break;
            case "rawfaces": RawFaces(only, Arg(args, "box=")!.Split(',').Select(float.Parse).ToArray()); break;
            case "fall": Fall(only, root); break;
            case "probe": Probe(only); break;
            case "padinfo": PadInfo(only); break;
            case "padenv": PadEnv(only); break;
            case "column": Column(only, args.Where(a => a.StartsWith("at=")).Select(a => a[3..].Split(',').Select(float.Parse).ToArray()).ToList()); break;
            case "dump": Dump(only, Arg(args, "out=") ?? "fh_dump"); break;
            case "spot": Spot(only, args.Where(a => a.StartsWith("at=")).Select(a => a[3..].Split(',').Select(float.Parse).ToArray()).ToList()); break;
            case "liftjump": LiftJump(only, int.Parse(Arg(args, "pad=") ?? "29")); break;
            case "bots": Bots(only, int.Parse(Arg(args, "secs=") ?? "90"), int.Parse(Arg(args, "bots=") ?? "3")); break;
            default: Console.WriteLine("verbs: convert, check, pads, fall, bots"); break;
            }
        }

        // can Samus reach a lift pad by jumping from the floor around it? (MP2's centre lifts float 2.9 up)
        static void LiftJump(string? only, int padId)
        {
            foreach (FirstHuntArena.Def d in Pick(only))
            {
                RoomMetadata meta = Metadata.GetRoomByName(d.Room).Item1!;
                var pad = Read.GetEntities(meta.EntityPath!, -1, firstHunt: false).First(e => e.Type == EntityType.JumpPad && e.EntityId == padId);
                var ray = new ArenaRaycaster(Tris(meta));
                using CampaignHost host = Start(d.Room, GameMode.Battle, 1, 36000);
                WaitAlive(host);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                PlayerEntity me = PlayerEntity.Main;
                foreach (float r in new[] { 0.8f, 1.2f, 1.6f, 2.0f, 2.5f })
                {
                    var hits = new List<string>();
                    for (int k = 0; k < 8; k++)
                    {
                        var dir = new Vec3(MathF.Cos(k * MathF.PI / 4), 0, MathF.Sin(k * MathF.PI / 4));
                        Vec3 xz = pad.Position + dir * r;
                        if (ray.Cast(new Vec3(xz.X, pad.Position.Y - 0.2f, xz.Z), new Vec3(xz.X, pad.Position.Y - 10, xz.Z)) is not { } h) continue;
                        float fy = pad.Position.Y - 0.2f - 9.8f * h.T;
                        foreach (int mv in new[] { -1, 0, 1, 2, 3, 4, 5, 6, 7 })
                        {
                            WaitAlive(host);
                            for (int f = 0; f < 90 && me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad); f++) host.Step(Idle);
                            me.Reposition(new Vec3(xz.X, fy + 1f, xz.Z) - me.Position, me.NodeRef);
                            me.Speed = Vec3.Zero;
                            for (int f = 0; f < 20; f++) host.Step(Idle);
                            float y0 = me.Position.Y;
                            var input = new CampaignInput { SelectWeapon = BeamType.None, Buttons = CampaignButtons.Jump };
                            if (mv >= 0) input.Move = new System.Numerics.Vector2(MathF.Cos(mv * MathF.PI / 4), MathF.Sin(mv * MathF.PI / 4));
                            bool used = false;
                            float top = 0;
                            for (int f = 0; f < 50 && !used; f++)
                            {
                                host.Step(f < 12 ? input : input with { Buttons = 0 });
                                used = me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad);
                                top = MathF.Max(top, me.Position.Y - y0);
                            }
                            if (used) { hits.Add($"{k * 45}deg mv{mv}"); break; }
                            if (mv == -1 && k == 0) hits.Add($"(straight jump rose {top:0.00} from floor {fy:0.00})");
                        }
                    }
                    Console.WriteLine($"  r {r}: " + string.Join(", ", hits));
                }
            }
        }

        static IEnumerable<FirstHuntArena.Def> Pick(string? only) => FirstHuntArena.All.Where(d => only == null || FirstHuntArena.Find(only) == d);

        static FirstHuntArena.Info InfoOf(string root, FirstHuntArena.Def d) =>
            System.Text.Json.JsonSerializer.Deserialize<FirstHuntArena.Info>(File.ReadAllText(Path.Combine(FirstHuntArena.ItemDir(root, d), "arena.json")))!;

        static CampaignHost Start(string room, GameMode mode, int bots, int seconds)
        {
            var settings = MatchSettings.Quick(mode, room, Hunter.Samus, bots, Math.Min(bots, 2));
            settings.TimeLimitSeconds = seconds;
            settings.PointGoal = 999;
            return CampaignHost.StartMatch(settings);
        }

        static readonly CampaignInput Idle = new() { SelectWeapon = BeamType.None };

        static void WaitAlive(CampaignHost host)
        {
            var press = new CampaignInput { SelectWeapon = BeamType.None, Buttons = CampaignButtons.Shoot | CampaignButtons.Jump };
            for (int f = 0; f < 900 && !(PlayerEntity.Main.Health > 0 && PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Active)); f++) host.Step(f % 2 == 0 ? press : Idle);
        }

        static string F(Vec3 v) => $"({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})";

        static void Check(string? only)
        {
            int failed = 0, runs = 0;
            foreach (FirstHuntArena.Def d in Pick(only))
            {
                foreach (GameMode mode in new[] { GameMode.Battle, GameMode.Survival, GameMode.PrimeHunter })
                {
                    runs++;
                    string result;
                    CampaignHost? host = null;
                    try
                    {
                        var settings = MatchSettings.Quick(mode, d.Room, Hunter.Samus, 3, 1);
                        host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
                        for (int f = 0; f < 300 && !host.Ended; f++) host.Step(Idle);
                        RoomMetadata meta = Metadata.GetRoomById(host.RoomId) ?? throw new Exception($"GetRoomById({host.RoomId}) = null");
                        if (meta.Name != d.Room) throw new Exception($"scene room {meta.Name}");
                        int alive = PlayerEntity.Players.Take(settings.Players.Count).Count(p => p.Health > 0);
                        int items = 0;
                        foreach (ItemInstanceEntity _ in host.Scene.GetItemInstanceEntities()) items++;
                        result = $"ok    id {host.RoomId}, {host.Frame} f, {alive}/{settings.Players.Count} alive, {items} pickups up"
                            + $", {MatchArenas.Census(meta, mode).Spawns} spawns";
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        result = $"FAIL  {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                    }
                    finally
                    {
                        host?.Dispose();
                    }
                    Console.WriteLine($"  {d.InGameName,-28} {mode,-12} {result}");
                }
            }
            Console.WriteLine(failed == 0 ? $"check: all {runs} ok" : $"check: {failed} FAILED");
        }

        // who is alive over the first seconds of a Battle, and where each death happened
        static void Probe(string? only)
        {
            foreach (FirstHuntArena.Def d in Pick(only))
            {
                var settings = MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, 3, 1);
                using CampaignHost host = CampaignHost.StartMatch(settings);
                int seats = settings.Players.Count;
                var was = new bool[seats];
                var last = new Vec3[seats];
                Console.WriteLine($"{d.InGameName}: kill height {Metadata.GetRoomByName(d.Room).Item1!.KillHeight}");
                for (int f = 0; f < 600; f++)
                {
                    host.Step(Idle);
                    for (int i = 0; i < seats; i++)
                    {
                        PlayerEntity p = PlayerEntity.Players[i];
                        bool live = p.LoadFlags.TestFlag(LoadFlags.Active) && p.Health > 0;
                        if (live != was[i]) Console.WriteLine($"  f{f} seat {i}{(settings.Players[i].Bot ? " bot" : "")}: {(live ? "alive at " + F(p.Position) : "gone, last " + F(last[i]) + $" hp {p.Health}")}");
                        was[i] = live;
                        if (live) last[i] = p.Position;
                    }
                }
                for (int i = 0; i < seats; i++) Console.WriteLine($"  seat {i}: {(was[i] ? "alive" : "not alive")} at {F(PlayerEntity.Players[i].Position)} hp {PlayerEntity.Players[i].Health} flags {PlayerEntity.Players[i].LoadFlags}");
            }
        }

        // each pad as First Hunt wrote it and as converted
        static void PadInfo(string? only)
        {
            foreach (FirstHuntArena.Def d in Pick(only))
            {
                RoomMetadata meta = Metadata.GetRoomByName(d.Room).Item1!;
                var mph = Read.GetEntities(meta.EntityPath!, -1, firstHunt: false).Where(e => e.Type == EntityType.JumpPad)
                    .ToDictionary(e => (int)e.EntityId, e => new JumpPadEntityEditor(e, ((Entity<JumpPadEntityData>)e).Data));
                string fh = Path.Combine(Paths.FhFileSystem, "levels", "entities", d.Entities + "_Ent.bin");
                Console.WriteLine($"{d.InGameName}:");
                foreach (Entity e in Read.GetEntitiesFromPath(fh, -1, firstHunt: true).Where(e => e.Type == EntityType.FhJumpPad))
                {
                    FhJumpPadEntityData r = ((Entity<FhJumpPadEntityData>)e).Data;
                    JumpPadEntityEditor? m = mph.GetValueOrDefault(e.EntityId);
                    Console.WriteLine($"  pad {e.EntityId} model {r.ModelId} beam {r.BeamType} flags {r.TriggerFlags} vol {r.VolumeType} lock {r.ControlLockTime} cool {r.CooldownTime} "
                        + $"facing {F(r.Header.FacingVector.ToFloatVector())} up {F(r.Header.UpVector.ToFloatVector())} beamvec {F(r.BeamVector.ToFloatVector())} speed {r.Speed.FloatValue:0.00}");
                    if (m != null) Console.WriteLine($"     -> flags {m.TriggerFlags} beamvec {F(m.BeamVector)} vol {m.Volume.Type} {F(m.Volume.BoxPosition)} {m.Volume.BoxDot1:0.0}x{m.Volume.BoxDot2:0.0}x{m.Volume.BoxDot3:0.0} "
                        + $"cyl {F(m.Volume.CylinderPosition)} r {m.Volume.CylinderRadius:0.0} h {m.Volume.CylinderDot:0.0} sph r {m.Volume.SphereRadius:0.0}");
                }
            }
        }

        // the floors around each lift pad: per ring (radius), the floor heights found by casting down, and whether the way
        // over from the shaft at that height is clear
        static void PadEnv(string? only)
        {
            foreach (FirstHuntArena.Def d in Pick(only))
            {
                RoomMetadata meta = Metadata.GetRoomByName(d.Room).Item1!;
                var ray = new ArenaRaycaster(Tris(meta));
                foreach (Entity e in Read.GetEntities(meta.EntityPath!, -1, firstHunt: false).Where(e => e.Type == EntityType.JumpPad))
                {
                    var pad = new JumpPadEntityEditor(e, ((Entity<JumpPadEntityData>)e).Data);
                    Console.WriteLine($"{d.InGameName} pad {pad.Id} at {F(pad.Position)} speed {pad.Speed:0.00}; ceiling above: "
                        + (ray.Cast(pad.Position + Vec3.UnitY * 0.5f, pad.Position + Vec3.UnitY * 30) is { } c ? $"{pad.Position.Y + 0.5f + 29.5f * c.T:0.0}" : "none"));
                    for (float r = 1f; r <= 10f; r += 1f)
                    {
                        var levels = new SortedDictionary<float, int>();
                        for (int a = 0; a < 24; a++)
                        {
                            float t = a * MathF.PI / 12;
                            float top = pad.Position.Y + 15;
                            var col = new Vec3(pad.Position.X + r * MathF.Cos(t), 0, pad.Position.Z + r * MathF.Sin(t));
                            while (top > pad.Position.Y - 1)
                            {
                                var from = new Vec3(col.X, top, col.Z);
                                if (ray.Cast(from, new Vec3(col.X, pad.Position.Y - 1, col.Z)) is not { } h) break;
                                float y = top - (top - (pad.Position.Y - 1)) * h.T;
                                if (h.Normal.Y >= 0.7f) { float k = MathF.Round(y * 2) / 2; levels[k] = levels.GetValueOrDefault(k) + 1; }
                                top = y - 0.05f;
                            }
                        }
                        Console.WriteLine($"   r {r,4:0}: " + string.Join("  ", levels.Select(kv => $"y {kv.Key:0.0} x{kv.Value}")));
                    }
                }
            }
        }

        // the room around points: floor below, ceiling above, and the clear distance level in 8 directions at knee and chest
        static void Spot(string? only, List<float[]> points)
        {
            foreach (FirstHuntArena.Def d in Pick(only))
            {
                var ray = new ArenaRaycaster(Tris(Metadata.GetRoomByName(d.Room).Item1!));
                foreach (float[] a in points)
                {
                    var p = new Vec3(a[0], a[1], a[2]);
                    float? Dist(Vec3 from, Vec3 dir, float max) => ray.Cast(from, from + dir * max) is { } h ? h.T * max : null;
                    Console.WriteLine($"{F(p)}: floor {(Dist(p + Vec3.UnitY, -Vec3.UnitY, 20) is float fl ? $"{p.Y + 1 - fl:0.00}" : "none")} ceiling {(Dist(p, Vec3.UnitY, 20) is float c ? $"{p.Y + c:0.00}" : "none")}");
                    foreach (float h in new[] { 0.3f, 1.2f })
                    {
                        Console.WriteLine($"   +{h}: " + string.Join("  ", Enumerable.Range(0, 8).Select(k =>
                        {
                            var dir = new Vec3(MathF.Cos(k * MathF.PI / 4), 0, MathF.Sin(k * MathF.PI / 4));
                            return $"{k * 45}deg {(Dist(p + Vec3.UnitY * h, dir, 10) is float x ? x.ToString("0.0") : "-")}";
                        })));
                    }
                }
            }
        }

        // the room as data for a top-down picture (scripts/fhmap.py): collision triangles, the bots' nodes, pads, spawns, items
        static void Dump(string? only, string outDir)
        {
            Directory.CreateDirectory(outDir);
            foreach (FirstHuntArena.Def d in Pick(only))
            {
                RoomMetadata meta = Metadata.GetRoomByName(d.Room).Item1!;
                using CampaignHost host = Start(d.Room, GameMode.Battle, 1, 36000);
                var nodes = host.Scene.Room?.NodeData?.Data.SelectMany(s => s.SelectMany(l => l)).Select(n => new[] { n.Position.X, n.Position.Y, n.Position.Z }).ToList();
                var ents = Read.GetEntities(meta.EntityPath!, -1, firstHunt: false);
                float[] P(Vec3 v) => new[] { v.X, v.Y, v.Z };
                var data = new
                {
                    tris = Tris(meta).Select(t => new[] { t.Item1.X, t.Item1.Y, t.Item1.Z, t.Item2.X, t.Item2.Y, t.Item2.Z, t.Item3.X, t.Item3.Y, t.Item3.Z }),
                    nodes,
                    pads = ents.Where(e => e.Type == EntityType.JumpPad).Select(e => P(e.Position)),
                    spawns = ents.Where(e => e.Type == EntityType.PlayerSpawn).Select(e => P(e.Position)),
                    items = ents.Where(e => e.Type == EntityType.ItemSpawn).Select(e => P(e.Position)),
                };
                File.WriteAllText(Path.Combine(outDir, d.Key + ".json"), System.Text.Json.JsonSerializer.Serialize(data));
                Console.WriteLine($"{d.InGameName}: {nodes?.Count} nodes -> {Path.Combine(outDir, d.Key + ".json")}");
            }
        }

        // every surface down a column (as ArenaNav's floor sampling walks it): height, normal, headroom
        static void Column(string? only, List<float[]> points)
        {
            foreach (FirstHuntArena.Def d in Pick(only))
            {
                var ray = new ArenaRaycaster(Tris(Metadata.GetRoomByName(d.Room).Item1!));
                foreach (float[] a in points)
                {
                    float top = ray.Max.Y + 1;
                    var line = new List<string>();
                    while (top > ray.Min.Y && line.Count < 20)
                    {
                        var from = new Vec3(a[0], top, a[1]);
                        if (ray.Cast(from, new Vec3(a[0], ray.Min.Y - 1, a[1])) is not { } h) break;
                        float y = top - (top - (ray.Min.Y - 1)) * h.T;
                        var floor = new Vec3(a[0], y, a[1]);
                        bool room = ray.Cast(floor + Vec3.UnitY * 0.05f, floor + Vec3.UnitY * 1.9f) == null;
                        line.Add($"y {y:0.00} n.y {h.Normal.Y:0.00}{(room ? "" : " LOW")}");
                        top = y - 0.05f;
                    }
                    Console.WriteLine($"({a[0]}, {a[1]}): " + string.Join(" | ", line));
                }
            }
        }

        static void Pads(string? only, string root)
        {
            foreach (FirstHuntArena.Def d in Pick(only))
            {
                RoomMetadata meta = Metadata.GetRoomByName(d.Room).Item1!;
                List<JumpPadEntityEditor> pads = Read.GetEntities(meta.EntityPath!, -1, firstHunt: false)
                    .Where(e => e.Type == EntityType.JumpPad).Select(e => new JumpPadEntityEditor(e, ((Entity<JumpPadEntityData>)e).Data)).ToList();
                if (pads.Count == 0) continue;
                FirstHuntArena.Info info = InfoOf(root, d);
                Console.WriteLine($"{d.InGameName}: {pads.Count} pads");
                using CampaignHost host = Start(d.Room, GameMode.Battle, 1, 36000);
                WaitAlive(host);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                PlayerEntity me = PlayerEntity.Main;
                var ray = new ArenaRaycaster(Tris(meta));
                foreach (JumpPadEntityEditor pad in pads)
                {
                    WaitAlive(host);
                    for (int f = 0; f < 60 && me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad); f++) host.Step(Idle);
                    me.Reposition(pad.Position + Vec3.UnitY * 0.3f - me.Position, me.NodeRef);
                    me.Speed = Vec3.Zero;
                    Vec3 from = me.Position;
                    var real = new List<string>();
                    int frames = 0, launched = -1;
                    float apex = 0;
                    string end = "still flying after 20 s";
                    for (; frames < 1200; frames++)
                    {
                        host.Step(Idle);
                        if (launched < 0 && me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)) launched = frames;
                        apex = MathF.Max(apex, me.Position.Y - from.Y);
                        if (Environment.GetEnvironmentVariable("PAD_TRACE") == pad.Id.ToString() && launched >= 0 && (frames - launched) % 12 == 0) real.Add($"{(frames - launched) / 2}:{F(me.Position)}");
                        if (me.Health == 0 || me.Position.Y < info.KillHeight) { end = "DIED"; break; }
                        if (launched >= 0 && frames > launched + 8 && me.Flags1.TestFlag(PlayerFlags1.Standing) && MathF.Abs(me.Speed.Y) < 0.05f) { end = "landed"; break; }
                        if (launched < 0 && frames > 60) { end = "never launched"; break; }
                    }
                    var trace = new List<string>();
                    FirstHuntArena.Trace = (f, p) => { if (f % 6 == 0) trace.Add($"{f}:{F(p)}"); };
                    Vec3? est = FirstHuntArena.PadLanding(ray, pad, info.KillHeight, pads);
                    FirstHuntArena.Trace = null;
                    if (Environment.GetEnvironmentVariable("PAD_TRACE") == pad.Id.ToString()) Console.WriteLine("    estimate path " + string.Join(" ", trace));
                    if (real.Count > 0) Console.WriteLine("    measured path " + string.Join(" ", real));
                    Vec3 launch = pad.BeamVector.Normalized() * pad.Speed;
                    Console.WriteLine($"  pad {pad.Id} at {F(pad.Position)} launch {F(launch)} {pad.Speed:0.00}/f lock {pad.ControlLockTime}: {end} at {F(me.Position)} "
                        + $"after {frames} f, rose {apex:0.0}; estimate {(est is Vec3 e ? F(e) + $" ({(e - me.Position).Length:0.0} off)" : "none")}");
                }
            }
        }

        // the morph-ball-only pads (MP1's tunnel boosts): Samus morphed and set on each, rolling with MPH's ball physics until
        // she stops; how far she got vs the open run ahead of the pad (a ray at ball height along the push)
        // Regulator's ball maze by the spawn (lid underside y 12.38, floor 11.68, opening at x 204.1): morph Samus at its mouth,
        // roll in along +x, with First Hunt's ball rules (PlayerEntity.HostFhBallRules) as the room load set them and then off.
        // BizHawk on the cart (out/fh/maze3): lined up with the slot (z -54.8) the ball rolls under the lid to x 205.86 (the maze's
        // first turn); off the slot the maze wall stops it at x 203.66
        static void BallMaze()
        {
            FirstHuntArena.Def d = FirstHuntArena.All.First(a => a.Key == "Regulator");
            using CampaignHost host = Start(d.Room, GameMode.Battle, 1, 36000);
            WaitAlive(host);
            if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
            PlayerEntity me = PlayerEntity.Main;
            bool loaded = PlayerEntity.HostFhBallRules;
            Console.WriteLine($"Regulator loaded: HostFhBallRules {loaded}");
            // coasting from one push (wall bumps count): x of the line in, the opening's z range, start x; at full speed then at half
            foreach (var (name, zs, x0, xIn) in new[] { ("slot", (-55.3f, -54.2f), 201.0f, 204.6f), ("hole", (-55.5f, -54.2f), 193.6f, 196.8f) })
            {
                foreach (float sp in new[] { 0.32f, 0.2f })
                {
                    foreach (bool on in new[] { loaded, false })
                    {
                        var row = new List<string>();
                        for (float z0 = zs.Item2 + 0.1f; z0 >= zs.Item1 - 0.11f; z0 -= 0.1f)
                        {
                            foreach (float vz in new[] { 0f, 0.05f, -0.05f })
                            {
                                PlayerEntity.HostFhBallRules = on;
                                WaitAlive(host);
                                for (int i = 0; i < 120 && !me.IsAltForm; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = i < 4 ? CampaignButtons.Morph : CampaignButtons.None });
                                host.PlacePlayer(new Vec3(x0, 11.8f, z0 - vz / sp * (xIn - 0.6f - x0)), Vec3.UnitX);
                                me.Speed = Vec3.Zero;
                                for (int f = 0; f < 60; f++) host.Step(Idle);
                                me.Speed = new Vec3(sp / 2, 0, vz / 2);
                                float bx = 0;
                                for (int f = 0; f < 150; f++)
                                {
                                    host.Step(Idle);
                                    bx = MathF.Max(bx, me.Position.X);
                                }
                                row.Add(bx > xIn ? "I" : ".");
                            }
                            row.Add(" ");
                        }
                        Console.WriteLine($"  coast {name} speed {sp:0.00} rules {(on ? "on " : "off")} z {zs.Item2 + 0.1f:0.0}..{zs.Item1 - 0.1f:0.0} (vz 0,+,-): " + string.Join("", row));
                    }
                }
            }
            // the hexagonal ball hole through the wall between the spawn (x 191) and the maze funnel: x 195.3..196.3, floor 11.74
            // (a 0.09 lip) 1.3 wide at z -55.5..-54.2, sides at 60 deg up to 12.84 (z -56.1..-53.6), roof 13.93
            foreach (bool on in new[] { loaded, false })
            {
                var row = new List<string>();
                string trace = "";
                for (float z0 = -54.1f; z0 >= -55.61f; z0 -= 0.1f)
                {
                    PlayerEntity.HostFhBallRules = on;
                    WaitAlive(host);
                    for (int i = 0; i < 120 && !me.IsAltForm; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = i < 4 ? CampaignButtons.Morph : CampaignButtons.None });
                    host.PlacePlayer(new Vec3(192.6f, 11.8f, z0), Vec3.UnitX);
                    me.Speed = Vec3.Zero;
                    for (int f = 0; f < 60; f++) host.Step(Idle);
                    float bx = 0;
                    for (int f = 0; f < 120; f++)
                    {
                        me.Speed = new Vec3(0.3f / 2, me.Speed.Y, 0);
                        host.Step(Idle);
                        bx = MathF.Max(bx, me.Position.X);
                        if (MathF.Abs(z0 + 54.8f) < 0.01f && f % 3 == 0 && f < 60) trace += $"{me.Position.X:0.00},{me.Position.Y:0.00},{me.Position.Z:0.00} ";
                    }
                    row.Add($"{z0:0.0}:{(bx > 197f ? "IN" : $"x{bx:0.00}")}");
                }
                Console.WriteLine($"  hole, rules {(on ? "on " : "off")}: " + string.Join(" ", row));
                Console.WriteLine("    z -54.8: " + trace);
            }
            foreach (bool on in new[] { loaded, false })
            {
                foreach (bool stick in new[] { false, true }) {
                var grid = new List<string>();
                foreach (float vz in new[] { 0f, -0.06f, 0.06f })
                {
                    var row = new List<string>();
                    for (float z0 = -54.3f; z0 >= -55.31f; z0 -= 0.1f)
                    {
                        PlayerEntity.HostFhBallRules = on;
                        WaitAlive(host);
                        for (int i = 0; i < 120 && !me.IsAltForm; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = i < 4 ? CampaignButtons.Morph : CampaignButtons.None });
                        // start so the line of travel crosses x 204.1 at z0
                        host.PlacePlayer(new Vec3(200.6f, 11.9f, z0 - vz / 0.3f * 3.5f), Vec3.UnitX);
                        me.Speed = Vec3.Zero;
                        for (int f = 0; f < 90; f++) host.Step(Idle);
                        float bx = 0;
                        for (int f = 0; f < 160; f++)
                        {
                            if (stick && f >= 8) host.Step(new CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(vz * 3, 1) });
                            else
                            {
                                me.Speed = new Vec3(0.3f / 2, me.Speed.Y, vz / 2);
                                host.Step(Idle);
                            }
                            bx = MathF.Max(bx, me.Position.X);
                            if (stick && vz == 0 && MathF.Abs(z0 + 54.8f) < 0.01f && f % 4 == 0 && f < 100) Console.Write($"{me.Position.X:0.00},{me.Position.Z:0.00} ");
                        }
                        row.Add($"{z0:0.0}:{(bx > 204.5f ? "IN" : "--")}{(stick && (z0 > -54.35f || z0 < -55.25f) ? $"(x{me.Position.X:0.0} z{me.Position.Z:0.0})" : "")}");
                    }
                    grid.Add($"vz {vz:+0.00;-0.00}: " + string.Join(" ", row));
                }
                Console.WriteLine($"  rules {(on ? "on " : "off")} slot z -55.3..-54.2, crossing z at x 204.1 -> in?");
                Console.WriteLine(stick ? "   (stick forward, camera along +x)" : "   (speed set along +x)");
                foreach (string g in grid) Console.WriteLine("    " + g);
                }
            }
            foreach (bool on in new[] { loaded, false })
            {
                PlayerEntity.HostFhBallRules = on;
                WaitAlive(host);
                for (int i = 0; i < 120 && !me.IsAltForm; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = i < 4 ? CampaignButtons.Morph : CampaignButtons.None });
                for (int f = 0; f < 30; f++) host.Step(Idle);
                host.PlacePlayer(new Vec3(201.6f, 11.9f, -54.8f), Vec3.UnitX);
                me.Speed = Vec3.Zero;
                for (int f = 0; f < 30; f++) host.Step(Idle);
                Vec3 start = me.Position;
                float best = start.X;
                var path = new List<string>();
                for (int f = 0; f < 240; f++)
                {
                    me.Speed = new Vec3(0.3f / 2, me.Speed.Y, me.Speed.Z); // the cart's roll-in speed (0.25-0.31 a 30 Hz tick), along +x
                    host.Step(Idle);
                    best = MathF.Max(best, me.Position.X);
                    if (f % 4 == 0 && f < 80) path.Add($"{me.Position.X:0.00}/{me.Position.Y:0.00}/{me.Position.Z:0.00}{(me.Flags1.TestFlag(PlayerFlags1.Standing) ? "s" : "")}{(me.Flags1.TestFlag(PlayerFlags1.StandingPrevious) ? "p" : "")}");
                }
                Console.WriteLine($"  rules {(on ? "on " : "off")}: from {F(start)} furthest x {best:0.00}, end {F(me.Position)} alt {me.IsAltForm}");
                Console.WriteLine("    x/y/z every 2 ticks " + string.Join(" ", path));
            }
        }

        static void BallPads(string? only, string root, bool boost = false)
        {
            foreach (FirstHuntArena.Def d in Pick(only))
            {
                RoomMetadata meta = Metadata.GetRoomByName(d.Room).Item1!;
                List<JumpPadEntityEditor> pads = Read.GetEntities(meta.EntityPath!, -1, firstHunt: false)
                    .Where(e => e.Type == EntityType.JumpPad).Select(e => new JumpPadEntityEditor(e, ((Entity<JumpPadEntityData>)e).Data))
                    .Where(p => !p.TriggerFlags.HasFlag(MphRead.Entities.TriggerFlags.PlayerBiped)).ToList();
                if (pads.Count == 0) continue;
                FirstHuntArena.Info info = InfoOf(root, d);
                Console.WriteLine($"{d.InGameName}: {pads.Count} ball pads");
                using CampaignHost host = Start(d.Room, GameMode.Battle, 1, 36000);
                WaitAlive(host);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                PlayerEntity me = PlayerEntity.Main;
                var ray = new ArenaRaycaster(Tris(meta));
                foreach (JumpPadEntityEditor pad in pads)
                {
                    WaitAlive(host);
                    for (int i = 0; i < 120 && !me.IsAltForm; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = i < 4 ? CampaignButtons.Morph : CampaignButtons.None });
                    for (int f = 0; f < 120 && me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad); f++) host.Step(Idle);
                    for (int f = 0; f < 40; f++) host.Step(Idle);
                    me.Reposition(pad.Position + Vec3.UnitY * 0.3f - me.Position, me.NodeRef);
                    me.Speed = Vec3.Zero;
                    Vec3 from = me.Position;
                    if (boost)
                    {
                        // arriving boosted: top speed 0.6 a tick (FH's boost floor, BizHawk out/fh/pad_vmax_long) and rolling at it
                        Vec3 dirIn = new Vec3(pad.BeamVector.X, 0, pad.BeamVector.Z).Normalized();
                        me.Speed = dirIn * 0.6f;
                        typeof(PlayerEntity).GetField("_hSpeedCap", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(me, 0.6f);
                    }
                    Vec3 push = pad.BeamVector.Normalized() * pad.Speed;
                    Vec3 dir = new Vec3(push.X, 0, push.Z).Normalized();
                    float open = ray.Cast(pad.Position + Vec3.UnitY * 0.5f, pad.Position + Vec3.UnitY * 0.5f + dir * 60) is { } hit ? 60 * hit.T : 60;
                    var path = new List<string>();
                    int frames = 0, launched = -1, still = 0, lockEnd = -1;
                    float best = 0;
                    for (; frames < 1200; frames++)
                    {
                        host.Step(Idle);
                        if (boost && frames < 0) Console.Write($"[{frames} cap {(float)typeof(PlayerEntity).GetField("_hSpeedCap", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(me)!:0.000} v {me.Speed.Xz.Length:0.000} pad {me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)}] ");
                        if (launched < 0 && me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)) launched = frames;
                        float along = (me.Position - from).Xz.Length; // (the editor's beam vector is local to the pad, so not the flight line)
                        best = MathF.Max(best, along);
                        if (launched >= 0 && (frames - launched) % 6 == 0) path.Add($"{(frames - launched) / 2}:{along:0.0}/{me.Position.Y - from.Y:0.0}{(me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad) ? "p" : "")}");
                        if (launched >= 0 && lockEnd < 0 && !me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)) lockEnd = (frames - launched) / 2;
                        if (me.Health == 0 || me.Position.Y < info.KillHeight) break;
                        still = launched >= 0 && me.Speed.Xz.Length < 0.01f ? still + 1 : 0;
                        if (still > 20 || launched < 0 && frames > 60) break;
                    }
                    Console.WriteLine($"  pad {pad.Id} at {F(pad.Position)} push {F(push)} {pad.Speed:0.00}/tick lock {pad.ControlLockTime}: "
                        + (launched < 0 ? "never launched" : $"rolled {best:0.0} along (stopped at {F(me.Position)}, pad flight ended tick {lockEnd})") + $"; open run ahead {open:0.0}");
                    Console.WriteLine("    ticks:along/rise " + string.Join(" ", path.Take(40)));
                }
            }
        }

        // the cart's own collision faces (FH format, before conversion) with any point inside box=x0,y0,z0,x1,y1,z1, next to the
        // converted MPH collision's faces there: normal, y range, x/z range
        static void RawFaces(string? only, float[] b)
        {
            foreach (FirstHuntArena.Def d in Pick(only))
            {
                bool In(Vec3 p) => p.X >= b[0] && p.X <= b[3] && p.Y >= b[1] && p.Y <= b[4] && p.Z >= b[2] && p.Z <= b[5];
                string Face(IList<Vec3> pts, Vec3 n) => $"n {F(n)} y {pts.Min(p => p.Y):0.00}..{pts.Max(p => p.Y):0.00} x {pts.Min(p => p.X):0.0}..{pts.Max(p => p.X):0.0} z {pts.Min(p => p.Z):0.0}..{pts.Max(p => p.Z):0.0}";
                var raw = (Formats.Collision.FhCollisionInfo)Formats.Collision.Collision.GetCollision(Metadata.RoomMetadata[d.Source], roomLayerMask: -1).Info;
                var rawFaces = new List<string>();
                foreach (Formats.Collision.FhCollisionData data in raw.Data)
                {
                    var pts = Enumerable.Range(0, data.VectorCount).Select(j => raw.Points[raw.Vectors[data.VectorStartIndex + j].Point2Index]).ToList();
                    if (!pts.Any(In)) continue;
                    var pl = raw.Planes[data.PlaneIndex];
                    rawFaces.Add(Face(pts, pl.Xyz));
                }
                Console.WriteLine($"{d.InGameName}: cart collision {rawFaces.Count} faces in the box");
                foreach (string f in rawFaces.OrderBy(f => f)) Console.WriteLine("  " + f);
                var conv = new List<string>();
                foreach (var (a, c, e) in Tris(Metadata.GetRoomByName(d.Room).Item1!))
                {
                    if (!In(a) && !In(c) && !In(e)) continue;
                    conv.Add(Face(new[] { a, c, e }, Vec3.Cross(c - a, e - a).Normalized()));
                }
                Console.WriteLine($"  converted: {conv.Count} triangles in the box");
            }
        }

        // First Hunt's morph ball model vs MPH Samus's: node bounds (model units, before the model scale) and the scale
        static void BallSize()
        {
            foreach ((string name, bool fh) in new[] { ("morphBall", true), ("SamusAlt_lod0", false), ("samus_hi_yellow", true), ("Samus_lod0", false), ("blueRoom", true), ("mp1", true), ("mp3", true) })
            {
                ModelInstance inst = Read.GetModelInstance(name, firstHunt: fh);
                Model m = inst.Model;
                Console.WriteLine($"{(fh ? "FH " : "MPH")} {name}: scale {m.Scale}");
                foreach (Node n in m.Nodes.Where(n => n.MeshCount > 0 || n.BoundingRadius > 0).Take(6))
                    Console.WriteLine($"   node {n.Name}: radius {n.BoundingRadius:0.000} min {F(n.MinBounds)} max {F(n.MaxBounds)} pos {F(n.Position)}");
            }
        }

        // the room's collision as triangles (what the converter's ArenaRaycaster saw), read back from the MPH file
        static List<(Vec3, Vec3, Vec3)> Tris(RoomMetadata meta)
        {
            var col = Formats.Collision.Collision.GetCollision(meta, roomLayerMask: -1);
            var tris = new List<(Vec3, Vec3, Vec3)>();
            var info = (Formats.Collision.MphCollisionInfo)col.Info;
            foreach (Formats.Collision.CollisionData data in info.Data)
            {
                var pts = Enumerable.Range(0, data.PointIndexCount).Select(i => info.Points[info.PointIndices[data.PointStartIndex + i]]).ToList();
                for (int j = 1; j + 1 < pts.Count; j++) tris.Add((pts[0], pts[j], pts[j + 1]));
            }
            return tris;
        }

        static void Fall(string? only, string root)
        {
            foreach (FirstHuntArena.Def d in Pick(only))
            {
                RoomMetadata meta = Metadata.GetRoomByName(d.Room).Item1!;
                FirstHuntArena.Info info = InfoOf(root, d);
                using CampaignHost host = Start(d.Room, GameMode.Battle, 1, 36000);
                WaitAlive(host);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                PlayerEntity me = PlayerEntity.Main;
                var ray = new ArenaRaycaster(Tris(meta));
                // floor spots: every 2 units across the arena, each walkable level of the column (as ArenaNav samples)
                var spots = new List<Vec3>();
                for (float x = ray.Min.X + 1; x < ray.Max.X; x += 2)
                {
                    for (float z = ray.Min.Z + 1; z < ray.Max.Z; z += 2)
                    {
                        float top = ray.Max.Y + 1;
                        while (top > ray.Min.Y)
                        {
                            if (ray.Cast(new Vec3(x, top, z), new Vec3(x, ray.Min.Y - 1, z)) is not { } h) break;
                            float y = top - (top - (ray.Min.Y - 1)) * h.T;
                            if (h.Normal.Y >= 0.7f && ray.Cast(new Vec3(x, y + 0.05f, z), new Vec3(x, y + 1.9f, z)) == null) spots.Add(new Vec3(x, y, z));
                            top = y - 0.05f;
                        }
                    }
                }
                int fell = 0, tested = 0;
                var bad = new List<string>();
                foreach (Vec3 s in spots)
                {
                    WaitAlive(host);
                    tested++;
                    me.Reposition(s + Vec3.UnitY * 1.0f - me.Position, me.NodeRef);
                    me.Speed = Vec3.Zero;
                    for (int f = 0; f < 20; f++) host.Step(Idle);
                    if (me.Health == 0 || me.Position.Y < s.Y - 1.5f)
                    {
                        fell++;
                        if (bad.Count < 12) bad.Add($"{F(s)} -> {F(me.Position)}");
                    }
                }
                Console.WriteLine($"{d.InGameName}: {tested} floor spots dropped onto, {fell} fell through" + (bad.Count > 0 ? ":\n    " + string.Join("\n    ", bad) : ""));
            }
        }

        static void Bots(string? only, int seconds, int bots)
        {
            const int Window = 120;
            int trace = int.Parse(Environment.GetEnvironmentVariable("BOT_TRACE") ?? "0"), traced = 0;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            System.Reflection.FieldInfo node40 = typeof(PlayerEntity.PlayerAiData).GetField("_node40", flags)!;
            System.Reflection.FieldInfo node3C = typeof(PlayerEntity.PlayerAiData).GetField("_node3C", flags)!;
            // a native MPH room by name (e.g. "MP3 PROVING GROUND") for a baseline
            var rooms = FirstHuntArena.Find(only ?? "") == null && only != null ? new[] { only } : Pick(only).Select(d => d.Room).ToArray();
            foreach (string roomName in rooms)
            {
                RoomMetadata meta = Metadata.GetRoomByName(roomName).Item1!;
                var d = (InGameName: meta.InGameName ?? roomName, Room: roomName);
                var settings = MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, bots, 2);
                settings.TimeLimitSeconds = seconds + 30;
                settings.PointGoal = 999;
                using CampaignHost host = CampaignHost.StartMatch(settings);
                int seats = settings.Players.Count;
                var path = new float[seats];
                var last = new Vec3[seats];
                var alive = new bool[seats];
                var wasAlive = new bool[seats];
                int windows = 0, stuck = 0, falls = 0, deaths = 0;
                var spots = new Dictionary<(int, int, int), int>();
                var fallAt = new List<string>();
                var picked = new Dictionary<ItemType, int>();
                var up = new HashSet<ItemInstanceEntity>();
                for (int i = 0; i < seats; i++) { last[i] = PlayerEntity.Players[i].Position; alive[i] = true; }
                for (int f = 1; f <= seconds * 60 && !host.Ended; f++)
                {
                    host.Step(Idle);
                    var now = new HashSet<ItemInstanceEntity>();
                    foreach (ItemInstanceEntity it in host.Scene.GetItemInstanceEntities()) now.Add(it);
                    foreach (ItemInstanceEntity gone in up.Where(u => !now.Contains(u))) picked[gone.ItemType] = picked.GetValueOrDefault(gone.ItemType) + 1;
                    up = now;
                    for (int i = 0; i < seats; i++)
                    {
                        PlayerEntity p = PlayerEntity.Players[i];
                        bool live = p.LoadFlags.TestFlag(LoadFlags.Active) && p.Health > 0;
                        if (wasAlive[i] && !live)
                        {
                            deaths++;
                            if (last[i].Y < meta.KillHeight + 3)
                            {
                                falls++;
                                if (fallAt.Count < 8) fallAt.Add(F(last[i]));
                            }
                        }
                        wasAlive[i] = live;
                        if (!live) alive[i] = false;
                        else path[i] += (p.Position - last[i]).Length;
                        last[i] = p.Position;
                    }
                    if (f % Window != 0) continue;
                    for (int i = 0; i < seats; i++)
                    {
                        if (settings.Players[i].Bot && alive[i] && f > Window)
                        {
                            windows++;
                            if (path[i] < 3)
                            {
                                stuck++;
                                if (traced < trace)
                                {
                                    traced++;
                                    PlayerEntity bp = PlayerEntity.Players[i];
                                    var n40 = node40.GetValue(bp.AiData!) as MphRead.Formats.NodeData3;
                                    var n3C = node3C.GetValue(bp.AiData!) as MphRead.Formats.NodeData3;
                                    Console.WriteLine($"    stuck f{f} seat {i} at {F(bp.Position)} speed {F(bp.Speed)} alt {bp.IsAltForm} target {n40?.Id} {(n40 != null ? F(n40.Position) : "")} "
                                        + $"dest {n3C?.Id} {(n3C != null ? F(n3C.Position) : "")} ai {bp.AiData!.HostDebugState()} others " + string.Join(" ", Enumerable.Range(0, seats).Where(o => o != i).Select(o => F(PlayerEntity.Players[o].Position) + (settings.Players[o].Bot ? "b" : "H"))));
                                }
                                Vec3 q = PlayerEntity.Players[i].Position;
                                var key = ((int)MathF.Round(q.X / 4) * 4, (int)MathF.Round(q.Y / 4) * 4, (int)MathF.Round(q.Z / 4) * 4);
                                spots[key] = spots.GetValueOrDefault(key) + 1;
                            }
                        }
                        path[i] = 0;
                        alive[i] = true;
                    }
                }
                int kills = Enumerable.Range(0, seats).Sum(i => GameState.Kills[i]);
                Console.WriteLine($"  {d.InGameName,-28} {bots} bots, {host.Frame / 60} s: stuck {stuck} of {windows} bot windows ({(windows > 0 ? 100.0 * stuck / windows : 0):0}%), "
                    + $"kills {kills}, deaths {deaths}, fell out {falls}" + (fallAt.Count > 0 ? " (" + string.Join(", ", fallAt) + ")" : "")
                    + "; picked up: " + string.Join(", ", picked.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}"))
                    + (spots.Count > 0 ? "; stuck spots " + string.Join(", ", spots.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} x{kv.Value}")) : ""));
            }
        }

        static string? Arg(string[] args, string key) => args.FirstOrDefault(a => a.StartsWith(key, StringComparison.OrdinalIgnoreCase))?[key.Length..];
    }
}
