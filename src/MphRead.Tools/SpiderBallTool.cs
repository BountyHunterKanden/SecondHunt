using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Arenas;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRead
{
    // the spider ball's dev check (docs/SPIDER_BALL.md, owner 2026-11-06):
    //   -spiderball [arena] [hunters=Samus,Spire,...] [frames=300]
    //   -spiderball bots [arena] [frames=18000] [bots=3]
    //   -spiderball drone [arena]
    // per Echoes arena with spider track: the track triangle count, then every hunter's alt form put on a few track
    // faces (sideways, facing up, facing down): spider held + forward (stays on, moves), spider held + no input (locks in
    // place), spider let go (falls off)
    internal static class SpiderBallTool
    {
        // -spiderball fx [disc=<echoes disc>]: which paks hold Echoes' spider look (PART 22b005a1, SWHC 4735d6e1) and
        // whether each pak has the whole dependency tree
        static void FxProbe(string[] args)
        {
            string disc = args.FirstOrDefault(a => a.StartsWith("disc="))?[5..] ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "brawl_extract", "prime", "echoes_retail.rvz"));
            using IFileSource src = DiscImage.Open(disc);
            foreach (string path in src.Paths.Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
            {
                byte[] bytes = src.ReadFile(path);
                var pk = new Pak(bytes, path);
                if (!EchoesSpider.FxRoots.Any(r => pk.Contains(r))) continue;
                Console.WriteLine($"{path}: sha1 {Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)).ToLowerInvariant()}");
                var notes = new List<string>();
                int files = EchoesSpider.FxWalk(pk, EchoesSpider.FxRoots, null, notes);
                Console.WriteLine($"{path}: {files} files; {string.Join("; ", notes)}");
                static string Show(Part.Elem? e) => e == null ? "-" : e.Kind + (e.Cat switch
                {
                    Part.Cat.Real when e.Kind == "CNST" => $" {e.F:0.###}",
                    Part.Cat.Int when e.Kind == "CNST" => $" {e.I}",
                    Part.Cat.Bool => $" {e.B}",
                    Part.Cat.Texture or Part.Cat.Asset => $" {e.Id:x8}",
                    _ => "",
                }) + (e.Kids.Length > 0 ? "(" + string.Join(",", e.Kids.Select(Show)) + ")" : "");
                if (pk.Contains(EchoesSpider.BeadsId))
                {
                    Part part = Part.Load(pk.Get(EchoesSpider.BeadsId), echoes: true);
                    Console.WriteLine("PART " + string.Join(" ", part.Props.Select(kv => $"{kv.Key}={Show(kv.Value)}")));
                }
                if (pk.Contains(EchoesSpider.ArcsId))
                {
                    Part.Swoosh w = Part.Swoosh.Load(pk.Get(EchoesSpider.ArcsId));
                    Console.WriteLine("SWHC " + string.Join(" ", w.Props.Select(kv => $"{kv.Key}={Show(kv.Value)}")));
                }
            }
        }

        public static void Run(string[] args)
        {
            if (args.Length > 1 && args[1] == "bots")
            {
                Bots(args);
                return;
            }
            if (args.Length > 1 && args[1] == "fx")
            {
                FxProbe(args);
                return;
            }
            if (args.Length > 1 && args[1] == "drone")
            {
                Drone(args);
                return;
            }
            string? only = args.Length > 1 && !args[1].Contains('=') ? args[1] : null;
            string? hunterArg = args.FirstOrDefault(a => a.StartsWith("hunters="))?[8..];
            int frames = args.FirstOrDefault(a => a.StartsWith("frames=")) is string fa ? Int32.Parse(fa[7..]) : 300;
            Hunter[] hunters = hunterArg?.Split(',').Select(Enum.Parse<Hunter>).ToArray()
                ?? new[] { Hunter.Samus, Hunter.Spire, Hunter.Noxus, Hunter.Kanden, Hunter.Trace, Hunter.Weavel, Hunter.Sylux };
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            EchoesArena.Register(Paths.FileSystem);
            foreach (EchoesArena.Def d in EchoesArena.All)
            {
                if (only != null && EchoesArena.Find(only) != d) continue;
                foreach (Hunter hunter in hunters)
                {
                    var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, hunter, 1, 0);
                    settings.TimeLimitSeconds = 36000;
                    settings.PointGoal = 999;
                    MphRecomp.Campaign.CampaignHost host;
                    try
                    {
                        host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"{d.InGameName}: no match ({ex.Message})");
                        break;
                    }
                    using (host)
                    {
                        var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                        for (int f = 0; f < 600 && !(PlayerEntity.Main.Health > 0 && PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Active)); f++) host.Step(idle);
                        if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                        int count = PlayerEntity.SpiderTrackCount(host.Scene);
                        if (hunter == hunters[0]) Console.WriteLine($"{d.InGameName}: gate {PlayerEntity.HostSpiderBall}, {count} track triangles");
                        if (count == 0) break;
                        if (args.Contains("tris") && hunter == hunters[0])
                        {
                            for (int i = 0; i < count; i++)
                            {
                                var (ta, tb, tc) = PlayerEntity.SpiderTrackCorners(host.Scene, i);
                                float[] e = { (tb - ta).Length, (tc - tb).Length, (ta - tc).Length };
                                Array.Sort(e);
                                Console.WriteLine($"    tri {i}: edges {e[0]:0.00} {e[1]:0.00} {e[2]:0.00} n {PlayerEntity.SpiderTrackFace(host.Scene, i).Normal:0.00}");
                            }
                            break;
                        }
                        Check(host, hunter, count, frames);
                    }
                }
            }
        }

        // a bot match per arena with track: per bot, the frames in alt form and attached, the attaches, the longest hold,
        // the deaths (a track that traps a bot shows as a long hold with no end)
        static void Bots(string[] args)
        {
            string? only = args.Length > 2 && !args[2].Contains('=') ? args[2] : null;
            int frames = args.FirstOrDefault(a => a.StartsWith("frames=")) is string fa ? Int32.Parse(fa[7..]) : 18000;
            int bots = args.FirstOrDefault(a => a.StartsWith("bots=")) is string ba ? Int32.Parse(ba[5..]) : 3;
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            EchoesArena.Register(Paths.FileSystem);
            foreach (EchoesArena.Def d in EchoesArena.All)
            {
                if (only != null && EchoesArena.Find(only) != d || !EchoesSpider.HasTrack(d.Key)) continue;
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, bots, 2);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                using var host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                var alt = new Dictionary<PlayerEntity, int>();
                var on = new Dictionary<PlayerEntity, int>();
                var attaches = new Dictionary<PlayerEntity, int>();
                var run = new Dictionary<PlayerEntity, int>();
                var longest = new Dictionary<PlayerEntity, int>();
                var deaths = new Dictionary<PlayerEntity, int>();
                var was = new Dictionary<PlayerEntity, bool>();
                var alive = new Dictionary<PlayerEntity, bool>();
                for (int f = 0; f < frames; f++)
                {
                    host.Step(idle);
                    foreach (PlayerEntity p in PlayerEntity.Players)
                    {
                        if (p == null || !p.IsBot) continue;
                        if (p.IsAltForm) alt[p] = alt.GetValueOrDefault(p) + 1;
                        bool a = p.SpiderAttached;
                        if (a)
                        {
                            on[p] = on.GetValueOrDefault(p) + 1;
                            run[p] = run.GetValueOrDefault(p) + 1;
                            longest[p] = Math.Max(longest.GetValueOrDefault(p), run[p]);
                            if (!was.GetValueOrDefault(p)) attaches[p] = attaches.GetValueOrDefault(p) + 1;
                        }
                        else run[p] = 0;
                        was[p] = a;
                        bool live = p.Health > 0;
                        if (alive.TryGetValue(p, out bool before) && before && !live) deaths[p] = deaths.GetValueOrDefault(p) + 1;
                        alive[p] = live;
                    }
                }
                Console.WriteLine($"{d.InGameName}: {bots} bots, {frames} frames, gate {PlayerEntity.HostSpiderBall}");
                foreach (PlayerEntity p in PlayerEntity.Players)
                {
                    if (p == null || !p.IsBot) continue;
                    Console.WriteLine($"  {p.Hunter,-7} alt {alt.GetValueOrDefault(p),6}  attached {on.GetValueOrDefault(p),6}  attaches {attaches.GetValueOrDefault(p),4}  "
                        + $"longest {longest.GetValueOrDefault(p),5}  deaths {deaths.GetValueOrDefault(p),3}  still on at end {p.SpiderAttached}");
                }
            }
        }

        // the Trooper's drone (owner 2026-11-06: on by itself driving straight at a rail, off with the fly button), on the
        // most sideways track face of each arena: no input / driving along it / driving at it / staying on / fly lets go
        static void Drone(string[] args)
        {
            string? only = args.Length > 2 && !args[2].Contains('=') ? args[2] : null;
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            EchoesArena.Register(Paths.FileSystem);
            foreach (EchoesArena.Def d in EchoesArena.All)
            {
                if (only != null && EchoesArena.Find(only) != d || !EchoesSpider.HasTrack(d.Key)) continue;
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Weavel, 1, 0);
                settings.Players[0].Character = "trooper";
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                using var host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                var morph = idle with { Buttons = MphRecomp.Campaign.CampaignButtons.Morph };
                var fly = idle with { Buttons = MphRecomp.Campaign.CampaignButtons.Jump };
                var fwd = idle with { Move = new System.Numerics.Vector2(0, 1) };
                var side = idle with { Move = new System.Numerics.Vector2(1, 0) };
                for (int f = 0; f < 600 && !(PlayerEntity.Main.Health > 0 && PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Active)); f++) host.Step(idle);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                PlayerEntity me = PlayerEntity.Main;
                int count = PlayerEntity.SpiderTrackCount(host.Scene);
                Console.WriteLine($"{d.InGameName}: drone {me.HostScoutDrone}, gate {PlayerEntity.HostSpiderBall}, {count} track triangles");
                if (count == 0 || !me.HostScoutDrone) continue;
                var faces = Enumerable.Range(0, count).Select(i => PlayerEntity.SpiderTrackFace(host.Scene, i)).Where(f => MathF.Abs(f.Normal.Y) < 0.3f).ToList();
                int tested = 0;
                foreach ((Vector3 centre, Vector3 normal) in faces)
                {
                    if (tested == 3) break;
                    var into = new Vector3(-normal.X, 0, -normal.Z).Normalized();
                    for (int f = 0; f < 60 && me.IsAltForm; f++) host.Step(f % 2 == 0 ? morph : idle);
                    for (int f = 0; f < 40; f++) host.Step(idle);
                    me.Reposition(me.Position, into, me.NodeRef);
                    for (int f = 0; f < 240 && !me.IsAltForm; f++) host.Step(f % 2 == 0 ? morph : idle);
                    for (int f = 0; f < 30; f++) host.Step(idle);
                    if (!me.IsAltForm)
                    {
                        Console.WriteLine("  skipped: didn't split");
                        continue;
                    }
                    float radius = Fixed.ToFloat(me.Values.AltColRadius);
                    PlayerEntity.DevSpiderSteer = into;
                    float gap = 0;
                    Vector3 runStart = Vector3.Zero;
                    // each part from the same spot just off the face
                    int Run(MphRecomp.Campaign.CampaignInput input, int frames)
                    {
                        Vector3 put = centre + normal * (radius + 0.6f) - Vector3.UnitY * Fixed.ToFloat(me.Values.AltColYPos);
                        me.Reposition(put - me.Position, me.NodeRef);
                        me.Speed = Vector3.Zero;
                        runStart = me.Position;
                        int first = -1;
                        for (int f = 0; f < frames; f++)
                        {
                            host.Step(input);
                            if (f == 1) gap = me.DroneFloorGap;
                            if (me.SpiderAttached && first < 0) first = f;
                        }
                        return first;
                    }
                    int still = Run(idle, 40);
                    // a face the drone can't hover beside (no floor under it in its reach) isn't one it can drive at
                    if (gap < 0 || gap > me.DroneCap) continue;
                    tested++;
                    for (int f = 0; f < 40; f++) host.Step(idle);
                    int along = Run(side, 40);
                    for (int f = 0; f < 40; f++) host.Step(idle);
                    int at = Run(fwd, 40);
                    int on = 0;
                    Vector3 from = me.Position;
                    for (int f = 0; f < 120; f++)
                    {
                        host.Step(f < 60 ? fwd : side);
                        if (me.SpiderAttached) on++;
                    }
                    float climbed = (me.Position - from).Length;
                    host.Step(fly);
                    bool offAfterFly = !me.SpiderAttached;
                    int back = 0;
                    Vector3 flyFrom = me.Position;
                    for (int f = 0; f < 20; f++)
                    {
                        host.Step(fly);
                        if (me.SpiderAttached) back++;
                    }
                    PlayerEntity.DevSpiderSteer = null;
                    Console.WriteLine($"  n=({normal.X:0.00},{normal.Y:0.00},{normal.Z:0.00}): no input {(still < 0 ? "stays off" : $"ON at {still}")}, "
                        + $"along {(along < 0 ? "stays off" : $"ON at {along}")}, at it {(at < 0 ? "stays off" : $"on at step {at}")}, "
                        + $"then on {on}/120 (60 up, 60 along) (moved {climbed:0.0}, {me.Position - runStart:0.0} from the start), fly: off {offAfterFly}, back on {back}/20, rose {me.Position.Y - flyFrom.Y:0.00}; floor gap {gap:0.0} (cap {me.DroneCap:0.0}), health {me.Health}, pos {me.Position:0.0}, face {centre:0.0}");
                }
            }
        }

        static void Check(MphRecomp.Campaign.CampaignHost host, Hunter hunter, int count, int frames)
        {
            PlayerEntity me = PlayerEntity.Main;
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            var morph = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Buttons = MphRecomp.Campaign.CampaignButtons.Morph };
            var hold = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Buttons = MphRecomp.Campaign.CampaignButtons.Spider };
            var holdMove = hold with { Move = new System.Numerics.Vector2(0, 1) };
            var holdSide = hold with { Move = new System.Numerics.Vector2(1, 0) };
            // a sideways face, the most upward and the most downward (one face only on a flat strip)
            var faces = Enumerable.Range(0, count).Select(i => (I: i, F: PlayerEntity.SpiderTrackFace(host.Scene, i))).ToList();
            var picks = new List<(string Name, Vector3 Centre, Vector3 Normal)>();
            var side = faces.OrderBy(f => MathF.Abs(f.F.Normal.Y)).First();
            picks.Add(("side", side.F.Centre, side.F.Normal));
            var top = faces.OrderByDescending(f => f.F.Normal.Y).First();
            if (top.F.Normal.Y > 0.5f) picks.Add(("top", top.F.Centre, top.F.Normal));
            var under = faces.OrderBy(f => f.F.Normal.Y).First();
            if (under.F.Normal.Y < -0.5f) picks.Add(("under", under.F.Centre, under.F.Normal));
            foreach ((string name, Vector3 centre, Vector3 normal) in picks)
            {
                for (int f = 0; f < 900 && me.Health == 0; f++) host.Step(f % 2 == 0 ? hold with { Buttons = MphRecomp.Campaign.CampaignButtons.Jump } : idle);
                // facing into the track before morphing, so the alt form's steering (set from the facing on entry) looks
                // at it: forward = up a wall, as with the camera behind the ball in play
                for (int f = 0; f < 60 && me.IsAltForm; f++) host.Step(f % 2 == 0 ? morph : idle);
                for (int f = 0; f < 40; f++) host.Step(idle);
                var into = new Vector3(-normal.X, 0, -normal.Z);
                if (into.LengthSquared > 0.01f) me.Reposition(me.Position, into.Normalized(), me.NodeRef);
                for (int f = 0; f < 240 && !me.IsAltForm; f++) host.Step(f % 2 == 0 ? morph : idle);
                for (int f = 0; f < 30; f++) host.Step(idle);
                if (!me.IsAltForm)
                {
                    Console.WriteLine($"  {hunter,-7} {name,-5} skipped: didn't morph");
                    continue;
                }
                float radius = Fixed.ToFloat(me.Values.AltColRadius);
                Vector3 put = centre + normal * (radius + 0.15f) - Vector3.UnitY * Fixed.ToFloat(me.Values.AltColYPos);
                me.Reposition(put - me.Position, me.NodeRef);
                me.Speed = Vector3.Zero;
                int on = 0, onRail = 0;
                float maxGap = 0, segGap = 0; int segBad = 0;
                Vector3 start = me.Position, mid = start;
                PlayerEntity.DevSpiderSteer = into.LengthSquared > 0.01f ? into.Normalized() : new Vector3(-normal.X, 0, -normal.Z + 1).Normalized();
                for (int f = 0; f < frames; f++)
                {
                    host.Step(f < frames / 2 ? holdMove : holdSide);
                    if (f == frames / 2 - 1) mid = me.Position;
                    if (me.SpiderAttached)
                    {
                        on++;
                        if (me.DevSpiderRail != Vector3.Zero) onRail++;
                        maxGap = MathF.Max(maxGap, me.SpiderDistance - radius);
                        if (hunter == Hunter.Kanden && f > 20) { float sg = me.DevKandenSegGap(); segGap = MathF.Max(segGap, sg); if (sg > 0.05f) segBad++; }
                    }
                }
                float moved = (me.Position - start).Length;
                Vector3 held = me.Position;
                for (int f = 0; f < 60; f++) host.Step(hold);
                float drift = (me.Position - held).Length;
                PlayerEntity.DevSpiderSteer = null;
                bool stillOn = me.SpiderAttached;
                Vector3 before = me.Position;
                for (int f = 0; f < 60; f++) host.Step(idle);
                float fell = (me.Position - before).Length;
                Console.WriteLine($"  {hunter,-7} {name,-5} n=({normal.X:0.00},{normal.Y:0.00},{normal.Z:0.00}): attached {on}/{frames} (rail {onRail}, r {radius:0.00}), "
                    + $"max gap {maxGap:0.000}{(hunter == Hunter.Kanden ? $", body gap {segGap:0.000} ({segBad} frames off)" : "")}, moved {moved:0.0} (fwd {mid - start:0.0} side {me.Position - mid:0.0}), idle drift {drift:0.000} (on {stillOn}), let go moved {fell:0.0}, health {me.Health}");
            }
        }
    }
}
