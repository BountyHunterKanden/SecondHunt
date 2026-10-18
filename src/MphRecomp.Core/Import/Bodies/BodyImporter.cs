using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MphRecomp.Import.Guns;
using MphRecomp.Import.Hd;
using MphRecomp.Import.Retro;
using MphRecomp.Import.Retro3;

namespace MphRecomp.Import.Bodies
{
    // A Retro character on ITS OWN skeleton with ITS OWN clips, from the user's own disc, for a multiplayer character
    // drawn natively (docs/CHARACTERS_AND_SKINS.md D20/D22: the Trooper on the Prime 1 pirate rig; D18: Dark Samus on
    // her Echoes / Corruption rig) -- no DS rig, no retarget. Written by GunWriter in the first-person guns' format, so
    // HdGun.Load reads it unchanged; into one folder bodies/<Name>/:
    //   body.bin      "MPHGUN1": skeleton + per-material skinned vertices + every single-ANIM clip sampled per key, in
    //                 MPH's Y-up space ((x, y, z) -> (-x, z, y): +Z = the character's forward, +X its left)
    //   body.gx.json  each material's GX program        body.cfg  the lines below        <tex>.png  the textures
    // Root motion is taken out of every clip (the game moves the player): the top bone stays on its bind spot unturned
    // and the pelvis ("Skeleton_Root") keeps only its sway across the floor around its bind spot, and its own height
    // (RootPin).
    // body.cfg:
    //   height <h>    the bind pose's mesh height (MPH units before any scale)        feet <y>   its lowest point
    //   root <bone>   the pelvis bone the clips were pinned by                        game <mp1|mp2|mp3>
    //   speed <clip> <v>   how fast the clip's own root motion travels across the floor (model units per second), so
    //                 a run plays at the player's ground speed
    public static class BodyImporter
    {
        public const string RootBone = "Skeleton_Root";

        // Prime 1 / Echoes: the ANCS character (res: resource bytes by id, the recipe's paks in order)
        public static GunImporter.Result Export(Func<uint, byte[]> res, HdRecipe h, string outDir, Action<float, string>? progress = null,
            CancellationToken ct = default)
        {
            bool echoes = h.Game == "mp2";
            (uint mid, uint sid, uint kid) = Ancs.FindCharacter(res(h.Ancs), h.Char);
            Cmdl m = Cmdl.Load(res, mid, sid, h.MatSet);
            Cinf skel = Cinf.Load(res(kid));
            List<(uint, float)[]> skin = m.Skin ?? throw new InvalidDataException($"{h.Char}: the model has no skin");
            GunMesh mesh = GunMesh.From(m);
            mesh.SkinOf = c => skin[c.P].Where(bw => bw.Item2 > 0);
            var src = new GunSource
            {
                Skeleton = GunSkeleton.From(skel), Texture = id => res((uint)id),
                Source = $"{h.Char} body (Retro, the owner's {(echoes ? "Echoes" : "Prime")} disc), its own skeleton and clips",
            };
            src.Meshes.Add(mesh);
            Ancs.AnimSet set = Ancs.Parse(res(h.Ancs)).Set ?? throw new InvalidDataException($"{h.Ancs:x8}: no animation set found");
            var order = new List<string>();
            var ids = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach ((string nm, Ancs.MetaAnim meta) in set.Anims)
            {
                List<Ancs.MetaAnim> prims = Ancs.Prims(meta);
                if (prims.Count != 1) continue;
                if (!ids.ContainsKey(nm)) order.Add(nm);
                ids[nm] = prims[0].AnimId;
            }
            foreach (string nm in order)
            {
                uint aid = ids[nm];
                src.Animations.Add((nm, () => Retro.Anim.Load(res(aid), echoes)));
            }
            progress?.Invoke(0.05f, $"{h.Char}: model {mid:x8} ({m.TriangleCount} tris, {m.Materials.Length} materials), {skel.Bones.Count} bones, {order.Count} clips");
            return Write(src, skel, h, outDir, progress, ct);
        }

        // Corruption: the recipe's first CHAR (its model, skin and skeleton; the clips of its animation set)
        public static GunImporter.Result Export3(Pak3Set res, HdRecipe h, string outDir, Action<float, string>? progress = null,
            CancellationToken ct = default)
        {
            if (h.Chars.Length == 0) throw new InvalidDataException($"{h.Name}: a Prime 3 body names its CHAR (\"chars\")");
            ulong cid = h.Chars[0];
            (string cname, List<(string Type, ulong Id)> parts) = Char3.CharIds(res.Get(cid), res.TypeOf);
            ulong Of(string type) => parts.Where(x => x.Type == type).Select(x => (ulong?)x.Id).FirstOrDefault()
                ?? throw new InvalidDataException($"CHAR {cid:x16} names no {type}");
            Cmdl3 m3 = Cmdl3.Load(res.Get(Of("CMDL")), h.MatSet);
            List<(uint Bone, float Weight)[]> skin = Skin3.Load(res.Get(Of("CSKR")), m3.PositionCount, m3.SkinOrder, m3.TrianglePositions());
            Cinf skel = Cinf.Load(res.Get(Of("CINF")));
            var mesh = new GunMesh
            {
                Positions = m3.Positions, Normals = m3.Normals, Uv0 = m3.Uv0, Triangles = m3.Triangles, TriangleMaterial = m3.TriangleMaterial,
                Materials = m3.Materials.Select(x => GunMaterial.From(Material3Gx.ToGx(x))).ToList(),
                SkinOf = c => skin[c.P].Where(bw => bw.Weight > 0),
            };
            var src = new GunSource
            {
                Skeleton = GunSkeleton.From(skel), Texture = res.Get,
                Source = $"{cname} body (Retro, the owner's Corruption disc), its own skeleton and clips",
            };
            src.Meshes.Add(mesh);
            var order = new List<string>();
            var ids = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach ((string nm, Char3.Meta meta) in Char3.Animations(res.Get(cid), id => res.TypeOf(id) == "ANIM"))
            {
                List<Char3.Meta> ps = Char3.Prims(meta);
                if (ps.Count != 1 || h.Skip.Contains(nm)) continue; // (the recipe's "skip": left out)
                if (!ids.ContainsKey(nm)) order.Add(nm);
                ids[nm] = ps[0].AnimId;
            }
            foreach (string nm in order)
            {
                ulong aid = ids[nm];
                src.Animations.Add((nm, () => Anim3.Load(res.Get(aid))));
            }
            // clips borrowed from another CHAR of the same paks, put on this skeleton by bone name ("clips")
            foreach (HdRecipe.Borrow b in h.Borrows)
            {
                (string bname, List<(string Type, ulong Id)> bparts) = Char3.CharIds(res.Get(b.Char), res.TypeOf);
                ulong bk = bparts.Where(x => x.Type == "CINF").Select(x => (ulong?)x.Id).FirstOrDefault()
                    ?? throw new InvalidDataException($"CHAR {b.Char:x16} names no CINF");
                Cinf bskel = Cinf.Load(res.Get(bk));
                var bids = new Dictionary<string, ulong>(StringComparer.Ordinal);
                foreach ((string nm, Char3.Meta meta) in Char3.Animations(res.Get(b.Char), id => res.TypeOf(id) == "ANIM"))
                {
                    List<Char3.Meta> ps = Char3.Prims(meta);
                    if (ps.Count == 1) bids.TryAdd(nm, ps[0].AnimId);
                }
                foreach (string nm in b.Names)
                {
                    if (!bids.TryGetValue(nm, out ulong aid)) throw new InvalidDataException($"{h.Name}: {bname} has no single clip '{nm}'");
                    if (ids.ContainsKey(nm)) continue;
                    ids[nm] = aid; order.Add(nm);
                    src.Animations.Add((nm, () => BodyClipRetarget.Retarget(bskel, skel, Anim3.Load(res.Get(aid)), RootBone)));
                }
            }
            // ... and her own bind pose as a clip ("bind": arms straight out), for an arm held straight along the aim
            if (h.Borrows.Length > 0 && !ids.ContainsKey(BodyClipRetarget.BindClip))
            {
                order.Add(BodyClipRetarget.BindClip);
                src.Animations.Add((BodyClipRetarget.BindClip, () => BodyClipRetarget.Bind(skel)));
            }
            progress?.Invoke(0.05f, $"{cname}: model {Of("CMDL"):x16} ({m3.TriangleCount} tris, {m3.Materials.Length} materials), {skel.Bones.Count} bones, {order.Count} clips");
            return Write(src, skel, h, outDir, progress, ct);
        }

        static GunImporter.Result Write(GunSource src, Cinf skel, HdRecipe h, string outDir, Action<float, string>? progress, CancellationToken ct)
        {
            var result = new GunImporter.Result();
            // the bind mesh's height, in MPH's Y-up space (Retro's Z)
            double lo = Double.MaxValue, hi = Double.MinValue;
            foreach (GunMesh mesh in src.Meshes)
            {
                for (int i = 2; i < mesh.Positions.Length; i += 3)
                {
                    lo = Math.Min(lo, mesh.Positions[i]);
                    hi = Math.Max(hi, mesh.Positions[i]);
                }
            }
            if (hi <= lo) throw new InvalidDataException($"{h.Name}: the model has no height");
            uint? pelvis = skel.Bones.Where(b => b.Value.Name == RootBone).Select(b => (uint?)b.Key).FirstOrDefault();
            var tops = skel.Bones.Where(b => b.Value.Parent == b.Key || !skel.Bones.ContainsKey(b.Value.Parent)).Select(b => b.Key).ToList();
            var speeds = new List<(string Clip, double Speed)>();
            var opt = new GunWriter.Options
            {
                BaseName = "body", SplitPalettes = true,
                Cfg = _ =>
                {
                    var c = new StringBuilder();
                    c.Append("height ").Append((hi - lo).ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
                    c.Append("feet ").Append(lo.ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
                    if (pelvis != null) c.Append("root ").Append(RootBone).Append('\n');
                    c.Append("game ").Append(h.Game).Append('\n');
                    foreach ((string clip, double v) in speeds)
                    {
                        c.Append("speed ").Append(clip).Append(' ').Append(v.ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
                    }
                    return c.ToString();
                },
                KeyFilter = (a, name) =>
                {
                    double v = RootSpeed(tops, pelvis, a);
                    if (v > 0.01) speeds.Add((name, v));
                    return RootPin(skel, tops, pelvis, a);
                },
            };
            GunWriter.Write(src, outDir, result, opt, progress, ct);
            return result;
        }

        // how fast the clip's root motion (the top bones' + the pelvis's translation, across the floor) travels, first key
        // to last
        static double RootSpeed(List<uint> tops, uint? pelvis, Retro.Anim a)
        {
            if (a.Keys < 2 || a.Duration <= 0) return 0;
            (double X, double Y) At(double t)
            {
                a.Sample(t, out _, out Dictionary<uint, double[]> trans);
                double x = 0, y = 0;
                foreach (uint b in tops.Concat(pelvis is uint p ? new[] { p } : Array.Empty<uint>()))
                {
                    if (trans.TryGetValue(b, out double[]? v))
                    {
                        x += v[0]; y += v[1];
                    }
                }
                return (x, y);
            }
            (double x0, double y0) = At(0);
            (double x1, double y1) = At((a.Keys - 1) * a.Tick);
            return Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / ((a.Keys - 1) * a.Tick);
        }

        // the clip's root motion out (see the header). Retro keeps the travel on the top bone ("root", across the floor
        // only) and the stance on the pelvis, whose local spot also makes up for the top bone's uneven steps: the pelvis's
        // spot over the floor (top + pelvis) less its steady drift (first key to last) and less its mean is the sway it
        // keeps, around its bind spot; its height is the clip's own. The top bones stay on their bind spots, unturned.
        static Action<Dictionary<uint, double[]>, Dictionary<uint, double[]>> RootPin(Cinf skel, List<uint> tops, uint? pelvis, Retro.Anim a)
        {
            double[]? pelvisBind = null;
            var sway = new (double X, double Y)[Math.Max(a.Keys, 1)];
            if (pelvis is uint pb)
            {
                Cinf.Bone b = skel.Bones[pb];
                double px = 0, py = 0, pz = 0;
                if (b.Parent != pb && skel.Bones.TryGetValue(b.Parent, out Cinf.Bone? par))
                {
                    px = par.X; py = par.Y; pz = par.Z;
                }
                pelvisBind = new[] { b.X - px, b.Y - py, b.Z - pz };
                var at = new (double X, double Y)[sway.Length];
                for (int k = 0; k < a.Keys; k++)
                {
                    a.Sample(k * a.Tick, out _, out Dictionary<uint, double[]> trans);
                    double x = trans.TryGetValue(pb, out double[]? t) ? t[0] : pelvisBind[0], y = t != null ? t[1] : pelvisBind[1];
                    foreach (uint top in tops)
                    {
                        if (trans.TryGetValue(top, out double[]? r))
                        {
                            x += r[0]; y += r[1];
                        }
                    }
                    at[k] = (x, y);
                }
                int last = Math.Max(a.Keys - 1, 1);
                double mx = 0, my = 0;
                for (int k = 0; k < a.Keys; k++)
                {
                    double f = (double)k / last;
                    sway[k] = (at[k].X - (at[0].X + (at[^1].X - at[0].X) * f), at[k].Y - (at[0].Y + (at[^1].Y - at[0].Y) * f));
                    mx += sway[k].X / a.Keys; my += sway[k].Y / a.Keys;
                }
                for (int k = 0; k < a.Keys; k++) sway[k] = (sway[k].X - mx, sway[k].Y - my);
            }
            int key = 0; // GunWriter filters the keys in order, once each
            return (rot, trans) =>
            {
                foreach (uint t in tops)
                {
                    Cinf.Bone b = skel.Bones[t];
                    rot[t] = new double[] { 0, 0, 0, 1 };
                    trans[t] = new[] { b.X, b.Y, b.Z };
                }
                if (pelvis is uint p && pelvisBind != null)
                {
                    (double sx, double sy) = sway[Math.Min(key, sway.Length - 1)];
                    double z = trans.TryGetValue(p, out double[]? cur) ? cur[2] : pelvisBind[2];
                    trans[p] = new[] { pelvisBind[0] + sx, pelvisBind[1] + sy, z };
                }
                key++;
            };
        }
    }

    // the "bodies" item kind: recipes/bodies/<Name>.json -> <root>/bodies/<Name>/, in the HD recipes' shape (HdRecipe:
    // game, discId, paks, ancs + char; Prime 3: chars, the first one the body)
    public sealed class BodyImportKind : IImportKind
    {
        public string Kind => "bodies";

        public IEnumerable<(string Path, string Sha1)> Sources(ImportRecipe recipe)
        {
            HdRecipe h = HdRecipe.From(recipe);
            if (h.Game == "mp4") yield break;   // Prime 4: the assets themselves are checked as they're read (BeyondBody)
            foreach (HdRecipe.PakRef p in h.Paks) yield return (p.Name, p.Sha1);
        }

        public void Run(ImportContext ctx, ImportRecipe recipe, string outDir, Action<float, string> progress, CancellationToken ct)
        {
            HdRecipe h = HdRecipe.From(recipe);
            if (h.Game == "mp4")
            {
                // Prime 4: the RomFS paks read in place, our clean-room readers (Import/Beyond)
                var archives = new List<Beyond.BeyondArchive>();
                try
                {
                    foreach (HdRecipe.PakRef p in h.Paks)
                    {
                        if (!ctx.Source.Contains(p.Name)) throw new FileNotFoundException($"{p.Name} is not in these files");
                        archives.Add(new Beyond.BeyondArchive(ctx.Source.OpenRead(p.Name), p.Name));
                    }
                    BeyondBody.Export(archives, h, outDir, progress, ct, recipe.OptStr("part") ?? "body"); // "lockjaw": Sylux's alt form
                }
                finally
                {
                    foreach (Beyond.BeyondArchive a in archives) a.Dispose();
                }
                return;
            }
            if (h.Game is not ("mp1" or "mp2" or "mp3")) throw new NotSupportedException($"{recipe.Id}: bodies from game '{h.Game}' are not importable yet");
            if (h.Game == "mp3")
            {
                // read in place (only the resources asked for): a Hyper effects pak can be huge (InGameAudio.pak, ~740 MB on
                // the standalone disc)
                var set = new Pak3Set(h.Paks.Select(p => ctx.Shared("pak3:" + p.Name, () => new Pak3(ctx.Source.OpenRead(p.Name), p.Name, leaveOpen: false))));
                BodyImporter.Export3(set, h, outDir, progress, ct);
                MphRecomp.Import.Effects.HyperFx.Export(set, recipe, outDir); // Dark Samus's Hyper Mode effects ("hyperfx")
                return;
            }
            Pak[] paks = h.Paks.Select(p => ctx.Pak(p.Name)).ToArray();
            byte[] Res(uint id)
            {
                foreach (Pak p in paks)
                {
                    if (p.Contains(id)) return p.Get(id);
                }
                throw new KeyNotFoundException($"{id:x8} is in none of {String.Join(", ", h.Paks.Select(p => p.Name))}");
            }
            BodyImporter.Export(Res, h, outDir, progress, ct);
            Effects.BodyFx.Export(Res, recipe, h.Ancs, outDir); // the body's own Prime effects, when the recipe asks ("fx")
        }
    }
}
