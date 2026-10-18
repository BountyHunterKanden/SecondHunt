using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Editor;
using MphRead.Entities;
using MphRecomp.Arenas;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Retro;

namespace MphRead
{
    // Echoes multiplayer arenas on the PC (docs/MP2_MULTIPLAYER_IMPORT.md, phase 1):
    //   -echoesarena import [disc=<echoes .iso>] [arena=all|<name>] [scale=0.593]
    //       writes MphRead room files for the arena(s) into the desktop extraction (src/MphRead/bin/Debug/net9.0/files/AMHE0)
    //   -echoesarena sim <arena> [matchsim options: mode= bots= level= time= hunter= move]
    //       registers the imported arenas and runs a -matchsim bot match in one
    //   -echoesarena music [disc=<echoes .iso>] [wav=<dir>] [match=<arena>] [time=<s>]
    //       copies the arenas' music streams from the disc (as import does), prints each track's loop and level; wav=
    //       writes the first play + one loop of each; match= runs a headless Battle there with a logging stream output
    //       (time= its time limit, default 70 s) to show when ArenaMusic starts, switches at one minute and stops
    //   -echoesarena check [arena]
    //       the device's match start per arena x mode (Battle, Survival, Prime Hunter; Combat Hall as the control):
    //       CampaignHost.StartMatch with draw items, 300 frames, and every frame the room lookups the Android status
    //       line and map make from the scene's room id (Metadata.GetRoomById(host.RoomId) threw on the Odin 15:45)
    internal static class EchoesArenaTool
    {
        static readonly string DefaultDisc = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "brawl_extract", "prime", "echoes_widescreen.iso"));

        public static void Run(string[] args)
        {
            string verb = args.Length > 1 ? args[1] : "import";
            if (Environment.GetEnvironmentVariable("PAD_MPHSTEER") == "1") JumpPadEntity.HostSameArcSteer = false;   // MPH's own steering on Echoes pads too (default: the owner's 10-08 ~01:10 left/right rule)
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            if (verb == "sim")
            {
                int n = EchoesArena.Register(Paths.FileSystem);
                string name = args.Length > 2 ? args[2] : "Crossfire Chaos";
                EchoesArena.Def def = EchoesArena.Find(name) ?? throw new ArgumentException($"no Echoes arena {name}");
                Console.WriteLine($"{n} Echoes arenas registered");
                MatchSim.Run(new[] { "-matchsim", $"arena={def.Room}" }.Concat(args.Skip(3)).ToArray());
                return;
            }
            if (verb == "check")
            {
                Check(args.Length > 2 ? args[2] : null);
                return;
            }
            if (verb == "bots")
            {
                Bots(args.Length > 2 && !args[2].Contains('=') ? args[2] : null,
                    Arg(args, "time") is string t ? int.Parse(t) : 120, Arg(args, "bots") is string b ? int.Parse(b) : 3);
                return;
            }
            if (verb == "jitter")
            {
                Jitter(args.Length > 2 && !args[2].Contains('=') ? args[2] : null,
                    Arg(args, "time") is string t ? int.Parse(t) : 120, Arg(args, "bots") is string b ? int.Parse(b) : 3,
                    Arg(args, "trace") is string tr ? int.Parse(tr) : 0, Arg(args, "root"),
                    Arg(args, "hunters")?.Split(',').Select(Enum.Parse<Hunter>).ToArray(), Arg(args, "botmoves") != "0");
                return;
            }
            if (verb == "alloc")
            {
                Alloc(args.Length > 2 && !args[2].Contains('=') ? args[2] : null,
                    Arg(args, "time") is string t ? int.Parse(t) : 60, Arg(args, "bots") is string b ? int.Parse(b) : 3, Arg(args, "draw") != "0");
                return;
            }
            if (verb == "padlock")
            {
                PadLock(args.Length > 2 && !args[2].Contains('=') ? args[2] : null);
                return;
            }
            if (verb == "padvanilla")
            {
                PadVanilla(args);
                return;
            }
            if (verb == "padcompare")
            {
                PadCompare(args.Length > 2 && !args[2].Contains('=') ? args[2] : null);
                return;
            }
            if (verb == "padtest")
            {
                PadTest(args.Length > 2 && !args[2].Contains('=') ? args[2] : null);
                return;
            }
            if (verb == "waterprobe")
            {
                WaterProbe(Arg(args, "disc") ?? DefaultDisc, args.Length > 2 && !args[2].Contains('=') ? args[2] : null);
                return;
            }
            if (verb == "movetest")
            {
                MoveTest(args.Length > 2 && !args[2].Contains('=') ? args[2] : null, Arg(args, "root"));
                return;
            }
            if (verb == "jumpreach")
            {
                JumpReach(Arg(args, "arena") ?? "CrossfireChaos");
                return;
            }
            if (verb == "jumpsweep")
            {
                JumpSweep(Arg(args, "root"));
                return;
            }
            if (verb == "ledgetest")
            {
                LedgeTest(Arg(args, "ledge") != "0");
                return;
            }
            if (verb == "flicker")
            {
                Flicker(args.Length > 2 && !args[2].Contains('=') ? args[2] : "Sidehopper Station",
                    Arg(args, "time") is string t ? int.Parse(t) : 300, Arg(args, "bots") is string b ? int.Parse(b) : 3);
                return;
            }
            if (verb == "jumplog")
            {
                JumpLog(args[2], Arg(args, "seat") is string seat ? int.Parse(seat) : null, Arg(args, "root"),
                    Arg(args, "ledge") is string ledge ? ledge != "0" : null);
                return;
            }
            if (verb == "breaktest")
            {
                BreakTest(args.Length > 2 && !args[2].Contains('=') ? args[2] : null, Arg(args, "root"));
                return;
            }
            if (verb == "cratetest")
            {
                CrateTest(args.Length > 2 && !args[2].Contains('=') ? args[2] : null, Arg(args, "root"));
                return;
            }
            if (verb == "padforgive")
            {
                PadForgive(args.Length > 2 && !args[2].Contains('=') ? args[2] : null);
                return;
            }
            if (verb == "spec")
            {
                // the per-material fragment programs ArenaGxSpec writes for each imported arena (out=<dir>: one file each)
                string? outDir = Arg(args, "out");
                foreach (EchoesArena.Def d in EchoesArena.All)
                {
                    string json = Path.Combine(Paths.FileSystem, "_archives", d.Archive, ArenaHdWriter.Dir, ArenaHdWriter.Materials);
                    if (!File.Exists(json)) continue;
                    var sources = new Dictionary<string, List<string>>();
                    int none = 0;
                    foreach (MphRecomp.Assets.GxMaterial m in MphRecomp.Assets.GxJson.Read(json))
                    {
                        string? src = ArenaGxSpec.Frag(MphRecomp.Render.GxShader.Pack(m));
                        if (src == null) { none++; continue; }
                        if (!sources.TryGetValue(src, out List<string>? names)) sources[src] = names = new List<string>();
                        names.Add(m.Name);
                    }
                    Console.WriteLine($"{d.InGameName}: {sources.Count} distinct programs for {sources.Values.Sum(n => n.Count)} materials, {none} left on the interpreter");
                    if (outDir == null) continue;
                    Directory.CreateDirectory(outDir);
                    int i = 0;
                    foreach ((string src, List<string> names) in sources)
                    {
                        File.WriteAllText(Path.Combine(outDir, $"{d.Archive}_{i++:00}.frag"), $"// {string.Join(", ", names)}\n{src}");
                    }
                }
                return;
            }
            if (verb == "mphitems")
            {
                // the retail multiplayer arenas' weapon pickups (item spawns) per entity layer = mode + player count
                // (Metadata._modeLayers: Battle/Prime Hunter 0-2 = 2/3/4 players, 3 Battle teams, 4-6 Nodes, 7 Nodes teams,
                // 8-10 Bounty, 11 Bounty teams, 12 Capture, 13 unused, 14 Defender, 15 Survival)
                ItemType[] weapons = { ItemType.VoltDriver, ItemType.Battlehammer, ItemType.Imperialist, ItemType.Judicator,
                    ItemType.Magmaul, ItemType.ShockCoil, ItemType.OmegaCannon, ItemType.AffinityWeapon, ItemType.PickWpnMissile };
                var counts = new Dictionary<ItemType, int>();
                EchoesArena.Register(Paths.FileSystem);
                foreach (RoomMetadata meta in Metadata.RoomMetadata.Values.Concat(Metadata.HostRooms).Where(m => m.Multiplayer && m.EntityPath != null).DistinctBy(m => m.EntityPath))
                {
                    var perLayer = new SortedDictionary<int, List<string>>();
                    var seen = new HashSet<ItemType>();
                    for (int layer = 0; layer < 16; layer++)
                    {
                        var items = Read.GetEntities(meta.EntityPath!, layer, firstHunt: false).Where(e => e.Type == EntityType.ItemSpawn)
                            .Select(e => ((Entity<ItemSpawnEntityData>)e).Data).Where(d => weapons.Contains(d.ItemType)).ToList();
                        if (items.Count == 0) continue;
                        perLayer[layer] = items.GroupBy(d => d.ItemType).OrderBy(g => g.Key)
                            .Select(g => $"{g.Key}{(g.Count() > 1 ? "x" + g.Count() : "")}{(g.Any(d => d.Enabled == 0) ? "(off)" : "")}").ToList();
                        foreach (var d in items) seen.Add(d.ItemType);
                    }
                    foreach (ItemType t in seen) counts[t] = counts.GetValueOrDefault(t) + 1;
                    Console.WriteLine($"{meta.InGameName ?? meta.Name} [{meta.Name}]: {string.Join(", ", weapons.Where(seen.Contains))}");
                    foreach (IGrouping<string, KeyValuePair<int, List<string>>> g in perLayer.GroupBy(kv => string.Join(" ", kv.Value)))
                    {
                        Console.WriteLine($"    layers {string.Join(",", g.Select(kv => kv.Key))}: {g.Key}");
                    }
                }
                Console.WriteLine("arenas with each: " + string.Join(", ", weapons.Select(w => $"{w} {counts.GetValueOrDefault(w)}")));
                return;
            }
            if (verb == "walllight")
            {
                WallLightProbe(Arg(args, "disc") ?? DefaultDisc);
                return;
            }
            if (verb == "cratelight")
            {
                CrateLightProbe(Arg(args, "disc") ?? DefaultDisc);
                return;
            }
            if (verb == "padinfo")
            {
                // the retail multiplayer arenas' jump pads (the defaults Echoes cannons copy); "padinfo all" = every room
                bool allRooms = args.Length > 2 && args[2] == "all";
                foreach (RoomMetadata meta in Metadata.RoomMetadata.Values.Where(m => (allRooms || m.Multiplayer) && m.EntityPath != null).DistinctBy(m => m.EntityPath))
                {
                    foreach (Entity e in Read.GetEntities(meta.EntityPath!, -1, firstHunt: false).Where(e => e.Type == EntityType.JumpPad))
                    {
                        JumpPadEntityData d = ((Entity<JumpPadEntityData>)e).Data;
                        var ed = new JumpPadEntityEditor(e, d);
                        CollisionVolume v = ed.Volume;
                        string vol = v.Type == VolumeType.Cylinder ? $"cyl r {v.CylinderRadius:0.##} dot {v.CylinderDot:0.##} vec {v.CylinderVector} at {v.CylinderPosition}"
                            : v.Type == VolumeType.Sphere ? $"sphere r {v.SphereRadius:0.##} at {v.SpherePosition}" : $"box {v.BoxDot1:0.##}x{v.BoxDot2:0.##}x{v.BoxDot3:0.##} at {v.BoxPosition}";
                        Console.WriteLine($"{meta.InGameName,-22} [{meta.Name}] pad {e.EntityId} at {e.Position} up {e.UpVector} facing {e.FacingVector} beam {ed.BeamVector} speed {ed.Speed:0.###} "
                            + $"lock {ed.ControlLockTime} cool {ed.CooldownTime} model {ed.ModelId} type {ed.BeamType} flags {ed.TriggerFlags} parent {ed.ParentId} u28 {ed.Unused28} active {ed.Active} | {vol}");
                    }
                }
                return;
            }
            if (verb == "preview")
            {
                EchoesArena.Register(Paths.FileSystem);
                foreach (EchoesArena.Def d in EchoesArena.All)
                {
                    if (args.Length > 2 && EchoesArena.Find(args[2]) != d) continue;
                    if (Metadata.GetRoomByName(d.Room).Item1 == null) continue;
                    string png = Path.Combine(Paths.FileSystem, "_archives", d.Archive, "preview.png");
                    Preview(d, png);
                    Console.WriteLine($"{d.InGameName}: {png}");
                }
                return;
            }
            if (verb == "scripts")
            {
                // every script object of one type in each arena (T6 2026-10-10: PCKP items/amounts, active or not)
                ScriptsVerb(Arg(args, "disc") ?? DefaultDisc, Arg(args, "type") ?? "PCKP", Arg(args, "arena"));
                return;
            }
            if (verb == "bare")
            {
                BareVerb(Arg(args, "disc") ?? DefaultDisc, Arg(args, "arena"),
                    Arg(args, "gap") is string g ? float.Parse(g, System.Globalization.CultureInfo.InvariantCulture) : 0.35f);
                return;
            }
            if (verb == "ammospots")
            {
                AmmoSpotsVerb(Arg(args, "disc") ?? DefaultDisc);
                return;
            }
            if (verb == "music")
            {
                MusicVerb(args);
                return;
            }
            if (verb == "replaymove")
            {
                // -echoesarena replaymove <file.mphreplay>: the local player's move stick and buttons per step (the
                // -replayplay trace shows the aim delta; this is the stick that steers a pad flight)
                MphRecomp.Replay.ReplayFile rf = MphRecomp.Replay.ReplayFile.Load(args[2]);
                for (int i = 0; i < rf.Steps.Count; i++)
                {
                    MphRecomp.Campaign.CampaignInput ci = rf.Steps[i].Input;
                    Console.WriteLine($"{i} {ci.Move.X:0.000} {ci.Move.Y:0.000} {ci.Buttons}");
                }
                return;
            }
            if (verb != "import")
            {
                Console.WriteLine($"-echoesarena: unknown verb {verb} (import, music, sim, check, bots, alloc, preview, padinfo, padtest, spec)");
                return;
            }
            string disc = Arg(args, "disc") ?? DefaultDisc;
            string which = Arg(args, "arena") ?? "all";
            float scale = Arg(args, "scale") is string s ? float.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : EchoesArena.DefaultScale;
            // (any image DiscImage reads: the retail .rvz too, the turret's source disc)
            using MphRecomp.Import.IFileSource gc = DiscImage.Open(disc);
            if (gc.GameId != "G2ME01")
            {
                Console.WriteLine($"  note: {gc.GameId} is not the NTSC-U Echoes disc these arenas were checked on");
            }
            var pak = new Pak(gc.ReadFile("Metroid6.pak"), "Metroid6.pak");
            // the sound lookup + the slot's arc group: Shooting Gallery's turret sounds
            var audio = new Pak(gc.ReadFile("AudioGrp.pak"), "AudioGrp.pak");
            // the turret HUD's textures
            var hud = new Pak(gc.ReadFile("NoARAM.pak"), "NoARAM.pak");
            foreach (EchoesArena.Def def in EchoesArena.All)
            {
                if (which != "all" && EchoesArena.Find(which) != def) continue;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // out=<dir> (T6 2026-10-10): one folder per arena there (the in-app import's layout, pads not tuned), not
                // the shared staged file system
                if (Arg(args, "out") is string outDir)
                {
                    EchoesArena.Import(pak, def, EchoesArena.Layout.Folder(Path.Combine(outDir, def.Key)), scale, null, Console.WriteLine, audio: audio, hud: hud);
                    Console.WriteLine($"  ({sw.ElapsedMilliseconds} ms)");
                    continue;
                }
                EchoesArena.Import(pak, def, Paths.FileSystem, scale, Console.WriteLine, audio, hud);
                Console.WriteLine($"  ({sw.ElapsedMilliseconds} ms)");
            }
            if (Arg(args, "out") == null) ArenaMusic.Import(gc, Paths.FileSystem, Console.WriteLine);
        }

        static void ScriptsVerb(string disc, string type, string? which)
        {
            using MphRecomp.Import.IFileSource gc = DiscImage.Open(disc);
            var pak = new Pak(gc.ReadFile("Metroid6.pak"), "Metroid6.pak");
            foreach (EchoesArena.Def def in EchoesArena.All)
            {
                if (which != null && EchoesArena.Find(which) != def) continue;
                Mrea area = Mrea.Read(pak.Get(def.Mrea));
                // (SCRIPTS_GENERATED=1: with the generated layer, where generators' templates live)
                List<Mrea.ScriptObject> objs = area.ReadScripts(includeGenerated: Environment.GetEnvironmentVariable("SCRIPTS_GENERATED") == "1")
                    .Where(o => type == "*" || o.Type == type).ToList();
                Console.WriteLine($"{def.InGameName}: {objs.Count} {type}");
                foreach (Mrea.ScriptObject o in objs)
                {
                    float[] x = o.Transform;
                    string extra = type == "PCKP"
                        ? $" item 0x{o.Root.Find(0xA02EF0C4)?.U32 ?? 0xFFFFFFFF:x} amount {o.Root.Find(0x94AF1445)?.I32 ?? 0}"
                        : "";
                    Console.WriteLine($"  {o.Id:x8} {(type == "*" ? o.Type + " " : "")}L{o.Layer} {(o.Active ? "on " : "off")} '{o.Name}' at ({x[0]:0.0}, {x[1]:0.0}, {x[2]:0.0}){extra}" +
                        $" conns {o.Connections.Count}" + (Environment.GetEnvironmentVariable("SCRIPTS_CONNS") == "1" ? ": " + string.Join(", ", o.Connections.Select(c => $"{c.State} {c.Message} {c.Target:x8}")) : ""));
                    if (Environment.GetEnvironmentVariable("SCRIPTS_PROPS") == "1")
                    {
                        void Dump(Mrea.Property pr, string indent)
                        {
                            foreach (Mrea.Property c in pr.Children ?? new List<Mrea.Property>())
                            {
                                string v = c.Data.Length == 4 ? $" = 0x{c.U32:x8} ({c.F32:0.###})" : c.Data.Length == 1 ? $" = {c.Data[0]}" : $" [{c.Data.Length} bytes]";
                                Console.WriteLine($"{indent}{c.Id:x8}{v}");
                                if (c.Children != null && indent.Length < 10) Dump(c, indent + "  ");
                            }
                        }
                        Dump(o.Root, "      ");
                    }
                }
            }
        }

        // T6 N5 (2026-10-10): spots for the UA pickups Echoes arenas get (EchoesAmmo). Anchors = Echoes' own Missile x5
        // spots, spread out (farthest-point order from the first); each UA sits 1.6 units beside its anchor on the same
        // floor, clear of walls and of every other pickup. Prints the table EchoesAmmo.cs holds.
        static void AmmoSpotsVerb(string disc)
        {
            using FileStream fs = File.OpenRead(disc);
            var gc = new GcDisc(fs);
            var pak = new Pak(gc.ReadFile("Metroid6.pak"), "Metroid6.pak");
            float scale = EchoesArena.DefaultScale;
            OpenTK.Mathematics.Vector3 Map(float x, float y, float z) => new OpenTK.Mathematics.Vector3(x, z, -y) * scale;
            foreach (EchoesArena.Def def in EchoesArena.All)
            {
                Mrea area = Mrea.Read(pak.Get(def.Mrea));
                List<Mrea.ScriptObject> scripts = area.ReadScripts();
                Mrea.Collision col = area.ReadCollision();
                var tris = new List<(OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3)>();
                for (int t = 0; t < col.TriangleCount; t++)
                {
                    OpenTK.Mathematics.Vector3 V(int k) { int i = col.Triangles[3 * t + k]; return Map(col.Vertices[3 * i], col.Vertices[3 * i + 1], col.Vertices[3 * i + 2]); }
                    tris.Add((V(0), V(1), V(2)));
                }
                var ray = new ArenaRaycaster(tris);
                OpenTK.Mathematics.Vector3 OnFloor(OpenTK.Mathematics.Vector3 p) => ray.FloorBelow(p + OpenTK.Mathematics.Vector3.UnitY * 0.3f, 3f) ?? p;
                var pickups = scripts.Where(o => o.Active && o.Type == "PCKP").Select(o => (Missile: o.Root.Find(0xA02EF0C4)?.U32 == 0x2C,
                    Pos: OnFloor(Map(o.Transform[0], o.Transform[1], o.Transform[2])))).ToList();
                var missiles = pickups.Where(p => p.Missile).Select(p => p.Pos).ToList();
                int want = Math.Clamp((int)Math.Round(missiles.Count / 2.0, MidpointRounding.AwayFromZero), 4, 6);
                // every missile in farthest-point order; the first `want` that have room get a UA
                var anchors = new List<OpenTK.Mathematics.Vector3> { missiles[0] };
                while (anchors.Count < missiles.Count)
                {
                    anchors.Add(missiles.Where(m => !anchors.Contains(m)).OrderByDescending(m => anchors.Min(a => (a - m).LengthSquared)).First());
                }
                // then the health spots (an arena whose missile spots are all on tight ledges: Pipeline)
                var health = pickups.Where(p => !p.Missile).Select(p => p.Pos).ToList();
                while (health.Count > 0)
                {
                    OpenTK.Mathematics.Vector3 h = health.OrderByDescending(m => anchors.Min(a => (a - m).LengthSquared)).First();
                    anchors.Add(h);
                    health.Remove(h);
                }
                var placed = new List<OpenTK.Mathematics.Vector3>();
                Console.WriteLine($"{def.Key} ({def.InGameName}): {missiles.Count} missiles -> {want} UA");
                foreach (OpenTK.Mathematics.Vector3 a in anchors)
                {
                    if (placed.Count >= want) break;
                    OpenTK.Mathematics.Vector3? best = null;
                    // try 16 directions at 1.6, 1.2 and 2.2 units, the one toward the arena's middle first
                    float start = MathF.Atan2(-a.Z, -a.X);
                    for (int k = 0; k < 48 && best == null; k++)
                    {
                        float ang = start + (k % 2 == 0 ? 1 : -1) * ((k % 16 + 1) / 2) * MathF.PI / 8;
                        float dist = k < 16 ? 1.6f : k < 32 ? 1.2f : 2.2f;
                        var c = a + new OpenTK.Mathematics.Vector3(MathF.Cos(ang), 0, MathF.Sin(ang)) * dist;
                        if (ray.FloorBelow(c + OpenTK.Mathematics.Vector3.UnitY * 0.4f, 1.0f) is not { } f || MathF.Abs(f.Y - a.Y) > 0.3f) continue;
                        var up = OpenTK.Mathematics.Vector3.UnitY * 0.8f;
                        if (ray.Cast(a + up, f + up) != null) continue;   // a wall between
                        bool tight = false;
                        for (int d = 0; d < 8 && !tight; d++)
                        {
                            float da = d * MathF.PI / 4;
                            if (ray.Cast(f + up, f + up + new OpenTK.Mathematics.Vector3(MathF.Cos(da), 0, MathF.Sin(da)) * 0.7f) != null) tight = true;
                        }
                        if (tight || ray.Cast(f + OpenTK.Mathematics.Vector3.UnitY * 0.1f, f + OpenTK.Mathematics.Vector3.UnitY * 2.2f) != null) continue;
                        // a floor edge: the floor must go on 0.5 past it
                        var dir = (f - a).Normalized();
                        if (ray.FloorBelow(f + dir * 0.5f + OpenTK.Mathematics.Vector3.UnitY * 0.4f, 1.0f) is not { } g || MathF.Abs(g.Y - f.Y) > 0.3f) continue;
                        if (pickups.Any(p => p.Pos != a && (p.Pos - f).Length < 1.2f) || placed.Any(p => (p - f).Length < 3f)) continue;
                        best = f;
                    }
                    if (best is { } b)
                    {
                        placed.Add(b);
                        Console.WriteLine($"    new Vector3({b.X:0.00}f, {b.Y:0.00}f, {b.Z:0.00}f),   // beside {(missiles.Contains(a) ? "missile" : "health")} ({a.X:0.0}, {a.Y:0.0}, {a.Z:0.0})");
                    }
                    else
                    {
                        Console.WriteLine($"    // no spot beside missile ({a.X:0.0}, {a.Y:0.0}, {a.Z:0.0})");
                    }
                }
                if (placed.Count < want) Console.WriteLine($"    // only {placed.Count} of {want}");
            }
        }

        // T6 N3 (2026-10-10, owner "invisible map objects in spires"): collision triangles with no drawn surface near them.
        // Collision = the area's (what the import converts); drawn = the area's render geometry (every material: the HD
        // look draws all of them). A collision triangle is bare when its centre is over `gap` units from every render
        // triangle; bare triangles are grouped by 3-unit cells and each group listed with its size, facing and the script
        // objects (not triggers/sounds) within 3 units of it.
        static void BareVerb(string disc, string? which, float gap)
        {
            using FileStream fs = File.OpenRead(disc);
            var gc = new GcDisc(fs);
            var pak = new Pak(gc.ReadFile("Metroid6.pak"), "Metroid6.pak");
            float scale = EchoesArena.DefaultScale;
            OpenTK.Mathematics.Vector3 Map(float x, float y, float z) => new OpenTK.Mathematics.Vector3(x, z, -y) * scale;
            foreach (EchoesArena.Def def in EchoesArena.All)
            {
                if (which != null && EchoesArena.Find(which) != def) continue;
                Mrea area = Mrea.Read(pak.Get(def.Mrea));
                Cmdl geo = area.ReadGeometry();
                var render = new List<(OpenTK.Mathematics.Vector3 A, OpenTK.Mathematics.Vector3 B, OpenTK.Mathematics.Vector3 C)>();
                for (int t = 0; t < geo.TriangleCount; t++)
                {
                    OpenTK.Mathematics.Vector3 P(int k) { Cmdl.Corner c = geo.Triangles[3 * t + k]; return Map(geo.Positions[3 * c.P], geo.Positions[3 * c.P + 1], geo.Positions[3 * c.P + 2]); }
                    render.Add((P(0), P(1), P(2)));
                }
                const float Cell = 2f;
                var grid = new Dictionary<(int, int, int), List<int>>();
                (int, int, int) Key(OpenTK.Mathematics.Vector3 v) => ((int)MathF.Floor(v.X / Cell), (int)MathF.Floor(v.Y / Cell), (int)MathF.Floor(v.Z / Cell));
                for (int i = 0; i < render.Count; i++)
                {
                    var (a, b, c) = render[i];
                    var lo = OpenTK.Mathematics.Vector3.ComponentMin(a, OpenTK.Mathematics.Vector3.ComponentMin(b, c)) - new OpenTK.Mathematics.Vector3(gap);
                    var hi = OpenTK.Mathematics.Vector3.ComponentMax(a, OpenTK.Mathematics.Vector3.ComponentMax(b, c)) + new OpenTK.Mathematics.Vector3(gap);
                    var k0 = Key(lo); var k1 = Key(hi);
                    if ((long)(k1.Item1 - k0.Item1 + 1) * (k1.Item2 - k0.Item2 + 1) * (k1.Item3 - k0.Item3 + 1) > 4000) continue;   // a sky dome
                    for (int x = k0.Item1; x <= k1.Item1; x++)
                        for (int y = k0.Item2; y <= k1.Item2; y++)
                            for (int z = k0.Item3; z <= k1.Item3; z++)
                            {
                                if (!grid.TryGetValue((x, y, z), out List<int>? l)) grid[(x, y, z)] = l = new List<int>();
                                l.Add(i);
                            }
                }
                Mrea.Collision col = area.ReadCollision();
                var bare = new List<(OpenTK.Mathematics.Vector3 Centre, OpenTK.Mathematics.Vector3 N, float Area, ulong Flags)>();
                int total = 0;
                for (int t = 0; t < col.TriangleCount; t++)
                {
                    OpenTK.Mathematics.Vector3 V(int k) { int i = col.Triangles[3 * t + k]; return Map(col.Vertices[3 * i], col.Vertices[3 * i + 1], col.Vertices[3 * i + 2]); }
                    var (a, b, c) = (V(0), V(1), V(2));
                    var cr = OpenTK.Mathematics.Vector3.Cross(b - a, c - a);
                    if (cr.LengthSquared < 1e-10f) continue;
                    total++;
                    var centre = (a + b + c) / 3;
                    float best = float.MaxValue;
                    if (grid.TryGetValue(Key(centre), out List<int>? cand))
                    {
                        foreach (int i in cand)
                        {
                            best = MathF.Min(best, (ClosestOnTriangle(centre, render[i].A, render[i].B, render[i].C) - centre).Length);
                            if (best <= gap) break;
                        }
                    }
                    if (best > gap && cr.Length / 2 < 500) bare.Add((centre, cr.Normalized(), cr.Length / 2, col.TriangleFlags[t]));   // (not the bounds box)
                }
                List<Mrea.ScriptObject> scripts = area.ReadScripts();
                Console.WriteLine($"{def.InGameName}: {bare.Count} of {total} collision triangles bare (> {gap} from any render triangle), {render.Count} render triangles");
                // group by 3-unit cells, joined through neighbours
                var cells = bare.Select((b, i) => (Cell: ((int)MathF.Floor(b.Centre.X / 2), (int)MathF.Floor(b.Centre.Y / 2), (int)MathF.Floor(b.Centre.Z / 2)), I: i))
                    .GroupBy(x => x.Cell).ToDictionary(g => g.Key, g => g.Select(x => x.I).ToList());
                var seen = new HashSet<(int, int, int)>();
                var groups = new List<List<int>>();
                foreach (var start in cells.Keys)
                {
                    if (!seen.Add(start)) continue;
                    var g = new List<int>();
                    var q = new Queue<(int, int, int)>();
                    q.Enqueue(start);
                    while (q.Count > 0)
                    {
                        var k = q.Dequeue();
                        g.AddRange(cells[k]);
                        for (int dx = -1; dx <= 1; dx++)
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dz = -1; dz <= 1; dz++)
                                {
                                    var n = (k.Item1 + dx, k.Item2 + dy, k.Item3 + dz);
                                    if (cells.ContainsKey(n) && seen.Add(n)) q.Enqueue(n);
                                }
                    }
                    groups.Add(g);
                }
                foreach (List<int> g in groups.OrderByDescending(g => g.Sum(i => bare[i].Area)).Take(40))
                {
                    var lo = g.Select(i => bare[i].Centre).Aggregate(OpenTK.Mathematics.Vector3.ComponentMin);
                    var hi = g.Select(i => bare[i].Centre).Aggregate(OpenTK.Mathematics.Vector3.ComponentMax);
                    float areaSum = g.Sum(i => bare[i].Area);
                    int up = g.Count(i => bare[i].N.Y > 0.7f), down = g.Count(i => bare[i].N.Y < -0.7f);
                    var surfaces = g.Select(i => Mrea.Collision.SurfaceOf(bare[i].Flags).ToString()).GroupBy(s => s).OrderByDescending(s => s.Count()).Take(3).Select(s => $"{s.Key}x{s.Count()}");
                    var near = scripts.Where(o => o.Type is not ("TRGR" or "SOND" or "SPFN" or "WAYP" or "TIMR" or "RLAY" or "CAMH" or "DLHT"))
                        .Select(o => (o, P: Map(o.Transform[0], o.Transform[1], o.Transform[2])))
                        .Where(x => x.P.X > lo.X - 3 && x.P.X < hi.X + 3 && x.P.Y > lo.Y - 3 && x.P.Y < hi.Y + 3 && x.P.Z > lo.Z - 3 && x.P.Z < hi.Z + 3)
                        .Select(x => $"{x.o.Type} '{x.o.Name}'{(x.o.Active ? "" : " (off)")}").Distinct().Take(6);
                    Console.WriteLine($"  {g.Count,4} tris, {areaSum,7:0.0} sq u, ({lo.X:0.0}, {lo.Y:0.0}, {lo.Z:0.0})..({hi.X:0.0}, {hi.Y:0.0}, {hi.Z:0.0}), up {up} down {down} side {g.Count - up - down}; {string.Join(" ", surfaces)}; near: {string.Join(", ", near)}");
                }
            }
        }

        // the point of triangle abc nearest p (Ericson, Real-Time Collision Detection 5.1.5)
        static OpenTK.Mathematics.Vector3 ClosestOnTriangle(OpenTK.Mathematics.Vector3 p, OpenTK.Mathematics.Vector3 a, OpenTK.Mathematics.Vector3 b, OpenTK.Mathematics.Vector3 c)
        {
            var ab = b - a; var ac = c - a; var ap = p - a;
            float d1 = OpenTK.Mathematics.Vector3.Dot(ab, ap), d2 = OpenTK.Mathematics.Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;
            var bp = p - b;
            float d3 = OpenTK.Mathematics.Vector3.Dot(ab, bp), d4 = OpenTK.Mathematics.Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            var cp = p - c;
            float d5 = OpenTK.Mathematics.Vector3.Dot(ab, cp), d6 = OpenTK.Mathematics.Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
            float denom = 1 / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }

        static void MusicVerb(string[] args)
        {
            string disc = Arg(args, "disc") ?? DefaultDisc;
            if (File.Exists(disc))
            {
                using FileStream fs = File.OpenRead(disc);
                using var gc = new GcDisc(fs);
                ArenaMusic.Import(gc, Paths.FileSystem, Console.WriteLine);
            }
            string dir = ArenaMusic.Folder(Paths.FileSystem);
            string? wav = Arg(args, "wav");
            if (wav != null) Directory.CreateDirectory(wav);
            foreach (string file in ArenaMusic.Files)
            {
                string path = Path.Combine(dir, file);
                if (!File.Exists(path))
                {
                    Console.WriteLine($"  {file}: missing");
                    continue;
                }
                var stream = new RetroStream(File.ReadAllBytes(path));
                // level of the first play + one loop, as interleaved PCM16
                RetroStreamReader reader = stream.OpenReader();
                long frames = stream.Loops ? stream.LoopEnd + (long)(stream.LoopEnd - stream.LoopStart) : stream.SampleCount;
                var buf = new short[4096 * stream.Channels];
                var pcm = wav != null ? new List<short>() : null;
                double sumSq = 0;
                int peak = 0;
                long n = 0;
                for (long left = frames; left > 0;)
                {
                    int got = reader.Read(buf, (int)Math.Min(4096, left));
                    if (got == 0) break;
                    for (int i = 0; i < got * stream.Channels; i++)
                    {
                        sumSq += (double)buf[i] * buf[i];
                        peak = Math.Max(peak, Math.Abs((int)buf[i]));
                    }
                    n += got * stream.Channels;
                    pcm?.AddRange(buf.AsSpan(0, got * stream.Channels).ToArray());
                    left -= got;
                }
                static double Db(double v) => 20 * Math.Log10(Math.Max(v, 1e-9) / 32768);
                Console.WriteLine($"  {file}: {stream.Channels} ch {stream.SampleRate} Hz, {stream.SampleCount / (double)stream.SampleRate:0.0} s, "
                    + (stream.Loops ? $"loop {stream.LoopStart / (double)stream.SampleRate:0.00}-{stream.LoopEnd / (double)stream.SampleRate:0.00} s" : "no loop")
                    + $", peak {Db(peak):0.0} dBFS, RMS {Db(Math.Sqrt(sumSq / Math.Max(n, 1))):0.0} dBFS");
                if (pcm != null) WriteWav(Path.Combine(wav!, Path.ChangeExtension(file, ".wav")), pcm, stream.Channels, stream.SampleRate);
            }
            if (Arg(args, "match") is string arena)
            {
                MusicMatch(arena, Arg(args, "time") is string t ? float.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : 70);
            }
        }

        static void WriteWav(string path, List<short> pcm, int channels, int rate)
        {
            using var w = new BinaryWriter(File.Create(path));
            int bytes = pcm.Count * 2;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
            w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
            w.Write("data"u8); w.Write(bytes);
            foreach (short v in pcm) w.Write(v);
        }

        sealed class LogOutput : ArenaMusic.IOutput
        {
            public Func<long> Frame = () => 0;
            public void Start(RetroStream stream, float gain, float fadeIn, float delay, float oldFadeOut) =>
                Console.WriteLine($"    frame {Frame()}: Start {stream.SampleCount / (double)stream.SampleRate:0.0} s stream, gain {gain:0.00}, fade in {fadeIn} s, after {delay} s, old fades {oldFadeOut} s");
            public void Stop(float fadeOut) => Console.WriteLine($"    frame {Frame()}: Stop, fade {fadeOut:0.00} s");
        }

        // a Battle with bots at the arena, a short time limit, the music hook's calls logged with the frame they came on
        static void MusicMatch(string arena, float seconds)
        {
            EchoesArena.Register(Paths.FileSystem);
            EchoesArena.Def def = EchoesArena.Find(arena) ?? throw new ArgumentException($"no Echoes arena {arena}");
            var output = new LogOutput();
            ArenaMusic.Attach(output, m => Console.WriteLine($"    {m}"));
            var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, Hunter.Samus, 3, 1);
            settings.TimeLimitSeconds = seconds;
            Console.WriteLine($"  match at {def.InGameName}, time limit {seconds} s:");
            MphRecomp.Campaign.CampaignHost? host = null;
            output.Frame = () => host?.Frame ?? 0;
            host = MphRecomp.Campaign.CampaignHost.StartMatch(settings, collectDrawItems: false, viewWidth: 1920, viewHeight: 1080);
            var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            int limit = (int)((seconds + 20) * 60);
            for (int f = 0; f < limit && !host.Ended; f++) host.Step(input);
            Console.WriteLine($"  ended {host.Ended} at frame {host.Frame}");
            MphRead.Music.RoomHost = null;
        }

        static void Check(string? only)
        {
            int n = EchoesArena.Register(Paths.FileSystem);
            Console.WriteLine($"{n} Echoes arenas registered");
            var rooms = EchoesArena.All.Where(d => only == null || EchoesArena.Find(only) == d)
                .Select(d => (d.Room, d.InGameName)).Prepend(("MP3 PROVING GROUND", "Combat Hall")).ToList();
            GameMode[] modes = { GameMode.Battle, GameMode.Survival, GameMode.PrimeHunter };
            int failed = 0;
            foreach ((string room, string inGame) in rooms)
            {
                foreach (GameMode mode in modes)
                {
                    string result;
                    MphRecomp.Campaign.CampaignHost? host = null;
                    try
                    {
                        var settings = MphRecomp.Multiplayer.MatchSettings.Quick(mode, room, Hunter.Samus, 3, 1);
                        host = MphRecomp.Campaign.CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
                        var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                        string? status = null;
                        for (int f = 0; f < 300 && !host.Ended; f++)
                        {
                            host.Step(input);
                            // CampaignRenderer.MatchStatusText / Status / CampaignMap: every frame from the scene's id
                            RoomMetadata meta = Metadata.GetRoomById(host.RoomId) ?? throw new Exception($"GetRoomById({host.RoomId}) = null");
                            status = $"{meta.InGameName}  " + MphRecomp.Multiplayer.MatchStatus.Scoreboard();
                        }
                        (RoomMetadata? byName, int id) = Metadata.GetRoomByName(room);
                        RoomMetadata byId = Metadata.GetRoomById(host.RoomId)!;
                        if (byName != byId || id != host.RoomId || byId.InGameName != inGame)
                        {
                            throw new Exception($"room {room}: by name id {id}, scene id {host.RoomId}, by id {byId.Name}");
                        }
                        result = $"ok    id {host.RoomId}, {host.Frame} f, \"{status!.Split('\n')[0]}\"";
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        result = $"FAIL  {ex.GetType().Name}: {ex.Message}";
                    }
                    finally
                    {
                        host?.Dispose();
                    }
                    Console.WriteLine($"  {inGame,-20} {mode,-12} {result}");
                }
            }
            Console.WriteLine(failed == 0 ? $"check: all {rooms.Count * modes.Length} ok" : $"check: {failed} FAILED");
        }

        // owner (Odin, 2026-10-03): bots on the Echoes arenas "get stuck pivoting around in one spot shooting wildly". A Battle
        // with bots per arena (Combat Hall as the control; the human seat stands still): every 2 s window a bot is alive
        // throughout, its path length; a window under 3 units is "stuck". Prints the stuck share per arena and where.
        static void Bots(string? only, int seconds, int bots)
        {
            EchoesArena.Register(Paths.FileSystem);
            var rooms = EchoesArena.All.Where(d => only == null || EchoesArena.Find(only) == d)
                .Select(d => (d.Room, d.InGameName)).Prepend(("MP3 PROVING GROUND", "Combat Hall")).ToList();
            const int Window = 120;
            foreach ((string room, string inGame) in rooms)
            {
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, room, Hunter.Samus, bots, 2);
                settings.TimeLimitSeconds = seconds + 30;
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                int seats = settings.Players.Count;
                var path = new float[seats];
                var last = new OpenTK.Mathematics.Vector3[seats];
                var aliveAll = new bool[seats];
                int windows = 0, stuck = 0;
                var spots = new System.Collections.Generic.Dictionary<(int, int, int), int>();
                for (int i = 0; i < seats; i++) { last[i] = PlayerEntity.Players[i].Position; aliveAll[i] = true; }
                // T6 N5: the UA pickups (EchoesAmmo) up now; one gone since the last step = taken, by the nearest player
                var uaUp = new System.Collections.Generic.HashSet<ItemInstanceEntity>();
                int uaMax = 0, uaBot = 0, uaHuman = 0;
                for (int f = 1; f <= seconds * 60 && !host.Ended; f++)
                {
                    host.Step(input);
                    var uaNow = new System.Collections.Generic.HashSet<ItemInstanceEntity>();
                    foreach (EntityBase e in host.Scene.Entities)
                    {
                        if (e is ItemInstanceEntity { ItemType: ItemType.UABig } it) uaNow.Add(it);
                    }
                    foreach (ItemInstanceEntity gone in uaUp.Where(u => !uaNow.Contains(u)))
                    {
                        int who = Enumerable.Range(0, seats).OrderBy(i => (PlayerEntity.Players[i].Position - gone.Position).LengthSquared).First();
                        if (settings.Players[who].Bot) uaBot++; else uaHuman++;
                    }
                    uaUp = uaNow;
                    uaMax = Math.Max(uaMax, uaNow.Count);
                    for (int i = 0; i < seats; i++)
                    {
                        PlayerEntity p = PlayerEntity.Players[i];
                        bool alive = p.LoadFlags.TestFlag(LoadFlags.Active) && p.Health > 0;
                        if (!alive) aliveAll[i] = false;
                        else path[i] += (p.Position - last[i]).Length;
                        last[i] = p.Position;
                    }
                    if (f % Window != 0) continue;
                    for (int i = 0; i < seats; i++)
                    {
                        if (settings.Players[i].Bot && aliveAll[i] && f > Window)
                        {
                            windows++;
                            if (path[i] < 3)
                            {
                                stuck++;
                                OpenTK.Mathematics.Vector3 q = PlayerEntity.Players[i].Position;
                                var key = ((int)MathF.Round(q.X / 4) * 4, (int)MathF.Round(q.Y / 4) * 4, (int)MathF.Round(q.Z / 4) * 4);
                                spots[key] = spots.GetValueOrDefault(key) + 1;
                            }
                        }
                        path[i] = 0;
                        aliveAll[i] = true;
                    }
                }
                int kills = Enumerable.Range(0, seats).Sum(i => GameState.Kills[i]);
                Console.WriteLine($"  {inGame,-20} {bots} bots, {host.Frame / 60} s: stuck {stuck} of {windows} bot windows ({(windows > 0 ? 100.0 * stuck / windows : 0):0}%), kills {kills}"
                    + $"; UA up at most {uaMax}, taken by bots {uaBot}, by the idle seat {uaHuman}"
                    + (spots.Count > 0 ? "; spots " + string.Join(", ", spots.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} x{kv.Value}")) : ""));
            }
        }

        // owner 10-09: bots "rapidly moving back and forth" in Echoes arenas. `bots` only counts bots that stand still; this
        // counts 1 s bot windows with many horizontal direction reversals and little net travel (jitter), and with
        // trace=N prints the first N jitter windows: the bot's target node (AiData._node40) per step and where it is.
        // hunters=Noxus,Sylux,...: the bots' hunters in seat order (default MatchSettings.Quick's: Kanden, Spire, Trace, ...).
        // "stays" (T7, owner 10-10 "Noxus got stuck jumping back and forth on a grate"): a bot that has stayed within 3
        // units across the floor for the last 10 s, counted per second (slow back-and-forth the 1 s jitter test misses)
        static void Jitter(string? only, int seconds, int bots, int trace, string? root, Hunter[]? hunters = null, bool botMoves = true)
        {
            EchoesArena.Register(Paths.FileSystem);
            if (root != null)
            {
                EchoesArena.ImportRoot = root;
                EchoesArena.RegisterImported(root);
            }
            var rooms = EchoesArena.All.Where(d => only == null || EchoesArena.Find(only) == d)
                .Select(d => (d.Room, d.InGameName)).Prepend(("MP3 PROVING GROUND", "Combat Hall")).ToList();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            System.Reflection.FieldInfo node40 = typeof(PlayerEntity.PlayerAiData).GetField("_node40", flags)!;
            System.Reflection.FieldInfo node3C = typeof(PlayerEntity.PlayerAiData).GetField("_node3C", flags)!;
            const int Window = 60;
            foreach ((string room, string inGame) in rooms)
            {
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, room, Hunter.Samus, bots, 2);
                settings.TimeLimitSeconds = seconds + 30;
                for (int i = 0; hunters != null && i < hunters.Length && i + 1 < settings.Players.Count; i++) settings.Players[i + 1].Hunter = hunters[i];
                if (!botMoves) settings.EchoesBotMoves = false;   // botmoves=0: T7's link jumps + item skips off
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                int seats = settings.Players.Count;
                var ring = new Queue<OpenTK.Mathematics.Vector3>[seats];
                for (int i = 0; i < seats; i++) ring[i] = new();
                int stays = 0;
                var staySpots = new Dictionary<string, int>();
                var start = new OpenTK.Mathematics.Vector3[seats];
                var last = new OpenTK.Mathematics.Vector3[seats];
                var lastV = new OpenTK.Mathematics.Vector2[seats];
                var flips = new int[seats];
                var targets = new HashSet<int>[seats];
                var log = new List<string>[seats];
                var alive = new bool[seats];
                int windows = 0, jitter = 0, traced = 0, atDest = 0;
                var spots = new Dictionary<(int, int, int), int>();
                for (int i = 0; i < seats; i++)
                {
                    start[i] = last[i] = PlayerEntity.Players[i].Position;
                    targets[i] = new HashSet<int>(); log[i] = new List<string>(); alive[i] = true;
                }
                // env GIVEUPS=1: each unstick give-up with the bot's last 3 s (Y range, XZ spread, grounded share)
                var hist = new Queue<(OpenTK.Mathematics.Vector3 P, bool G)>[seats];
                for (int i = 0; i < seats; i++) hist[i] = new();
                var giveNodes = new Dictionary<int, int>();
                PlayerEntity.HostUnstickGiveUp = Environment.GetEnvironmentVariable("GIVEUPS") != "1" ? null : (p, n) =>
                {
                    giveNodes[n.Id] = giveNodes.GetValueOrDefault(n.Id) + 1;
                    var h = hist[p.SlotIndex];
                    float y0 = h.Count > 0 ? h.Min(e => e.P.Y) : 0, y1 = h.Count > 0 ? h.Max(e => e.P.Y) : 0;
                    float xz = h.Count > 0 ? h.Max(e => (e.P - p.Position).Xz.Length) : 0;
                    Console.WriteLine($"    giveup seat {p.SlotIndex} pos ({p.Position.X:0.0},{p.Position.Y:0.0},{p.Position.Z:0.0}) node {n.Id} ({n.Position.X:0.0},{n.Position.Y:0.0},{n.Position.Z:0.0}) "
                        + $"dxz {(n.Position - p.Position).Xz.Length:0.00} dy {n.Position.Y - p.Position.Y:0.00} max {n.MaxDistance:0.00} type {n.NodeType} gnd {(p.Flags1.TestFlag(PlayerFlags1.Grounded) ? 1 : 0)} "
                        + $"alt {(p.IsAltForm ? 1 : 0)} spd ({p.Speed.X:0.00},{p.Speed.Y:0.00},{p.Speed.Z:0.00}) last3s y {y0:0.0}..{y1:0.0} xz {xz:0.0} gnd {h.Count(e => e.G) * 100 / Math.Max(1, h.Count)}%"
                        + $" dest {(node3C.GetValue(p.AiData!) as MphRead.Formats.NodeData3)?.Id} ai {p.AiData!.HostDebugState()}");
                    if (giveNodes[n.Id] == 1)
                    {
                        var touch = new MphRead.Formats.CollisionResult[8];
                        int nt = MphRead.Formats.CollisionDetection.CheckInRadius(p.Position, 1f, 8, false, MphRead.Formats.TestFlags.Players, host.Scene, touch);
                        for (int t = 0; t < nt; t++) Console.WriteLine($"      touch n ({touch[t].Plane.X:0.00},{touch[t].Plane.Y:0.00},{touch[t].Plane.Z:0.00}) d {touch[t].Plane.W:0.00}");
                        var to = new MphRead.Formats.CollisionResult();
                        OpenTK.Mathematics.Vector3 a0 = p.Position + OpenTK.Mathematics.Vector3.UnitY * 0.3f, a1 = n.Position + OpenTK.Mathematics.Vector3.UnitY * 0.3f;
                        if (MphRead.Formats.CollisionDetection.CheckBetweenPoints(a0, new OpenTK.Mathematics.Vector3(a1.X, a0.Y, a1.Z), MphRead.Formats.TestFlags.Players, host.Scene, ref to))
                            Console.WriteLine($"      toward node: blocked at {to.Distance:0.00} n ({to.Plane.X:0.00},{to.Plane.Y:0.00},{to.Plane.Z:0.00}) d {to.Plane.W:0.00}");
                        if (host.Scene.Room?.NodeData is { } nd2 && node3C.GetValue(p.AiData!) is MphRead.Formats.NodeData3 dst) // the routes from her 4 closest nodes to her destination
                        {
                            var all = nd2.Data.SelectMany(s => s.SelectMany(l => l)).ToList();
                            var near = all.OrderBy(q => (q.Position - p.Position).LengthSquared).Take(4).ToList();
                            foreach (var st in near)
                            {
                                var path = new List<string>();
                                var at = st; int guard = 0;
                                while (at.Id != dst.Id && guard++ < 30)
                                {
                                    path.Add($"{at.Id}({at.Position.X:0.0},{at.Position.Y:0.0},{at.Position.Z:0.0})");
                                    int acc = 0, k = 0;
                                    while (acc + at.Values[at.Index1 + k] <= dst.Id) { acc += at.Values[at.Index1 + k]; k += 2; }
                                    int hop = at.Values[at.Index1 + k + 1]; if (hop == at.Id) { path.Add("NOROUTE"); break; }
                                    at = all[hop];
                                }
                                Console.WriteLine($"      route from {st.Id} (d {(st.Position - p.Position).Length:0.0}): {string.Join(" > ", path)} > {dst.Id}");
                            }
                        }
                        for (float up = 0.5f; up <= 3f; up += 0.5f)
                            if (!MphRead.Formats.CollisionDetection.CheckBetweenPoints(a0 + OpenTK.Mathematics.Vector3.UnitY * up, new OpenTK.Mathematics.Vector3(a1.X, a0.Y + up, a1.Z), MphRead.Formats.TestFlags.Players, host.Scene, ref to))
                            { Console.WriteLine($"      clear toward node at +{up:0.0}"); break; }
                    }
                };
                for (int f = 1; f <= seconds * 60 && !host.Ended; f++)
                {
                    host.Step(input);
                    if (f == 1 && host.Scene.Room?.NodeData is { } nd)
                    {
                        // the node file's route table: pairs whose next hop is the node itself (no route), and routes
                        // that don't arrive within n hops (a loop)
                        IReadOnlyList<MphRead.Formats.NodeData3> list = nd.Data.SelectMany(s => s.SelectMany(l => l)).ToList();
                        int nn = list.Count, self = 0, loops = 0;
                        int Hop(MphRead.Formats.NodeData3 from, int dst)
                        {
                            int acc = 0, k = 0;
                            while (acc + from.Values[from.Index1 + k] <= dst) { acc += from.Values[from.Index1 + k]; k += 2; }
                            return from.Values[from.Index1 + k + 1];
                        }
                        for (int s = 0; s < nn; s++)
                        {
                            for (int d = 0; d < nn; d++)
                            {
                                if (s == d) continue;
                                int at = s, steps = 0;
                                while (at != d && steps <= nn) { int h = Hop(list[at], d); if (h == at) break; at = h; steps++; }
                                if (at == d) continue;
                                if (steps > nn) loops++; else self++;
                            }
                        }
                        Console.WriteLine($"  {inGame,-20} nav: {nn} nodes, {self} of {nn * (nn - 1)} pairs have no route ({100.0 * self / Math.Max(1, nn * (nn - 1)):0.0}%), {loops} loop");
                    }
                    for (int i = 0; i < seats; i++)
                    {
                        PlayerEntity p = PlayerEntity.Players[i];
                        if (!p.LoadFlags.TestFlag(LoadFlags.Active) || p.Health <= 0) alive[i] = false;
                        OpenTK.Mathematics.Vector2 v = (p.Position - last[i]).Xz;
                        if (v.LengthSquared > 0.01f * 0.01f)
                        {
                            if (OpenTK.Mathematics.Vector2.Dot(v, lastV[i]) < 0) flips[i]++;
                            lastV[i] = v;
                        }
                        last[i] = p.Position;
                        hist[i].Enqueue((p.Position, p.Flags1.TestFlag(PlayerFlags1.Grounded)));
                        if (hist[i].Count > 180) hist[i].Dequeue();
                        if (f % 6 == 0) { ring[i].Enqueue(p.Health > 0 ? p.Position : new OpenTK.Mathematics.Vector3(float.NaN)); if (ring[i].Count > 100) ring[i].Dequeue(); }
                        if (settings.Players[i].Bot && p.AiData != null && node40.GetValue(p.AiData) is MphRead.Formats.NodeData3 n)
                        {
                            targets[i].Add(n.Id);
                            if (trace > traced)
                            {
                                log[i].Add($"{f % Window,2} pos ({p.Position.X:0.0},{p.Position.Y:0.0},{p.Position.Z:0.0}) node {n.Id} "
                                    + $"({n.Position.X:0.0},{n.Position.Y:0.0},{n.Position.Z:0.0}) gnd {(p.Flags1.TestFlag(PlayerFlags1.Grounded) ? 1 : 0)} "
                                    + $"spd ({p.Speed.X:0.00},{p.Speed.Y:0.00},{p.Speed.Z:0.00}) hp {p.Health} "
                                    + $"alt {(p.IsAltForm ? 1 : 0)} dest {(node3C.GetValue(p.AiData) as MphRead.Formats.NodeData3)?.Id} ai {p.AiData.HostDebugState()}");
                            }
                        }
                    }
                    if (f % Window != 0) continue;
                    for (int i = 0; i < seats; i++)
                    {
                        PlayerEntity p = PlayerEntity.Players[i];
                        if (settings.Players[i].Bot && ring[i].Count == 100 && ring[i].All(q => !float.IsNaN(q.X)))
                        {
                            OpenTK.Mathematics.Vector3 c = p.Position;
                            if (ring[i].All(q => (q - c).Xz.LengthSquared < 3 * 3))
                            {
                                stays++;
                                string key = $"{settings.Players[i].Hunter} ({MathF.Round(c.X / 4) * 4},{MathF.Round(c.Y / 4) * 4},{MathF.Round(c.Z / 4) * 4})";
                                staySpots[key] = staySpots.GetValueOrDefault(key) + 1;
                                if (staySpots[key] == 1 && trace > 0)
                                {
                                    float y0 = ring[i].Min(q => q.Y), y1 = ring[i].Max(q => q.Y);
                                    var n40 = p.AiData != null ? node40.GetValue(p.AiData) as MphRead.Formats.NodeData3 : null;
                                    var n3c = p.AiData != null ? node3C.GetValue(p.AiData) as MphRead.Formats.NodeData3 : null;
                                    Console.WriteLine($"    stay seat {i} {settings.Players[i].Hunter} t={f / 60} s at ({c.X:0.0},{c.Y:0.0},{c.Z:0.0}) y {y0:0.0}..{y1:0.0} gnd {(p.Flags1.TestFlag(PlayerFlags1.Grounded) ? 1 : 0)} alt {(p.IsAltForm ? 1 : 0)} "
                                        + $"node {n40?.Id} ({n40?.Position.X:0.0},{n40?.Position.Y:0.0},{n40?.Position.Z:0.0}) dest {n3c?.Id} ({n3c?.Position.X:0.0},{n3c?.Position.Y:0.0},{n3c?.Position.Z:0.0}) ai {p.AiData?.HostDebugState()}");
                                }
                            }
                        }
                        if (settings.Players[i].Bot && alive[i] && f > Window)
                        {
                            windows++;
                            float net = (p.Position - start[i]).Xz.Length;
                            if (flips[i] >= 6 && net < 2)
                            {
                                jitter++;
                                // at her destination (MphRead's own waiting about: a pickup, a corner) vs on the way
                                if (p.AiData != null && node40.GetValue(p.AiData) is MphRead.Formats.NodeData3 n40 && n40 == node3C.GetValue(p.AiData)
                                    && (n40.Position - p.Position).Xz.LengthSquared < 2 * 2) atDest++;
                                OpenTK.Mathematics.Vector3 q = p.Position;
                                var key = ((int)MathF.Round(q.X / 4) * 4, (int)MathF.Round(q.Y / 4) * 4, (int)MathF.Round(q.Z / 4) * 4);
                                spots[key] = spots.GetValueOrDefault(key) + 1;
                                if (traced < trace)
                                {
                                    traced++;
                                    Console.WriteLine($"    jitter seat {i} t={f / 60} s: {flips[i]} reversals, net {net:0.00}, nodes [{string.Join(",", targets[i])}]");
                                    // the collision she touches (radius 1 around her position) and straight down
                                    var touch = new MphRead.Formats.CollisionResult[8];
                                    int nt = MphRead.Formats.CollisionDetection.CheckInRadius(p.Position, 1f, 8, false, MphRead.Formats.TestFlags.Players, host.Scene, touch);
                                    for (int t = 0; t < nt; t++) Console.WriteLine($"      touch n ({touch[t].Plane.X:0.00},{touch[t].Plane.Y:0.00},{touch[t].Plane.Z:0.00}) d {touch[t].Plane.W:0.00} f14 {touch[t].Field14:0.00} slip {touch[t].Slipperiness} at ({touch[t].Position.X:0.0},{touch[t].Position.Y:0.0},{touch[t].Position.Z:0.0})");
                                    var down = new MphRead.Formats.CollisionResult();
                                    if (MphRead.Formats.CollisionDetection.CheckBetweenPoints(p.Position, p.Position - OpenTK.Mathematics.Vector3.UnitY * 6, MphRead.Formats.TestFlags.Players, host.Scene, ref down))
                                        Console.WriteLine($"      below: n ({down.Plane.X:0.00},{down.Plane.Y:0.00},{down.Plane.Z:0.00}) at y {down.Position.Y:0.00}");
                                    foreach (string line in log[i].Where((_, k) => k % (Environment.GetEnvironmentVariable("JITTER_ALL") == "1" ? 1 : 3) == 0)) Console.WriteLine("      " + line);
                                }
                            }
                        }
                        start[i] = p.Position; flips[i] = 0; alive[i] = true; targets[i].Clear(); log[i].Clear();
                    }
                }
                int kills = Enumerable.Range(0, seats).Sum(i => GameState.Kills[i]);
                // falls (T7: gap links must not walk bots into the void): deaths with no attacker or by themselves
                int falls = Enumerable.Range(0, seats).Sum(i => GameState.Suicides[i]);
                Console.WriteLine($"  {inGame,-20} unstick {(PlayerEntity.HostBotUnstick ? "on" : "off")}: unmorphs {PlayerEntity.HostUnstickCounts[0]}, climbs {PlayerEntity.HostUnstickCounts[1]}, give-ups {PlayerEntity.HostUnstickCounts[2]}, drifts {PlayerEntity.HostUnstickCounts[3]}, link jumps {PlayerEntity.HostUnstickCounts[4]}, items skipped {PlayerEntity.HostUnstickCounts[5]}");
                Array.Clear(PlayerEntity.HostUnstickCounts);
                if (PlayerEntity.HostUnstickGiveUp != null)
                {
                    Console.WriteLine($"  {inGame,-20} give-ups by node: " + string.Join(", ", giveNodes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}")));
                    PlayerEntity.HostUnstickGiveUp = null;
                }
                Console.WriteLine($"  {inGame,-20} {bots} bots, {host.Frame / 60} s: jitter {jitter} of {windows} bot seconds ({(windows > 0 ? 100.0 * jitter / windows : 0):0}%; on the way {jitter - atDest} = {(windows > 0 ? 100.0 * (jitter - atDest) / windows : 0):0}%), kills {kills}, falls {falls}"
                    + (spots.Count > 0 ? "; spots " + string.Join(", ", spots.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} x{kv.Value}")) : ""));
                Console.WriteLine($"  {inGame,-20} stays (10 s within 3 units) {stays} bot seconds ({(windows > 0 ? 100.0 * stays / windows : 0):0.0}%)"
                    + (staySpots.Count > 0 ? ": " + string.Join(", ", staySpots.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} x{kv.Value}")) : ""));
            }
        }

        // owner (Odin 20:37): a ~13 ms .NET gen0 GC in the sim every 4-7 s (sim alloc ~17 MB/s on Sidehopper). The device's
        // match per arena (Combat Hall as the control; draw items collected; the human seat stands still): MB allocated per
        // simulated second, gen0 GCs, and the types behind it (the runtime's allocation ticks, one per ~100 KB)
        static void Alloc(string? only, int seconds, int bots, bool draw)
        {
            EchoesArena.Register(Paths.FileSystem);
            var rooms = EchoesArena.All.Where(d => only == null || EchoesArena.Find(only) == d)
                .Select(d => (d.Room, d.InGameName)).Prepend(("MP3 PROVING GROUND", "Combat Hall")).ToList();
            using var ticks = new AllocTicks();
            foreach ((string room, string inGame) in rooms)
            {
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, room, Hunter.Samus, bots, 2);
                settings.TimeLimitSeconds = seconds + 60;
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings, collectDrawItems: draw, viewWidth: 1920, viewHeight: 1080);
                var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                for (int f = 0; f < 120 && !host.Ended; f++) host.Step(input);
                System.Threading.Thread.Sleep(1500); // the listener's events arrive late
                ticks.Clear();
                long a0 = GC.GetTotalAllocatedBytes(precise: true);
                int g0 = GC.CollectionCount(0), frames = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (; frames < seconds * 60 && !host.Ended; frames++) host.Step(input);
                if (draw && Environment.GetEnvironmentVariable("ALLOC_SPLIT") == "1")
                {
                    // the draw collection's parts run once more after each step, each measured on its own
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                    var parts = new (string Name, Action Run)[]
                    {
                        ("GetDrawItems", () => typeof(Scene).GetMethod("GetDrawItems", flags, Type.EmptyTypes)!.Invoke(host.Scene, null)),
                        ("RecordHud", () => typeof(Scene).GetMethod("RecordHud", flags)!.Invoke(host.Scene, null)),
                        ("DrawHudModels", () => PlayerEntity.Main.DrawHudModels()),
                        ("DrawHudObjects", () => PlayerEntity.Main.DrawHudObjects()),
                    };
                    var sums = new long[parts.Length];
                    for (int f = 0; f < 600 && !host.Ended; f++)
                    {
                        host.Step(input);
                        for (int i = 0; i < parts.Length; i++)
                        {
                            long b0 = GC.GetAllocatedBytesForCurrentThread();
                            parts[i].Run();
                            sums[i] += GC.GetAllocatedBytesForCurrentThread() - b0;
                        }
                    }
                    for (int i = 0; i < parts.Length; i++) Console.WriteLine($"      split {parts[i].Name,-15} {sums[i] / 600 / 1024.0:0.0} KB/frame");
                }
                double secs = frames / 60.0, ms = sw.Elapsed.TotalMilliseconds;
                long bytes = GC.GetTotalAllocatedBytes(precise: true) - a0;
                int gcs = GC.CollectionCount(0) - g0;
                System.Threading.Thread.Sleep(2000);
                Console.WriteLine($"  {inGame,-20} {secs:0} s: {bytes / 1048576.0 / secs:0.00} MB/s ({bytes / frames / 1024.0:0.0} KB/frame), gen0 GCs {gcs}, "
                    + $"sim {ms / frames:0.00} ms/frame, kills {Enumerable.Range(0, settings.Players.Count).Sum(i => GameState.Kills[i])}");
                foreach ((string type, long amount) in ticks.Top(10))
                {
                    Console.WriteLine($"      {amount / 1048576.0 / secs,6:0.00} MB/s  {type}");
                }
            }
        }

        // the runtime's GC allocation ticks summed by type
        sealed class AllocTicks : System.Diagnostics.Tracing.EventListener
        {
            readonly Dictionary<string, long> _by = new();

            protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
            {
                if (source.Name == "Microsoft-Windows-DotNETRuntime")
                {
                    EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
                }
            }

            protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
            {
                if (e.EventName?.StartsWith("GCAllocationTick") != true || e.PayloadNames == null || e.Payload == null) return;
                int t = e.PayloadNames.IndexOf("TypeName"), a = e.PayloadNames.IndexOf("AllocationAmount64");
                if (a < 0) a = e.PayloadNames.IndexOf("AllocationAmount");
                if (t < 0 || a < 0) return;
                string type = e.Payload[t] as string ?? "?";
                long amount = Convert.ToInt64(e.Payload[a]);
                lock (_by) _by[type] = _by.GetValueOrDefault(type) + amount;
            }

            public void Clear() { lock (_by) _by.Clear(); }

            public List<(string, long)> Top(int n)
            {
                lock (_by) return _by.OrderByDescending(kv => kv.Value).Take(n).Select(kv => (kv.Key, kv.Value)).ToList();
            }
        }

        // the arenas' jump pads fired the way the device fires them: pad data read back from the arena's entity file (no
        // reflection; the ControlLockTime the device gets), the Samus seat walking onto the pad from 3 units back, then
        // either letting go of the stick or holding it toward the target for the whole flight
        static void PadTest(string? only)
        {
            EchoesArena.Register(Paths.FileSystem);
            foreach (EchoesArena.Def d in EchoesArena.All)
            {
                if (only != null && EchoesArena.Find(only) != d) continue;
                string json = Path.Combine(Paths.FileSystem, "_archives", d.Archive, "arena.json");
                if (!File.Exists(json)) continue;
                EchoesArena.Info info = System.Text.Json.JsonSerializer.Deserialize<EchoesArena.Info>(File.ReadAllText(json))!;
                List<ArenaCannons.Cannon> cannons = info.Cannons.Where(c => !c.Dropped).ToList();
                if (cannons.Count == 0) continue;
                RoomMetadata meta = Metadata.GetRoomByName(d.Room).Item1!;
                Dictionary<short, JumpPadEntityEditor> file = Read.GetEntities(meta.EntityPath!, -1, firstHunt: false)
                    .Where(e => e.Type == EntityType.JumpPad)
                    .ToDictionary(e => e.EntityId, e => new JumpPadEntityEditor(e, ((Entity<JumpPadEntityData>)e).Data));
                Console.WriteLine($"{d.InGameName}:");
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, 1, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                if (Environment.GetEnvironmentVariable("PAD_FALLDMG") == "mph") settings.EchoesPadNoFall = false;   // T6 N4: MPH's 10-frame rule
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                for (int f = 0; f < 600 && !(PlayerEntity.Main.Health > 0 && PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Active)); f++) host.Step(idle);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                foreach (ArenaCannons.Cannon c in cannons)
                {
                    if (!file.TryGetValue(c.EntityId, out JumpPadEntityEditor? ed))
                    {
                        Console.WriteLine($"  cannon '{c.Name}': pad {c.EntityId} NOT in the entity file");
                        continue;
                    }
                    OpenTK.Mathematics.Vector3 fileLaunch = ed.BeamVector.Normalized() * ed.Speed;
                    Console.WriteLine($"  cannon '{c.Name}' pad {c.EntityId}: file launch ({fileLaunch.X:0.00}, {fileLaunch.Y:0.00}, {fileLaunch.Z:0.00}) "
                        + $"{ed.Speed:0.00}/f lock {ed.ControlLockTime} | tuned ({c.Launch[0]:0.00}, {c.Launch[1]:0.00}, {c.Launch[2]:0.00}) lock {c.LockFrames}, "
                        + $"pad ({ed.Position.X:0.0}, {ed.Position.Y:0.0}, {ed.Position.Z:0.0}) target ({c.Target[0]:0.0}, {c.Target[1]:0.0}, {c.Target[2]:0.0})");
                    if (Environment.GetEnvironmentVariable("PADTEST_STEER") == "1")
                    {
                        // owner 10-08 00:1x ("any directional input" falls short): taps, weaving and turning with the aim off the line
                        var mv = (float x, float y) => new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(x, y) };
                        var steerCases = new (string Name, float Aim, Func<int, MphRecomp.Campaign.CampaignInput> In)[]
                        {
                            ("let go (reference)          ", 0, f => mv(0, 0)),
                            ("tap left 0.25 s at 1 s      ", 0, f => f >= 60 && f < 75 ? mv(-1, 0) : mv(0, 0)),
                            ("tap right 0.5 s at 0.5 s    ", 0, f => f >= 30 && f < 60 ? mv(1, 0) : mv(0, 0)),
                            ("tap back 0.1 s at 0.5 s     ", 0, f => f >= 30 && f < 36 ? mv(0, -1) : mv(0, 0)),
                            ("tap back 0.25 s at 0.5 s    ", 0, f => f >= 30 && f < 45 ? mv(0, -1) : mv(0, 0)),
                            ("tap back 0.25 s at 1.5 s    ", 0, f => f >= 90 && f < 105 ? mv(0, -1) : mv(0, 0)),
                            ("back held all flight        ", 0, f => mv(0, -1)),
                            ("weave +-0.8, no forward     ", 0, f => mv(f / 20 % 2 == 1 ? -0.8f : 0.8f, 0)),
                            ("weave + fwd, aim +20        ", 20, f => mv(f / 20 % 2 == 1 ? -0.8f : 0.8f, 1)),
                            ("weave + fwd, aim -20        ", -20, f => mv(f / 20 % 2 == 1 ? -0.8f : 0.8f, 1)),
                            ("weave + fwd, aim +45        ", 45, f => mv(f / 20 % 2 == 1 ? -0.8f : 0.8f, 1)),
                            ("weave no fwd, aim +45       ", 45, f => mv(f / 20 % 2 == 1 ? -0.8f : 0.8f, 0)),
                            ("tap left 0.25 s, aim +45    ", 45, f => f >= 60 && f < 75 ? mv(-1, 0) : mv(0, 0)),
                            ("tap left 0.25 s, aim -45    ", -45, f => f >= 60 && f < 75 ? mv(-1, 0) : mv(0, 0)),
                            ("weave + fwd, turning 2 d/f  ", 0, f => new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None,
                                Move = new System.Numerics.Vector2(f / 20 % 2 == 1 ? -0.8f : 0.8f, 1), AimDelta = new System.Numerics.Vector2(f / 45 % 2 == 1 ? -2 : 2, 0) }),
                        };
                        foreach (var sc in steerCases)
                        {
                            Console.WriteLine($"    {sc.Name}: {WalkOn(host, ed.Position, c.TargetV, info.KillHeight, true, 0, sc.Aim, fly: sc.In)}");
                        }
                        continue;
                    }
                    foreach (bool hold in new[] { false, true })
                    {
                        Console.WriteLine($"    {(hold ? "stick held toward the target" : "stick let go on launch      ")}: {WalkOn(host, ed.Position, c.TargetV, info.KillHeight, hold)}");
                    }
                    // walked onto from other sides (a player coming from elsewhere), the stick kept pushed the way they walked:
                    // MphRead (like vanilla) cuts the pad's push for good when the stick works against it in the air
                    foreach (float deg in new[] { 90f, 180f })
                    {
                        foreach (bool hold in new[] { false, true })
                        {
                            Console.WriteLine($"    walked on {deg,3:0} deg off, {(hold ? "stick kept pushed" : "stick let go     ")}: {WalkOn(host, ed.Position, c.TargetV, info.KillHeight, hold, deg)}");
                        }
                    }
                    // the morph ball rolled on toward the target, stick let go after the launch (same arc as the biped: ArenaCannons.SameArcBit)
                    Console.WriteLine($"    morph ball rolled on, let go: {WalkOn(host, ed.Position, c.TargetV, info.KillHeight, false, alt: true)}");
                    Console.WriteLine($"    landing platform: {Platform(host, c.TargetV, ed.Position)}");
                    // walked on toward the target, then forward held with the aim turned a little off the pad's line
                    foreach (float aim in new[] { -20f, -10f, -5f, 5f, 10f, 20f })
                    {
                        Console.WriteLine($"    forward held, aim {aim,3:+0;-0} deg off the line: {WalkOn(host, ed.Position, c.TargetV, info.KillHeight, true, 0, aim)}");
                    }
                    // the owner's way (Spires replay 10-07 23:44): forward held the whole flight, the stick weaving left/right
                    // (+-0.8, every 1/3 s), or half the stick held to one side the whole flight
                    Console.WriteLine($"    forward held, weaving +-0.8     : {WalkOn(host, ed.Position, c.TargetV, info.KillHeight, true, side: 0.8f, weave: 20)}");
                    foreach (float side in new[] { -0.5f, 0.5f })
                    {
                        Console.WriteLine($"    forward held, stick {side:+0.0;-0.0} to the side: {WalkOn(host, ed.Position, c.TargetV, info.KillHeight, true, side: side)}");
                    }
                }
            }
        }

        // MphRead side of tools/accuracy/routes/probe_jumppad.lua (PAD_AT=deck): Celestial Gateway's pad 14 moved under
        // Samus on the gunship (volume base y 6.95), launch (-0.3, 0.5196, 0), the given lock (30 Hz ticks), a d-pad key
        // held from the tick after the launch; velocity per 30 Hz tick next to the vanilla trace (vanilla=<csv>).
        // -echoesarena padvanilla lock=12 key=Right vanilla=<mph-emu/out/jp_d_L12_Right.csv>
        static void PadVanilla(string[] args)
        {
            ushort lock30 = ushort.Parse(Arg(args, "lock") ?? "0");
            string key = Arg(args, "key") ?? "none";
            string? csv = Arg(args, "vanilla");
            var launch = new OpenTK.Mathematics.Vector3(-1, 1.732f, 0).Normalized() * 0.6f;
            var move = key switch
            {
                "Up" => new System.Numerics.Vector2(0, 1), "Down" => new System.Numerics.Vector2(0, -1),
                "Right" => new System.Numerics.Vector2(1, 0), "Left" => new System.Numerics.Vector2(-1, 0), _ => System.Numerics.Vector2.Zero,
            };
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            System.Reflection.FieldInfo dataField = typeof(JumpPadEntity).GetField("_data", flags)!;
            System.Reflection.FieldInfo beamField = typeof(JumpPadEntity).GetField("_beamVector", flags)!;
            System.Reflection.FieldInfo lockField = typeof(PlayerEntity).GetField("_jumpPadControlLock", flags)!;
            using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.Start("UNIT2_LAND", collectDrawItems: true);
            for (int i = 0; i < 1000 && !host.Ended; i++) // as AccuracyTest.StartLanding
            {
                host.Step(new MphRecomp.Campaign.CampaignInput
                {
                    DialogButton = !host.DialogPaused || i % 10 != 0 ? HostDialogButton.None
                        : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance
                });
            }
            var spot = new OpenTK.Mathematics.Vector3(-2.96167f, 7.226807f, 0.119385f); // vanilla, landing.State frame 8
            host.PlacePlayer(spot + new OpenTK.Mathematics.Vector3(0, 0.25f, 0), new OpenTK.Mathematics.Vector3(-1, 0, 0));
            for (int i = 0; i < 120; i++) host.Step(default);
            PlayerEntity me = host.Player;
            if (!host.Scene.TryGetEntity(14, out EntityBase? e) || e is not JumpPadEntity pad)
            {
                Console.WriteLine("pad 14 not found");
                return;
            }
            JumpPadEntityData orig = (JumpPadEntityData)dataField.GetValue(pad)!;
            dataField.SetValue(pad, new JumpPadEntityData(orig.Header, orig.ParentId, orig.Volume, orig.BeamVector, orig.Speed, lock30, 0, orig.Active,
                orig.ModelId, orig.BeamType, orig.TriggerFlags));
            beamField.SetValue(pad, launch);
            OpenTK.Mathematics.Vector3 start = me.Position;
            pad.Position = new OpenTK.Mathematics.Vector3(spot.X, 6.95f, spot.Z);
            var rows = new List<(OpenTK.Mathematics.Vector3 P, OpenTK.Mathematics.Vector3 V, int Lock)>();
            int fired = -1;
            for (int f = 0; f < 140; f++)
            {
                host.Step(new MphRecomp.Campaign.CampaignInput { Move = fired >= 0 && f >= fired + 2 ? move : System.Numerics.Vector2.Zero });
                if (fired < 0 && me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)) fired = f;
                if (fired >= 0 && (f - fired) % 2 == 1) rows.Add((me.Position, me.Speed, (ushort)lockField.GetValue(me)!));
            }
            Console.WriteLine($"MphRead: standing at ({start.X:0.000}, {start.Y:0.000}, {start.Z:0.000}) (vanilla {spot.X:0.000}, {spot.Y:0.000}, {spot.Z:0.000}), fired at step {fired}, lock {lock30}, key {key}");
            var van = new List<(OpenTK.Mathematics.Vector3 P, OpenTK.Mathematics.Vector3 V)>();
            if (csv != null && File.Exists(csv))
            {
                string[] lines = File.ReadAllLines(csv);
                string[] head = lines[0].Split(',');
                int I(string n) => Array.IndexOf(head, n);
                float F(string[] c, string n) => float.Parse(c[I(n)], System.Globalization.CultureInfo.InvariantCulture);
                foreach (string[] c in lines.Skip(1).Select(l => l.Split(',')).Where(c => int.Parse(c[0]) >= 10 && int.Parse(c[0]) % 2 == 0))
                {
                    van.Add((new OpenTK.Mathematics.Vector3(F(c, "px"), F(c, "py"), F(c, "pz")), new OpenTK.Mathematics.Vector3(F(c, "vx"), F(c, "vy"), F(c, "vz"))));
                }
            }
            Console.WriteLine("  tick  MphRead v (x, y, z) |h| lock(frames)       vanilla v (x, y, z) |h|          dv");
            for (int t = 0; t < Math.Min(rows.Count, 30); t++)
            {
                var (p, v, l) = rows[t];
                string line = $"  {t,4}  ({v.X,7:0.0000},{v.Y,7:0.0000},{v.Z,7:0.0000}) {v.Xz.Length:0.0000} {l,3}";
                if (t < van.Count)
                {
                    OpenTK.Mathematics.Vector3 w = van[t].V;
                    line += $"   ({w.X,7:0.0000},{w.Y,7:0.0000},{w.Z,7:0.0000}) {w.Xz.Length:0.0000}   {(v - w).Length:0.0000}";
                }
                Console.WriteLine(line);
            }
        }

        // R7 experiment: each pad solved with MPH's own control lock (straight climb, no steering, then a fall you steer)
        // onto the roomiest floor near its target, and every plan scored by how much stick/aim it takes (forward held with
        // the aim N deg off the pad's line, stick let go, the morph ball)
        static void PadLock(string? only)
        {
            EchoesArena.Register(Paths.FileSystem);
            System.Reflection.FieldInfo dataField = typeof(JumpPadEntity).GetField("_data", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            System.Reflection.FieldInfo beamField = typeof(JumpPadEntity).GetField("_beamVector", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            foreach (EchoesArena.Def d in EchoesArena.All)
            {
                if (only != null && EchoesArena.Find(only) != d) continue;
                string json = Path.Combine(Paths.FileSystem, "_archives", d.Archive, "arena.json");
                if (!File.Exists(json)) continue;
                EchoesArena.Info info = System.Text.Json.JsonSerializer.Deserialize<EchoesArena.Info>(File.ReadAllText(json))!;
                List<ArenaCannons.Cannon> cannons = info.Cannons.Where(c => !c.Dropped).ToList();
                if (cannons.Count == 0) continue;
                Console.WriteLine($"{d.InGameName}:");
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, 1, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                if (Environment.GetEnvironmentVariable("PAD_FALLDMG") == "mph") settings.EchoesPadNoFall = false;   // T6 N4: MPH's 10-frame rule
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                for (int f = 0; f < 600 && !(PlayerEntity.Main.Health > 0 && PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Active)); f++) host.Step(idle);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                float[] aims = { 0, 5, -5, 10, -10, 20, -20, 45, -45, 90, -90, 180 };
                foreach (ArenaCannons.Cannon c in cannons)
                {
                    if (!host.Scene.TryGetEntity(c.EntityId, out EntityBase? e) || e is not JumpPadEntity pad) continue;
                    JumpPadEntityData orig = (JumpPadEntityData)dataField.GetValue(pad)!;
                    OpenTK.Mathematics.Vector3 padAt = c.PadV, t0 = c.TargetV;
                    // the roomiest floor near the target (same height)
                    (float Room, OpenTK.Mathematics.Vector3 At) best = (ReachMin(host, t0, 25), t0);
                    for (float dx = -20; dx <= 20; dx += 1)
                    {
                        for (float dz = -20; dz <= 20; dz += 1)
                        {
                            OpenTK.Mathematics.Vector3 q = t0 + new OpenTK.Mathematics.Vector3(dx, 0, dz);
                            float m = ReachMin(host, q, 25);
                            if (m > best.Room) best = (m, q);
                        }
                    }
                    OpenTK.Mathematics.Vector3 t = best.At;
                    float room = best.Room;
                    Console.WriteLine($"  cannon '{c.Name}': target ({t0.X:0.0}, {t0.Y:0.0}, {t0.Z:0.0}) room {ReachMin(host, t0, 25):0.0} -> middle ({t.X:0.0}, {t.Y:0.0}, {t.Z:0.0}) room {room:0.0}");
                    var lineH = new OpenTK.Mathematics.Vector3(t.X - padAt.X, 0, t.Z - padAt.Z).Normalized();
                    OpenTK.Mathematics.Vector3 from = padAt - lineH * (ArenaCannons.PadRadius - 0.15f);

                    (OpenTK.Mathematics.Vector3 Land, bool Dead) Fly(OpenTK.Mathematics.Vector3 v, ushort lock30, bool alt, bool hold, float aimDeg)
                    {
                        PlayerEntity me = PlayerEntity.Main;
                        var press = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Buttons = MphRecomp.Campaign.CampaignButtons.Shoot | MphRecomp.Campaign.CampaignButtons.Jump };
                        var morph = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Buttons = MphRecomp.Campaign.CampaignButtons.Morph };
                        var walk = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(0, 1) };
                        for (int f = 0; f < 900 && me.Health == 0; f++) host.Step(f % 2 == 0 ? press : idle);
                        dataField.SetValue(pad, new JumpPadEntityData(orig.Header, orig.ParentId, orig.Volume, orig.BeamVector, orig.Speed, lock30, 0, orig.Active,
                            orig.ModelId, orig.BeamType, TriggerFlags.PlayerBiped | TriggerFlags.PlayerAlt));
                        beamField.SetValue(pad, OpenTK.Mathematics.Vector3.Zero);
                        pad.Position = padAt;
                        for (int f = 0; f < 40 && me.IsAltForm != alt; f++) host.Step(f % 2 == 0 ? morph : idle);
                        var face = OpenTK.Mathematics.Matrix3.CreateRotationY(OpenTK.Mathematics.MathHelper.DegreesToRadians(aimDeg)) * lineH;
                        me.Reposition(from + OpenTK.Mathematics.Vector3.UnitY * 0.2f - me.Position, me.NodeRef);
                        me.Reposition(me.Position, face, me.NodeRef);
                        me.Speed = OpenTK.Mathematics.Vector3.Zero;
                        for (int f = 0; f < 20; f++) host.Step(idle);
                        for (int f = 0; f < 40 && me.IsAltForm != alt; f++) host.Step(f % 2 == 0 ? morph : idle);
                        me.Reposition(me.Position, face, me.NodeRef);
                        beamField.SetValue(pad, v);
                        OpenTK.Mathematics.Vector3 start = me.Position;
                        bool left = false;
                        for (int frames = 0; frames < 1500; frames++)
                        {
                            host.Step(hold ? walk : idle);
                            if (me.Health == 0 || me.Position.Y < info.KillHeight) break;
                            if (!left)
                            {
                                left = (me.Position - start).Length > 0.3f;
                                continue;
                            }
                            if (frames > 8 && !me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad) && me.Flags1.TestFlag(PlayerFlags1.Standing) && MathF.Abs(me.Speed.Y) < 0.05f) break;
                        }
                        OpenTK.Mathematics.Vector3 land = me.Position;
                        bool dead = me.Health == 0 || me.Position.Y < info.KillHeight;
                        if (!left) Console.WriteLine($"      (never launched: at ({start.X:0.0}, {start.Y:0.0}, {start.Z:0.0}), pad ({padAt.X:0.0}, {padAt.Y:0.0}, {padAt.Z:0.0}), alt {me.IsAltForm}, hp {me.Health})");
                        beamField.SetValue(pad, OpenTK.Mathematics.Vector3.Zero);
                        for (int f = 0; f < 40 && me.IsAltForm; f++) host.Step(f % 2 == 0 ? morph : idle);
                        return (land, dead);
                    }
                    bool On((OpenTK.Mathematics.Vector3 Land, bool Dead) r, float up) => !r.Dead && MathF.Abs(r.Land.Y - up - t.Y) < 1 && (r.Land.Xz - t.Xz).Length <= room + 0.5f;

                    string Score(OpenTK.Mathematics.Vector3 v, ushort lock30)
                    {
                        var parts = new List<string>();
                        int ok = 0;
                        foreach (float a in aims)
                        {
                            var r = Fly(v, lock30, false, true, a);
                            bool on = On(r, 0.5f);
                            if (on) ok++;
                            parts.Add($"{a:+0;-0;0}:{(on ? "ok" : r.Dead ? "fell" : $"off{(r.Land.Xz - t.Xz).Length:0}")}");
                        }
                        var go = Fly(v, lock30, false, false, 0);
                        var ball = Fly(v, lock30, true, false, 0);
                        return $"held {ok}/{aims.Length} [{string.Join(" ", parts)}], let go {(On(go, 0.5f) ? "ok" : "MISS")} {(go.Land.Xz - t.Xz).Length:0.0}, "
                            + $"ball {(!ball.Dead && MathF.Abs(ball.Land.Y - t.Y) < 1.5f && (ball.Land.Xz - t.Xz).Length <= room + 0.5f ? "ok" : ball.Dead ? "fell" : "off")} {(ball.Land.Xz - t.Xz).Length:0.0}";
                    }

                    // today's launch (lock 0, onto the old target), scored against the middle
                    Console.WriteLine($"    today      lock  0 v {c.LaunchV.Length:0.00}: {Score(c.LaunchV, 0)}");
                    // locked plans: a straight climb at a point h over the middle, the lock about that long, then the fall;
                    // the aim point corrected until the let-go landing is on the middle
                    foreach (float h in new[] { 6f, 10f, 15f })
                    {
                        foreach (float s in new[] { 2.2f, 3f, 4f })
                        {
                            OpenTK.Mathematics.Vector3 p = t + OpenTK.Mathematics.Vector3.UnitY * (h + 0.5f);
                            OpenTK.Mathematics.Vector3 v = default;
                            ushort lk = 0;
                            float miss = float.MaxValue;
                            for (int it = 0; it < 16; it++)
                            {
                                OpenTK.Mathematics.Vector3 dv = p - (from + OpenTK.Mathematics.Vector3.UnitY * 0.5f);
                                v = dv.Normalized() * s;
                                lk = (ushort)Math.Max(1, (int)(dv.Length / s / 2));
                                var r = Fly(v, lk, false, false, 0);
                                OpenTK.Mathematics.Vector3 err = r.Land - (t + OpenTK.Mathematics.Vector3.UnitY * 0.5f);
                                miss = r.Dead ? float.MaxValue : err.Length;
                                if (miss < 0.6f) break;
                                p -= new OpenTK.Mathematics.Vector3(err.X, 0, err.Z) * 0.6f;
                            }
                            if (miss > room * 0.5f)
                            {
                                Console.WriteLine($"    h {h,2:0} s {s:0.0}: no landing on the middle (miss {(miss == float.MaxValue ? "fell" : miss.ToString("0.0"))})");
                                continue;
                            }
                            Console.WriteLine($"    h {h,2:0} s {s:0.0} lock {lk,2} ({lk / 30f:0.00} s): {Score(v, lk)}");
                        }
                    }
                    dataField.SetValue(pad, orig);
                    beamField.SetValue(pad, OpenTK.Mathematics.Vector3.Zero);
                }
            }
        }

        // owner 10-08 ~01:05 ("make these long jumps more forgiving on left and right input so people can do left and right
        // a bit more and still make it"): each long Echoes jump (> 60 across) re-solved onto the same target as flatter and
        // higher arcs, with and without a short MPH lock at the start (no stick, no gravity: MPH's own rule for its long
        // pads), each plan landing with the stick let go; then walked on like a player and scored on left/right input
        // (taps, a side held, weaving, forward with the aim off the line), back taps and the ball for reference. MPH's own
        // steering: Echoes pads on the owner's left/right rule (default), env PAD_MPHSTEER=1 = MPH's own
        static void PadForgive(string? only)
        {
            EchoesArena.Register(Paths.FileSystem);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            System.Reflection.FieldInfo dataField = typeof(JumpPadEntity).GetField("_data", flags)!;
            System.Reflection.FieldInfo beamField = typeof(JumpPadEntity).GetField("_beamVector", flags)!;
            System.Reflection.FieldInfo lockField = typeof(JumpPadEntityData).GetField(nameof(JumpPadEntityData.ControlLockTime))!;
            var mv = (float x, float y) => new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(x, y) };
            Func<int, MphRecomp.Campaign.CampaignInput> Tap(float x, float y, float at, float len) =>
                f => f >= at * 60 && f < (at + len) * 60 ? mv(x, y) : mv(0, 0);
            // the stick is digital on the device (CampaignHost: past 0.35 = the full direction), so no half-stick cases
            var lr = new (string Name, float Aim, Func<int, MphRecomp.Campaign.CampaignInput> In)[]
            {
                ("tap L 0.25 s at 0.5 s", 0, Tap(-1, 0, 0.5f, 0.25f)), ("tap R 0.25 s at 0.5 s", 0, Tap(1, 0, 0.5f, 0.25f)),
                ("tap L 0.25 s at 1 s", 0, Tap(-1, 0, 1, 0.25f)), ("tap R 0.25 s at 1 s", 0, Tap(1, 0, 1, 0.25f)),
                ("tap L 0.25 s at 2 s", 0, Tap(-1, 0, 2, 0.25f)), ("tap R 0.25 s at 2 s", 0, Tap(1, 0, 2, 0.25f)),
                ("tap L 0.5 s at 1 s", 0, Tap(-1, 0, 1, 0.5f)), ("tap R 0.5 s at 1 s", 0, Tap(1, 0, 1, 0.5f)),
                ("L held all flight", 0, f => mv(-1, 0)), ("R held all flight", 0, f => mv(1, 0)),
                ("weave L/R every 1/3 s", 0, f => mv(f / 20 % 2 == 1 ? -1 : 1, 0)),
                ("weave L/R + forward", 0, f => mv(f / 20 % 2 == 1 ? -1 : 1, 1)),
                ("forward, aim -10", -10, f => mv(0, 1)), ("forward, aim +10", 10, f => mv(0, 1)),
                ("forward, aim -20", -20, f => mv(0, 1)), ("forward, aim +20", 20, f => mv(0, 1)),
            };
            var back = new (string Name, Func<int, MphRecomp.Campaign.CampaignInput> In)[]
            {
                ("back 0.1 s at 0.5 s", Tap(0, -1, 0.5f, 0.1f)), ("back 0.25 s at 0.5 s", Tap(0, -1, 0.5f, 0.25f)),
            };
            Console.WriteLine($"steering: {(JumpPadEntity.HostSameArcSteer ? "Echoes left/right rule (default)" : "MPH's own (PAD_MPHSTEER=1)")}");
            Console.WriteLine("left/right cases, in order (+ = lands on the target's platform, x = falls, o = lands on another level):");
            for (int i = 0; i < lr.Length; i++) Console.WriteLine($"  {i + 1,2} {lr[i].Name}");
            foreach (EchoesArena.Def d in EchoesArena.All)
            {
                if (only != null && EchoesArena.Find(only) != d) continue;
                string json = Path.Combine(Paths.FileSystem, "_archives", d.Archive, "arena.json");
                if (!File.Exists(json)) continue;
                EchoesArena.Info info = System.Text.Json.JsonSerializer.Deserialize<EchoesArena.Info>(File.ReadAllText(json))!;
                List<ArenaCannons.Cannon> cannons = info.Cannons.Where(c => !c.Dropped && (c.TargetV.Xz - c.PadV.Xz).Length > 60).ToList();
                if (cannons.Count == 0) continue;
                Console.WriteLine($"{d.InGameName}:");
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, 1, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                if (Environment.GetEnvironmentVariable("PAD_FALLDMG") == "mph") settings.EchoesPadNoFall = false;   // T6 N4: MPH's 10-frame rule
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                for (int f = 0; f < 600 && !(PlayerEntity.Main.Health > 0 && PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Active)); f++) host.Step(idle);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                foreach (ArenaCannons.Cannon c in cannons)
                {
                    if (!host.Scene.TryGetEntity(c.EntityId, out EntityBase? e) || e is not JumpPadEntity pad) continue;
                    object orig = dataField.GetValue(pad)!;
                    var origBeam = (OpenTK.Mathematics.Vector3)beamField.GetValue(pad)!;
                    OpenTK.Mathematics.Vector3 padAt = pad.Position, t = c.TargetV, stand = t + OpenTK.Mathematics.Vector3.UnitY * 0.5f;
                    void Set(OpenTK.Mathematics.Vector3 v, ushort lk)
                    {
                        object data = dataField.GetValue(pad)!;
                        lockField.SetValue(data, lk);
                        dataField.SetValue(pad, data);
                        beamField.SetValue(pad, v);
                    }
                    bool On() => LastEnd == "landed" && MathF.Abs(LastLand.Y - stand.Y) < 1.2f && (LastLand.Xz - t.Xz).Length < 35;
                    char Mark() => On() ? '+' : LastEnd == "landed" ? 'o' : 'x';
                    // gravity per frame from today's let-go flight (lock 0): rise = vy^2 / 2g
                    Set(c.LaunchV, c.LockFrames);
                    WalkOn(host, padAt, t, info.KillHeight, true, fly: f => mv(0, 0));
                    float g = c.LaunchV.Y * c.LaunchV.Y / (2 * MathF.Max(LastApex, 1));
                    Console.WriteLine($"  cannon '{c.Name}' pad {c.EntityId}: ({padAt.X:0.0}, {padAt.Y:0.0}, {padAt.Z:0.0}) -> ({t.X:0.0}, {t.Y:0.0}, {t.Z:0.0}), "
                        + $"{(t.Xz - padAt.Xz).Length:0} across; today {c.LaunchV.Length:0.00}/f lock {c.LockFrames}: {LastFrames} f, rose {LastApex:0.0} (gravity {g:0.0000}/f2)");

                    // each plan flown let go and corrected (across speed and heading) until it lands on the target
                    (bool Ok, OpenTK.Mathematics.Vector3 V, int Frames, float Rose, string Why) Solve(float clear, ushort lk)
                    {
                        float top = MathF.Max(padAt.Y, t.Y) + 0.5f + clear, y0 = padAt.Y + 0.5f, l = lk * 2;
                        float vy = g * (-l + MathF.Sqrt(l * l + 2 * (top - y0) / g));
                        float frames = l + vy / g + MathF.Sqrt(2 * MathF.Max(top - stand.Y, 0.1f) / g);
                        var dir = new OpenTK.Mathematics.Vector3(t.X - padAt.X, 0, t.Z - padAt.Z).Normalized();
                        OpenTK.Mathematics.Vector3 v = dir * ((t.Xz - padAt.Xz).Length / frames) + OpenTK.Mathematics.Vector3.UnitY * vy;
                        string why = "";
                        for (int it = 0; it < 8; it++)
                        {
                            Set(v, lk);
                            WalkOn(host, padAt, t, info.KillHeight, true, fly: f => mv(0, 0));
                            float miss = (LastLand - stand).Length;
                            if (LastEnd == "landed" && miss < 0.8f) return (true, v, LastFrames, LastApex, "");
                            var line = new OpenTK.Mathematics.Vector3(t.X - LastFrom.X, 0, t.Z - LastFrom.Z);
                            float dist = line.Length;
                            line /= dist;
                            var side = OpenTK.Mathematics.Vector3.Cross(OpenTK.Mathematics.Vector3.UnitY, line);
                            OpenTK.Mathematics.Vector3 got = LastLand - LastFrom;
                            float along = got.X * line.X + got.Z * line.Z;
                            why = $"{LastEnd} {miss:0.0} off";
                            if (LastEnd == "landed" && MathF.Abs(LastLand.Y - stand.Y) > 1 && along < dist - 1) return (false, v, 0, 0, $"blocked: came down on another level {along:0} out");
                            var h = new OpenTK.Mathematics.Vector3(v.X, 0, v.Z);
                            if (along > 0.5f) h *= Math.Clamp(dist / along, 0.7f, 1.4f);
                            float yaw = -MathF.Atan2(got.X * side.X + got.Z * side.Z, MathF.Max(along, 1));
                            h = OpenTK.Mathematics.Matrix3.CreateRotationY(yaw) * h;
                            v = new OpenTK.Mathematics.Vector3(h.X, v.Y, h.Z);
                        }
                        return (false, v, 0, 0, why);
                    }

                    void Score(string plan, OpenTK.Mathematics.Vector3 v, ushort lk, int frames, float rose)
                    {
                        Set(v, lk);
                        var marks = new System.Text.StringBuilder();
                        int ok = 0;
                        foreach (var sc in lr)
                        {
                            WalkOn(host, padAt, t, info.KillHeight, true, 0, sc.Aim, fly: sc.In);
                            char m = Mark();
                            if (m == '+') ok++;
                            marks.Append(m);
                            if (marks.Length % 4 == 0) marks.Append(' ');
                        }
                        var refs = new List<string>();
                        foreach (var b in back)
                        {
                            WalkOn(host, padAt, t, info.KillHeight, true, fly: b.In);
                            refs.Add($"{Mark()}");
                        }
                        WalkOn(host, padAt, t, info.KillHeight, false, alt: true);
                        refs.Add($"ball {Mark()}");
                        Console.WriteLine($"    {plan,-22} {v.Length:0.00}/f ({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00}) {frames,3} f rose {rose,4:0.0} | left/right {ok,2}/{lr.Length} {marks}| back 0.1/0.25 s {refs[0]}{refs[1]} {refs[2]}");
                    }

                    Score($"today, lock {c.LockFrames}", c.LaunchV, c.LockFrames, LastFrames, LastApex);
                    foreach (ushort lk in new ushort[] { 0, 8, 15, 23, 30 })
                    {
                        foreach (float clear in new[] { 4f, 8f, 13f, 20f, 30f, 45f })
                        {
                            string plan = $"lock {lk,2} ({lk / 30f:0.00} s) top +{clear:0}";
                            var s = Solve(clear, lk);
                            if (!s.Ok)
                            {
                                Console.WriteLine($"    {plan,-22} no landing ({s.Why})");
                                continue;
                            }
                            Score(plan, s.V, lk, s.Frames, s.Rose);
                        }
                    }
                    dataField.SetValue(pad, orig);
                    beamField.SetValue(pad, origBeam);
                }
            }
        }

        // how far the floor at the target's height reaches from the target (16 directions; a step of more than 0.6 ends it),
        // and the angle that is from the pad
        static string Platform(MphRecomp.Campaign.CampaignHost host, OpenTK.Mathematics.Vector3 target, OpenTK.Mathematics.Vector3 pad)
        {
            string here = Reach(host, target, pad);
            // the most room within 12 of the target, same floor height
            (float Min, OpenTK.Mathematics.Vector3 At) best = (-1, target);
            for (float dx = -12; dx <= 12; dx += 1)
            {
                for (float dz = -12; dz <= 12; dz += 1)
                {
                    OpenTK.Mathematics.Vector3 q = target + new OpenTK.Mathematics.Vector3(dx, 0, dz);
                    float m = ReachMin(host, q, 20);
                    if (m > best.Min) best = (m, q);
                }
            }
            return here + $"; most room nearby at ({best.At.X:0.0}, {best.At.Y:0.0}, {best.At.Z:0.0}): {Reach(host, best.At, pad)}";
        }

        static float ReachMin(MphRecomp.Campaign.CampaignHost host, OpenTK.Mathematics.Vector3 target, float limit)
        {
            float min = float.MaxValue;
            for (int k = 0; k < 16 && min > 0; k++) min = MathF.Min(min, ReachDir(host, target, k, limit));
            return min;
        }

        static float ReachDir(MphRecomp.Campaign.CampaignHost host, OpenTK.Mathematics.Vector3 target, int k, float limit)
        {
            float a = k * MathF.PI / 8;
            var d = new OpenTK.Mathematics.Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            float r = 0;
            for (float x = 0.5f; x <= limit; x += 0.5f)
            {
                OpenTK.Mathematics.Vector3 p = target + d * x;
                var res = new MphRead.Formats.CollisionResult();
                if (!MphRead.Formats.CollisionDetection.CheckBetweenPoints(p + OpenTK.Mathematics.Vector3.UnitY * 0.6f, p - OpenTK.Mathematics.Vector3.UnitY * 0.6f,
                    MphRead.Formats.TestFlags.None, host.Scene, ref res) || res.Plane.Y < 0.7f) break;
                r = x;
            }
            return r;
        }

        static string Reach(MphRecomp.Campaign.CampaignHost host, OpenTK.Mathematics.Vector3 target, OpenTK.Mathematics.Vector3 pad)
        {
            List<float> reach = Enumerable.Range(0, 16).Select(k => ReachDir(host, target, k, 40)).ToList();
            float dist = (target.Xz - pad.Xz).Length;
            float min = reach.Min();
            return $"floor reaches {min:0.0}-{reach.Max():0.0} (min {min:0.0} = {OpenTK.Mathematics.MathHelper.RadiansToDegrees(MathF.Atan2(min, dist)):0.0} deg seen from the pad, {dist:0.0} away)";
        }

        static OpenTK.Mathematics.Vector3 LastFrom, LastLand;
        static int LastFrames;
        static string LastEnd = "";
        static float LastApex;

        // owner 10-08 00:2x ("make Echoes pads 1:1 with MPH pads, then decide trajectory/target"): every jump pad in every
        // multiplayer arena, MPH's and Echoes', fired the same way (walked on along the pad's own horizontal launch line)
        // with the stick let go, back / to the side / forward held the whole flight, and the morph ball let go: launch,
        // lock, flight, and how far each hold moves the landing (along the launch line and across it) vs let go
        static void PadCompare(string? only)
        {
            EchoesArena.EnsureRegistered();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            Console.WriteLine($"steering: {(JumpPadEntity.HostSameArcSteer ? "Echoes left/right rule on Echoes pads (default)" : "MPH's own (PAD_MPHSTEER=1)")}");
            Console.WriteLine("arena                  pad  speed lock  up deg | let go: frames  dist  rose | back: along  side | side: along  side | fwd: along | ball: along  side");
            foreach (RoomMetadata meta in MphRecomp.Multiplayer.MatchArenas.All)
            {
                if (only != null && !meta.Name.Contains(only, StringComparison.OrdinalIgnoreCase) && !meta.InGameName.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                if (meta.EntityPath == null) continue;
                List<JumpPadEntityEditor> pads;
                try
                {
                    pads = Read.GetEntities(meta.EntityPath, -1, firstHunt: false).Where(e => e.Type == EntityType.JumpPad)
                        .Select(e => new JumpPadEntityEditor(e, ((Entity<JumpPadEntityData>)e).Data)).ToList();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{meta.InGameName}: {ex.Message}");
                    continue;
                }
                if (pads.Count == 0) continue;
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, meta.Name, Hunter.Samus, 1, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                if (Environment.GetEnvironmentVariable("PAD_FALLDMG") == "mph") settings.EchoesPadNoFall = false;   // T6 N4: MPH's 10-frame rule
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                for (int f = 0; f < 600 && !(PlayerEntity.Main.Health > 0 && PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Active)); f++) host.Step(idle);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                float kill = -1000;
                foreach (JumpPadEntityEditor ed in pads)
                {
                    // the launch the device uses: the live entity's beam vector (Echoes pads' tuned launch rides in the file)
                    OpenTK.Mathematics.Vector3 beam = ed.BeamVector;
                    if (host.Scene.TryGetEntity(ed.Id, out EntityBase? ent) && ent is JumpPadEntity live)
                    {
                        beam = (OpenTK.Mathematics.Vector3)typeof(JumpPadEntity).GetField("_beamVector", flags)!.GetValue(live)!;
                    }
                    OpenTK.Mathematics.Vector3 launch = beam.Normalized() * ed.Speed;
                    var h = new OpenTK.Mathematics.Vector3(launch.X, 0, launch.Z);
                    OpenTK.Mathematics.Vector3 dir = h.Length > 0.05f ? h.Normalized() : OpenTK.Mathematics.Vector3.UnitZ;
                    var side = new OpenTK.Mathematics.Vector3(-dir.Z, 0, dir.X);
                    OpenTK.Mathematics.Vector3 target = ed.Position + dir * 50;
                    float upDeg = OpenTK.Mathematics.MathHelper.RadiansToDegrees(MathF.Atan2(launch.Y, h.Length));
                    var mv = (float x, float y) => new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(x, y) };
                    WalkOn(host, ed.Position, target, kill, true, fly: f => mv(0, 0));
                    if (LastEnd != "landed")
                    {
                        Console.WriteLine($"{meta.InGameName,-22} {ed.Id,3} {ed.Speed,6:0.00} {ed.ControlLockTime,4} {upDeg,6:0} | let go: {LastEnd} ({(LastFrom == default ? "" : "")}{LastFrames} f)");
                        continue;
                    }
                    OpenTK.Mathematics.Vector3 from = LastFrom, land = LastLand;
                    int frames = LastFrames;
                    float rose = LastApex;
                    float dist = OpenTK.Mathematics.Vector3.Dot(land - from, dir);
                    string Hold(Func<int, MphRecomp.Campaign.CampaignInput> fly, bool alt = false, bool two = true)
                    {
                        string r = WalkOn(host, ed.Position, target, kill, !alt, alt: alt, fly: fly);
                        if (LastEnd != "landed") return $"{(LastEnd == "DIED" ? "DIED" : "air"),11}" + (two ? "      " : "");
                        OpenTK.Mathematics.Vector3 d = LastLand - land;
                        return $"{OpenTK.Mathematics.Vector3.Dot(d, dir),+6:+0.0;-0.0}" + (two ? $" {OpenTK.Mathematics.Vector3.Dot(d, side),5:+0.0;-0.0}" : "") + (MathF.Abs(LastLand.Y - land.Y) > 1 ? $" dy{LastLand.Y - land.Y:+0;-0}" : "");
                    }
                    Console.WriteLine($"{meta.InGameName,-22} {ed.Id,3} {ed.Speed,6:0.00} {ed.ControlLockTime,4} {upDeg,6:0} | let go: {frames,6} {dist,5:0.0} {rose,5:0.0} | back: {Hold(f => mv(0, -1))} "
                        + $"| side: {Hold(f => mv(1, 0))} | fwd: {Hold(f => mv(0, 1), two: false)} | ball: {Hold(f => mv(0, 0), alt: true)} | back 0.25 s at 0.5 s: {Hold(f => f >= 30 && f < 45 ? mv(0, -1) : mv(0, 0), two: false)}");
                }
            }
        }

        static string WalkOn(MphRecomp.Campaign.CampaignHost host, OpenTK.Mathematics.Vector3 pad, OpenTK.Mathematics.Vector3 target, float killHeight, bool hold,
            float approachDeg = 0, float aimOffDeg = 0, bool alt = false, float side = 0, int weave = 0,
            Func<int, MphRecomp.Campaign.CampaignInput>? fly = null)
        {
            PlayerEntity me = PlayerEntity.Main;
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            var morph = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Buttons = MphRecomp.Campaign.CampaignButtons.Morph };
            var press = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Buttons = MphRecomp.Campaign.CampaignButtons.Shoot | MphRecomp.Campaign.CampaignButtons.Jump };
            var walk = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(0, 1) };
            for (int f = 0; f < 900 && me.Health == 0; f++) host.Step(f % 2 == 0 ? press : idle);   // respawn after a fall
            for (int f = 0; f < 60 && me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad); f++) host.Step(idle);   // the last flight's push ends on landing
            var dir =new OpenTK.Mathematics.Vector3(target.X - pad.X, 0, target.Z - pad.Z).Normalized();
            dir = OpenTK.Mathematics.Matrix3.CreateRotationY(OpenTK.Mathematics.MathHelper.DegreesToRadians(approachDeg)) * dir;
            OpenTK.Mathematics.Vector3 start = pad - dir * 3 + OpenTK.Mathematics.Vector3.UnitY * 0.3f;
            me.Reposition(start - me.Position, me.NodeRef);
            me.Reposition(me.Position, dir, me.NodeRef);
            me.Speed = OpenTK.Mathematics.Vector3.Zero;
            for (int f = 0; f < 40 && me.IsAltForm != alt; f++) host.Step(f % 2 == 0 ? morph : idle);
            for (int f = 0; f < 30; f++) host.Step(idle);
            if (MathF.Abs(me.Position.Y - pad.Y) > 1 || me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad))
            {
                return $"no clean run-up 3 units back (at ({me.Position.X:0.0}, {me.Position.Y:0.0}, {me.Position.Z:0.0}))";
            }
            int walked = 0;
            for (; walked < 120 && !me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad); walked++) host.Step(walk);
            if (!me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)) return $"never launched (walked to ({me.Position.X:0.0}, {me.Position.Y:0.0}, {me.Position.Z:0.0}))";
            if (aimOffDeg != 0)
            {
                var toTarget = new OpenTK.Mathematics.Vector3(target.X - pad.X, 0, target.Z - pad.Z).Normalized();
                me.Reposition(me.Position, OpenTK.Mathematics.Matrix3.CreateRotationY(OpenTK.Mathematics.MathHelper.DegreesToRadians(aimOffDeg)) * toTarget, me.NodeRef);
            }
            OpenTK.Mathematics.Vector3 from = me.Position;
            int hp0 = me.Health;
            float apex = 0, far = 0;
            int frames = 0;
            string end = "still flying after 20 s";
            for (; frames < 1200; frames++)
            {
                if (fly != null)
                {
                    host.Step(fly(frames));
                    if (Environment.GetEnvironmentVariable("PAD_TRACE") == "1" && frames % 5 == 0)
                    {
                        var accel = (OpenTK.Mathematics.Vector3)typeof(PlayerEntity).GetField("_jumpPadAccel",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(me)!;
                        Console.WriteLine($"      f{frames,3} push {accel.Xz.Length:0.000} speed ({me.Speed.X:0.00}, {me.Speed.Y:0.00}, {me.Speed.Z:0.00}) "
                            + $"pos ({me.Position.X:0.0}, {me.Position.Y:0.0}, {me.Position.Z:0.0}) {me.Flags1}");
                    }
                }
                else if (hold && side != 0)
                {
                    float x = weave > 0 && frames / weave % 2 == 1 ? -side : side;
                    host.Step(new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(x, 1) });
                }
                else
                {
                    host.Step(hold ? walk : idle);
                }
                apex = MathF.Max(apex, me.Position.Y - from.Y);
                far = MathF.Max(far, (me.Position.Xz - from.Xz).Length);
                if (me.Health == 0 || me.Position.Y < killHeight) { end = "DIED"; break; }
                if (frames > 8 && me.Flags1.TestFlag(PlayerFlags1.Standing) && MathF.Abs(me.Speed.Y) < 0.05f) { end = "landed"; break; }
            }
            OpenTK.Mathematics.Vector3 p = me.Position;
            LastFrom = from; LastLand = p; LastFrames = frames; LastEnd = end; LastApex = apex;
            float across = (p.Xz - target.Xz).Length;
            for (int f = 0; f < 40 && me.IsAltForm; f++) host.Step(f % 2 == 0 ? morph : idle);
            return $"{end} at ({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}) after {frames} f: {across:0.0} across from the target, dy {p.Y - target.Y - 0.5f:+0.0;-0.0}, "
                + $"rose {apex:0.0}, went {far:0.0} out (pad->target {(target.Xz - from.Xz).Length:0.0}), hp {me.Health - hp0:+0;-0}";
        }

        // the room model as MphRead loads it (its own reader, display lists decoded), drawn in software from the first
        // spawn at eye height: proves the DS stand-in end to end without a device
        static void Preview(EchoesArena.Def d, string png)
        {
            Model model = Read.GetRoomModelInstance(d.Room).Model;
            var tris = new System.Collections.Generic.List<(OpenTK.Mathematics.Vector3 P, OpenTK.Mathematics.Vector3 C)>();
            foreach (Mesh mesh in model.Meshes)
            {
                var color = OpenTK.Mathematics.Vector3.One;
                foreach (RenderInstruction ins in model.RenderInstructionLists[mesh.DlistId])
                {
                    if (ins.Code == InstructionCode.COLOR)
                    {
                        uint c = ins.Arguments[0];
                        color = new OpenTK.Mathematics.Vector3(c & 31, c >> 5 & 31, c >> 10 & 31) / 31f;
                    }
                    else if (ins.Code == InstructionCode.VTX_16)
                    {
                        static float S(uint v) => (short)(ushort)v / 4096f;
                        tris.Add((new OpenTK.Mathematics.Vector3(S(ins.Arguments[0]), S(ins.Arguments[0] >> 16), S(ins.Arguments[1])) * model.Scale, color));
                    }
                }
            }
            var spawn = Read.GetEntities(Metadata.GetRoomByName(d.Room).Item1!.EntityPath!, 0, firstHunt: false)
                .First(e => e.Type == EntityType.PlayerSpawn);
            var eye = spawn.Position + OpenTK.Mathematics.Vector3.UnitY * 1.4f;
            var fwd = spawn.FacingVector;
            var view = OpenTK.Mathematics.Matrix4.LookAt(eye, eye + fwd, OpenTK.Mathematics.Vector3.UnitY);
            const int W = 960, H = 540;
            var proj = OpenTK.Mathematics.Matrix4.CreatePerspectiveFieldOfView(OpenTK.Mathematics.MathHelper.DegreesToRadians(70), W / (float)H, 0.1f, 1000);
            var vp = view * proj;
            var depth = new float[W * H];
            Array.Fill(depth, float.MaxValue);
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(W, H, new SixLabors.ImageSharp.PixelFormats.Rgba32(40, 44, 52, 255));
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                var sp = new (float X, float Y, float Z)[3];
                bool behind = false;
                for (int k = 0; k < 3; k++)
                {
                    var c = new OpenTK.Mathematics.Vector4(tris[t + k].P, 1) * vp;
                    if (c.W < 0.1f) { behind = true; break; }
                    sp[k] = ((c.X / c.W * 0.5f + 0.5f) * W, (0.5f - c.Y / c.W * 0.5f) * H, c.W);
                }
                if (behind) continue;
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sp[0].X, MathF.Min(sp[1].X, sp[2].X))));
                int x1 = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(sp[0].X, MathF.Max(sp[1].X, sp[2].X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sp[0].Y, MathF.Min(sp[1].Y, sp[2].Y))));
                int y1 = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(sp[0].Y, MathF.Max(sp[1].Y, sp[2].Y))));
                float area = (sp[1].X - sp[0].X) * (sp[2].Y - sp[0].Y) - (sp[2].X - sp[0].X) * (sp[1].Y - sp[0].Y);
                if (MathF.Abs(area) < 1e-6f) continue;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float w0 = ((sp[1].X - px) * (sp[2].Y - py) - (sp[2].X - px) * (sp[1].Y - py)) / area;
                        float w1 = ((sp[2].X - px) * (sp[0].Y - py) - (sp[0].X - px) * (sp[2].Y - py)) / area;
                        float w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        float z = w0 * sp[0].Z + w1 * sp[1].Z + w2 * sp[2].Z;
                        if (z >= depth[y * W + x]) continue;
                        depth[y * W + x] = z;
                        var col = tris[t].C * w0 + tris[t + 1].C * w1 + tris[t + 2].C * w2;
                        img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgba32(col.X, col.Y, col.Z, 1);
                    }
                }
            }
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, png);
        }

        // the weapon crates (EchoesCrates) in a real match per arena: none before 15 s, all up at 15 s; the player shoots
        // one with the Power Beam (taps) until it breaks, picks up what it dropped (a weapon from the arena's pool), and
        // it's back 30 s later; then the same arena with Crates off (an old replay) has none. root=<import root> reads
        // the arenas an import wrote there (an -importcheck run), else the game's file system.
        // the crates' Echoes lighting (EchoesCrates.Lighting, as the import works it out) beside Echoes' own lightmaps around
        // them: per crate, its faces' brightness (max channel of ambient + lights: top, sides mean, sides min..max; with
        // and without the shadow rays) and the mean lightmap texel (max channel) of the lightmapped render corners within
        // 4 m of its centre, on floors and on walls
        // why a breakable gets the Echoes lights it gets (EchoesCrates.Lighting at its centre): every non-ambient light in
        // reach, its strength there, and whether the collision hides it
        // -echoesarena waterprobe [arena] [disc=]: each WATR's box, and where its top is open water (air above it, a floor
        // under it inside the box) on a 3 m grid of the area collision; the arena's "underwater" triggers
        static void WaterProbe(string disc, string? only)
        {
            using FileStream fs = File.OpenRead(disc);
            var gc = new GcDisc(fs);
            var pak = new Pak(gc.ReadFile("Metroid6.pak"), "Metroid6.pak");
            float scale = EchoesArena.DefaultScale;
            OpenTK.Mathematics.Vector3 Map(float x, float y, float z) => new OpenTK.Mathematics.Vector3(x, z, -y) * scale;
            foreach (EchoesArena.Def def in EchoesArena.All)
            {
                if (only != null && EchoesArena.Find(only) != def) continue;
                Mrea area = Mrea.Read(pak.Get(def.Mrea));
                List<Mrea.ScriptObject> scripts = area.ReadScripts();
                List<Mrea.ScriptObject> waters = scripts.Where(o => o.Type == "WATR").ToList();
                if (waters.Count == 0) continue;
                Mrea.Collision col = area.ReadCollision();
                var tris = new List<(OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3)>();
                for (int t = 0; t < col.TriangleCount; t++)
                {
                    OpenTK.Mathematics.Vector3 V(int k) { int i = col.Triangles[3 * t + k]; return Map(col.Vertices[3 * i], col.Vertices[3 * i + 1], col.Vertices[3 * i + 2]); }
                    tris.Add((V(0), V(1), V(2)));
                }
                var ray = new ArenaRaycaster(tris);
                Console.WriteLine($"{def.Key}: collision Echoes z {col.Vertices.Where((v, i) => i % 3 == 2).Min():0.0}..{col.Vertices.Where((v, i) => i % 3 == 2).Max():0.0}");
                foreach (Mrea.ScriptObject o in waters)
                {
                    float[] x = o.Transform;
                    float x0 = x[0] - x[6] / 2, x1 = x[0] + x[6] / 2, y0 = x[1] - x[7] / 2, y1 = x[1] + x[7] / 2, z0 = x[2] - x[8] / 2, z1 = x[2] + x[8] / 2;
                    int open = 0, buried = 0, noFloor = 0;
                    float floorMin = float.MaxValue, floorMax = float.MinValue;
                    var openXs = new List<(float, float)>();
                    for (float px = x0 + 1.5f; px < x1; px += 3)
                    {
                        for (float py = y0 + 1.5f; py < y1; py += 3)
                        {
                            OpenTK.Mathematics.Vector3 top = Map(px, py, z1 - 0.05f);
                            // under the top: the first floor down (inside the box)
                            var down = ray.Cast(top, Map(px, py, z0 - 0.5f));
                            var up = ray.Cast(top, Map(px, py, z1 + 3));
                            if (down == null) { noFloor++; continue; }
                            float fz = z1 - 0.05f - down.Value.T * (z1 - 0.05f - (z0 - 0.5f));
                            // a downward-facing hit right under the top = the top is inside rock
                            if (down.Value.Normal.Y < 0) { buried++; continue; }
                            if (up == null || up.Value.T * 3.05f > 1.0f)
                            {
                                open++;
                                openXs.Add((px, py));
                                floorMin = MathF.Min(floorMin, fz);
                                floorMax = MathF.Max(floorMax, fz);
                            }
                            else buried++;
                        }
                    }
                    string conns = string.Join(" ", o.Connections.Select(c => $"{c.State}/{c.Message}->{c.Target:x8}"));
                    Console.WriteLine($"  WATR {o.Id:x8} '{o.Name}' layer {o.Layer} active {o.Active}: x {x0:0.0}..{x1:0.0} y {y0:0.0}..{y1:0.0} z {z0:0.0}..{z1:0.0} (MPH surface y {z1 * scale:0.00})");
                    Console.WriteLine($"    top samples: {open} open water, {buried} under rock, {noFloor} no floor; open floors z {floorMin:0.0}..{floorMax:0.0}"
                        + (openXs.Count > 0 ? $"; open x {openXs.Min(q => q.Item1):0}..{openXs.Max(q => q.Item1):0} y {openXs.Min(q => q.Item2):0}..{openXs.Max(q => q.Item2):0}" : ""));
                    if (conns.Length > 0) Console.WriteLine($"    conns: {conns}");
                }
                // a top-down map (4 m cells, x across, y down): every floor in the cell's column, top to bottom: '~' = under a
                // WATR's top inside its box, '.' = dry; the cell shows its highest floor (upper case W/D when it has floors
                // of both kinds under it); ' ' = no floor
                {
                    float zTop = col.Vertices.Where((v, i) => i % 3 == 2).Max() + 1, zBot = col.Vertices.Where((v, i) => i % 3 == 2).Min() - 1;
                    float ax0 = col.Vertices.Where((v, i) => i % 3 == 0).Min(), ax1 = col.Vertices.Where((v, i) => i % 3 == 0).Max();
                    float ay0 = col.Vertices.Where((v, i) => i % 3 == 1).Min(), ay1 = col.Vertices.Where((v, i) => i % 3 == 1).Max();
                    bool Wet(float px, float py, float pz) => waters.Any(o => { float[] t = o.Transform; return MathF.Abs(px - t[0]) <= t[6] / 2 && MathF.Abs(py - t[1]) <= t[7] / 2 && pz + 0.5f >= t[2] - t[8] / 2 && pz + 0.5f <= t[2] + t[8] / 2; });
                    int wet = 0, dry = 0;
                    for (float py = ay1 - 2; py > ay0; py -= 4)
                    {
                        var line = new System.Text.StringBuilder($"  {py,6:0} ");
                        for (float px = ax0 + 2; px < ax1; px += 4)
                        {
                            float z = zTop;
                            int n = 0; bool? first = null; bool mixed = false;
                            while (z > zBot && n < 8)
                            {
                                var h = ray.Cast(Map(px, py, z), Map(px, py, zBot));
                                if (h == null) break;
                                float hz = z - h.Value.T * (z - zBot);
                                if (h.Value.Normal.Y > 0.5f)
                                {
                                    bool w = Wet(px, py, hz);
                                    if (w) wet++; else dry++;
                                    if (first == null) first = w; else if (first != w) mixed = true;
                                    n++;
                                }
                                z = hz - 0.3f;
                            }
                            line.Append(first == null ? ' ' : first.Value ? (mixed ? 'W' : '~') : (mixed ? 'D' : '.'));
                        }
                        Console.WriteLine(line.ToString().TrimEnd());
                    }
                    Console.WriteLine($"  floors: {wet} under water, {dry} dry (x {ax0:0}..{ax1:0} left to right, 4 m cells)");
                }
                foreach (Mrea.ScriptObject o in scripts.Where(o => o.Name.Contains("nderwater", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("Fluid", StringComparison.OrdinalIgnoreCase)))
                {
                    if (o.Type == "WATR") continue;
                    float[] x = o.Transform;
                    Console.WriteLine($"  {o.Type} {o.Id:x8} '{o.Name}' layer {o.Layer} active {o.Active} pos ({x[0]:0.0}, {x[1]:0.0}, {x[2]:0.0}) size ({x[6]:0.0}, {x[7]:0.0}, {x[8]:0.0}) "
                        + string.Join(" ", o.Connections.Select(c => $"{c.State}/{c.Message}->{c.Target:x8}")));
                }
            }
        }

        static void WallLightProbe(string disc)
        {
            using FileStream fs = File.OpenRead(disc);
            var gc = new GcDisc(fs);
            var pak = new Pak(gc.ReadFile("Metroid6.pak"), "Metroid6.pak");
            float scale = EchoesArena.DefaultScale;
            OpenTK.Mathematics.Vector3 Map(float x, float y, float z) => new OpenTK.Mathematics.Vector3(x, z, -y) * scale;
            foreach (EchoesArena.Def def in EchoesArena.All)
            {
                Mrea area = Mrea.Read(pak.Get(def.Mrea));
                List<Mrea.ScriptObject> scripts = area.ReadScripts();
                List<Mrea.ScriptObject> walls = scripts.Where(o => o.Layer == 0 && o.Active && o.Type == "PLAT" && o.Name == "wall").ToList();
                if (walls.Count == 0) continue;
                List<Mrea.Light> lights = area.ReadLights()[0];
                Mrea.Collision col = area.ReadCollision();
                var tris = new List<(OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3)>();
                for (int t = 0; t < col.TriangleCount; t++)
                {
                    OpenTK.Mathematics.Vector3 V(int k) { int i = col.Triangles[3 * t + k]; return Map(col.Vertices[3 * i], col.Vertices[3 * i + 1], col.Vertices[3 * i + 2]); }
                    tris.Add((V(0), V(1), V(2)));
                }
                var ray = new ArenaRaycaster(tris);
                var none = new ArenaRaycaster(new List<(OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3)>
                    { (new(1e6f, 1e6f, 1e6f), new(1e6f + 1, 1e6f, 1e6f), new(1e6f, 1e6f, 1e6f + 1)) });
                Cmdl geo = area.ReadGeometry();
                var texCache = new Dictionary<uint, (byte[] Rgba, int W, int H)?>();
                Cmdl wm = Cmdl.Load(pak.Get, 0xc220e7e0);
                var lo = new OpenTK.Mathematics.Vector3(float.MaxValue);
                var hi = new OpenTK.Mathematics.Vector3(float.MinValue);
                for (int i = 0; i + 2 < wm.Positions.Length; i += 3)
                {
                    var v = new OpenTK.Mathematics.Vector3(wm.Positions[i], wm.Positions[i + 1], wm.Positions[i + 2]);
                    lo = OpenTK.Mathematics.Vector3.ComponentMin(lo, v);
                    hi = OpenTK.Mathematics.Vector3.ComponentMax(hi, v);
                }
                Console.WriteLine($"{def.Key}: {lights.Count} lights, wall model bounds {lo} .. {hi}");
                Console.WriteLine("  collision flags in the area: " + string.Join(", ", col.TriangleFlags.GroupBy(f => f).OrderByDescending(g => g.Count()).Select(g => $"{g.Key:x16} x{g.Count()}")));
                foreach (Mrea.ScriptObject o in walls)
                {
                    OpenTK.Mathematics.Vector3 c = ArenaHdWriter.Place(o.Transform, (lo + hi) / 2);
                    OpenTK.Mathematics.Vector3 at = Map(c.X, c.Y, c.Z);
                    (float[] amb, List<float[]> got) = EchoesCrates.Lighting(lights, c, Map, ray);
                    Console.WriteLine($"  wall {o.Id:x8} pos ({o.Transform[0]:0.0}, {o.Transform[1]:0.0}, {o.Transform[2]:0.0}) rot ({o.Transform[3]:0}, {o.Transform[4]:0}, {o.Transform[5]:0}) centre {c} -> {got.Count} lights, ambient {amb[0]:0.00} {amb[1]:0.00} {amb[2]:0.00}");
                    // Echoes' own baked lightmaps on the world around it (within 6 m): how bright Echoes draws that spot
                    double floorSum = 0, wallSum = 0; int floorN = 0, wallN = 0;
                    for (int t = 0; t < geo.TriangleCount; t++)
                    {
                        Cmdl.Material m = geo.Materials[geo.TriangleMaterial[t]];
                        if ((m.Flags & 0x800) == 0 || m.TexIds.Length == 0 || geo.ShortUvs == null) continue;
                        for (int k = 0; k < 3; k++)
                        {
                            Cmdl.Corner cn = geo.Triangles[3 * t + k];
                            var qp = new OpenTK.Mathematics.Vector3(geo.Positions[3 * cn.P], geo.Positions[3 * cn.P + 1], geo.Positions[3 * cn.P + 2]);
                            if ((qp - c).Length > 6 || cn.T0 < 0 || 2 * cn.T0 + 1 >= geo.ShortUvs.Length || 3 * cn.N + 2 >= geo.Normals.Length) continue;
                            uint id = m.TexIds[0];
                            if (!texCache.TryGetValue(id, out var img))
                            {
                                img = null;
                                if (pak.Contains(id) && Txtr.Decode(pak.Get(id), out _, out int w, out int h) is byte[] rgba) img = (rgba, w, h);
                                texCache[id] = img;
                            }
                            if (img is not { } im) continue;
                            float s1 = geo.ShortUvs[2 * cn.T0], t1 = geo.ShortUvs[2 * cn.T0 + 1];
                            int x = (int)((s1 - MathF.Floor(s1)) * im.W) % im.W, y = (int)((t1 - MathF.Floor(t1)) * im.H) % im.H;
                            int px = 4 * (y * im.W + x);
                            float v = Math.Max(im.Rgba[px], Math.Max(im.Rgba[px + 1], im.Rgba[px + 2])) / 255f;
                            var nv = new OpenTK.Mathematics.Vector3(geo.Normals[3 * cn.N], geo.Normals[3 * cn.N + 1], geo.Normals[3 * cn.N + 2]);
                            float nz = nv.LengthSquared > 1e-12f ? nv.Z / nv.Length : 0;
                            if (nz > 0.7f) { floorSum += v; floorN++; }
                            else if (MathF.Abs(nz) < 0.3f) { wallSum += v; wallN++; }
                        }
                    }
                    (float[] amb2, List<float[]> all) = EchoesCrates.Lighting(lights, c, Map, none);
                    Console.WriteLine($"    Echoes lightmap around it: floor " + (floorN > 0 ? $"{floorSum / floorN:0.00} ({floorN})" : "-") + " walls " + (wallN > 0 ? $"{wallSum / wallN:0.00} ({wallN})" : "-")
                        + $"; with no shadow test it would get {all.Count} lights (strongest {(all.Count > 0 ? all.Max(l => MathF.Max(l[3], MathF.Max(l[4], l[5]))) : 0):0.000})");
                    foreach (Mrea.Light l in lights)
                    {
                        if (l.Type == Mrea.LightType.LocalAmbient) continue;
                        var lp = new OpenTK.Mathematics.Vector3(l.Position[0], l.Position[1], l.Position[2]);
                        float d = (lp - c).Length;
                        float q = MathF.Max(l.Multiplier, 1.1920929e-7f);
                        float den = l.Falloff switch { 0 => 2 / q, 1 => 250 / q * d, _ => 25000 / q * d * d };
                        float k = 1 / MathF.Max(den, 1e-6f), cone = 1;
                        var ld = new OpenTK.Mathematics.Vector3(l.Direction[0], l.Direction[1], l.Direction[2]);
                        if (l.Type == Mrea.LightType.Spot && ld.LengthSquared > 1e-8f)
                        {
                            float cr = MathF.Cos(OpenTK.Mathematics.MathHelper.DegreesToRadians(l.SpotCutoff / 2));
                            float cc = OpenTK.Mathematics.Vector3.Dot((c - lp) / d, ld.Normalized());
                            cone = cr < 1 ? MathF.Max(cc * (cc - cr) / (1 - cr), 0) : 0;
                        }
                        float s0 = MathF.Max(l.Color[0], MathF.Max(l.Color[1], l.Color[2])) * k;
                        if (s0 < 1 / 255f || d > 40) continue;
                        OpenTK.Mathematics.Vector3 lpm = Map(lp.X, lp.Y, lp.Z);
                        float len = (lpm - at).Length;
                        string shadow = !l.CastShadows ? "no shadows" : ray.Cast(at, lpm) is { } hit ? $"HIT at {hit.T * len:0.00} of {len:0.00}" : "clear";
                        Console.WriteLine($"    {l.Type,-11} at ({lp.X:0.0}, {lp.Y:0.0}, {lp.Z:0.0}) d {d,5:0.0} m falloff {l.Falloff} q {q,6:0.00} k {k:0.000} cone {cone:0.00} strength {s0 * cone:0.000} {shadow}");
                        // every collision triangle on the way (area space), with its material flags
                        for (int t = 0; t < col.TriangleCount; t++)
                        {
                            OpenTK.Mathematics.Vector3 A(int k2) { int i = col.Triangles[3 * t + k2]; return new(col.Vertices[3 * i], col.Vertices[3 * i + 1], col.Vertices[3 * i + 2]); }
                            OpenTK.Mathematics.Vector3 a = A(0), e1 = A(1) - a, e2 = A(2) - a, dir = lp - c;
                            OpenTK.Mathematics.Vector3 pv = OpenTK.Mathematics.Vector3.Cross(dir, e2);
                            float det = OpenTK.Mathematics.Vector3.Dot(e1, pv);
                            if (MathF.Abs(det) < 1e-9f) continue;
                            OpenTK.Mathematics.Vector3 tv = c - a;
                            float u = OpenTK.Mathematics.Vector3.Dot(tv, pv) / det;
                            if (u < 0 || u > 1) continue;
                            OpenTK.Mathematics.Vector3 qv = OpenTK.Mathematics.Vector3.Cross(tv, e1);
                            float v = OpenTK.Mathematics.Vector3.Dot(dir, qv) / det;
                            if (v < 0 || u + v > 1) continue;
                            float tt = OpenTK.Mathematics.Vector3.Dot(e2, qv) / det;
                            if (tt <= 0 || tt >= 1) continue;
                            OpenTK.Mathematics.Vector3 n = OpenTK.Mathematics.Vector3.Cross(e1, e2).Normalized();
                            Console.WriteLine($"      tri {t} at {tt * d:0.00} m flags {col.TriangleFlags[t]:x16} ({Mrea.Collision.SurfaceOf(col.TriangleFlags[t])}) normal ({n.X:0.00}, {n.Y:0.00}, {n.Z:0.00}) {(OpenTK.Mathematics.Vector3.Dot(n, dir) < 0 ? "facing the wall" : "back face")}");
                        }
                    }
                }
            }
        }

        static void CrateLightProbe(string disc)
        {
            using FileStream fs = File.OpenRead(disc);
            var gc = new GcDisc(fs);
            var pak = new Pak(gc.ReadFile("Metroid6.pak"), "Metroid6.pak");
            float scale = EchoesArena.DefaultScale;
            OpenTK.Mathematics.Vector3 Map(float x, float y, float z) => new OpenTK.Mathematics.Vector3(x, z, -y) * scale;
            var texCache = new Dictionary<uint, (byte[] Rgba, int W, int H)?>();
            foreach (EchoesArena.Def def in EchoesArena.All)
            {
                Mrea area = Mrea.Read(pak.Get(def.Mrea));
                List<Mrea.ScriptObject> scripts = area.ReadScripts();
                List<Mrea.Light> lights = area.ReadLights()[0];
                Mrea.Collision col = area.ReadCollision();
                var tris = new List<(OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3)>();
                for (int t = 0; t < col.TriangleCount; t++)
                {
                    OpenTK.Mathematics.Vector3 V(int k) { int i = col.Triangles[3 * t + k]; return Map(col.Vertices[3 * i], col.Vertices[3 * i + 1], col.Vertices[3 * i + 2]); }
                    tris.Add((V(0), V(1), V(2)));
                }
                var ray = new ArenaRaycaster(tris);
                var none = new ArenaRaycaster(new List<(OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3)>
                    { (new(1e6f, 1e6f, 1e6f), new(1e6f + 1, 1e6f, 1e6f), new(1e6f, 1e6f, 1e6f + 1)) });
                Cmdl geo = area.ReadGeometry();
                Console.WriteLine($"{def.Key}: {lights.Count} lights");
                var seen = new List<OpenTK.Mathematics.Vector3>();
                foreach (Mrea.ScriptObject o in scripts.Where(o => o.Type == "ACTR" && o.Name.Contains("Weapon Crate")))
                {
                    var p = new OpenTK.Mathematics.Vector3(o.Transform[0], o.Transform[1], o.Transform[2]);
                    if (seen.Any(q => (q - p).LengthSquared < 0.25f)) continue;
                    seen.Add(p);
                    string Faces(ArenaRaycaster r)
                    {
                        (float[] amb, List<float[]> ls) = EchoesCrates.Lighting(lights, p, Map, r);
                        float F(OpenTK.Mathematics.Vector3 n)
                        {
                            var c = new OpenTK.Mathematics.Vector3(amb[0], amb[1], amb[2]);
                            foreach (float[] l in ls) c += new OpenTK.Mathematics.Vector3(l[3], l[4], l[5]) * MathF.Max(0, OpenTK.Mathematics.Vector3.Dot(n, new(l[0], l[1], l[2])));
                            return MathF.Min(1, MathF.Max(c.X, MathF.Max(c.Y, c.Z)));
                        }
                        float[] sides = { F(OpenTK.Mathematics.Vector3.UnitX), F(-OpenTK.Mathematics.Vector3.UnitX), F(OpenTK.Mathematics.Vector3.UnitZ), F(-OpenTK.Mathematics.Vector3.UnitZ) };
                        return $"top {F(OpenTK.Mathematics.Vector3.UnitY):0.00} sides {sides.Average():0.00} ({sides.Min():0.00}..{sides.Max():0.00}), {ls.Count} lights";
                    }
                    // Echoes' lightmaps around it
                    double floorSum = 0, wallSum = 0; int floorN = 0, wallN = 0;
                    for (int t = 0; t < geo.TriangleCount; t++)
                    {
                        Cmdl.Material m = geo.Materials[geo.TriangleMaterial[t]];
                        if ((m.Flags & 0x800) == 0 || m.TexIds.Length == 0 || geo.ShortUvs == null) continue;
                        for (int k = 0; k < 3; k++)
                        {
                            Cmdl.Corner c = geo.Triangles[3 * t + k];
                            var q = new OpenTK.Mathematics.Vector3(geo.Positions[3 * c.P], geo.Positions[3 * c.P + 1], geo.Positions[3 * c.P + 2]);
                            if ((q - p).Length > 4 || c.T0 < 0 || 2 * c.T0 + 1 >= geo.ShortUvs.Length || 3 * c.N + 2 >= geo.Normals.Length) continue;
                            uint id = m.TexIds[0];
                            if (!texCache.TryGetValue(id, out var img))
                            {
                                img = null;
                                if (pak.Contains(id) && Txtr.Decode(pak.Get(id), out _, out int w, out int h) is byte[] rgba) img = (rgba, w, h);
                                texCache[id] = img;
                            }
                            if (img is not { } im) continue;
                            float s0 = geo.ShortUvs[2 * c.T0], t0 = geo.ShortUvs[2 * c.T0 + 1];
                            int x = (int)((s0 - MathF.Floor(s0)) * im.W) % im.W, y = (int)((t0 - MathF.Floor(t0)) * im.H) % im.H;
                            int at = 4 * (y * im.W + x);
                            float v = Math.Max(im.Rgba[at], Math.Max(im.Rgba[at + 1], im.Rgba[at + 2])) / 255f;
                            var nv = new OpenTK.Mathematics.Vector3(geo.Normals[3 * c.N], geo.Normals[3 * c.N + 1], geo.Normals[3 * c.N + 2]); float nz = nv.LengthSquared > 1e-12f ? nv.Z / nv.Length : 0;
                            if (nz > 0.7f) { floorSum += v; floorN++; }
                            else if (MathF.Abs(nz) < 0.3f) { wallSum += v; wallN++; }
                        }
                    }
                    Console.WriteLine($"  crate ({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}): lit {Faces(ray)} | no shadow rays: {Faces(none)} | lightmap floor "
                        + (floorN > 0 ? $"{floorSum / floorN:0.00} ({floorN})" : "-") + " walls " + (wallN > 0 ? $"{wallSum / wallN:0.00} ({wallN})" : "-"));
                }
            }
        }

        static void CrateTest(string? only, string? root)
        {
            if (root != null)
            {
                EchoesArena.ImportRoot = root;
                EchoesArena.RegisterImported(root);
            }
            else
            {
                EchoesArena.Register(Paths.FileSystem);
            }
            int fails = 0;
            void Check(bool ok, string what)
            {
                if (!ok) fails++;
                Console.WriteLine($"    {(ok ? "ok  " : "FAIL")} {what}");
            }
            List<EchoesCrateEntity> Crates(MphRecomp.Campaign.CampaignHost h)
            {
                var list = new List<EchoesCrateEntity>();
                foreach (EnemyInstanceEntity e in h.Scene.GetEnemyInstanceEntities()) if (e is EchoesCrateEntity c) list.Add(c);
                return list;
            }
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            foreach (EchoesArena.Def d in EchoesArena.All)
            {
                if (only != null && EchoesArena.Find(only) != d) continue;
                if (Metadata.GetRoomByName(d.Room).Item1 == null) continue;
                Console.WriteLine($"  {d.InGameName}:");
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, 1, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                using (MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings))
                {
                    if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;   // a still target, not a shooter
                    List<EchoesCrateEntity> crates = Crates(host);
                    Check(crates.Count > 0, $"{crates.Count} crates in the match");
                    if (crates.Count == 0) continue;
                    int f = 0;
                    for (; f < EchoesCrateEntity.AppearFrames - 2; f++) host.Step(idle);
                    Check(crates.All(c => !c.Up), $"none up at step {f}");
                    for (; f < EchoesCrateEntity.AppearFrames + 2; f++) host.Step(idle);
                    Check(crates.All(c => c.Up), $"all up at step {f} (15 s = {EchoesCrateEntity.AppearFrames})");
                    // shoot the first crate from 3 units away, trying 8 directions until the shots land
                    EchoesCrateEntity crate = crates[0];
                    int shots = 0, start = crate.Health;
                    long broke = -1;
                    void StepWatch(in MphRecomp.Campaign.CampaignInput input)
                    {
                        host.Step(input);
                        if (broke < 0 && crate.Breaks > 0) broke = host.Frame;
                    }
                    for (int dir = 0; dir < 8 && crate.Breaks == 0; dir++)
                    {
                        float a = dir * MathF.PI / 4;
                        var away = new OpenTK.Mathematics.Vector3(MathF.Sin(a), 0, MathF.Cos(a));
                        // standing (position 0.5 above the feet) on the crate's floor: its drop point minus 0.65
                        OpenTK.Mathematics.Vector3 stand = crate.DropPoint.AddY(-0.65f + 0.5f) + away * 3;
                        host.PlacePlayer(stand, -away);
                        for (int k = 0; k < 20; k++) host.Step(idle);
                        int before = crate.Health;
                        for (int tap = 0; tap < 12 && crate.Breaks == 0; tap++)
                        {
                            var fire = idle;
                            fire.Buttons = MphRecomp.Campaign.CampaignButtons.Shoot;
                            StepWatch(fire);
                            shots++;
                            for (int k = 0; k < 11; k++) StepWatch(idle);
                        }
                        Console.WriteLine($"    from direction {dir}: health {before} -> {crate.Health}, breaks {crate.Breaks}");
                    }
                    Check(crate.Breaks == 1, $"broke after {shots} shots (health {start}: {start / 6} Power Beam shots)");
                    if (crate.Breaks == 0) continue;
                    Check(!crate.Up, "hidden once broken");
                    string[] pool = EchoesArena.CratePools[d.Key].Concat(EchoesCrates.Blaster ? new[] { ItemType.PirateBlaster } : Array.Empty<ItemType>())
                        .Select(t => t.ToString()).ToArray();
                    Check(crate.Pool.Contains(ItemType.PirateBlaster) == EchoesCrates.Blaster,
                        $"Pirate Blaster in the pool: {crate.Pool.Contains(ItemType.PirateBlaster)} (EXTRA PICKUPS: {EchoesCrates.Blaster})");
                    Check(pool.Contains(crate.LastDrop.ToString()), $"dropped {crate.LastDrop} (pool {string.Join(", ", pool)})");
                    ItemInstanceEntity? drop = null;
                    foreach (ItemInstanceEntity it in host.Scene.GetItemInstanceEntities()) if (it.ItemType == crate.LastDrop) drop = it;
                    Check(drop != null, drop != null ? $"the drop is in the arena at ({drop.Position.X:0.0}, {drop.Position.Y:0.0}, {drop.Position.Z:0.0})" : "the drop is in the arena");
                    if (drop != null)
                    {
                        host.PlacePlayer(drop.Position.AddY(-0.65f + 0.5f), OpenTK.Mathematics.Vector3.UnitZ);
                        for (int k = 0; k < 30; k++) host.Step(idle);
                        BeamType beam = Enum.Parse<BeamType>(crate.LastDrop.ToString());
                        Check(PlayerEntity.Main.AvailableWeapons[beam], $"picked up: the player has the {beam}");
                    }
                    long back = -1;
                    for (int k = 0; k < EchoesCrateEntity.RespawnFrames + 120 && back < 0; k++)
                    {
                        host.Step(idle);
                        if (crate.Up) back = host.Frame - broke;
                    }
                    Check(back >= EchoesCrateEntity.RespawnFrames - 2 && back <= EchoesCrateEntity.RespawnFrames + 2,
                        $"back after {back} steps (30 s = {EchoesCrateEntity.RespawnFrames}), health {crate.Health}");
                }
                settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, 1, 0);
                settings.Crates = false;
                using (MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings))
                {
                    Check(Crates(host).Count == 0, "Crates off (an old replay): no crates");
                }
            }
            {
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, "MP3 PROVING GROUND", Hunter.Samus, 1, 0);
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                Console.WriteLine("  Combat Hall (MPH):");
                Check(Crates(host).Count == 0, "an MPH arena: no crates");
            }
            Console.WriteLine(fails == 0 ? "  cratetest: PASS" : $"  cratetest: {fails} FAIL");
        }

        // Echoes arenas' breakables + Dark Water (EchoesBreakables): the damage rule; in each arena that has them, a
        // grate holds a player up, takes 5 Power Beam shots, drops the player into Dark Water (MPH acid, ~7.5/s), comes
        // back 10 s later once nobody is inside it, and breaks to a morph ball bomb; a wall takes 20 real Power Beam shots
        // and stays broken; none with the match's Breakables off or in an MPH arena
        // -echoesarena movetest [arena] [root=<import root>]: Echoes movement (EchoesMovement). Double jump: a jump, then a
        // second press at the top of it, in an Echoes arena (default Crossfire Chaos) with it on, the same arena with
        // EchoesMoves false, and Combat Hall; the second press only lifts her in the first.
        static void MoveTest(string? only, string? root)
        {
            if (root != null)
            {
                EchoesArena.ImportRoot = root;
                EchoesArena.RegisterImported(root);
            }
            else
            {
                EchoesArena.Register(Paths.FileSystem);
            }
            int fails = 0;
            void Check(bool ok, string what)
            {
                if (!ok) fails++;
                Console.WriteLine($"    {(ok ? "ok  " : "FAIL")} {what}");
            }
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            MphRecomp.Campaign.CampaignInput Press(MphRecomp.Campaign.CampaignButtons b) { var i = idle; i.Buttons = b; return i; }
            EchoesArena.Def echoes = EchoesArena.Find(only ?? "CrossfireChaos") ?? throw new ArgumentException($"no Echoes arena {only}");
            string combat = MphRecomp.Multiplayer.MatchArenas.Find("Combat Hall")?.Name ?? throw new InvalidOperationException("no Combat Hall");
            // (lift of the first jump, lift of the second press over the first jump's top)
            (float First, float Second) Jumps(string room, bool? moves)
            {
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, room, Hunter.Samus, 1, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                settings.Crates = false;
                settings.Breakables = false;
                settings.EchoesMoves = moves;
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                PlayerEntity me = PlayerEntity.Main;
                for (int k = 0; k < 600 && !me.LoadFlags.TestFlag(LoadFlags.Spawned); k++) host.Step(idle);
                for (int k = 0; k < 60 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++) host.Step(idle);
                for (int k = 0; k < 30; k++) host.Step(idle);
                float y0 = me.Position.Y;
                for (int k = 0; k < 4; k++) host.Step(Press(MphRecomp.Campaign.CampaignButtons.Jump));
                for (int k = 0; k < 120 && me.Speed.Y > 0; k++) host.Step(idle);
                float top = me.Position.Y;
                for (int k = 0; k < 4; k++) host.Step(Press(MphRecomp.Campaign.CampaignButtons.Jump));
                float top2 = me.Position.Y;
                for (int k = 0; k < 120 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++)
                {
                    host.Step(idle);
                    top2 = MathF.Max(top2, me.Position.Y);
                }
                return (top - y0, top2 - top);
            }
            Console.WriteLine($"  double jump ({echoes.InGameName}; Combat Hall as the control):");
            (float a1, float a2) = Jumps(echoes.Room, null);
            Check(a1 > 0.5f && a2 > a1 * 0.8f, $"Echoes arena: jump {a1:0.00}, the second press lifts {a2:0.00} more");
            (float b1, float b2) = Jumps(echoes.Room, false);
            Check(b1 > 0.5f && b2 < 0.05f, $"Echoes arena, EchoesMoves off: jump {b1:0.00}, second press {b2:0.00}");
            (float c1, float c2) = Jumps(combat, null);
            Check(c1 > 0.5f && c2 < 0.05f, $"Combat Hall: jump {c1:0.00}, second press {c2:0.00}");
            Check(PlayerEntity.HostEchoesMoves == false, "the hook is off after Combat Hall loaded");
            // water: Pipeline's flooded cave (spawn 'Multiplayer 6 Under Water', Echoes (23.0, -0.2, -9.4)) vs its dry
            // east floor (spawn 'Multiplayer 4', Echoes (86.0, -9.1, 7.2))
            EchoesArena.Def? pipe = EchoesArena.Find("Pipeline");
            if (pipe != null && Metadata.GetRoomByName(pipe.Room).Item1 != null)
            {
                OpenTK.Mathematics.Vector3 M(float x, float y, float z) => new OpenTK.Mathematics.Vector3(x, z, -y) * EchoesArena.DefaultScale;
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, pipe.Room, Hunter.Samus, 1, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                settings.Crates = false;
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                PlayerEntity me = PlayerEntity.Main;
                for (int k = 0; k < 600 && !me.LoadFlags.TestFlag(LoadFlags.Spawned); k++) host.Step(idle);
                for (int k = 0; k < 60 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++) host.Step(idle);
                for (int k = 0; k < 30; k++) host.Step(idle);
                Console.WriteLine($"  water (Pipeline: {PlayerEntity.HostWater?.Length ?? 0} boxes):");
                // after a teleport the Standing flag is the old spot's until she lands: step first, then wait for it
                void Settle()
                {
                    for (int k = 0; k < 4; k++) host.Step(idle);
                    for (int k = 0; k < 240 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++) host.Step(idle);
                    for (int k = 0; k < 30; k++) host.Step(idle);
                    for (int k = 0; k < 60 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++) host.Step(idle);
                }
                // stand at a spot, settle; then a jump (held `hold` steps, then a second press at the top held `press2` steps)
                (float Lift, float Second, int Rise, bool Wet, float Kick) Jump(OpenTK.Mathematics.Vector3 at, int hold, int press2)
                {
                    host.PlacePlayer(at + OpenTK.Mathematics.Vector3.UnitY, OpenTK.Mathematics.Vector3.UnitZ);
                    Settle();
                    bool wet = me.EchoesSubmerged;
                    if (Environment.GetEnvironmentVariable("MOVETRACE") == "1")
                    {
                        Console.WriteLine($"      (placed at {at}, settled at {me.Position}, standing {me.Flags1.TestFlag(PlayerFlags1.Standing)}, wet {wet})");
                    }
                    float y0 = me.Position.Y, top = y0;
                    int rise = 0;
                    for (int k = 0; k < Math.Max(hold, 4); k++) { host.Step(Press(MphRecomp.Campaign.CampaignButtons.Jump)); top = MathF.Max(top, me.Position.Y); rise++; }
                    for (int k = 0; k < 300 && me.Speed.Y > 0; k++) { host.Step(idle); top = MathF.Max(top, me.Position.Y); rise++; }
                    float top2 = top, kick = 0;
                    if (press2 > 0)
                    {
                        for (int k = 0; k < press2; k++)
                        {
                            host.Step(Press(MphRecomp.Campaign.CampaignButtons.Jump));
                            top2 = MathF.Max(top2, me.Position.Y);
                            kick = MathF.Max(kick, me.Speed.Y);
                        }
                        for (int k = 0; k < 300 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++)
                        {
                            host.Step(idle);
                            top2 = MathF.Max(top2, me.Position.Y);
                            if (Environment.GetEnvironmentVariable("MOVETRACE") == "2") Console.WriteLine($"        after {k}: y {me.Position.Y:0.0000} vy {me.Speed.Y:0.0000}");
                        }
                    }
                    for (int k = 0; k < 400 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++) host.Step(idle);
                    return (top - y0, top2 - top, rise, wet, kick);
                }
                OpenTK.Mathematics.Vector3 wetSpot = M(23.0f, -0.2f, -9.4f), drySpot = M(86.0f, -9.1f, 7.2f);
                var dry = Jump(drySpot, 4, 0);
                var wet = Jump(wetSpot, 4, 0);
                Check(!dry.Wet && wet.Wet, $"submerged in the cave ({wet.Wet}), not on the dry floor ({dry.Wet})");
                // Echoes as shipped (owner 2026-10-09): the Gravity Boost's free movement, so her own jump under water
                Check(MathF.Abs(wet.Lift - dry.Lift) < dry.Lift * 0.05f && Math.Abs(wet.Rise - dry.Rise) <= 2,
                    $"water jump {wet.Lift:0.00} over {wet.Rise} steps = dry {dry.Lift:0.00} over {dry.Rise} (free movement)");
                // as in Echoes, holding the first jump is just a jump (it doesn't use up the boost); the second press held is
                // the boost, up to 1.5 s
                var held = Jump(wetSpot, 120, 0);
                Check(MathF.Abs(held.Lift - dry.Lift) < dry.Lift * 0.05f, $"holding the first jump under water is a jump: lift {held.Lift:0.00} (dry {dry.Lift:0.00})");
                var boost = Jump(wetSpot, 4, 100);
                Check(boost.Second > dry.Lift * 1.5f, $"second press held under water = Gravity Boost: {boost.Second:0.00} more over the jump's top (dry jump {dry.Lift:0.00})");
                // under water a second press is the boost, not a double jump: a tap lifts next to nothing (the boost runs
                // while jump is held), holding it rises
                var tap = Jump(wetSpot, 4, 4);
                var again = Jump(wetSpot, 4, 60);
                Check(tap.Second < dry.Lift * 0.3f && again.Second > dry.Lift * 0.5f,
                    $"a second press at the top = the boost: tapped {tap.Second:0.00}, held {again.Second:0.00} more (no double jump: a dry one lifts {dry.Lift:0.00})");
                // the dry floor's double jump: the east floor's ceiling is ~3.5 over it (a double jump's height hits it), so
                // check the second press's launch speed instead: her full jump speed again
                float jumpSpeed = Fixed.ToFloat(me.Values.JumpSpeed);
                var dry2 = Jump(drySpot, 4, 4);
                Check(!dry2.Wet && dry2.Kick > jumpSpeed * 0.9f && dry2.Second > 0.3f,
                    $"on the dry floor the second press is the double jump: launched at {dry2.Kick:0.000}/step (jump speed {jumpSpeed:0.000}), {dry2.Second:0.00} more to the ceiling; under water the tap launched at {tap.Kick:0.000}");
                // walking: 60 steps forward from a standstill
                float Walk(OpenTK.Mathematics.Vector3 at)
                {
                    host.PlacePlayer(at + OpenTK.Mathematics.Vector3.UnitY, OpenTK.Mathematics.Vector3.UnitZ);
                    Settle();
                    var walk = idle; walk.Move = new System.Numerics.Vector2(0, 1);
                    float best = 0;
                    for (int k = 0; k < 60; k++) { host.Step(walk); best = MathF.Max(best, new OpenTK.Mathematics.Vector2(me.Speed.X, me.Speed.Z).Length); }
                    return best;
                }
                float vDry = Walk(drySpot), vWet = Walk(wetSpot);
                Check(MathF.Abs(vWet - vDry) < vDry * 0.05f, $"walking under water {vWet:0.000}/step = dry {vDry:0.000} (free movement)");
                // falling: dropped from 3 units over the floor, under water and dry
                float Fall(OpenTK.Mathematics.Vector3 at)
                {
                    host.PlacePlayer(at + OpenTK.Mathematics.Vector3.UnitY * 3, OpenTK.Mathematics.Vector3.UnitZ);
                    float fastest = 0;
                    for (int k = 0; k < 4; k++) host.Step(idle);
                    for (int k = 0; k < 240 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++) { host.Step(idle); fastest = MathF.Min(fastest, me.Speed.Y); }
                    return -fastest;
                }
                float fWet = Fall(wetSpot), fDry = Fall(drySpot);
                Check(fWet > fDry * 0.9f, $"falls under water at her own gravity: fastest {fWet:0.000}/tick (dry {fDry:0.000})");
            }
            Console.WriteLine(fails == 0 ? "movetest PASS" : $"movetest FAIL ({fails})");
        }

        // the second press at every point of the first jump's arc: the double jump (Crossfire Chaos, dry) and the Gravity
        // Boost (Pipeline's cave), the first press held short (4 steps) and long (20 steps, past the boost's hold)
        // T7 (owner 10-10, N24 "Do bots know they have double jump and gravity boost in echoes maps"): how high and how far
        // each hunter gets with a jump and Echoes' double jump (second press at the top, as the bots' climb does it),
        // standing and running, and how high the Gravity Boost lifts her from Pipeline's cave floor: ArenaNav's link limits
        static void JumpReach(string arena)
        {
            EchoesArena.Register(Paths.FileSystem);
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            OpenTK.Mathematics.Vector3 M(float x, float y, float z) => new OpenTK.Mathematics.Vector3(x, z, -y) * EchoesArena.DefaultScale;
            foreach (Hunter h in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel, Hunter.Guardian })
            {
                EchoesArena.Def def = EchoesArena.Find(arena) ?? throw new ArgumentException(arena);
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, h, 1, 0);
                settings.TimeLimitSeconds = 36000; settings.PointGoal = 999; settings.Crates = false; settings.Breakables = false;
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                PlayerEntity me = PlayerEntity.Main;
                for (int k = 0; k < 600 && !me.LoadFlags.TestFlag(LoadFlags.Spawned); k++) host.Step(idle);
                for (int k = 0; k < 90; k++) host.Step(idle);
                // a straight flat run: 24 units of floor at one height, nothing in the way up to 4 above it
                bool FloorY(OpenTK.Mathematics.Vector3 p, out float y)
                {
                    var r = new MphRead.Formats.CollisionResult(); y = 0;
                    if (!MphRead.Formats.CollisionDetection.CheckBetweenPoints(p + OpenTK.Mathematics.Vector3.UnitY * 1, p - OpenTK.Mathematics.Vector3.UnitY * 1, MphRead.Formats.TestFlags.Players, host.Scene, ref r)) return false;
                    y = r.Position.Y; return r.Plane.Y > 0.8f;
                }
                OpenTK.Mathematics.Vector3 at = default, dir = default; bool found = false;
                var nodes = host.Scene.Room?.NodeData?.Data.SelectMany(q => q.SelectMany(l => l)).Select(n => n.Position).ToList() ?? new();
                foreach (var n in nodes)
                {
                    for (int d = 0; d < 8 && !found; d++)
                    {
                        var v = new OpenTK.Mathematics.Vector3(MathF.Cos(d * MathF.PI / 4), 0, MathF.Sin(d * MathF.PI / 4));
                        if (!FloorY(n, out float y0)) continue;
                        var b = new OpenTK.Mathematics.Vector3(n.X, y0, n.Z);
                        bool ok = true;
                        for (float t = 0.5f; t <= 24 && ok; t += 0.5f)
                        {
                            var q = b + v * t;
                            ok = FloorY(q, out float y) && MathF.Abs(y - y0) < 0.1f;
                            var r = new MphRead.Formats.CollisionResult();
                            ok = ok && !MphRead.Formats.CollisionDetection.CheckBetweenPoints(b + v * (t - 0.5f) + OpenTK.Mathematics.Vector3.UnitY * 0.8f, q + OpenTK.Mathematics.Vector3.UnitY * 0.8f, MphRead.Formats.TestFlags.Players, host.Scene, ref r)
                                && !MphRead.Formats.CollisionDetection.CheckBetweenPoints(q + OpenTK.Mathematics.Vector3.UnitY * 0.8f, q + OpenTK.Mathematics.Vector3.UnitY * 4f, MphRead.Formats.TestFlags.Players, host.Scene, ref r);
                        }
                        if (ok) { at = new OpenTK.Mathematics.Vector3(b.X, y0 + 0.5f, b.Z); dir = v; found = true; }
                    }
                    if (found) break;
                }
                if (!found) { Console.WriteLine($"  {h}: no flat run found in {arena}"); continue; }
                void Settle(OpenTK.Mathematics.Vector3 p)
                {
                    host.PlacePlayer(p + OpenTK.Mathematics.Vector3.UnitY * 0.2f, dir);
                    for (int k = 0; k < 4; k++) host.Step(idle);
                    for (int k = 0; k < 240 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++) host.Step(idle);
                    for (int k = 0; k < 30; k++) host.Step(idle);
                }
                // one jump (second press at the top when `twice`), moving forward from the start when `run`: the rise and, on
                // the way down, how far across the floor from the take-off she is as she passes each height above it
                (float Rise, float[] Across, int Frames, float Speed) Jump(bool twice, bool run)
                {
                    Settle(at);
                    var fwd = idle; fwd.Move = new System.Numerics.Vector2(0, 1);
                    if (run) for (int k = 0; k < 30; k++) host.Step(fwd);
                    float speed = me.Speed.Xz.Length;
                    OpenTK.Mathematics.Vector3 off = me.Position;
                    float y0 = off.Y, top = y0;
                    var across = new float[] { float.NaN, float.NaN, float.NaN, float.NaN, float.NaN };
                    int f = 0, secondAt = -1;
                    var move = run ? fwd : idle;
                    var press = move; press.Buttons = MphRecomp.Campaign.CampaignButtons.Jump;
                    for (; f < 400; f++)
                    {
                        if (twice && secondAt < 0 && f > 16 && me.Speed.Y < 0.05f) secondAt = f;
                        bool down = f < 8 || secondAt >= 0 && f < secondAt + 8;
                        host.Step(down ? press : move);
                        top = MathF.Max(top, me.Position.Y);
                        if (me.Speed.Y < 0)
                        {
                            for (int i = 0; i < across.Length; i++)
                                if (float.IsNaN(across[i]) && me.Position.Y <= y0 + i) across[i] = (me.Position - off).Xz.Length;
                        }
                        if (f > 10 && me.Flags1.TestFlag(PlayerFlags1.Standing)) break;
                    }
                    return (top - y0, across, f, speed);
                }
                var one = Jump(false, false); var two = Jump(true, false);
                var runOne = Jump(false, true); var runTwo = Jump(true, true);
                Console.WriteLine($"  {h,-8} (spawned {me.Hunter}, jump speed {Fixed.ToFloat(me.Values.JumpSpeed):0.000}) rise: jump {one.Rise:0.00} ({one.Frames} f), double {two.Rise:0.00} ({two.Frames} f); run speed {runOne.Speed:0.000}; across when back down to +0/+1/+2/+3/+4: "
                    + $"jump {string.Join("/", runOne.Across.Select(v => v.ToString("0.0")))}, double {string.Join("/", runTwo.Across.Select(v => v.ToString("0.0")))}");
            }
            // the Gravity Boost: from Pipeline's cave floor, a jump, then a second press at the top held
            {
                EchoesArena.Def def = EchoesArena.Find("Pipeline")!;
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, Hunter.Samus, 1, 0);
                settings.TimeLimitSeconds = 36000; settings.PointGoal = 999;
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                PlayerEntity me = PlayerEntity.Main;
                for (int k = 0; k < 600 && !me.LoadFlags.TestFlag(LoadFlags.Spawned); k++) host.Step(idle);
                var jump = idle; jump.Buttons = MphRecomp.Campaign.CampaignButtons.Jump;
                foreach (int hold in new[] { 8, 30, 60, 120, 240 })
                {
                    host.PlacePlayer(M(23.0f, -0.2f, -9.4f) + OpenTK.Mathematics.Vector3.UnitY * 0.2f, OpenTK.Mathematics.Vector3.UnitZ);
                    for (int k = 0; k < 240 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++) host.Step(idle);
                    for (int k = 0; k < 30; k++) host.Step(idle);
                    float y0 = me.Position.Y, top = y0; bool wet = me.EchoesSubmerged;
                    int f = 0, second = -1;
                    for (; f < 600; f++)
                    {
                        if (second < 0 && f > 16 && me.Speed.Y < 0.05f) second = f;
                        bool down = f < 8 || second >= 0 && f < second + hold;
                        host.Step(down ? jump : idle);
                        top = MathF.Max(top, me.Position.Y);
                        if (f > 10 && me.Flags1.TestFlag(PlayerFlags1.Standing)) break;
                    }
                    Console.WriteLine($"  boost (Samus, Pipeline cave floor y {y0:0.00}, submerged {wet}): second press held {hold,3} steps: rise {top - y0:0.00}, {f} steps");
                }
            }
        }

        static void JumpSweep(string? root)
        {
            if (root != null)
            {
                EchoesArena.ImportRoot = root;
                EchoesArena.RegisterImported(root);
            }
            else
            {
                EchoesArena.Register(Paths.FileSystem);
            }
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            var jump = idle; jump.Buttons = MphRecomp.Campaign.CampaignButtons.Jump;
            OpenTK.Mathematics.Vector3 M(float x, float y, float z) => new OpenTK.Mathematics.Vector3(x, z, -y) * EchoesArena.DefaultScale;
            int misses = 0;
            var runs = new List<(string, OpenTK.Mathematics.Vector3?, bool, Hunter)>();
            foreach (Hunter h in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel })
            {
                runs.Add(("CrossfireChaos", null, false, h));
            }
            runs.Add(("Pipeline", M(23.0f, -0.2f, -9.4f), true, Hunter.Samus));
            foreach ((string arena, OpenTK.Mathematics.Vector3? spot, bool wet, Hunter hunter) in runs)
            {
                EchoesArena.Def def = EchoesArena.Find(arena) ?? throw new ArgumentException(arena);
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, hunter, 1, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                settings.Crates = false;
                settings.Breakables = false;
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                PlayerEntity me = PlayerEntity.Main;
                for (int k = 0; k < 600 && !me.LoadFlags.TestFlag(LoadFlags.Spawned); k++) host.Step(idle);
                for (int k = 0; k < 60 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++) host.Step(idle);
                for (int k = 0; k < 30; k++) host.Step(idle);
                OpenTK.Mathematics.Vector3 at = spot ?? me.Position;
                float jumpSpeed = Fixed.ToFloat(me.Values.JumpSpeed);
                foreach (int hold1 in new[] { 4, 20 })
                {
                    var line = new System.Text.StringBuilder();
                    int arc = 0;
                    for (int d = hold1 + 1; d <= 90; d++)
                    {
                        host.PlacePlayer(at + OpenTK.Mathematics.Vector3.UnitY * 0.2f, OpenTK.Mathematics.Vector3.UnitZ);
                        for (int k = 0; k < 4; k++) host.Step(idle);
                        for (int k = 0; k < 240 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++) host.Step(idle);
                        for (int k = 0; k < 40; k++) host.Step(idle);
                        bool landed = false;
                        for (int k = 0; k < d; k++)
                        {
                            host.Step(k < hold1 ? jump : idle);
                            if (k > 4 && me.Flags1.TestFlag(PlayerFlags1.Standing)) landed = true;
                        }
                        if (landed)
                        {
                            line.Append('L');
                            continue;
                        }
                        arc = d;
                        float y0 = me.Position.Y, vBefore = me.Speed.Y, top = y0, kick = float.MinValue;
                        int press2 = wet ? 40 : 4;
                        for (int k = 0; k < press2; k++)
                        {
                            host.Step(jump);
                            kick = MathF.Max(kick, me.Speed.Y);
                            top = MathF.Max(top, me.Position.Y);
                        }
                        // fired: the double jump relaunches at jump speed; the boost lifts her well over where she was
                        bool fired = wet ? top - y0 > 0.5f : kick > jumpSpeed * 0.9f;
                        if (!fired)
                        {
                            misses++;
                            Console.WriteLine($"      miss: {arena} {hunter} first press {hold1} steps, second press {d} steps in: vy before {vBefore:0.000}, kick {kick:0.000}, rise {top - y0:0.00}, standing {me.Flags1.TestFlag(PlayerFlags1.Standing)}, usedJump {me.Flags1.TestFlag(PlayerFlags1.UsedJump)}, boosting {me.EchoesBoosting}");
                        }
                        line.Append(fired ? '+' : '.');
                    }
                    Console.WriteLine($"  {arena} {hunter} ({(wet ? "boost" : "double jump")}), first press {hold1,2} steps, second press at steps {hold1 + 1}..90: {line} (in the air to step {arc})");
                }
            }
            Console.WriteLine(misses == 0 ? "jumpsweep PASS" : $"jumpsweep FAIL ({misses} misses)");
        }

        // a recording's jump presses (owner 2026-10-09: "double jump still doesn't always fire", Sidehopper Station): plays
        // it as the app does, checking every step, and prints each press of the seat's jump with what the jump check saw
        // (standing, jump used, steps since standing) and what came of it: GROUND (a jump that keeps the double jump),
        // AIR (the one air jump), NOTHING (with why). A first jump of an air time that came out AIR = the double jump spent
        // on the first press, the usual cause of "the second press didn't fire"
        static void JumpLog(string path, int? seatArg, string? root, bool? ledge)
        {
            if (root != null)
            {
                EchoesArena.ImportRoot = root;
                EchoesArena.RegisterImported(root);
            }
            else
            {
                EchoesArena.Register(Paths.FileSystem);
            }
            var file = MphRecomp.Replay.ReplayFile.Load(path);
            int seat = seatArg ?? file.Settings.LocalSeat;
            Console.WriteLine($"{Path.GetFileName(path)}: {file.Settings.Mode} in {file.Settings.Arena}, {file.Steps.Count} steps, "
                + $"seat {seat}, recorded on {file.Platform} ({file.Build})");
            if (MphRecomp.Replay.ReplayPlayer.CheckBeforeStart(file) is string refused)
            {
                Console.WriteLine("REFUSED: " + refused);
                return;
            }
            var player = new MphRecomp.Replay.ReplayPlayer(file);
            using MphRecomp.Campaign.CampaignHost host = player.Start();
            if (player.CheckAfterStart() is string why)
            {
                Console.WriteLine("REFUSED: " + why);
                return;
            }
            if (ledge is bool l)
            {
                // ledge=0|1: the jump just after leaving the floor as before the fix / with it (a file recorded with the
                // other rule then differs from the first press it changes)
                PlayerEntity.HostEchoesLedgeJump = l && PlayerEntity.HostEchoesMoves;
            }
            Console.WriteLine($"Echoes moves {PlayerEntity.HostEchoesMoves}, ledge jump {PlayerEntity.HostEchoesLedgeJump}");
            var f = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            System.Reflection.FieldInfo tss = typeof(PlayerEntity).GetField("_timeSinceStanding", f)!;
            System.Reflection.FieldInfo lockMin = typeof(PlayerEntity).GetField("_jumpPadControlLockMin", f)!;
            bool prevDown = false;
            int presses = 0, nothing = 0, airFirst = 0, firstFail = -1;
            int jumpsThisAir = 0;
            string lastJump = "";
            while (!host.Ended && player.Position < file.Steps.Count)
            {
                int step = player.Position;
                MphRecomp.Campaign.CampaignInput input = file.Steps[step].Input;
                bool down = seat == file.Settings.LocalSeat && input.Buttons.HasFlag(MphRecomp.Campaign.CampaignButtons.Jump);
                PlayerEntity me = PlayerEntity.Players[seat];
                bool pressed = down && !prevDown;
                prevDown = down;
                bool standing = me.Flags1.TestFlag(PlayerFlags1.Standing), used = me.Flags1.TestFlag(PlayerFlags1.UsedJump);
                bool alt = me.IsAltForm || me.IsMorphing, wet = me.EchoesSubmerged;
                int since = (ushort)tss.GetValue(me)!, lockSteps = (ushort)lockMin.GetValue(me)!;
                float vy0 = me.Speed.Y;
                OpenTK.Mathematics.Vector3 at = me.Position;
                player.BeforeStep();
                host.Step(input);
                if (!player.Check(host.Scene) && firstFail < 0)
                {
                    firstFail = step;
                    Console.WriteLine($"  step {step}: DIFFERS from the recording ({player.Failure}); lines after this may be wrong");
                }
                float jumpSpeed = me.IsPrimeHunter ? 0.35f : Fixed.ToFloat(me.Values.JumpSpeed);
                if (standing) jumpsThisAir = 0;
                if (!pressed) continue;
                presses++;
                bool fired = !alt && MathF.Abs(me.Speed.Y - jumpSpeed) < 0.02f && MathF.Abs(vy0 - jumpSpeed) > 0.001f;
                string what;
                if (fired)
                {
                    bool air = me.Flags1.TestFlag(PlayerFlags1.UsedJump); // a ground jump keeps the double jump
                    what = air ? "AIR" : "GROUND";
                    if (air && jumpsThisAir == 0)
                    {
                        airFirst++;
                        what += " (first jump of this air time: the double jump spent on it)";
                    }
                    jumpsThisAir++;
                    lastJump = $"{step / 60f:0.00} s";
                }
                else
                {
                    nothing++;
                    what = "NOTHING: " + (alt ? "alt form" : lockSteps > 0 ? $"jump pad lock {lockSteps}" : used ? "jump already used"
                        : wet ? "under water (boost press)" : "?");
                }
                Console.WriteLine($"  step {step,6} ({step / 60f,7:0.00} s, {(step - file.Steps.Count) / 60f,7:0.00} s from the end): {what}; "
                    + $"standing {standing}, used {used}, steps since standing {since}, vy {vy0:0.000} -> {me.Speed.Y:0.000}, "
                    + $"pos ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00})");
            }
            Console.WriteLine($"{presses} presses: {nothing} did nothing, {airFirst} first jumps came out as the air jump; "
                + (firstFail < 0 ? $"exact to step {player.Position}" : $"differs from step {firstFail}"));
        }

        // a drop off the floor without a jump (she's lifted 0.6 and falls), the first press 1..24 steps into it, the second
        // 12 steps later: '+' = the second press relaunched her (the first was a ground jump), '.' = it did nothing, 'L' =
        // she landed first. With the ledge jump the first ~15 steps (MPH's Grounded) are '+'; before it every one was '.'
        static void LedgeTest(bool ledge)
        {
            EchoesArena.Register(Paths.FileSystem);
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            var jump = idle; jump.Buttons = MphRecomp.Campaign.CampaignButtons.Jump;
            int fails = 0;
            foreach (Hunter hunter in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel })
            {
                EchoesArena.Def def = EchoesArena.Find("CrossfireChaos") ?? throw new ArgumentException("CrossfireChaos");
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, hunter, 1, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                settings.Crates = false;
                settings.Breakables = false;
                settings.EchoesLedgeJump = ledge;
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                PlayerEntity me = PlayerEntity.Main;
                for (int k = 0; k < 600 && !me.LoadFlags.TestFlag(LoadFlags.Spawned); k++) host.Step(idle);
                for (int k = 0; k < 60 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++) host.Step(idle);
                for (int k = 0; k < 30; k++) host.Step(idle);
                OpenTK.Mathematics.Vector3 at = me.Position;
                float jumpSpeed = me.IsPrimeHunter ? 0.35f : Fixed.ToFloat(me.Values.JumpSpeed);
                var line = new System.Text.StringBuilder();
                int lastPlus = 0;
                for (int d = 1; d <= 24; d++)
                {
                    host.PlacePlayer(at + OpenTK.Mathematics.Vector3.UnitY * 0.2f, OpenTK.Mathematics.Vector3.UnitZ);
                    for (int k = 0; k < 4; k++) host.Step(idle);
                    for (int k = 0; k < 240 && !me.Flags1.TestFlag(PlayerFlags1.Standing); k++) host.Step(idle);
                    for (int k = 0; k < 40; k++) host.Step(idle);
                    host.PlacePlayer(at + OpenTK.Mathematics.Vector3.UnitY * 0.6f, OpenTK.Mathematics.Vector3.UnitZ);
                    bool landed = false;
                    for (int k = 0; k < d; k++)
                    {
                        host.Step(idle);
                        if (k > 0 && me.Flags1.TestFlag(PlayerFlags1.Standing)) landed = true;
                    }
                    for (int k = 0; k < 4; k++) host.Step(jump);
                    for (int k = 0; k < 8; k++) host.Step(idle);
                    float kick = float.MinValue;
                    for (int k = 0; k < 4; k++)
                    {
                        host.Step(jump);
                        kick = MathF.Max(kick, me.Speed.Y);
                        if (me.Flags1.TestFlag(PlayerFlags1.Standing)) landed = true;
                    }
                    bool fired = kick > jumpSpeed * 0.9f;
                    line.Append(landed ? 'L' : fired ? '+' : '.');
                    if (fired && !landed) lastPlus = d;
                }
                // with the ledge jump: '+' from the first step; without it: none
                bool ok = ledge ? line[0] == '+' && lastPlus >= 10 : !line.ToString().Contains('+');
                if (!ok) fails++;
                Console.WriteLine($"  {hunter,-6} first press 1..24 steps after leaving the floor: {line} {(ok ? "ok" : "FAIL")}");
            }
            Console.WriteLine(fails == 0 ? $"ledgetest PASS (ledge jump {(ledge ? "on" : "off")})" : $"ledgetest FAIL ({fails})");
        }

        // how often the bots, running about an arena, leave the ground for a step or few without jumping (a slope, a seam, a
        // step down): a jump pressed then is the air jump, so the double jump is gone (owner 2026-10-09, Sidehopper Station)
        static void Flicker(string arena, int seconds, int bots)
        {
            EchoesArena.Register(Paths.FileSystem);
            EchoesArena.Def def = EchoesArena.Find(arena) ?? throw new ArgumentException($"no Echoes arena {arena}");
            var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, Hunter.Samus, bots, 2);
            settings.TimeLimitSeconds = seconds + 30;
            settings.PointGoal = 999;
            using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
            var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            int seats = settings.Players.Count;
            var air = new int[seats];
            var jumped = new bool[seats];
            var from = new OpenTK.Mathematics.Vector3[seats];
            var lengths = new int[21];
            var spots = new Dictionary<(int, int, int), int>();
            long ground = 0, flickerSteps = 0;
            int flickers = 0;
            for (int f = 1; f <= seconds * 60 && !host.Ended; f++)
            {
                host.Step(input);
                for (int i = 0; i < seats; i++)
                {
                    if (!settings.Players[i].Bot) continue;
                    PlayerEntity p = PlayerEntity.Players[i];
                    if (!p.LoadFlags.TestFlag(LoadFlags.Active) || p.Health <= 0 || p.IsAltForm || p.IsMorphing)
                    {
                        air[i] = 0;
                        jumped[i] = true;
                        continue;
                    }
                    if (p.Flags1.TestFlag(PlayerFlags1.Standing))
                    {
                        if (air[i] > 0 && air[i] <= 20 && !jumped[i])
                        {
                            flickers++;
                            flickerSteps += air[i];
                            lengths[air[i]]++;
                            var key = ((int)MathF.Round(from[i].X / 4) * 4, (int)MathF.Round(from[i].Y / 4) * 4, (int)MathF.Round(from[i].Z / 4) * 4);
                            spots[key] = spots.GetValueOrDefault(key) + 1;
                        }
                        air[i] = 0;
                        jumped[i] = false;
                        ground++;
                        continue;
                    }
                    if (air[i] == 0) from[i] = p.Position;
                    air[i]++;
                    if (p.Speed.Y > 0.15f || p.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)) jumped[i] = true;
                }
            }
            Console.WriteLine($"{def.InGameName}: {bots} bots, {seconds} s: {flickers} short drops off the ground without a jump "
                + $"({flickers * 60f / seconds / bots:0.0} a minute per bot), {flickerSteps} steps of them vs {ground} standing steps "
                + $"(a press at a random grounded moment lands in one {100.0 * flickerSteps / Math.Max(1, ground + flickerSteps):0.0}% of the time)");
            Console.WriteLine("  length in steps: " + string.Join(", ", Enumerable.Range(1, 20).Where(n => lengths[n] > 0).Select(n => $"{n}: {lengths[n]}")));
            // a grace of g steps after leaving the ground: the grounded moments still lost (steps past g of longer drops)
            Console.WriteLine("  still lost with a grace of: " + string.Join(", ", new[] { 0, 3, 6, 8, 10, 16 }.Select(g =>
                $"{g} steps {100.0 * Enumerable.Range(g + 1, 20 - g).Sum(n => (long)lengths[n] * (n - g)) / Math.Max(1, ground + flickerSteps):0.00}%")));
            foreach (var kv in spots.OrderByDescending(kv => kv.Value).Take(12))
            {
                Console.WriteLine($"  around ({kv.Key.Item1}, {kv.Key.Item2}, {kv.Key.Item3}): {kv.Value}");
            }
        }

        static void BreakTest(string? only, string? root)
        {
            if (root != null)
            {
                EchoesArena.ImportRoot = root;
                EchoesArena.RegisterImported(root);
            }
            else
            {
                EchoesArena.Register(Paths.FileSystem);
            }
            int fails = 0;
            void Check(bool ok, string what)
            {
                if (!ok) fails++;
                Console.WriteLine($"    {(ok ? "ok  " : "FAIL")} {what}");
            }
            Console.WriteLine("  damage rule:");
            Check(EchoesBreakables.Damage(BeamType.PowerBeam, 6, false) == 6, "Power Beam uncharged = 6 (5 per grate, 20 per wall)");
            Check(EchoesBreakables.Damage(BeamType.PowerBeam, 36, true) == 90, "Power Beam full charge = 90 (1 per grate, 2 per wall)");
            Check(EchoesBreakables.Damage(BeamType.PowerBeam, 6, true) == 6, "Power Beam least charge = 6");
            Check(EchoesBreakables.Damage(BeamType.Missile, 32, false) == 120 && EchoesBreakables.Damage(BeamType.Missile, 48, true) == 120, "any missile = 120 (1 per grate, 1 per wall)");
            Check(EchoesBreakables.Damage(BeamType.Imperialist, 72, false) == 72, "other weapons: their MPH damage (Imperialist 72)");
            List<T> All<T>(MphRecomp.Campaign.CampaignHost h) where T : EnemyInstanceEntity
            {
                var list = new List<T>();
                foreach (EnemyInstanceEntity e in h.Scene.GetEnemyInstanceEntities()) if (e is T t) list.Add(t);
                return list;
            }
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            MphRecomp.Campaign.CampaignInput Press(MphRecomp.Campaign.CampaignButtons b) { var i = idle; i.Buttons = b; return i; }
            foreach (EchoesArena.Def d in EchoesArena.All)
            {
                if (only != null && EchoesArena.Find(only) != d) continue;
                if (Metadata.GetRoomByName(d.Room).Item1 == null) continue;
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, 1, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                settings.Crates = false;
                using (MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings))
                {
                    if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                    List<EchoesBreakableEntity> breaks = All<EchoesBreakableEntity>(host);
                    List<EchoesFluidEntity> fluids = All<EchoesFluidEntity>(host);
                    if (breaks.Count == 0 && fluids.Count == 0) continue;
                    Console.WriteLine($"  {d.InGameName}: {breaks.Count} breakables, {fluids.Count} pool entities");
                    PlayerEntity me = PlayerEntity.Main;
                    // a match spawns its players a moment after the load
                    for (int k = 0; k < 600 && !me.LoadFlags.TestFlag(LoadFlags.Spawned); k++) host.Step(idle);
                    for (int k = 0; k < 30; k++) host.Step(idle);
                    Check(me.LoadFlags.TestFlag(LoadFlags.Spawned) && me.Health > 0, $"the player spawned (step {host.Frame})");
                    OpenTK.Mathematics.Vector3 start = me.Position;
                    EchoesBreakableEntity? grate = breaks.FirstOrDefault(b => b.FullHp == EchoesBreakables.GrateHealth);
                    if (grate != null)
                    {
                        var top = new OpenTK.Mathematics.Vector3((grate.Min.X + grate.Max.X) / 2, grate.Max.Y + 0.5f, (grate.Min.Z + grate.Max.Z) / 2);
                        host.PlacePlayer(top + OpenTK.Mathematics.Vector3.UnitY * 0.3f, OpenTK.Mathematics.Vector3.UnitZ);
                        for (int k = 0; k < 40; k++) host.Step(idle);
                        Check(MathF.Abs(me.Position.Y - top.Y) < 0.1f && me.Flags1.TestFlag(PlayerFlags1.Standing),
                            $"stands on the grate (y {me.Position.Y:0.00}, top {grate.Max.Y:0.00})");
                        for (int i = 0; i < 4; i++) grate.Hit(EchoesBreakables.Damage(BeamType.PowerBeam, 6, false));
                        Check(grate.Up && grate.Hp == 6, $"4 Power Beam shots: still up (health {grate.Hp})");
                        grate.Hit(EchoesBreakables.Damage(BeamType.PowerBeam, 6, false));
                        long broke = host.Frame;
                        Check(!grate.Up && grate.Breaks == 1, "the 5th breaks it");
                        for (int k = 0; k < 60; k++) host.Step(idle);
                        // its feet: half a unit under its position
                        Check(me.Position.Y - 0.5f < grate.Min.Y, $"the player fell through (feet y {me.Position.Y - 0.5f:0.00}, grate bottom {grate.Min.Y:0.00})");
                        if (fluids.Count > 0)
                        {
                            EchoesFluidEntity pool = fluids[0];
                            Check(pool.InPool(me), "the player is in the Dark Water");
                            int h0 = me.Health, n0 = pool.Hurts;
                            for (int k = 0; k < 240; k++) host.Step(idle);
                            int lost = h0 - me.Health;
                            Check(lost >= 28 && lost <= 32, $"Dark Water: {lost} energy in 4 s (MPH acid: 30), {pool.Hurts - n0} hurts");
                            // still inside the grate's box: it waits
                            Check(!grate.Up, $"not back while the player is inside it ({host.Frame - broke} steps)");
                        }
                        host.PlacePlayer(start, OpenTK.Mathematics.Vector3.UnitZ);
                        long back = -1;
                        for (int k = 0; k < EchoesBreakables.GrateRespawn + 60 && back < 0; k++)
                        {
                            host.Step(idle);
                            if (grate.Up) back = host.Frame - broke;
                        }
                        Check(back >= EchoesBreakables.GrateRespawn - 2, $"back after {back} steps (10 s = {EchoesBreakables.GrateRespawn}, later if occupied), health {grate.Hp}");
                        // a morph ball bomb on it
                        host.PlacePlayer(top + OpenTK.Mathematics.Vector3.UnitY * 0.3f, OpenTK.Mathematics.Vector3.UnitZ);
                        for (int k = 0; k < 30; k++) host.Step(idle);
                        // presses held a few steps, as the other tests' (CampaignDoorProbe)
                        for (int k = 0; k < 120 && !me.IsAltForm; k++) host.Step(k < 4 ? Press(MphRecomp.Campaign.CampaignButtons.Morph) : idle);
                        for (int k = 0; k < 20; k++) host.Step(idle);
                        bool ball = me.IsAltForm;
                        for (int k = 0; k < 4; k++) host.Step(Press(MphRecomp.Campaign.CampaignButtons.AltAttack));
                        for (int k = 0; k < 120 && grate.Breaks < 2; k++) host.Step(idle);
                        Check(ball && grate.Breaks == 2, $"a morph ball bomb breaks it (ball {ball}, breaks {grate.Breaks})");
                        for (int k = 0; k < 120 && me.IsAltForm; k++) host.Step(k < 4 ? Press(MphRecomp.Campaign.CampaignButtons.Morph) : idle);
                        host.PlacePlayer(start, OpenTK.Mathematics.Vector3.UnitZ);
                        for (int k = 0; k < 30; k++) host.Step(idle);
                    }
                    EchoesBreakableEntity? wall = breaks.FirstOrDefault(b => b.FullHp == EchoesBreakables.WallHealth);
                    if (wall != null)
                    {
                        // shoot it from 3 units off its broad side, both sides, until the shots land
                        var c = (wall.Min + wall.Max) / 2;
                        bool alongX = wall.Max.X - wall.Min.X < wall.Max.Z - wall.Min.Z;
                        int shots = 0;
                        for (int side = 0; side < 2 && wall.Breaks == 0; side++)
                        {
                            var away = alongX ? new OpenTK.Mathematics.Vector3(side == 0 ? 1 : -1, 0, 0) : new OpenTK.Mathematics.Vector3(0, 0, side == 0 ? 1 : -1);
                            var face = alongX ? (side == 0 ? wall.Max.X : wall.Min.X) : (side == 0 ? wall.Max.Z : wall.Min.Z);
                            var stand = alongX ? new OpenTK.Mathematics.Vector3(face + 3, wall.Min.Y + 0.6f, c.Z) : new OpenTK.Mathematics.Vector3(c.X, wall.Min.Y + 0.6f, face - (side == 0 ? -3 : 3));
                            host.PlacePlayer(stand, -away);
                            for (int k = 0; k < 30; k++) host.Step(idle);
                            int before = wall.Hp;
                            for (int tap = 0; tap < 26 && wall.Breaks == 0; tap++)
                            {
                                host.Step(Press(MphRecomp.Campaign.CampaignButtons.Shoot));
                                shots++;
                                for (int k = 0; k < 11; k++) host.Step(idle);
                            }
                            Console.WriteLine($"    side {side}: health {before} -> {wall.Hp}, breaks {wall.Breaks}, at ({me.Position.X:0.0}, {me.Position.Y:0.0}, {me.Position.Z:0.0})");
                        }
                        Check(wall.Breaks == 1, $"wall broke after {shots} shots (health {wall.FullHp}: 20 Power Beam shots)");
                        for (int k = 0; k < 1200; k++) host.Step(idle);
                        Check(!wall.Up && wall.EntityCollision[0]?.Collision.Active == false, "the wall stays broken (20 s later)");
                    }
                }
                settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, 1, 0);
                settings.Breakables = false;
                using (MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings))
                {
                    Check(All<EchoesBreakableEntity>(host).Count == 0 && All<EchoesFluidEntity>(host).Count == 0, "Breakables off (an old replay): none");
                }
            }
            {
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, "MP3 PROVING GROUND", Hunter.Samus, 1, 0);
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                Console.WriteLine("  Combat Hall (MPH):");
                Check(All<EchoesBreakableEntity>(host).Count == 0, "an MPH arena: no breakables");
            }
            Console.WriteLine(fails == 0 ? "  breaktest: PASS" : $"  breaktest: {fails} FAIL");
        }

        static string? Arg(string[] args, string key) =>
            args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..];
    }
}
