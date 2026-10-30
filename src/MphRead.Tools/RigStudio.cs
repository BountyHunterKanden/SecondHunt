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
    // Rig Studio: a LOCAL web tool for grouping a Brawl trophy's mesh into the same per-bone chunks the game's
    // own hunter model is built from, setting each joint's seam (rigid like the DS, or a narrow blend), and
    // previewing the game's clips on the result beside the game model.
    //
    // The page (RigStudio/ next to this file) is plain code; every mesh it shows is read at runtime from the
    // user's own extracted files and served to localhost only -- nothing is uploaded, bundled or published.
    // Saving writes "<dae>.chunks.txt" beside the trophy (TrophyChunks), refits, and refreshes the rig cache
    // the app loads.
    // Run: MphRead.Tools.dll -rigstudio [port]   then open http://localhost:<port>/
    internal static class RigStudio
    {
        sealed class Session
        {
            public string Trophy = "", DaePath = "", Log = "";
            public Hunter Hunter;
            public Model Model = null!;
            public DsSkeleton Sk = null!;
            public List<Vector3> Corners = new(), CornerNormals = new();
            public int[] CornerVertex = Array.Empty<int>();
            public TrophyRig Rig = null!;
            public TrophyChunks? Chunks;
        }

        static string _repo = "", _conv = "", _web = "";
        static readonly Dictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            _repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            _conv = Path.Combine(_repo, "brawl_extract", "Converted");
            _web = Path.Combine(_repo, "src", "MphRead.Tools", "RigStudio");
            int port = args.Length > 1 && int.TryParse(args[1], out int p) ? p : 8765;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            Console.WriteLine($"Rig Studio: http://localhost:{port}/  (page files: {_web}; trophies: {_conv})");
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
            // static page files (no directory escapes)
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
            if (ctx.Request.HttpMethod != "HEAD") ctx.Response.OutputStream.Write(body);   // HEAD: headers only
            ctx.Response.OutputStream.Close();
        }
        static void Json(HttpListenerContext ctx, object o) => Reply(ctx, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(o));
        static string B64<T>(T[] a) where T : struct => Convert.ToBase64String(MemoryMarshal.AsBytes(a.AsSpan()));

        static object List() => Directory.GetDirectories(_conv).Select(Path.GetFileName)
            .Where(t => t != null && TrophyRigs.TryHunterFor(t, out _) && Directory.GetFiles(Path.Combine(_conv, t), "*.dae").Length > 0)
            .Select(t =>
            {
                string dae = Directory.GetFiles(Path.Combine(_conv, t!), "*.dae")[0];
                TrophyRigs.TryHunterFor(t!, out Hunter h);
                return new { name = t, hunter = h.ToString(), landmarks = File.Exists(TrophyLandmarks.PathFor(dae)), chunks = File.Exists(TrophyChunks.PathFor(dae)) };
            }).ToList();

        static Session Get(string trophy)
        {
            if (Sessions.TryGetValue(trophy, out var s)) return s;
            if (!TrophyRigs.TryHunterFor(trophy, out Hunter h)) throw new ArgumentException($"unknown trophy {trophy}");
            string dae = Directory.GetFiles(Path.Combine(_conv, trophy), "*.dae").First();
            var hd = DaeModel.Load(dae);
            s = new Session
            {
                Trophy = trophy, DaePath = dae, Hunter = h, Model = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model,
                Corners = hd.Meshes.SelectMany(m => m.Positions).Select(q => new Vector3(q.X, q.Y, q.Z)).ToList(),
                CornerNormals = hd.Meshes.SelectMany(m => m.Normals).Select(q => new Vector3(q.X, q.Y, q.Z)).ToList(),
            };
            s.Sk = new DsSkeleton(s.Model);
            Fit(s);
            Sessions[trophy] = s;
            return s;
        }

        static void Fit(Session s)
        {
            var log = new StringBuilder();
            string jp = TrophyLandmarks.PathFor(s.DaePath), cp = TrophyChunks.PathFor(s.DaePath);
            var landmarks = File.Exists(jp) ? TrophyLandmarks.Load(jp) : null;
            s.Chunks = File.Exists(cp) ? TrophyChunks.Load(cp, s.Sk.Names) : null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            s.Rig = TrophyRigger.Fit(s.Model, s.Corners, new TrophyRigger.Options
            {
                Landmarks = landmarks, Chunks = s.Chunks,
                Log = m => { log.AppendLine(m.Trim()); Console.WriteLine(m); },
            });
            log.AppendLine($"fitted in {sw.Elapsed.TotalSeconds:0.0}s");
            s.CornerVertex = s.Corners.Select(q => s.Rig.IndexOf(new System.Numerics.Vector3(q.X, q.Y, q.Z))).ToArray();
            // the app loads this cache: keep it in step with what the page shows
            TrophyRigIO.Save(Path.ChangeExtension(s.DaePath, ".mphrig"), s.Rig, TrophyRigIO.CacheKey(s.DaePath));
            s.Log = log.ToString();
        }

        static object ModelJson(Session s)
        {
            var rig = s.Rig; var sk = s.Sk; var g = rig.TrophyToRig;
            int nv = rig.Vertices.Length, nb = sk.Count;
            // trophy in its sculpted (bind) pose, rig space: game units, facing -Z like the game model
            var pos = new float[nv * 3];
            for (int v = 0; v < nv; v++) { var q = Vector3.TransformPosition(rig.Vertices[v], g); pos[v * 3] = q.X; pos[v * 3 + 1] = q.Y; pos[v * 3 + 2] = q.Z; }
            var acc = new Vector3[nv];
            for (int c = 0; c < s.CornerVertex.Length; c++) if (s.CornerVertex[c] >= 0) acc[s.CornerVertex[c]] += Vector3.TransformNormal(s.CornerNormals[c], g);
            var nrm = new float[nv * 3];
            for (int v = 0; v < nv; v++) { var q = acc[v].LengthSquared > 1e-20f ? acc[v].Normalized() : Vector3.UnitY; nrm[v * 3] = q.X; nrm[v * 3 + 1] = q.Y; nrm[v * 3 + 2] = q.Z; }
            // mesh pieces (connected components) and small patches inside each (piece, owner) region
            var adj = new List<int>[nv];
            for (int v = 0; v < nv; v++) adj[v] = new List<int>();
            for (int t = 0; t + 2 < s.CornerVertex.Length; t += 3)
            {
                int a = s.CornerVertex[t], b = s.CornerVertex[t + 1], c = s.CornerVertex[t + 2];
                if (a < 0 || b < 0 || c < 0) continue;
                adj[a].Add(b); adj[a].Add(c); adj[b].Add(a); adj[b].Add(c); adj[c].Add(a); adj[c].Add(b);
            }
            var piece = Flood(nv, adj, (_, _) => true, null, float.MaxValue);
            var owner = new int[nv];
            for (int v = 0; v < nv; v++) owner[v] = rig.Bones[v * 4];
            var user = s.Chunks != null && s.Chunks.Owner.Length == nv ? s.Chunks.Owner : null;
            var regionOwner = user != null ? user.Select((u, v) => u >= 0 ? u : owner[v]).ToArray() : owner;
            float height = 0; { float lo = float.MaxValue, hi = float.MinValue; for (int v = 0; v < nv; v++) { lo = MathF.Min(lo, pos[v * 3 + 1]); hi = MathF.Max(hi, pos[v * 3 + 1]); } height = hi - lo; }
            var patch = Flood(nv, adj, (a, b) => piece[a] == piece[b] && regionOwner[a] == regionOwner[b], pos, 0.035f * height);
            // the game model: every triangle corner in its bone's local frame (posed per frame on the page)
            var dsL = new List<float>(); var dsN = new List<float>(); var dsNode = new List<int>();
            {
                var poser = new BipedAnimator(s.Model);
                poser.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
                HunterRig.Pose(poser, 0f, new float[16 * 32]);
                foreach (var b in MphRecomp.Render.GeometryBaker.Bake(s.Model, new float[3], out _, useAnimation: true, useMatrixStack: true, skinned: true))
                    for (int i = 0; i + 11 < b.Verts.Count; i += 12)
                    {
                        dsL.Add(b.Verts[i]); dsL.Add(b.Verts[i + 1]); dsL.Add(b.Verts[i + 2]);
                        dsN.Add(b.Verts[i + 3]); dsN.Add(b.Verts[i + 4]); dsN.Add(b.Verts[i + 5]);
                        dsNode.Add(s.Model.NodeMatrixIds[(int)b.Verts[i + 11]]);
                    }
            }
            var ownsDs = new bool[nb]; foreach (int nd in dsNode) ownsDs[nd] = true;
            // game-model parts attached to the trophy (landmark attach/replace), in bind space
            var parts = TrophyRigger.ReplacementParts(s.Model, rig).SelectMany(x => x.Corners).ToList();
            var joints = new float[nb * 3];
            for (int i = 0; i < nb; i++) { var j = rig.BindWorld[i].ExtractTranslation(); joints[i * 3] = j.X; joints[i * 3 + 1] = j.Y; joints[i * 3 + 2] = j.Z; }
            return new
            {
                trophy = s.Trophy, hunter = s.Hunter.ToString(), log = s.Log,
                bones = Enumerable.Range(0, nb).Select(i => new { name = sk.Names[i], parent = sk.Parent[i], ownsDs = ownsDs[i] }).ToList(),
                height,
                vertices = nv,
                pos = B64(pos), nrm = B64(nrm), tri = B64(s.CornerVertex),
                bi = B64(rig.Bones), bw = B64(rig.Weights), owner = B64(owner), user = user != null ? B64(user) : null,
                stiff = B64(Enumerable.Range(0, nv).Select(v => user != null && s.Chunks!.Stiff.Length == nv && s.Chunks.Stiff[v] ? 1 : 0).ToArray()),
                hidden = B64(Enumerable.Range(0, nv).Select(v => rig.Hidden.Length == nv && rig.Hidden[v] ? 1 : 0).ToArray()),
                piece = B64(piece), patch = B64(patch), joints = B64(joints),
                dsLocal = B64(dsL.ToArray()), dsNormal = B64(dsN.ToArray()), dsNode = B64(dsNode.ToArray()),
                partPos = B64(parts.SelectMany(c => new[] { c.Pos.X, c.Pos.Y, c.Pos.Z }).ToArray()),
                partNrm = B64(parts.SelectMany(c => new[] { c.Normal.X, c.Normal.Y, c.Normal.Z }).ToArray()),
                partNode = B64(parts.Select(c => c.Node).ToArray()),
                seams = (s.Chunks?.Seams ?? new()).ToDictionary(kv => kv.Key, kv => new { rigid = kv.Value.Rigid, width = kv.Value.Width }),
                defaultSeamWidth = TrophyChunks.DefaultSeamWidth,
                clips = Enumerable.Range(0, s.Model.AnimationGroups.Node.Count).Select(c => new { id = c, name = ((PlayerAnimation)c).ToString(), frames = s.Model.AnimationGroups.Node[c].FrameCount }).ToList(),
            };
        }

        // connected flood fill; with a radius, each region also stays within that distance of its seed
        static int[] Flood(int nv, List<int>[] adj, Func<int, int, bool> same, float[]? pos, float radius)
        {
            var id = new int[nv]; Array.Fill(id, -1);
            int next = 0; float r2 = radius * radius;
            var q = new Queue<int>();
            for (int s0 = 0; s0 < nv; s0++)
            {
                if (id[s0] >= 0) continue;
                id[s0] = next; q.Enqueue(s0);
                while (q.Count > 0)
                {
                    int u = q.Dequeue();
                    foreach (int w in adj[u])
                    {
                        if (id[w] >= 0 || !same(s0, w)) continue;
                        if (pos != null)
                        {
                            float dx = pos[w * 3] - pos[s0 * 3], dy = pos[w * 3 + 1] - pos[s0 * 3 + 1], dz = pos[w * 3 + 2] - pos[s0 * 3 + 2];
                            if (dx * dx + dy * dy + dz * dz > r2) continue;
                        }
                        id[w] = next; q.Enqueue(w);
                    }
                }
                next++;
            }
            return id;
        }

        // one clip, every frame: the trophy's skin matrices (inverse bind * pose) and the game model's bone
        // matrices, row-major (row vectors: v' = v * M), plus both skeletons' joint positions
        static object ClipJson(Session s, int clip)
        {
            var rig = s.Rig; var sk = s.Sk; int nb = sk.Count;
            int frames = s.Model.AnimationGroups.Node[clip].FrameCount;
            var pose = new SkeletonPose(nb); var tw = new Matrix4[nb]; var dw = new Matrix4[nb];
            var tskin = new float[frames * nb * 16]; var dmat = new float[frames * nb * 16];
            var tj = new float[frames * nb * 3]; var dj = new float[frames * nb * 3];
            static void Put(float[] a, int o, Matrix4 m)
            {
                a[o] = m.M11; a[o + 1] = m.M12; a[o + 2] = m.M13; a[o + 3] = m.M14; a[o + 4] = m.M21; a[o + 5] = m.M22; a[o + 6] = m.M23; a[o + 7] = m.M24;
                a[o + 8] = m.M31; a[o + 9] = m.M32; a[o + 10] = m.M33; a[o + 11] = m.M34; a[o + 12] = m.M41; a[o + 13] = m.M42; a[o + 14] = m.M43; a[o + 15] = m.M44;
            }
            for (int f = 0; f < frames; f++)
            {
                DsSkeleton.Sample(s.Model, clip, f, pose);
                sk.Fk(pose, tw, 0f, rig.Lengths, rig.Offsets);
                sk.Fk(pose, dw);
                for (int i = 0; i < nb; i++)
                {
                    Put(tskin, (f * nb + i) * 16, rig.InvBind[i] * tw[i]);
                    Put(dmat, (f * nb + i) * 16, dw[i]);
                    var a = tw[i].ExtractTranslation(); var b = dw[i].ExtractTranslation();
                    tj[(f * nb + i) * 3] = a.X; tj[(f * nb + i) * 3 + 1] = a.Y; tj[(f * nb + i) * 3 + 2] = a.Z;
                    dj[(f * nb + i) * 3] = b.X; dj[(f * nb + i) * 3 + 1] = b.Y; dj[(f * nb + i) * 3 + 2] = b.Z;
                }
            }
            return new { clip, frames, trophySkin = B64(tskin), dsBones = B64(dmat), trophyJoints = B64(tj), dsJoints = B64(dj) };
        }

        static object Save(Session s, string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var owner = MemoryMarshal.Cast<byte, int>(Convert.FromBase64String(root.GetProperty("owner").GetString()!)).ToArray();
            if (owner.Length != s.Rig.Vertices.Length) throw new ArgumentException($"grouping covers {owner.Length} vertices, the trophy has {s.Rig.Vertices.Length}");
            var chunks = new TrophyChunks { Owner = owner, Stiff = new bool[owner.Length] };
            if (root.TryGetProperty("stiff", out var st) && st.ValueKind == JsonValueKind.String)
            {
                var sf = MemoryMarshal.Cast<byte, int>(Convert.FromBase64String(st.GetString()!)).ToArray();
                if (sf.Length == owner.Length) for (int i = 0; i < sf.Length; i++) chunks.Stiff[i] = sf[i] != 0;
            }
            if (root.TryGetProperty("seams", out var seams))
                foreach (var p in seams.EnumerateObject())
                    chunks.Seams[p.Name] = (p.Value.GetProperty("rigid").GetBoolean(), p.Value.GetProperty("width").GetSingle());
            string cp = TrophyChunks.PathFor(s.DaePath);
            if (File.Exists(cp)) File.Copy(cp, cp + ".bak", overwrite: true);
            TrophyChunks.Save(cp, chunks, s.Sk.Names, $"{s.Trophy}: chunk grouping from Rig Studio, {DateTime.Now:yyyy-MM-dd HH:mm}");
            Console.WriteLine($"  saved {cp}");
            Fit(s);
            return ModelJson(s);
        }
    }
}
