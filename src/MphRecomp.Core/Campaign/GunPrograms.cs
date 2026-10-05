using MphRecomp.Arenas;
using MphRecomp.Render;

namespace MphRecomp.Campaign
{
    // The GL programs of the first-person Prime gun (the device's CampaignGun and the PC's -guntest speccheck build the
    // same sources) and of the HD suit (below). The MP4 Legacy gun through the GX interpreter kept the Odin's GPU 93-99% busy in the VDO 2 Slench
    // fight (2026-10-03): seven normal-mapped materials, each pixel walking the interpreter's stage loop. Now
    //   * each material runs its own straight-line TEV program (ArenaGxSpec, with the gun's tint and normal maps); a
    //     material it can't write stays on the interpreter;
    //   * the gun's depth goes in first (Depth: no fragment work), so the room behind it and the gun's own hidden faces
    //     skip their shading, then the colour pass shades each gun pixel once.
    // Every program uses Vert, GxShader.Vert with an invariant position: the depth pass and the colour pass must put a
    // pixel at exactly the same depth for the colour pass's <= test.
    public static class GunPrograms
    {
        public static readonly string Vert = GxShader.Vert.Replace("#version 300 es\n", "#version 300 es\ninvariant gl_Position;\n");

        public const string DepthFrag = "#version 300 es\nprecision mediump float;\nvoid main(){}\n";

        // material m's own fragment program, or null (the interpreter, GxShader.Frag)
        public static string? Frag(HdGun gun, int m, GxShader.Params p) =>
            ArenaGxSpec.Frag(p, (int)TintMode(gun, m, p), gun.GlowUnit, normalMaps: true);

        // GxShader's uTint.w for material m: 0 none, 1 the whole material, 2 only texture unit GlowUnit
        public static float TintMode(HdGun gun, int m, GxShader.Params p) =>
            gun.TintMode(m, gun.GlowUnit >= 0 && gun.GlowUnit < 8 && p.Units[gun.GlowUnit] != null);

        // The HD suit's body and ball (the device's CampaignSuit, the PC's -suitspec): the same treatment, plus uFade,
        // MphRead's unmorph / cloak alpha, multiplying the output alpha. The MP4 Legacy body (28.5k triangles) through the
        // interpreter, twice a frame, filled the Odin's GPU when a cutscene camera passed through it (owner queue #37:
        // the Celestial Archives landing, 85-99% busy, 50-82 fps, 2026-10-04).

        // the interpreter with uFade; GxShader.Frag as it is (no fade) if its text ever changes under these two lines
        public static readonly string SuitInterpFrag = AddFade(GxShader.Frag);
        public static bool SuitFades => SuitInterpFrag != GxShader.Frag;

        static string AddFade(string f)
        {
            const string decl = "uniform int uBlendAlpha;", outA = "uBlendAlpha==1?outa:1.0)";
            if (!f.Contains(decl) || !f.Contains(outA)) return f;
            return f.Replace(decl, decl + " uniform float uFade;").Replace(outA, "(uBlendAlpha==1?outa:1.0)*uFade)");
        }

        // a suit material's own program (no tint), or null (the interpreter, SuitInterpFrag)
        public static string? SuitFrag(GxShader.Params p)
        {
            string? f = ArenaGxSpec.Frag(p, normalMaps: true);
            string a = p.Blend ? "outa" : "1.0", end = $" * uExposure, {a});\n}}\n";
            if (f == null || !f.EndsWith(end) || !f.Contains("out vec4 o;\n")) return null;
            f = f[..^end.Length] + $" * uExposure, {a} * uFade);\n}}\n";
            return f.Replace("out vec4 o;\n", "out vec4 o;\nuniform float uFade;\n");
        }
    }
}
