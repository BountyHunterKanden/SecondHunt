using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MphRecomp.Import.Retro;
using MphRecomp.Import.Retro3;

namespace MphRecomp.Import.Guns
{
    // Metroid Prime 3: Corruption's first-person arm cannon from the user's own Trilogy disc, in the same gun.bin /
    // gun.gx.json / gun.cfg / PNG format as the Prime 1 / Echoes guns (GunWriter). Port of gun_export3.py with its
    // defaults (Corruption's OWN animations; the --echoes-anims stand-in is not ported):
    //   the gun CHAR ("Power") = the gun CMDL (5 beam looks packed as material sets) + SKIN + CINF -- the same 50 bones
    //   as Prime 1 / Echoes' gun skeleton, numbered differently; the skeleton is read as Echoes' CINF (Cinf)
    //   skin: Skin3 in the model's SkinOrder (Cmdl3), with the neighbour vote at split copies
    //   materials: each material's texture passes turned into a GX program (Material3Gx)
    //   animations: the CHAR's animation set (Char3), every entry that plays one ANIM, decoded by Anim3 (version-1
    //   bitstream) and sampled per key like the others; plus the recipe's aliases (GunViewmodel's names)
    public static class Gun3Importer
    {
        public const string SourceText = "Corruption first-person gun (Retro, the owner's Trilogy disc), its own animations";

        public static GunImporter.Result Export(Pak3Set res, Gun3Recipe a, string outDir, Action<float, string>? progress = null,
            CancellationToken ct = default)
        {
            var result = new GunImporter.Result();
            void Say(float f, string msg)
            {
                result.Log.Add(msg);
                progress?.Invoke(f, msg);
            }
            Directory.CreateDirectory(outDir);
            foreach ((string what, ulong id, string type) in new[] { ("char", a.Char, "CHAR"), ("cmdl", a.Cmdl, "CMDL"), ("cskr", a.Cskr, "CSKR"), ("cinf", a.Cinf, "CINF") })
            {
                string? t = res.TypeOf(id);
                if (t != type) throw new InvalidDataException($"{what} {id:x16} is {(t == null ? "missing" : "a " + t)}, not a {type}");
            }
            Cmdl3 model = Cmdl3.Load(res.Get(a.Cmdl), a.GunSet);
            int n = model.PositionCount;
            List<(uint Bone, float Weight)[]> skin = Skin3.Load(res.Get(a.Cskr), n, model.SkinOrder, model.TrianglePositions());
            Cinf skel = Cinf.Load(res.Get(a.Cinf));
            GunSkeleton sk = GunSkeleton.From(skel);
            Say(0.05f, $"gun {a.Cmdl:x16} (material set {a.GunSet} of {model.MaterialSets}{(model.Packed ? ", packed" : "")}): "
                + $"{model.TriangleCount} tris, {model.Materials.Length} materials, {sk.BoneIds.Count} bones");

            var mesh = new GunMesh
            {
                Positions = model.Positions, Normals = model.Normals, Uv0 = model.Uv0, Triangles = model.Triangles,
                TriangleMaterial = model.TriangleMaterial,
                Materials = model.Materials.Select(m => GunMaterial.From(Material3Gx.ToGx(m))).ToList(),
                SkinOf = c => skin[c.P].Where(bw => bw.Weight > 0),
            };
            var src = new GunSource { Skeleton = sk, Texture = res.Get, Lights = a.Lights, Source = SourceText };
            src.Meshes.Add(mesh);
            ct.ThrowIfCancellationRequested();

            // the CHAR's animations that play one ANIM (a repeated name keeps its first place, its last id)
            var order = new List<string>();
            var ids = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach ((string nm, Char3.Meta meta) in Char3.Animations(res.Get(a.Char), id => res.TypeOf(id) == "ANIM"))
            {
                List<Char3.Meta> ps = Char3.Prims(meta);
                if (ps.Count != 1) continue;
                if (!ids.ContainsKey(nm)) order.Add(nm);
                ids[nm] = ps[0].AnimId;
            }
            foreach ((string alias, string from) in a.AnimAliases)
            {
                if (ids.TryGetValue(from, out ulong id) && !ids.ContainsKey(alias))
                {
                    order.Add(alias);
                    ids[alias] = id;
                }
            }
            foreach (string nm in order)
            {
                ulong aid = ids[nm];
                src.Animations.Add((nm, () => Anim3.Load(res.Get(aid))));
            }
            Say(0.1f, $"animations: {order.Count} (Corruption's own)");

            GunWriter.Write(src, outDir, result, progress, ct);
            return result;
        }
    }
}
