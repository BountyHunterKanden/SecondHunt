using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Assets;
using OpenTK.Mathematics;

namespace MphRead
{
    // Pose Studio: a LOCAL web page where the owner poses a Brawl statue's pieces by hand -- the game's model, the statue as
    // sculpted and the working copy side by side, all in the same pose (a clean rest A-pose, or the game's standing Idle) --
    // then plays the game's own clips on the result. Each correction is a turn of one bone's statue piece in that bone's own
    // frame, saved as "<dae>.posed.txt" (TrophyRigIO.PosedSet) and laid on the prepared rig by the app exactly as the page
    // shows it (skin = InvBind * K * world). Local only: every mesh is read from the user's own extracted files.
    // Run: MphRead.Tools.dll -posestudio [port]   then open http://localhost:<port>/
    internal static class PoseStudio
    {
        sealed class Session
        {
            public string Trophy = "", DaePath = "", BaseKey = "";
            public Hunter Hunter;
            public Model Model = null!;
            public DsSkeleton Sk = null!;
            public TrophyRig Rig = null!;
            public int[] CornerVertex = Array.Empty<int>();
            public Vector4[] CornerColor = Array.Empty<Vector4>();
            public TrophyRigIO.PosedSet? Posed;
        }

        static string _conv = "", _web = "";
        static readonly Dictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            _conv = Path.Combine(repo, "brawl_extract", "Converted");
            _web = Path.Combine(repo, "src", "MphRead.Tools", "PoseStudio");
            int port = args.Length > 1 && int.TryParse(args[1], out int p) ? p : 8766;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            Console.WriteLine($"Pose Studio: http://localhost:{port}/  (page files: {_web}; trophies: {_conv})");
            while (true)
            {
                var ctx = listener.GetContext();
                try { Handle(ctx); }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {ctx.Request.Url?.PathAndQuery}: {ex}");
                    try { Reply(ctx, 500, "text/plain", Encoding.UTF8.GetBytes(ex.Message)); } catch { }
                }
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url!.AbsolutePath;
            string? trophy = ctx.Request.QueryString["t"];
            if (path == "/api/list") { Json(ctx, List()); return; }
            if (path == "/api/model" && trophy != null) { Json(ctx, ModelJson(Get(trophy))); return; }
            if (path == "/api/clip" && trophy != null) { Json(ctx, ClipJson(Get(trophy), int.Parse(ctx.Request.QueryString["c"] ?? "0"))); return; }
            if (path == "/api/save" && trophy != null && ctx.Request.HttpMethod == "POST")
            {
                using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                Json(ctx, Save(Get(trophy), sr.ReadToEnd()));
                return;
            }
            string rel = path == "/" ? "index.html" : path.TrimStart('/');
            string file = Path.GetFullPath(Path.Combine(_web, rel));
            if (!file.StartsWith(_web, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) { Reply(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("not found")); return; }
            string type = Path.GetExtension(file) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", _ => "application/octet-stream" };
            Reply(ctx, 200, type + "; charset=utf-8", File.ReadAllBytes(file));
        }

        static void Reply(HttpListenerContext ctx, int status, string type, byte[] body)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = type;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentLength64 = body.Length;
            if (ctx.Request.HttpMethod != "HEAD") ctx.Response.OutputStream.Write(body);
            ctx.Response.OutputStream.Close();
        }
        static void Json(HttpListenerContext ctx, object o) => Reply(ctx, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(o));
        static string B64<T>(T[] a) where T : struct => Convert.ToBase64String(MemoryMarshal.AsBytes(a.AsSpan()));
        static void Put(float[] a, int o, Matrix4 m)
        {
            a[o] = m.M11; a[o + 1] = m.M12; a[o + 2] = m.M13; a[o + 3] = m.M14; a[o + 4] = m.M21; a[o + 5] = m.M22; a[o + 6] = m.M23; a[o + 7] = m.M24;
            a[o + 8] = m.M31; a[o + 9] = m.M32; a[o + 10] = m.M33; a[o + 11] = m.M34; a[o + 12] = m.M41; a[o + 13] = m.M42; a[o + 14] = m.M43; a[o + 15] = m.M44;
        }
        static float[] Mats(Matrix4[] m) { var a = new float[m.Length * 16]; for (int i = 0; i < m.Length; i++) Put(a, i * 16, m[i]); return a; }

        // the statues (a prepared PIECES or RIGID rig), not the imports that already come with their own rest pose
        static readonly string[] BaseOrder = { "pieces", "rigid", "retro" };
        static object List() => Directory.GetDirectories(_conv).Select(Path.GetFileName)
            .Where(t => t != null && TrophyRigs.TryHunterFor(t, out _) && Directory.GetFiles(Path.Combine(_conv, t), "*.dae").Length > 0)
            .Select(t => (t: t!, dae: Directory.GetFiles(Path.Combine(_conv, t!), "*.dae")[0]))
            .Where(x => File.Exists(Path.ChangeExtension(x.dae, ".pieces.mphrig")) || File.Exists(Path.ChangeExtension(x.dae, ".rigid.mphrig")))
            .Where(x => !File.Exists(Path.ChangeExtension(x.dae, ".convert.json")))
            .Select(x => new { name = x.t, posed = File.Exists(TrophyRigIO.PosedPathFor(x.dae)) }).ToList();

        static Session Get(string trophy)
        {
            if (Sessions.TryGetValue(trophy, out var s)) return s;
            if (!TrophyRigs.TryHunterFor(trophy, out Hunter h)) throw new ArgumentException($"unknown trophy {trophy}");
            string dae = Directory.GetFiles(Path.Combine(_conv, trophy), "*.dae").First();
            var model = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
            var sk = new DsSkeleton(model);
            var posed = TrophyRigIO.LoadPosed(TrophyRigIO.PosedPathFor(dae));
            TrophyRig? rig = null; string key = "";
            foreach (string k in posed != null ? new[] { posed.Base }.Concat(BaseOrder) : BaseOrder)
            {
                var v = TrophyRigIO.Variants.FirstOrDefault(x => x.Key == k);
                if (v == null) continue;
                rig = TrophyRigIO.Load(TrophyRigIO.VariantPathFor(dae, v), TrophyRigIO.VariantKey(dae, v));
                if (rig != null) { key = k; break; }
            }
            if (rig == null) throw new InvalidOperationException($"{trophy}: no current pieces / rigid rig cache");
            TrophyFeet.Settle(rig, model, sk);   // as the app does, so the page's floor is the device's
            var hd = DaeModel.Load(dae);
            var corners = hd.Meshes.SelectMany(m => m.Positions).ToList();
            var colors = hd.Meshes.SelectMany(m => Enumerable.Range(0, m.Positions.Count).Select(i => m.Colors.Count > i ? m.Colors[i] : m.FlatColor))
                .Select(c => new Vector4(c.X, c.Y, c.Z, c.W)).ToArray();
            s = new Session
            {
                Trophy = trophy, DaePath = dae, Hunter = h, Model = model, Sk = sk, Rig = rig, BaseKey = key, Posed = posed, CornerColor = colors,
                CornerVertex = corners.Select(q => rig.IndexOf(q)).ToArray(),
            };
            Sessions[trophy] = s;
            Console.WriteLine($"  {trophy}: base rig '{key}', {rig.Vertices.Length} vertices{(posed != null ? $", your pose: {posed.K.Count} pieces turned" : "")}");
            return s;
        }

        // ---- the poses: the game's standing Idle f0, and a clean REST A-pose made from it --------------------------------------
        // Rest: every limb straightened along its bone (legs straight down, arms 40 deg out from the body, spine and neck
        // upright), each by the least turn, so a bone's twist is Idle's; the head and feet keep facing the way they face in Idle.
        // Solved separately for the game skeleton and for the rig's own (its joint offsets), with the same targets.
        static readonly Vector3 Up = new(0, 1, 0), Down = new(0, -1, 0);
        static Vector3 ArmL => new Vector3(-MathF.Sin(0.70f), -MathF.Cos(0.70f), 0);   // the game model's left is -X
        static Vector3 ArmR => new Vector3(MathF.Sin(0.70f), -MathF.Cos(0.70f), 0);

        static Matrix4 FromTo(Vector3 a, Vector3 b)   // row-vector rotation taking direction a to b
        {
            a = a.Normalized(); b = b.Normalized();
            var q = Quaternion.FromAxisAngle(Vector3.Cross(a, b).LengthSquared < 1e-12f ? Vector3.UnitY : Vector3.Cross(a, b).Normalized(),
                MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)));
            return Matrix4.CreateFromQuaternion(q);
        }

        // world matrices of the pose; `local` (optional) receives each node's local matrix (Scale * Rot * Translate, identity
        // where not animated) so the page can chain them itself with joints the owner moved. The rest solve always aims
        // along the BASE rig's offsets, so a limb the owner re-posed stays as posed after a reload.
        static Matrix4[] Pose(Session s, bool rig, bool rest, Matrix4[]? local = null)
        {
            var sk = s.Sk; int n = sk.Count; var model = s.Model;
            var pose = new SkeletonPose(n); DsSkeleton.Sample(model, (int)PlayerAnimation.Idle, 0, pose);
            float ms = model.Scale.X;
            var rot = new Matrix4[n]; var tr = new Vector3[n]; var sc = new Vector3[n]; var order = new List<int>();
            { var placed = new bool[n]; while (order.Count < n) for (int i = 0; i < n; i++) if (!placed[i] && (sk.Parent[i] < 0 || placed[sk.Parent[i]])) { placed[i] = true; order.Add(i); } }
            for (int i = 0; i < n; i++)
            {
                rot[i] = Matrix4.CreateRotationX(pose.R[i].X) * Matrix4.CreateRotationY(pose.R[i].Y) * Matrix4.CreateRotationZ(pose.R[i].Z);
                sc[i] = pose.S[i];
                tr[i] = (rig && s.Rig.Offsets.Length > i && s.Rig.Offsets[i] is Vector3 o ? o : pose.T[i] * (rig && s.Rig.Lengths.Length > i ? s.Rig.Lengths[i] : 1f)) / ms;
            }
            var w = new Matrix4[n];
            void Fk() { foreach (int i in order) { if (!pose.Animated[i]) { w[i] = Matrix4.Identity; continue; } var m = Matrix4.CreateScale(sc[i]) * rot[i] * Matrix4.CreateTranslation(tr[i]); w[i] = sk.Parent[i] >= 0 ? m * w[sk.Parent[i]] : m; } }
            void Locals() { if (local != null) for (int i = 0; i < n; i++) local[i] = pose.Animated[i] ? Matrix4.CreateScale(sc[i]) * rot[i] * Matrix4.CreateTranslation(tr[i]) : Matrix4.Identity; }
            Fk();
            if (!rest) { Locals(); return w; }
            var idle = (Matrix4[])w.Clone();
            int Idx(string nm) => Array.IndexOf(sk.Names, nm);
            // each game bone's part: its centre, from its joint, in the bone's frame (for bones with no child joint)
            var partDir = new Dictionary<int, Vector3>();
            var baked = MphRecomp.Render.GeometryBaker.Bake(model, new float[3], out _, useAnimation: false, useMatrixStack: true, skinned: true);
            var sum = new Vector3[n]; var cnt = new int[n];
            foreach (var b in baked)
                for (int k = 0; k + MphRecomp.Render.GeometryBaker.StrideSkinned <= b.Verts.Count; k += MphRecomp.Render.GeometryBaker.StrideSkinned)
                { int nd = model.NodeMatrixIds[(int)b.Verts[k + 11]]; sum[nd] += new Vector3(b.Verts[k], b.Verts[k + 1], b.Verts[k + 2]); cnt[nd]++; }
            for (int i = 0; i < n; i++) if (cnt[i] > 0) partDir[i] = sum[i] / cnt[i];
            void Aim(int i, Vector3 dirLocalOrChild, bool isChild, Vector3 target)
            {
                if (i < 0) return;
                Fk();
                var wr = w[i].ClearTranslation();
                var cur = isChild ? Vector3.TransformVector(dirLocalOrChild, w[i]) : Vector3.TransformVector(dirLocalOrChild, w[i]);
                if (cur.LengthSquared < 1e-10f) return;
                var nw = wr * FromTo(cur, target);
                var pr = sk.Parent[i] >= 0 ? w[sk.Parent[i]].ClearTranslation() : Matrix4.Identity;
                rot[i] = Matrix4.Invert(Matrix4.CreateScale(sc[i])) * nw * Matrix4.Invert(pr);
                rot[i] = rot[i].ClearTranslation();
            }
            void AimChild(string bone, string child, Vector3 target) { int i = Idx(bone), c = Idx(child); if (i >= 0 && c >= 0) Aim(i, tr[c], true, target); }
            void AimPart(string bone, Vector3 target) { int i = Idx(bone); if (i >= 0 && partDir.TryGetValue(i, out var d)) Aim(i, d, false, target); }
            void KeepIdle(string bone)    // the part faces in world as it does in Idle (head, feet)
            {
                int i = Idx(bone); if (i < 0) return; Fk();
                var pr = sk.Parent[i] >= 0 ? w[sk.Parent[i]].ClearTranslation() : Matrix4.Identity;
                rot[i] = (Matrix4.Invert(Matrix4.CreateScale(sc[i])) * idle[i].ClearTranslation() * Matrix4.Invert(pr)).ClearTranslation();
            }
            AimChild("Spine_1", "Spine_2", Up);
            if (Idx("Neck_1") >= 0) { AimChild("Spine_2", "Collar", Up); AimChild("Collar", "Neck_1", Up); } else AimChild("Spine_2", "Head_1", Up);
            foreach (var (side, arm) in new[] { ("L", ArmL), ("R", ArmR) })
            {
                AimChild($"{side}_hip", $"{side}_knee", Down); AimChild($"{side}_knee", $"{side}_ankle", Down); KeepIdle($"{side}_ankle");
                AimChild($"{side}_shoulder", $"{side}_elbow", arm);
                if (Idx($"{side}_wrist") >= 0) { AimChild($"{side}_elbow", $"{side}_wrist", arm); AimPart($"{side}_wrist", arm); }
                else AimPart($"{side}_elbow", arm);   // the gun forearm: no wrist bone
            }
            KeepIdle("Head_1");
            Fk(); Locals();
            return w;
        }

        // the owner's moved joints as the page chains them: offset / model scale (NaN = the rig's own)
        static float[] PageOffsets(Session s)
        {
            int nb = s.Sk.Count; var ms = s.Sk.ModelScale; var a = new float[nb * 3]; Array.Fill(a, float.NaN);
            if (s.Posed != null)
                for (int i = 0; i < nb; i++)
                    if (s.Posed.Offsets.TryGetValue(s.Sk.Names[i], out var o)) { a[i * 3] = o.X / ms.X; a[i * 3 + 1] = o.Y / ms.Y; a[i * 3 + 2] = o.Z / ms.Z; }
            return a;
        }
        static int[] Animated(Session s, int clip)
        {
            var pose = new SkeletonPose(s.Sk.Count); DsSkeleton.Sample(s.Model, clip, 0, pose);
            return pose.Animated.Select(x => x ? 1 : 0).ToArray();
        }

        static object ModelJson(Session s)
        {
            var rig = s.Rig; var sk = s.Sk; var g = rig.TrophyToRig;
            int nv = rig.Vertices.Length, nb = sk.Count;
            var sculpt = new float[nv * 3]; var bind = new float[nv * 3];
            for (int v = 0; v < nv; v++)
            {
                var q = Vector3.TransformPosition(rig.Vertices[v], g); sculpt[v * 3] = q.X; sculpt[v * 3 + 1] = q.Y; sculpt[v * 3 + 2] = q.Z;
                var b = rig.BindPos != null ? rig.BindPos[v] : q; bind[v * 3] = b.X; bind[v * 3 + 1] = b.Y; bind[v * 3 + 2] = b.Z;
            }
            // per vertex colour (the statue's own vertex colours), triangles as drawn by the app
            var col = new float[nv * 3]; var ccount = new int[nv];
            for (int c = 0; c < s.CornerVertex.Length; c++)
            {
                int v = s.CornerVertex[c]; if (v < 0) continue; var cc = s.CornerColor[c];
                col[v * 3] += cc.X; col[v * 3 + 1] += cc.Y; col[v * 3 + 2] += cc.Z; ccount[v]++;
            }
            for (int v = 0; v < nv; v++) if (ccount[v] > 0) { col[v * 3] /= ccount[v]; col[v * 3 + 1] /= ccount[v]; col[v * 3 + 2] /= ccount[v]; }
            var tri = new List<int>();
            for (int t = 0; t + 2 < s.CornerVertex.Length; t += 3)
            {
                int a = s.CornerVertex[t], b = s.CornerVertex[t + 1], c = s.CornerVertex[t + 2];
                if (a < 0 || b < 0 || c < 0 || rig.HidesTriangle(a, b, c)) continue;
                tri.Add(a); tri.Add(b); tri.Add(c);
            }
            // the game model: every triangle corner in its bone's frame
            var dsL = new List<float>(); var dsNode = new List<int>();
            foreach (var b in MphRecomp.Render.GeometryBaker.Bake(s.Model, new float[3], out _, useAnimation: false, useMatrixStack: true, skinned: true))
                for (int i = 0; i + MphRecomp.Render.GeometryBaker.StrideSkinned <= b.Verts.Count; i += MphRecomp.Render.GeometryBaker.StrideSkinned)
                { dsL.Add(b.Verts[i]); dsL.Add(b.Verts[i + 1]); dsL.Add(b.Verts[i + 2]); dsNode.Add(s.Model.NodeMatrixIds[(int)b.Verts[i + 11]]); }
            // game parts the rig draws on the statue (Weavel's gun block): bind space of the BASE rig, they ride their bone
            var parts = TrophyRigger.ReplacementParts(s.Model, rig).SelectMany(x => x.Corners).ToList();
            var k = new Matrix4[nb];
            for (int i = 0; i < nb; i++) k[i] = s.Posed != null && s.Posed.K.TryGetValue(sk.Names[i], out var m) ? m : Matrix4.Identity;
            var restL = new Matrix4[nb]; var idleL = new Matrix4[nb];
            var restW = Pose(s, true, true, restL); var idleW = Pose(s, true, false, idleL);
            return new
            {
                trophy = s.Trophy, hunter = s.Hunter.ToString(), baseRig = s.BaseKey,
                bones = Enumerable.Range(0, nb).Select(i => new { name = sk.Names[i], parent = sk.Parent[i] }).ToList(),
                vertices = nv,
                sculpt = B64(sculpt), bind = B64(bind), col = B64(col), tri = B64(tri.ToArray()),
                bi = B64(rig.Bones), bw = B64(rig.Weights), invBind = B64(Mats(rig.InvBind)), bindWorld = B64(Mats(rig.BindWorld)), k = B64(Mats(k)),
                off = B64(PageOffsets(s)), anim = B64(Animated(s, (int)PlayerAnimation.Idle)),
                dsLocal = B64(dsL.ToArray()), dsNode = B64(dsNode.ToArray()),
                partPos = B64(parts.SelectMany(c => new[] { c.Pos.X, c.Pos.Y, c.Pos.Z }).ToArray()), partNode = B64(parts.Select(c => c.Node).ToArray()),
                poses = new
                {
                    rest = new { rig = B64(Mats(restW)), local = B64(Mats(restL)), ds = B64(Mats(Pose(s, false, true))) },
                    idle = new { rig = B64(Mats(idleW)), local = B64(Mats(idleL)), ds = B64(Mats(Pose(s, false, false))) },
                },
                clips = Enumerable.Range(0, s.Model.AnimationGroups.Node.Count).Select(c => new { id = c, name = ((PlayerAnimation)c).ToString(), frames = s.Model.AnimationGroups.Node[c].FrameCount }).ToList(),
            };
        }

        // one clip, every frame: the rig's LOCAL matrices (the page chains them with the owner's moved joints, then skins with
        // InvBind * K * world) and the game skeleton's world matrices (row-major, row vectors)
        static object ClipJson(Session s, int clip)
        {
            var rig = s.Rig; var sk = s.Sk; int nb = sk.Count;
            int frames = s.Model.AnimationGroups.Node[clip].FrameCount;
            var pose = new SkeletonPose(nb); var tl = new Matrix4[nb]; var dw = new Matrix4[nb];
            var rl = new float[frames * nb * 16]; var dm = new float[frames * nb * 16];
            for (int f = 0; f < frames; f++)
            {
                DsSkeleton.Sample(s.Model, clip, f, pose);
                sk.Locals(pose, tl, 0f, rig.Lengths, rig.Offsets); sk.Fk(pose, dw);
                for (int i = 0; i < nb; i++) { Put(rl, (f * nb + i) * 16, tl[i]); Put(dm, (f * nb + i) * 16, dw[i]); }
            }
            return new { clip, frames, local = B64(rl), anim = B64(Animated(s, clip)), ds = B64(dm) };
        }

        static object Save(Session s, string body)
        {
            using var doc = JsonDocument.Parse(body);
            var f = MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(doc.RootElement.GetProperty("k").GetString()!)).ToArray();
            int nb = s.Sk.Count;
            if (f.Length != nb * 16) throw new ArgumentException($"pose covers {f.Length / 16} bones, the skeleton has {nb}");
            var p = new TrophyRigIO.PosedSet { Base = s.BaseKey };
            for (int i = 0; i < nb; i++)
            {
                var m = new Matrix4(f[i * 16], f[i * 16 + 1], f[i * 16 + 2], f[i * 16 + 3], f[i * 16 + 4], f[i * 16 + 5], f[i * 16 + 6], f[i * 16 + 7],
                                    f[i * 16 + 8], f[i * 16 + 9], f[i * 16 + 10], f[i * 16 + 11], f[i * 16 + 12], f[i * 16 + 13], f[i * 16 + 14], f[i * 16 + 15]);
                float d = 0; for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) d += MathF.Abs(m[r, c] - (r == c ? 1f : 0f));
                if (d > 1e-5f) p.K[s.Sk.Names[i]] = m;
            }
            // joints moved with a limb turned like a doll's: page units (offset / model scale) back to the rig's
            if (doc.RootElement.TryGetProperty("off", out var jo))
            {
                var o = MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(jo.GetString()!)).ToArray();
                if (o.Length != nb * 3) throw new ArgumentException($"joints cover {o.Length / 3} bones, the skeleton has {nb}");
                var ms = s.Sk.ModelScale;
                for (int i = 0; i < nb; i++)
                    if (!float.IsNaN(o[i * 3])) p.Offsets[s.Sk.Names[i]] = new Vector3(o[i * 3] * ms.X, o[i * 3 + 1] * ms.Y, o[i * 3 + 2] * ms.Z);
            }
            string path = TrophyRigIO.PosedPathFor(s.DaePath);
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            TrophyRigIO.SavePosed(path, p, $"{s.Trophy}: pose from Pose Studio, {DateTime.Now:yyyy-MM-dd HH:mm}, on the '{s.BaseKey}' rig");
            s.Posed = p;
            Console.WriteLine($"  saved {path} ({p.K.Count} pieces turned, {p.Offsets.Count} joints moved)");
            return new { ok = true, path = Path.GetFileName(path), turned = p.K.Count, moved = p.Offsets.Count };
        }
    }
}
