using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Retro;
using MphRecomp.Import.Retro3;
using OpenTK.Mathematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // -retropose <disc.iso> --pak Metroid1.pak[,other.pak|all] --ancs 39900a11 --char Dark_Samus [--echoes] [--clips a,b,...] [--frames 6] [--keeproot]
    //            [--yaw 25] [--size 320] [--gif <clip>] [--out dir] [--list] [--prop <cmdl>@<bone>[@dx,dy,dz][@rx,ry,rz]] [--clipsfrom "<disc.iso>;<pak>;<ancs hex>;<char>[;echoes]"]
    // A Retro character on ITS OWN skeleton playing ITS OWN clips from the user's disc -- no DS rig, no retarget: the CMDL
    // skinned by its CSKR to its CINF, each clip sampled with Retro.Anim and posed the way the Prime first-person guns are
    // (GunViewmodel.Pose: local = R * T(trans), world = local * parent; skin = T(-bind) * world, Retro binds carry no
    // rotation). Software-rendered (z-buffer, each material's first non-environment texture, a headlight), so it needs no
    // GL context: a look at how a character such as Dark Samus would move natively (Multiplayer characters, Phase 5).
    // Writes <out>/<char>_sheet.png (a row per clip, --frames evenly spaced frames each) and, with --gif, <char>_<clip>.gif.
    // --clipsfrom: the clips come from another character (e.g. the Echoes Pirate Commando's shield clips on the Prime 1
    // trooper), matched by bone name, rotations only (this skeleton keeps its own bone offsets): a look, not a retarget.
    // Corruption: -retropose - --mp3 <Trilogy DATA/files> [--paks3 Metroid1.pak] --char3 <CHAR hex16> [--prop <CHAR hex16>:<clip>@<bone>..]
    internal static class RetroBodyPose
    {
        sealed class Tex
        {
            public int W, H;
            public byte[] Rgba = Array.Empty<byte>();
        }

        public static void Run(string[] args)
        {
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            if (args.Length < 2 || args[1].StartsWith("-"))
            {
                Console.WriteLine("  -retropose <disc.iso> --pak <pak> --ancs <hex id> --char <name> [--echoes] [--clips a,b] [--frames 6] [--yaw 25] [--size 320] [--gif <clip>] [--out dir] [--list]");
                return;
            }
            bool echoes = args.Contains("--echoes");
            bool keepRoot = args.Contains("--keeproot");
            uint ancsId = UInt32.Parse(Arg("--ancs", "39900a11"), NumberStyles.HexNumber);
            string charName = Arg("--char", "Dark_Samus");
            int frames = Int32.Parse(Arg("--frames", "6"), CultureInfo.InvariantCulture);
            double yaw = Double.Parse(Arg("--yaw", "25"), CultureInfo.InvariantCulture) * Math.PI / 180;
            int size = Int32.Parse(Arg("--size", "320"), CultureInfo.InvariantCulture);
            string outDir = Path.GetFullPath(Arg("--out", Path.Combine(Path.GetTempPath(), "mph-retropose")));
            Directory.CreateDirectory(outDir);

            Dictionary<uint, uint>? boneMap = null;
            Cmdl m;
            Cinf skel;
            List<(uint Bone, float Weight)[]> skin;
            var clips = new List<(string Name, Func<Anim> Load)>();
            var texCache = new Dictionary<ulong, Tex?>();
            Func<ulong, byte[]?> texBytes = _ => null;
            Tex? TexOf(ulong id)
            {
                if (texCache.TryGetValue(id, out Tex? t)) return t;
                int w = 0, h = 0;
                byte[]? T = texBytes(id);
                byte[]? rgba = T != null ? Txtr.Decode(T, out _, out w, out h) : null;
                t = rgba == null ? null : new Tex { W = w, H = h, Rgba = rgba };
                texCache[id] = t;
                return t;
            }
            // per material: its texture and whether it blends (additive: drawn after, no depth write)
            var matTex = new List<Tex?>();
            var matAdd = new List<bool>();
            // --prop <cmdl hex>|<char3 hex16>:<clip>@<bone>[@dx,dy,dz][@rx,ry,rz]: a rigid model held by a bone (the Echoes
            // commando's shield dish at L_wrist; Corruption's Pirate_Shield posed by one of its own clips, last frame)
            // "@bind" in place of the offset: the prop's positions are already in this skeleton's bind space (Corruption's
            // open_shield clip puts the shield at the pirate's left wrist), so it is only skinned to the bone
            var props = new List<(Cmdl Model, Vector3[] P, List<Tex?> Tex, List<bool> Add, string Bone, Vector3 Off, Vector3 Rot)>();
            static bool PropBind(string pr) => pr.Split('@').Length > 2 && pr.Split('@')[2] == "bind";
            static (Vector3 Off, Vector3 Rot) PropPlace(string[] q)
            {
                static Vector3 V(string t)
                {
                    float[] f = t.Split(',').Select(x => Single.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    return new Vector3(f[0], f[1], f[2]);
                }
                return (q.Length > 2 && q[2] != "bind" ? V(q[2]) : Vector3.Zero, q.Length > 3 ? V(q[3]) : Vector3.Zero);
            }
            IEnumerable<string> PropArgs() => args.Select((a, i) => (a, i)).Where(x => x.a == "--prop" && x.i + 1 < args.Length).Select(x => args[x.i + 1]);
            string modelLine;
            string mp3 = Arg("--mp3", "");
            if (mp3.Length > 0)
            {
                // Corruption: -retropose - --mp3 <...DATA/files> [--paks3 Metroid1.pak,..] --char3 <CHAR hex16> (args[1] unused)
                var src = new FolderSource(mp3);
                List<Pak3> paks3 = Arg("--paks3", "Metroid1.pak").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(n => new Pak3(src.OpenRead("MP3/" + n), "MP3/" + n, leaveOpen: false)).ToList();
                var res3 = new Pak3Set(paks3);
                texBytes = id => res3.Contains(id) ? res3.Get(id) : null;
                (Cmdl M, List<(uint Bone, float Weight)[]> Skin, Cinf Skel, List<(string Name, Func<Anim> Load)> Clips, List<Tex?> Tex, string Line) Load3(ulong cid)
                {
                    (string cname, List<(string Type, ulong Id)> ids) = Char3.CharIds(res3.Get(cid), res3.TypeOf);
                    ulong Of(string type) => ids.Where(x => x.Type == type).Select(x => (ulong?)x.Id).FirstOrDefault()
                        ?? throw new InvalidDataException($"CHAR {cid:x16} names no {type}");
                    Cmdl3 m3 = Cmdl3.Load(res3.Get(Of("CMDL")));
                    var mm = new Cmdl
                    {
                        Version = 5, Positions = m3.Positions, Normals = m3.Normals, Uv0 = m3.Uv0, Triangles = m3.Triangles, TriangleMaterial = m3.TriangleMaterial,
                    };
                    List<(uint Bone, float Weight)[]> sk = Skin3.Load(res3.Get(Of("CSKR")), m3.PositionCount, m3.SkinOrder, m3.TrianglePositions());
                    Cinf k = Cinf.Load(res3.Get(Of("CINF")));
                    var cl = new List<(string Name, Func<Anim> Load)>();
                    foreach ((string nm, Char3.Meta meta) in Char3.Animations(res3.Get(cid), id => res3.TypeOf(id) == "ANIM"))
                    {
                        List<Char3.Meta> ps = Char3.Prims(meta);
                        if (ps.Count != 1 || cl.Any(c => c.Name == nm)) continue;
                        ulong aid = ps[0].AnimId;
                        cl.Add((nm, () => Anim3.Load(res3.Get(aid))));
                    }
                    // the "CLR " pass's texture (Corruption's base colour, Material3Gx's first stage)
                    List<Tex?> tx = m3.Materials.Select(x => x.Passes.FirstOrDefault(q => q.Type == "CLR " && q.Tex != UInt64.MaxValue) is Cmdl3.Pass d ? TexOf(d.Tex) : null).ToList();
                    return (mm, sk, k, cl, tx, $"{cname}: model {Of("CMDL"):x16} ({m3.PositionCount} positions, {m3.TriangleCount} triangles, {m3.Materials.Length} materials), "
                        + $"skeleton {Of("CINF"):x16} ({k.Bones.Count} bones)");
                }
                var main = Load3(UInt64.Parse(Arg("--char3", "b82daba63a625397"), NumberStyles.HexNumber));
                (m, skin, skel) = (main.M, main.Skin, main.Skel);
                clips.AddRange(main.Clips);
                matTex.AddRange(main.Tex);
                matAdd.AddRange(main.Tex.Select(_ => false));
                modelLine = main.Line;
                foreach (string pr in PropArgs())
                {
                    string[] q = pr.Split('@');
                    string[] what = q[0].Split(':');
                    var pc = Load3(UInt64.Parse(what[0], NumberStyles.HexNumber));
                    Anim? pa = what.Length > 1 ? pc.Clips.Where(c => c.Name == what[1]).Select(c => c.Load()).FirstOrDefault() : null;
                    if (what.Length > 1 && pa == null) Console.WriteLine($"  prop {what[0]}: no clip '{what[1]}' ({String.Join(", ", pc.Clips.Select(c => c.Name))})");
                    (Vector3 off, Vector3 rot) = PropPlace(q);
                    props.Add((pc.M, PoseRigid(pc.M, pc.Skin, pc.Skel, pa, pa?.Duration ?? 0), pc.Tex, pc.Tex.Select(_ => false).ToList(), q[1], off, rot));
                    Console.WriteLine($"  prop {pc.Line}{(pa != null ? $", posed in {what[1]} (last frame)" : "")}, held by {q[1]}");
                    Vector3[] pq = props[^1].P;
                    Console.WriteLine($"    bounds {pq.Aggregate(Vector3.ComponentMin)} .. {pq.Aggregate(Vector3.ComponentMax)}; bones {String.Join(", ", pc.Skel.Bones.Values.Select(b => $"{b.Name} ({b.X:F2},{b.Y:F2},{b.Z:F2})"))}");
                }
            }
            else
            {
                IFileSource disc = DiscImage.Open(args[1]);   // .iso, .rvz (the retail reference)
                // --pak a.pak[,b.pak]: a resource comes from the first pak that has it ("all": every pak on the disc)
                string pakArg = Arg("--pak", "Metroid1.pak");
                List<string> pakNames = pakArg == "all"
                    ? disc.Paths.Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal).ToList()
                    : pakArg.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                List<Pak> paks = pakNames.Select(n => new Pak(disc.ReadFile(n), n)).ToList();
                bool Has(uint id) => paks.Any(q => q.Contains(id));
                byte[] Res(uint id) => (paks.FirstOrDefault(q => q.Contains(id)) ?? throw new KeyNotFoundException($"{id:x8} is in none of {pakArg}")).Get(id);
                texBytes = id => id <= UInt32.MaxValue && Has((uint)id) ? Res((uint)id) : null;

                Ancs ancs = Ancs.Parse(Res(ancsId));
                (uint mid, uint sid, uint kid) = Ancs.FindCharacter(Res(ancsId), charName);
                m = Cmdl.Load(Res, mid, sid);
                skel = Cinf.Load(Res(kid));
                skin = m.Skin ?? throw new InvalidDataException("the model has no skin");
                // --clipsfrom: another disc/character's clips, by bone name (source bone id -> this skeleton's bone id)
                string from = Arg("--clipsfrom", "");
                Func<uint, byte[]> clipRes = Res;
                bool clipEchoes = echoes;
                Ancs? fromAncs = null;
                if (from.Length > 0)
                {
                    string[] f = from.Split(';');
                    clipEchoes = f.Length > 4 && f[4] == "echoes";
                    IFileSource fromDisc = DiscImage.Open(f[0]);
                    var fromPak = new Pak(fromDisc.ReadFile(f[1]), f[1]);
                    clipRes = fromPak.Get;
                    uint fromAncsId = UInt32.Parse(f[2], NumberStyles.HexNumber);
                    fromAncs = Ancs.Parse(clipRes(fromAncsId));
                    (_, _, uint fkid) = Ancs.FindCharacter(clipRes(fromAncsId), f[3]);
                    Cinf fromSkel = Cinf.Load(clipRes(fkid));
                    boneMap = new Dictionary<uint, uint>();
                    foreach ((uint sb, Cinf.Bone sbone) in fromSkel.Bones)
                    {
                        foreach ((uint tb, Cinf.Bone tbone) in skel.Bones)
                        {
                            if (tbone.Name == sbone.Name) { boneMap[sb] = tb; break; }
                        }
                    }
                    Console.WriteLine($"  clips from {f[3]} ({f[1]} {f[2]}): {boneMap.Count} of {fromSkel.Bones.Count} bones matched by name");
                }
                foreach ((string nm, Ancs.MetaAnim meta) in (fromAncs ?? ancs).Set!.Anims)
                {
                    List<Ancs.MetaAnim> prims = Ancs.Prims(meta);
                    if (prims.Count != 1 || clips.Any(c => c.Name == nm)) continue;
                    uint aid = prims[0].AnimId;
                    clips.Add((nm, () => Anim.Load(clipRes(aid), clipEchoes)));
                }
                modelLine = $"{charName}: model {mid:x8} ({m.PositionCount} positions, {m.TriangleCount} triangles, {m.Materials.Length} materials), "
                    + $"skeleton {kid:x8} ({skel.Bones.Count} bones)";
                if (args.Contains("--list"))
                {
                    for (int i = 0; i < m.Materials.Length; i++)
                    {
                        Cmdl.Material mm = m.Materials[i];
                        Console.WriteLine($"    material {i}: blend {mm.BlendSrc}/{mm.BlendDst}, textures {String.Join(" ", mm.TexIds.Select(x => x.ToString("x8")))}, "
                            + $"{m.TriangleMaterial.Count(x => x == i)} triangles");
                        foreach (Cmdl.TevStage st in mm.Stages)
                        {
                            string ts = st.TexCoord < mm.TexGens.Length ? mm.TexGens[st.TexCoord].Src.ToString(CultureInfo.InvariantCulture) : "-";
                            Console.WriteLine($"      stage c[{String.Join(",", st.ColorIn)}] op {st.ColorOp.Op} tex {st.Tex} texgen src {ts} ras {st.Ras} kc {st.KColor}");
                        }
                    }
                }
                // per material: the first stage's texture that isn't an environment map
                (List<Tex?> Tex, List<bool> Add) Mats(Cmdl mm)
                {
                    var tx = new List<Tex?>();
                    var ad = new List<bool>();
                    foreach (Cmdl.Material mat in mm.Materials)
                    {
                        ad.Add(!(mat.BlendSrc == 1 && mat.BlendDst == 0));
                        Tex? t = null;
                        foreach (Cmdl.TevStage st in mat.Stages)
                        {
                            if (st.Tex == 255 || st.Tex >= mat.TexIds.Length) continue;
                            bool env = st.TexCoord < mat.TexGens.Length && mat.TexGens[st.TexCoord].Src is 0 or 1;
                            if (env) continue;
                            t = TexOf(mat.TexIds[st.Tex]);
                            break;
                        }
                        tx.Add(t);
                    }
                    return (tx, ad);
                }
                (List<Tex?> mt, List<bool> ma) = Mats(m);
                matTex.AddRange(mt);
                matAdd.AddRange(ma);
                foreach (string pr in PropArgs())
                {
                    string[] q = pr.Split('@');
                    uint pid = UInt32.Parse(q[0], NumberStyles.HexNumber);
                    Cmdl pm = Cmdl.Load(Res, pid);
                    (Vector3 off, Vector3 rot) = PropPlace(q);
                    (List<Tex?> ptx, List<bool> pad) = Mats(pm);
                    Vector3[] pp = Enumerable.Range(0, pm.PositionCount).Select(i => new Vector3(pm.Positions[3 * i], pm.Positions[3 * i + 1], pm.Positions[3 * i + 2])).ToArray();
                    // --propadd: drawn additive, as the particle that carries it is (the commando's dish)
                    if (args.Contains("--propadd")) pad = pad.Select(_ => true).ToList();
                    props.Add((pm, pp, ptx, pad, q[1], off, rot));
                    Console.WriteLine($"  prop {pid:x8} ({pm.PositionCount} positions, {pm.TriangleCount} triangles), held by {q[1]}");
                }
            }
            Console.WriteLine($"  {modelLine}, {clips.Count} single clips");
            if (args.Contains("--list"))
            {
                foreach (uint b in skel.SortedIds()) Console.WriteLine($"    bone {b,3} {skel.Bones[b].Name,-24} parent {skel.Bones[b].Parent} at ({skel.Bones[b].X:F2}, {skel.Bones[b].Y:F2}, {skel.Bones[b].Z:F2})");
                foreach ((string nm, _) in clips) Console.WriteLine($"    clip {nm}");
                return;
            }
            // props join the mesh, skinned whole to their bone at its bind spot (Retro binds carry no rotation, so the
            // prop's axes are the model's): its rotation (degrees about X, then Y, then Z), then its offset from the bone
            List<string> propArgs = PropArgs().ToList();
            for (int pi = 0; pi < props.Count; pi++)
            {
                (Cmdl pm, Vector3[] pp, List<Tex?> ptx, List<bool> pad, string boneName, Vector3 off, Vector3 rot) = props[pi];
                uint pb = skel.Bones.First(b => b.Value.Name == boneName).Key;
                Cinf.Bone bb = skel.Bones[pb];
                Matrix4 place = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(rot.X)) * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(rot.Y))
                    * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(rot.Z)) * Matrix4.CreateTranslation(off + (PropBind(propArgs[pi]) ? Vector3.Zero : new Vector3((float)bb.X, (float)bb.Y, (float)bb.Z)));
                int np0 = m.PositionCount, nu0 = m.Uv0.Length / 2, mat0 = matTex.Count;
                var P = m.Positions.ToList();
                foreach (Vector3 v in pp)
                {
                    Vector3 w = (new Vector4(v, 1) * place).Xyz;
                    P.Add(w.X); P.Add(w.Y); P.Add(w.Z);
                    skin.Add(new[] { (pb, 1f) });
                }
                m.Positions = P.ToArray();
                m.Uv0 = m.Uv0.Concat(pm.Uv0).ToArray();
                m.Triangles = m.Triangles.Concat(pm.Triangles.Select(c => new Cmdl.Corner { P = c.P + np0, N = c.N, T0 = c.T0 >= 0 ? c.T0 + nu0 : -1 })).ToArray();
                m.TriangleMaterial = m.TriangleMaterial.Concat(pm.TriangleMaterial.Select(x => x + mat0)).ToArray();
                matTex.AddRange(ptx);
                matAdd.AddRange(pad);
            }

            List<uint> order = skel.SortedIds();
            // parents before children (ids ascending is not guaranteed to be)
            var depth = new Dictionary<uint, int>();
            int Depth(uint b) => depth.TryGetValue(b, out int d) ? d : depth[b] = skel.Bones.TryGetValue(skel.Bones[b].Parent, out _) && skel.Bones[b].Parent != b ? Depth(skel.Bones[b].Parent) + 1 : 0;
            order = order.OrderBy(Depth).ToList();

            // the posed positions + normals, Retro space (X right, Y forward, Z up)
            (Vector3[] P, Vector3[] N) Pose(Anim? a, double t)
            {
                Dictionary<uint, double[]>? rot = null, trans = null;
                a?.Sample(t, out rot, out trans);
                if (boneMap != null && rot != null)
                {
                    var mapped = new Dictionary<uint, double[]>();
                    foreach ((uint sb, double[] q) in rot) if (boneMap.TryGetValue(sb, out uint tb)) mapped[tb] = q;
                    rot = mapped;
                    trans = null;
                }
                var world = new Dictionary<uint, Matrix4>();
                foreach (uint b in order)
                {
                    Cinf.Bone bone = skel.Bones[b];
                    bool hasParent = skel.Bones.TryGetValue(bone.Parent, out Cinf.Bone? par) && bone.Parent != b;
                    Quaternion r = Quaternion.Identity;
                    if (rot != null && rot.TryGetValue(b, out double[]? q)) r = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
                    Vector3 p = hasParent ? new Vector3((float)(bone.X - par!.X), (float)(bone.Y - par.Y), (float)(bone.Z - par.Z))
                        : new Vector3((float)bone.X, (float)bone.Y, (float)bone.Z);
                    if (trans != null && trans.TryGetValue(b, out double[]? tv)) p = new Vector3((float)tv[0], (float)tv[1], (float)tv[2]);
                    Matrix4 local = Matrix4.CreateFromQuaternion(r) * Matrix4.CreateTranslation(p);
                    world[b] = hasParent ? local * world[bone.Parent] : local;
                }
                // root motion out (the game moves the actor by it, and her super missile / scatter shot rise metres): the
                // pelvis stays on its bind spot, so the pictures show the body's own motion; --keeproot leaves it in
                Matrix4 pin = Matrix4.Identity;
                uint? pelvis = order.Cast<uint?>().FirstOrDefault(b => skel.Bones[b!.Value].Name == "Skeleton_Root");
                if (!keepRoot && pelvis is uint pb)
                {
                    Vector3 at = world[pb].ExtractTranslation();
                    pin = Matrix4.CreateTranslation((float)skel.Bones[pb].X - at.X, (float)skel.Bones[pb].Y - at.Y, (float)skel.Bones[pb].Z - at.Z);
                }
                var skinM = new Dictionary<uint, Matrix4>();
                foreach (uint b in order)
                {
                    Cinf.Bone bone = skel.Bones[b];
                    skinM[b] = Matrix4.CreateTranslation(-(float)bone.X, -(float)bone.Y, -(float)bone.Z) * world[b] * pin;
                }
                int np = m.PositionCount;
                var P = new Vector3[np];
                for (int i = 0; i < np; i++)
                {
                    var v = new Vector4(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2], 1);
                    Vector3 acc = Vector3.Zero;
                    float wsum = 0;
                    if (i < skin.Count)
                    {
                        foreach ((uint bb, float w) in skin[i])
                        {
                            if (!skinM.TryGetValue(bb, out Matrix4 sm)) continue;
                            acc += w * (v * sm).Xyz;
                            wsum += w;
                        }
                    }
                    P[i] = wsum > 0 ? acc / wsum : v.Xyz;
                }
                // normals: rebuilt per position from the posed faces (smooth enough for a look)
                var N = new Vector3[np];
                for (int t3 = 0; t3 + 2 < m.Triangles.Length; t3 += 3)
                {
                    int a0 = m.Triangles[t3].P, a1 = m.Triangles[t3 + 1].P, a2 = m.Triangles[t3 + 2].P;
                    Vector3 fn = Vector3.Cross(P[a1] - P[a0], P[a2] - P[a0]);
                    N[a0] += fn; N[a1] += fn; N[a2] += fn;
                }
                for (int i = 0; i < np; i++) N[i] = N[i].LengthSquared > 0 ? N[i].Normalized() : Vector3.UnitZ;
                return (P, N);
            }

            // camera: orthographic, from her front (+Y) turned by yaw about Z, a little from above; screen x = the camera's right
            Vector3 fwd = new((float)-Math.Sin(yaw), (float)-Math.Cos(yaw), -0.12f);
            fwd.Normalize();
            Vector3 right = Vector3.Cross(fwd, Vector3.UnitZ).Normalized();
            Vector3 up = Vector3.Cross(right, fwd).Normalized();
            Vector3 light = (-fwd + 0.4f * up + 0.3f * right).Normalized();

            // framing from the rest of the first clip (or the bind): fixed for every frame, feet on a shared line
            List<string> want = Arg("--clips", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (want.Count == 0) want = clips.Select(c => c.Name).Take(8).ToList();
            var anims = new Dictionary<string, Anim>();
            foreach (string w in want)
            {
                (string Name, Func<Anim> Load) c = clips.FirstOrDefault(x => x.Name == w);
                if (c.Name == null) { Console.WriteLine($"  no clip '{w}'"); continue; }
                anims[w] = c.Load();
            }
            (Vector3[] P0, _) = Pose(anims.Count > 0 ? anims.Values.First() : null, 0);
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (Vector3 p in P0)
            {
                float sx = Vector3.Dot(p, right), sy = Vector3.Dot(p, up);
                minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx); minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
            }
            float span = Math.Max(maxX - minX, maxY - minY) * 1.45f;
            float cx = (minX + maxX) / 2, cy = (minY + maxY) / 2 + span * 0.04f;

            byte[] Render((Vector3[] P, Vector3[] N) pose)
            {
                int S = size;
                var img = new byte[S * S * 4];
                var zb = new float[S * S];
                Array.Fill(zb, float.MaxValue);
                for (int i = 0; i < S * S; i++)
                {
                    float g = 18 + 22f * (i / S) / S;   // dark backdrop, a little lighter at the bottom
                    img[4 * i] = (byte)g; img[4 * i + 1] = (byte)g; img[4 * i + 2] = (byte)(g + 6); img[4 * i + 3] = 255;
                }
                Vector3 Scr(Vector3 p) => new((Vector3.Dot(p, right) - cx) / span * S + S / 2f, S / 2f - (Vector3.Dot(p, up) - cy) / span * S, Vector3.Dot(p, fwd));
                int nuv = m.Uv0.Length / 2;
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int t = 0; t < m.TriangleMaterial.Length; t++)
                    {
                        int mi = m.TriangleMaterial[t];
                        if (matAdd[mi] != (pass == 1)) continue;
                        Tex? tex = matTex[mi];
                        Cmdl.Corner c0 = m.Triangles[3 * t], c1 = m.Triangles[3 * t + 1], c2 = m.Triangles[3 * t + 2];
                        Vector3 s0 = Scr(pose.P[c0.P]), s1 = Scr(pose.P[c1.P]), s2 = Scr(pose.P[c2.P]);
                        float area = (s1.X - s0.X) * (s2.Y - s0.Y) - (s2.X - s0.X) * (s1.Y - s0.Y);
                        if (Math.Abs(area) < 1e-6f) continue;
                        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(s0.X, Math.Min(s1.X, s2.X)))), x1 = Math.Min(S - 1, (int)Math.Ceiling(Math.Max(s0.X, Math.Max(s1.X, s2.X))));
                        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(s0.Y, Math.Min(s1.Y, s2.Y)))), y1 = Math.Min(S - 1, (int)Math.Ceiling(Math.Max(s0.Y, Math.Max(s1.Y, s2.Y))));
                        Vector2 Uv(Cmdl.Corner c) => c.T0 >= 0 && c.T0 < nuv ? new Vector2(m.Uv0[2 * c.T0], m.Uv0[2 * c.T0 + 1]) : Vector2.Zero;
                        Vector2 u0 = Uv(c0), u1 = Uv(c1), u2 = Uv(c2);
                        Vector3 n0 = pose.N[c0.P], n1 = pose.N[c1.P], n2 = pose.N[c2.P];
                        for (int y = y0; y <= y1; y++)
                        {
                            for (int x = x0; x <= x1; x++)
                            {
                                float px = x + 0.5f, py = y + 0.5f;
                                float w0 = ((s1.X - px) * (s2.Y - py) - (s2.X - px) * (s1.Y - py)) / area;
                                float w1 = ((s2.X - px) * (s0.Y - py) - (s0.X - px) * (s2.Y - py)) / area;
                                float w2 = 1 - w0 - w1;
                                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                                float z = w0 * s0.Z + w1 * s1.Z + w2 * s2.Z;
                                int o = y * S + x;
                                if (z >= zb[o]) continue;
                                float r = 0.62f, g = 0.62f, b = 0.66f;
                                if (tex != null)
                                {
                                    Vector2 uv = w0 * u0 + w1 * u1 + w2 * u2;
                                    int tx = (int)Math.Floor(uv.X * tex.W) % tex.W, ty = (int)Math.Floor(uv.Y * tex.H) % tex.H;
                                    if (tx < 0) tx += tex.W;
                                    if (ty < 0) ty += tex.H;
                                    int to = 4 * (ty * tex.W + tx);
                                    r = tex.Rgba[to] / 255f; g = tex.Rgba[to + 1] / 255f; b = tex.Rgba[to + 2] / 255f;
                                }
                                if (pass == 1)
                                {
                                    // additive (her glow): no depth write
                                    img[4 * o] = (byte)Math.Min(255, img[4 * o] + r * 200);
                                    img[4 * o + 1] = (byte)Math.Min(255, img[4 * o + 1] + g * 200);
                                    img[4 * o + 2] = (byte)Math.Min(255, img[4 * o + 2] + b * 200);
                                    continue;
                                }
                                zb[o] = z;
                                Vector3 n = (w0 * n0 + w1 * n1 + w2 * n2).Normalized();
                                float lit = 0.55f + 1.0f * Math.Max(0, Math.Abs(Vector3.Dot(n, light)));
                                img[4 * o] = (byte)Math.Min(255, r * lit * 255);
                                img[4 * o + 1] = (byte)Math.Min(255, g * lit * 255);
                                img[4 * o + 2] = (byte)Math.Min(255, b * lit * 255);
                            }
                        }
                    }
                }
                return img;
            }

            // the sheet: a row per clip, frames evenly spread over the clip (its first and last key included)
            var rows = anims.ToList();
            int W = size * frames, H = size * Math.Max(1, rows.Count);
            var sheet = new byte[W * H * 4];
            for (int ri = 0; ri < rows.Count; ri++)
            {
                Anim a = rows[ri].Value;
                for (int fi = 0; fi < frames; fi++)
                {
                    double t = frames == 1 ? 0 : a.Duration * fi / (frames - 1);
                    byte[] img = Render(Pose(a, t));
                    for (int y = 0; y < size; y++) Buffer.BlockCopy(img, y * size * 4, sheet, ((ri * size + y) * W + fi * size) * 4, size * 4);
                }
                Console.WriteLine($"    {rows[ri].Key}: {a.Duration:F2} s, {a.Keys} keys");
            }
            string sheetFile = Path.Combine(outDir, $"{charName}_sheet.png");
            PngOut.Write(sheetFile, sheet, W, H);
            File.WriteAllText(Path.Combine(outDir, $"{charName}_sheet.txt"), String.Join("\n", rows.Select(r => r.Key)) + "\n");
            Console.WriteLine($"  wrote {sheetFile} ({rows.Count} rows x {frames} frames)");

            string gifClip = Arg("--gif", "");
            if (gifClip.Length > 0 && anims.TryGetValue(gifClip, out Anim? ga))
            {
                using var gif = new Image<Rgba32>(size, size);
                int n = Math.Max(2, (int)Math.Round(ga.Duration * 30));
                for (int k = 0; k < n; k++)
                {
                    byte[] img = Render(Pose(ga, ga.Duration * k / n));
                    using Image<Rgba32> fr = Image.LoadPixelData<Rgba32>(img, size, size);
                    gif.Frames.AddFrame(fr.Frames.RootFrame);
                }
                gif.Frames.RemoveFrame(0);
                foreach (ImageFrame<Rgba32> f in gif.Frames) f.Metadata.GetGifMetadata().FrameDelay = 3;
                gif.Metadata.GetGifMetadata().RepeatCount = 0;
                string gifFile = Path.Combine(outDir, $"{charName}_{gifClip}.gif");
                gif.SaveAsGif(gifFile);
                Console.WriteLine($"  wrote {gifFile} ({gif.Frames.Count} frames)");
            }
        }

        // a character's posed positions at time t on its own skeleton (root left where the clip puts it): a prop's shape
        static Vector3[] PoseRigid(Cmdl m, List<(uint Bone, float Weight)[]> skin, Cinf skel, Anim? a, double t)
        {
            Dictionary<uint, double[]>? rot = null, trans = null;
            a?.Sample(t, out rot, out trans);
            var world = new Dictionary<uint, Matrix4>();
            int Depth(uint b) => skel.Bones.TryGetValue(skel.Bones[b].Parent, out _) && skel.Bones[b].Parent != b ? Depth(skel.Bones[b].Parent) + 1 : 0;
            foreach (uint b in skel.SortedIds().OrderBy(Depth))
            {
                Cinf.Bone bone = skel.Bones[b];
                bool hasParent = skel.Bones.TryGetValue(bone.Parent, out Cinf.Bone? par) && bone.Parent != b;
                Quaternion r = rot != null && rot.TryGetValue(b, out double[]? q) ? new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]) : Quaternion.Identity;
                Vector3 p = hasParent ? new Vector3((float)(bone.X - par!.X), (float)(bone.Y - par.Y), (float)(bone.Z - par.Z))
                    : new Vector3((float)bone.X, (float)bone.Y, (float)bone.Z);
                if (trans != null && trans.TryGetValue(b, out double[]? tv)) p = new Vector3((float)tv[0], (float)tv[1], (float)tv[2]);
                Matrix4 local = Matrix4.CreateFromQuaternion(r) * Matrix4.CreateTranslation(p);
                world[b] = hasParent ? local * world[bone.Parent] : local;
            }
            var P = new Vector3[m.PositionCount];
            for (int i = 0; i < P.Length; i++)
            {
                var v = new Vector4(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2], 1);
                Vector3 acc = Vector3.Zero;
                float ws = 0;
                if (i < skin.Count)
                {
                    foreach ((uint bb, float w) in skin[i])
                    {
                        if (!world.TryGetValue(bb, out Matrix4 wm)) continue;
                        Cinf.Bone bone = skel.Bones[bb];
                        acc += w * (v * Matrix4.CreateTranslation(-(float)bone.X, -(float)bone.Y, -(float)bone.Z) * wm).Xyz;
                        ws += w;
                    }
                }
                P[i] = ws > 0 ? acc / ws : v.Xyz;
            }
            return P;
        }
    }
}
