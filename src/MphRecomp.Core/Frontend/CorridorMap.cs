using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRecomp.Render;
using OpenTK.Mathematics;

// The door-bounded pieces of campaign rooms as areas of their own on the map (owner 2026-10-14: "find every single corridor
// that is technically part of another room, sever it from that room, and make it its own room? so that it doesnt show until
// you enter and so that it doesnt show bigger attached rooms until you get there. can we then label each corridor room
// "corridor A" "corridor b""; then: "no split for no door corridors", and a door-bounded side room with no name of its own
// (the ROM's LocationNames.bin names rooms only) is "Chamber A", "Chamber B"...). Prime 1 draws each hall as its own area,
// so its mini map zooms in once for a hall and back out at the next room.
//
// The pieces come from the game itself: MPH splits a room into parts at its collision portals, and a door inside a room
// (connector 255) closes the portal it sits on, so the parts joined only through a door are the door-bounded pieces
// (Tools -primehud roomparts: Celestial Gateway rmConnect, Helm Room rmjump + rmbugs, ...). Two doors sit inside one part
// with no portal (Tetra Vista D2, Echo Hall D17): D2 closes a side chamber the map draws as its own shape, so that one is
// split by its map shape; D17 has the same shape on both sides, so no split. Frost Labyrinth's morph cave (rmCave) has no
// shape of its own on the map (one flat slab draws the whole room), so it stays in the room. Lettered per planet:
// the landing site first, then the map's order. On the map each piece is the triangles of its map nodes (Synergy Core's
// pCube76 holds two chambers and a hall of the room: its triangles go by the game part's box they sit in).
//
// Seen: a piece shows once Samus has been in it (the room around it shows by the vanilla visited rule). Kept in the
// campaign save (CampaignSaveFile.MapPieces); a save without the field counts every piece of a visited room as seen, so
// old saves keep the map they had.
namespace MphRecomp.Frontend
{
    public static class CorridorMap
    {
        public sealed record Cut(string[] Nodes, Vector3 Min, Vector3 Max);

        // Room = MphRead's room name; Parts = the game's room parts it is; Nodes = its map model nodes (Cut: shared nodes,
        // the triangles whose centre is in the box); Min/Max = where Samus is in it (map space, the game parts' node box,
        // or with Sub the map shape's, telling two pieces of one part apart); OwnDoors = door entity ids only it touches
        // (hidden with it); Connectors = the map's connector parts past its own doors (hidden with it)
        public sealed record Piece(string Room, string Name, string[] Parts, string[] Nodes, Cut? Cut, Vector3 Min,
            Vector3 Max, bool Sub, int[] OwnDoors, string[] Connectors)
        {
            public string Key => Room.ToUpperInvariant() + "|" + Name;
        }

        public static readonly IReadOnlyList<Piece> Pieces = new Piece[]
        {
            // Alinos
            new("Unit1_C0", "Corridor A", new[] { "rmC0C" }, new[] { "pCube55", "polySurface1447" }, null,
                new(-62.73f, 1.54f, 0.63f), new(-28.79f, 11.40f, 21.59f), false, new[] { 1 }, new[] { "Con02" }),
            // Celestial Archives
            new("UNIT2_LAND", "Corridor A", new[] { "rmConnect" }, new[] { "pCube55" }, null,
                new(-4.01f, 3.15f, -1.80f), new(8.40f, 6.40f, 13.88f), false, new[] { 9 }, new[] { "Con01" }),
            new("UNIT2_C0", "Corridor B", new[] { "rmjump" }, new[] { "pCube58", "pCube59", "pCube61" }, null,
                new(-41.01f, 0.99f, -56.31f), new(-25.91f, 11.49f, -37.55f), false, new[] { 2 }, Array.Empty<string>()),
            new("UNIT2_C0", "Chamber A", new[] { "rmbugs" }, new[] { "pCube60" }, null,
                new(-54.84f, 1.67f, -71.30f), new(-33.34f, 17.82f, -47.57f), false, new[] { 1, 2 }, new[] { "Con02" }),
            new("UNIT2_C4", "Chamber B", new[] { "rmRory" }, Array.Empty<string>(), new Cut(new[] { "pCube76" }, new(-8.66f, 44.19f, 41.41f), new(11.35f, 56.47f, 62.29f)),
                new(-8.16f, 45.69f, 41.91f), new(10.85f, 54.97f, 61.79f), false, new[] { 4 }, Array.Empty<string>()),
            new("UNIT2_C4", "Chamber C", new[] { "rmEnd" }, Array.Empty<string>(), new Cut(new[] { "pCube76" }, new(7.21f, 46.33f, 29.20f), new(21.50f, 54.33f, 48.01f)),
                new(7.71f, 47.83f, 29.70f), new(21.00f, 52.83f, 47.51f), false, new[] { 4 }, Array.Empty<string>()),
            new("UNIT2_C4", "Chamber D", new[] { "rmTele" }, new[] { "polySurface60" }, null,
                new(-32.80f, 51.76f, 20.76f), new(-21.27f, 61.96f, 32.92f), false, Array.Empty<int>(), Array.Empty<string>()),
            new("UNIT2_RM3", "Chamber E", new[] { "rmLinkS" }, new[] { "pCube72", "pCylinder31" }, null,
                new(-28.85f, 35.60f, 20.16f), new(-25.08f, 41.66f, 25.55f), false, Array.Empty<int>(), Array.Empty<string>()),
            new("Unit2_C6", "Corridor C", new[] { "rmtele" }, new[] { "polySurface506" }, null,
                new(-47.17f, 0.09f, -3.68f), new(-36.54f, 3.34f, 6.95f), true, new[] { 2 }, Array.Empty<string>()),
            new("Unit2_C6", "Chamber F", new[] { "rmtele" }, new[] { "polySurface55792668" }, null,
                new(-36.55f, 0.00f, 1.32f), new(-29.55f, 3.50f, 8.82f), true, new[] { 2 }, Array.Empty<string>()),
            new("Unit2_C7", "Corridor D", new[] { "rmEntrance" }, new[] { "pTorus15" }, null,
                new(18.81f, -5.09f, 25.09f), new(50.53f, 8.66f, 56.77f), false, new[] { 16 }, Array.Empty<string>()),
            new("Unit2_C7", "Corridor E", new[] { "rmhall" }, new[] { "Poly1" }, null,
                new(40.96f, -0.69f, 6.40f), new(46.40f, 4.00f, 25.74f), false, new[] { 15, 16 }, new[] { "Con09" }),
            new("Unit2_C7", "Chamber G", new[] { "rmUgly" }, new[] { "polySurface55792669" }, null,
                new(-61.57f, 21.44f, 2.81f), new(-52.97f, 28.88f, 11.37f), false, Array.Empty<int>(), Array.Empty<string>()),
        };

        private static readonly Dictionary<string, Piece[]> _byRoom = Pieces.GroupBy(p => p.Room.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _seen = new();
        private static readonly Dictionary<AreaMapModel.Part, sbyte[]> _polyPiece = new();

        // bumps when a piece is first seen (the maps repaint)
        public static int Version { get; private set; }

        // the PRIME map's D-pad up (owner 2026-10-14: check the splits, and compare them with the whole rooms): All = every
        // piece shown as if entered; Off = no room has pieces (and every piece counts as seen), so both maps draw the rooms
        // as the game does
        public static bool Off { get; private set; }
        public static bool All { get; private set; }

        public static void SetView(bool off, bool all)
        {
            if (Off != off || All != all)
            {
                (Off, All) = (off, all);
                Version++;
            }
        }

        public static Piece[] Of(string room) => !Off && _byRoom.TryGetValue(room, out Piece[]? list) ? list : Array.Empty<Piece>();

        // the piece Samus is in: part = the game's room part she's in (PlayerEntity.NodeRef's), null when not known (the
        // parts' boxes then); mapPosition = her position + the room's map origin. null = the room itself
        public static Piece? At(string room, string? part, Vector3 mapPosition)
        {
            Piece[] list = Of(room);
            if (list.Length == 0)
            {
                return null;
            }
            Piece? found = null;
            float best = Single.MaxValue;
            foreach (Piece p in list)
            {
                bool inBox = Inside(p, mapPosition);
                if (part != null ? !p.Parts.Contains(part, StringComparer.OrdinalIgnoreCase) || p.Sub && !inBox : !inBox)
                {
                    continue;
                }
                Vector3 size = p.Max - p.Min;
                float volume = (size.X + 1) * (size.Y + 1) * (size.Z + 1);
                if (volume < best)
                {
                    best = volume;
                    found = p;
                }
            }
            if (found == null && part != null)
            {
                // a part shared by Sub pieces, standing just outside both shapes (a doorway): the nearest
                found = list.Where(p => p.Sub && p.Parts.Contains(part, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(p => Vector3.Distance(Vector3.Clamp(mapPosition, p.Min, p.Max), mapPosition)).FirstOrDefault();
            }
            return found;
        }

        // the game's own test for a spot in a part (RoomEntity.GetNodeRefContaining's margins)
        private static bool Inside(Piece p, Vector3 v)
        {
            return v.X >= p.Min.X - 0.5f && v.X <= p.Max.X + 0.5f && v.Z >= p.Min.Z - 0.5f && v.Z <= p.Max.Z + 0.5f
                && v.Y >= p.Min.Y - 1.5f && v.Y <= p.Max.Y + 1.5f;
        }

        public static bool Seen(Piece piece) => Off || All || _seen.Contains(piece.Key);

        public static void MarkSeen(Piece piece)
        {
            if (_seen.Add(piece.Key))
            {
                Version++;
            }
        }

        // the campaign save: the seen pieces' keys
        public static string[] ForSave() => _seen.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        // a loaded save: its keys; null (a save from before the field) = every piece of a room it visited
        public static void Adopt(string[]? keys, StorySave? story)
        {
            _seen.Clear();
            if (keys != null)
            {
                foreach (string key in keys)
                {
                    _seen.Add(key);
                }
            }
            else if (story != null)
            {
                foreach (Piece p in Pieces)
                {
                    (RoomMetadata? meta, int id) = Metadata.GetRoomByName(p.Room.ToUpperInvariant());
                    if (meta != null && story.CheckVisitedRoom(id))
                    {
                        _seen.Add(p.Key);
                    }
                }
            }
            Version++;
        }

        // each of a room part's map triangles (Part.Nav.Polys) -> its piece (index into Of(room)), -1 = the room itself.
        // Nav lists the triangles in AreaMapModel.Bake's node order (the room node's subtree first, then its own meshes),
        // so walking the nodes the same way names each triangle's node
        public static sbyte[] PolyPieces(AreaMapModel model, AreaMapModel.Part part)
        {
            if (_polyPiece.TryGetValue(part, out sbyte[]? known))
            {
                return known;
            }
            var result = new sbyte[part.Nav.Polys.Count];
            Array.Fill(result, (sbyte)-1);
            Piece[] list = Of(part.Name);
            if (list.Length > 0)
            {
                Model m = model.Model;
                int root = m.Nodes.ToList().FindIndex(n => n.ParentIndex == -1);
                int index = root < 0 ? -1 : m.Nodes.ToList().FindIndex(n => n.ParentIndex == root && n.Name == part.Name);
                if (index >= 0)
                {
                    int poly = 0;
                    void Mark(Node node)
                    {
                        sbyte whole = -1;
                        bool cut = false;
                        for (int i = 0; i < list.Length; i++)
                        {
                            if (list[i].Nodes.Contains(node.Name)) whole = (sbyte)i;
                            if (list[i].Cut?.Nodes.Contains(node.Name) == true) cut = true;
                        }
                        foreach (int meshId in node.GetMeshIds())
                        {
                            float[] v = DsDisplayList.Decode(m, m.Meshes[meshId], isRoom: false);
                            int count = v.Length / (3 * DsDisplayList.Stride);
                            for (int t = 0; t < count && poly < result.Length; t++, poly++)
                            {
                                result[poly] = cut ? CutPiece(list, node.Name, part.Nav, part.Nav.Polys[poly]) : whole;
                            }
                        }
                    }
                    void Walk(int first)
                    {
                        for (int i = first; i != -1; i = m.Nodes[i].NextIndex)
                        {
                            Mark(m.Nodes[i]);
                            Walk(m.Nodes[i].ChildIndex);
                        }
                    }
                    Walk(m.Nodes[index].ChildIndex);
                    Mark(m.Nodes[index]);
                }
            }
            _polyPiece[part] = result;
            return result;
        }

        // a shared node's triangle: the smallest cut box its centre is in, else the room
        private static sbyte CutPiece(Piece[] list, string node, MapGeometry g, MapGeometry.Poly p)
        {
            Vector3 c = Vector3.Zero;
            for (int i = 0; i < p.Count; i++)
            {
                c += g.Points[g.Indices[p.Start + i]];
            }
            c /= p.Count;
            sbyte found = -1;
            float best = Single.MaxValue;
            for (int i = 0; i < list.Length; i++)
            {
                Cut? cut = list[i].Cut;
                if (cut == null || !cut.Nodes.Contains(node) || c.X < cut.Min.X || c.X > cut.Max.X || c.Y < cut.Min.Y
                    || c.Y > cut.Max.Y || c.Z < cut.Min.Z || c.Z > cut.Max.Z)
                {
                    continue;
                }
                Vector3 size = cut.Max - cut.Min;
                float volume = size.X * size.Y * size.Z;
                if (volume < best)
                {
                    best = volume;
                    found = (sbyte)i;
                }
            }
            return found;
        }
    }
}
