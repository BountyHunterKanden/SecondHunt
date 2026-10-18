using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

// An HD recipe's "recolor" (owner 2026-10-13: the beta Phazon Suit's morph ball = the final Phazon ball reskinned in the beta
// suit's colours, its inside and cracks popping): per texture of the user's own disc, recoloured as it is written. Only numbers
// live in the recipe -- measured on the two suits (the final and the beta Phazon Suit share one body, so the same spots pair
// their colours up) -- and nothing of the art ships.
//   "recolor": { "ramp": [[mid], [neon], [hot]], "textures": [
//       { "tex": "8b105f2e", "role": "shell", "curve": [[lum, r, g, b], ...], "gain": 2.0 },  greys by brightness on the curve;
//                                                                                              red/orange lines glow (gain)
//       { "tex": "2f1ac0dd", "role": "glow", "gain": 2.0 },                                   red/orange light -> the ramp
//       { "tex": "66cc3df4", "role": "tint", "color": [r, g, b], "boost": 1.6, "mix": 0.7 } ] }  towards one colour
namespace MphRecomp.Import.Hd
{
    public sealed class HdRecolor
    {
        public sealed record Texture(ulong Tex, string Role, double[][] Curve, double Gain, double[] Color, double Boost, double Mix);

        public double[] Mid = { 30, 150, 130 }, Neon = { 74, 255, 189 }, Hot = { 170, 255, 225 };
        public Texture[] Textures = Array.Empty<Texture>();

        static double[] V(JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

        public static HdRecolor Parse(JsonElement e)
        {
            var r = new HdRecolor();
            if (e.TryGetProperty("ramp", out JsonElement ramp))
            {
                double[][] c = ramp.EnumerateArray().Select(V).ToArray();
                if (c.Length != 3 || c.Any(x => x.Length != 3)) throw new InvalidOperationException("recolor ramp: three colours");
                (r.Mid, r.Neon, r.Hot) = (c[0], c[1], c[2]);
            }
            var list = new List<Texture>();
            foreach (JsonElement t in e.GetProperty("textures").EnumerateArray())
            {
                string role = t.GetProperty("role").GetString() ?? "";
                if (role is not ("shell" or "glow" or "tint")) throw new InvalidOperationException($"recolor role '{role}'");
                double[][] curve = t.TryGetProperty("curve", out JsonElement cu) ? cu.EnumerateArray().Select(V).ToArray() : Array.Empty<double[]>();
                if (role == "shell" && (curve.Length < 2 || curve.Any(p => p.Length != 4))) throw new InvalidOperationException("recolor shell: a curve of [lum, r, g, b]");
                list.Add(new Texture(UInt64.Parse(t.GetProperty("tex").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture), role, curve,
                    t.TryGetProperty("gain", out JsonElement g) ? g.GetDouble() : 1,
                    t.TryGetProperty("color", out JsonElement co) ? V(co) : new double[] { 255, 255, 255 },
                    t.TryGetProperty("boost", out JsonElement b) ? b.GetDouble() : 1,
                    t.TryGetProperty("mix", out JsonElement m) ? m.GetDouble() : 1));
            }
            r.Textures = list.ToArray();
            return r;
        }

        static double Lum(double r, double g, double b) => r * 0.299 + g * 0.587 + b * 0.114;

        static double Clamp01(double v) => Math.Clamp(v, 0, 1);

        // the texture's RGBA in place, when the recipe names it
        public void Apply(ulong tex, byte[] rgba)
        {
            Texture? t = Textures.FirstOrDefault(x => x.Tex == tex);
            if (t == null) return;
            Span<double> o = stackalloc double[3];
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                double r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                switch (t.Role)
                {
                    case "shell":
                    {
                        double l = Lum(r, g, b);
                        double[] c = Interp(t.Curve, l);
                        double ratio = Math.Clamp((l + 4) / (Math.Clamp(l, t.Curve[0][0], t.Curve[^1][0]) + 4), 0.5, 2);
                        double warm = Clamp01((r - b) / (r + 1) * 2);
                        Glow(r, g, b, t.Gain, o);
                        for (int k = 0; k < 3; k++) o[k] = c[k] * ratio * (1 - warm) + o[k] * warm;
                        break;
                    }
                    case "glow":
                        Glow(r, g, b, t.Gain, o);
                        break;
                    default: // tint
                    {
                        double l = Lum(r, g, b) / 255;
                        double[] src = { r, g, b };
                        for (int k = 0; k < 3; k++) o[k] = t.Color[k] * l * t.Boost * t.Mix + src[k] * (1 - t.Mix);
                        break;
                    }
                }
                rgba[i] = (byte)Math.Round(Math.Clamp(o[0], 0, 255));
                rgba[i + 1] = (byte)Math.Round(Math.Clamp(o[1], 0, 255));
                rgba[i + 2] = (byte)Math.Round(Math.Clamp(o[2], 0, 255));
            }
        }

        // red/orange light up the ramp by its brightness (x gain): mid -> neon -> hot; greys and whites keep their colour
        void Glow(double r, double g, double b, double gain, Span<double> o)
        {
            double l = Clamp01(Lum(r, g, b) / 255 * gain);
            double warm = Clamp01((r - b) / (r + 1));
            double t1 = Clamp01(l / 0.35), t2 = Clamp01((l - 0.35) / 0.45), t3 = Clamp01((l - 0.8) * 5);
            double[] src = { r, g, b };
            for (int k = 0; k < 3; k++)
            {
                double ramp = Math.Clamp((Mid[k] * t1 * (1 - t2) + Neon[k] * t2) * (1 - t3) + Hot[k] * t3, 0, 255);
                o[k] = ramp * warm + src[k] * (1 - warm);
            }
        }

        static double[] Interp(double[][] curve, double l)
        {
            if (l <= curve[0][0]) return curve[0][1..];
            for (int i = 1; i < curve.Length; i++)
            {
                if (l > curve[i][0]) continue;
                double f = (l - curve[i - 1][0]) / Math.Max(curve[i][0] - curve[i - 1][0], 1e-9);
                return new[] { 1, 2, 3 }.Select(k => curve[i - 1][k] + (curve[i][k] - curve[i - 1][k]) * f).ToArray();
            }
            return curve[^1][1..];
        }
    }
}
