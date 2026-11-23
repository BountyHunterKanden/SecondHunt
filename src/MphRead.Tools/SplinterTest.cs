using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Campaign.Retro;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Enemies;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRead
{
    // Echoes enemy pilot (E1): the Splinter (SPTR).
    //   -splintertest survey [disc=]: which paks hold the Splinter ANCS, every SPTR placement (pak, area, name, health),
    //                                 and the full property tree of the first plain Splinter (names from PropertyMap.xml,
    //                                 a research reference that never ships)
    //   -splintertest import [disc=] [out=]: the enemies kind's export of recipes/enemies/MP2Splinter.json
    //   -splintertest spawn [out=] [ROOM]: the imported Splinter in a campaign room (UNIT1_RM1): its numbers from enemy.cfg,
    //                                 its state machine from the player seeing it to the pounce and back, a hit, death
    //   -splintertest grenchler [out=] [ROOM]: the imported Grenchler (import name=MP2Grenchler first), SplinterTest.Grenchler.cs
    internal static partial class SplinterTest
    {
        static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        const uint SplinterAncs = 0x5034dc4a;

        static string? Arg(string[] args, string key) => args.FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..];

        static Dictionary<uint, (string Name, string Type)> _names = new();

        static void LoadNames()
        {
            string path = Path.Combine(Root, "extract_out", "multiplayer", "mp2", "mp2", "PropertyMap.xml");
            if (!File.Exists(path)) return;
            foreach (Match m in Regex.Matches(File.ReadAllText(path), "<Key ID=\"0x([0-9A-Fa-f]+)\" Type=\"([^\"]*)\"/>\\s*<Value Name=\"([^\"]*)\"/>"))
            {
                _names.TryAdd(Convert.ToUInt32(m.Groups[1].Value, 16), (m.Groups[3].Value, m.Groups[2].Value));
            }
        }

        static string NameOf(uint id) => _names.TryGetValue(id, out var n) ? $"{n.Name}:{n.Type}" : $"{id:X8}";

        static void Dump(Mrea.Property p, string indent)
        {
            foreach (Mrea.Property c in p.Children ?? new())
            {
                string val = c.Children != null ? $"struct x{c.Children.Count}" :
                    c.Data.Length == 4 ? $"{Be.F32(c.Data, 0):0.####} / {Be.I32(c.Data, 0)} / {Be.U32(c.Data, 0):x8}" :
                    c.Data.Length == 1 ? $"{c.Data[0]}" :
                    c.Data.Length > 0 && c.Data.Length <= 64 && c.Data.Take(c.Data.Length - 1).All(b => b >= 32 && b < 127) ? $"\"{c.Str}\"" :
                    $"{c.Data.Length} bytes: {Convert.ToHexString(c.Data.AsSpan(0, Math.Min(c.Data.Length, 24)))}";
                Console.WriteLine($"{indent}{c.Id:X8} {NameOf(c.Id)} = {val}");
                if (c.Children != null) Dump(c, indent + "  ");
            }
        }

        public static void Run(string[] args)
        {
            string mode = args.Length > 1 ? args[1] : "";
            if (mode == "survey") { Survey(args); return; }
            if (mode == "events") { Events(args); return; }
            if (mode == "import") { Import(args); return; }
            if (mode == "parts") { Parts(args); return; }
            if (mode == "spawn") { Spawn(args); return; }
            if (mode == "render") { Render(args); return; }
            if (mode == "grenchler") { Grenchler(args); return; }
            Console.WriteLine("usage: -splintertest survey|events|import|parts|spawn|render|grenchler [disc=] [out=] [png=] [ROOM]");
        }

        static int _fails;

        static void Check(bool ok, string what)
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) _fails++;
        }

        static void Steps(CampaignHost host, int frames, Action? each = null)
        {
            var input = new CampaignInput();
            for (int i = 0; i < frames; i++)
            {
                host.Step(input);
                each?.Invoke();
            }
        }

        static bool Alive(Scene scene, EchoesSplinter s)
        {
            foreach (EnemyInstanceEntity e in scene.GetEnemyInstanceEntities()) if (e == s) return true;
            return false;
        }

        // the imported Splinter (run "import" first) in a real campaign room, the player standing still
        static void Spawn(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string dir = Path.Combine(Out(args), "MP2Splinter");
            if (!RetroEnemyConfig.Exists(dir)) { Console.WriteLine($"no import at {dir}: run -splintertest import first"); return; }
            string roomName = args.Skip(2).FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('=')) ?? "UNIT1_RM1";
            RetroEnemyConfig cfg = RetroEnemyConfig.Load(dir);
            Console.WriteLine($"== config {dir}: {cfg.Count} properties, type {cfg.Type}, fsm {cfg.FsmId:x8}");
            Check(cfg.Type == "SPTR" && cfg.Fsm != null && cfg.Fsm.States.Count == 47, $"SPTR, {cfg.Fsm?.States.Count} states (47)");
            Check(cfg.F32("b3774750/cf90d15e/f0668919", 0) == 6, $"health {cfg.F32("b3774750/cf90d15e/f0668919", 0)} (6)");
            Check(cfg.F32("b3774750/ff77c96f", 0) == 14 && cfg.F32("b3774750/8db77ee4", 0) == 80, "max attack 14, detect 80");

            Console.WriteLine($"== live room {roomName}");
            using CampaignHost host = CampaignHost.Start(roomName);
            Scene scene = host.Scene;
            Steps(host, 10);
            Vector3 start = host.Player.Position;
            // a spot ~12 units off with a clear line to the player and floor under it
            Vector3? spot = null;
            for (int a = 0; a < 16 && spot == null; a++)
            {
                for (float r = 14; r >= 9 && spot == null; r -= 2.5f)
                {
                    float ang = a * MathF.Tau / 16;
                    Vector3 p = start + new Vector3(MathF.Sin(ang), 0, MathF.Cos(ang)) * r;
                    CollisionResult res = default;
                    if (CollisionDetection.CheckBetweenPoints(start.AddY(0.8f), p.AddY(0.8f), TestFlags.None, scene, ref res)) continue;
                    res = default;
                    if (!CollisionDetection.CheckBetweenPoints(p.AddY(0.8f), p.AddY(-2), TestFlags.None, scene, ref res) || res.Plane.Y < 0.5f) continue;
                    spot = res.Position;
                }
            }
            Check(spot != null, $"player at {start}, open floor at {spot}");
            if (spot == null) { Console.WriteLine($"== {_fails} FAILED"); return; }
            Vector3 to = start - spot.Value;
            float yaw = MathF.Atan2(to.X, to.Z);
            var s = new EchoesSplinter(31001, cfg, spot.Value, yaw, scene, seed: 5);
            scene.AddEntity(s);
            Check(Alive(scene, s), $"spawned, hp {s.Hp} of {s.HpMax} (6 x {EchoesSplinter.HealthScale}), state {s.Brain.StateName}");
            Check(s.Vulnerability(EchoesSplinter.VDark) == 200 && s.Vulnerability(EchoesSplinter.VLight) == 50, "Dark 200, Light 50 (from the disc)");

            int hp0 = host.Player.Health;
            float minY = spot.Value.Y, maxY = spot.Value.Y, closest = to.Length, flat = to.Xz.Length;
            var clips = new List<string>();
            Steps(host, 60 * 15, () =>
            {
                minY = MathF.Min(minY, s.Position.Y);
                maxY = MathF.Max(maxY, s.Position.Y);
                closest = MathF.Min(closest, (host.Player.Position - s.Position).Length);
                flat = MathF.Min(flat, (host.Player.Position.Xz - s.Position.Xz).Length);
                if (clips.Count == 0 || clips[^1] != s.Clip) clips.Add(s.Clip);
            });
            Console.WriteLine($"  states: {String.Join(" > ", s.Visited)}");
            Console.WriteLine($"  clips: {String.Join(" > ", clips.Take(40))}");
            Console.WriteLine($"  y {minY:0.00}..{maxY:0.00}, closest {closest:0.00}, jumps {s.Jumps}, dealt {s.DamageDealt}, player hp {hp0} -> {host.Player.Health}");
            Check(s.Visited.Contains("Alert") && s.Visited.Contains("PathFind"), "saw the player: Alert -> PathFind");
            Check(s.Jumps >= 1 && s.Visited.Contains("Jump"), $"pounced ({s.Jumps} jumps)");
            Check(s.Visited.Contains("Taunt") || s.Visited.Contains("Sidestep"), "taunted or stepped aside after landing");
            Check(minY > spot.Value.Y - 2.5f, $"kept to the floor (lowest {minY:0.00}, spawned at {spot.Value.Y:0.00})");
            Check(closest < 3, $"reached the player ({closest:0.00})");
            Check(flat > 0.3f, $"stopped at the player's side, not inside them ({flat:0.00} across)");
            Check(s.DamageDealt > 0 && host.Player.Health < hp0, $"hurt the player ({s.DamageDealt})");

            Console.WriteLine("== damage");
            float hp = s.Hp;
            s.TakeDamage(6, host.Player);
            Check(MathF.Abs(s.Hp - (hp - 6)) < 0.01f && s.Hits == 1, $"Power Beam 6 -> hp {s.Hp}");
            Steps(host, 5);
            Check(s.Clip.Contains("LightHit") || s.Brain.StateName == "Jump", $"hit reaction {s.Clip}");
            while (s.Hp > 0 && s.Hits < 20) s.TakeDamage(6, host.Player);
            Check(s.Dying && s.Clip == EchoesSplinter.Death && Alive(scene, s), $"dead after {s.Hits} hits, plays {s.Clip}");
            Steps(host, 60 * 6);
            Check(!Alive(scene, s), "removed once the death clip ends");
            Console.WriteLine(_fails == 0 ? "== ALL OK" : $"== {_fails} FAILED");
        }

        // render: the imported body as the app loads it (NativeBody + its clip map), every clip EchoesSplinter plays present,
        // then BodyTest's flat-shaded posed sheet of the main clips (png=, default <out>/splinter.png)
        static void Render(string[] args)
        {
            string dir = Path.Combine(Out(args), "MP2Splinter", EnemyImportKind.BodyDir);
            if (!NativeBody.Exists(dir)) { Console.WriteLine($"no body at {dir}: run -splintertest import first"); return; }
            NativeBody body = NativeBody.Load(dir);
            Console.WriteLine($"== body {dir}: {body.Data.BoneCount} bones, {body.Data.Materials.Length} draws, {body.Data.Anims.Count} clips, height {body.Height:0.###}");
            Check(BodyClips.For(body) is BodyClips m && m.Idle == EchoesSplinter.Ready, "clip map (idle B_ready_splinter)");
            string[] clips = { EchoesSplinter.Ready, EchoesSplinter.Run, EchoesSplinter.Alert, EchoesSplinter.Sniff, EchoesSplinter.TurnLeft,
                EchoesSplinter.TurnRight, EchoesSplinter.JumpInto, EchoesSplinter.JumpArc, EchoesSplinter.Land, EchoesSplinter.StepBack,
                EchoesSplinter.StepLeft, EchoesSplinter.StepRight, EchoesSplinter.Taunt1, EchoesSplinter.Taunt2, EchoesSplinter.Taunt3,
                EchoesSplinter.Death, EchoesSplinter.HitFront, EchoesSplinter.HitBack, EchoesSplinter.HitLeft, EchoesSplinter.HitRight };
            foreach (string c in clips) Check(body.Clip(c) != null, $"clip {c}");
            Check(body.Data.Materials.Sum(x => x.VertexCount) > 0, "mesh has triangles");
            Matrix4 at = EchoesSplinter.ModelMatrix(body, new Vector3(1, 2, 3), 0);
            Vector3 feet = Vector3.TransformPosition(new Vector3(0, body.Feet, 0), at);
            Check(MathF.Abs(feet.Y - 2) < 0.001f, $"feet on the position ({feet.Y:0.###})");
            string png = Arg(args, "png") ?? Path.Combine(Out(args), "splinter.png");
            BodyTest.Run(new[] { "-bodytest", Path.Combine("..", "MP2Splinter", EnemyImportKind.BodyDir), "--out", Out(args),
                "--bones", String.Join(',', EchoesSplinter.Ready, EchoesSplinter.Run, EchoesSplinter.JumpArc, EchoesSplinter.HitFront, EchoesSplinter.Death),
                "--render", png });
            Check(File.Exists(png), $"render {png}");
            Console.WriteLine(_fails == 0 ? "ALL OK" : $"{_fails} FAILED");
        }

        static string Disc(string[] args) => Arg(args, "disc") ?? Path.Combine(Root, "brawl_extract", "prime", "echoes_retail.rvz");
        // events / parts on another Echoes enemy: pak=Metroid4.pak ancs=09ed9cde (default the Splinter's)
        static string PakArg(string[] args) => Arg(args, "pak") ?? "Metroid1.pak";
        static uint AncsArg(string[] args) => Arg(args, "ancs") is string a ? Convert.ToUInt32(a, 16) : 0xb46550b0u;
        static string Out(string[] args) => Arg(args, "out") ?? Path.Combine(Path.GetTempPath(), "splintertest");

        // the enemies kind's export of recipes/enemies/MP2Splinter.json from the disc, then its files checked
        static void Import(string[] args)
        {
            string name = Arg(args, "name") ?? "MP2Splinter";
            ImportRecipe recipe = ImportRecipe.Parse("enemies", name, File.ReadAllText(Path.Combine(Root, "recipes", "enemies", name + ".json")));
            string outDir = Path.Combine(Out(args), name);
            using IFileSource src = DiscImage.Open(Disc(args));
            var ctx = new ImportContext(src);
            var kind = new MphRecomp.Import.Enemies.EnemyImportKind();
            foreach ((string path, string sha) in kind.Sources(recipe))
            {
                string got = ctx.Sha1(path);
                Console.WriteLine($"source {path} {(got == sha ? "ok" : $"MISMATCH {got}")}");
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            kind.Run(ctx, recipe, outDir, (f, s) => { }, default);
            Console.WriteLine($"imported to {outDir} in {sw.ElapsedMilliseconds} ms");
            foreach (string f in Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).GroupBy(Path.GetExtension).Select(g => $"{g.Key} x{g.Count()}"))
            {
                Console.WriteLine("  " + f);
            }
        }

        // every PART the clips and the gib name (and their children): which ones Part.Load (Echoes) can't read yet
        static void Parts(string[] args)
        {
            using IFileSource src = DiscImage.Open(Disc(args));
            string mp1 = src.Paths.First(p => p.EndsWith(PakArg(args), StringComparison.OrdinalIgnoreCase));
            var pk = new Pak(src.ReadFile(mp1), mp1);
            byte[] raw = pk.Get(AncsArg(args));
            Ancs a = Ancs.Parse(raw);
            Console.WriteLine(String.Join(", ", a.Characters.Select((c, i) => $"[{i}] {c.Name} cmdl {c.Cmdl:x8}")));
            var ev = EchoesEvents.Read(raw, a.Set!.EventsAt);
            uint gib = Arg(args, "gib") is string gx ? Convert.ToUInt32(gx, 16) : Arg(args, "ancs") == null ? 0xfc5924b2u : 0;
            var todo = new Queue<uint>(ev.Sets.SelectMany(x => x.Particles).Select(x => x.Part).Concat(gib != 0 ? new[] { gib } : Array.Empty<uint>()));
            var done = new HashSet<uint>();
            int bad = 0;
            while (todo.Count > 0)
            {
                uint id = todo.Dequeue();
                if (!done.Add(id)) continue;
                try
                {
                    foreach ((string t, uint dep) in Part.Load(pk.Get(id), echoes: true).Dependencies()) if (t == "PART") todo.Enqueue(dep);
                }
                catch (Exception e)
                {
                    bad++;
                    Console.WriteLine($"{id:x8} ({System.Text.Encoding.ASCII.GetString(pk.Get(id), 0, 4)}): {e.Message}");
                    if (Arg(args, "dump") is string d) File.WriteAllBytes(Path.Combine(d, $"{id:x8}.part"), pk.Get(id));
                }
            }
            Console.WriteLine($"{done.Count} parts, {bad} unreadable");
        }

        // every clip's EVNT points (particles, sounds) and which audio group renders each sound
        static void Events(string[] args)
        {
            string disc = Arg(args, "disc") ?? Path.Combine(Root, "brawl_extract", "prime", "echoes_retail.rvz");
            using IFileSource src = DiscImage.Open(disc);
            string mp1 = src.Paths.First(p => p.EndsWith(PakArg(args), StringComparison.OrdinalIgnoreCase));
            string aud = src.Paths.First(p => p.EndsWith("AudioGrp.pak", StringComparison.OrdinalIgnoreCase));
            var pk = new Pak(src.ReadFile(mp1), mp1);
            var audio = new Pak(src.ReadFile(aud), aud);
            uint aid = AncsArg(args);
            Ancs a = Ancs.Parse(pk.Get(aid));
            if (Arg(args, "dump") is string dumpTo) File.WriteAllBytes(dumpTo, pk.Get(aid));
            var ev = EchoesEvents.Read(pk.Get(aid), a.Set!.EventsAt);
            Console.WriteLine($"{ev.Sets.Count} event sets, read to {ev.End} of {pk.Get(aid).Length}");
            var sounds = new List<(string Clip, float Time, uint Id)>();
            foreach ((string name, Ancs.MetaAnim meta) in a.Set.Anims)
            {
                var prims = Ancs.Prims(meta);
                EchoesEvents.Set? set = ev.For(meta);
                Console.WriteLine($"clip {name} prims {string.Join(",", prims.Select(p => $"{p.PrimId}:{p.PrimName}"))}");
                if (set == null) continue;
                foreach (var u in set.Users) Console.WriteLine($"  user {u.Base.Time:0.###} {u.Base.Name} type {u.UserType} bone {u.Bone}");
                foreach (var l in set.Loops) Console.WriteLine($"  loop {l.Base.Time:0.###} {l.Base.Name} {l.Value}");
                foreach (var e in set.Particles) Console.WriteLine($"  part {e.Base.Time:0.###} {e.Frames}f bone {e.Bone} {e.Part:x8} mode {e.Mode} scale {e.Scale}");
                foreach (var s in set.Sounds) { Console.WriteLine($"  sound {s.Base.Time:0.###} {s.Id:x} {s.Base.Name}"); sounds.Add((name, s.Base.Time, s.Id)); }
            }
            short[] lookup = MusyX.Lookup(audio.Get(0xb7c1b3a7));
            string pn = Path.GetFileNameWithoutExtension(mp1);
            var groups = pk.Order.Where(id => pk.TypeOf(id) == "AGSC").Select(id => (id, pk, pn))
                .Concat(audio.Order.Where(id => audio.TypeOf(id) == "AGSC").Select(id => (id, pk: audio, "AudioGrp"))).ToList();
            var parsed = new Dictionary<uint, Agsc>();
            foreach (var s in sounds)
            {
                int m = MusyX.Translate(lookup, s.Id);
                string where = "-";
                foreach (var (gid, gp, gn) in groups)
                {
                    if (!parsed.TryGetValue(gid, out Agsc? g)) parsed[gid] = g = Agsc.Parse(gp.Get(gid));
                    if (m >= 0 && g.Sounds.ContainsKey((ushort)m)) { where = $"{gn} {gid:x8} '{g.Group}'"; break; }
                }
                Console.WriteLine($"  sound {s.Clip} {s.Time:0.###} id {s.Id:x} musyx {m} in {where}");
            }
        }

        static void Survey(string[] args)
        {
            LoadNames();
            string disc = Arg(args, "disc") ?? Path.Combine(Root, "brawl_extract", "prime", "echoes_retail.rvz");
            // type=GRCH surveys another Echoes enemy the same way (the next E1 enemy); ancs=<hex> also lists its characters
            string type = Arg(args, "type") ?? "SPTR";
            uint extraAncs = Arg(args, "ancs") is string ax ? Convert.ToUInt32(ax, 16) : 0xb46550b0u;
            using IFileSource src = DiscImage.Open(disc);
            var objs = new List<(Mrea.ScriptObject O, string Pak, uint Area)>();
            var seen = new HashSet<uint>();
            var ancsSeen = new HashSet<uint>();
            foreach (string path in src.Paths.Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
            {
                Pak pk;
                try { pk = new Pak(src.ReadFile(path), path); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { continue; }
                foreach (uint aid in new[] { SplinterAncs, extraAncs })
                {
                    if (!pk.Contains(aid) || !ancsSeen.Add(aid)) continue;
                    Ancs an = Ancs.Parse(pk.Get(aid));
                    Console.WriteLine($"ANCS {aid:x8} in {path}: " + String.Join(", ", an.Characters.Select((c, i) =>
                        $"[{i}] {c.Name} cmdl {c.Cmdl:x8} pas {c.PasStates.Count}")) + $"; {an.Set?.Anims.Count} anims");
                }
                foreach (var kv in pk.Resources)
                {
                    if (kv.Value.Type != "MREA" || !seen.Add(kv.Key)) continue;
                    try
                    {
                        foreach (Mrea.ScriptObject o in Mrea.Read(pk.Get(kv.Key)).ReadScripts())
                        {
                            if (o.Type == type) objs.Add((o, path, kv.Key));
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or InvalidDataException) { }
                }
            }
            foreach (var (o, pak, area) in objs)
            {
                Mrea.Property? pat = o.Root.Find(0xB3774750);
                float hp = pat?.Find(0xCF90D15E)?.Find(0xF0668919)?.F32 ?? -1;
                byte[]? set = pat?.Find(0xE25FB08C)?.Data;
                string ch = set != null && set.Length >= 12 ? $"{Be.U32(set, 0):x8}[{Be.U32(set, 4)}] anim {Be.U32(set, 8)}" : "?";
                Console.WriteLine($"{type} {o.Id:x8} {pak} area {area:x8} \"{o.Name}\" layer {o.Layer} active {o.Active} hp {hp} {ch}");
                // the numbers that vary between placements: Patterned speed/turn/ranges, then the Splinter's own scalars
                string Num(Mrea.Property? q) => q == null ? "-" : q.Data.Length == 4 && _names.TryGetValue(q.Id, out var nt) && nt.Type == "int" ? $"{Be.I32(q.Data, 0)}"
                    : q.Data.Length == 4 ? $"{q.F32:0.###}" : q.Data.Length == 1 ? $"{q.Data[0]}" : "?";
                uint[] pids = { 0x6392404E, 0x020C78BB, 0x8DB77EE4, 0x83DFC40F, 0x58434916, 0xFF77C96F, 0xB0CFE015, 0xC80E329B };
                uint[] sids = { 0x72EDEB7D, 0xB8ED9FFA, 0x5E8D301B, 0xB98BB88F, 0x5FEB176E, 0x726CD31D, 0x376E909F, 0xB63B810C, 0x7DC82F46, 0x51BE00D3, 0xB7DEAF32 };
                Console.WriteLine("     pat " + String.Join(" ", pids.Select(i => Num(pat?.Find(i)))) + " | own " + String.Join(" ", sids.Select(i => Num(o.Root.Find(i))))
                    + $" | atk {o.Root.Find(0x66DCAACB)?.Find(0xF2D02613)?.F32} di {o.Root.Find(0x4436A388)?.Find(0xF2D02613)?.F32}");
            }
            // the reference: the plain combat Splinter (ANCS b46550b0 character 0 "splinter", health 6)
            // (another type: the first placement with its most common health, character 0)
            float refHp = type == "SPTR" ? 6f : objs.GroupBy(x => x.O.Root.Find(0xB3774750)?.Find(0xCF90D15E)?.Find(0xF0668919)?.F32 ?? -1)
                .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            var first = objs.FirstOrDefault(x => x.O.Root.Find(0xB3774750)?.Find(0xCF90D15E)?.Find(0xF0668919)?.F32 == refHp
                && x.O.Root.Find(0xB3774750)?.Find(0xE25FB08C)?.Data is byte[] d && Be.U32(d, 4) == 0);
            if (first.O != null)
            {
                Console.WriteLine($"== property tree of {first.O.Id:x8} ({first.Pak} area {first.Area:x8})");
                Dump(first.O.Root, "  ");
            }
        }
    }
}
