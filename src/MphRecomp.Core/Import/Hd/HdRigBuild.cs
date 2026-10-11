using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MphRead;
using MphRecomp.Anim;
using MphRecomp.Assets;
using OpenTK.Mathematics;

namespace MphRecomp.Import.Hd
{
    // The rig caches an imported character needs, made at import from the folder RetroTrophy wrote, the way the PC batch
    // made them (rig_work/session16/scripts/native_batch.sh: -hdrig --savevariants, -rigscan --rig rigid --bind, then
    // prime_to_trophy.py --layout and -hdrig again):
    //   1. the "rigid" variant (<Stem>.rigid.mphrig): fitted on the game's own hunter model (MPH's Samus for the suits);
    //   2. Retro's skin laid onto that rig's vertices (<Stem>.retro.txt, RetroLayout);
    //   3. the "retro" variant (<Stem>.retro.mphrig), built from that layout -- the rig the campaign suit plays.
    // The other variants (straight, clean, tpose, hinge) are comparisons for the PC and dev viewer and aren't made here.
    public static class HdRigBuild
    {
        const string Crlf = "\r\n";

        static (Hunter, Model, List<Vector3>) Inputs(string daePath, string name)
        {
            if (!TrophyRigs.TryHunterFor(name, out Hunter hunter)) throw new InvalidDataException($"{name}: no hunter skeleton is mapped to it (TrophyRigs.RigOf)");
            Model model = Read.GetModelInstance(Metadata.HunterModels[hunter][0]).Model;
            DaeModel dae = DaeModel.Load(daePath);
            return (hunter, model, dae.Meshes.SelectMany(m => m.Positions).Select(p => new Vector3(p.X, p.Y, p.Z)).ToList());
        }

        // one variant fitted again from the files in the folder (the golden check rebuilds a rig from a reference layout)
        public static bool Refit(string daePath, string name, string key, Action<string>? log = null)
        {
            (_, Model model, List<Vector3> corners) = Inputs(daePath, name);
            return TrophyRigIO.LoadOrFitVariant(daePath, model, corners, TrophyRigIO.Variants.First(v => v.Key == key), fit: true, log) != null;
        }

        public static void Run(string daePath, RetroTrophy.Result conv, HdRecipe h, Action<float, string> progress, CancellationToken ct)
        {
            (Hunter hunter, Model model, List<Vector3> corners) = Inputs(daePath, h.Name);
            float at = 0.75f;
            void Log(string s) => progress(at, s);

            TrophyRigIO.RigVariant rigid = TrophyRigIO.Variants.First(v => v.Key == "rigid");
            TrophyRigIO.RigVariant retro = TrophyRigIO.Variants.First(v => v.Key == "retro");
            progress(0.75f, $"fitting the {hunter} rig (rigid)");
            TrophyRig rig = TrophyRigIO.LoadOrFitVariant(daePath, model, corners, rigid, fit: true, Log)
                ?? throw new InvalidDataException($"{h.Name}: the rigid rig could not be built");
            ct.ThrowIfCancellationRequested();

            // the rig's drawn triangles as vertex indices (RigScan --bind's <T>_tris.txt)
            var tris = new List<(int, int, int)>();
            for (int i = 0; i + 2 < corners.Count; i += 3)
            {
                int a = rig.IndexOf(new System.Numerics.Vector3(corners[i].X, corners[i].Y, corners[i].Z));
                int b = rig.IndexOf(new System.Numerics.Vector3(corners[i + 1].X, corners[i + 1].Y, corners[i + 1].Z));
                int c = rig.IndexOf(new System.Numerics.Vector3(corners[i + 2].X, corners[i + 2].Y, corners[i + 2].Z));
                if (a < 0 || b < 0 || c < 0 || rig.HidesTriangle(a, b, c)) continue;
                tris.Add((a, b, c));
            }
            var sculpt = rig.Vertices.Select(p => new System.Numerics.Vector3(p.X, p.Y, p.Z)).ToList();
            var log = new List<string>();
            string layout = RetroLayout.Build(conv, h, sculpt, tris, log);
            foreach (string s in log) Log(s);
            File.WriteAllText(TrophyRigIO.LayoutPathFor(daePath, retro), layout.Replace("\n", Crlf));
            ct.ThrowIfCancellationRequested();

            progress(at = 0.9f, "fitting the retro rig");
            _ = TrophyRigIO.LoadOrFitVariant(daePath, model, corners, retro, fit: true, Log)
                ?? throw new InvalidDataException($"{h.Name}: the retro rig could not be built");
        }
    }
}
