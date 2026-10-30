using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MphRecomp.Import;
using MphRecomp.Import.Beyond;

namespace MphRead
{
    // TEST-ONLY inputs of the Beyond gun builder (Guns/Gun4Builder) for -importcheck guns4: they read the reference
    // pipeline's intermediate files, which come from third-party tools and never ship (docs/IMPORT_PIPELINE.md rule 3).
    // The in-app importer implements IBeyondModelSource / IBeyondLookSource on our own Beyond readers instead.

    // The Prime 4 Model Dumper's glTF of a model (port of rig_work/gun/scripts/mp4_gltf.py): every mesh primitive's
    // positions, indices, JOINTS_0 / WEIGHTS_0, the skin's joint names; mesh "Mesh<k>_LOD<l>_MatID<m>" -> Number k, Lod l,
    // MaterialId m. Its node matrices and inverse binds are not read (the game's skeleton comes from the CHPR).
    internal sealed class GltfBeyondModel : IBeyondModelSource
    {
        readonly List<BeyondMesh> _meshes = new();
        public string Name { get; private set; } = "";
        public IReadOnlyList<BeyondMesh> Meshes => _meshes;

        static readonly Regex MeshNo = new(@"^Mesh(\d+)_"), LodNo = new(@"LOD(\d+)"), MatNo = new(@"MatID(\d+)");

        public static GltfBeyondModel Load(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement g = doc.RootElement;
            string dir = Path.GetDirectoryName(path) ?? ".";
            var bins = g.GetProperty("buffers").EnumerateArray().Select(b => File.ReadAllBytes(Path.Combine(dir, b.GetProperty("uri").GetString()!))).ToList();
            JsonElement accessors = g.GetProperty("accessors"), views = g.GetProperty("bufferViews");

            // one accessor as doubles (count x components), normalized integers as float32 / max (numpy's reading)
            double[] Acc(int i, out int nc)
            {
                JsonElement a = accessors[i];
                JsonElement bv = views[a.GetProperty("bufferView").GetInt32()];
                int ct = a.GetProperty("componentType").GetInt32();
                nc = a.GetProperty("type").GetString() switch
                {
                    "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16,
                    string t => throw new InvalidDataException($"glTF accessor type {t}"),
                    null => throw new InvalidDataException("glTF accessor without a type"),
                };
                int size = ct switch
                {
                    5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4,
                    _ => throw new InvalidDataException($"glTF component type {ct}"),
                };
                int count = a.GetProperty("count").GetInt32();
                int off = (bv.TryGetProperty("byteOffset", out JsonElement bo) ? bo.GetInt32() : 0) + (a.TryGetProperty("byteOffset", out JsonElement ao) ? ao.GetInt32() : 0);
                int stride = bv.TryGetProperty("byteStride", out JsonElement bs) && bs.GetInt32() > 0 ? bs.GetInt32() : size * nc;
                bool norm = a.TryGetProperty("normalized", out JsonElement ne) && ne.ValueKind == JsonValueKind.True;
                byte[] b = bins[bv.GetProperty("buffer").GetInt32()];
                float max = ct switch { 5120 => SByte.MaxValue, 5121 => Byte.MaxValue, 5122 => Int16.MaxValue, 5123 => UInt16.MaxValue, 5125 => UInt32.MaxValue, _ => 1 };
                var v = new double[count * nc];
                for (int r = 0; r < count; r++)
                {
                    for (int c = 0; c < nc; c++)
                    {
                        int p = off + r * stride + c * size;
                        double x = ct switch
                        {
                            5120 => (sbyte)b[p], 5121 => b[p], 5122 => BitConverter.ToInt16(b, p), 5123 => BitConverter.ToUInt16(b, p),
                            5125 => BitConverter.ToUInt32(b, p), _ => BitConverter.ToSingle(b, p),
                        };
                        if (norm && ct != 5126) x = (float)x / max;
                        v[r * nc + c] = x;
                    }
                }
                return v;
            }

            JsonElement nodes = g.GetProperty("nodes");
            var names = new List<string>();
            for (int i = 0; i < nodes.GetArrayLength(); i++)
            {
                names.Add(nodes[i].TryGetProperty("name", out JsonElement nm) ? nm.GetString() ?? "" : $"node{i}");
            }
            string[] joints = g.TryGetProperty("skins", out JsonElement skins) && skins.GetArrayLength() > 0
                ? skins[0].GetProperty("joints").EnumerateArray().Select(j => names[j.GetInt32()]).ToArray() : Array.Empty<string>();
            var model = new GltfBeyondModel { Name = Path.GetFileName(path) };
            JsonElement meshes = g.GetProperty("meshes");
            for (int mi = 0; mi < meshes.GetArrayLength(); mi++)
            {
                JsonElement m = meshes[mi];
                string mname = m.TryGetProperty("name", out JsonElement mn) ? mn.GetString() ?? "" : $"mesh{mi}";
                Match no = MeshNo.Match(mname), lod = LodNo.Match(mname), mat = MatNo.Match(mname);
                foreach (JsonElement p in m.GetProperty("primitives").EnumerateArray())
                {
                    JsonElement at = p.GetProperty("attributes");
                    double[] P = Acc(at.GetProperty("POSITION").GetInt32(), out int pc);
                    if (pc != 3) throw new InvalidDataException($"{mname}: POSITION is not VEC3");
                    var bm = new BeyondMesh
                    {
                        Name = mname, Number = no.Success ? Int32.Parse(no.Groups[1].Value, CultureInfo.InvariantCulture) : mi,
                        Lod = lod.Success ? Int32.Parse(lod.Groups[1].Value, CultureInfo.InvariantCulture) : -1,
                        MaterialId = mat.Success ? Int32.Parse(mat.Groups[1].Value, CultureInfo.InvariantCulture) : -1,
                        Positions = P.Select(x => (float)x).ToArray(), JointNames = joints,
                    };
                    if (at.TryGetProperty("JOINTS_0", out JsonElement ja))
                    {
                        double[] J = Acc(ja.GetInt32(), out int jc), W = Acc(at.GetProperty("WEIGHTS_0").GetInt32(), out int wc);
                        if (jc != wc) throw new InvalidDataException($"{mname}: JOINTS_0 / WEIGHTS_0 widths differ");
                        bm.InfluencesPerVertex = jc;
                        bm.Joints = J.Select(x => (int)x).ToArray();
                        bm.Weights = W.Select(x => (float)x).ToArray();
                    }
                    bm.Indices = p.TryGetProperty("indices", out JsonElement ie)
                        ? Acc(ie.GetInt32(), out _).Select(x => (int)x).ToArray()
                        : Enumerable.Range(0, bm.VertexCount).ToArray();
                    model._meshes.Add(bm);
                }
            }
            return model;
        }
    }

    // (the static conversion's look: Core Import/Guns/Gun4Sources.cs, Gun4Look -- the in-app import reads BeyondTrophy's)
}
