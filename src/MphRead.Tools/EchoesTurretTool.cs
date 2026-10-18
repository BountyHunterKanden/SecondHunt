using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Arenas;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Retro;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead
{
    // Shooting Gallery's manned turret + bomb slot (docs/SHOOTING_GALLERY_TURRET.md):
    //   -echoesturret probe [disc=<retail echoes .rvz/.iso>]: prints the turret's script objects (every property by id),
    //       the animation set's characters, skeleton locators and animations, the shot's WPSC values and the models'
    //       bounds, read from the retail disc
    //   -echoesturret dump id=<hex> out=<file> [disc=]: one resource's bytes
    //   -echoesturret test: a Shooting Gallery match on the PC (the arena imported from the retail disc:
    //       -echoesarena import disc=../brawl_extract/prime/echoes_retail.rvz arena=M06_ShootingGallery) driving the turret
    //       through its states: the entry kill, control, the barrel's limits, fire rate, a hit, getting out, the turret's
    //       health, and the bomb slot (Samus's bomb, then each other hunter's alt-form attack laying Samus's bomb)
    //   -echoesturret look [out=<dir>]: the imported look (hd/turret.json) drawn by the thumbnail renderer: lowered with
    //       the hologram, half risen, raised, raised and turned, recoiling, half lowered (turret_<state>.png)
    //   -echoesturret nettest: the turret in a LAN match on this PC (a host + a joiner process, NetTest below)
    internal static class EchoesTurretTool
    {
        static readonly string RetailDisc = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "brawl_extract", "prime", "echoes_retail.rvz"));

        static string? Arg(string[] args, string key) => args.FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..];

        public static Pak LoadPak(string disc)
        {
            using IFileSource src = DiscImage.Open(disc);
            string path = src.Paths.First(p => p.EndsWith("Metroid6.pak", StringComparison.OrdinalIgnoreCase) && !p.Contains("MP2/") == !src.GameId.StartsWith("R3M"));
            return new Pak(src.ReadFile(path), "Metroid6.pak");
        }

        public static void Run(string[] args)
        {
            string verb = args.Length > 1 ? args[1] : "probe";
            string disc = Arg(args, "disc") ?? RetailDisc;
            if (verb == "probe")
            {
                Probe(disc);
                return;
            }
            if (verb == "dump" && Arg(args, "id") is string hex && Arg(args, "out") is string outPath)
            {
                // one resource's (decompressed) bytes, for a look at a format Core doesn't read
                File.WriteAllBytes(outPath, LoadPak(disc).Get(Convert.ToUInt32(hex, 16)));
                return;
            }
            if (verb == "modelinfo" && Arg(args, "id") is string mhex)
            {
                // a CMDL's materials as Core reads them (textures, blend, flags, stages, texgens, UV animations) + bounds
                Pak pak = LoadPak(disc);
                uint mid = Convert.ToUInt32(mhex, 16);
                Cmdl g = Cmdl.Load(pak.Get, mid);
                byte[] raw = pak.Get(mid);
                Console.WriteLine($"CMDL {mid:x8} v{g.Version} flags {g.Flags:x}: {g.PositionCount} positions, {g.TriangleCount} triangles; bounds "
                    + string.Join(" ", Enumerable.Range(0, 6).Select(k => Be.F32(raw, 12 + 4 * k).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))));
                for (int mi = 0; mi < g.Materials.Length; mi++)
                {
                    Cmdl.Material m = g.Materials[mi];
                    int tris = g.TriangleMaterial.Count(t => t == mi);
                    Console.WriteLine($"  mat {mi} ({tris} tris) flags {m.Flags:x} blend src {m.BlendSrc} dst {m.BlendDst} tex [{string.Join(",", m.TexIds.Select(t => t.ToString("x8")))}] "
                        + $"chan [{string.Join(",", m.Channels.Select(c => c.ToString("x")))}] konst [{string.Join(" ", m.Konst.Select(k => Convert.ToHexString(k)))}]");
                    foreach (Cmdl.TevStage st in m.Stages)
                    {
                        Console.WriteLine($"    stage c[{string.Join(",", st.ColorIn)}] a[{string.Join(",", st.AlphaIn)}] cop {st.RawColorOp:x} aop {st.RawAlphaOp:x} tex {st.Tex} tc {st.TexCoord} ras {st.Ras} kc {st.KColor} ka {st.KAlpha}");
                    }
                    foreach (Cmdl.TexGen tg in m.TexGens) Console.WriteLine($"    texgen type {tg.Type} src {tg.Src} mtx {tg.Mtx} post {tg.Post} norm {tg.Normalize}");
                    foreach (Cmdl.UvAnim ua in m.UvAnims) Console.WriteLine($"    uvanim mode {ua.Mode} [{string.Join(", ", ua.Params.Select(x => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))}]");
                }
                if (Arg(args, "verts") == "1")
                {
                    for (int vi = 0; vi < g.PositionCount; vi++)
                    {
                        Console.WriteLine($"  v{vi} " + string.Join(" ", Enumerable.Range(0, 3).Select(k => g.Positions[vi * 3 + k].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))));
                    }
                    float[] uvs = g.ShortUvs is { Length: > 0 } su ? su : g.Uv0;
                    Console.WriteLine($"  uv arrays: float {g.Uv0.Length / 2}, short {(g.ShortUvs?.Length ?? 0) / 2}; corner0 p{g.Triangles[0].P} t0 {g.Triangles[0].T0} t1 {g.Triangles[0].T1}");
                    var seen = new HashSet<(int, int)>();
                    for (int ti = 0; ti < g.Triangles.Length; ti++)
                    {
                        Cmdl.Corner cn = g.Triangles[ti];
                        if (!seen.Add((cn.P, cn.T0)) || cn.T0 < 0 || cn.T0 * 2 + 1 >= uvs.Length) continue;
                        Console.WriteLine($"  corner p{cn.P} z {g.Positions[cn.P * 3 + 2]:0.###} uv {uvs[cn.T0 * 2]:0.###} {uvs[cn.T0 * 2 + 1]:0.###} (mat {g.TriangleMaterial[ti / 3]})");
                    }
                }
                return;
            }
            if (verb == "scan")
            {
                // every script object in the arena + every named pak resource (looking for the occupant's own view model)
                Pak pak = LoadPak(disc);
                EchoesArena.Def def = EchoesArena.Find("M06_ShootingGallery")!;
                Mrea area = Mrea.Read(pak.Get(def.Mrea));
                string? only = Arg(args, "type");
                if (Arg(args, "genhex") != null) { byte[] g = area.Sections[area.GeneratedSection]; Console.WriteLine($"SGEN {g.Length}: {Convert.ToHexString(g, 0, Math.Min(96, g.Length))}"); return; }
                foreach (Mrea.ScriptObject o in area.ReadScripts(includeGenerated: true).Where(o => only == null || o.Type == only))
                {
                    Console.WriteLine($"{o.Type} {o.Id:x8} '{o.Name}' layer={o.Layer} active={o.Active} conns={string.Join(" ", o.Connections.Select(c => $"{c.State}/{c.Message}->{c.Target:x}"))}");
                    if (only != null) Dump(o.Root, 1);
                }
                foreach ((uint id, (string type, string name)) in pak.Names) Console.WriteLine($"name {type} {id:x8} {name}");
                return;
            }
            if (verb == "findname" && Arg(args, "name") is string want)
            {
                // named resources across every pak on the disc (the turret HUD frame RS5_TurretHud2Combat etc.)
                using IFileSource src = DiscImage.Open(disc);
                foreach (string path in src.Paths.Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
                {
                    var pk = new Pak(src.ReadFile(path), path);
                    foreach ((uint id, (string type, string name)) in pk.Names)
                    {
                        if (name.Contains(want, StringComparison.OrdinalIgnoreCase)) Console.WriteLine($"{path}: {type} {id:x8} {name} size={(pk.Contains(id) ? pk.Resources[id].Size : -1)}");
                    }
                    if (Arg(args, "out") is string outDir && Arg(args, "id") is string hx && pk.Contains(Convert.ToUInt32(hx, 16)))
                    {
                        uint id = Convert.ToUInt32(hx, 16);
                        Directory.CreateDirectory(outDir);
                        File.WriteAllBytes(Path.Combine(outDir, $"{id:x8}.{pk.TypeOf(id).ToLowerInvariant()}"), pk.Get(id));
                        Console.WriteLine($"wrote {id:x8} from {path}");
                    }
                }
                return;
            }
            if (verb == "partlight")
            {
                // the turret effects' light keys (LTYP, LCLR, LINT, LOFF, LDIR, LFOT, LFOR, LSLA) and their children
                Pak pak = LoadPak(disc);
                var seen = new HashSet<uint>();
                void Show(uint id, int depth)
                {
                    if (!seen.Add(id) || !pak.Contains(id) || pak.TypeOf(id) != "PART") return;
                    Part part = Part.Load(pak.Get(id), echoes: true);
                    string pad = new string(' ', 2 * depth);
                    string Desc(Part.Elem? e) => e == null ? "-" : e.Cat switch
                    {
                        Part.Cat.Real => e.Kind == "CNST" ? $"{e.F:0.###}" : e.Keys != null ? $"{e.Kind}[loop={e.Loop} {e.LoopStart}-{e.LoopEnd} pct={e.Percent} n={e.Keys.Length}: {string.Join(",", e.Keys.Select(x => x.ToString("0.##")))}]" : e.Kind,
                        Part.Cat.Int => e.Kind == "CNST" ? $"{e.I}" : e.Kind,
                        Part.Cat.Color => e.Kind + "(" + string.Join(",", e.Kids.Select(k => k?.Kind == "CNST" ? $"{k.F:0.##}" : k?.Kind ?? "-")) + ")",
                        _ => e.Kind + "(" + string.Join(",", e.Kids.Select(k => k?.Kind == "CNST" ? $"{k.F:0.##}" : k?.Keys != null ? $"{k.Kind}[loop={k.Loop} {k.LoopStart}-{k.LoopEnd} pct={k.Percent} n={k.Keys.Length}: {string.Join(",", k.Keys.Select(x => x.ToString("0.##")))}]" : k?.Kind ?? "-")) + ")",
                    };
                    Console.WriteLine($"{pad}PART {id:x8}: " + string.Join(" ", part.Props.Where(kv => Arg(args, "all") != null || kv.Key.StartsWith("L") || kv.Key is "COLR" or "MAXP" or "LTME" or "PMAB" or "AAPH").Select(kv => $"{kv.Key}={Desc(kv.Value)}")));
                    foreach ((string type, uint dep) in part.Dependencies()) if (type == "PART") Show(dep, depth + 1);
                }
                foreach (string h in (Arg(args, "ids") ?? "795c46cc,db867cd9,a007a9a9,8dd96ac,934afcf0").Split(',')) Show(Convert.ToUInt32(h, 16), 0);
                return;
            }
            if (verb == "liftprobe")
            {
                // -echoesturret liftprobe: the lift effect (role lift) run where it's placed; each model particle and
                // PMUS square's height and size every 6 frames
                if (!ToolPaths.UseDesktopExtraction()) return;
                EchoesArena.Def gallery = EchoesArena.Find("M06_ShootingGallery")!;
                string dir = EchoesArena.HdDir(gallery.Room) ?? Path.Combine(Paths.FileSystem, "_archives", gallery.Archive, ArenaHdWriter.Dir);
                TurretLook look = EchoesTurret.LoadLook(dir)!;
                var lib = MphRecomp.Campaign.RetroParticles.Load(Path.Combine(dir, EchoesTurret.FxDir), echoes: true);
                TurretFx lf = look.Fx!.First(f => f.Role == (Arg(args, "role") ?? "lift"));
                float[] a = lf.At!;
                var at = new Matrix4(a[0], a[1], a[2], 0, a[3], a[4], a[5], 0, a[6], a[7], a[8], 0, a[9], a[10], a[11], 1);
                var sys = new MphRecomp.Campaign.RetroParticleSystem(lib, lf.Id, at, follow: true, seed: 1);
                var models = new List<MphRecomp.Campaign.RetroModelParticle>();
                for (int k = 0; k < 240; k++)
                {
                    sys.Step();
                    models.Clear();
                    sys.CollectModels(models);
                    if (k % 2 != 0) continue;
                    Console.WriteLine($"{k,4}: " + string.Join("  ", models.Select(m =>
                        $"{(m.Model == 0 ? "sq" : "md")} y={m.Transform.Row3.Y:0.00} det={m.Transform.Determinant:0.00} top={m.Transform.Row3.Y + 3.25f * m.Transform.Row2.Y:0.00} s={m.Transform.Row0.Xyz.Length:0.00}/{m.Transform.Row1.Xyz.Length:0.00}/{m.Transform.Row2.Xyz.Length:0.00} a={m.Color.W:0.00}")));
                }
                return;
            }
            if (verb == "crateprobe")
            {
                // -echoesturret crateprobe <replay>: every Echoes crate or grate break in a replay, with each player's
                // distance to it, form and whether its alt attack is on (what broke it)
                var seen = new Dictionary<EntityBase, int>();
                var hp = new Dictionary<EntityBase, int>();
                ReplayTest.StepProbe = (step, host) =>
                {
                    if (step % 600 == 0)
                    {
                        Console.WriteLine($"  step {step} players: " + string.Join("; ", Enumerable.Range(0, PlayerEntity.PlayerCount).Select(i => PlayerEntity.Players[i])
                            .Where(p => p.LoadFlags.TestFlag(LoadFlags.Active)).Select(p => $"{p.Hunter} hp {p.Health} at {p.Position.X:0.0},{p.Position.Y:0.0},{p.Position.Z:0.0}{(p.IsAltForm ? " alt" : "")}{(p.HostSeated ? " SEATED" : "")}")));
                    }
                    // each crate hit: the damage, the beams and bombs by it, each player's form, place and alt attack
                    foreach (EnemyInstanceEntity e in host.Scene.GetEnemyInstanceEntities())
                    {
                        if (e is not EchoesCrateEntity { Up: true } c) { hp.Remove(e); continue; }
                        int was = hp.TryGetValue(e, out int h) ? h : c.Health;
                        hp[e] = c.Health;
                        if (c.Health >= was) continue;
                        var near = new List<string>();
                        foreach (EntityBase x in host.Scene.Entities)
                        {
                            float d = Vector3.Distance(x.Position, e.Position);
                            if (d > 4) continue;
                            if (x is BeamProjectileEntity bp) near.Add($"beam {bp.Owner?.GetType().Name}/{(bp.Owner as PlayerEntity)?.Hunter} {bp.Beam} {d:0.0}");
                            else if (x is BombEntity bo) near.Add($"bomb {bo.Owner?.Hunter} {d:0.0}");
                        }
                        string who = string.Join(", ", Enumerable.Range(0, PlayerEntity.PlayerCount).Select(i => PlayerEntity.Players[i])
                            .Where(p => p.LoadFlags.TestFlag(LoadFlags.Active))
                            .Select(p => $"{p.Hunter}{(p.IsAltForm ? " alt" : "")}{(p.Flags2.TestFlag(PlayerFlags2.AltAttack) ? " ATTACK" : "")} {Vector3.Distance(p.Position, e.Position):0.0}"));
                        Console.WriteLine($"  step {step}: crate at {e.Position.X:0.0},{e.Position.Z:0.0} -{was - c.Health} ({c.Health}); near: {string.Join("; ", near)}; {who}");
                    }
                    foreach (EnemyInstanceEntity e in host.Scene.GetEnemyInstanceEntities())
                    {
                        int breaks = e is EchoesCrateEntity c ? c.Breaks : e is EchoesBreakableEntity b ? b.Breaks : -1;
                        if (breaks < 0) continue;
                        int was = seen.TryGetValue(e, out int n) ? n : breaks;
                        seen[e] = breaks;
                        if (breaks <= was) continue;
                        string who = string.Join(", ", Enumerable.Range(0, PlayerEntity.PlayerCount).Select(i => PlayerEntity.Players[i])
                            .Where(p => p.LoadFlags.TestFlag(LoadFlags.Active))
                            .Select(p => $"{p.Hunter}{(p.IsAltForm ? " alt" : "")}{(p.Flags2.TestFlag(PlayerFlags2.AltAttack) ? " ATTACK" : "")} {Vector3.Distance(p.Position, e.Position):0.0}"));
                        Console.WriteLine($"  step {step}: {(e is EchoesCrateEntity ? "crate" : "grate")} at {e.Position} broke; {who}");
                    }
                };
                ReplayTest.PlayFile(new[] { "-replayplay", args[2] });
                return;
            }
            if (verb == "test")
            {
                if (!ToolPaths.UseDesktopExtraction()) return;
                Environment.Exit(Test() ? 0 : 1);
            }
            if (verb == "look")
            {
                if (!ToolPaths.UseDesktopExtraction()) return;
                Environment.Exit(Look(Arg(args, "out") ?? ".") ? 0 : 1);
            }
            if (verb == "nettest")
            {
                if (!ToolPaths.UseDesktopExtraction()) return;
                Environment.Exit(NetTest() ? 0 : 1);
            }
            if (verb == "sounds")
            {
                Sounds(disc);
                return;
            }
            if (verb == "sfxfind" && Arg(args, "sfx") is string sfxArg)
            {
                // which audio groups (AGSC, in every pak on the disc) hold a MusyX sfx id
                ushort sfxId = UInt16.Parse(sfxArg);
                using IFileSource src = DiscImage.Open(disc);
                foreach (string path in src.Paths.Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
                {
                    var pk = new Pak(src.ReadFile(path), path);
                    foreach (uint id in pk.Order)
                    {
                        if (pk.TypeOf(id) != "AGSC") continue;
                        Agsc g = Agsc.Parse(pk.Get(id));
                        if (g.Sounds.ContainsKey(sfxId)) Console.WriteLine($"{path} AGSC {id:x8} '{g.Group}' has sfx {sfxId}");
                    }
                }
                return;
            }
            if (verb == "impactfx")
            {
                if (!ToolPaths.UseDesktopExtraction()) return;
                EchoesArena.Register(Paths.FileSystem);
                // one turret effect (role=, default impact) run in its own space (Echoes metres, Y out of the surface):
                // where its particles go, frame by frame, per texture
                EchoesArena.Def gallery = EchoesArena.Find("M06_ShootingGallery")!;
                string dir = EchoesArena.HdDir(gallery.Room) ?? Path.Combine(Paths.FileSystem, "_archives", gallery.Archive, ArenaHdWriter.Dir);
                TurretLook look = EchoesTurret.LoadLook(dir)!;
                var lib = MphRecomp.Campaign.RetroParticles.Load(Path.Combine(dir, EchoesTurret.FxDir), echoes: true);
                string role = Arg(args, "role") ?? "impact";
                TurretFx f = look.Fx!.First(x => x.Role == role);
                Console.WriteLine($"{role}: PART {f.Id:x8}");
                if (Arg(args, "pieces") != null)
                {
                    Console.WriteLine($"shoot {look.ShootTime:0.00} s");
                    foreach (TurretPiece pc in look.Pieces)
                    {
                        if (pc.Shoot == null) continue;
                        Matrix4 up = EchoesTurret.PieceMatrix(pc.Up ?? pc.Shoot, 1);
                        Console.WriteLine($"  {pc.Bone} pass {pc.Pass} tris {pc.Triangles}: up {up.Row3.Xyz} " + string.Join(" ", Enumerable.Range(0, 11).Select(i => { Matrix4 m = EchoesTurret.PieceMatrix(pc.Shoot, i / 10f); return $"{i / 10f:0.0}:{(m.Row3.Xyz - up.Row3.Xyz).Length:0.00}/{Vector3.Dot(m.Row1.Xyz.Normalized(), up.Row1.Xyz.Normalized()):0.000}"; })));
                    }
                    return;
                }
                if (look.Locators?.FirstOrDefault(l => l.Bone == f.Bone) is TurretPiece lb)
                {
                    Matrix4 pm = EchoesTurret.PieceNow(look, lb, null);
                    Console.WriteLine($"  bone {f.Bone}: at {pm.Row3.Xyz} axes x{pm.Row0.Xyz} y{pm.Row1.Xyz} z{pm.Row2.Xyz}");
                }
                // an electric system (ELSC): its properties
                string elscPath = Path.Combine(dir, EchoesTurret.FxDir, $"{f.Id:x8}.elsc");
                if (File.Exists(elscPath))
                {
                    var el = MphRecomp.Import.Retro.Elsc.Load(File.ReadAllBytes(elscPath));
                    foreach (var kv in el.Props) Console.WriteLine($"  ELSC {kv.Key} = {E(kv.Value)}");
                }
                static string E(MphRecomp.Import.Retro.Part.Elem? e) => e == null ? "-" : e.Kind == "CNST" && e.Kids.Length > 0 ? "(" + string.Join(",", e.Kids.Select(E)) + ")" : e.Kind == "CNST" ? (e.Cat == MphRecomp.Import.Retro.Part.Cat.Int ? e.I.ToString() : e.F.ToString("0.###"))
                    : e.Cat == MphRecomp.Import.Retro.Part.Cat.Bool ? e.B.ToString() : e.Id != 0 ? $"{e.Kind}:{e.Id:x8}" + (e.Kids.Length > 0 ? $"({string.Join(",", e.Kids.Select(E))})" : "") : $"{e.Kind}({string.Join(",", e.Kids.Select(E))}{(e.Keys != null ? $" keys[{e.Keys.Length}{(e.Kind == "KEYE" ? $" loop {e.Loop} {e.LoopStart}-{e.LoopEnd}" : "")}] " + string.Join(" ", e.Keys.Take(e.Kind == "KEYE" ? 64 : 12).Select(v => v.ToString("0.##"))) : "")})";
                var shown = new HashSet<uint>();
                void Tree(uint id, string ind)
                {
                    if (!shown.Add(id) || lib.Get(id) is not { } pt) return;
                    Console.WriteLine($"{ind}PART {id:x8}: " + string.Join(" ", pt.Props.Select(kv => $"{kv.Key}={E(kv.Value)}")));
                    foreach (var sp in pt.Spawns) { Console.WriteLine($"{ind}  KSSM f{sp.Frame} -> {sp.Part:x8}"); Tree(sp.Part, ind + "    "); }
                    foreach (var kv in pt.Props) if (kv.Value is { Cat: MphRecomp.Import.Retro.Part.Cat.Asset, Kind: "PART" } ch) { Console.WriteLine($"{ind}  {kv.Key} -> {ch.Id:x8}"); Tree(ch.Id, ind + "    "); }
                }
                Tree(f.Id, "  ");
                if (Arg(args, "tree") != null) return;
                var sys = new MphRecomp.Campaign.RetroParticleSystem(lib, f.Id, Matrix4.Identity, follow: true, seed: 1);
                var bb = new List<MphRecomp.Campaign.RetroBillboard>();
                var models = new List<MphRecomp.Campaign.RetroModelParticle>();
                for (int k = 0; k < 90; k++)
                {
                    sys.Step();
                    if (k % 3 != 0) continue;
                    bb.Clear(); models.Clear();
                    sys.Collect(bb); sys.CollectModels(models);
                    var groups = bb.GroupBy(b => b.Texture).Select(g => $"tex {g.Key:x8} x{g.Count()} y {g.Min(b => b.Position.Y):0.0}..{g.Max(b => b.Position.Y):0.0} z {g.Min(b => b.Position.Z):0.0}..{g.Max(b => b.Position.Z):0.0} x {g.Min(b => b.Position.X):0.0}..{g.Max(b => b.Position.X):0.0} size {g.Max(b => b.Size):0.0} a {g.Max(b => b.Color.W):0.00}");
                    var sq = models.Select(m => $"{(m.Model == 0 ? "square" : m.Model.ToString("x8"))} at {m.Transform.Row3.Xyz} scale {m.Transform.Row0.Xyz.Length:0.0}/{m.Transform.Row1.Xyz.Length:0.0}/{m.Transform.Row2.Xyz.Length:0.0} a {m.Color.W:0.00}");
                    Console.WriteLine($"  f{k}: {string.Join(" | ", groups)}{(models.Count > 0 ? " || " + string.Join(" | ", sq) : "")}");
                }
                return;
            }
            if (verb == "cratelist")
            {
                // -echoesturret cratelist: each Echoes arena's crates in a quick Battle (EXTRA PICKUPS as the match has it):
                // count, the affinity crates and where, up or not, after 2 s
                if (!ToolPaths.UseDesktopExtraction()) return;
                EchoesArena.Register(Paths.FileSystem);
                foreach (EchoesArena.Def d in EchoesArena.All)
                {
                    var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, 1, 0);
                    using var host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                    var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                    for (int k = 0; k < 120; k++) host.Step(idle);
                    var crates = new List<EchoesCrateEntity>();
                    foreach (EnemyInstanceEntity e in host.Scene.GetEnemyInstanceEntities()) if (e is EchoesCrateEntity c) crates.Add(c);
                    var aff = crates.Where(c => c.Pool.Contains(ItemType.AffinityWeapon)).ToList();
                    Console.WriteLine($"{d.Room}: placed={MphRecomp.Arenas.PlacedPickups.Enabled} crates={MphRecomp.Arenas.PlacedPickups.CratesEnabled}; {crates.Count} crates, "
                        + $"{aff.Count} affinity: {string.Join("; ", aff.Select(c => $"({c.Position.X:0.0},{c.Position.Y:0.0},{c.Position.Z:0.0}) up={c.Up}"))}");
                }
                return;
            }
            if (verb == "beamhit")
            {
                if (!ToolPaths.UseDesktopExtraction()) return;
                BeamHit();
                return;
            }
            if (verb == "beamzones")
            {
                if (!ToolPaths.UseDesktopExtraction()) return;
                BeamZones();
                return;
            }
            if (verb == "botprobe")
            {
                if (!ToolPaths.UseDesktopExtraction()) return;
                EchoesTurretBots.ForceSeat = Arg(args, "force") == "1"; // the dev turret kill test's bots (--es turrettest 1)
                BotProbe(Int32.Parse(Arg(args, "level") ?? "3"), Single.Parse(Arg(args, "seconds") ?? "30", System.Globalization.CultureInfo.InvariantCulture), Arg(args, "put") != "0", Arg(args, "occupy") == "1");
                return;
            }
            if (verb == "netjoin")
            {
                if (!ToolPaths.UseDesktopExtraction()) return;
                NetJoin(Int32.Parse(Arg(args, "hostport") ?? "0"));
                return;
            }
            Console.WriteLine($"-echoesturret: unknown verb {verb} (probe, dump, test, look, nettest)");
        }

        static void Probe(string disc)
        {
            Pak pak = LoadPak(disc);
            EchoesArena.Def def = EchoesArena.Find("M06_ShootingGallery")!;
            Mrea area = Mrea.Read(pak.Get(def.Mrea));
            List<Mrea.ScriptObject> scripts = area.ReadScripts();
            uint[] ids = { 0xf6, 0x119, 0x11d, 0x10e, 0x10b, 0x10c, 0x11b, 0x194, 0x185, 0x258, 0x17e, 0x2cc, 0x3a, 0x2a6, 0xea, 0x114, 0x115 };
            foreach (Mrea.ScriptObject o in scripts.Where(o => ids.Contains(o.Id)))
            {
                float[] x = o.Transform;
                Console.WriteLine($"{o.Type} {o.Id:x8} '{o.Name}' active={o.Active} layer={o.Layer} pos=({x[0]:0.###},{x[1]:0.###},{x[2]:0.###}) rot=({x[3]:0.###},{x[4]:0.###},{x[5]:0.###}) scale=({x[6]:0.###},{x[7]:0.###},{x[8]:0.###})");
                Dump(o.Root, 1);
            }
            void Bounds(uint id)
            {
                if (!pak.Contains(id)) { Console.WriteLine($"model {id:x8}: not in the pak"); return; }
                try
                {
                    Cmdl m = Cmdl.Load(pak.Get, id);
                    float[] lo = { float.MaxValue, float.MaxValue, float.MaxValue }, hi = { float.MinValue, float.MinValue, float.MinValue };
                    for (int i = 0; i + 2 < m.Positions.Length; i += 3)
                    {
                        for (int k = 0; k < 3; k++) { lo[k] = MathF.Min(lo[k], m.Positions[i + k]); hi[k] = MathF.Max(hi[k], m.Positions[i + k]); }
                    }
                    Console.WriteLine($"model {id:x8} ({pak.TypeOf(id)}): {m.TriangleCount} triangles, bounds ({lo[0]:0.###},{lo[1]:0.###},{lo[2]:0.###})..({hi[0]:0.###},{hi[1]:0.###},{hi[2]:0.###})");
                }
                catch (Exception ex) { Console.WriteLine($"model {id:x8}: {ex.Message}"); }
            }
            foreach (uint m in new uint[] { 0xecf072bc, 0x3801de98, 0xb2ed35b1 }) Bounds(m);
            foreach (uint a in new uint[] { 0xe749cb49, 0x0777526b })
            {
                if (!pak.Contains(a)) { Console.WriteLine($"ANCS {a:x8} missing"); continue; }
                Ancs ancs = Ancs.Parse(pak.Get(a));
                foreach (Ancs.Character c in ancs.Characters)
                {
                    Console.WriteLine($"ANCS {a:x8} char '{c.Name}' cmdl {c.Cmdl:x8} cskr {c.Cskr:x8} cinf {c.Cinf:x8}; anims: {string.Join(", ", c.AnimNames.Select(n => $"{n.Index}:{n.Name}"))}");
                    if (pak.Contains(c.Cmdl)) Bounds(c.Cmdl);
                    if (!pak.Contains(c.Cinf)) continue;
                    Cinf sk = Cinf.Load(pak.Get(c.Cinf));
                    foreach (uint b in sk.SortedIds())
                    {
                        Cinf.Bone bone = sk.Bones[b];
                        Console.WriteLine($"   bone {b,3} '{bone.Name}' parent {bone.Parent} at ({bone.X:0.###},{bone.Y:0.###},{bone.Z:0.###})");
                    }
                }
                if (ancs.Set != null)
                {
                    foreach ((string name, Ancs.MetaAnim m) in ancs.Set.Anims) Console.WriteLine($"   anim '{name}' {m.Kind} {m.AnimId:x8} {m.PrimName}");
                }
            }
            // the turret's poses: locators (model space, metres, Z up) at the start and end of each animation
            {
                Ancs ancs = Ancs.Parse(pak.Get(0xe749cb49));
                Ancs.Character c = ancs.Characters[0];
                Cinf sk = Cinf.Load(pak.Get(c.Cinf));
                string[] show = { "upwardballsnap_LCTR", "muzzleend_LCTR", "muzzle_SDK", "turret_SDK", "can01_LCTR", "ball_SDK", "base_SDK", "ring03_SDK" };
                foreach ((string name, Ancs.MetaAnim m) in ancs.Set!.Anims)
                {
                    Anim a = Anim.Load(pak.Get(m.AnimId), echoes: true);
                    foreach (double t in new[] { 0, a.Duration })
                    {
                        Dictionary<uint, (double[] Q, double[] P)> world = Pose(sk, a, t);
                        Console.WriteLine($"pose '{name}' t={t:0.###}/{a.Duration:0.###}: " + string.Join("  ", show.Select(n =>
                        {
                            uint b = sk.Bones.Values.First(x => x.Name == n).Id;
                            (double[] q, double[] p) = world[b];
                            double[] fwd = Rotate(q, new double[] { 0, 0, -1 });
                            return $"{n}=({p[0]:0.###},{p[1]:0.###},{p[2]:0.###})" + (n == "turret_SDK" ? $" -Z->({fwd[0]:0.##},{fwd[1]:0.##},{fwd[2]:0.##})" : "");
                        })));
                    }
                }
            }
        }

        static bool Look(string outDir)
        {
            EchoesArena.Register(Paths.FileSystem);
            EchoesArena.Def def = EchoesArena.Find("M06_ShootingGallery")!;
            string dir = EchoesArena.HdDir(def.Room) ?? Path.Combine(Paths.FileSystem, "_archives", def.Archive, ArenaHdWriter.Dir);
            TurretLook? look = EchoesTurret.LoadLook(dir);
            TurretSpot? spot = EchoesTurret.For(def.Id);
            if (look == null || spot == null)
            {
                Console.WriteLine($"  no turret look in {dir} (re-import the arena)");
                return false;
            }
            static System.Numerics.Matrix4x4 N(Matrix4 m) => new(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34,
                m.M41, m.M42, m.M43, m.M44);
            var pivot = new Vector3(spot.Pivot[0], spot.Pivot[1], spot.Pivot[2]);
            var views = new (string Name, string Clip, float F, float Yaw, float Pitch, bool Holo, bool Ring)[]
            {
                ("free", "up", 0, 0, 0, true, false), ("rising", "up", 0.5f, 0, 0, false, true), ("raised", "up", 1, 0, 0, false, true),
                ("turned", "up", 1, 60, 30, false, true), ("recoil", "shoot", 0.3f, 0, 0, false, true), ("lowering", "down", 0.5f, 0, 0, false, false),
            };
            Directory.CreateDirectory(outDir);
            var eye = new System.Numerics.Vector3(9, 12.5f, 5);
            var target = new System.Numerics.Vector3(0, 8.5f, 17);
            Environment.SetEnvironmentVariable("THUMB_PLACED", "1");
            Environment.SetEnvironmentVariable("THUMB_EYE", FormattableString.Invariant($"{eye.X},{eye.Y},{eye.Z},{target.X},{target.Y},{target.Z}"));
            foreach (var v in views)
            {
                var models = new Dictionary<int, System.Numerics.Matrix4x4?>();
                foreach (TurretPiece p in look.Pieces)
                {
                    if (p.Holo)
                    {
                        models[p.Pass] = v.Holo ? N(EchoesTurret.PieceMatrix(p.Spin, 0.25f)) : null;
                        continue;
                    }
                    float[]? poses = v.Clip == "shoot" ? p.Shoot : v.Clip == "down" ? p.Down : p.Up;
                    Matrix4 m = EchoesTurret.PieceMatrix(poses ?? p.Up, v.F);
                    if (p.Turns) m = m * Matrix4.CreateTranslation(-pivot) * EchoesTurretEntity.Turn(spot.CentreHeading, v.Yaw, v.Pitch) * Matrix4.CreateTranslation(pivot);
                    models[p.Pass] = N(m);
                }
                if (look.RingPass >= 0) models[look.RingPass] = v.Ring ? System.Numerics.Matrix4x4.Identity : null;
                // the arena's other placed passes (the grates) as written
                ArenaThumbnail.PassModels = models;
                string png = Path.GetFullPath(Path.Combine(outDir, $"turret_{v.Name}.png"));
                ArenaThumbnail.Write(dir, new[] { target }, png);
                Console.WriteLine($"  {v.Name}: {png}");
            }
            ArenaThumbnail.PassModels = null;
            return true;
        }

        // ---- -echoesturret nettest: the turret over LAN (T5) ----

        const float NetSeconds = 50;

        // the turret's states as one device saw them: each change of (state, occupant), the seats the slot held, the
        // shots and blow-ups, and the most the barrel turned while each seat had it
        sealed class TurretWatch
        {
            public readonly List<string> Changes = new();
            public readonly SortedSet<int> HeldSeats = new();
            public readonly float[] MaxYaw = new float[PlayerEntity.MaxPlayers];
            string _last = "";

            public void Watch(EchoesTurretEntity t)
            {
                string now = $"{t.State}/{(t.Occupant != null ? t.Occupant.SlotIndex.ToString() : "-")}";
                if (now != _last) Changes.Add(now);
                _last = now;
                if (t.Held != null) HeldSeats.Add(t.Held.SlotIndex);
                if (t.Occupant != null && t.State == EchoesTurretEntity.TurretState.Active)
                {
                    int i = t.Occupant.SlotIndex;
                    MaxYaw[i] = MathF.Max(MaxYaw[i], MathF.Abs(t.Yaw));
                }
            }

            public string Line(EchoesTurretEntity t) => $"TURRET {string.Join(" ", Changes)} | held {string.Join(",", HeldSeats)} | shots {t.Shots}"
                + $" blowups {t.Destructions} | yaw0 {MaxYaw[0]:0} yaw1 {MaxYaw[1]:0}";
        }

        static void RealTime(MphRecomp.Campaign.CampaignHost host, Action step, float seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long frames = 0;
            while (!host.Ended && clock.Elapsed.TotalSeconds < seconds)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * 60);
                while (frames < due && !host.Ended)
                {
                    step();
                    frames++;
                }
                System.Threading.Thread.Sleep(1);
            }
        }

        static EchoesTurretEntity? TurretIn(Scene scene) => MphRecomp.Multiplayer.Net.NetWorld.TurretOf(scene);

        // the host (this process; seat 0 Samus, idle but for its script) and a joiner (a second copy of the tool: seat 1,
        // Sylux). The host puts seat 1 in the turret's box (a teleport: the host's pose wins on the joiner too); the joiner
        // turns the barrel with its own aim, fires 3 shots and gets out. Then the host's own player gets in; the joiner
        // morphs, the host puts its ball in the slot, the joiner's alt attack blows the turret up and kills the host's
        // player (seat 1's kill). PASS = the joiner saw the same turret (state changes, held seats, shots, blow-ups,
        // the barrel's turn) and the same scores.
        static bool NetTest()
        {
            EchoesArena.Register(Paths.FileSystem);
            EchoesArena.Def def = EchoesArena.Find("M06_ShootingGallery")!;
            var template = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, Hunter.Samus, 0, 0);
            template.TimeLimitSeconds = 600;
            template.PointGoal = 99;
            template.Crates = false;
            using var net = MphRecomp.Multiplayer.Net.LanSession.Host(template, Paths.MphKey, "PC host", 2, port: 0);
            string dll = typeof(EchoesTurretTool).Assembly.Location;
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, $"\"{dll}\" -echoesturret netjoin hostport={net.Port}")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(dll)!
            };
            using var joiner = System.Diagnostics.Process.Start(psi)!;
            var output = new List<string>();
            joiner.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add(e.Data); };
            joiner.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add("stderr: " + e.Data); };
            joiner.BeginOutputReadLine();
            joiner.BeginErrorReadLine();
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (net.Joiners.Count(j => !j.Left) < 1 && waited.ElapsedMilliseconds < 30000) System.Threading.Thread.Sleep(20);
            if (net.Joiners.Count(j => !j.Left) < 1)
            {
                Console.WriteLine("host: nobody joined in 30 s");
                joiner.Kill();
                return false;
            }
            using var host = MphRecomp.Campaign.CampaignHost.StartMatch(net.Settings!);
            var driver = new MphRecomp.Multiplayer.MatchNetDriver(net, host);
            EchoesTurretEntity turret = TurretIn(host.Scene)!;
            TurretSpot spot = EchoesTurret.For(host.RoomId)!;
            Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
            Vector3 enter = (V(spot.EnterMin) + V(spot.EnterMax)) / 2, slot = (V(spot.SlotMin) + V(spot.SlotMax)) / 2;
            PlayerEntity me = PlayerEntity.Players[0], guest = PlayerEntity.Players[1];
            void Put(PlayerEntity p, Vector3 at) => p.Teleport(at, -Vector3.UnitZ, host.Scene.GetNodeRefContaining(at));
            var watch = new TurretWatch();
            int phase = 0;
            long since = 0, hostControl = -1;
            var log = new List<string>();
            void Next(string what) { phase++; since = host.Frame; log.Add($"host frame {host.Frame}: {what}"); }
            RealTime(host, () =>
            {
                var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                long t = host.Frame - since;
                switch (phase)
                {
                case 0: // both up
                    if (me.Health == 0 && host.Frame % 2 == 0) input.Buttons = MphRecomp.Campaign.CampaignButtons.Shoot;
                    if (me.LoadFlags.TestFlag(LoadFlags.Spawned) && guest.LoadFlags.TestFlag(LoadFlags.Spawned) && me.Health > 0 && guest.Health > 0 && host.Frame > 180)
                    {
                        Put(me, V(spot.Base) + new Vector3(0, 1, -14));
                        Put(guest, enter.WithY(spot.EnterMax[1]));
                        Next("seat 1 into the turret's box");
                    }
                    break;
                case 1: // seat 1 plays the turret, gets out; the turret frees
                    if (turret.Occupant == guest && turret.State == EchoesTurretEntity.TurretState.Active) log.Add($"(seat 1 in control at {host.Frame})");
                    if (t > 120 && turret.State == EchoesTurretEntity.TurretState.Free && !guest.HostSeated)
                    {
                        Put(me, enter.WithY(spot.EnterMax[1]));
                        Next("the host's player into the box");
                    }
                    break;
                case 2: // the host's player in control: turns the barrel 45 right, 2 shots
                    if (turret.State == EchoesTurretEntity.TurretState.Active && turret.Occupant == me)
                    {
                        if (hostControl < 0) hostControl = host.Frame;
                        long c = host.Frame - hostControl;
                        if (c >= 10 && c < 25) input.AimDelta = new System.Numerics.Vector2(4 * 3, 0);
                        if (c == 40 || c == 110) input.Buttons = MphRecomp.Campaign.CampaignButtons.Shoot;
                        if (c > 140 && guest.IsAltForm && guest.Health > 0)
                        {
                            Put(guest, slot);
                            Next("seat 1's ball into the slot");
                        }
                    }
                    break;
                case 3:
                    if (turret.State == EchoesTurretEntity.TurretState.Destroyed && phase == 3) Next("blown up");
                    break;
                }
                driver.Step(input);
                watch.Watch(turret);
            }, NetSeconds);
            string hostLine = watch.Line(turret);
            string hostScores = OnlineMatchTool.Scores(net.Settings!.Players.Count, host.Scene);
            foreach (string l in log.Where(l => !l.StartsWith("(")).Concat(log.Where(l => l.StartsWith("(")).Take(1))) Console.WriteLine("  " + l);
            Console.WriteLine("host:   " + hostLine);
            Console.WriteLine("host:   " + hostScores);
            net.Dispose();
            if (!joiner.WaitForExit(30000)) joiner.Kill();
            joiner.WaitForExit();
            string? guestLine, guestScores;
            lock (output)
            {
                foreach (string l in output) Console.WriteLine("  [joiner] " + l);
                guestLine = output.FirstOrDefault(l => l.StartsWith("TURRET "));
                guestScores = output.FirstOrDefault(l => l.StartsWith("SCORES "));
            }
            var problems = new List<string>();
            if (phase < 4) problems.Add($"the script stopped at phase {phase}");
            if (guestLine != hostLine) problems.Add($"the joiner's turret differs: {guestLine ?? "none"}");
            if (guestScores != hostScores) problems.Add($"the joiner's scores differ: {guestScores ?? "none"}");
            if (watch.MaxYaw[1] < 30) problems.Add($"seat 1 didn't turn the barrel on the host ({watch.MaxYaw[1]:0})");
            if (watch.MaxYaw[0] < 30) problems.Add($"the host's player didn't turn the barrel ({watch.MaxYaw[0]:0})");
            if (!hostLine.Contains("shots 5")) problems.Add("not 5 shots (3 + 2)");
            if (!hostLine.Contains("held 1")) problems.Add("the slot never held seat 1");
            if (!hostLine.Contains("blowups 1")) problems.Add("no blow-up");
            if (GameState.Kills[1] < 1) problems.Add("seat 1 has no kill");
            Console.WriteLine(problems.Count == 0 ? "turret net test: PASS" : "turret net test: FAIL\n  " + string.Join("\n  ", problems));
            return problems.Count == 0;
        }

        // the joiner: seat 1. Seated with control: turns the barrel 60 degrees with its aim, fires 3 shots, gets out;
        // with the host's player in the turret: morphs; held in the slot: its alt attack. Respawns when dead.
        static void NetJoin(int hostPort)
        {
            EchoesArena.Register(Paths.FileSystem);
            using var net = MphRecomp.Multiplayer.Net.LanSession.Browse(Paths.MphKey);
            var found = System.Diagnostics.Stopwatch.StartNew();
            MphRecomp.Multiplayer.Net.LanHostInfo? target = null;
            while (target == null && found.ElapsedMilliseconds < 10000)
            {
                target = net.Hosts.FirstOrDefault(h => h.Address.Port == hostPort && !h.Running);
                System.Threading.Thread.Sleep(50);
            }
            if (target == null || !net.Join(target.Address, Hunter.Sylux, "PC client"))
            {
                Console.WriteLine("joiner: no host (" + net.Status + ")");
                return;
            }
            using var host = MphRecomp.Campaign.CampaignHost.StartMatch(net.Settings!);
            var driver = new MphRecomp.Multiplayer.MatchNetDriver(net, host);
            EchoesTurretEntity turret = TurretIn(host.Scene)!;
            PlayerEntity me = host.Player;
            var watch = new TurretWatch();
            long control = -1, held = -1;
            int seatedFrames = 0, morphs = 0;
            float seatError = 0;
            RealTime(host, () =>
            {
                var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                if (me.Health == 0)
                {
                    if (host.Frame % 2 == 0) input.Buttons = MphRecomp.Campaign.CampaignButtons.Shoot;
                }
                else if (me.HostSeated)
                {
                    seatedFrames++;
                    seatError = MathF.Max(seatError, (me.Position - me.HostSeatPosition).Length);
                    if (me.HostSeatControl && control < 0) control = host.Frame;
                    long t = control < 0 ? -1 : host.Frame - control;
                    if (t >= 10 && t < 40) input.AimDelta = new System.Numerics.Vector2(-4 * 2, 0); // 2 degrees a step: 60 left
                    if (t == 60 || t == 130 || t == 200) input.Buttons = MphRecomp.Campaign.CampaignButtons.Shoot;
                    if (t >= 260 && t < 264) input.Buttons = MphRecomp.Campaign.CampaignButtons.Jump;
                }
                else if (turret.Occupant == PlayerEntity.Players[0] && turret.State == EchoesTurretEntity.TurretState.Active && !me.IsAltForm
                    && !me.IsMorphing && morphs < 3 && host.Frame % 30 == 0)
                {
                    morphs++;
                    input.Buttons = MphRecomp.Campaign.CampaignButtons.Morph;
                }
                else if (me.HostSlotHeld)
                {
                    if (held < 0) held = host.Frame;
                    if (host.Frame - held >= 30 && host.Frame - held < 34) input.Buttons = MphRecomp.Campaign.CampaignButtons.AltAttack;
                }
                driver.Step(input);
                watch.Watch(turret);
            }, NetSeconds + 1);
            Console.WriteLine($"joiner: seated {seatedFrames} frames (feet off the seat at most {seatError:0.000}), control at {control}, "
                + $"held at {held}, morph presses {morphs}, pulled back {host.LocalCorrections} times");
            Console.WriteLine(watch.Line(turret));
            Console.WriteLine(OnlineMatchTool.Scores(net.Settings!.Players.Count, host.Scene));
        }

        // -echoesturret beamzones: a bot's Power Beam shot at the middle of every triangle of the turret's
        // hit mesh from 5 units out along its outward normal (and from 30 degrees either side): which zones don't hurt the
        // turret, and what each missed shot hit instead (dev; owner 10-17: 'several zones where shooting the cannon
        // doesn't hit it')
        static void BeamZones()
        {
            EchoesArena.Register(Paths.FileSystem);
            EchoesArena.Def def = EchoesArena.Find("M06_ShootingGallery")!;
            var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, Hunter.Samus, 1, 0);
            settings.TimeLimitSeconds = 36000;
            settings.PointGoal = 999;
            using var host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            EchoesTurretEntity turret = TurretIn(host.Scene)!;
            TurretSpot spot = EchoesTurret.For(host.RoomId)!;
            Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
            Vector3 enter = (V(spot.EnterMin) + V(spot.EnterMax)) / 2;
            PlayerEntity me = PlayerEntity.Main, bot = PlayerEntity.Players[1];
            for (int k = 0; k < 600 && !bot.LoadFlags.TestFlag(LoadFlags.Spawned); k++) host.Step(idle);
            bot.IsBot = false;
            for (int k = 0; k < 120; k++) host.Step(idle);
            for (int k = 0; k < 900 && turret.State != EchoesTurretEntity.TurretState.Active; k++)
            {
                if (k % 60 == 0 && turret.State == EchoesTurretEntity.TurretState.Free)
                    me.Teleport(enter.WithY(spot.EnterMax[1]), -Vector3.UnitZ, host.Scene.GetNodeRefContaining(enter));
                host.Step(idle);
            }
            for (int k = 0; k < 60; k++) host.Step(idle);
            var hpField = typeof(EchoesTurretEntity).GetField("_hp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            hpField.SetValue(turret, 100000); // (never destroyed)
            float[] sv = spot.ShapeVerts!;
            int[] st = spot.ShapeTris!;
            Vector3 P(int i) => new Vector3(sv[3 * i], sv[3 * i + 1], sv[3 * i + 2]);
            Vector3 mid = Vector3.Zero;
            for (int i = 0; i < sv.Length / 3; i++) mid += P(i);
            mid /= sv.Length / 3;
            Console.WriteLine($"  seated {me.HostSeated}, state {turret.State}, pivot {turret.PivotNow}, hp {turret.Hp}, mesh centre {mid}");
            Console.WriteLine($"  column {V(spot.ColumnMin!)} .. {V(spot.ColumnMax!)}; base {(spot.BaseMin is { } bm ? V(bm) : default)} .. {(spot.BaseMax is { } bx ? V(bx) : default)}; base now {turret.BaseEntity?.Position}; turret transform {turret.Transform}");
            int shots = 0, hits = 0, blocked = 0;
            var missed = new List<string>();
            for (int k = 0; k + 2 < st.Length; k += 3)
            {
                Vector3 a = P(st[k]), b = P(st[k + 1]), c = P(st[k + 2]);
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared < 1e-10f) continue;
                n.Normalize();
                Vector3 cen = (a + b + c) / 3;
                if (Vector3.Dot(n, cen - mid) < 0) n = -n;
                Vector3 side = Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX).Normalized();
                foreach (float tilt in new[] { 0f, 30f, -30f })
                {
                    if (turret.State != EchoesTurretEntity.TurretState.Active) break;
                    float r = MathHelper.DegreesToRadians(tilt);
                    Vector3 wn = Vector3.TransformVector(n * MathF.Cos(r) + side * MathF.Sin(r), turret.Transform).Normalized();
                    Vector3 target = Vector3.TransformPosition(cen, turret.Transform);
                    Vector3 from = target + wn * 5;
                    // the way in clear of the room (else it's no fair test)
                    CollisionResult cr = default;
                    if (CollisionDetection.CheckBetweenPoints(from, target, TestFlags.None, host.Scene, ref cr) && cr.EntityCollision == null)
                    {
                        blocked++;
                        continue;
                    }
                    int hp = turret.Hp, myHp = me.Health;
                    var before = new HashSet<BeamProjectileEntity>(); foreach (BeamProjectileEntity x in host.Scene.GetBeamProjectileEntities()) before.Add(x);
                    bot.Teleport(from - (target - from).Normalized() * 0.5f, (target - from).Normalized(), host.Scene.GetNodeRefContaining(from)); bot.PrevPosition = bot.Position;
                    BeamProjectileEntity.Spawn(bot, bot.EquipInfo, from, (target - from).Normalized(), BeamSpawnFlags.None, bot.NodeRef, host.Scene);
                    BeamProjectileEntity? sh = null;
                    foreach (BeamProjectileEntity x in host.Scene.GetBeamProjectileEntities()) if (!before.Contains(x)) sh = x;
                    Vector3 last = from;
                    Vector3 endAt = from; bool col = false; for (int j = 0; j < 120 && sh != null && sh.Lifespan > 0 && !sh.Flags.TestFlag(BeamFlags.Collided); j++) { last = sh.Position; host.Step(idle); endAt = sh.Position; col = sh.Flags.TestFlag(BeamFlags.Collided); }
                    host.Step(idle);
                    if (sh == null) { blocked++; continue; } // (no shot: the spot is outside the room)
                    shots++;
                    if (turret.Hp < hp) hits++;
                    else
                    {
                        CollisionResult rr = default;
                        bool any = CollisionDetection.CheckBetweenPoints(from, from + (target - from).Normalized() * 20, TestFlags.Beams, host.Scene, ref rr);
                        string what = any ? (rr.EntityCollision?.Entity?.GetType().Name ?? "room") + $" at {(rr.Position - target).Length:0.00} from the aim ({rr.Position})" : "nothing";
                        missed.Add($"tri {k / 3} local {cen} world {target} n {n} tilt {tilt}: shot {(col ? "collided" : "no collision")} at {(endAt - target).Length:0.00} from the aim ({endAt}), speed {sh?.Speed}; ray hits {what}; my hp {myHp}->{me.Health}");
                    }
                    for (int j = 0; j < 8; j++) host.Step(idle);
                }
            }
            Console.WriteLine($"  {shots} shots: {hits} hurt the turret, {shots - hits} didn't; {blocked} skipped (the room in the way, or no shot from there)");
            foreach (string m in missed) Console.WriteLine("    " + m);
        }

        // -echoesturret beamhit: the player seated, a bot's Power Beam shots from around the turret at its pivot: does each
        // hit the turret's collision (its health) (dev)
        static void BeamHit()
        {
            EchoesArena.Register(Paths.FileSystem);
            EchoesArena.Def def = EchoesArena.Find("M06_ShootingGallery")!;
            var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, Hunter.Samus, 1, 0);
            settings.TimeLimitSeconds = 36000;
            settings.PointGoal = 999;
            using var host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            EchoesTurretEntity turret = TurretIn(host.Scene)!;
            TurretSpot spot = EchoesTurret.For(host.RoomId)!;
            Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
            Vector3 enter = (V(spot.EnterMin) + V(spot.EnterMax)) / 2;
            PlayerEntity me = PlayerEntity.Main, bot = PlayerEntity.Players[1];
            for (int k = 0; k < 600 && !bot.LoadFlags.TestFlag(LoadFlags.Spawned); k++) host.Step(idle);
            bot.IsBot = false;
            for (int k = 0; k < 120; k++) host.Step(idle);
            for (int k = 0; k < 900 && turret.State != EchoesTurretEntity.TurretState.Active; k++)
            {
                if (k % 60 == 0 && turret.State == EchoesTurretEntity.TurretState.Free)
                    me.Teleport(enter.WithY(spot.EnterMax[1]), -Vector3.UnitZ, host.Scene.GetNodeRefContaining(enter));
                host.Step(idle);
            }
            for (int k = 0; k < 60; k++) host.Step(idle);
            Vector3 pivot = turret.PivotNow;
            if (spot.ShapeVerts is { } sv && spot.ShapeTris is { } st)
            {
                Vector3 P(int i) => new Vector3(sv[3 * i], sv[3 * i + 1], sv[3 * i + 2]);
                Vector3 mid = Vector3.Zero;
                for (int i = 0; i < sv.Length / 3; i++) mid += P(i);
                mid /= sv.Length / 3;
                int outward = 0, inward = 0;
                for (int k = 0; k + 2 < st.Length; k += 3)
                {
                    Vector3 a = P(st[k]), b = P(st[k + 1]), c = P(st[k + 2]);
                    Vector3 n = Vector3.Cross(b - a, c - a);
                    if (Vector3.Dot(n, (a + b + c) / 3 - mid) > 0) outward++; else inward++;
                }
                Console.WriteLine($"  shape: {st.Length / 3} triangles, normals out {outward}, in {inward} (centre {mid})");
            }
            Console.WriteLine($"  seated: {me.HostSeated}, state {turret.State}, pivot {pivot}, hp {turret.Hp}, my volume {me.Volume.Type} {me.Volume.SpherePosition - pivot} r {me.Volume.SphereRadius}");
            foreach (Vector3 off in new[] { new Vector3(0, -2, -12), new Vector3(0, -6, -10), new Vector3(8, -6, -6), new Vector3(-10, -4, 0),
                new Vector3(0, -6, 10), new Vector3(0, 0, -15), new Vector3(6, 3, -10) })
            {
                Vector3 from = pivot + off;
                foreach (Vector3 aim in new[] { pivot, pivot + new Vector3(0, -1.5f, 0), pivot + new Vector3(0, 1.5f, 0) })
                {
                    int hp = turret.Hp, myHp = me.Health;
                    CollisionResult cr = default;
                    bool wall = CollisionDetection.CheckBetweenPoints(from, from + (aim - from).Normalized() * 40, TestFlags.None, host.Scene, ref cr);
                    var before = new HashSet<BeamProjectileEntity>();
                    foreach (BeamProjectileEntity x in host.Scene.GetBeamProjectileEntities()) before.Add(x);
                    BeamProjectileEntity.Spawn(bot, bot.EquipInfo, from, (aim - from).Normalized(), BeamSpawnFlags.None, bot.NodeRef, host.Scene);
                    BeamProjectileEntity? b = null;
                    foreach (BeamProjectileEntity x in host.Scene.GetBeamProjectileEntities()) if (!before.Contains(x)) b = x;
                    Vector3 last = from;
                    for (int k = 0; k < 120 && b != null && b.Lifespan > 0 && !b.Flags.TestFlag(BeamFlags.Collided); k++) { last = b.Position; host.Step(idle); }
                    host.Step(idle);
                    Console.WriteLine($"      ray: {(wall ? $"{(cr.EntityCollision?.Entity?.GetType().Name ?? "room")} at {cr.Position - pivot}" : "nothing")}; my hp {myHp} -> {me.Health}");
                    Console.WriteLine($"    from {off} at {aim - pivot}: {(b == null ? "no shot" : $"stopped {(last - pivot).Length:0.0} from the pivot ({last - pivot})")}, hp {hp} -> {turret.Hp}");
                    for (int k = 0; k < 20; k++) host.Step(idle);
                }
            }
            // the splash on crates: a turret shot sent down onto the floor beside each crate, at several distances from it
            for (int k = 0; k < 20 * 60; k++) host.Step(idle);
            var crates = new List<MphRecomp.Arenas.EchoesCrateEntity>();
            foreach (EnemyInstanceEntity e in host.Scene.GetEnemyInstanceEntities()) if (e is MphRecomp.Arenas.EchoesCrateEntity c) crates.Add(c);
            Console.WriteLine($"  crates: {crates.Count}, up {crates.Count(c => c.Up)}; splash radius {spot.SplashRadius:0.00}");
            foreach (MphRecomp.Arenas.EchoesCrateEntity c in crates.Take(1))
            {
                foreach (float dist in new[] { 1.5f, 3f, 4.5f, 5.5f, 6.5f })
                {
                    for (int k = 0; k < 40 * 60 && !c.Up; k++) host.Step(idle);
                    int breaks = c.Breaks;
                    var before = new HashSet<BeamProjectileEntity>();
                    foreach (BeamProjectileEntity x in host.Scene.GetBeamProjectileEntities()) before.Add(x);
                    for (int k = 0; k < 90; k++) host.Step(idle);
                    me.HostSeatFire = true;
                    host.Step(idle);
                    BeamProjectileEntity? b = null;
                    foreach (BeamProjectileEntity x in host.Scene.GetBeamProjectileEntities()) if (!before.Contains(x) && x.Equip?.Weapon is { } w && EchoesTurret.IsShotWeapon(w)) b = x;
                    if (b == null) { Console.WriteLine($"    no turret shot ({turret.State}, seated {me.HostSeated}, hp {me.Health})"); continue; }
                    // a side with floor within a unit and a half under the crate's centre
                    Vector3 side = new Vector3(dist, 3, 0);
                    for (int a = 0; a < 16; a++)
                    {
                        float ang = a * MathF.PI / 8;
                        var tryS = new Vector3(MathF.Cos(ang) * dist, 3, MathF.Sin(ang) * dist);
                        CollisionResult fl = default;
                        if (CollisionDetection.CheckBetweenPoints(c.Position + tryS, c.Position + tryS - Vector3.UnitY * 6, TestFlags.None, host.Scene, ref fl)
                            && MathF.Abs(fl.Position.Y - (c.Position.Y - 1.15f)) < 0.3f) { side = tryS; break; }
                    }
                    b.Position = b.BackPosition = c.Position + side;
                    b.Direction = -Vector3.UnitY;
                    b.Velocity = -Vector3.UnitY * b.Velocity.Length;
                    Vector3 last = b.Position;
                    for (int k = 0; k < 60 && b.Lifespan > 0 && !b.Flags.TestFlag(BeamFlags.Collided); k++) { last = b.Position; host.Step(idle); }
                    for (int k = 0; k < 3; k++) host.Step(idle);
                    Console.WriteLine($"    crate at {c.Position}: blast at {last - c.Position} (flat {(last - c.Position).Xz.Length:0.00}): {(c.Breaks > breaks ? "BROKE" : "intact")}");
                }
            }
            // shots into the sky: where (if anywhere) each bursts (owner: explosions up in the sky)
            BeamProjectileEntity.HostOwnLookWeapon = EchoesTurret.IsShotWeapon;   // (the app's: hits counted on the shot)
            foreach ((float yawD, float pitchD) in new[] { (0f, 10f), (0f, 25f), (0f, 45f), (40f, 20f), (-40f, 20f), (80f, 30f), (-80f, 30f), (0f, 70f), (180f, 30f), (0f, 0f) })
            {
                var before = new HashSet<BeamProjectileEntity>();
                foreach (BeamProjectileEntity x in host.Scene.GetBeamProjectileEntities()) before.Add(x);
                for (int k = 0; k < 90; k++) host.Step(idle);
                me.HostSeatFire = true;
                host.Step(idle);
                BeamProjectileEntity? b = null;
                // (the turret's shots come from a pool: the live one with the most life left)
                foreach (BeamProjectileEntity x in host.Scene.GetBeamProjectileEntities())
                    if (x.Equip?.Weapon is { } w && EchoesTurret.IsShotWeapon(w) && x.Lifespan > 0 && !x.Flags.TestFlag(BeamFlags.Collided) && (b == null || x.Lifespan > b.Lifespan)) b = x;
                if (b == null) { Console.WriteLine($"    sky: no shot ({turret.State}, seated {me.HostSeated})"); continue; }
                float h = MathHelper.DegreesToRadians(yawD), pt = MathHelper.DegreesToRadians(pitchD);
                var dir = new Vector3(MathF.Cos(pt) * MathF.Sin(h), MathF.Sin(pt), -MathF.Cos(pt) * MathF.Cos(h));
                b.Direction = dir;
                b.Velocity = dir * b.Velocity.Length;
                int hits = b.HostHits;
                Vector3 start = b.Position, last = b.Position;
                int steps = 0;
                for (; steps < 600 && b.Lifespan > 0 && !b.Flags.TestFlag(BeamFlags.Collided); steps++) { last = b.Position; host.Step(idle); }
                Console.WriteLine($"    sky yaw {yawD} pitch {pitchD}: {steps} steps, travelled {(last - start).Length:0.0}, hits {b.HostHits - hits} at {b.HostHitAt - pivot} normal {b.HostHitNormal} expired {b.HostHitExpired} lifespan {b.Lifespan}");
            }
            // getting out: where she goes
            var jump = idle;
            jump.Buttons = MphRecomp.Campaign.CampaignButtons.Jump;
            host.Step(jump);
            for (int k = 0; k < 50; k++)
            {
                host.Step(idle);
                if (k < 12 || k % 4 == 0)
                    Console.WriteLine($"    exit {k}: {turret.State} pos {me.Position - pivot} speed {me.Speed} grounded {me.Flags1.TestFlag(PlayerFlags1.Grounded)} cam {me.CameraInfo.Position - pivot}");
            }
        }

        // -echoesturret botprobe [level=] [seconds=] [put=0] [occupy=1] [force=1: EchoesTurretBots.ForceSeat]: a bot put in the turret (put=0: left alone; occupy=1:
        // the idle player put back in it whenever it is free, for the bots' bomb-slot runs); what they do there (dev)
        static void BotProbe(int level, float seconds, bool put, bool occupy)
        {
            EchoesArena.Register(Paths.FileSystem);
            EchoesArena.Def def = EchoesArena.Find("M06_ShootingGallery")!;
            var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, Hunter.Samus, 3, level);
            settings.TimeLimitSeconds = 36000;
            settings.PointGoal = 999;
            using var host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            EchoesTurretEntity turret = TurretIn(host.Scene)!;
            TurretSpot spot = EchoesTurret.For(host.RoomId)!;
            Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
            Vector3 enter = (V(spot.EnterMin) + V(spot.EnterMax)) / 2;
            PlayerEntity bot = PlayerEntity.Players[1];
            for (int k = 0; k < 600 && !bot.LoadFlags.TestFlag(LoadFlags.Spawned); k++) host.Step(idle);
            for (int k = 0; k < 120; k++) host.Step(idle);
            if (put) bot.Teleport(enter.WithY(spot.EnterMax[1]), -Vector3.UnitZ, host.Scene.GetNodeRefContaining(enter));
            int kills0 = GameState.Kills[1];
            float minYaw = 0, maxYaw = 0;
            long control = -1;
            var log = new List<string>();
            EchoesTurretEntity.TurretState last = turret.State;
            int shots0 = turret.Shots, jumps = 0, shootDown = 0, aimFrames = 0;
            var closest = new float[PlayerEntity.Players.Count];
            Array.Fill(closest, 999);
            int slotHeld = 0, meSeated = 0, hpLost = 0, lastHp = turret.Hp, meLost = 0, meLast = PlayerEntity.Main.Health;
            var targetMe = new int[PlayerEntity.Players.Count];
            var shootAtMe = new int[PlayerEntity.Players.Count];
            var morphs = new int[PlayerEntity.Players.Count];
            int slotMorphs = 0;
            var shotNear = new Dictionary<BeamProjectileEntity, (int Slot, BeamType Beam, Vector3 From, Vector3 Dir, float Near, Vector3 Last)>();
            var wasBall = new bool[PlayerEntity.Players.Count];
            PlayerEntity me = PlayerEntity.Main;
            for (int f = 0; f < seconds * 60; f++)
            {
                if (occupy && turret.State == EchoesTurretEntity.TurretState.Free && me.Health > 0 && !me.HostSeated && f % 60 == 0)
                {
                    me.Teleport(enter.WithY(spot.EnterMax[1]), -Vector3.UnitZ, host.Scene.GetNodeRefContaining(enter));
                }
                host.Step(idle);
                if (PlayerEntity.Players.Any(p => p.IsBot && p.HostSlotHeld)) slotHeld++;
                if (me.HostSeated && turret.Occupant == me)
                {
                    meSeated++;
                    if (turret.Hp < lastHp) hpLost += lastHp - turret.Hp;
                    if (me.Health < meLast) meLost += meLast - me.Health;
                    foreach (BeamProjectileEntity b in host.Scene.GetBeamProjectileEntities())
                    {
                        if (b.Owner is not PlayerEntity o || !o.IsBot || b.Lifespan <= 0) continue;
                        float dd = (b.Position - turret.PivotNow).Length;
                        if (!shotNear.TryGetValue(b, out var r) || r.From != b.SpawnPosition) r = (o.SlotIndex, b.Beam, b.SpawnPosition, b.Direction, dd, b.Position);
                        shotNear[b] = r with { Near = MathF.Min(r.Near, dd), Last = b.Position };
                    }
                    for (int i = 1; i < targetMe.Length; i++)
                    {
                        PlayerEntity p = PlayerEntity.Players[i];
                        if (!p.IsBot || p.Health == 0) continue;
                        if (p.IsAltForm != wasBall[i]) morphs[i]++;
                        if (p.IsAltForm != wasBall[i] && (p.Position - turret.SlotCentre).Length < 10) slotMorphs++;
                        wasBall[i] = p.IsAltForm;
                        if (p.AiData.HostTargetPlayer == me) targetMe[i]++;
                        if (p.AiData.HostTargetPlayer == me && p.Controls.Shoot.IsDown) shootAtMe[i]++;
                    }
                }
                lastHp = turret.Hp;
                meLast = me.Health;
                if (turret.State != last) { log.Add($"{f}: {turret.State} occ {turret.Occupant?.SlotIndex.ToString() ?? "-"}"); last = turret.State; }
                if (turret.Occupant == bot && turret.State == EchoesTurretEntity.TurretState.Active)
                {
                    if (control < 0) control = f;
                    minYaw = MathF.Min(minYaw, turret.Yaw);
                    maxYaw = MathF.Max(maxYaw, turret.Yaw);
                    if (bot.Controls.Jump.IsDown) jumps++;
                    if (bot.Controls.Shoot.IsDown) shootDown++;
                    if (MathF.Abs(turret.Yaw) > 0.5f) aimFrames++;
                }
                for (int i = 1; i < closest.Length; i++)
                {
                    PlayerEntity p = PlayerEntity.Players[i];
                    if (!p.LoadFlags.TestFlag(LoadFlags.Active) || p.Health == 0) continue;
                    Vector3 d = turret.EnterCentre - p.Position;
                    if (MathF.Abs(d.Y) < 2.5f) closest[i] = MathF.Min(closest[i], d.Xz.Length);
                }
                if (f % 60 == 0 && turret.Occupant == bot)
                    log.Add($"  {f}: yaw {turret.Yaw:0} pitch {turret.Pitch:0} shots {turret.Shots - shots0}");
            }
            foreach (string l in log.Take(60)) Console.WriteLine("  " + l);
            Console.WriteLine($"bot level {level}: control at {control}, yaw {minYaw:0}..{maxYaw:0}, shots {turret.Shots - shots0}, "
                + $"jump-down frames {jumps}, shoot-down frames {shootDown}, kills {GameState.Kills[1] - kills0}, entries {turret.Entries}");
            if (host.Scene.Room?.NodeData is { } nodes)
            {
                var near = nodes.Data.SelectMany(a => a).SelectMany(b => b).Select(d => (d, dist: (d.Position - turret.EnterCentre).Length))
                    .OrderBy(x => x.dist).Take(6);
                Console.WriteLine("  nav nodes nearest the enter box: " + string.Join(", ", near.Select(x => $"{x.dist:0.0} at ({x.d.Position.X:0.0}, {x.d.Position.Y:0.0}, {x.d.Position.Z:0.0})")));
                if (Environment.GetEnvironmentVariable("NAVDUMP") == "1")
                {
                    var all = nodes.Data.SelectMany(a => a).SelectMany(b => b).ToList();
                    foreach (var x in near)
                    {
                        var nb = Enumerable.Range(0, x.d.Count2).Select(i => x.d.Values[x.d.Index2 + i]).Select(id => all.FirstOrDefault(q => q.Id == id))
                            .Where(q => q != null).Select(q => $"{q!.Id}({q.Position.X:0.0},{q.Position.Y:0.0},{q.Position.Z:0.0})");
                        Console.WriteLine($"    node {x.d.Id} {x.d.NodeType} ({x.d.Position.X:0.0},{x.d.Position.Y:0.0},{x.d.Position.Z:0.0}) max {x.d.MaxDistance:0.0} -> {string.Join(" ", nb)}");
                    }
                }
            }
            Console.WriteLine($"  me seated {meSeated / 60f:0.0} s: turret hp lost {hpLost}, my health lost {meLost}; bots targeting me (s) "
                + string.Join(", ", targetMe.Skip(1).Select(x => (x / 60f).ToString("0.0"))) + ", shooting at me (s) "
                + string.Join(", ", shootAtMe.Skip(1).Select(x => (x / 60f).ToString("0.0"))) + "; morphs in or out " + string.Join(", ", morphs.Skip(1)) + $" ({slotMorphs} within 10 of the slot)");
            foreach (var r in shotNear.Values.Take(25))
                Console.WriteLine($"    shot bot {r.Slot} {r.Beam} from ({r.From.X:0.0},{r.From.Y:0.0},{r.From.Z:0.0}) dir ({r.Dir.X:0.00},{r.Dir.Y:0.00},{r.Dir.Z:0.00}) nearest pivot {r.Near:0.0} last ({r.Last.X:0.0},{r.Last.Y:0.0},{r.Last.Z:0.0})");
            if (spot.ShapeVerts is { } sv)
            {
                var xs = sv.Where((_, i) => i % 3 == 0); var ys = sv.Where((_, i) => i % 3 == 1); var zs = sv.Where((_, i) => i % 3 == 2);
                Console.WriteLine($"  shape {sv.Length / 3} verts: x {xs.Min():0.00}..{xs.Max():0.00} y {ys.Min():0.00}..{ys.Max():0.00} z {zs.Min():0.00}..{zs.Max():0.00} (about the pivot)");
            }
            Console.WriteLine($"  pivot ({turret.PivotNow.X:0.0},{turret.PivotNow.Y:0.0},{turret.PivotNow.Z:0.0}), me ({me.Position.X:0.0},{me.Position.Y:0.0},{me.Position.Z:0.0}) cam ({me.CameraInfo.Position.X:0.0},{me.CameraInfo.Position.Y:0.0},{me.CameraInfo.Position.Z:0.0}), {shotNear.Count} bot shots");
            Console.WriteLine($"  destroyed {turret.Destructions} times, a bot held in the slot {slotHeld / 60f:0.0} s");
            int[] c = EchoesTurretBots.Counts;
            Console.WriteLine($"  bot brain: seat dice {c[0]}, seat errands {c[1]}, slot dice {c[2]}, slot errands {c[3]}, hops {c[4]}, jump steps {c[5]}; closest (level) to the enter box: "
                + string.Join(", ", closest.Skip(1).Where(x => x < 999).Select(x => x.ToString("0.0"))));
        }

        // -echoesturret sounds [disc=]: Shooting Gallery's sound objects (SOND) with every property and who messages them
        // (dev: which sounds Echoes plays for the turret and the slot)
        static void Sounds(string disc)
        {
            Pak pak = LoadPak(disc);
            EchoesArena.Def def = EchoesArena.Find("M06_ShootingGallery")!;
            Mrea area = Mrea.Read(pak.Get(def.Mrea));
            List<Mrea.ScriptObject> scripts = area.ReadScripts();
            var byId = scripts.GroupBy(o => o.Id & 0xFFFF).ToDictionary(g => g.Key, g => g.First());
            string Of(uint id) => byId.TryGetValue(id & 0xFFFF, out var o) ? $"{o.Type} {o.Id & 0xFFFF:x} '{o.Name}'" : $"? {id:x}";
            foreach (Mrea.ScriptObject o in scripts.Where(o => o.Type == "SOND"))
            {
                float[] x = o.Transform;
                Console.WriteLine($"SOND {o.Id & 0xFFFF:x} '{o.Name}' layer {o.Layer} active {o.Active} at ({x[0]:0.0}, {x[1]:0.0}, {x[2]:0.0})");
                Dump(o.Root, 1);
                foreach (Mrea.ScriptObject from in scripts)
                {
                    foreach (var c in from.Connections.Where(c => (c.Target & 0xFFFF) == (o.Id & 0xFFFF)))
                    {
                        Console.WriteLine($"    <- {Of(from.Id)}: {c.State} -> {c.Message}");
                    }
                }
            }
        }

        static bool Test()
        {
            EchoesArena.Register(Paths.FileSystem);
            EchoesArena.Def def = EchoesArena.Find("M06_ShootingGallery")!;
            int fails = 0;
            void Check(bool ok, string what)
            {
                if (!ok) fails++;
                Console.WriteLine($"    {(ok ? "ok  " : "FAIL")} {what}");
            }
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            MphRecomp.Campaign.CampaignInput Press(MphRecomp.Campaign.CampaignButtons b) { var i = idle; i.Buttons = b; return i; }
            foreach (Hunter hunter in new[] { Hunter.Samus, Hunter.Spire, Hunter.Kanden, Hunter.Sylux, Hunter.Noxus, Hunter.Trace, Hunter.Weavel })
            {
                Console.WriteLine($"  Shooting Gallery, the bomb slot's player as {hunter}:");
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, hunter, 1, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                settings.Crates = false;
                using var host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                EchoesTurretEntity? turret = null;
                foreach (EnemyInstanceEntity e in host.Scene.GetEnemyInstanceEntities()) if (e is EchoesTurretEntity t) turret = t;
                Check(turret != null, $"the turret is in the match (on: {EchoesTurret.Enabled})");
                if (turret == null) return false;
                PlayerEntity me = PlayerEntity.Main, dummy = PlayerEntity.Players[1];
                dummy.IsBot = false;
                void Step(int n, MphRecomp.Campaign.CampaignInput? input = null) { for (int k = 0; k < n; k++) host.Step(input ?? idle); }
                void Revive(PlayerEntity p)
                {
                    bool bot = p.IsBot;
                    p.IsBot = true;
                    for (int k = 0; k < 900 && p.Health == 0; k++) host.Step(p == me ? Press(MphRecomp.Campaign.CampaignButtons.Shoot) : idle);
                    p.IsBot = bot;
                    Step(10);
                }
                void Put(PlayerEntity p, Vector3 at, Vector3 facing) => p.Teleport(at, facing, host.Scene.GetNodeRefContaining(at));
                for (int k = 0; k < 600 && !me.LoadFlags.TestFlag(LoadFlags.Spawned); k++) host.Step(idle);
                Step(30);
                TurretSpot spot = EchoesTurret.For(host.RoomId)!;
                Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
                Vector3 bas = V(spot.Base), enter = (V(spot.EnterMin) + V(spot.EnterMax)) / 2;
                Check(turret.State == EchoesTurretEntity.TurretState.Free, $"free at the start (pivot y {turret.PivotNow.Y:0.00})");
                // the entry kill: the dummy 2 units off (inside 5 m), then the player walks in
                Put(dummy, bas + new Vector3(2, 0.5f, 0), Vector3.UnitZ);
                Put(me, enter.WithY(spot.EnterMax[1]), -Vector3.UnitZ);
                int kills0 = GameState.Kills[me.SlotIndex];
                for (int k = 0; k < 60 && turret.State == EchoesTurretEntity.TurretState.Free; k++) host.Step(idle);
                Check(turret.State == EchoesTurretEntity.TurretState.Entering && turret.Occupant == me && me.HostSeated,
                    $"walking into the box gets in (state {turret.State}, feet y {me.Position.Y:0.00})");
                Check(dummy.Health == 0 && GameState.Kills[me.SlotIndex] == kills0 + 1, $"the dummy 2 units off dies, the occupant's kill (health {dummy.Health}, kills {GameState.Kills[me.SlotIndex] - kills0})");
                long t0 = host.Frame;
                for (int k = 0; k < 180 && turret.State == EchoesTurretEntity.TurretState.Entering; k++) host.Step(idle);
                Check(turret.State == EchoesTurretEntity.TurretState.Active && Math.Abs(host.Frame - t0 - 120) <= 2, $"control after {host.Frame - t0} steps (2 s)");
                Check(MathF.Abs(turret.PivotNow.Y - spot.Pivot[1]) < 0.01f && MathF.Abs(me.Position.Y - (spot.Pivot[1] + spot.SeatOffset[1])) < 0.01f,
                    $"raised: pivot y {turret.PivotNow.Y:0.00}, feet y {me.Position.Y:0.00}");
                // the lift's solid base (Echoes' 'Dummy Base') rose with it: a drop beside the seat's column lands on its top
                EchoesTurretBaseEntity? lift = null;
                foreach (EntityBase e in host.Scene.Entities) lift ??= e as EchoesTurretBaseEntity;
                float liftWant = spot.BaseMax![1] + spot.BaseRise;
                // (a drop from just above the raised top, under the turret's head, beside the column)
                float BaseTop()
                {
                    CollisionResult hit = default;
                    float x = spot.BaseMax![0] - 0.2f, z = (spot.BaseMin![2] + spot.BaseMax[2]) / 2;
                    return CollisionDetection.CheckBetweenPoints(new Vector3(x, liftWant + 0.8f, z), new Vector3(x, liftWant - 3, z), TestFlags.None, host.Scene, ref hit)
                        ? hit.Position.Y : float.NaN;
                }
                float liftTop = BaseTop();
                Check(lift != null && MathF.Abs(lift.Height - spot.BaseRise) < 0.01f && MathF.Abs(liftTop - liftWant) < 0.05f,
                    $"the lift's base rose {lift?.Height:0.00} in 2 s ({spot.BaseRise:0.00}): solid, its top at y {liftTop:0.00} ({liftWant:0.00})");
                // seated inside the seat's column (Echoes' 'Dumb Collision Box', solid while manned): the feet and view hold still
                {
                    Vector3 feet0 = me.Position, seatCam0 = me.CameraInfo.Position;
                    float feetMove = 0, seatCamMove = 0;
                    for (int k = 0; k < 60; k++)
                    {
                        host.Step(idle);
                        feetMove = Math.Max(feetMove, (me.Position - feet0).Length);
                        seatCamMove = Math.Max(seatCamMove, (me.CameraInfo.Position - seatCam0).Length);
                    }
                    bool column = (lift ?? (EntityBase)turret).EntityCollision[1]?.Collision.Active == true || spot.ColumnMin == null;
                    Check(column && feetMove < 0.01f && seatCamMove < 0.01f, $"seated in the solid column: still (feet {feetMove:0.000}, view {seatCamMove:0.000}, column on {column})");
                }
                Revive(dummy);
                // turning: the move stick at 120/s, the aim inputs directly, inside +-90 / +-45
                var left = idle; left.Move = new System.Numerics.Vector2(-1, 0);
                Step(30, left);
                Check(MathF.Abs(turret.Yaw - 60) < 3, $"stick left 0.5 s: yaw {turret.Yaw:0.0} (60)");
                Step(60, left);
                Check(MathF.Abs(turret.Yaw - 90) < 0.01f, $"stick left 1.5 s: yaw {turret.Yaw:0.0} (stops at 90)");
                var aim = idle; aim.AimDelta = new System.Numerics.Vector2(4 * 30, 0);
                Step(1, aim);
                {
                    // drawn round the occupant's camera: the barrel turned as the camera looks this step, not a step behind
                    Vector3 centreWay = new Vector3(MathF.Sin(MathHelper.DegreesToRadians(spot.CentreHeading)), 0, MathF.Cos(MathHelper.DegreesToRadians(spot.CentreHeading)));
                    Vector3 drawn = Vector3.TransformVector(centreWay, turret.DrawTurn), lagged = Vector3.TransformVector(centreWay, turret.AimTurn);
                    Check(Vector3.Dot(drawn, me.SeatAimVector) > 0.99999f && Vector3.Dot(lagged, me.SeatAimVector) < 0.99f,
                        $"the shell drawn with the camera's aim the same step (off {MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(drawn, me.SeatAimVector), -1, 1))):0.00} deg; the turret's own a step behind: {MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(lagged, me.SeatAimVector), -1, 1))):0.0} deg)");
                }
                Step(1);   // (the turret follows its occupant's aim a step later)
                Check(MathF.Abs(turret.Yaw - 60) < 1, $"aim 30 deg right: yaw {turret.Yaw:0.0} (60)");
                aim.AimDelta = new System.Numerics.Vector2(4 * 60, 0);
                Step(1, aim);
                Step(1);
                Check(MathF.Abs(turret.Yaw) < 1, $"aim 60 more: yaw {turret.Yaw:0.0} (0)");
                var down = idle; down.Move = new System.Numerics.Vector2(0, -1);
                // the stick's pitch eases in (Echoes' 1 - PhongBlob(gap / 90, 8)): the reference walk of the same steps
                float Eased(float from, float to, int frames)
                {
                    for (int k = 0; k < frames; k++)
                    {
                        float amount = spot.PitchRate / 60 * PlayerEntity.PitchEase(to - from);
                        from = MathF.Abs(to - from) <= MathF.Max(amount, 0.1f) ? to : from + MathF.Sign(to - from) * amount;
                    }
                    return from;
                }
                float want = Eased(0, -spot.PitchDown, 30);
                Step(30, down);
                Check(MathF.Abs(turret.Pitch - want) < spot.PitchRate / 60 + 0.2f && want < -20, $"stick down 0.5 s: pitch {turret.Pitch:0.0} ({want:0.0}, eased; the stick lands a frame later)");
                want = Eased(want, -spot.PitchDown, 60);
                Step(60, down);
                Check(MathF.Abs(turret.Pitch - want) < 0.5f && want < -38, $"stick down 1.5 s: pitch {turret.Pitch:0.0} ({want:0.0}, creeping to -45)");
                want = Eased(want, 0, 60);
                Step(60);
                Check(MathF.Abs(turret.Pitch - want) < 0.5f && MathF.Abs(want) < 10, $"released: pitch {turret.Pitch:0.0} ({want:0.0}, easing back to 0)");
                float pr = MathHelper.DegreesToRadians(turret.Pitch);
                Check(Vector3.Dot(me.SeatAimVector, new Vector3(0, MathF.Sin(pr), -MathF.Cos(pr))) > 0.999f, "the view is the barrel's (centre: -Z, at its pitch)");
                // firing: fresh presses, 1 s apart
                int shots0 = turret.Shots;
                Step(1, Press(MphRecomp.Campaign.CampaignButtons.Shoot));
                Step(1);
                Check(turret.Shots == shots0 + 1, $"Shoot fires ({turret.Shots - shots0})");
                Step(1, Press(MphRecomp.Campaign.CampaignButtons.Shoot));
                Step(1);
                Check(turret.Shots == shots0 + 1, "a press 2 steps later doesn't (1 s rate)");
                Step(60);
                Step(1, Press(MphRecomp.Campaign.CampaignButtons.Shoot));
                Step(1);
                Check(turret.Shots == shots0 + 2, "one a second later does");
                // a shot at the dummy straight ahead, at the pivot's height (held there)
                Step(60);
                Vector3 ahead = turret.PivotNow + new Vector3(0, -Fixed.ToFloat(dummy.Values.AimYOffset) * 0.6f, -10);
                Put(dummy, ahead, Vector3.UnitZ);
                int h0 = dummy.Health;
                Step(1, Press(MphRecomp.Campaign.CampaignButtons.Shoot));
                for (int k = 0; k < 40 && dummy.Health == h0; k++) { dummy.Speed = Vector3.Zero; Put(dummy, ahead, Vector3.UnitZ); host.Step(idle); }
                Check(h0 - dummy.Health >= 70, $"a shot at a player 10 units ahead: {h0 - dummy.Health} damage of {h0} (75)");
                // its knockback (Echoes: power 20 = 50 m/s, a third and a half of it in the air, along the shot, flat; in
                // the air scaled by 0.65 + 0.35 |n . right|, here square on so 0.65; steering cut 0.4 s)
                {
                    float unit = spot.ShotRange / 51 / 30, knock = 20 * 2.5f / 3.5f * unit * 0.65f;
                    var push = new Vector3(dummy.Speed.X, 0, dummy.Speed.Z);
                    Check(MathF.Abs(push.Length - knock) < knock * 0.1f && push.Z < 0 && MathF.Abs(dummy.HostKnockLockout - 0.4f) < 0.02f,
                        $"knocked back along the shot: {push.Length:0.000} a tick ({knock:0.000}), steering cut {dummy.HostKnockLockout:0.00} s (0.4)");
                }
                Revive(dummy);
                // the splash, as Echoes' ApplyRadiusDamage: in when the target's bounds reach into the radius (its centre
                // may be outside), flat; a shot straight ahead into the far wall shows where it lands, then the dummy by
                // that spot: its centre past the radius, its body inside it (hurt) or just outside it too (not)
                {
                    float R = spot.SplashRadius, r = dummy.Volume.SphereRadius;
                    Put(dummy, bas + new Vector3(4, 0.5f, 0), Vector3.UnitZ);
                    Step(60);
                    BeamProjectileEntity? Fire()
                    {
                        var before = new HashSet<BeamProjectileEntity>();
                        foreach (BeamProjectileEntity b in host.Scene.GetBeamProjectileEntities()) before.Add(b);
                        Step(1, Press(MphRecomp.Campaign.CampaignButtons.Shoot));
                        BeamProjectileEntity? shot = null;
                        for (int k = 0; k < 3 && shot == null; k++)
                        {
                            host.Step(idle);
                            foreach (BeamProjectileEntity b in host.Scene.GetBeamProjectileEntities())
                            {
                                if (!before.Contains(b) && b.Equip?.Weapon is { } w && EchoesTurret.IsShotWeapon(w)) shot = b;
                            }
                        }
                        if (shot == null) Console.WriteLine($"      (no shot: {turret.Shots} shots)");
                        return shot;
                    }
                    Vector3? Land(BeamProjectileEntity? shot, Action? hold = null)
                    {
                        for (int k = 0; k < 150 && shot != null; k++)
                        {
                            if (shot.Flags.TestFlag(BeamFlags.Collided)) return shot.Position;
                            hold?.Invoke();
                            host.Step(idle);
                        }
                        return null;
                    }
                    BeamProjectileEntity? first = Fire();
                    Vector3? hit = Land(first);
                    Check(hit != null, $"a shot ahead lands at {hit}");
                    if (hit is Vector3 f && first != null)
                    {
                        // the rule on a grate: a blast above its box by a little under the radius reaches it, its centre doesn't
                        foreach (EnemyInstanceEntity e in host.Scene.GetEnemyInstanceEntities())
                        {
                            if (e is not EchoesBreakableEntity { Up: true } br) continue;
                            Vector3 save = first.Position;
                            first.Position = br.Position + new Vector3(0, (br.Max.Y - br.Min.Y) / 2 + R - 0.2f, 0);
                            float? d = EchoesTurret.SplashDistance(first, br, br.Position);
                            float centre = Vector3.Distance(first.Position, br.Position);
                            bool took = EchoesTurret.SplashHit(first, br, spot.Damage);
                            first.Position = save;
                            Check(d < R && centre > R && took && !br.Up, $"a blast {centre:0.00} from a grate's centre, {d:0.00} from its box (radius {R:0.00}): it breaks ({!br.Up})");
                            break;
                        }
                        void Stand(float off)
                        {
                            // in front of the wall by 1, sideways by off, feet half a unit under the blast's height
                            // (the bounds' column starts at its sphere's bottom: put that half a unit under the blast)
                            float side = MathF.Sqrt(MathF.Max(0, off * off - 1));
                            Put(dummy, new Vector3(f.X + side, f.Y, f.Z + 1), Vector3.UnitZ);
                            host.Step(idle);
                            float up = dummy.Volume.SpherePosition.Y - dummy.Position.Y;
                            Put(dummy, new Vector3(f.X + side, f.Y - 0.5f + r - up, f.Z + 1), Vector3.UnitZ);
                            dummy.Speed = Vector3.Zero;
                        }
                        foreach ((float off, bool hurt) in new[] { (R + r + 0.4f, false), (R + r / 2, true) })
                        {
                            Step(60);
                            Revive(dummy);
                            Stand(off);
                            int hp = dummy.Health;
                            float centre = Vector3.Distance(dummy.Position, f);
                            Vector3? at = Land(Fire(), () => Stand(off));
                            bool took = dummy.Health < hp;
                            if (at is Vector3 l) centre = Vector3.Distance(dummy.Position, l);
                            Check(at is Vector3 a && (a - f).Length < 1 && took == hurt && centre > R,
                                $"landed {at}, the dummy's centre {centre:0.00} from the blast, its body {off - r:0.00} (radius {R:0.00}): {(took ? $"hurt {hp - dummy.Health}" : "not hurt")} ({(hurt ? "hurt" : "not")})");
                        }
                    }
                    Revive(dummy);
                    // the splash through MphRead's own check (CheckSplashDamage) on every grate: a blast on the nearest
                    // surface beside it (a ray out from its centre that leaves its box and lands within the radius), as a
                    // shot landing by it; Echoes' sight line (TestRayDamage) ignores the target itself, so each breaks
                    {
                        var splash = typeof(BeamProjectileEntity).GetMethod("CheckSplashDamage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                        BeamProjectileEntity? probe = Fire();
                        int tried = 0, broke = 0;
                        var missed = new List<string>();
                        var grates = new List<EchoesBreakableEntity>();
                        foreach (EnemyInstanceEntity e in host.Scene.GetEnemyInstanceEntities())
                        {
                            if (e is EchoesBreakableEntity { Up: true, Grate: true } g) grates.Add(g);
                        }
                        foreach (EchoesBreakableEntity br in grates)
                        {
                            if (probe == null) continue;
                            Vector3? at = null;
                            foreach (Vector3 dir in new[] { Vector3.UnitY, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitY })
                            {
                                // from just outside the box along dir, out to the radius: the first world surface
                                Vector3 half = (br.Max - br.Min) / 2;
                                Vector3 from = br.Position + dir * (MathF.Abs(Vector3.Dot(half, dir)) + 0.05f);
                                CollisionResult res = default;
                                if (CollisionDetection.CheckBetweenPoints(from, from + dir * (R * 0.7f), TestFlags.Beams, host.Scene, ref res))
                                {
                                    Vector3 hitAt = res.Position - dir * 0.05f;
                                    if (EchoesTurret.SplashDistance(probe, br, br.Position) is float && Vector3.Distance(hitAt, br.Position) > 0.1f) { at = hitAt; break; }
                                }
                            }
                            if (at is not Vector3 blast) continue;
                            tried++;
                            Vector3 save = probe.Position;
                            probe.Position = blast;
                            splash.Invoke(probe, new object?[] { null });
                            probe.Position = save;
                            if (!br.Up) broke++; else missed.Add($"{br.Position} from {blast} ({EchoesTurret.SplashDistance(probe, br, br.Position):0.00})");
                        }
                        Check(tried > 0 && broke == tried, $"a blast on a surface beside each grate breaks it: {broke} of {tried}" + string.Concat(missed.Take(4).Select(m => "\n        missed " + m)));
                    }
                }
                // getting out
                Step(60);
                Step(1, Press(MphRecomp.Campaign.CampaignButtons.Jump));
                Step(1);
                Check(turret.State == EchoesTurretEntity.TurretState.Leaving && !me.HostSeated, $"Jump gets out (state {turret.State})");
                Check(me.Speed.Y > 0.3f && me.Speed.Z > 0.3f, $"pushed back and up ({me.Speed.X:0.00}, {me.Speed.Y:0.00}, {me.Speed.Z:0.00} a tick)");
                t0 = host.Frame;
                for (int k = 0; k < 300 && turret.State != EchoesTurretEntity.TurretState.Free; k++) host.Step(idle);
                Check(turret.State == EchoesTurretEntity.TurretState.Free && Math.Abs(host.Frame - t0 - 240) <= 2, $"free again after {host.Frame - t0} steps (4 s)");
                Check(MathF.Abs(turret.PivotNow.Y - spot.RiseCurve[0]) < 0.05f, $"lowered (pivot y {turret.PivotNow.Y:0.00})");
                // the turret's health: the dummy gets in, the player's hits destroy it, credited to the player
                Put(me, bas + new Vector3(0, 1, -12), Vector3.UnitZ);
                Step(10);
                Put(dummy, enter.WithY(spot.EnterMax[1]), -Vector3.UnitZ);
                for (int k = 0; k < 60 && turret.State == EchoesTurretEntity.TurretState.Free; k++) host.Step(idle);
                Check(turret.Occupant == dummy, $"the dummy gets in ({turret.State})");
                Step(130);
                // the player's shots: one at the seat's column under the ball stops on its side and does nothing (owner 10-16,
                // as Echoes: 'Dumb Collision Box' solid to shots, no damage); one straight at the ball hurts it
                if (spot.ColumnMin != null)
                {
                    (Vector3 Stop, int Lost) ShootAt(Vector3 aim)
                    {
                        Vector3 from = turret.PivotNow + new Vector3(0, -2, -12);
                        int hp = turret.Hp;
                        var before = new HashSet<BeamProjectileEntity>();
                        foreach (BeamProjectileEntity x in host.Scene.GetBeamProjectileEntities()) before.Add(x);
                        BeamProjectileEntity.Spawn(me, me.EquipInfo, from, (aim - from).Normalized(), BeamSpawnFlags.None, me.NodeRef, host.Scene);
                        BeamProjectileEntity? shot = null;
                        foreach (BeamProjectileEntity x in host.Scene.GetBeamProjectileEntities()) if (!before.Contains(x)) shot = x;
                        Vector3 last = from;
                        for (int k = 0; k < 120 && shot != null && shot.Lifespan > 0 && !shot.Flags.TestFlag(BeamFlags.Collided); k++) { last = shot.Position; host.Step(idle); }
                        host.Step(idle);
                        return (last, hp - turret.Hp);
                    }
                    Vector3 pv = turret.PivotNow;
                    // (the column below the ball's underside; owner 10-17: it stops shots only there, the ball takes the rest)
                    float under = EchoesTurretEntity.RestBottom(spot) - pv.Y;
                    (Vector3 colStop, int colLost) = ShootAt(pv + new Vector3(0, under - 1f, 0));
                    bool onSide = colStop.Z >= spot.ColumnMin[2] - 1f && colStop.Z <= spot.ColumnMin[2] + 0.2f;
                    Check(colLost == 0 && onSide, $"a shot at the column under the ball stops on its side, no damage (at z {colStop.Z - pv.Z:0.00} from the pivot, lost {colLost})");
                    (_, int ballLost) = ShootAt(pv);
                    (_, int lowLost) = ShootAt(pv + new Vector3(0, under * 0.75f, 0));
                    Check(lowLost > 0, $"a shot at the ball's lower half ({under * 0.75f:0.00} under the pivot) hurts it, not the column (lost {lowLost})");
                    Check(ballLost > 0, $"a shot straight at the ball hurts it (lost {ballLost})");
                }
                kills0 = GameState.Kills[me.SlotIndex];
                int hp0 = turret.Hp;
                int charged = EchoesBreakables.Damage(BeamType.PowerBeam, 36, true);
                for (int i = 0; i < 6; i++) turret.Hit(charged, me);
                Check(turret.State == EchoesTurretEntity.TurretState.Active && turret.Hp == hp0 - 6 * charged, $"6 full-charge Power Beam shots ({charged} each): up ({turret.Hp})");
                turret.Hit(EchoesBreakables.Damage(BeamType.PowerBeam, 36, true), me);
                Step(1);
                Check(turret.State == EchoesTurretEntity.TurretState.Destroyed && dummy.Health == 0 && GameState.Kills[me.SlotIndex] == kills0 + 1,
                    $"the 7th destroys it: occupant dead, the player's kill ({turret.State}, health {dummy.Health}, kills {GameState.Kills[me.SlotIndex] - kills0})");
                Check(lift != null && lift.Height == 0 && !(BaseTop() > liftWant - 0.08f),
                    $"the base drops to the bottom at once (Echoes' RSET: height {lift?.Height:0.00}, top y {BaseTop():0.00})");
                t0 = host.Frame;
                for (int k = 0; k < 480 && turret.State != EchoesTurretEntity.TurretState.Free; k++) host.Step(idle);
                Check(Math.Abs(host.Frame - t0 - 420) <= 2, $"back after {host.Frame - t0} steps (7 s)");
                Revive(dummy);
                // the bomb slot: the dummy in the turret, the player's ball in the slot
                Put(dummy, enter.WithY(spot.EnterMax[1]), -Vector3.UnitZ);
                for (int k = 0; k < 60 && turret.State == EchoesTurretEntity.TurretState.Free; k++) host.Step(idle);
                Step(130);
                Check(turret.Occupant == dummy && turret.Armed, $"the dummy in the turret, the slot armed ({turret.State})");
                Vector3 slot = (V(spot.SlotMin) + V(spot.SlotMax)) / 2;
                Put(me, slot + new Vector3(0, 0, -2), Vector3.UnitZ);
                for (int k = 0; k < 120 && !me.IsAltForm; k++) host.Step(k < 4 ? Press(MphRecomp.Campaign.CampaignButtons.Morph) : idle);
                Step(20);
                Check(me.IsAltForm && me.Health > 0, $"the player morphs by the slot (health {me.Health}, at {me.Position})");
                Put(me, slot, -Vector3.UnitZ);
                Step(5);
                Check(turret.Held == me && (me.Volume.SpherePosition - slot).Length < 0.05f, $"the slot holds the ball (at {(me.Volume.SpherePosition - slot).Length:0.00} from its centre)");
                for (int k = 0; k < 30; k++) host.Step(k < 4 ? Press(MphRecomp.Campaign.CampaignButtons.Morph) : idle);
                Check(me.IsAltForm && turret.Held == me, "no unmorphing while held");
                var roll = idle; roll.Move = new System.Numerics.Vector2(0, 1);
                Step(30, roll);
                Check((me.Volume.SpherePosition - slot).Length < 0.05f, "rolling goes nowhere");
                // held still: the camera and the ball don't move frame to frame (owner 2026-10-16: Sylux's camera shook there)
                float camMove = 0, ballMove = 0;
                Vector3 cam0 = me.CameraInfo.Position, ball0 = me.Volume.SpherePosition;
                for (int k = 0; k < 120; k++)
                {
                    var hold = idle;
                    if (k >= 60) hold.Move = new System.Numerics.Vector2(MathF.Sin(k * 0.3f), 1); // rolling against the hold
                    host.Step(hold);
                    camMove = Math.Max(camMove, (me.CameraInfo.Position - cam0).Length);
                    ballMove = Math.Max(ballMove, (me.Volume.SpherePosition - ball0).Length);
                    if (Environment.GetEnvironmentVariable("HOLDTRACE") == "1")
                        Console.WriteLine($"      held {k}: pos {me.Position} ball {me.Volume.SpherePosition} cam {me.CameraInfo.Position} speed {me.Speed} tgt {me.CameraInfo.Target} type {me.CameraType} face {me.FacingVector} morphcam {me.MorphCamera != null}");
                    cam0 = me.CameraInfo.Position;
                    ball0 = me.Volume.SpherePosition;
                }
                Check(camMove < 0.01f && ballMove < 0.01f, $"held, still and rolling: the camera and the ball stay put (largest step: camera {camMove:0.000}, ball {ballMove:0.000})");
                // the view is one of the slot's hint cameras (CAMH 0x31 / 0x2ce, BALT ENTR), looking its way
                if (spot.SlotCams is { Length: >= 6 } cams)
                {
                    float best = Single.MaxValue, look = 0;
                    for (int c = 0; c + 5 < cams.Length; c += 6)
                    {
                        float d = (me.CameraInfo.Position - new Vector3(cams[c], cams[c + 1], cams[c + 2])).Length;
                        if (d >= best) continue;
                        best = d;
                        look = Vector3.Dot((me.CameraInfo.Target - me.CameraInfo.Position).Normalized(), new Vector3(cams[c + 3], cams[c + 4], cams[c + 5]).Normalized());
                    }
                    Check(best < 0.05f && look > 0.999f, $"held: the view is the slot's hint camera ({best:0.000} from it, facing {look:0.0000})");
                }
                kills0 = GameState.Kills[me.SlotIndex];
                Step(4, Press(MphRecomp.Campaign.CampaignButtons.AltAttack));
                if (hunter != Hunter.Samus)
                {
                    int laid = 0, own = 0;
                    foreach (BombEntity b in host.Scene.GetBombEntities())
                    {
                        if (b.Owner == me && b.BombType == BombType.MorphBall) laid++;
                        else if (b.Owner == me) own++;
                    }
                    Check(laid == 1 && own == 0 && !me.Flags2.TestFlag(PlayerFlags2.AltAttack),
                        $"held, the alt-form attack lays Samus's bomb instead of its own ({laid} Samus bombs, {own} of its own)");
                }
                for (int k = 0; k < 150 && turret.State != EchoesTurretEntity.TurretState.Destroyed; k++) host.Step(idle);
                Check(turret.State == EchoesTurretEntity.TurretState.Destroyed && dummy.Health == 0 && GameState.Kills[me.SlotIndex] == kills0 + 1,
                    $"{(hunter == Hunter.Samus ? "a bomb in the slot" : "that bomb")}: the turret destroyed, occupant dead, the player's kill ({turret.State}, health {dummy.Health}, kills {GameState.Kills[me.SlotIndex] - kills0})");
                Step(5);
                Check(turret.Held == null && me.HostSlotHold == null, "the slot lets go once it is disarmed");
                // the kick-out while disarmed
                Vector3 kick = (V(spot.KickMin) + V(spot.KickMax)) / 2;
                if (me.IsAltForm)
                {
                    Put(me, kick, -Vector3.UnitZ);
                    me.Speed = Vector3.Zero;
                    Step(10);
                    Vector3 moved = me.Volume.SpherePosition - kick;
                    Check(moved.Z < -0.05f, $"the kick-out pushes the ball out along Echoes +Y (moved {moved.X:0.00} {moved.Y:0.00} {moved.Z:0.00})");
                }
            }
            // a seated bot (the turret's bot brain): it turns onto a player in front of the barrel and fires, and gets out once
            // nobody is in sight for EchoesTurretEntity.BotIdleOut
            {
                Console.WriteLine("  a bot in the turret:");
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, Hunter.Samus, 1, 2);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                settings.Crates = false;
                using var host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                EchoesTurretEntity turret = TurretIn(host.Scene)!;
                TurretSpot spot = EchoesTurret.For(host.RoomId)!;
                PlayerEntity me = PlayerEntity.Main, bot = PlayerEntity.Players[1];
                for (int k = 0; k < 600 && !(me.LoadFlags.TestFlag(LoadFlags.Spawned) && bot.LoadFlags.TestFlag(LoadFlags.Spawned)); k++) host.Step(idle);
                for (int k = 0; k < 30; k++) host.Step(idle);
                Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
                Vector3 enter = (V(spot.EnterMin) + V(spot.EnterMax)) / 2;
                me.Teleport(new Vector3(-6, 2.4f, 4), Vector3.UnitZ, host.Scene.GetNodeRefContaining(new Vector3(-6, 2.4f, 4)));
                bot.Teleport(enter.WithY(spot.EnterMax[1]), -Vector3.UnitZ, host.Scene.GetNodeRefContaining(enter));
                int shots0 = turret.Shots, seen = -1, shot = -1;
                for (int f = 0; f < 10 * 60 && shot < 0; f++)
                {
                    host.Step(idle);
                    if (seen < 0 && turret.BotTarget == me) seen = f;
                    if (turret.Shots > shots0) shot = f;
                }
                Check(turret.Occupant == bot && seen >= 0 && shot >= 0,
                    $"the bot got in, picked the player at {seen / 60f:0.00} s, fired at {shot / 60f:0.00} s (yaw {turret.Yaw:0}, pitch {turret.Pitch:0})");
                Vector3 hide = new Vector3(0, 2.4f, 40);
                int left = -1;
                for (int f = 0; f < (EchoesTurretEntity.BotIdleOut + 180) && left < 0; f++)
                {
                    if (me.Health > 0 && f % 30 == 0) me.Teleport(hide, Vector3.UnitZ, host.Scene.GetNodeRefContaining(hide));
                    host.Step(idle);
                    if (turret.State == EchoesTurretEntity.TurretState.Leaving) left = f;
                }
                Check(left >= 0, $"with nobody in sight the bot got out after {left / 60f:0.0} s");
            }
            // the effects' model particles (PMDL): each placed model drawn by some effect, near where the effect runs
            {
                Console.WriteLine("  the turret effects' model particles:");
                EchoesArena.Def gallery = EchoesArena.Find("M06_ShootingGallery")!;
                string dir = EchoesArena.HdDir(gallery.Room) ?? Path.Combine(Paths.FileSystem, "_archives", gallery.Archive, ArenaHdWriter.Dir);
                TurretLook? look = EchoesTurret.LoadLook(dir);
                if (look?.Fx == null || look.ModelPasses.Count == 0)
                {
                    Check(false, "no model passes in the turret look (re-import the arena)");
                }
                else
                {
                    var lib = MphRecomp.Campaign.RetroParticles.Load(Path.Combine(dir, EchoesTurret.FxDir), echoes: true);
                    var seen = new HashSet<uint>();
                    int squares = 0;
                    foreach (TurretFx f in look.Fx.Where(f => f.Kind == "part" && lib.Get(f.Id) != null))
                    {
                        var sys = new MphRecomp.Campaign.RetroParticleSystem(lib, f.Id, Matrix4.CreateScale(EchoesArena.DefaultScale), follow: true, seed: 1);
                        var models = new List<MphRecomp.Campaign.RetroModelParticle>();
                        int most = 0;
                        float far = 0;
                        for (int k = 0; k < 40; k++)
                        {
                            sys.Step();
                            models.Clear();
                            sys.CollectModels(models);
                            most = Math.Max(most, models.Count);
                            foreach (var m in models)
                            {
                                if (m.Model == 0) { squares++; continue; } // a PMUS square (textured, no model)
                                seen.Add(m.Model);
                                far = MathF.Max(far, m.Transform.Row3.Xyz.Length);
                            }
                        }
                        if (most > 0) Console.WriteLine($"    {f.Role}: up to {most} model particles ({string.Join(", ", models.Select(m => m.Model.ToString("x8")).Distinct())}), farthest {far:0.00} u");
                    }
                    Check(squares > 0, $"the impact's PMUS flash squares are collected ({squares} square-steps)");
                    // the lift's rings (6f3a4416's PMSC z: a frame-keyed KEYE, read at the emitter's time as Retro does)
                    // stretch once as the lift starts; rings made later (every 128 frames) stay low, under the seat's
                    // camera (owner 10-16: a green flash about every second while idle in the seat)
                    if (look.Fx.FirstOrDefault(f => f.Role == "lift" && lib.Get(f.Id) != null) is { At: { Length: 12 } la } lift)
                    {
                        var at = new Matrix4(la[0], la[1], la[2], 0, la[3], la[4], la[5], 0, la[6], la[7], la[8], 0, la[9], la[10], la[11], 1);
                        var sys = new MphRecomp.Campaign.RetroParticleSystem(lib, lift.Id, at, follow: true, seed: 1);
                        var models = new List<MphRecomp.Campaign.RetroModelParticle>();
                        float early = 0, late = 0;
                        for (int k = 0; k < 400; k++)
                        {
                            sys.Step();
                            models.Clear();
                            sys.CollectModels(models);
                            foreach (var m in models.Where(m => m.Model != 0))
                            {
                                float z = m.Transform.Row2.Length;
                                if (k < 64) early = MathF.Max(early, z);
                                else late = MathF.Max(late, z);
                            }
                        }
                        Check(early > 2 * late && late > 0, $"the lift's rings stretch once as it starts ({early:0.00}), then stay low ({late:0.00} after 64 frames)");
                    }
                    // (plus the grates' break pieces: debris models, drawn by the app's GrateBit, not a particle)
                    var debris = look.Fx!.Where(f => f.Kind == "debris").Select(f => f.Id).ToHashSet();
                    Check(look.ModelPasses.Keys.All(m => seen.Contains(m) || debris.Contains(m)) && seen.All(look.ModelPasses.ContainsKey),
                        $"every placed model drawn by an effect ({string.Join(", ", seen.Select(m => m.ToString("x8")))}; debris {string.Join(", ", debris.Select(m => m.ToString("x8")))}; placed {look.ModelPasses.Count})");
                    Check(debris.Count == 2 && look.Fx!.Count(f => f.Kind == "debris") == 8 && look.Fx!.Count(f => f.Death != 0) == 1,
                        $"the grates' break: 8 debris pieces on 2 models, one with a death particle");
                    Check(look.Sounds?.Any(x => x.Role == "grate") == true, "the grates' break sound (MetalDebrisMed)");
                }
            }
            Console.WriteLine(fails == 0 ? "turret test: all ok" : $"turret test: {fails} FAILED");
            return fails == 0;
        }

        // forward kinematics: each bone's world rotation (x y z w) and position from the animation's local keys at t (a
        // bone without keys keeps its bind offset from its parent)
        static Dictionary<uint, (double[] Q, double[] P)> Pose(Cinf sk, Anim a, double t)
        {
            a.Sample(t, out Dictionary<uint, double[]> rot, out Dictionary<uint, double[]> trans);
            var world = new Dictionary<uint, (double[], double[])>();
            (double[], double[]) Of(uint id)
            {
                if (world.TryGetValue(id, out var w)) return w;
                Cinf.Bone b = sk.Bones[id];
                double[] q = rot.TryGetValue(id, out double[]? r) ? r : new double[] { 0, 0, 0, 1 };
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

        static double[] Mul(double[] a, double[] b) => new[]
        {
            a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
            a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
            a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
            a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
        };

        static double[] Rotate(double[] q, double[] v)
        {
            double[] r = Mul(Mul(q, new[] { v[0], v[1], v[2], 0 }), new[] { -q[0], -q[1], -q[2], q[3] });
            return new[] { r[0], r[1], r[2] };
        }

        static string Show(Part.Elem? e)
        {
            if (e == null) return "null";
            string s = e.Kind switch { "CNST" => e.Kids.Length > 0 ? "(" + string.Join(",", e.Kids.Select(Show)) + ")" : $"{e.F:0.####}/{e.I}", "BOOL" => e.B.ToString(), _ => e.Id != 0 && e.Id != uint.MaxValue ? $"{e.Kind}:{e.Id:x8}" : e.Kind };
            if (e.Kind != "CNST" && e.Kids.Length > 0) s += "(" + string.Join(",", e.Kids.Select(Show)) + ")";
            return s;
        }

        static void Dump(Mrea.Property p, int depth)
        {
            if (p.Children == null) return;
            foreach (Mrea.Property c in p.Children)
            {
                if (c.Id == Mrea.ScriptObject.EditorPropertiesId) continue;
                string pad = new string(' ', 2 * depth);
                if (c.Children != null && c.Children.Count > 0 && c.Data.Length > 8)
                {
                    Console.WriteLine($"{pad}{c.Id:x8} {{");
                    Dump(c, depth + 1);
                    Console.WriteLine($"{pad}}}");
                }
                else if (c.Data.Length == 4)
                {
                    Console.WriteLine($"{pad}{c.Id:x8} = {c.F32:0.####} / {c.U32:x8}");
                }
                else
                {
                    Console.WriteLine($"{pad}{c.Id:x8} = [{c.Data.Length}] {Convert.ToHexString(c.Data.AsSpan(0, Math.Min(c.Data.Length, 48)))}");
                }
            }
        }
    }
}
