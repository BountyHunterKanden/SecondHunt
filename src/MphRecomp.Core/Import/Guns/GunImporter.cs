using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Guns
{
    // A Retro first-person arm cannon from the user's own Prime / Echoes disc, as the campaign viewmodel reads it
    // (HdGun.Load). Port of gun_export.py (the writing half is GunWriter, shared with Corruption's Gun3Importer); writes,
    // into one suit folder:
    //   gun.bin      "MPHGUN1": skeleton + per-material skinned vertices (GxShader layout) + every animation sampled per key
    //   gun.gx.json  each material's GX program, converted from Retro's exactly like prime_to_trophy.py (GxJson.Read)
    //   gun.cfg      "lights <materials>" (take MPH's per-weapon colour) + "scale <s>" (the gun's size in MPH's gun space)
    //   <tex>.png    the textures, stored upside down like the trophies' (UVs stay Retro's)
    // The suit's forearm (recipe "arm": Prime 1 PowerArm/VariaArm/...; Echoes VariaArm with one material set per suit) is
    // drawn by Retro at the gun's "elbow" locator, whose bind pose is the origin with no rotation -- so its vertices are
    // already in gun space and ride the elbow bone rigidly (recoil and holstering move it with the gun).
    // Coordinates are converted from Retro's (X right, Y forward, Z up) to MPH's gun space (X left, Y up, Z forward):
    // (x, y, z) -> (-x, z, y), a proper rotation, so rotations convert as q -> (C * axis, w).
    public static class GunImporter
    {
        // MPH's own gun's elbow-to-muzzle length (see GunWriter)
        public const double MphGunLength = GunWriter.MphGunLength;

        public sealed class Result
        {
            public int Bones, Materials, Animations, Textures, Bytes;
            public double Scale;
            public readonly List<string> Log = new();
        }

        // res: resource bytes by id (the recipe's pak)
        public static Result Export(Func<uint, byte[]> res, GunRecipe a, string outDir, Action<float, string>? progress = null,
            CancellationToken ct = default)
        {
            var result = new Result();
            void Say(float f, string msg)
            {
                result.Log.Add(msg);
                progress?.Invoke(f, msg);
            }
            Directory.CreateDirectory(outDir);
            (uint mid, uint sid, uint kid) = Retro.Ancs.FindCharacter(res(a.Ancs), a.Char);
            Cmdl gun = Cmdl.Load(res, mid, sid, a.GunSet);
            Cinf skel = Cinf.Load(res(kid));
            GunSkeleton sk = GunSkeleton.From(skel);
            var src = new GunSource
            {
                Skeleton = sk, Texture = id => res((uint)id), Lights = a.Lights,
                Source = $"{a.Char} first-person gun (Retro, the owner's Prime disc)" + (a.Arm != null ? $" + arm {a.Arm}" : ""),
            };

            // parts: (model, per-corner skin lookup)
            List<(uint, float)[]> gunSkin = gun.Skin ?? throw new InvalidDataException("the gun has no skin");
            GunMesh gunMesh = GunMesh.From(gun);
            gunMesh.SkinOf = c => gunSkin[c.P].Where(bw => bw.Item2 > 0);
            src.Meshes.Add(gunMesh);
            if (a.Arm != null)
            {
                Cmdl arm = Cmdl.Load(res, a.ArmId, null, a.ArmSet);
                int ei = sk.BoneIds.FindIndex(b => skel.Bones[b].Name == "elbow");
                if (ei < 0) throw new InvalidDataException("the gun skeleton has no 'elbow'");
                uint elbowId = sk.BoneIds[ei];
                double[] eb = sk.Bind[sk.Index[elbowId]];
                if (eb.Any(v => Math.Abs(v) > 1e-4))
                {
                    throw new InvalidDataException($"elbow bind ({eb[0]}, {eb[1]}, {eb[2]}) is not the origin");
                }
                GunMesh armMesh = GunMesh.From(arm);
                armMesh.SkinOf = _ => new[] { (elbowId, 1f) };
                src.Meshes.Add(armMesh);
                Say(0.1f, $"arm {a.Arm} (material set {a.ArmSet}): {arm.TriangleCount} tris, {arm.Materials.Length} materials on bone 'elbow'");
            }
            ct.ThrowIfCancellationRequested();

            // animations: the ANCS set's single-primitive entries (a repeated name keeps its first place, its last id)
            Retro.Ancs ancs = Retro.Ancs.Parse(res(a.Ancs));
            Retro.Ancs.AnimSet set = ancs.Set ?? throw new InvalidDataException($"{a.Ancs:x8}: no animation set found");
            var animOrder = new List<string>();
            var animIds = new Dictionary<string, uint>();
            foreach ((string nm, Retro.Ancs.MetaAnim meta) in set.Anims)
            {
                List<Retro.Ancs.MetaAnim> prims = Retro.Ancs.Prims(meta);
                if (prims.Count != 1) continue;
                if (!animIds.ContainsKey(nm)) animOrder.Add(nm);
                animIds[nm] = prims[0].AnimId;
            }
            foreach (string nm in animOrder)
            {
                uint aid = animIds[nm];
                src.Animations.Add((nm, () => Retro.Anim.Load(res(aid), a.Echoes)));
            }

            GunWriter.Write(src, outDir, result, progress, ct);
            return result;
        }
    }

    // the "guns" item kind: recipes/guns/<Suit>.json -> <root>/guns/<Suit>/ (Prime 1 / Echoes: GunImporter; Corruption,
    // game "mp3": Gun3Importer; Beyond, game "mp4": Gun4Builder, not importable yet -- see Run)
    public sealed class GunImportKind : IImportKind
    {
        public string Kind => "guns";

        public IEnumerable<(string Path, string Sha1)> Sources(ImportRecipe recipe)
        {
            if (Gun4Recipe.Applies(recipe))
            {
                // TODO (Beyond RomFS readers): the CHPR and the model are resources inside a RomFS pak (Gun4Recipe.Pak),
                // checked against chprSha1 (+ the model's hash) once the pak reader can hand them out
                yield break;
            }
            if (Gun3Recipe.Applies(recipe))
            {
                Gun3Recipe g3 = Gun3Recipe.From(recipe);
                yield return (g3.Pak, g3.PakSha1);
                yield break;
            }
            GunRecipe g = GunRecipe.From(recipe);
            yield return (g.Pak, g.PakSha1);
        }

        public void Run(ImportContext ctx, ImportRecipe recipe, string outDir, Action<float, string> progress, CancellationToken ct)
        {
            if (Gun4Recipe.Applies(recipe))
            {
                // TODO: game "mp4" is not registered in ImportService yet. Gun4Builder.Build is ready (golden-checked by
                // -importcheck guns4 on test-only adapters); it needs IBeyondModelSource / IBeyondLookSource from our
                // own Beyond readers (RomFS pak + SMDL/CMDL + MATI/TXTR + the material conversion: the rigging
                // session's) and ChprCharacter.Load on the CHPR's bytes
                throw new NotSupportedException($"{recipe.Id}: Metroid Prime 4 guns need the Beyond RomFS readers (not in yet)");
            }
            if (Gun3Recipe.Applies(recipe))
            {
                Gun3Recipe g3 = Gun3Recipe.From(recipe);
                Retro3.Pak3 pak3 = ctx.Shared("pak3:" + g3.Pak, () => new Retro3.Pak3(ctx.File(g3.Pak), g3.Pak));
                Gun3Importer.Export(new Retro3.Pak3Set(new[] { pak3 }), g3, outDir, progress, ct);
                return;
            }
            GunRecipe g = GunRecipe.From(recipe);
            Pak pak = ctx.Pak(g.Pak);
            GunImporter.Export(pak.Get, g, outDir, progress, ct);
        }
    }
}
