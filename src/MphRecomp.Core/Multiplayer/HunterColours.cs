using System;
using System.Collections.Generic;
using System.Globalization;
using MphRead;
using MphRead.Formats;

namespace MphRecomp.Multiplayer
{
    // The 5th-8th seat playing the same hunter in an 8-player match (owner 10-11/10-12): four more colours per hunter, as
    // MPH's own pal_01-04 are made. Vanilla's copies put two colours on two groups of parts (Trace's chest hole + eye vs his
    // armour, Kanden's lights, Sylux's stripes vs body), so each new one is built from one of MPH's own palettes by moving
    // its two hue groups to two new colours (owner-approved
    // colour sheet). Extra palette sets appended to every model of the hunter as it loads (Read.HostExtraRecolors,
    // through LookAlikes' hook), picked per seat by PlayerEntity.HostLookRecolor / HostGunLookRecolor. The seat's MPH
    // Recolor stays 0-3 (gameplay, effects, replays and netcode as before): drawing only. Their bombs' colours are made
    // the same way (Sylux's Lockjaw glow and trail, Kanden's tail bomb).
    public static class HunterColours
    {
        public const int Copies = 4; // the 5th-8th

        static readonly Dictionary<string, Hunter> ModelHunter = new(StringComparer.Ordinal);
        // MPH's own recolor count of each model that got the sets (their first index), recorded as it loads
        static readonly Dictionary<string, int> _start = new(StringComparer.Ordinal);
        static readonly object _lock = new();
        static bool _installed;

        static HunterColours()
        {
            foreach (var kv in Metadata.HunterModels)
            {
                if (kv.Key == Hunter.Guardian) continue; // (not a multiplayer pick; shares Samus's alt and gun)
                foreach (string m in kv.Value) ModelHunter.TryAdd(m, kv.Key);
            }
            ModelHunter["KandenAlt_TailBomb"] = Hunter.Kanden;
            ModelHunter["WeavelAlt_Turret_lod0"] = Hunter.Weavel;
        }

        // per hunter, the 5th-8th: "two:K:A:TA:B:TB" = MPH's palette K (1-4), its colours nearer hue A turned so A lands on
        // TA, nearer B so B lands on TB (TA/TB a hue, "hue*brightness", w = white, k = black); "lights:T" / "white:T" =
        // Spire from palette 1: crystals to hue T / near-white, the rock's shadows toward it (see Spire())
        static readonly Dictionary<Hunter, string[]> Specs = new()
        {
            [Hunter.Samus] = new[] { "two:3:210:w:315:190", "two:3:210:50*1.35:315:0*1.2", "two:3:210:120*1.2:315:275", "two:3:210:k:315:50*1.3" },
            [Hunter.Kanden] = new[] { "two:2:290:130:220:185", "two:2:290:25*1.4:220:270", "two:2:290:330*1.25:220:w", "two:2:290:w:220:0" },
            [Hunter.Trace] = new[] { "two:4:255:235*1.4:300:30", "two:4:255:130:300:300", "two:4:255:w:300:195", "two:4:255:k:300:55" },
            [Hunter.Sylux] = new[] { "two:1:235:345:115:0", "two:1:235:235:115:w", "two:1:235:25:115:30", "two:1:235:240:115:235" },
            [Hunter.Noxus] = new[] { "two:2:270:350:40:30", "two:2:270:45:40:0", "two:2:270:w:40:190", "two:2:270:120:40:55" },
            [Hunter.Spire] = new[] { "lights:0", "lights:220", "lights:275", "white:200" },
            [Hunter.Weavel] = new[] { "two:4:60:w:185:25", "two:4:60:275:185:55", "two:4:60:175:185:330", "two:4:60:k:185:185" },
        };

        // the 5th-8th Sylux's Lockjaw glow (MPH's bombStartSylux* differ only in the glow's colour constants: 0.25,0.8,1 /
        // yellow / violet / white / orange / green): his stripes' (lights group) colour in each new one
        public static (float R, float G, float B) SyluxBombColor(int copy) => Target(Specs[Hunter.Sylux][Math.Clamp(copy, 0, Copies - 1)].Split(':')[5]);

        // once, before a match's models load: the sets go on as hunter models load (models already loaded in menus are
        // dropped so they reload with them)
        public static void Install()
        {
            lock (_lock)
            {
                if (_installed) return;
                _installed = true;
            }
            LookAlikes.InstallHook();
            foreach (string m in ModelHunter.Keys) Read.RemoveModel(m);
            Read.RemoveModel(TrailModel);
        }

        // the recolor index of the nth new colour (0-3 = the 5th-8th) on a hunter's model (or arcWelder1, the Lockjaw
        // trail); -1: not one of them. A model not loaded yet gets them after MPH's own (its metadata's count; the users
        // check the index against the loaded model)
        public static int RecolorFor(string model, int copy)
        {
            if (copy < 0 || copy >= Copies || !ModelHunter.ContainsKey(model) && model != TrailModel) return -1;
            lock (_lock)
            {
                if (_start.TryGetValue(model, out int s)) return s + copy;
            }
            return Metadata.GetModelByName(model) is ModelMetadata meta ? meta.Recolors.Count + copy : -1;
        }

        public const string TrailModel = "arcWelder1";

        // the model's new sets (none for other models)
        public static List<Recolor> Build(string name, IReadOnlyList<Recolor> recolors)
        {
            var list = new List<Recolor>();
            if (recolors.Count == 0) return list;
            if (name == TrailModel)
            {
                // the Lockjaw trail (arcWelder1: MPH's pal_02-04 + teams as recolors 0-4): MPH's white one (pal_04's)
                // coloured like the 5th-8th's bomb glow
                if (recolors.Count < 3) return list;
                for (int k = 0; k < Copies; k++) list.Add(Colourize(recolors[2], SyluxBombColor(k), $"hc_trail_{k}"));
            }
            else if (ModelHunter.TryGetValue(name, out Hunter h) && Specs.TryGetValue(h, out string[]? specs))
            {
                // Trace's gun: its purples straddle the two groups' split, so the two-way move speckled it (owner 2026-10-29
                // "mangled/rough"); it moves whole, every colour by the same step, as one piece of armour
                bool whole = h == Hunter.Trace && name == Metadata.HunterModels[h][3];
                for (int k = 0; k < Copies; k++)
                {
                    string[] f = specs[k].Split(':');
                    if (whole && f[0] == "two")
                    {
                        Recolor src = recolors[Math.Clamp(int.Parse(f[1], CultureInfo.InvariantCulture) - 1, 0, recolors.Count - 1)];
                        list.Add(Whole(src, F(f[2]), f[3], F(f[4]), f[5], $"hc_{k}"));
                        continue;
                    }
                    list.Add(f[0] == "two"
                        ? Two(recolors[Math.Clamp(int.Parse(f[1], CultureInfo.InvariantCulture) - 1, 0, recolors.Count - 1)],
                            F(f[2]), f[3], F(f[4]), f[5], $"hc_{k}")
                        : Spire(recolors[0], f[0], F(f[1]), name == Metadata.HunterModels[h][3], $"hc_{k}"));
                }
            }
            else
            {
                return list;
            }
            lock (_lock) _start[name] = recolors.Count;
            return list;
        }

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        static Recolor Two(Recolor src, float a, string ta, float b, string tb, string name) => Map(src, c =>
        {
            (float h, float s, float v) = Hsv(c);
            if (s < 0.2f) return c; // greys and blacks stay
            bool isA = HueDist(h, a) <= HueDist(h, b);
            float peak = isA ? a : b;
            string t = isA ? ta : tb;
            if (t == "w")
            {
                s *= 0.12f;
                v = MathF.Min(1, 0.30f + 0.75f * v);
            }
            else if (t == "k")
            {
                s *= 0.30f;
                v *= 0.35f;
            }
            else
            {
                string[] tm = t.Split('*');
                h = (h + F(tm[0]) - peak + 720) % 360;
                if (tm.Length > 1) v = MathF.Min(1, v * F(tm[1]));
            }
            return Rgb(h, s, v, c);
        }, name);

        // one move for every colour (the greys too), the group most of the palette's colours fall in deciding it: no
        // neighbouring texels sent to different colours
        static Recolor Whole(Recolor src, float a, string ta, float b, string tb, string name)
        {
            int na = 0, nb = 0;
            foreach (IReadOnlyList<PaletteData> pal in src.PaletteData)
            {
                foreach (PaletteData p in pal)
                {
                    (float h, float s, _) = Hsv(p.Data);
                    if (s < 0.2f) continue;
                    if (HueDist(h, a) <= HueDist(h, b)) na++;
                    else nb++;
                }
            }
            (float peak, string t) = na >= nb ? (a, ta) : (b, tb);
            string[] tm = t.Split('*');
            return Map(src, c =>
            {
                (float h, float s, float v) = Hsv(c);
                if (t == "w")
                {
                    s *= 0.12f;
                    v = MathF.Min(1, 0.30f + 0.75f * v);
                }
                else if (t == "k")
                {
                    s *= 0.30f;
                    v *= 0.35f;
                }
                else
                {
                    h = (h + F(tm[0]) - peak + 720) % 360;
                    if (tm.Length > 1) v = MathF.Min(1, v * F(tm[1]));
                }
                return Rgb(h, s, v, c);
            }, name);
        }

        // Spire, after MPH's own 2nd-4th (owner 2026-10-29: the stone changes too, subtly; only moving its bright specks
        // speckled it): a rock palette (16 colours) takes the new hue in its shadows, its highlights staying warm stone,
        // and the gun's rock turns whole like MPH's guns do; the crystals turn to the hue; the big body sheet (moss, crystal,
        // orange patch, silver) turns its orange and crystal and greys its moss, as MPH's 2nd-4th do
        static Recolor Spire(Recolor src, string mode, float target, bool gun, string name)
        {
            var palettes = new List<IReadOnlyList<PaletteData>>(src.PaletteData.Count);
            foreach (IReadOnlyList<PaletteData> pal in src.PaletteData)
            {
                float ms = 0;
                foreach (PaletteData e in pal) ms += Hsv(e.Data).Item2;
                ms /= Math.Max(1, pal.Count);
                Func<ushort, ushort> f = pal.Count > 16 ? c => Sheet(c, mode, target)
                    : ms > 0.7f ? c => Lights(c, mode, target)
                    : c => Rock(c, mode, target, gun);
                var p = new PaletteData[pal.Count];
                for (int i = 0; i < pal.Count; i++) p[i] = new PaletteData(f(pal[i].Data));
                palettes.Add(p);
            }
            return new Recolor(name, src.Textures, src.Palettes, src.TextureData, palettes);
        }

        static float Toward(float h, float target, float w)
        {
            float d = (target - h + 540) % 360 - 180;
            return (h + d * w + 360) % 360;
        }

        static ushort Rock(ushort c, string mode, float target, bool gun)
        {
            (float h, float s, float v) = Hsv(c);
            if (s < 0.05f) return c;
            // the red gun goes deep crimson: a plain red one matched MPH's 3rd (salmon red) at a glance
            bool red = target < 15 || target > 345;
            if (gun) return mode == "white" ? Rgb(h, s * 0.25f, MathF.Min(1, v * 1.1f), c)
                : Rgb(red ? 345 : target, MathF.Min(1, s * 1.3f), red ? v * 0.75f : v, c);
            // shadows mostly, highlights a little (MPH's 3rd: shadows magenta, highlights still warm), blended in RGB so the
            // in-between tones don't pass through other hues (brown to blue by hue went purple, in blotches)
            float sh = 1 - Math.Clamp((v - 0.3f) / 0.3f, 0, 1);
            float w = mode == "white" ? 0.5f + 0.35f * sh : 0.45f + 0.5f * sh;
            ushort t = mode == "white" ? Rgb(h, 0.04f, MathF.Min(1, v * 1.1f), c) : Rgb(target, s, v, c);
            static float Ch(ushort u, int k) => ((u >> (5 * k)) & 0x1F) / 31f;
            return Pack(Ch(c, 0) + (Ch(t, 0) - Ch(c, 0)) * w, Ch(c, 1) + (Ch(t, 1) - Ch(c, 1)) * w,
                Ch(c, 2) + (Ch(t, 2) - Ch(c, 2)) * w, c);
        }

        static ushort Sheet(ushort c, string mode, float target)
        {
            (float h, float s, float v) = Hsv(c);
            if (h >= 40 && h <= 170 && v > 0.15f)
                return Rgb(mode == "white" ? h : Toward(h, target, 0.5f), s * (mode == "white" ? 0.2f : 0.35f), v, c);
            return Lights(c, mode, target);
        }

        // Spire's crystals and the sheet's orange: the saturated bright parts to hue T ("lights") or near-white tinted T ("white")
        static ushort Lights(ushort c, string mode, float target)
        {
            (float h, float s, float v) = Hsv(c);
            if (s <= 0.45f || v <= 0.40f) return c;
            h = target;
            if (mode == "white")
            {
                s *= 0.18f;
                v = MathF.Min(1, v * 1.15f);
            }
            return Rgb(h, s, v, c);
        }

        // a white glow coloured: its brightness in the colour, the hottest part staying white
        static Recolor Colourize(Recolor src, (float R, float G, float B) col, string name) => Map(src, c =>
        {
            float r = (c & 0x1F) / 31f, g = ((c >> 5) & 0x1F) / 31f, b = ((c >> 10) & 0x1F) / 31f;
            float v = MathF.Max(r, MathF.Max(g, b));
            float hot = Math.Clamp((v - 0.8f) / 0.2f, 0, 1) * 0.5f;
            return Pack(col.R * v + (v - col.R * v) * hot, col.G * v + (v - col.G * v) * hot, col.B * v + (v - col.B * v) * hot, c);
        }, name);

        // a spec's colour target as RGB (hue at full colour, w white, k near-black)
        static (float R, float G, float B) Target(string t)
        {
            if (t == "w") return (1f, 1f, 1f);
            if (t == "k") return (0.25f, 0.25f, 0.28f);
            string[] tm = t.Split('*');
            ushort c = Rgb(F(tm[0]) % 360, 0.85f, 1f, 0);
            return ((c & 0x1F) / 31f, ((c >> 5) & 0x1F) / 31f, ((c >> 10) & 0x1F) / 31f);
        }

        static Recolor Map(Recolor src, Func<ushort, ushort> map, string name)
        {
            var palettes = new List<IReadOnlyList<PaletteData>>(src.PaletteData.Count);
            foreach (IReadOnlyList<PaletteData> pal in src.PaletteData)
            {
                var p = new PaletteData[pal.Count];
                for (int i = 0; i < pal.Count; i++) p[i] = new PaletteData(map(pal[i].Data));
                palettes.Add(p);
            }
            var textures = new List<IReadOnlyList<TextureData>>(src.TextureData.Count);
            for (int t = 0; t < src.TextureData.Count; t++)
            {
                IReadOnlyList<TextureData> data = src.TextureData[t];
                if (src.Textures[t].Format != TextureFormat.DirectRgb)
                {
                    textures.Add(data);
                    continue;
                }
                var d = new TextureData[data.Count];
                for (int i = 0; i < data.Count; i++) d[i] = new TextureData(map((ushort)data[i].Data), data[i].Alpha);
                textures.Add(d);
            }
            return new Recolor(name, src.Textures, src.Palettes, textures, palettes);
        }

        static float HueDist(float a, float b)
        {
            float d = MathF.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }

        static (float, float, float) Hsv(ushort c)
        {
            float r = (c & 0x1F) / 31f, g = ((c >> 5) & 0x1F) / 31f, b = ((c >> 10) & 0x1F) / 31f;
            float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
            float d = max - min, h = 0;
            if (d > 0)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * ((b - r) / d + 2);
                else h = 60 * ((r - g) / d + 4);
            }
            if (h < 0) h += 360;
            return (h, max > 0 ? d / max : 0, max);
        }

        static ushort Rgb(float h, float s, float v, ushort orig)
        {
            float c = v * s, x = c * (1 - MathF.Abs(h / 60 % 2 - 1)), m = v - c;
            (float r, float g, float b) = h < 60 ? (c, x, 0f) : h < 120 ? (x, c, 0f) : h < 180 ? (0f, c, x)
                : h < 240 ? (0f, x, c) : h < 300 ? (x, 0f, c) : (c, 0f, x);
            return Pack(r + m, g + m, b + m, orig);
        }

        static ushort Pack(float r, float g, float b, ushort orig)
        {
            int R = Math.Clamp((int)MathF.Round(r * 31), 0, 31);
            int G = Math.Clamp((int)MathF.Round(g * 31), 0, 31);
            int B = Math.Clamp((int)MathF.Round(b * 31), 0, 31);
            return (ushort)(R | G << 5 | B << 10 | (orig & 0x8000));
        }
    }
}
