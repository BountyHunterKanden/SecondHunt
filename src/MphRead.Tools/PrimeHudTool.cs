using System;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // -primehud [off] [room=UNIT2_LAND] [arena=MP3 PROVING GROUND] [frames=N]: the Prime-style HUD (owner 2026-10-14: a round
    // radar top left on every hunter, a mini map of the current room top right in adventure; PlayerPrimeHud.cs) recorded
    // headless and software-rendered at 1920x1080 as -huddialog does: adventure Samus + a battle per hunter (radar rule on, 3
    // bots) -> extract_out/campaign/primehud_*.png. "off" renders today's HUD for the before pictures.
    internal static class PrimeHudTool
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            if (args.Contains("sprites"))
            {
                // dev: every HUD object's frames as PNGs (HudInfo.TestObjects' export; the radar's dot choices)
                MphRead.Hud.HudInfo.TestObjects(null, 0, 0, 0, 0, export: true);
                Console.WriteLine($"exported under {Paths.Export}");
                return;
            }
            if (args.Contains("mapnodes"))
            {
                // every area map model's node tree: per part (room / Con), its sub-nodes with triangle count + bounds; the
                // triangles (map space) per node -> extract_out/campaign/mapnodes_tris.txt (file|part|node|x y z x y z x y z ...)
                using var triOut = new StreamWriter(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign", "mapnodes_tris.txt")));
                foreach (string mf in MphRecomp.Frontend.AreaMapModel.FindFiles(Paths.FileSystem))
                {
                    Model mm = Read.ReadModelFile(Path.GetFileNameWithoutExtension(mf), "hud/" + mf, null);
                    mm.ComputeNodeMatrices(0);
                    float sc = mm.Scale.X;
                    Console.WriteLine($"== {mf} scale {sc}");
                    void Walk(int first, int depth, string partName)
                    {
                        for (int i = first; i != -1; i = mm.Nodes[i].NextIndex)
                        {
                            Node nd = mm.Nodes[i];
                            string pn = depth == 0 ? nd.Name : partName;
                            var line = new System.Text.StringBuilder($"{mf}|{pn}|{nd.Name}|");
                            int tris = 0;
                            var mn = new OpenTK.Mathematics.Vector3(1e9f);
                            var mx = new OpenTK.Mathematics.Vector3(-1e9f);
                            foreach (int meshId in nd.GetMeshIds())
                            {
                                float[] v = MphRecomp.Render.DsDisplayList.Decode(mm, mm.Meshes[meshId], isRoom: false);
                                for (int k = 0; k + MphRecomp.Render.DsDisplayList.Stride <= v.Length; k += MphRecomp.Render.DsDisplayList.Stride)
                                {
                                    var p = OpenTK.Mathematics.Vector3.TransformPosition(new OpenTK.Mathematics.Vector3(v[k], v[k + 1], v[k + 2]), nd.Transform) * sc;
                                    mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, p);
                                    mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, p);
                                    line.Append(System.FormattableString.Invariant($"{p.X:F2} {p.Y:F2} {p.Z:F2} "));
                                }
                                tris += v.Length / MphRecomp.Render.DsDisplayList.Stride / 3;
                            }
                            string box = tris > 0 ? $" tris {tris} min ({mn.X:F1},{mn.Y:F1},{mn.Z:F1}) max ({mx.X:F1},{mx.Y:F1},{mx.Z:F1})" : "";
                            Console.WriteLine($"{new string(' ', depth * 2)}{nd.Name} pos ({nd.Position.X:F1},{nd.Position.Y:F1},{nd.Position.Z:F1}){box}");
                            if (tris > 0) triOut.WriteLine(line.ToString().TrimEnd());
                            Walk(nd.ChildIndex, depth + 1, pn);
                        }
                    }
                    int rootIx = mm.Nodes.ToList().FindIndex(n => n.ParentIndex == -1);
                    if (rootIx >= 0) Walk(mm.Nodes[rootIx].ChildIndex, 0, "");
                    // each room part's doors in map space (the map screens' DoorPoint + the part's origin):
                    // file|part|#DOOR|x y z|type|entity id|its lock's facing
                    MphRecomp.Frontend.AreaMapModel am = MphRecomp.Frontend.AreaMapModel.Load(mf);
                    foreach (MphRecomp.Frontend.AreaMapModel.Part part in am.Parts.Where(p => !p.IsConnector))
                    {
                        (RoomMetadata? meta, int _) = Metadata.GetRoomByName(part.Name.ToUpperInvariant());
                        if (meta?.EntityPath == null) continue;
                        foreach (Entity e in Read.GetEntities(meta.EntityPath, -1, firstHunt: false))
                        {
                            if (e.Type != EntityType.Door || e is not Entity<DoorEntityData> door) continue;
                            // PauseMap.DoorPoint (internal to Core): the lock, then Celestial Archives' squashed rooms
                            int dt = (int)door.Data.DoorType;
                            OpenTK.Mathematics.Vector3 p = e.Position + e.UpVector * (dt >= 0 && dt < Metadata.Doors.Count ? Metadata.Doors[dt].LockOffset : 0);
                            if (part.Name.Equals("UNIT2_C2", StringComparison.OrdinalIgnoreCase)) p.Y *= 0x20cc / 4096f;
                            else if (part.Name.Equals("UNIT2_C3", StringComparison.OrdinalIgnoreCase)) p.Y *= 0x14e1 / 4096f;
                            p += part.Origin;
                            OpenTK.Mathematics.Vector3 f = e.FacingVector;
                            triOut.WriteLine(System.FormattableString.Invariant(
                                $"{mf}|{part.Name}|#DOOR|{p.X:F2} {p.Y:F2} {p.Z:F2}|{door.Data.DoorType}|{e.EntityId}|{f.X:F2} {f.Y:F2} {f.Z:F2}"));
                        }
                    }
                }
                return;
            }
            if (args.Contains("pieces"))
            {
                // the door-bounded pieces on the mini map (CorridorMap): per piece, the 192 map in its room before it's seen,
                // standing in it, and back in the room after -> extract_out/campaign/pieces/<n>_<step>.rgba + pieces.txt
                string pdir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign", "pieces"));
                Directory.CreateDirectory(pdir);
                MphRecomp.Frontend.HudMiniMap.Install(Paths.FileSystem);
                using var log = new StreamWriter(Path.Combine(pdir, "pieces.txt"));
                var px = new ColorRgba[192 * 192];
                float clock = 0;
                void Shot(string rn, string? part, OpenTK.Mathematics.Vector3 at, string file)
                {
                    var fac = -OpenTK.Mathematics.Vector3.UnitZ;
                    for (int i = 0; i < 3; i++) PlayerEntity.HostPrimeMap!(rn, part, at, fac, clock += 5, true, px, 192, out _, out _);
                    using FileStream fs = File.Create(Path.Combine(pdir, file));
                    foreach (ColorRgba c in px) fs.Write(new[] { c.Red, c.Green, c.Blue, c.Alpha });
                }
                int k = 0;
                foreach (MphRecomp.Frontend.CorridorMap.Piece piece in MphRecomp.Frontend.CorridorMap.Pieces)
                {
                    string pr = piece.Room.ToUpperInvariant();
                    MphRecomp.Frontend.AreaMapModel? am = MphRecomp.Frontend.AreaMapModel.ForRoom(Paths.FileSystem, pr);
                    MphRecomp.Frontend.AreaMapModel.Part? rp = am?.Parts.FirstOrDefault(p => p.Name.Equals(pr, StringComparison.OrdinalIgnoreCase));
                    if (rp == null) { log.WriteLine($"{piece.Key}: no map part"); continue; }
                    MphRecomp.Frontend.CorridorMap.Adopt(Array.Empty<string>(), null);
                    OpenTK.Mathematics.Vector3 body = rp.Centre - rp.Origin;
                    OpenTK.Mathematics.Vector3 inPiece = (piece.Min + piece.Max) / 2 - rp.Origin;
                    inPiece.Y = piece.Min.Y + 0.5f - rp.Origin.Y;
                    var atBody = MphRecomp.Frontend.CorridorMap.At(rp.Name, null, body + rp.Origin);
                    var atPiece = MphRecomp.Frontend.CorridorMap.At(rp.Name, piece.Parts[0], inPiece + rp.Origin);
                    var atPieceBox = MphRecomp.Frontend.CorridorMap.At(rp.Name, null, inPiece + rp.Origin);
                    sbyte[] pp = MphRecomp.Frontend.CorridorMap.PolyPieces(am!, rp);
                    int idx = Array.IndexOf(MphRecomp.Frontend.CorridorMap.Of(rp.Name), piece);
                    log.WriteLine($"{k} {piece.Key}: tris {pp.Count(x => x == idx)} of {pp.Length}; at body -> {atBody?.Name ?? "room"}, in piece (part) -> {atPiece?.Name ?? "room"}, (box) -> {atPieceBox?.Name ?? "room"}");
                    Shot(pr, null, body, $"{k}_a.rgba");
                    Shot(pr, piece.Parts[0], inPiece, $"{k}_b.rgba");
                    Shot(pr, null, body, $"{k}_c.rgba");
                    k++;
                }
                Console.WriteLine($"pieces: {k} -> {pdir}");
                return;
            }
            if (args.Contains("roomparts"))
            {
                // the game's own room parts per campaign room (owner 2026-10-14: split only door-bounded pieces): the parts
                // the collision portals name, each with its node box in map space (+ the map part's origin); every portal
                // (open or a door's); every door (in-room ones close the portal "<data node>_<entity node>")
                // -> extract_out/campaign/roomparts.txt
                using var po = new StreamWriter(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign", "roomparts.txt")));
                string F(OpenTK.Mathematics.Vector3 v) => System.FormattableString.Invariant($"{v.X:F2} {v.Y:F2} {v.Z:F2}");
                foreach (string mf in MphRecomp.Frontend.AreaMapModel.FindFiles(Paths.FileSystem))
                {
                    MphRecomp.Frontend.AreaMapModel am = MphRecomp.Frontend.AreaMapModel.Load(mf);
                    foreach (MphRecomp.Frontend.AreaMapModel.Part part in am.Parts.Where(p => !p.IsConnector))
                    {
                        (RoomMetadata? meta, int _) = Metadata.GetRoomByName(part.Name.ToUpperInvariant());
                        if (meta == null) continue;
                        CollisionInstance col = Collision.GetCollision(meta, roomLayerMask: -1);
                        Model rm = Read.GetRoomModelInstance(meta.Name).Model;
                        po.WriteLine($"#ROOM|{mf}|{part.Name}|{meta.Name}|{F(part.Origin)}");
                        var names = col.Info.Portals.Select(p => p.NodeName1).Concat(col.Info.Portals.Select(p => p.NodeName2)).Distinct().ToList();
                        foreach (Node pn in rm.Nodes.Where(n => names.Contains(n.Name) && n.ChildIndex != -1))
                        {
                            var mn = new OpenTK.Mathematics.Vector3(1e9f);
                            var mx = new OpenTK.Mathematics.Vector3(-1e9f);
                            var stack = new System.Collections.Generic.Stack<int>();
                            stack.Push(pn.ChildIndex);
                            while (stack.Count > 0)
                            {
                                for (int i = stack.Pop(); i != -1; i = rm.Nodes[i].NextIndex)
                                {
                                    Node nd = rm.Nodes[i];
                                    if (nd.ChildIndex != -1) stack.Push(nd.ChildIndex);
                                    // skip sky shells (Helm Room's rmjump/rmbugs carry 270-unit boxes)
                                    if (nd.MeshCount == 0 || (nd.MaxBounds - nd.MinBounds).X > 150 || (nd.MaxBounds - nd.MinBounds).Z > 150) continue;
                                    mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, nd.MinBounds);
                                    mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, nd.MaxBounds);
                                }
                            }
                            po.WriteLine($"#PART|{pn.Name}|{F(mn + part.Origin)}|{F(mx + part.Origin)}");
                        }
                        foreach (Portal p in col.Info.Portals)
                        {
                            po.WriteLine($"#PORTAL|{p.Name}|{p.NodeName1}|{p.NodeName2}|{F(p.Position + part.Origin)}|{(p.IsForceField ? "ff" : "")}");
                        }
                        if (meta.EntityPath == null) continue;
                        foreach (Entity e in Read.GetEntities(meta.EntityPath, -1, firstHunt: false))
                        {
                            if (e.Type != EntityType.Door || e is not Entity<DoorEntityData> door) continue;
                            po.WriteLine($"#DOOR|{e.EntityId}|{door.Data.NodeName.MarshalString()}_{e.NodeName}|{door.Data.ConnectorId}|{door.Data.DoorType}|{F(e.Position + part.Origin)}");
                        }
                    }
                }
                return;
            }
            bool on = true;
            string room = "UNIT2_LAND", arena = "MP3 PROVING GROUND";
            int frames = 420;
            foreach (string arg in args)
            {
                if (arg == "off") on = false;
                else if (arg.StartsWith("room=")) room = arg[5..];
                else if (arg.StartsWith("arena=")) arena = arg[6..];
                else if (arg.StartsWith("frames=")) frames = Int32.Parse(arg[7..]);
            }
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign"));
            Directory.CreateDirectory(outDir);
            string tag = on ? "on" : "off";
            PlayerEntity.HostPrimeHud = on;
            if (on && !args.Contains("flat")) MphRecomp.Frontend.HudMiniMap.Install(Paths.FileSystem); // "flat": the floor picture
            int sideFail = 0;
            try
            {
                // the campaign, every hunter (each can play it): radar + the room's mini map
                foreach (Hunter hunter in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus,
                    Hunter.Spire, Hunter.Weavel })
                {
                    using CampaignHost host = CampaignHost.Start(room, hunter: hunter, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
                    int landing = 0;
                    for (; landing < 3000 && (landing < 300 || CameraSequence.Current != null); landing++) host.Step(default);
                    for (int i = 0; i < 60; i++) host.Step(default);
                    if (args.Contains("visitall"))
                    {
                        // every room visited: the mini map's neighbours (rooms past the connectors) show
                        for (int id = 0; id < 72; id++) GameState.StorySave.SetVisitedRoom(id);
                    }
                    string file = Path.Combine(outDir, $"primehud_{tag}_adventure_{room}_{hunter}.png");
                    CampaignSim.RenderHud(host.Scene, 1920, 1080, file);
                    Console.WriteLine($"  adventure {room} {hunter}: {host.Scene.HudItemCount} HUD items -> {file}");
                    if (on)
                    {
                        DumpTexture(host.Player, "_radarFrameInst", Path.Combine(outDir, $"primehud_radar_adventure_{hunter}.rgba"));
                        DumpTexture(host.Player, "_map3DInst", Path.Combine(outDir, $"primehud_map_{hunter}.rgba")); // 192 x 192 (the 3D room)
                        DumpTexture(host.Player, "_mapMarkerInst", Path.Combine(outDir, $"primehud_marker_{hunter}.rgba")); // 64 x 64 (the player marker)
                        Console.WriteLine($"  map player {hunter}: {typeof(PlayerEntity).GetField("_mapPlayer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(host.Player)} share {typeof(PlayerEntity).GetField("_mapMarkerShare", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(host.Player)}");
                        DumpTexture(host.Player, "_mapFrameInst", Path.Combine(outDir, $"primehud_mapframe_{hunter}.rgba"));
                        if (hunter == Hunter.Samus && PlayerEntity.HostPrimeMap != null && host.Scene.Room?.Meta?.Name is string name)
                        {
                            // the 3D room big, to judge its shapes: 384 x 384, raw RGBA
                            const int big = 384;
                            var pixels = new ColorRgba[big * big];
                            OpenTK.Mathematics.Vector3 facing = host.Player.CameraInfo.Facing;
                            if (PlayerEntity.HostPrimeMap(name, null, host.Player.Position, new OpenTK.Mathematics.Vector3(facing.X, 0, facing.Z), 0f, true, pixels, big, out _, out _))
                            {
                                using FileStream fs = File.Create(Path.Combine(outDir, $"primehud_map3d_big_{room}.rgba"));
                                foreach (ColorRgba c in pixels) fs.Write(new[] { c.Red, c.Green, c.Blue, c.Alpha });
                                Console.WriteLine($"  big 3D map {name}: pos {host.Player.Position}, facing {facing}");
                                // the HUD's own paint (192), timed: it runs on frames the player moves or turns
                                var hud = new ColorRgba[192 * 192];
                                for (int i = 0; i < 300; i++) PlayerEntity.HostPrimeMap(name, null, host.Player.Position, new OpenTK.Mathematics.Vector3(facing.X, 0, facing.Z), 0f, true, hud, 192, out _, out _); // warm (tiered JIT)
                                var clock = System.Diagnostics.Stopwatch.StartNew();
                                for (int i = 0; i < 200; i++) PlayerEntity.HostPrimeMap(name, null, host.Player.Position, new OpenTK.Mathematics.Vector3(facing.X, 0, facing.Z), 0f, true, hud, 192, out _, out _);
                                Console.WriteLine($"  HUD map paint: {clock.Elapsed.TotalMilliseconds / 200:F2} ms (192, PC)");
                                if (args.Contains("hallhud"))
                                {
                                    // the real HUD pipeline with Samus put in the low hallway, at two headings: the HUD picture's
                                    // map corner + the texture and the marker spot the HUD used, to compare
                                    int hk = 0;
                                    foreach (var yaw in new[] { -OpenTK.Mathematics.Vector3.UnitX, OpenTK.Mathematics.Vector3.UnitZ })
                                    {
                                        host.Player.Position = new OpenTK.Mathematics.Vector3(-36, 0.3f, 4);
                                        host.Player.CameraInfo.Facing = yaw;
                                        for (int i = 0; i < 40; i++) { host.Player.Position = new OpenTK.Mathematics.Vector3(-36, 0.3f, 4); host.Step(default); }
                                        string hf = Path.Combine(outDir, $"primehud_hallhud_{hk}.png");
                                        CampaignSim.RenderHud(host.Scene, 1920, 1080, hf);
                                        DumpTexture(host.Player, "_map3DInst", Path.Combine(outDir, $"primehud_hallhud_{hk}.rgba"));
                                        Console.WriteLine($"  hallhud {hk}: pos {host.Player.Position} cam facing {host.Player.CameraInfo.Facing} map player {typeof(PlayerEntity).GetField("_mapPlayer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(host.Player)}");
                                        hk++;
                                    }
                                }
                                if (args.Contains("mapdiag") && host.Scene.Room is MphRead.Entities.RoomEntity re)
                                {
                                    // the marker's spot over the room's (and its connectors') floors, at four headings
                                    Console.WriteLine(MphRecomp.Frontend.HudMiniMap.Describe(name));
                                    // the map at spots along the room's low hallway (floor cells y 0-4), facing -X then turned 90 left (+Z... -> -Z)
                                    {
                                        float tt = 1000;
                                        int k = 0;
                                        foreach (var spotP in new[] { new OpenTK.Mathematics.Vector3(-36, 0.5f, 8), new OpenTK.Mathematics.Vector3(-20, 3, 0),
                                            new OpenTK.Mathematics.Vector3(0, 3, 0), new OpenTK.Mathematics.Vector3(20, 3, 0) })
                                        {
                                            foreach (var fac in new[] { -OpenTK.Mathematics.Vector3.UnitX, OpenTK.Mathematics.Vector3.UnitZ })
                                            {
                                                PlayerEntity.HostPrimeMap(name, null, spotP, fac, tt += 5, true, hud, 192, out _, out _);
                                                PlayerEntity.HostPrimeMap(name, null, spotP, fac, tt += 5, true, hud, 192, out OpenTK.Mathematics.Vector2 sp, out _);
                                                using (FileStream fs3 = File.Create(Path.Combine(outDir, $"primehud_hall_{k}.rgba")))
                                                    foreach (ColorRgba c in hud) fs3.Write(new[] { c.Red, c.Green, c.Blue, c.Alpha });
                                                Console.WriteLine($"  hall {k}: at {spotP} facing {fac}: marker {sp.X:F1} {sp.Y:F1}");
                                                k++;
                                            }
                                        }
                                    }
                                    // the room's floors (faces up) by spot: world x/z extents of the floor faces, in 8-unit cells
                                    if (re.RoomCollision[0].Info is MphRead.Formats.Collision.MphCollisionInfo fi)
                                    {
                                        var cells = new System.Collections.Generic.SortedDictionary<(int, int), float>();
                                        OpenTK.Mathematics.Vector3 tr = re.RoomCollision[0].Translation;
                                        foreach (var d in fi.Data)
                                        {
                                            if (d.IgnorePlayers || fi.Planes[d.PlaneIndex].Y < 0.6f) continue;
                                            var cen = OpenTK.Mathematics.Vector3.Zero;
                                            for (int j = 0; j < d.PointIndexCount; j++) cen += fi.Points[fi.PointIndices[d.PointStartIndex + j]] + tr;
                                            cen /= d.PointIndexCount;
                                            var key = ((int)MathF.Floor(cen.X / 8), (int)MathF.Floor(cen.Z / 8));
                                            cells[key] = cells.TryGetValue(key, out float hy) ? MathF.Max(hy, cen.Y) : cen.Y;
                                        }
                                        Console.WriteLine("  floor cells (x8, z8 -> top y): " + string.Join(" ", cells.Select(c => $"[{c.Key.Item1},{c.Key.Item2}]{c.Value:F0}")));
                                    }
                                    float diagT = 100;
                                    for (int ci = 0; ci < re.RoomCollision.Count; ci++)
                                    {
                                        if (re.RoomCollision[ci].Info is not MphRead.Formats.Collision.MphCollisionInfo ci2) continue;
                                        OpenTK.Mathematics.Vector3 t = re.RoomCollision[ci].Translation;
                                        var mn = new OpenTK.Mathematics.Vector3(Single.MaxValue);
                                        var mx = new OpenTK.Mathematics.Vector3(Single.MinValue);
                                        float worst = 0;
                                        OpenTK.Mathematics.Vector3 worstAt = default;
                                        foreach (OpenTK.Mathematics.Vector3 p0 in ci2.Points)
                                        {
                                            OpenTK.Mathematics.Vector3 p = p0 + t;
                                            mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, p);
                                            mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, p);
                                            foreach (var f in new[] { (1f, 0f), (0f, 1f), (-1f, 0f), (0f, -1f) })
                                            {
                                                var fac = new OpenTK.Mathematics.Vector3(f.Item1, 0, f.Item2);
                                                PlayerEntity.HostPrimeMap(name, null, p, fac, diagT += 1f, true, hud, 192, out OpenTK.Mathematics.Vector2 spot, out _);
                                                float d = (spot - new OpenTK.Mathematics.Vector2(96)).Length;
                                                if (d > worst) { worst = d; worstAt = p; }
                                            }
                                        }
                                        Console.WriteLine($"  collision {ci}: min {mn} max {mx}; marker farthest {worst:F0} texels from the middle (96 = edge) at {worstAt}");
                                        // the map at the middle of this collision's floor, facing -X, glide done: raw RGBA + the marker's spot
                                        var mid = new OpenTK.Mathematics.Vector3((mn.X + mx.X) / 2, mn.Y + 0.1f, (mn.Z + mx.Z) / 2);
                                        PlayerEntity.HostPrimeMap(name, null, mid, -OpenTK.Mathematics.Vector3.UnitX, diagT += 1f, true, hud, 192, out _, out _);
                                        PlayerEntity.HostPrimeMap(name, null, mid, -OpenTK.Mathematics.Vector3.UnitX, diagT += 1f, true, hud, 192, out OpenTK.Mathematics.Vector2 midSpot, out _);
                                        using (FileStream fs2 = File.Create(Path.Combine(outDir, $"primehud_mapdiag_{ci}.rgba")))
                                            foreach (ColorRgba c in hud) fs2.Write(new[] { c.Red, c.Green, c.Blue, c.Alpha });
                                        Console.WriteLine($"    middle {mid}: marker at {midSpot}");
                                    }
                                }
                            }
                        }
                    }
                }
                foreach (Hunter hunter in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus,
                    Hunter.Spire, Hunter.Weavel })
                {
                    MatchSettings settings = MatchSettings.Quick(GameMode.Battle, arena, hunter, 3, 1);
                    settings.RadarPlayers = true;
                    using CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
                    var input = new CampaignInput { SelectWeapon = BeamType.None };
                    // PRESS FIRE TO BEGIN: tap fire until the intro is over, then a short turn so the radar has dots around
                    int f = 0;
                    for (; f < 1200 && (f < 60 || CameraSequence.Current?.IsIntro == true); f++)
                    {
                        input.Buttons = f % 20 < 2 ? CampaignButtons.Shoot : CampaignButtons.None;
                        host.Step(input);
                    }
                    input.Buttons = CampaignButtons.None;
                    for (f = 0; f < frames; f++) host.Step(input);
                    // the radar's sides vs the camera's: each bot's dot must be on the side the view has it
                    PlayerEntity me = host.Player;
                    var fwd = new OpenTK.Mathematics.Vector2(me.CameraInfo.Facing.X, me.CameraInfo.Facing.Z).Normalized();
                    Console.WriteLine($"    me: health {me.Health}, alt {me.IsAltForm}");
                    var right = new OpenTK.Mathematics.Vector2(-fwd.Y, fwd.X);
                    foreach (PlayerEntity bot in host.Scene.GetPlayerEntities())
                    {
                        if (bot == me || bot.Health == 0) continue;
                        OpenTK.Mathematics.Vector3 rel = bot.Position - me.Position;
                        float radarX = OpenTK.Mathematics.Vector2.Dot(rel.Xz, right);
                        float viewX = (new OpenTK.Mathematics.Vector4(bot.Position, 1) * host.Scene.ViewMatrix).X;
                        bool same = MathF.Abs(radarX) < 0.5f || MathF.Sign(radarX) == MathF.Sign(viewX);
                        if (!same) sideFail++;
                        Console.WriteLine($"    side {(same ? "ok" : "WRONG")}: bot {bot.SlotIndex} radar x {radarX:0.0}, view x {viewX:0.0}");
                    }
                    string file = Path.Combine(outDir, $"primehud_{tag}_{hunter}.png");
                    CampaignSim.RenderHud(host.Scene, 1920, 1080, file);
                    // the radar's frame texture as raw RGBA (96x96), for the placement page's draggable radar
                    if (on)
                    {
                        DumpTexture(me, "_radarFrameInst", Path.Combine(outDir, $"primehud_radar_{hunter}.rgba"));
                    }
                    Console.WriteLine($"  battle {hunter}: {host.Scene.HudItemCount} HUD items -> {file}");
                }
            }
            finally
            {
                PlayerEntity.HostPrimeHud = false;
            }
            Console.WriteLine(sideFail == 0 ? "PASS (radar sides)" : $"FAIL ({sideFail} radar sides)");
        }

        // a PlayerPrimeHud texture (HudObjectInstance field) as raw RGBA, for the placement page's images
        private static void DumpTexture(PlayerEntity player, string field, string file)
        {
            if (typeof(PlayerEntity).GetField(field, System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)?.GetValue(player) is MphRead.Hud.HudObjectInstance inst)
            {
                var raw = new byte[inst.Texture.Length * 4];
                for (int i = 0; i < inst.Texture.Length; i++)
                {
                    raw[i * 4] = inst.Texture[i].Red;
                    raw[i * 4 + 1] = inst.Texture[i].Green;
                    raw[i * 4 + 2] = inst.Texture[i].Blue;
                    raw[i * 4 + 3] = inst.Texture[i].Alpha;
                }
                File.WriteAllBytes(file, raw);
            }
        }
    }
}
