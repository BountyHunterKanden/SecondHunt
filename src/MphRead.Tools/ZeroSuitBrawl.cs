using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Assets;
using MphRecomp.Campaign;
using MphRecomp.Import.Brawl;
using MphRecomp.Import.BrawlBody;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using OpenTK.Mathematics;

namespace MphRead
{
    // -zsbrawl <command> (Brawl Zero Suit Samus on her own rig, docs/ZERO_SUIT_SAMUS.md section 8):
    //   dsbones                      vanilla Samus's DS skeleton: each node's parent and its model-space spot and axes in
    //                                her Idle clip's first frame (the rest the strafes and aiming are retargeted from)
    //   pose <data root> <body> <clip[+torso][@yaw:pitch],...> <png>   (-bodytest --bones on it, DS clips added)
    //   proto <dump dir> <fighter pac> <out dir>
    //                                PC STUDY PROTOTYPE, never shipped: her fighter body written as bodies/<out> from a
    //                                black-box study export (a .dae + per-frame clip values, made on the PC), so the owner
    //                                can judge the look on the device before the clean-room import exists
    internal static class ZeroSuitBrawl
    {
        public static void Run(string[] args)
        {
            string cmd = args.Length >= 2 ? args[1] : "";
            Paths.UpdatePaths();
            Paths.ChooseMphPath();
            if (cmd == "dsbones") DsBones();
            else if (cmd == "proto" && args.Length >= 5)
            {
                string? Opt(string name) => Array.IndexOf(args, name) is int i && i >= 5 && i + 1 < args.Length ? args[i + 1] : null;
                Proto(args[2], args[3], args[4], Opt("--ual"), Opt("--faces"));
            }
            // her body's clips posed and drawn (-bodytest --bones), with the DS clips put on her at load as the app does
            else if (cmd == "pose" && args.Length >= 6 && (BodyTest.RunAfterPose = true)) BodyTest.Run(new[] { "-bodytest", args[3], "--out", args[2], "--bones", args[4], "--render", args[5] });
            // rawpose: the clips as they are, no AfterPose (her own Brawl gun hold, for comparison)
            else if (cmd == "rawpose" && args.Length >= 6) BodyTest.Run(new[] { "-bodytest", args[3], "--out", args[2], "--bones", args[4], "--render", args[5] });
            // legs: each leg through a clip, frame by frame (where the knee and toes point, each joint's twist about its bone)
            else if (cmd == "legs" && args.Length >= 5 && (BrawlZeroSuitRig.Straight = !args.Contains("nolegs")) is var _) Legs(Path.Combine(args[2], "bodies", args[3]), args[4], args.Skip(5).Contains("raw") | (Gait = args.Contains("gait")) & false | (Joints = args.Contains("joints")) & false,
                args.Skip(5).FirstOrDefault(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ?? "");
            // hand: the gun hand close up (her side, from above, from her front), frame 0 of each clip, after the pose
            else if (cmd == "hand" && args.Length >= 6) Hand(Path.Combine(args[2], "bodies", args[3]), args[4], args[5]);
            else if (cmd == "tree" && args.Length >= 4) Tree(Path.Combine(args[2], "bodies", args[3]));
            else if (cmd == "torso" && args.Length >= 5 && (BrawlZeroSuitRig.Hair = !args.Contains("nohair")) is var _ && (Probe = args.FirstOrDefault(a => a.StartsWith("probe=")) is string pr ? float.Parse(pr[6..]) : 0) >= 0
                && (TileKeys = args.FirstOrDefault(a => a.StartsWith("keys=")) is string ks ? int.Parse(ks[5..]) : 12) > 0) Torso(Path.Combine(args[2], "bodies", args[3]), args[4], args.Skip(5).FirstOrDefault(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ?? "");
            else Console.WriteLine("usage: -zsbrawl dsbones | proto <dump dir> <fighter pac> <out dir> | pose <data root> <body> <clips> <png> | legs <data root> <body> <clips> [raw] [png]");
        }

        // degrees: knee = where the bent knee points (yaw from her forward, + = toward her left; '-' when nearly straight),
        // toe = the foot's heading (its side axis turned a quarter, so a foot pointing down still reads), bend = the knee's bend; tw = each joint's twist about its own bone against its parent's
        // (the roll a skinned mesh wraps around), thigh / shin / foot. png: 12 frames of each clip, front + side
        static bool Gait, Joints;
        static float Probe;
        static int TileKeys = 12;

        static void Legs(string dir, string clipsArg, bool raw, string png)
        {
            var tiles = new List<(string Label, SixLabors.ImageSharp.PixelFormats.Rgba32[] Px)>();
            NativeBody b = NativeBody.Load(dir);
            BodyClips? map = BodyClips.For(b);
            HdGun g = b.Data;
            Vector3 At(int i) => b.World[i].ExtractTranslation();
            Vector3 BindAt(int i) => g.InvBind[i].Inverted().ExtractTranslation();
            static float Yaw(Vector3 v) => MathF.Abs(v.X) + MathF.Abs(v.Z) < 1e-4f ? float.NaN : MathHelper.RadiansToDegrees(MathF.Atan2(v.X, v.Z));
            Vector3 Side(int i) => Vector3.TransformVector(Vector3.UnitX, Delta(i));
            Matrix4 Delta(int i) { Matrix4 m = g.InvBind[i] * b.World[i]; m.Row3 = Vector4.UnitW; return m; }
            float Twist(int bone, int child)
            {
                Vector3 axis = (BindAt(child) - BindAt(bone)).Normalized();
                // row vectors: a bind-space vector through the bone, back through its parent = the bone's own turn
                Quaternion rel = (Delta(bone) * Delta(g.Parent[bone]).Inverted()).ExtractRotation();
                rel.Normalize();
                return MathHelper.RadiansToDegrees(2 * MathF.Atan2(Vector3.Dot(rel.Xyz, axis), rel.W));
            }
            foreach (string clip in clipsArg.Split(','))
            {
                if (b.Clip(clip) is not HdGun.Anim an) { Console.WriteLine($"{clip}: missing"); continue; }
                Console.WriteLine($"{clip} ({an.Duration:F2} s){(raw ? " raw" : "")}");
                Console.WriteLine("   t    | L knee  toe  bend | tw thigh  shin  foot || R knee  toe  bend | tw thigh  shin  foot");
                for (int k = 0; k < 24; k++)
                {
                    float t = an.Duration * k / 24;
                    b.Pose(an, t);
                    if (!raw) map?.AfterPose?.Invoke(b, null);
                    if (png.Length > 0 && k % 2 == 0)
                    {
                        tiles.Add(($"{clip} {t:F2}", LegSplat(b, Joints ? 90 : 0)));
                        tiles.Add((Joints ? "other side" : "back", LegSplat(b, Joints ? 270 : 180)));
                    }
                    string line = $"  {t,5:F2} ";
                    foreach (string s in new[] { "L", "R" })
                    {
                        int hip = b.Bone(s + "LegJ"), knee = b.Bone(s + "KneeJ"), foot = b.Bone(s + "FootJ"), toe = b.Bone(s + "ToeN");
                        Vector3 h = At(hip), kn = At(knee), f = At(foot), to = At(toe);
                        Vector3 line2 = (f - h).Normalized();
                        Vector3 off = kn - h - line2 * Vector3.Dot(kn - h, line2);
                        float bend = 180 - MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot((h - kn).Normalized(), (f - kn).Normalized()), -1, 1)));
                        string kneeYaw = bend < 8 ? "    -" : $"{Yaw(off),5:F0}";
                        line += $"| {kneeYaw} {Yaw(new Vector3(-Side(foot).Z, 0, Side(foot).X)),5:F0} {bend,5:F0} | {Twist(hip, knee),8:F0} {Twist(knee, foot),5:F0} {Twist(foot, toe),5:F0} ";
                    }
                    Vector3 hs = Side(b.Bone("HipN"));
                    if (Joints)
                    {
                        // each joint's bend (degrees): hip = thigh off straight down, knee = shin off the thigh's line (and
                        // which way the knee points: + ahead), ankle = foot off square to the shin (+ toes up), and the toe
                        static float Ang(Vector3 a, Vector3 c) => MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(a.Normalized(), c.Normalized()), -1, 1)));
                        string J(string s)
                        {
                            Vector3 h = At(b.Bone(s + "LegJ")), kn = At(b.Bone(s + "KneeJ")), f = At(b.Bone(s + "FootJ")), to = At(b.Bone(s + "ToeN"));
                            Vector3 line2 = (f - h).Normalized(), off = kn - h - line2 * Vector3.Dot(kn - h, line2);
                            float ahead = off.Length < 1e-3f ? 0 : Vector3.Dot(off.Normalized(), Vector3.UnitZ);
                            int tc = Array.IndexOf(g.Parent, b.Bone(s + "ToeN"));
                            string toe = tc >= 0 ? $"{Ang(to - f, At(tc) - to),4:F0}" : "   -";
                            return $"{s} hip {Ang(kn - h, -Vector3.UnitY),4:F0} knee {Ang(kn - h, f - kn),4:F0} ({ahead,5:F2}) ankle {Ang(f - kn, to - f) - 90,4:F0} toe {toe}";
                        }
                        Console.WriteLine($"  {t,5:F2} | {J("L")} | {J("R")}");
                        continue;
                    }
                    if (Gait)
                    {
                        // the pelvis's sway (x), yaw and roll, and where each knee and ankle sit across (x) and ahead (z)
                        int hp = b.Bone("HipN");
                        Vector3 hf = Vector3.TransformVector(Vector3.UnitZ, Delta(hp));
                        string G(string s) => $"{s} thigh x {At(b.Bone(s + "LegJ")).X,5:F2} knee x {At(b.Bone(s + "KneeJ")).X,5:F2} ankle x {At(b.Bone(s + "FootJ")).X,5:F2} y {At(b.Bone(s + "FootJ")).Y,5:F2} z {At(b.Bone(s + "FootJ")).Z,5:F2}";
                        Console.WriteLine($"  {t,5:F2} hip x {At(hp).X,5:F2} y {At(hp).Y,5:F2} yaw {Yaw(hf),4:F0} roll {MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(hs.Y, -1, 1))),4:F0} | {G("L")} | {G("R")}");
                        continue;
                    }
                    string Plane(string s)
                    {
                        Vector3 u = At(b.Bone(s + "KneeJ")) - At(b.Bone(s + "LegJ")), w = At(b.Bone(s + "FootJ")) - At(b.Bone(s + "KneeJ"));
                        Vector3 n = Vector3.Cross(u, w);
                        return (n.LengthSquared < 1e-8f ? "-" : $"{Vector3.Dot(n.Normalized(), hs):F2}") + $" u({u.X:F1},{u.Y:F1},{u.Z:F1}) w({w.X:F1},{w.Y:F1},{w.Z:F1})";
                    }
                    Console.WriteLine(line + $" plane.side L {Plane("L")} R {Plane("R")}");
                }
            }
            if (tiles.Count > 0) BodyTest.SaveSheet(tiles, png);
        }

        // her torso per key (after AfterPose): each spine bone's turn off its bind in model space as yaw (+ toward her
        // left), pitch (+ leaning forward) and roll (+ her left side down); png: whole body, front / side / back, 12 keys
        static void Torso(string dir, string clipsArg, string png)
        {
            var tiles = new List<(string Label, SixLabors.ImageSharp.PixelFormats.Rgba32[] Px)>();
            NativeBody b = NativeBody.Load(dir);
            BodyClips? map = BodyClips.For(b);
            string[] names = { "HipN", "BodyN", "BustN", "NeckN", "HeadN" };
            if (map?.AfterPose?.Target is BrawlZeroSuitRig r0) Console.WriteLine(r0.HairInfo());
            foreach (string clip in clipsArg.Split(','))
            {
                if (b.Clip(clip) is not HdGun.Anim an) { Console.WriteLine($"{clip}: missing"); continue; }
                Console.WriteLine($"{clip} ({an.Duration:F2} s)   yaw/pitch/roll: " + string.Join("  ", names));
                for (int k = 0; k <= 24; k++)
                {
                    float t = an.Duration * k / 24;
                    b.Pose(an, t);
                    map?.AfterPose?.Invoke(b, null);
                    string line = $"  {t,5:F2}";
                    foreach (string n in names)
                    {
                        int i = b.Bone(n);
                        Matrix4 r = b.Data.InvBind[i] * b.World[i];
                        Vector3 f = Vector3.TransformVector(Vector3.UnitZ, r).Normalized(), s = Vector3.TransformVector(Vector3.UnitX, r).Normalized();
                        float yaw = MathHelper.RadiansToDegrees(MathF.Atan2(f.X, f.Z)), pitch = MathHelper.RadiansToDegrees(-MathF.Asin(Math.Clamp(f.Y, -1, 1)));
                        float roll = MathHelper.RadiansToDegrees(-MathF.Asin(Math.Clamp(s.Y, -1, 1)));
                        line += $" | {yaw,4:F0} {pitch,4:F0} {roll,4:F0}";
                    }
                    if (map?.AfterPose?.Target is BrawlZeroSuitRig rig) line += " | hair in " + string.Join(" ", rig.HairDepth(b).Select(x => x.ToString("F2")));
                    if (Probe > 0 && map?.AfterPose?.Target is BrawlZeroSuitRig rp)
                    {
                        // the ponytail swung forward into her by `Probe` degrees at its root, then cleared
                        int pt = b.Bone("Ponytail01N");
                        Vector3 at = b.World[pt].ExtractTranslation();
                        b.SetWorld(pt, b.World[pt] * Matrix4.CreateTranslation(-at) * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(-Probe)) * Matrix4.CreateTranslation(at));
                        line += " | swung " + string.Join(" ", rp.HairDepth(b).Select(x => x.ToString("F2")));
                        rp.Collide(b);
                        line += " | cleared " + string.Join(" ", rp.HairDepth(b).Select(x => x.ToString("F2")));
                    }
                    Console.WriteLine(line);
                    if (png.Length > 0 && k % (24 / TileKeys) == 0 && k < 24)
                    {
                        Vector3 c = b.World[b.Bone("BustN")].ExtractTranslation() + new Vector3(0, -2, 0);
                        tiles.Add(($"{clip} {t:F2}", Close(b, c, 8f, Matrix4.Identity)));
                        tiles.Add(("left side", Close(b, c, 8f, Matrix4.CreateRotationY(MathHelper.DegreesToRadians(-90)))));
                        tiles.Add(("back", Close(b, c, 8f, Matrix4.CreateRotationY(MathHelper.DegreesToRadians(180)))));
                    }
                }
            }
            if (tiles.Count > 0) BodyTest.SaveSheet(tiles, png);
        }

        // the skeleton: each bone under its parent, with where it sits at bind
        static void Tree(string dir)
        {
            NativeBody b = NativeBody.Load(dir);
            HdGun g = b.Data;
            foreach (int i in g.Order)
            {
                int depth = 0;
                for (int q = g.Parent[i]; q >= 0; q = g.Parent[q]) depth++;
                Vector3 at = g.InvBind[i].Inverted().ExtractTranslation();
                Console.WriteLine($"{i,3} {new string(' ', depth * 2)}{g.Names[i]}  ({at.X:F2}, {at.Y:F2}, {at.Z:F2})");
            }
        }

        static void Hand(string dir, string clipsArg, string png)
        {
            var tiles = new List<(string Label, SixLabors.ImageSharp.PixelFormats.Rgba32[] Px)>();
            NativeBody b = NativeBody.Load(dir);
            BodyClips? map = BodyClips.For(b);
            foreach (string clip in clipsArg.Split(','))
            {
                if (b.Clip(clip) is not HdGun.Anim an) { Console.WriteLine($"{clip}: missing"); continue; }
                b.Pose(an, 0);
                map?.AfterPose?.Invoke(b, null);
                Vector3 c = b.World[b.Bone("RHandN")].ExtractTranslation();
                tiles.Add(($"{clip} side", Close(b, c, 1.6f, Matrix4.CreateRotationY(MathHelper.DegreesToRadians(-90)))));
                tiles.Add(("top", Close(b, c, 1.6f, Matrix4.CreateRotationX(MathHelper.DegreesToRadians(90)))));
                tiles.Add(("front", Close(b, c, 1.6f, Matrix4.Identity)));
                Vector3 ch = b.World[b.Bone("RBreastN")].ExtractTranslation();
                tiles.Add(("chest front", Close(b, ch, 3f, Matrix4.Identity)));
                tiles.Add(("chest top", Close(b, ch, 3f, Matrix4.CreateRotationX(MathHelper.DegreesToRadians(90)))));
                tiles.Add(("chest right", Close(b, ch, 3f, Matrix4.CreateRotationY(MathHelper.DegreesToRadians(90)))));
            }
            BodyTest.SaveSheet(tiles, png);
        }

        // the skin round `center` (half: the view's half width), orthographic through `view`, shaded by face
        static SixLabors.ImageSharp.PixelFormats.Rgba32[] Close(NativeBody b, Vector3 center, float half, Matrix4 view)
        {
            const int Tile = 280;
            HdGun g = b.Data;
            var px = new SixLabors.ImageSharp.PixelFormats.Rgba32[Tile * Tile];
            var depth = new float[Tile * Tile];
            Array.Fill(px, new SixLabors.ImageSharp.PixelFormats.Rgba32(40, 44, 52, 255));
            Array.Fill(depth, Single.MaxValue);
            float scale = Tile / (2 * half);
            var light = new Vector3(0.4f, 0.6f, 0.7f).Normalized();
            var pal = new float[32 * 16];
            for (int m = 0; m < g.Materials.Length; m++)
            {
                HdGun.Material mat = g.Materials[m];
                b.FillPalette(m, pal);
                var s = new Vector3[mat.VertexCount];
                for (int v = 0; v < mat.VertexCount; v++)
                {
                    int o = v * HdGun.FloatsPerVertex;
                    var p = new Vector4(mat.Vertices[o], mat.Vertices[o + 1], mat.Vertices[o + 2], 1);
                    Vector4 acc = Vector4.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = mat.Vertices[o + 15 + k];
                        if (w <= 0) continue;
                        int bi = (int)mat.Vertices[o + 11 + k] * 16;
                        var M = new Matrix4(pal[bi], pal[bi + 1], pal[bi + 2], pal[bi + 3], pal[bi + 4], pal[bi + 5], pal[bi + 6], pal[bi + 7],
                            pal[bi + 8], pal[bi + 9], pal[bi + 10], pal[bi + 11], pal[bi + 12], pal[bi + 13], pal[bi + 14], pal[bi + 15]);
                        acc += w * (p * M);
                    }
                    Vector3 q = Vector3.TransformPosition(acc.Xyz - center, view);
                    s[v] = new Vector3(-q.X, q.Y, -q.Z);
                }
                for (int t = 0; t + 2 < s.Length; t += 3)
                {
                    Vector3 a = s[t], c1 = s[t + 1], c2 = s[t + 2];
                    Vector3 n = Vector3.Cross(c1 - a, c2 - a);
                    if (n.LengthSquared < 1e-12f) continue;
                    n = n.Normalized();
                    float shade = 0.25f + 0.75f * Math.Abs(Vector3.Dot(n, light));
                    var col = new SixLabors.ImageSharp.PixelFormats.Rgba32((byte)(200 * shade), (byte)(170 * shade), (byte)(230 * shade), 255);
                    Vector2 P(Vector3 v) => new(Tile / 2f + v.X * scale, Tile / 2f - v.Y * scale);
                    Vector2 p0 = P(a), p1 = P(c1), p2 = P(c2);
                    int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
                    int x1 = Math.Min(Tile - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
                    int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
                    int y1 = Math.Min(Tile - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
                    float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
                    if (MathF.Abs(area) < 1e-6f) continue;
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            float w0 = ((p1.X - x) * (p2.Y - y) - (p2.X - x) * (p1.Y - y)) / area;
                            float w1 = ((p2.X - x) * (p0.Y - y) - (p0.X - x) * (p2.Y - y)) / area;
                            float w2 = 1 - w0 - w1;
                            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                            float z = w0 * a.Z + w1 * c1.Z + w2 * c2.Z;
                            int i = y * Tile + x;
                            if (z >= depth[i]) continue;
                            depth[i] = z;
                            px[i] = col;
                        }
                    }
                }
            }
            return px;
        }

        // her legs close up (hips down), orthographic, from `yaw` degrees round her (0 = facing us, 180 = her back), each
        // leg's skin coloured by which side of it the vertex sat at in her bind -- red front, blue back, green outside, yellow
        // inside -- so a roll of the leg shows as the bands turning round it; shaded by face
        static SixLabors.ImageSharp.PixelFormats.Rgba32[] LegSplat(NativeBody b, float yaw)
        {
            const int Tile = 280;
            HdGun g = b.Data;
            var px = new SixLabors.ImageSharp.PixelFormats.Rgba32[Tile * Tile];
            var depth = new float[Tile * Tile];
            Array.Fill(px, new SixLabors.ImageSharp.PixelFormats.Rgba32(40, 44, 52, 255));
            Array.Fill(depth, Single.MaxValue);
            float top = b.Height * 0.58f, scale = Tile / (top * 1.05f);
            Matrix4 view = Matrix4.CreateRotationY(MathHelper.DegreesToRadians(-yaw));
            var light = new Vector3(0.4f, 0.6f, 0.7f).Normalized();
            int lk = b.Bone("LKneeJ"), rk = b.Bone("RKneeJ");
            Vector3 lKnee = g.InvBind[lk].Inverted().ExtractTranslation(), rKnee = g.InvBind[rk].Inverted().ExtractTranslation();
            float hipY = g.InvBind[b.Bone("LLegJ")].Inverted().ExtractTranslation().Y;
            var pal = new float[32 * 16];
            for (int m = 0; m < g.Materials.Length; m++)
            {
                HdGun.Material mat = g.Materials[m];
                b.FillPalette(m, pal);
                var s = new Vector3[mat.VertexCount];
                var hue = new Vector3[mat.VertexCount];
                for (int v = 0; v < mat.VertexCount; v++)
                {
                    int o = v * HdGun.FloatsPerVertex;
                    var p = new Vector4(mat.Vertices[o], mat.Vertices[o + 1], mat.Vertices[o + 2], 1);
                    Vector4 acc = Vector4.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = mat.Vertices[o + 15 + k];
                        if (w <= 0) continue;
                        int bi = (int)mat.Vertices[o + 11 + k] * 16;
                        var M = new Matrix4(pal[bi], pal[bi + 1], pal[bi + 2], pal[bi + 3], pal[bi + 4], pal[bi + 5], pal[bi + 6], pal[bi + 7],
                            pal[bi + 8], pal[bi + 9], pal[bi + 10], pal[bi + 11], pal[bi + 12], pal[bi + 13], pal[bi + 14], pal[bi + 15]);
                        acc += w * (p * M);
                    }
                    Vector3 q = Vector3.TransformPosition(acc.Xyz, view);
                    s[v] = new Vector3(-q.X, q.Y - b.Feet, -q.Z);
                    // which side of its leg the vertex sits at in the bind (a vertical line through the knee)
                    hue[v] = new Vector3(0.8f, 0.75f, 0.85f);
                    if (p.Y < hipY + 1)
                    {
                        Vector3 c = p.X >= 0 ? lKnee : rKnee;
                        float out_ = (p.X - c.X) * (p.X >= 0 ? 1 : -1), fwd = p.Z - c.Z;
                        hue[v] = MathF.Abs(fwd) >= MathF.Abs(out_)
                            ? (fwd > 0 ? new Vector3(1, 0.3f, 0.3f) : new Vector3(0.3f, 0.45f, 1))
                            : (out_ > 0 ? new Vector3(0.3f, 0.9f, 0.35f) : new Vector3(1, 0.9f, 0.25f));
                    }
                }
                for (int t = 0; t + 2 < s.Length; t += 3)
                {
                    Vector3 a = s[t], c1 = s[t + 1], c2 = s[t + 2];
                    Vector3 n = Vector3.Cross(c1 - a, c2 - a);
                    if (n.LengthSquared < 1e-12f) continue;
                    n = n.Normalized();
                    float shade = 0.3f + 0.7f * Math.Abs(Vector3.Dot(n, light));
                    Vector3 h = (hue[t] + hue[t + 1] + hue[t + 2]) / 3 * shade * 255;
                    var col = new SixLabors.ImageSharp.PixelFormats.Rgba32((byte)h.X, (byte)h.Y, (byte)h.Z, 255);
                    Vector2 P(Vector3 v) => new(Tile / 2f + v.X * scale, Tile - 6 - v.Y * scale);
                    Vector2 p0 = P(a), p1 = P(c1), p2 = P(c2);
                    int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
                    int x1 = Math.Min(Tile - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
                    int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
                    int y1 = Math.Min(Tile - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
                    float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
                    if (MathF.Abs(area) < 1e-6f) continue;
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            float w0 = ((p1.X - x) * (p2.Y - y) - (p2.X - x) * (p1.Y - y)) / area;
                            float w1 = ((p2.X - x) * (p0.Y - y) - (p0.X - x) * (p2.Y - y)) / area;
                            float w2 = 1 - w0 - w1;
                            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                            float z = w0 * a.Z + w1 * c1.Z + w2 * c2.Z;
                            int i = y * Tile + x;
                            if (z >= depth[i]) continue;
                            depth[i] = z;
                            px[i] = col;
                        }
                    }
                }
            }
            return px;
        }

        static void DsBones()
        {
            Model m = Read.GetModelInstance(Metadata.HunterModels[Hunter.Samus][0]).Model;
            var skel = new DsSkeleton(m);
            var pose = new SkeletonPose(skel.Count);
            var world = new Matrix4[skel.Count];
            DsSkeleton.Sample(m, (int)PlayerAnimation.Idle, 0, pose);
            skel.Fk(pose, world);
            Console.WriteLine($"Samus: {skel.Count} nodes, model scale {skel.ModelScale}, clips {m.AnimationGroups.Node.Count}");
            for (int i = 0; i < skel.Count; i++)
            {
                Vector3 p = world[i].ExtractTranslation();
                string par = skel.Parent[i] >= 0 ? skel.Names[skel.Parent[i]] : "-";
                Console.WriteLine($"{i,3} {skel.Names[i],-18} parent {par,-18} at {V(p)} anim {pose.Animated[i]}");
            }
            foreach (PlayerAnimation a in new[] { PlayerAnimation.Idle, PlayerAnimation.WalkLeft, PlayerAnimation.WalkBackward, PlayerAnimation.Shoot })
            {
                Console.WriteLine($"clip {a} = {m.AnimationGroups.Node[(int)a].FrameCount} frames");
            }
        }

        static string V(Vector3 v) => $"({v.X,8:0.000} {v.Y,8:0.000} {v.Z,8:0.000})";

        // her fighter's polygons drawn: 0 body, 1-2 eyes, 20-24 hair, and her five face sets (face, lashes, back of the
        // head each; the yellow eyes 3-4 are a power-up's): 11-13 FaceM (neutral), 5-7 BlinkHalfM, 8-10 BlinkM, 14-16 OuchM
        // (hurt), 17-19 TalkM (shouting). In her model's own draw order: the opaque ones, then the blended ones by draw
        // priority (pony 0, inner hair 21 at 1, bangs 23 at 2, outer hair 22 at 4, lashes at 6) -- the hair shells are
        // layered, so the order is the look
        static readonly int[] Keep = { 0, 1, 2, 11, 13, 5, 7, 8, 10, 14, 16, 17, 19, 24, 20, 21, 23, 22, 12, 6, 9, 15, 18 };
        // the face sets: each gets a bone of its own under HeadN (AddFaces), so BrawlZeroSuitRig can show one at a time
        // (Brawl shows one by its visibility bone, keyed per animation in her motion pack's VIS0s: faces.txt)
        public static readonly (string Bone, int[] Polygons)[] FaceSets =
        {
            ("FaceSetF", new[] { 11, 12, 13 }), ("FaceSetBH", new[] { 5, 6, 7 }), ("FaceSetB", new[] { 8, 9, 10 }),
            ("FaceSetO", new[] { 14, 15, 16 }), ("FaceSetT", new[] { 17, 18, 19 }),
        };
        static readonly HashSet<string> Loops = new() { "Wait1", "Run", "Dash", "WalkSlow", "WalkMiddle", "WalkFast", "SquatWait", "SquatF", "SquatB", "Fall", "FallF" };

        // the CC0 humanoid clips put on her (UalRetarget), as "ual:<name>" (BrawlZeroSuitBody's movement)
        static readonly string[] UalClips =
        {
            "Idle_Loop", "Walk_Loop", "Jog_Fwd_Loop", "Sprint_Loop", "Jump_Start", "Jump_Loop", "Jump_Land", "Hit_Chest", "Hit_Head",
            "Pistol_Idle_Loop", "Pistol_Shoot", "Crouch_Idle_Loop", "Crouch_Fwd_Loop", "Roll",
        };

        static void Proto(string dump, string pac, string outDir, string? ual = null, string? faces = null)
        {
            var src = new BrawlBodySource { Source = "study export (PC only)" };
            XElement scene = Dae(Path.Combine(dump, "FitSZerosuit00.dae"), out XNamespace ns, out XDocument doc);
            var names = new List<string>();
            var parents = new List<int>();
            void Walk(XElement node, int parent)
            {
                int me = parent;
                if ((string?)node.Attribute("type") == "JOINT")
                {
                    me = names.Count;
                    names.Add((string)node.Attribute("name")!);
                    parents.Add(parent);
                }
                foreach (XElement c in node.Elements(ns + "node")) Walk(c, me);
            }
            foreach (XElement c in scene.Elements(ns + "node")) Walk(c, -1);
            src.Names = names.ToArray();
            src.Parent = parents.ToArray();
            int n = names.Count;
            src.Scale = new Vector3[n];
            src.Rotation = new Vector3[n];
            src.Translation = new Vector3[n];
            var srt = new Dictionary<string, (Vector3 S, Vector3 R, Vector3 T)>();
            foreach (string line in File.ReadLines(Path.Combine(dump, "..", "objs.txt")))
            {
                if (!line.StartsWith("bone ")) continue;
                string[] t = line.Split(' ');
                srt[t[1]] = (V3(t[3]), V3(t[5]), V3(t[7]));
            }
            for (int i = 0; i < n; i++)
            {
                (src.Scale[i], src.Rotation[i], src.Translation[i]) = srt.TryGetValue(names[i], out var b) ? b : (Vector3.One, Vector3.Zero, Vector3.Zero);
            }

            // check: the S/R/T bind against the export's own joint transforms and inverse binds
            Matrix4[] bind = BrawlBodyWriter.BindWorld(src);
            Dictionary<string, Matrix4> daeWorld = NodeWorlds(scene, ns);
            float worst = 0;
            for (int i = 0; i < n; i++) worst = Math.Max(worst, Diff(bind[i], daeWorld[names[i]]));
            Console.WriteLine($"{n} bones; bind vs export joints: worst {worst:0.00000}");
            worst = 0;
            foreach (XElement ctl in doc.Descendants(ns + "controller").Take(1))
            {
                string[] joints = Words(ctl.Descendants(ns + "Name_array").First().Value);
                float[] mats = Floats(ctl.Descendants(ns + "float_array").First(e => ((string)e.Attribute("id")!).EndsWith("MatArr")).Value);
                for (int j = 0; j < joints.Length; j++)
                {
                    Matrix4 inv = Mat(mats, j * 16);
                    worst = Math.Max(worst, Diff(bind[src.Bone(joints[j])].Inverted(), inv));
                }
            }
            Console.WriteLine($"bind vs export inverse binds: worst {worst:0.00000}");

            // her polygons
            Dictionary<string, string> symbols = Symbols(doc, ns);
            var faceParts = new Dictionary<BrawlBodyPart, int>();
            foreach (int k in Keep)
            {
                int set = Array.FindIndex(FaceSets, f => Array.IndexOf(f.Polygons, k) >= 0);
                foreach (BrawlBodyPart part in Parts(doc, ns, "polygon" + k, symbols, j => src.Bone(j), Matrix4.Identity, -1))
                {
                    src.Parts.Add(part);
                    if (set >= 0) faceParts[part] = set;
                }
            }

            // the Paralyzer, held fast on her right hand: its own HaveN laid on her RHaveN
            XElement pscene = Dae(Path.Combine(dump, "WpnSZerosuitParalyzer.dae"), out XNamespace pns, out XDocument pdoc);
            Matrix4 have = NodeWorlds(pscene, pns)["HaveN"];
            Matrix4 toHand = have.Inverted() * bind[src.Bone("RHaveN")];
            Dictionary<string, string> psym = Symbols(pdoc, pns);
            int pcount = 0;
            foreach (XElement g in pdoc.Descendants(pns + "geometry"))
            {
                foreach (BrawlBodyPart part in Parts(pdoc, pns, (string)g.Attribute("id")!, psym, _ => -1, toHand, src.Bone("RHaveN")))
                {
                    if (part.Material.StartsWith("Glow")) continue;   // the shot's muzzle glow quads (drawn with the shot later)
                    part.Material += " para.brres";
                    src.Parts.Add(part);
                    pcount++;
                }
            }
            Console.WriteLine($"{src.Parts.Count} parts ({pcount} Paralyzer), {src.Parts.Sum(p => p.Corners.Count) / 3} triangles");

            // her chest: two bones of our own for its sway (BrawlZeroSuitRig; her fighter has none)
            AddChest(src);
            AddFaces(src, faceParts);
            n = src.Names.Length;

            // her own clips
            BrawlBodyClip? clip = null;
            foreach (string line in File.ReadLines(Path.Combine(dump, "clips.txt")))
            {
                string[] t = line.Split(' ');
                if (t[0] == "CHR0")
                {
                    int frames = int.Parse(t[3], CultureInfo.InvariantCulture);
                    clip = new BrawlBodyClip
                    {
                        Name = t[1], Frames = frames, Loop = Loops.Contains(t[1]),
                        S = new Vector3[frames, n], R = new Vector3[frames, n], T = new Vector3[frames, n], Has = new bool[n],
                    };
                    src.Clips.Add(clip);
                    continue;
                }
                int b = src.Bone(t[0]);
                if (clip == null || b < 0) continue;
                int f = int.Parse(t[1], CultureInfo.InvariantCulture);
                float[] v = t.Skip(2).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                clip.Has[b] = true;
                clip.S[f, b] = new Vector3(v[0], v[1], v[2]);
                clip.R[f, b] = new Vector3(v[3], v[4], v[5]);
                clip.T[f, b] = new Vector3(v[6], v[7], v[8]);
            }

            if (ual != null)
            {
                BrawlBodyClip wait = src.Clips.First(c => c.Name == "Wait1");
                src.Clips.AddRange(UalRetarget.Clips(ual, src, wait, UalClips, "ual:"));
            }

            BrawlBodyWriter.Result res = BrawlBodyWriter.Write(src, outDir);
            // her face sets' keys per animation (a VIS0 dump of her motion pack: "<clip> <frames> F[..] BH[..] ..."), for
            // BrawlZeroSuitRig's faces
            if (faces != null) File.Copy(faces, Path.Combine(outDir, "faces.txt"), true);
            Console.WriteLine($"wrote {outDir}: {res.Materials} materials, {res.Vertices} vertices, height {res.Height:0.00}, feet {res.Feet:0.00}, "
                + $"{src.Clips.Count} clips, speeds {string.Join(", ", res.Speeds.Select(s => $"{s.Key} {s.Value:0.0}"))}");

            // the model files the materials come from, out of her fighter archive
            var formats = new Dictionary<string, int>();
            BrawlArchive arc = BrawlArchive.Read(File.ReadAllBytes(pac));
            string tmp = Path.Combine(outDir, "_tmp.brres");
            foreach (BrawlArchiveEntry e in arc.Entries)
            {
                byte[] data = arc.DataOf(e).ToArray();
                if (data.Length < 4 || data[0] != 'b' || data[1] != 'r' || data[2] != 'e' || data[3] != 's') continue;
                File.WriteAllBytes(tmp, data);
                foreach ((string k, int v) in Mdl0Gx.TextureFormats(tmp)) formats[k] = v;
                bool mdl = Has(data, "MDL0");
                string? name = mdl && Has(data, "FitSZerosuit00") && !Has(data, "WpnSZerosuitParalyzer") ? "body.brres"
                    : mdl && Has(data, "WpnSZerosuitParalyzer") ? "para.brres" : null;
                Console.WriteLine($"archive entry {e.Number}: bres {data.Length} bytes{(mdl ? " MDL0" : "")}{(name != null ? " -> " + name : "")}");
                if (name != null && !File.Exists(Path.Combine(outDir, name)))
                {
                    File.Copy(tmp, Path.Combine(outDir, name));
                    Console.WriteLine($"  {name}: {Mdl0Gx.Read(tmp).Count} materials");
                }
            }
            File.Delete(tmp);

            // textures: stored row-flipped as the native bodies' are; an intensity texture's grey goes in its alpha too
            foreach (string png in Directory.GetFiles(dump, "*.png"))
            {
                string name = Path.GetFileNameWithoutExtension(png);
                using var img = Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(png);
                bool intensity = formats.TryGetValue(name, out int fmt) && Mdl0Gx.IsIntensity(fmt);
                if (intensity)
                {
                    img.ProcessPixelRows(a =>
                    {
                        for (int y = 0; y < a.Height; y++)
                        {
                            Span<SixLabors.ImageSharp.PixelFormats.Rgba32> row = a.GetRowSpan(y);
                            for (int x = 0; x < row.Length; x++) row[x].A = row[x].R;
                        }
                    });
                }
                img.Mutate(c => c.Flip(FlipMode.Vertical));
                img.SaveAsPng(Path.Combine(outDir, name + ".png"));
                Console.WriteLine($"texture {name} {img.Width}x{img.Height} format {(formats.TryGetValue(name, out int f2) ? f2 : -1)}");
            }
        }

        // LBreastN / RBreastN under BustN: each at its side's front-most chest point pulled back in toward the sternum's
        // depth, and the chest skin near it moved onto it (a share of its BustN weight falling off with the distance)
        public const string LBreast = "LBreastN", RBreast = "RBreastN";

        // each face set's head weight onto a bone of its own: a child of HeadN at HeadN's place with no turn of its own
        // (so it moves exactly as HeadN does until BrawlZeroSuitRig folds the sets not shown away)
        static void AddFaces(BrawlBodySource src, Dictionary<BrawlBodyPart, int> faceParts)
        {
            int head = src.Bone("HeadN");
            if (head < 0 || faceParts.Count == 0 || src.Bone(FaceSets[0].Bone) >= 0) return;
            int first = src.Names.Length;
            src.Names = src.Names.Concat(FaceSets.Select(f => f.Bone)).ToArray();
            src.Parent = src.Parent.Concat(FaceSets.Select(_ => head)).ToArray();
            src.Scale = src.Scale.Concat(FaceSets.Select(_ => Vector3.One)).ToArray();
            src.Rotation = src.Rotation.Concat(FaceSets.Select(_ => Vector3.Zero)).ToArray();
            src.Translation = src.Translation.Concat(FaceSets.Select(_ => Vector3.Zero)).ToArray();
            int moved = 0, other = 0;
            var offHead = new Dictionary<string, int>();
            foreach ((BrawlBodyPart p, int set) in faceParts)
            {
                for (int k = 0; k < p.Corners.Count; k++)
                {
                    BrawlBodyCorner c = p.Corners[k];
                    if (c.Skin.Any(s => s.Bone != head))
                    {
                        other++;
                        offHead[p.Material] = offHead.GetValueOrDefault(p.Material) + 1;
                    }
                    c.Skin = c.Skin.Select(s => s.Bone == head ? (first + set, s.Weight) : s).ToArray();
                    p.Corners[k] = c;
                    moved++;
                }
            }
            Console.WriteLine($"faces: {FaceSets.Length} sets on bones {first}..{first + FaceSets.Length - 1}, {moved} corners ({other} also weighted off the head: {string.Join(", ", offHead.Select(e => $"{e.Key} {e.Value}"))})");
        }

        static void AddChest(BrawlBodySource src)
        {
            int bust = src.Bone("BustN");
            if (bust < 0 || src.Bone(LBreast) >= 0) return;
            Matrix4[] bind = BrawlBodyWriter.BindWorld(src);
            Vector3 bustAt = bind[bust].ExtractTranslation();
            static float W(BrawlBodyCorner c, int b) => c.Skin.Where(s => s.Bone == b).Sum(s => s.Weight);
            List<BrawlBodyCorner> chest = src.Parts.Where(p => !p.Material.Contains(".brres")).SelectMany(p => p.Corners)
                .Where(c => W(c, bust) > 0.5f && c.Normal.Z > 0.2f).ToList();
            if (chest.Count == 0) return;
            float midX = 0;
            Vector3 Tip(int side) => chest.Where(c => side * (c.Position.X - midX) > 0.1f)
                .OrderByDescending(c => c.Position.Z).First().Position;
            Vector3 tl = Tip(1), tr = Tip(-1);
            float y = (tl.Y + tr.Y) / 2;
            BrawlBodyCorner[] mid = chest.Where(c => MathF.Abs(c.Position.X) < MathF.Abs(tl.X) * 0.25f && MathF.Abs(c.Position.Y - y) < MathF.Abs(tl.X)).ToArray();
            float sternum = mid.Length > 0 ? mid.Max(c => c.Position.Z) : bustAt.Z;
            float depth = Math.Max(1e-3f, (tl.Z + tr.Z) / 2 - sternum);
            float radius = MathF.Abs(tl.X - tr.X) * 0.55f;
            Console.WriteLine($"chest: tips L {V(tl)} R {V(tr)}, sternum z {sternum:0.000}, depth {depth:0.000}, radius {radius:0.000}, bust at {V(bustAt)}");
            int moved = 0;
            var names = src.Names.ToList();
            var parents = src.Parent.ToList();
            var scale = src.Scale.ToList();
            var rot = src.Rotation.ToList();
            var tr2 = src.Translation.ToList();
            var bones = new List<(int Bone, Vector3 Tip, Vector3 Center)>();
            foreach ((string name, Vector3 tip) in new[] { (LBreast, tl), (RBreast, tr) })
            {
                Vector3 center = tip - new Vector3(0, 0, depth * 1.2f);
                // local = world * bust^-1 with no turn of its own: its rotation is BustN's, so its Euler keys stay 0
                Vector3 local = Vector3.TransformPosition(center, bind[bust].Inverted());
                bones.Add((names.Count, tip, center));
                names.Add(name);
                parents.Add(bust);
                scale.Add(Vector3.One);
                rot.Add(Vector3.Zero);
                tr2.Add(local);
            }
            src.Names = names.ToArray();
            src.Parent = parents.ToArray();
            src.Scale = scale.ToArray();
            src.Rotation = rot.ToArray();
            src.Translation = tr2.ToArray();
            foreach (BrawlBodyPart p in src.Parts)
            {
                if (p.Material.Contains(".brres")) continue;
                for (int k = 0; k < p.Corners.Count; k++)
                {
                    BrawlBodyCorner c = p.Corners[k];
                    float wb = W(c, bust);
                    if (wb <= 0 || c.Normal.Z < -0.2f) continue;
                    (int bone, Vector3 tip, Vector3 _) = bones.OrderBy(b => (c.Position - b.Tip).LengthSquared).First();
                    Vector3 d = c.Position - tip;
                    float dist = new Vector3(d.X, d.Y * 1.1f, d.Z * 0.5f).Length / radius;
                    if (dist >= 1) continue;
                    float s = 1 - dist;
                    float share = wb * s * s * (3 - 2 * s);
                    c.Skin = c.Skin.Select(x => x.Bone == bust ? (Bone: x.Bone, Weight: x.Weight - share) : x).Where(x => x.Weight > 1e-4f)
                        .Append((bone, share)).ToArray();
                    p.Corners[k] = c;
                    moved++;
                }
            }
            Console.WriteLine($"chest: {moved} corners share their BustN weight with {LBreast} / {RBreast}");
        }

        static bool Has(byte[] b, string s) => b.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(s)) >= 0;

        static XElement Dae(string path, out XNamespace ns, out XDocument doc)
        {
            doc = XDocument.Load(path);
            ns = doc.Root!.Name.Namespace;
            return doc.Descendants(ns + "visual_scene").First();
        }

        static Vector3 V3(string s)
        {
            float[] v = s.Trim('(', ')').Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            return new Vector3(v[0], v[1], v[2]);
        }

        static string[] Words(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        static float[] Floats(string s) => Words(s).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        static int[] Ints(string s) => Words(s).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();

        // a COLLADA matrix (written row by row for column vectors) as a row-vector matrix
        static Matrix4 Mat(float[] m, int o) => new Matrix4(
            m[o], m[o + 4], m[o + 8], m[o + 12], m[o + 1], m[o + 5], m[o + 9], m[o + 13],
            m[o + 2], m[o + 6], m[o + 10], m[o + 14], m[o + 3], m[o + 7], m[o + 11], m[o + 15]);

        static float Diff(Matrix4 a, Matrix4 b)
        {
            float d = 0;
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(a[r, c] - b[r, c]) / (r == 3 ? 10 : 1));
            return d;
        }

        // every node's model-space bind from its own <translate>/<rotate>/<scale> (listed outermost first)
        static Dictionary<string, Matrix4> NodeWorlds(XElement scene, XNamespace ns)
        {
            var res = new Dictionary<string, Matrix4>();
            void Walk(XElement node, Matrix4 parent)
            {
                Matrix4 local = Matrix4.Identity;
                foreach (XElement e in node.Elements())
                {
                    Matrix4 m;
                    if (e.Name == ns + "translate") { float[] v = Floats(e.Value); m = Matrix4.CreateTranslation(v[0], v[1], v[2]); }
                    else if (e.Name == ns + "rotate") { float[] v = Floats(e.Value); m = Matrix4.CreateFromAxisAngle(new Vector3(v[0], v[1], v[2]), MathHelper.DegreesToRadians(v[3])); }
                    else if (e.Name == ns + "scale") { float[] v = Floats(e.Value); m = Matrix4.CreateScale(v[0], v[1], v[2]); }
                    else if (e.Name == ns + "matrix") m = Mat(Floats(e.Value), 0);
                    else continue;
                    local = m * local;
                }
                Matrix4 world = local * parent;
                if (node.Attribute("name") is XAttribute a) res[a.Value] = world;
                foreach (XElement c in node.Elements(ns + "node")) Walk(c, world);
            }
            foreach (XElement c in scene.Elements(ns + "node")) Walk(c, Matrix4.Identity);
            return res;
        }

        // material symbol -> the material it names (instance_material target)
        static Dictionary<string, string> Symbols(XDocument doc, XNamespace ns)
        {
            var res = new Dictionary<string, string>();
            foreach (XElement im in doc.Descendants(ns + "instance_material"))
            {
                res[(string)im.Attribute("symbol")!] = ((string)im.Attribute("target")!).TrimStart('#');
            }
            return res;
        }

        // one geometry's triangles, skinned by its controller (joint name -> body bone), or with rigid >= 0 all on that
        // bone after `xf`; UVs as GX's (t down: the export's v flipped back)
        static IEnumerable<BrawlBodyPart> Parts(XDocument doc, XNamespace ns, string id, Dictionary<string, string> symbols,
            Func<string, int> bone, Matrix4 xf, int rigid)
        {
            XElement geo = doc.Descendants(ns + "geometry").First(g => (string?)g.Attribute("id") == id);
            var arrays = new Dictionary<string, float[]>();
            foreach (XElement s in geo.Descendants(ns + "source"))
            {
                if (s.Element(ns + "float_array") is XElement fa) arrays["#" + (string)s.Attribute("id")!] = Floats(fa.Value);
            }
            string posSrc = (string)geo.Descendants(ns + "vertices").First().Element(ns + "input")!.Attribute("source")!;

            // per position index: its skin
            var skins = new List<(int, float)[]>();
            XElement? ctl = doc.Descendants(ns + "controller").FirstOrDefault(c => (string?)c.Element(ns + "skin")?.Attribute("source") == "#" + id);
            if (rigid < 0 && ctl != null)
            {
                string[] joints = Words(ctl.Descendants(ns + "Name_array").First().Value);
                float[] weights = Floats(ctl.Descendants(ns + "float_array").First(e => ((string)e.Attribute("id")!).EndsWith("WeightArr")).Value);
                XElement vw = ctl.Descendants(ns + "vertex_weights").First();
                int[] vc = Ints(vw.Element(ns + "vcount")!.Value), v = Ints(vw.Element(ns + "v")!.Value);
                int at = 0;
                foreach (int c in vc)
                {
                    var sk = new (int, float)[c];
                    for (int k = 0; k < c; k++, at += 2) sk[k] = (bone(joints[v[at]]), weights[v[at + 1]]);
                    skins.Add(sk);
                }
            }

            foreach (XElement tris in geo.Descendants(ns + "triangles"))
            {
                string sym = (string)tris.Attribute("material")!;
                var part = new BrawlBodyPart { Material = symbols.TryGetValue(sym, out string? mat) ? mat : sym };
                int stride = 0, oPos = -1, oNrm = -1, oCol = -1, oUv = -1;
                string? nrmSrc = null, colSrc = null, uvSrc = null;
                foreach (XElement inp in tris.Elements(ns + "input"))
                {
                    int off = (int)inp.Attribute("offset")!;
                    stride = Math.Max(stride, off + 1);
                    string sem = (string)inp.Attribute("semantic")!;
                    string s = (string)inp.Attribute("source")!;
                    int set = (int?)inp.Attribute("set") ?? 0;
                    if (sem == "VERTEX") oPos = off;
                    else if (sem == "NORMAL") { oNrm = off; nrmSrc = s; }
                    else if (sem == "COLOR" && set == 0) { oCol = off; colSrc = s; }
                    else if (sem == "TEXCOORD" && set == 0) { oUv = off; uvSrc = s; }
                }
                int[] p = Ints(tris.Element(ns + "p")!.Value);
                float[] pos = arrays[posSrc];
                for (int c = 0; c + stride <= p.Length; c += stride)
                {
                    int pi = p[c + oPos];
                    var corner = new BrawlBodyCorner
                    {
                        Position = Vector3.TransformPosition(new Vector3(pos[pi * 3], pos[pi * 3 + 1], pos[pi * 3 + 2]), xf),
                        Normal = Vector3.UnitY, Color = Vector4.One,
                        Skin = rigid >= 0 ? new[] { (rigid, 1f) } : pi < skins.Count ? skins[pi] : new[] { (0, 1f) },
                    };
                    if (nrmSrc != null)
                    {
                        float[] a = arrays[nrmSrc];
                        int q = p[c + oNrm] * 3;
                        corner.Normal = Vector3.TransformNormal(new Vector3(a[q], a[q + 1], a[q + 2]), xf).Normalized();
                    }
                    if (colSrc != null)
                    {
                        float[] a = arrays[colSrc];
                        int q = p[c + oCol] * 4;
                        corner.Color = new Vector4(a[q], a[q + 1], a[q + 2], a[q + 3]);
                    }
                    if (uvSrc != null)
                    {
                        float[] a = arrays[uvSrc];
                        int q = p[c + oUv] * 2;
                        corner.Uv = new Vector2(a[q], 1 - a[q + 1]);
                    }
                    part.Corners.Add(corner);
                }
                yield return part;
            }
        }
    }
}
