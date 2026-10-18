using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Frontend;
using SixLabors.ImageSharp;

// -primemap: PNGs of the PRIME map screen (Core Frontend/PrimeMap.cs, Prime 1's map) as the device draws it.
//   -primemap [room] [visited rooms, comma-separated, or "all"] [script "f:input,..."]
//   inputs (held ones for 20 frames, or name*frames): move<dir> (left stick), rot<dir> (right stick), zin / zout (R / L),
//   a (planet map / room map), y (panels), x (DETAILED look), b (close), up / down (D-pad: splits ON/ALL/OFF; menu rows),
//   report (L3/R3: the split report menu), tap@x;y (DS-canvas units), shot
//   env as -femap render: FE_CONS (connectors walked: "all" or numbers), FE_AREAS (planets found, hex, default 55),
//   FE_WEAPONS, FE_ARTIFACTS, FE_OCTOLITHS, FE_LOST, FE_BOSS, FE_DETAILED=1, FE_OUT; FE_DOORS=1 lists the door markers,
//   FE_PIECES=all|"Corridor A,..." (CorridorMap pieces entered), FE_POS=x,y,z (Samus in the room), FE_REPORTS=path
//   (where the report menu saves; default <out dir>/map_reports.json)
namespace MphRead
{
    internal static class PrimeMapTool
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string room = args.Length >= 2 ? args[1] : "UNIT2_LAND";
            string visitedArg = args.Length >= 3 ? args[2] : "";
            var visited = new HashSet<int>();
            foreach (string name in visitedArg.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (name == "all") { for (int i = 27; i <= 92; i++) visited.Add(i); continue; }
                (RoomMetadata? meta, int id) = Metadata.GetRoomByName(name.ToUpperInvariant());
                if (meta != null) visited.Add(id);
            }
            (RoomMetadata? own, int ownId) = Metadata.GetRoomByName(room);
            if (own != null) visited.Add(ownId);
            // FE_PIECES: the corridors / chambers split off rooms (CorridorMap) counted as entered: "all" or piece names
            string pieces = Environment.GetEnvironmentVariable("FE_PIECES") ?? "";
            foreach (CorridorMap.Piece piece in CorridorMap.Pieces)
            {
                if (pieces == "all" || pieces.Split(',').Contains(piece.Name, StringComparer.OrdinalIgnoreCase)) CorridorMap.MarkSeen(piece);
            }
            var map = new PrimeMap(Paths.FileSystem, new UiTextureCache());
            map.SetDetailed(Environment.GetEnvironmentVariable("FE_DETAILED") == "1");
            uint[] cons = new uint[8];
            int pairOf = AreaMapModel.ForRoom(Paths.FileSystem, room) is AreaMapModel own0 ? (AreaMapModel.UnitHalf(own0.File).Unit - 1) * 2 : 0;
            foreach (string c in (Environment.GetEnvironmentVariable("FE_CONS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (c == "all") { for (int n = 1; n <= 64; n++) AreaMapIndex.Set(cons, pairOf, n); }
                else if (Int32.TryParse(c, out int n)) AreaMapIndex.Set(cons, pairOf, n);
            }
            var log = new List<string>();
            map.Log = m => log.Add(m);
            map.PlaySound = id => log.Add($"sound 0x{id:x}");
            map.ReportPath = Environment.GetEnvironmentVariable("FE_REPORTS") ?? Path.Combine(OutDir(), "map_reports.json");
            static uint Hex(string env, string def) => UInt32.Parse(Environment.GetEnvironmentVariable(env) ?? def, System.Globalization.NumberStyles.HexNumber);
            map.Open(new PauseMap.Info
            {
                Room = room,
                Title = own?.InGameName ?? room,
                Position = SamusAt(),
                Facing = -OpenTK.Mathematics.Vector3.UnitX,
                Up = OpenTK.Mathematics.Vector3.UnitY,
                Visited = id => visited.Contains(id),
                Areas = (int)Hex("FE_AREAS", "55"),
                Artifacts = Hex("FE_ARTIFACTS", "0"),
                Octoliths = (int)Hex("FE_OCTOLITHS", "0"),
                LostOctoliths = Hex("FE_LOST", "ffffffff"),
                Weapons = (int)Hex("FE_WEAPONS", "0"),
                Connectors = cons,
                BossFlags = (int)Hex("FE_BOSS", "0")
            });
            if (Environment.GetEnvironmentVariable("FE_DOORS") == "1") DumpDoors(map, log);
            string script = args.Length >= 4 ? args[3] : "60:shot";
            var steps = script.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split(':')).Select(p => (Frame: Int32.Parse(p[0]), Input: p[1])).ToList();
            const int W = 1920, H = 1080;
            int end = steps.Max(s => s.Frame);
            int shot = 0, hold = 0;
            var held = new PrimeMap.Input();
            for (int f = 0; f <= end; f++)
            {
                var input = new PrimeMap.Input();
                foreach (var st in steps.Where(s => s.Frame == f))
                {
                    string inp = st.Input.ToLowerInvariant();
                    int frames = 20;
                    if (inp.Contains('*'))
                    {
                        frames = Int32.Parse(inp[(inp.IndexOf('*') + 1)..]);
                        inp = inp[..inp.IndexOf('*')];
                    }
                    if (inp == "shot")
                    {
                        map.Build(W, H);
                        using var img = FrontendTool.RasterCanvas(map.DrawList, W, H);
                        // the panels (drawn over the helmet on the device): their non-black pixels over the map
                        using var over = FrontendTool.RasterCanvas(map.Overlay, W, H);
                        img.ProcessPixelRows(over, (dst, src) =>
                        {
                            for (int y = 0; y < dst.Height; y++)
                            {
                                Span<SixLabors.ImageSharp.PixelFormats.Rgba32> d = dst.GetRowSpan(y), o = src.GetRowSpan(y);
                                for (int x = 0; x < d.Length; x++)
                                {
                                    if (o[x].R + o[x].G + o[x].B > 0) d[x] = o[x];
                                }
                            }
                        });
                        string path = Path.Combine(OutDir(), $"primemap_{room.ToLowerInvariant()}_{++shot}.png");
                        img.SaveAsPng(path);
                        log.Add($"{f}: shot -> {path} ({map.DrawList.Vertices.Count / 3} triangles)");
                        continue;
                    }
                    if (inp == "a") { input.World = true; continue; }
                    if (inp == "y") { input.Legend = true; continue; }
                    if (inp == "x") { input.ToggleLook = true; continue; }
                    if (inp == "b") { input.Close = true; continue; }
                    if (inp == "up") { input.Up = true; continue; }
                    if (inp == "down") { input.Down = true; continue; }
                    if (inp == "report") { input.Report = true; continue; }
                    if (inp.StartsWith("tap@"))
                    {
                        string[] xy = inp[4..].Split(';');
                        map.Build(W, H);
                        float tapX = Single.Parse(xy[0]) * (H / 192f), tapY = Single.Parse(xy[1]) * (H / 192f);
                        map.Touch(tapX, tapY, 0);
                        map.Touch(tapX, tapY, 2);
                        continue;
                    }
                    held = new PrimeMap.Input();
                    hold = frames;
                    if (inp == "zin") held.ZoomIn = true;
                    else if (inp == "zout") held.ZoomOut = true;
                    else if (inp.StartsWith("move")) (held.MoveX, held.MoveY) = Dir(inp[4..]);
                    else if (inp.StartsWith("rot")) (held.RotateX, held.RotateY) = Dir(inp[3..]);
                }
                if (hold-- > 0)
                {
                    input.MoveX = held.MoveX;
                    input.MoveY = held.MoveY;
                    input.RotateX = held.RotateX;
                    input.RotateY = held.RotateY;
                    input.ZoomIn = held.ZoomIn;
                    input.ZoomOut = held.ZoomOut;
                }
                map.Tick(input);
            }
            log.Add($"end: open {map.IsOpen}, blur {map.Blur:0.00}");
            foreach (string line in log) Console.WriteLine(line);
        }

        // FE_DOORS=1: the door model's extent, then each door against the shown parts' boxes (map space)
        private static void DumpDoors(PrimeMap map, List<string> log)
        {
            var f = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var door = PauseMap.SmallModel.Load("hud/Door_NAV_Model.bin", null);
            if (door != null)
            {
                var mn = door.Corners.Aggregate(OpenTK.Mathematics.Vector3.ComponentMin);
                var mx = door.Corners.Aggregate(OpenTK.Mathematics.Vector3.ComponentMax);
                log.Add($"door model: min {mn} max {mx}, scale {door.Model.Scale}");
            }
            var shown = (System.Collections.IList)typeof(PrimeMap).GetField("_shown", f)!.GetValue(map)!;
            var doors = (List<(OpenTK.Mathematics.Vector3 Pos, OpenTK.Mathematics.Matrix3 Rot, OpenTK.Mathematics.Vector3 Colour)>)typeof(PrimeMap).GetField("_doors", f)!.GetValue(map)!;
            foreach (object s in shown)
            {
                var part = (AreaMapModel.Part)s.GetType().GetField("Part")!.GetValue(s)!;
                var off = (OpenTK.Mathematics.Vector3)s.GetType().GetField("Offset")!.GetValue(s)!;
                log.Add($"part {part.Name,-14} origin {part.Origin} min {part.Min + off} max {part.Max + off}");
            }
            foreach (var d in doors)
            {
                string near = "";
                float best = float.MaxValue;
                foreach (object s in shown)
                {
                    var part = (AreaMapModel.Part)s.GetType().GetField("Part")!.GetValue(s)!;
                    var off = (OpenTK.Mathematics.Vector3)s.GetType().GetField("Offset")!.GetValue(s)!;
                    var q = OpenTK.Mathematics.Vector3.Clamp(d.Pos, part.Min + off, part.Max + off);
                    float dist = (q - d.Pos).Length;
                    if (part.IsConnector && dist < best) { best = dist; near = part.Name; }
                }
                log.Add($"door {d.Pos} facing {d.Rot.Row2} up {d.Rot.Row1}: nearest connector {near} {best:0.00} away");
            }
        }

        private static OpenTK.Mathematics.Vector3 SamusAt()
        {
            string? pos = Environment.GetEnvironmentVariable("FE_POS");
            if (pos == null) return new OpenTK.Mathematics.Vector3(-13.2f, 7.06f, 0.03f);
            float[] v = pos.Split(',').Select(Single.Parse).ToArray();
            return new OpenTK.Mathematics.Vector3(v[0], v[1], v[2]);
        }

        private static (float, float) Dir(string d) => d switch { "up" => (0, 1), "down" => (0, -1), "left" => (-1, 0), "right" => (1, 0), _ => (0, 0) };

        private static string OutDir()
        {
            string dir = Environment.GetEnvironmentVariable("FE_OUT") ?? Path.Combine(Paths.FileSystem, "..", "..", "extract_out", "frontend");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
