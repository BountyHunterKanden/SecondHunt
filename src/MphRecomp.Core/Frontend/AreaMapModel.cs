using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Render;
using OpenTK.Mathematics;
using Col = MphRead.Formats.Collision;

// The pause map's 3D area model: the ROM's map model for each area half, hud/unitN_MNAV_Model.bin (overlay 8's name
// table at 0x2132fdc: unit1_1 .. unit4_1, seven files). Under world_root it has one node per room, named after the room
// (UNIT2_LAND, UNIT2_C0, ... = MphRead's room names) and one per connector (Con01, Con02, ...); each of those has a
// centRoomN child marking its centre and the plain box/cylinder geometry the map draws. The game finds its nodes by name
// (ov8 "R%d%02d", "Con%d", "cent"). Loaded once per area; the triangles are baked into model space here (node matrices,
// model scale) so the screen can light and project them on the CPU.
//
// The DETAILED look (owner 2026-10-03, a recomp option): each room drawn from its own collision mesh instead -- the
// floors, walls and platforms the game collides with -- at the same place and scale (room space + the room node's
// position, as Samus is placed), and each connector from its connector room's collision, placed the way
// RoomEntity.AddConnector places it at the door.
namespace MphRecomp.Frontend
{
    // Polygons for the map to light and project: the map model's triangles, or a room's collision polygons.
    public sealed class MapGeometry
    {
        public struct Poly
        {
            public int Start, Count; // corners in Indices
            public int Material; // the area model's material: colour, alpha, (map model only) culling
            public Vector3 Normal; // lighting: the display list's normal, or the collision plane's
            public bool Top; // a 'T'op face: left out of the selected room's outline
        }

        public List<Vector3> Points { get; } = new();
        public List<int> Indices { get; } = new();
        public List<Poly> Polys { get; } = new();
        // collision polygons face the side the game collides from (into the room): drawn when that side faces the
        // camera, and the selected room's outline is each polygon's own edges. The map model's triangles: culled by the
        // material and their winding, outlined where an edge belongs to one triangle only (a quad's diagonal drops out).
        public bool IsCollision { get; init; }
        // collision only: each polygon's edges the outline may draw -- where the surface bends or ends; the splits inside
        // a flat surface (an edge lying on another polygon of the same plane) are left out
        public List<(int A, int B, int Poly)> Creases { get; } = new();
        public Vector3 Min { get; private set; }
        public Vector3 Max { get; private set; }

        public void ComputeBounds()
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (Vector3 p in Points)
            {
                min = Vector3.ComponentMin(min, p);
                max = Vector3.ComponentMax(max, p);
            }
            Min = min;
            Max = max;
        }
    }

    public sealed class AreaMapModel
    {
        public sealed class Part
        {
            public string Name { get; init; } = "";
            public bool IsConnector { get; init; }
            public int ConnectorNumber { get; init; } // Con%d's number (they count on across an area's two halves)
            public int RoomId { get; init; } = -1;
            // the node's own position: a room's coordinates + this = map space (1:1; ov8 0x213186c places Samus so)
            public Vector3 Origin { get; init; }
            public Vector3 Centre { get; set; }
            public Vector3 Min { get; set; }
            public Vector3 Max { get; set; }
            // the map model's triangles (model space)
            public MapGeometry Nav { get; } = new();
            // the detailed look's collision polygons (model space); null until LoadDetail, or when there is none
            public MapGeometry? Detail { get; set; }
        }

        // the second half's place in the first half's space, by area (ov8 0x21330e4, fx32): Alinos 2, Celestial
        // Archives 2, Vesper Defense Outpost 2 (the others 0)
        public static readonly Vector3[] HalfOffsets =
        {
            Vector3.Zero, new(-331259 / 4096f, 91271 / 4096f, 842674 / 4096f), Vector3.Zero, new(0, 73, 0),
            Vector3.Zero, new(-560332 / 4096f, -868 / 4096f, 17879 / 4096f), Vector3.Zero, Vector3.Zero
        };

        public string File { get; }
        public Model Model { get; }
        public List<Part> Parts { get; } = new();
        public bool DetailLoaded { get; private set; }

        private AreaMapModel(string file, Model model)
        {
            File = file;
            Model = model;
        }

        // the map model files in a game file tree (their names' case varies: unit1_1nav_model.bin, unit2_1NAV_Model.bin)
        public static List<string> FindFiles(string fileSystemRoot)
        {
            string hud = Path.Combine(fileSystemRoot, "hud");
            if (!Directory.Exists(hud)) return new List<string>();
            return Directory.GetFiles(hud)
                .Select(Path.GetFileName)
                .Where(n => n != null && n.StartsWith("unit", StringComparison.OrdinalIgnoreCase)
                    && n.EndsWith("nav_model.bin", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // unit2_1NAV_Model.bin -> (2, 1)
        public static (int Unit, int Half) UnitHalf(string file)
        {
            string n = file.ToLowerInvariant();
            return n.Length > 6 && Char.IsDigit(n[4]) && Char.IsDigit(n[6]) ? (n[4] - '0', n[6] - '0') : (0, 0);
        }

        public static AreaMapModel Load(string fileName)
        {
            Model model = Read.ReadModelFile(Path.GetFileNameWithoutExtension(fileName), "hud/" + fileName, null);
            var map = new AreaMapModel(fileName, model);
            map.Bake();
            return map;
        }

        // the map model that has a node for this room (null: none does, e.g. the Oubliette or a multiplayer room)
        public static AreaMapModel? ForRoom(string fileSystemRoot, string roomName)
        {
            foreach (string file in FindFiles(fileSystemRoot))
            {
                // a cheap name scan first: node names are stored as 64-byte strings in the file
                byte[] bytes = System.IO.File.ReadAllBytes(Path.Combine(fileSystemRoot, "hud", file));
                if (!ContainsName(bytes, roomName)) continue;
                AreaMapModel map = Load(file);
                if (map.Parts.Any(p => p.Name.Equals(roomName, StringComparison.OrdinalIgnoreCase))) return map;
            }
            return null;
        }

        private static bool ContainsName(byte[] bytes, string name)
        {
            byte[] want = System.Text.Encoding.ASCII.GetBytes(name.ToUpperInvariant());
            for (int i = 0; i + want.Length < bytes.Length; i++)
            {
                int k = 0;
                while (k < want.Length && char.ToUpperInvariant((char)bytes[i + k]) == want[k]) k++;
                if (k == want.Length && bytes[i + k] == 0) return true;
            }
            return false;
        }

        private void Bake()
        {
            Model.ComputeNodeMatrices(0);
            int root = Model.Nodes.ToList().FindIndex(n => n.ParentIndex == -1);
            if (root < 0) return;
            float scale = Model.Scale.X;
            for (int i = Model.Nodes[root].ChildIndex; i != -1; i = Model.Nodes[i].NextIndex)
            {
                Node node = Model.Nodes[i];
                bool connector = node.Name.StartsWith("Con", StringComparison.OrdinalIgnoreCase);
                int roomId = -1, number = 0;
                if (connector)
                {
                    Int32.TryParse(node.Name[3..], out number);
                }
                else
                {
                    // node names are the rooms' names in mixed case (Unit1_Land, CrystalRoom); MphRead's are upper case
                    (RoomMetadata? meta, int id) = MphRead.Metadata.GetRoomByName(node.Name.ToUpperInvariant());
                    if (meta != null) roomId = id;
                }
                var part = new Part { Name = node.Name, IsConnector = connector, ConnectorNumber = number, RoomId = roomId, Origin = node.Position };
                Vector3? centre = null;
                AddSubtree(part, node.ChildIndex, scale, ref centre);
                AddMeshes(part, node, scale);
                if (part.Nav.Points.Count == 0 && centre == null) continue;
                if (part.Nav.Points.Count > 0)
                {
                    part.Nav.ComputeBounds();
                    part.Min = part.Nav.Min;
                    part.Max = part.Nav.Max;
                }
                part.Centre = centre ?? (part.Min + part.Max) / 2;
                Parts.Add(part);
            }
        }

        private void AddSubtree(Part part, int first, float scale, ref Vector3? centre)
        {
            for (int i = first; i != -1; i = Model.Nodes[i].NextIndex)
            {
                Node node = Model.Nodes[i];
                if (node.Name.StartsWith("cent", StringComparison.OrdinalIgnoreCase))
                {
                    centre = node.Transform.ExtractTranslation() * scale;
                }
                AddMeshes(part, node, scale);
                AddSubtree(part, node.ChildIndex, scale, ref centre);
            }
        }

        private void AddMeshes(Part part, Node node, float scale)
        {
            MapGeometry g = part.Nav;
            foreach (int meshId in node.GetMeshIds())
            {
                Mesh mesh = Model.Meshes[meshId];
                bool top = Model.Materials[mesh.MaterialId].Name.StartsWith("T", StringComparison.Ordinal);
                float[] v = DsDisplayList.Decode(Model, mesh, isRoom: false);
                for (int k = 0; k + 3 * DsDisplayList.Stride <= v.Length; k += 3 * DsDisplayList.Stride)
                {
                    Vector3 normal = Vector3.Zero;
                    int start = g.Indices.Count;
                    for (int c = 0; c < 3; c++)
                    {
                        int o = k + c * DsDisplayList.Stride;
                        var p = new Vector3(v[o], v[o + 1], v[o + 2]);
                        g.Indices.Add(g.Points.Count);
                        g.Points.Add(Vector3.TransformPosition(p, node.Transform) * scale);
                        normal += Vector3.TransformNormal(new Vector3(v[o + 3], v[o + 4], v[o + 5]), node.Transform);
                    }
                    g.Polys.Add(new MapGeometry.Poly
                    {
                        Start = start,
                        Count = 3,
                        Material = mesh.MaterialId,
                        Normal = normal.LengthSquared > 1e-12f ? normal.Normalized() : Vector3.Zero,
                        Top = top
                    });
                }
            }
        }

        // ---------------------------------------------------------------- the detailed look

        // RoomEntity._connectorSizes: a connector room sits at the door + half this (negated for doors facing +x / +z)
        private static readonly Vector3[] ConnectorSizes =
        {
            new(10, 0, 0), new(10, 0, 0), new(0, 0, 10), new(0, 0, 10), new(10, 0, 0), new(10, 0, 0), new(0, 0, 10),
            new(0, 0, 10), new(0xA60F / 4096f, 0, 0), new(0xA60F / 4096f, 0, 0), new(0, 0, 0xA60F / 4096f),
            new(0, 0, 0xA60F / 4096f), new(10, 0, 0), new(10, 0, 0), new(0, 0, 10), new(0, 0, 10), new(10, 0, 0),
            new(10, 0, 0), new(0, 0, 10), new(0, 0, 10), new(0, 0x24B9 / 4096f, 0x16A77 / 4096f),
            new(0, -7659 / 4096f, 0x16A76 / 4096f), new(10, 0, 0), new(10, 0, 0), new(0, 0, 20), new(0, 0, 10), new(0, 0, 10)
        };

        // Loads every part's collision polygons (once). Rooms: the room's own collision with the layers the game uses for
        // it (SceneSetup: single player, the room's node layer). Connectors: found through the rooms' doors -- each door
        // with a connector places that connector room's collision as RoomEntity.AddConnector does, and the map's Con node
        // for that spot gets it (see below). A room whose collision can't be read keeps its map model.
        public void LoadDetail(Action<string>? log)
        {
            if (DetailLoaded) return;
            DetailLoaded = true;
            var placed = new List<(int Connector, Vector3 At, string Name)>();
            foreach (Part part in Parts)
            {
                if (part.IsConnector || part.RoomId < 0) continue;
                RoomMetadata? meta = MphRead.Metadata.GetRoomById(part.RoomId, noThrow: true);
                if (meta == null) continue;
                try
                {
                    int mask = SceneSetup.GetNodeLayer(GameMode.SinglePlayer, meta.NodeLayer, playerCount: 1);
                    Col.MphCollisionInfo? col = ReadCollision(meta.CollisionPath, mask);
                    if (col != null) part.Detail = FromCollision(col, part.Origin, part);
                    if (meta.EntityPath == null) continue;
                    foreach (Entity e in Read.GetEntities(meta.EntityPath, -1, firstHunt: false))
                    {
                        if (e is not Entity<DoorEntityData> door || door.Data.ConnectorId >= ConnectorSizes.Length) continue;
                        Vector3 size = ConnectorSizes[door.Data.ConnectorId];
                        Vector3 facing = e.FacingVector;
                        if (facing.X > 2896 / 4096f || facing.Z > 2896 / 4096f) size = -size;
                        // a connector door names its connector's map node (RoomName "Con07")
                        placed.Add(((int)door.Data.ConnectorId, e.Position + size / 2 + part.Origin, door.Data.RoomName.MarshalString()));
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"map: {part.Name} collision: {ex.Message}");
                }
            }
            // which door spot is which connector: first by place (the spot nearest the map box's middle, within reach;
            // each spot used once -- both rooms' doors place the same connector), then by the name the door gives
            // (needed where the map box sits off the door, e.g. Vesper Defense Outpost's Con06). A connector with neither
            // is part of a room's own shape (Alinos 1's Con07 is the Crystal Room's hall): nothing in the detailed look.
            var used = new bool[placed.Count];
            var match = new Dictionary<Part, int>();
            // the connector rooms' collision, read once each; a spot's shape = its collision's bounds there
            var shapes = new Dictionary<int, Col.MphCollisionInfo?>();
            Col.MphCollisionInfo? Shape(int connector)
            {
                if (shapes.TryGetValue(connector, out Col.MphCollisionInfo? c)) return c;
                RoomMetadata? meta = MphRead.Metadata.GetRoomById(connector, noThrow: true);
                try
                {
                    c = meta == null ? null : ReadCollision(meta.CollisionPath, -1);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"map: connector {connector} collision: {ex.Message}");
                    c = null;
                }
                shapes[connector] = c;
                return c;
            }
            Vector3 ShapeMid(int i)
            {
                Col.MphCollisionInfo? c = Shape(placed[i].Connector);
                if (c == null || c.Points.Count == 0) return placed[i].At;
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                foreach (Vector3 p in c.Points)
                {
                    min = Vector3.ComponentMin(min, p);
                    max = Vector3.ComponentMax(max, p);
                }
                return (min + max) / 2 + placed[i].At;
            }
            void Take(Part con, int i)
            {
                match[con] = i;
                for (int k = 0; k < placed.Count; k++)
                {
                    if ((placed[k].At - placed[i].At).LengthSquared < 2.5f * 2.5f) used[k] = true;
                }
            }
            const float Reach = 1.5f;
            foreach (Part con in Parts)
            {
                if (!con.IsConnector) continue;
                Vector3 mid = (con.Min + con.Max) / 2;
                int best = -1;
                for (int i = 0; i < placed.Count; i++)
                {
                    Vector3 at = placed[i].At;
                    if (used[i] || at.X < con.Min.X - Reach || at.X > con.Max.X + Reach || at.Y < con.Min.Y - Reach
                        || at.Y > con.Max.Y + Reach || at.Z < con.Min.Z - Reach || at.Z > con.Max.Z + Reach) continue;
                    // the two rooms' doors can place it a little apart: the one whose shape sits best in the map's box
                    if (best < 0 || (ShapeMid(i) - mid).LengthSquared < (ShapeMid(best) - mid).LengthSquared) best = i;
                }
                if (best >= 0) Take(con, best);
            }
            foreach (Part con in Parts)
            {
                if (!con.IsConnector || match.ContainsKey(con)) continue;
                int i = Enumerable.Range(0, placed.Count).FirstOrDefault(k => !used[k] && placed[k].Name.Equals(con.Name, StringComparison.OrdinalIgnoreCase), -1);
                if (i >= 0) Take(con, i);
            }
            foreach (Part con in Parts)
            {
                if (!con.IsConnector) continue;
                if (!match.TryGetValue(con, out int i))
                {
                    con.Detail = new MapGeometry { IsCollision = true };
                    log?.Invoke($"map: {File} {con.Name}: no connector door of its own -- part of a room's shape");
                    continue;
                }
                if (Shape(placed[i].Connector) is Col.MphCollisionInfo col) con.Detail = FromCollision(col, placed[i].At, con);
            }
        }

        // the file read straight (no shared cache: the map may load while the game loads a room)
        private static Col.MphCollisionInfo? ReadCollision(string path, int mask)
        {
            byte[] bytes = System.IO.File.ReadAllBytes(Paths.Combine(Paths.FileSystem, path));
            Col.CollisionHeader header = Read.ReadStruct<Col.CollisionHeader>(bytes);
            return header.Type.MarshalString() == "wc01" ? Col.Collision.ReadMphCollision(header, bytes, mask) : null;
        }

        // collision polygons -> map polygons at `at`, in the part's own map colours: floors (facing up) in its 'T'op
        // material, everything else in its main one (the materials its map model uses most)
        private MapGeometry FromCollision(Col.MphCollisionInfo col, Vector3 at, Part part)
        {
            (int wall, int top) = PartMaterials(part);
            var g = new MapGeometry { IsCollision = true };
            var used = new Dictionary<int, int>();
            var planes = new List<Vector4>();
            foreach (Col.CollisionData data in col.Data)
            {
                // beam-only surfaces (players pass through them) aren't part of the room's shape
                if (data.IgnorePlayers || data.PointIndexCount < 3) continue;
                Vector4 plane = col.Planes[data.PlaneIndex];
                var normal = new Vector3(plane.X, plane.Y, plane.Z);
                bool floor = normal.Y > 0.7f;
                int start = g.Indices.Count;
                for (int k = 0; k < data.PointIndexCount; k++)
                {
                    int index = col.PointIndices[data.PointStartIndex + k];
                    if (!used.TryGetValue(index, out int mine))
                    {
                        mine = g.Points.Count;
                        used.Add(index, mine);
                        g.Points.Add(col.Points[index] + at);
                    }
                    g.Indices.Add(mine);
                }
                g.Polys.Add(new MapGeometry.Poly { Start = start, Count = data.PointIndexCount, Material = floor ? top : wall, Normal = normal, Top = floor });
                planes.Add(new Vector4(normal, plane.W + Vector3.Dot(normal, at)));
            }
            g.ComputeBounds();
            FindCreases(g, planes);
            return g;
        }

        private static void FindCreases(MapGeometry g, List<Vector4> planes)
        {
            // polygons by plane (the same plane stored twice counts as one)
            var byPlane = new Dictionary<(int, int, int, int), List<int>>();
            for (int p = 0; p < g.Polys.Count; p++)
            {
                Vector4 q = planes[p];
                var key = ((int)MathF.Round(q.X * 500), (int)MathF.Round(q.Y * 500), (int)MathF.Round(q.Z * 500), (int)MathF.Round(q.W * 20));
                if (!byPlane.TryGetValue(key, out List<int>? list)) byPlane[key] = list = new List<int>();
                list.Add(p);
            }
            foreach (List<int> same in byPlane.Values)
            {
                foreach (int p in same)
                {
                    MapGeometry.Poly poly = g.Polys[p];
                    for (int i = 0; i < poly.Count; i++)
                    {
                        int a = g.Indices[poly.Start + i], b = g.Indices[poly.Start + (i + 1) % poly.Count];
                        Vector3 mid = (g.Points[a] + g.Points[b]) / 2;
                        bool inner = false;
                        foreach (int o in same)
                        {
                            if (o != p && OnPolygon(g, g.Polys[o], mid))
                            {
                                inner = true;
                                break;
                            }
                        }
                        if (!inner) g.Creases.Add((a, b, p));
                    }
                }
            }
        }

        // a point of the polygon's plane inside it or on its edge (convex, either winding)
        private static bool OnPolygon(MapGeometry g, MapGeometry.Poly poly, Vector3 point)
        {
            const float Eps = 0.02f;
            int sign = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                Vector3 a = g.Points[g.Indices[poly.Start + i]], b = g.Points[g.Indices[poly.Start + (i + 1) % poly.Count]];
                Vector3 edge = b - a;
                float len = edge.Length;
                if (len < 1e-5f) continue;
                float side = Vector3.Dot(Vector3.Cross(edge, point - a), poly.Normal) / len;
                if (MathF.Abs(side) <= Eps) continue;
                int s = side > 0 ? 1 : -1;
                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
            return true;
        }

        private (int Wall, int Top) PartMaterials(Part part)
        {
            var walls = new Dictionary<int, int>();
            var tops = new Dictionary<int, int>();
            foreach (MapGeometry.Poly p in part.Nav.Polys)
            {
                Dictionary<int, int> d = p.Top ? tops : walls;
                d[p.Material] = d.TryGetValue(p.Material, out int n) ? n + 1 : 1;
            }
            int wall = walls.Count > 0 ? walls.OrderByDescending(e => e.Value).First().Key : tops.Count > 0 ? tops.Keys.First() : 0;
            int top = tops.Count > 0 ? tops.OrderByDescending(e => e.Value).First().Key : wall;
            return (wall, top);
        }
    }

    // Every map model's rooms and connectors in their planet's space (the first half's; the second half moved by
    // HalfOffsets), for the campaign to see which connector Samus is walking through while she plays (the Prime-style
    // map shows a connector once she has been in it). Built once, off the game's thread.
    public sealed class AreaMapIndex
    {
        private readonly Dictionary<string, (int Pair, Vector3 Origin)> _rooms = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(int Pair, int RoomId, Vector3 Min, Vector3 Max)> _roomBoxes = new();
        private readonly List<(int Pair, int Number, Vector3 Min, Vector3 Max)> _connectors = new();

        public static AreaMapIndex Build(string fileSystemRoot)
        {
            var index = new AreaMapIndex();
            foreach (string file in AreaMapModel.FindFiles(fileSystemRoot))
            {
                (int unit, int half) = AreaMapModel.UnitHalf(file);
                if (unit < 1 || unit > 4) continue;
                int pair = (unit - 1) * 2;
                Vector3 offset = half == 2 ? AreaMapModel.HalfOffsets[pair + 1] : Vector3.Zero;
                AreaMapModel map = AreaMapModel.Load(file);
                foreach (AreaMapModel.Part p in map.Parts)
                {
                    if (p.IsConnector)
                    {
                        index._connectors.Add((pair, p.ConnectorNumber, p.Min + offset, p.Max + offset));
                    }
                    else
                    {
                        index._rooms[p.Name] = (pair, p.Origin + offset);
                        if (p.RoomId >= 0) index._roomBoxes.Add((pair, p.RoomId, p.Min + offset, p.Max + offset));
                    }
                }
            }
            return index;
        }

        // the connector Samus is in: her position in her room's space, the room's MphRead name
        public (int Pair, int Number)? ConnectorAt(string room, Vector3 position)
        {
            if (!_rooms.TryGetValue(room, out var r)) return null;
            Vector3 p = position + r.Origin;
            const float Margin = 0.25f;
            foreach (var c in _connectors)
            {
                if (c.Pair != r.Pair) continue;
                if (p.X >= c.Min.X - Margin && p.X <= c.Max.X + Margin && p.Y >= c.Min.Y - Margin && p.Y <= c.Max.Y + Margin
                    && p.Z >= c.Min.Z - Margin && p.Z <= c.Max.Z + Margin)
                {
                    return (c.Pair, c.Number);
                }
            }
            return null;
        }

        // saves from before connectors were tracked: every connector between two visited rooms counts as walked through
        public void Seed(uint[] bits, Func<int, bool> visited)
        {
            const float Reach = 1.5f;
            foreach (var c in _connectors)
            {
                var near = _roomBoxes.Where(r => r.Pair == c.Pair && c.Min.X <= r.Max.X + Reach && c.Max.X >= r.Min.X - Reach
                    && c.Min.Y <= r.Max.Y + Reach && c.Max.Y >= r.Min.Y - Reach && c.Min.Z <= r.Max.Z + Reach && c.Max.Z >= r.Min.Z - Reach).ToList();
                if (near.Count >= 2 && near.All(r => visited(r.RoomId))) Set(bits, c.Pair, c.Number);
            }
        }

        // vanilla's layout (save +0x30, ov8): a word per area half, bit n-1 of word (area & ~1) + (n > 32)
        public static bool Has(uint[]? bits, int pair, int number)
        {
            if (bits == null || number < 1 || number > 64) return false;
            int word = pair + (number > 32 ? 1 : 0);
            return word >= 0 && word < bits.Length && (bits[word] & (1u << ((number - 1) & 31))) != 0;
        }

        public static bool Set(uint[] bits, int pair, int number)
        {
            if (number < 1 || number > 64) return false;
            int word = pair + (number > 32 ? 1 : 0);
            if (word < 0 || word >= bits.Length) return false;
            uint bit = 1u << ((number - 1) & 31);
            if ((bits[word] & bit) != 0) return false;
            bits[word] |= bit;
            return true;
        }
    }
}
