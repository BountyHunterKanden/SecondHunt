using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Anim;
using MphRecomp.Render;
using OpenTK.Mathematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // The Trooper's Wall Cling look on MPH's own Weavel (the no-import fallback; owner 2026-10-09 "option 2": a hand-made
    // cling pose on Weavel's DS rig), on the PC: Weavel's DS model posed by the game's own two-layer posing (HunterRig.Pose)
    // from a clip frame, plus per-joint turns (Node.AfterTransform, as PlayerDraw turns the spine), baked by GeometryBaker
    // and drawn by a small z-buffered software rasteriser (texture + vertex colour, one light), front and side, beside a
    // wall plane. Mockups only.
    //   -weavelcling nodes                       Weavel's joints (index, parent, name, idle position)
    //   -weavelcling frames [clips=12,9,14]      sheet of every 4th frame of those clips (front + side)
    //   -weavelcling pose <spec> [<spec> ...]    one tile pair per spec; spec = name:clip,frame[;Node=rx,ry,rz;...] (degrees)
    //   [out=<dir>] (default extract_out/characters_mockups/weavel_cling)
    internal static class WeavelClingTool
    {
        static int Tile = 320;
        static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static void Run(string[] args)
        {
            string verb = args.Length > 1 ? args[1] : "nodes";
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out",
                "characters_mockups", "weavel_cling"));
            var rest = new List<string>();
            string clips = "12,9,14";
            foreach (string a in args.Skip(2))
            {
                if (a.StartsWith("out=")) outDir = a[4..];
                else if (a.StartsWith("clips=")) clips = a[6..];
                else if (a.StartsWith("size=")) Tile = Int32.Parse(a[5..], Ci);
                else rest.Add(a);
            }
            Directory.CreateDirectory(outDir);
            if (!ToolPaths.UseDesktopExtraction()) return;
            Model model = Read.GetModelInstance(Metadata.HunterModels[Hunter.Weavel][0]).Model;
            var anim = new BipedAnimator(model) { Manual = true };
            if (verb == "nodes")
            {
                Pose(anim, (int)PlayerAnimation.Idle, 0, null);
                for (int i = 0; i < model.Nodes.Count; i++)
                {
                    Node n = model.Nodes[i];
                    Vector3 p = n.Animation.ExtractTranslation();
                    Console.WriteLine($"{i,3} parent {n.ParentIndex,3}  {n.Name,-20} ({p.X,6:F2},{p.Y,6:F2},{p.Z,6:F2})");
                }
                for (int k = 0; k < model.AnimationGroups.Node.Count; k++)
                {
                    Console.WriteLine($"clip {k,2} ({(PlayerAnimation)k}): {model.AnimationGroups.Node[k].FrameCount} frames");
                }
                return;
            }
            if (verb == "probe")
            {
                // per joint and axis: where its first child (and that child's child) moves for +30 degrees, in his own
                // terms: right (+X), up (+Y), forward (-Z)
                string[] joints = rest.Count > 0 ? rest.ToArray() : new[] { "L_hip", "L_knee", "R_hip", "R_knee", "L_shoulder", "L_elbow", "R_shoulder", "Spine_2", "Head_1" };
                foreach (string j in joints)
                {
                    int ji = model.Nodes.ToList().FindIndex(n => n.Name == j);
                    if (ji < 0) continue;
                    int c1 = model.Nodes[ji].ChildIndex;
                    int c2 = c1 >= 0 ? model.Nodes[c1].ChildIndex : -1;
                    Pose(anim, 8, 0, null);
                    Vector3 a1 = c1 >= 0 ? model.Nodes[c1].Animation.ExtractTranslation() : Vector3.Zero;
                    Vector3 a2 = c2 >= 0 ? model.Nodes[c2].Animation.ExtractTranslation() : a1;
                    foreach ((string ax, Vector3 deg) in new[] { ("X", new Vector3(30, 0, 0)), ("Y", new Vector3(0, 30, 0)), ("Z", new Vector3(0, 0, 30)) })
                    {
                        Pose(anim, 8, 0, new Dictionary<string, Vector3> { [j] = deg });
                        Vector3 b1 = c1 >= 0 ? model.Nodes[c1].Animation.ExtractTranslation() : Vector3.Zero;
                        Vector3 b2 = c2 >= 0 ? model.Nodes[c2].Animation.ExtractTranslation() : b1;
                        string D(Vector3 d) => $"right {d.X,6:F2} up {d.Y,6:F2} fwd {-d.Z,6:F2}";
                        Console.WriteLine($"{j,-11} +30 {ax}: child {D(b1 - a1)} | grandchild {D(b2 - a2)}");
                    }
                }
                return;
            }
            var tiles = new List<(string, Rgba32[])>();
            if (verb == "frames")
            {
                foreach (string cs in clips.Split(','))
                {
                    int clip = Int32.Parse(cs, Ci);
                    anim.Legs.SetAnimation(clip);
                    int count = anim.Legs.AnimInfo.FrameCount[0];
                    for (int f = 0; f < count; f += 4)
                    {
                        Pose(anim, clip, f, null);
                        AddTiles(model, tiles, $"{(PlayerAnimation)clip} {f}/{count}");
                    }
                }
            }
            else
            {
                foreach (string spec in rest)
                {
                    string[] parts = spec.Split(';');
                    string[] head = parts[0].Split(':');
                    string[] cf = head[1].Split(',');
                    var turns = new Dictionary<string, Vector3>();
                    foreach (string t in parts.Skip(1))
                    {
                        string[] kv = t.Split('=');
                        float[] v = kv[1].Split(',').Select(x => Single.Parse(x, Ci)).ToArray();
                        turns[kv[0]] = new Vector3(v[0], v[1], v[2]);
                    }
                    Pose(anim, Int32.Parse(cf[0], Ci), Int32.Parse(cf[1], Ci), turns);
                    AddTiles(model, tiles, head[0]);
                }
            }
            string path = Path.Combine(outDir, $"weavel_cling_{verb}.png");
            SaveSheet(tiles, path);
        }

        static void Pose(BipedAnimator anim, int clip, int frame, Dictionary<string, Vector3>? turns)
        {
            Model model = anim.Model;
            anim.Legs.SetAnimation(clip);
            anim.Legs.AnimInfo.Frame[0] = Math.Min(frame, anim.Legs.AnimInfo.FrameCount[0] - 1);
            anim.Torso.SetAnimation(clip);
            anim.Torso.AnimInfo.Frame[0] = anim.Legs.AnimInfo.Frame[0];
            HunterRig.Pose(anim, 0, new float[16 * HunterRig.PaletteSize(model)]);
            if (turns != null)
            {
                var list = new List<(int, Matrix4)>();
                foreach ((string name, Vector3 deg) in turns)
                {
                    int index = model.Nodes.ToList().FindIndex(n => n.Name == name);
                    if (index < 0)
                    {
                        Console.WriteLine($"  no joint {name}");
                        continue;
                    }
                    list.Add((index, Matrix4.CreateRotationX(MathHelper.DegreesToRadians(deg.X))
                        * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(deg.Y))
                        * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(deg.Z))));
                }
                TurnJoints(model, list);
                model.UpdateMatrixStack();
            }
        }

        // each joint turned in its own frame (row vectors: turn * world), its descendants carried along; parents first
        static void TurnJoints(Model model, List<(int Node, Matrix4 Turn)> turns)
        {
            foreach ((int index, Matrix4 turn) in turns.OrderBy(t => t.Node))
            {
                Matrix4 before = model.Nodes[index].Animation;
                Matrix4 after = turn * before;
                Matrix4 delta = before.Inverted() * after;
                model.Nodes[index].Animation = after;
                for (int i = 0; i < model.Nodes.Count; i++)
                {
                    int p = model.Nodes[i].ParentIndex;
                    while (p != -1 && p != index) p = model.Nodes[p].ParentIndex;
                    if (p == index) model.Nodes[i].Animation = model.Nodes[i].Animation * delta;
                }
            }
        }

        static void AddTiles(Model model, List<(string, Rgba32[])> tiles, string label)
        {
            List<GeometryBaker.Batch> batches = GeometryBaker.Bake(model, new float[3], out _, useAnimation: true, useMatrixStack: true);
            tiles.Add((label + " front", Raster(batches, front: true)));
            tiles.Add(("3/4", Raster(batches, front: true, yaw: 50)));
            tiles.Add(("side", Raster(batches, front: false)));
        }

        // orthographic; the DS rig faces -Z: front = looking at his face; side = his facing to the right, the wall (behind
        // him, +Z) drawn as a grey band on the left of the tile
        static Rgba32[] Raster(List<GeometryBaker.Batch> batches, bool front, float yaw = 0)
        {
            var px = new Rgba32[Tile * Tile];
            var depth = new float[Tile * Tile];
            Array.Fill(px, new Rgba32(40, 44, 52, 255));
            Array.Fill(depth, Single.MaxValue);
            float unit = Tile / 2.6f; // 2.6 model units across
            float cx = Tile / 2f, floorY = Tile - 16;
            var light = Vector3.Normalize(new Vector3(0.4f, 0.8f, front ? -0.5f : 0.4f));
            float cy = MathF.Cos(MathHelper.DegreesToRadians(yaw)), sy = MathF.Sin(MathHelper.DegreesToRadians(yaw));
            (float X, float Y, float Z) Project(Vector3 p) => Proj(new Vector3(p.X * cy + p.Z * sy, p.Y, -p.X * sy + p.Z * cy));
            (float X, float Y, float Z) Proj(Vector3 p) => front
                ? (cx - p.X * unit, floorY - p.Y * unit, p.Z)
                : (cx - p.Z * unit, floorY - p.Y * unit, -p.X);
            if (!front)
            {
                // the wall he clings to, behind him (+Z = left of the tile) at 0.45 units
                int wx = (int)(cx - 0.45f * unit);
                for (int y = 0; y < Tile; y++)
                {
                    for (int x = 0; x < wx && x < Tile; x++) px[y * Tile + x] = new Rgba32(70, 74, 82, 255);
                }
            }
            foreach (GeometryBaker.Batch b in batches)
            {
                int stride = b.Stride;
                List<float> v = b.Verts;
                for (int t = 0; t + stride * 3 <= v.Count; t += stride * 3)
                {
                    var p = new Vector3[3];
                    var uv = new Vector2[3];
                    var col = new Vector3[3];
                    for (int k = 0; k < 3; k++)
                    {
                        int o = t + k * stride;
                        p[k] = new Vector3(v[o], v[o + 1], v[o + 2]);
                        uv[k] = new Vector2(v[o + 6], v[o + 7]);
                        col[k] = new Vector3(v[o + 8], v[o + 9], v[o + 10]);
                    }
                    Vector3 n = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
                    if (n.LengthSquared < 1e-14f) continue;
                    n.Normalize();
                    float shade = 0.45f + 0.55f * MathF.Abs(Vector3.Dot(n, light));
                    var s = new (float X, float Y, float Z)[3];
                    for (int k = 0; k < 3; k++) s[k] = Project(p[k]);
                    int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(s[0].X, MathF.Min(s[1].X, s[2].X))));
                    int x1 = Math.Min(Tile - 1, (int)MathF.Ceiling(MathF.Max(s[0].X, MathF.Max(s[1].X, s[2].X))));
                    int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(s[0].Y, MathF.Min(s[1].Y, s[2].Y))));
                    int y1 = Math.Min(Tile - 1, (int)MathF.Ceiling(MathF.Max(s[0].Y, MathF.Max(s[1].Y, s[2].Y))));
                    float area = (s[1].X - s[0].X) * (s[2].Y - s[0].Y) - (s[2].X - s[0].X) * (s[1].Y - s[0].Y);
                    if (MathF.Abs(area) < 1e-6f) continue;
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            float fx = x + 0.5f, fy = y + 0.5f;
                            float w0 = ((s[1].X - fx) * (s[2].Y - fy) - (s[2].X - fx) * (s[1].Y - fy)) / area;
                            float w1 = ((s[2].X - fx) * (s[0].Y - fy) - (s[0].X - fx) * (s[2].Y - fy)) / area;
                            float w2 = 1 - w0 - w1;
                            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                            float z = w0 * s[0].Z + w1 * s[1].Z + w2 * s[2].Z;
                            int i = y * Tile + x;
                            if (z >= depth[i]) continue;
                            Vector3 c = w0 * col[0] + w1 * col[1] + w2 * col[2];
                            if (b.Pixels != null && b.W > 0 && b.H > 0)
                            {
                                Vector2 q = w0 * uv[0] + w1 * uv[1] + w2 * uv[2];
                                int tx = ((int)MathF.Floor(q.X * b.W) % b.W + b.W) % b.W;
                                int ty = ((int)MathF.Floor(q.Y * b.H) % b.H + b.H) % b.H;
                                int texel = b.Pixels[ty * b.W + tx];
                                if (((texel >> 24) & 0xFF) < 128) continue;
                                c *= new Vector3((texel & 0xFF) / 255f, ((texel >> 8) & 0xFF) / 255f, ((texel >> 16) & 0xFF) / 255f);
                            }
                            c *= shade;
                            depth[i] = z;
                            px[i] = new Rgba32((byte)Math.Clamp(c.X * 255, 0, 255), (byte)Math.Clamp(c.Y * 255, 0, 255),
                                (byte)Math.Clamp(c.Z * 255, 0, 255), 255);
                        }
                    }
                }
            }
            // the floor
            for (int x = 0; x < Tile; x++) px[(int)floorY * Tile + x] = new Rgba32(60, 200, 90, 255);
            return px;
        }

        static void SaveSheet(List<(string Label, Rgba32[] Px)> tiles, string path)
        {
            int cols = 3, rows = (tiles.Count + cols - 1) / cols;
            using var img = new Image<Rgba32>(cols * Tile, rows * Tile, new Rgba32(40, 44, 52, 255));
            for (int k = 0; k < tiles.Count; k++)
            {
                int ox = k % cols * Tile, oy = k / cols * Tile;
                for (int y = 0; y < Tile; y++)
                {
                    for (int x = 0; x < Tile; x++) img[ox + x, oy + y] = tiles[k].Px[y * Tile + x];
                }
            }
            img.SaveAsPng(path);
            Console.WriteLine($"sheet {path}: {tiles.Count / 3} poses (front, 3/4, side each):");
            for (int k = 0; k < tiles.Count; k += 3) Console.WriteLine($"  {k / 3}: {tiles[k].Label}");
        }
    }
}
