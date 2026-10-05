using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Frontend;
using SixLabors.ImageSharp;

// -femap: the pause screen's area map (Core Frontend/AreaMapModel + PauseMap).
//   -femap info            every map model: materials, and each room/connector part (room id, triangles, centre, bounds)
//   -femap detail          the DETAILED look per map model: each part's collision polygons, connector matches, load time
//   -femap render ...      a PNG per "shot" (see Render); env FE_DETAILED=1 (the collision look), FE_PRIME=1 (Prime mode),
//                          FE_CONS (Prime: connectors walked through, "all" or numbers 1,2,..), FE_AREAS/ARTIFACTS/...
namespace MphRead
{
    internal static class MapTool
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string mode = args.Length >= 2 ? args[1] : "info";
            if (mode == "info") Info();
            else if (mode == "nodes") Nodes(args.Length >= 3 ? args[2] : "unit2_1");
            else if (mode == "render") Render(args);
            else if (mode == "detail") Detail();
            else if (mode == "doors") Doors(args.Length >= 3 ? args[2] : "UNIT2_LAND");
            else if (mode == "savetest") SaveTest();
        }

        // -femap render [room] [visited rooms, comma-separated, or "all"] [script "f:input,..."]
        //   inputs: zin/zout (L/R for 20 frames), pan<dir> (the pan stick 20 frames), turn<dir> (the turn stick 20 frames),
        //   drag@dx (a stylus drag of dx canvas units over 6 frames), hop<dir> (Prime: a D-pad press), centre (A), home (X),
        //   look (Y: the DETAILED look on/off), tap@x;y (a tap at DS-canvas x, y), shot (also prints the 3D build time)
        //   defaults: UNIT2_LAND, nothing else visited, Samus where the vanilla state has her (BizHawk revisit_CA_exited)
        private static void Render(string[] args)
        {
            string room = args.Length >= 3 ? args[2] : "UNIT2_LAND";
            string visitedArg = args.Length >= 4 ? args[3] : "";
            var visited = new HashSet<int>();
            foreach (string name in visitedArg.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (name == "all") { for (int i = 27; i <= 92; i++) visited.Add(i); continue; }
                (RoomMetadata? meta, int id) = Metadata.GetRoomByName(name.ToUpperInvariant());
                if (meta != null) visited.Add(id);
            }
            (RoomMetadata? own, int ownId) = Metadata.GetRoomByName(room);
            if (own != null) visited.Add(ownId);
            var map = new PauseMap(Paths.FileSystem, new UiTextureCache());
            map.Prime = Environment.GetEnvironmentVariable("FE_PRIME") == "1";
            map.SetDetailed(Environment.GetEnvironmentVariable("FE_DETAILED") == "1");
            uint[] cons = new uint[8];
            string consArg = Environment.GetEnvironmentVariable("FE_CONS") ?? "";
            int pairOf = AreaMapModel.ForRoom(Paths.FileSystem, room) is AreaMapModel own0 ? (AreaMapModel.UnitHalf(own0.File).Unit - 1) * 2 : 0;
            foreach (string c in consArg.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (c == "all") { for (int n = 1; n <= 64; n++) AreaMapIndex.Set(cons, pairOf, n); }
                else if (Int32.TryParse(c, out int n)) AreaMapIndex.Set(cons, pairOf, n);
            }
            var log = new System.Collections.Generic.List<string>();
            map.Log = m => log.Add(m);
            map.PlaySound = id => log.Add($"sound 0x{id:x}");
            map.Quit = () => log.Add("QUIT");
            int weapons = Int32.Parse(Environment.GetEnvironmentVariable("FE_WEAPONS") ?? "0", System.Globalization.NumberStyles.HexNumber);
            int areas = Int32.Parse(Environment.GetEnvironmentVariable("FE_AREAS") ?? "c", System.Globalization.NumberStyles.HexNumber);
            uint artifacts = UInt32.Parse(Environment.GetEnvironmentVariable("FE_ARTIFACTS") ?? "0", System.Globalization.NumberStyles.HexNumber);
            int octoliths = Int32.Parse(Environment.GetEnvironmentVariable("FE_OCTOLITHS") ?? "0", System.Globalization.NumberStyles.HexNumber);
            uint lost = UInt32.Parse(Environment.GetEnvironmentVariable("FE_LOST") ?? "ffffffff", System.Globalization.NumberStyles.HexNumber);
            map.Open(new PauseMap.Info
            {
                Room = room,
                Title = own?.InGameName ?? room,
                Position = new OpenTK.Mathematics.Vector3(-13.2f, 7.06f, 0.03f),
                Facing = -OpenTK.Mathematics.Vector3.UnitX,
                Visited = id => visited.Contains(id),
                Areas = areas,
                Artifacts = artifacts,
                Octoliths = octoliths,
                LostOctoliths = lost,
                Weapons = weapons,
                Connectors = cons,
                BossFlags = Int32.Parse(Environment.GetEnvironmentVariable("FE_BOSS") ?? "0", System.Globalization.NumberStyles.HexNumber)
            });
            string script = args.Length >= 5 ? args[4] : "60:shot";
            var steps = script.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split(':')).Select(p => (Frame: Int32.Parse(p[0]), Input: p[1])).ToList();
            const int W = 1920, H = 1080;
            int end = steps.Max(s => s.Frame);
            int shot = 0;
            float px = 0, py = 0, tx = 0, ty = 0;
            bool zin = false, zout = false;
            var press = new PauseMap.Input();
            int hold = 0;
            (int Frame, float Dx)? drag = null;
            for (int f = 0; f <= end; f++)
            {
                foreach (var st in steps.Where(s => s.Frame == f))
                {
                    string inp = st.Input.ToLowerInvariant();
                    if (inp == "shot")
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        map.Build(W, H);
                        log.Add($"{f}: build {watch.Elapsed.TotalMilliseconds:0.0} ms, {map.DrawList.Vertices.Count / 3} triangles");
                        using var img = FrontendTool.RasterCanvas(map.DrawList, W, H);
                        string path = Path.Combine(OutDir(), $"map_{room.ToLowerInvariant()}_{++shot}.png");
                        img.SaveAsPng(path);
                        log.Add($"{f}: shot -> {path}");
                        continue;
                    }
                    if (inp == "bench")
                    {
                        // 240 frames of turning, built every frame: the steady-state 3D rebuild cost
                        var times = new List<double>();
                        for (int b = 0; b < 240; b++)
                        {
                            map.Tick(new PauseMap.Input { TurnX = 1 });
                            var bw = System.Diagnostics.Stopwatch.StartNew();
                            map.Build(W, H);
                            times.Add(bw.Elapsed.TotalMilliseconds);
                        }
                        var last = times.Skip(120).ToList();
                        log.Add($"{f}: bench: build avg {last.Average():0.00} ms, max {last.Max():0.00} ms over the last 120 frames, {map.DrawList.Vertices.Count / 3} triangles");
                        continue;
                    }
                    if (inp.StartsWith("hop")) { (float hx, float hy) = Dir(inp[3..]); press.HopX = (int)hx; press.HopY = (int)hy; continue; }
                    if (inp == "centre") { press.Centre = true; continue; }
                    if (inp == "home") { press.Home = true; continue; }
                    if (inp == "look") { press.ToggleLook = true; continue; }
                    if (inp.StartsWith("tap@"))
                    {
                        string[] xy = inp[4..].Split(';');
                        map.Build(W, H);
                        float tapX = Single.Parse(xy[0]) * (H / 192f), tapY = Single.Parse(xy[1]) * (H / 192f);
                        map.Touch(tapX, tapY, 0);
                        map.Touch(tapX, tapY, 2);
                        continue;
                    }
                    hold = 20;
                    px = py = tx = ty = 0;
                    zin = inp == "zin";
                    zout = inp == "zout";
                    if (inp.StartsWith("pan")) (px, py) = Dir(inp[3..]);
                    if (inp.StartsWith("turn")) (tx, ty) = Dir(inp[4..]);
                    if (inp.StartsWith("drag@"))
                    {
                        drag = (f, Single.Parse(inp[5..]));
                        map.Build(W, H);
                        map.Touch(W / 2f, H / 2f, 0);
                        hold = 0;
                    }
                }
                if (drag is (int df, float dx) && f - df <= 6)
                {
                    map.Touch(W / 2f + dx * (H / 192f) * (f - df) / 6, H / 2f, f - df == 6 ? 2 : 1);
                }
                bool held = hold-- > 0;
                press.PanX = held ? px : 0;
                press.PanY = held ? py : 0;
                press.TurnX = held ? tx : 0;
                press.TurnY = held ? ty : 0;
                press.ZoomIn = held && zin;
                press.ZoomOut = held && zout;
                map.Tick(press);
                press = new PauseMap.Input();
            }
            foreach (string line in log) Console.WriteLine(line);
        }

        // -femap doors <room>: every door entity (all layers): position, facing, connector, palette, type, target
        private static void Doors(string room)
        {
            (RoomMetadata? meta, _) = Metadata.GetRoomByName(room.ToUpperInvariant());
            if (meta?.EntityPath == null) return;
            foreach (Entity e in Read.GetEntities(meta.EntityPath, -1, firstHunt: false))
            {
                if (e is not Entity<DoorEntityData> d) continue;
                Console.WriteLine($"{room} door {e.EntityId,3} layer {e.LayerMask:x4} pos ({e.Position.X:0.00}, {e.Position.Y:0.00}, {e.Position.Z:0.00}) facing ({e.FacingVector.X:0.00}, {e.FacingVector.Y:0.00}, {e.FacingVector.Z:0.00})"
                    + $" connector {d.Data.ConnectorId} palette {d.Data.PaletteId} type {d.Data.DoorType} locked {d.Data.Locked} room {d.Data.RoomName.MarshalString()}");
            }
        }

        // -femap savetest: the walked-through connectors survive a campaign save round trip; old saves read as null
        private static void SaveTest()
        {
            var file = new MphRecomp.Campaign.CampaignSaveFile { Story = new StorySave(), MapConnectors = new uint[] { 1, 0, 0x80000005, 0, 7, 0, 0, 0xFFFFFFFF } };
            var back = MphRecomp.Campaign.CampaignSaves.FromJson(MphRecomp.Campaign.CampaignSaves.ToJson(file));
            bool same = back.MapConnectors != null && back.MapConnectors.SequenceEqual(file.MapConnectors);
            Console.WriteLine($"[{(same ? "PASS" : "FAIL")}] MapConnectors round trip: {String.Join(",", back.MapConnectors ?? Array.Empty<uint>())}");
            var old = MphRecomp.Campaign.CampaignSaves.FromJson(MphRecomp.Campaign.CampaignSaves.ToJson(new MphRecomp.Campaign.CampaignSaveFile { Story = new StorySave() }));
            Console.WriteLine($"[{(old.MapConnectors == null ? "PASS" : "FAIL")}] a save without the field reads as null (seeded on first use)");
            uint[] bits = new uint[8];
            bool set = AreaMapIndex.Set(bits, 2, 9) && AreaMapIndex.Has(bits, 2, 9) && !AreaMapIndex.Has(bits, 2, 8) && !AreaMapIndex.Set(bits, 2, 9)
                && AreaMapIndex.Set(bits, 4, 40) && bits[5] == 1u << 7;
            Console.WriteLine($"[{(set ? "PASS" : "FAIL")}] connector bits: word (area & ~1) + (n > 32), bit n - 1");
        }

        private static void Detail()
        {
            foreach (string file in AreaMapModel.FindFiles(Paths.FileSystem))
            {
                AreaMapModel map = AreaMapModel.Load(file);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var log = new List<string>();
                map.LoadDetail(log.Add);
                Console.WriteLine($"== {file}: detail loaded in {watch.ElapsedMilliseconds} ms");
                foreach (AreaMapModel.Part p in map.Parts)
                {
                    MapGeometry? d = p.Detail;
                    string what = d == null ? "(map model)" : $"{d.Polys.Count,5} polys {d.Points.Count,5} points, bounds ({d.Min.X:0.0}, {d.Min.Y:0.0}, {d.Min.Z:0.0})-({d.Max.X:0.0}, {d.Max.Y:0.0}, {d.Max.Z:0.0})";
                    Console.WriteLine($"   {p.Name,-14} nav ({p.Min.X:0.0}, {p.Min.Y:0.0}, {p.Min.Z:0.0})-({p.Max.X:0.0}, {p.Max.Y:0.0}, {p.Max.Z:0.0})  {what}");
                }
                foreach (string line in log) Console.WriteLine("   " + line);
            }
        }

        private static (float, float) Dir(string d) => d switch { "up" => (0, 1), "down" => (0, -1), "left" => (-1, 0), "right" => (1, 0), _ => (0, 0) };

        private static string OutDir()
        {
            string dir = Path.Combine(Paths.FileSystem, "..", "..", "extract_out", "frontend");
            string? env = Environment.GetEnvironmentVariable("FE_OUT");
            if (env != null) dir = env;
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Nodes(string which)
        {
            string? file = AreaMapModel.FindFiles(Paths.FileSystem).FirstOrDefault(f => f.StartsWith(which, StringComparison.OrdinalIgnoreCase));
            if (file == null) return;
            AreaMapModel map = AreaMapModel.Load(file);
            Model m = map.Model;
            for (int i = 0; i < m.Nodes.Count; i++)
            {
                Node n = m.Nodes[i];
                var t = n.Transform.ExtractTranslation();
                Console.WriteLine($"{i,3} {n.Name,-16} parent {n.ParentIndex,3} pos ({n.Position.X:0.###}, {n.Position.Y:0.###}, {n.Position.Z:0.###}) scale ({n.Scale.X:0.###}) angle ({n.Angle.X:0.###},{n.Angle.Y:0.###},{n.Angle.Z:0.###})"
                    + $" world ({t.X:0.###}, {t.Y:0.###}, {t.Z:0.###}) meshes {n.MeshCount}");
            }
        }

        private static void Info()
        {
            foreach (string file in AreaMapModel.FindFiles(Paths.FileSystem))
            {
                AreaMapModel map = AreaMapModel.Load(file);
                Model m = map.Model;
                Console.WriteLine($"== {file}: {m.Nodes.Count} nodes, {m.Meshes.Count} meshes, scale {m.Scale.X}, {map.Parts.Count} parts");
                for (int i = 0; i < m.Materials.Count; i++)
                {
                    Material mat = m.Materials[i];
                    Console.WriteLine($"   material {i} {mat.Name}: diffuse {mat.Diffuse.Red},{mat.Diffuse.Green},{mat.Diffuse.Blue} ambient {mat.Ambient.Red},{mat.Ambient.Green},{mat.Ambient.Blue}"
                        + $" specular {mat.Specular.Red},{mat.Specular.Green},{mat.Specular.Blue} alpha {mat.Alpha} lighting {mat.Lighting} poly {mat.PolygonMode} render {mat.RenderMode} cull {mat.Culling} tex {mat.TextureId} wire {mat.Wireframe}");
                }
                foreach (AreaMapModel.Part p in map.Parts)
                {
                    string mats = String.Join(",", p.Nav.Polys.Select(q => q.Material).Distinct());
                    Console.WriteLine($"   {p.Name,-14} room {p.RoomId,3} tris {p.Nav.Polys.Count,4} mats {mats} centre ({p.Centre.X:0.0}, {p.Centre.Y:0.0}, {p.Centre.Z:0.0})"
                        + $" min ({p.Min.X:0.0}, {p.Min.Y:0.0}, {p.Min.Z:0.0}) max ({p.Max.X:0.0}, {p.Max.Y:0.0}, {p.Max.Z:0.0})");
                }
            }
        }
    }
}
