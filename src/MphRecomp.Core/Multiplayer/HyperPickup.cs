using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MphRead;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer
{
    // The Hyper Beam's pickup (item 24, model "pick_wpn_hyper"), made the Pirate Blaster's way (BlasterPickup.cs): a copy
    // of one of the user's own weapon pickups, its backdrop unchanged, its glows in the Hyper Beam's blue 33,132,255
    // (HyperBeam.cs, its HUD tile) and its emblem's shape replaced by the visor of the Hyper Beam's menu icon (owner
    // 2026-11-16 picked Y1: PlayerEntity._hyperIconMask), flat, at the emblem's own depth and in its own colour. No
    // game data in the code: every quad, texture and material comes from the user's ROM at load (Read.HostBuildModel).
    // Drawing only: the item, its pickup and the sim are MPH's.
    // Item 24's model (Metadata.Items): the owner picked look E, the Magmaul's kit (2026-11-16).
    public static class HyperPickup
    {
        public const string ModelName = "pick_wpn_hyper";

        public const Look Current = Look.E;

        // the looks (mockups for the owner's pick), the blaster's set: the copy of A = the Judicator's pickup, B = the
        // Imperialist's, C = the Battlehammer's, D = the Volt Driver's, E = the Magmaul's, F = the Shock Coil's, G = the
        // affinity pickup's
        public enum Look { A, B, C, D, E, F, G }

        static string KitOf(Look look) => look switch
        {
            Look.A => "pick_wpn_shotgun",
            Look.B => "pick_wpn_snipergun",
            Look.C => "pick_wpn_jackhammer",
            Look.D => "pick_wpn_electro",
            Look.E => "pick_wpn_mortar",
            Look.F => "pick_wpn_ghostbuster",
            _ => "pick_wpn_all",
        };

#pragma warning disable CA2255 // registers the model with MphRead's loader before any scene loads; no other caller needed
        [ModuleInitializer]
        internal static void Init()
        {
            Func<string, Model?>? previous = Read.HostBuildModel;
            Read.HostBuildModel = name => name == ModelName ? Build(Current)
                : name.StartsWith(ModelName + "_", StringComparison.Ordinal)
                && Enum.TryParse(name[(ModelName.Length + 1)..], out Look look) ? Build(look) : previous?.Invoke(name);
            // dev pictures (never in a public build): MPH_DEV_HYPER_LINEUP=1 draws each Hyper Beam pickup in its own look,
            // A to G by the order they're first made (a placement file's order), the same look again where one respawns;
            // MPH_DEV_HYPER_PICKUP=A..G draws them all in one look
            bool lineup = Environment.GetEnvironmentVariable("MPH_DEV_HYPER_LINEUP") == "1";
            bool one = Enum.TryParse(Environment.GetEnvironmentVariable("MPH_DEV_HYPER_PICKUP"), true, out Look single);
            if (lineup || one)
            {
                Func<ItemType, Vector3, string?>? previousItem = MphRead.Entities.ItemInstanceEntity.HostItemModel;
                MphRead.Entities.ItemInstanceEntity.HostItemModel = (type, pos) => type == ItemType.HyperBeam
                    && !MphRecomp.Config.RecompSettings.PublicBuild ? (lineup ? LineupModel(pos) : $"{ModelName}_{single}")
                    : previousItem?.Invoke(type, pos);
            }
        }

        static readonly List<Vector3> _lineup = new();

        static string LineupModel(Vector3 pos)
        {
            int index = _lineup.FindIndex(p => (p - pos).LengthSquared < 0.01f);
            if (index < 0)
            {
                index = _lineup.Count;
                _lineup.Add(pos);
            }
            var looks = Enum.GetValues<Look>();
            return $"{ModelName}_{looks[index % looks.Length]}";
        }
#pragma warning restore CA2255

        // the Hyper Beam's blue (its beam and HUD tile)
        static (int R, int G, int B) Glow = (33, 132, 255);

        // model units per icon pixel (the visor's wings reach ~0.28 from the middle, as the vanilla emblems)
        const float Unit = 0.019f;

        // null: the kit isn't the shape this expects (MphRead then loads plain pick_wpn_all)
        static Model? Build(Look look)
        {
            // dev pictures: MPH_DEV_HYPER_GLOW=r,g,b tries another glow colour
            string[]? rgb = MphRecomp.Config.RecompSettings.PublicBuild ? null : Environment.GetEnvironmentVariable("MPH_DEV_HYPER_GLOW")?.Split(',');
            if (rgb is { Length: 3 } && int.TryParse(rgb[0], out int r) && int.TryParse(rgb[1], out int g) && int.TryParse(rgb[2], out int b))
            {
                Glow = (r, g, b);
            }
            ModelMetadata? meta = Metadata.GetModelByName(KitOf(look));
            if (meta == null) return null;
            Model model = Read.ReadModelFile(ModelName, meta.ModelPath, null);
            if (model.RenderInstructionLists is not List<IReadOnlyList<RenderInstruction>> lists) return null;
            // the kit: one lit mesh (the emblem), the backdrop disc (material "...pick_wpn_m", left as it is), the rest glows
            int emblem = -1;
            var glows = new List<Mesh>();
            var keptPalettes = new HashSet<int>();
            for (int i = 0; i < model.Meshes.Count; i++)
            {
                Mesh mesh = model.Meshes[i];
                Material m = model.Materials[mesh.MaterialId];
                if (m.Lighting != 0)
                {
                    if (emblem >= 0) return null;
                    emblem = i;
                    keptPalettes.Add(m.PaletteId);
                }
                else if (!m.Name.EndsWith("pick_wpn_m", StringComparison.Ordinal))
                {
                    glows.Add(mesh);
                }
                else
                {
                    keptPalettes.Add(m.PaletteId);
                }
            }
            // the glows modulate a texture by their colour. Most kits' glow textures are white, so the colour goes blue;
            // the Imperialist's, Battlehammer's and Magmaul's are yellow and orange (x blue = green), so theirs turn into
            // blues instead (ToHyperHue), and the colour goes white. The Magmaul keeps its two layers (orange middle in a
            // yellow halo) as a deep blue in a lighter blue; the other two get squeezed into the blues, since their thin
            // see-through hazes go green over the arenas when any of them reaches cyan
            var palettes = model.Recolors[0].PaletteData as List<IReadOnlyList<PaletteData>>;
            var tinted = new HashSet<int>();
            foreach (Mesh mesh in glows)
            {
                int p = model.Materials[mesh.MaterialId].PaletteId;
                bool tint = palettes != null && p >= 0 && p < palettes.Count && !keptPalettes.Contains(p)
                    && Tinted(palettes[p]);
                if (tint && tinted.Add(p))
                {
                    palettes![p] = palettes[p].Select(c => new PaletteData(ToHyperHue(c.Data, 0.4f, look == Look.E))).ToList();
                }
                uint glow = tint ? Rgb15((255, 255, 255)) : Rgb15(Glow);
                lists[mesh.DlistId] = lists[mesh.DlistId].Select(ins => ins.Code == InstructionCode.COLOR
                    ? new RenderInstruction(InstructionCode.COLOR, glow) : ins).ToList();
            }
            if (emblem < 0) return null;
            Mesh em = model.Meshes[emblem];
            Material material = model.Materials[em.MaterialId];
            if (material.TextureId < 0) return null;
            Texture texture = model.Recolors[0].Textures[material.TextureId];
            // the emblem's own depth (its front) and colour command (DIF_AMB or COLOR, if it has one)
            float[] v = MphRecomp.Render.DsDisplayList.Decode(model, em, isRoom: false);
            float faceZ = Single.MinValue;
            for (int k = 2; k < v.Length; k += MphRecomp.Render.DsDisplayList.Stride) faceZ = MathF.Max(faceZ, v[k]);
            if (faceZ == Single.MinValue) return null;
            RenderInstruction? color = lists[em.DlistId].FirstOrDefault(ins => ins.Code is InstructionCode.DIF_AMB or InstructionCode.COLOR);
            lists[em.DlistId] = Emblem(faceZ, color, texture.Width, texture.Height);
            return model;
        }

        // a coloured palette, not a white or grey one: these glows' textures are one colour (their shape is in the alpha)
        // with the rest of the palette white filler, so one strong colour is enough
        static bool Tinted(IReadOnlyList<PaletteData> palette) => palette.Any(c => Hsv(c.Data) is { S: > 0.5f, V: > 0.3f });

        static (float H, float S, float V) Hsv(ushort rgb15)
        {
            float r = (rgb15 & 31) / 31f, g = (rgb15 >> 5 & 31) / 31f, b = (rgb15 >> 10 & 31) / 31f;
            float max = MathF.Max(r, MathF.Max(g, b)), d = max - MathF.Min(r, MathF.Min(g, b));
            float h = d == 0 ? 0 : max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            return ((h * 60 + 360) % 360, max == 0 ? 0 : d / max, max);
        }

        // a warm colour to the Hyper Beam's blues, brightness kept. `lighter` (the Magmaul): the kits' orange (vanilla
        // 247,99,33, hue 18.5) goes to a deep blue (226) and the yellow (hue 58) to a lighter, paler blue (220, half the
        // saturation), so the two layers read as blue and light blue, never cyan (owner 11-16). Otherwise the hue is
        // mirrored round the orange onto 228 with `spread` pulling it towards the blue, saturation kept
        static ushort ToHyperHue(ushort rgb15, float spread, bool lighter)
        {
            (float h, float s, float v) = Hsv(rgb15);
            float warm = Math.Clamp(h > 180 ? h - 360 : h, -30, 90); // reds just under 360 sit just under orange
            float yellow = Math.Clamp((warm - 18.5f) / 39.5f, 0, 1);
            if (lighter) s *= 1 - 0.55f * yellow;
            float hue = (lighter ? 226 - 6 * yellow : (228 - (warm - 18.5f) * spread + 360) % 360) / 60;
            float c = v * s, x = c * (1 - MathF.Abs(hue % 2 - 1)), m = v - c;
            (float r, float g, float b) = (int)hue switch
            {
                0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x), 3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x),
            };
            static int Ch(float f) => Math.Clamp((int)MathF.Round(f * 31), 0, 31);
            return (ushort)(Ch(r + m) | Ch(g + m) << 5 | Ch(b + m) << 10 | rgb15 & 0x8000);
        }

        static uint Rgb15((int R, int G, int B) c) => (uint)(c.R * 31 / 255 | c.G * 31 / 255 << 5 | c.B * 31 / 255 << 10);

        // the visor's right half as the owner's Y1 was drawn (extract_out/dark_samus_hyper/hyper_icons5.py TRACE: half-width
        // and y in the reference's pixels, wing tip first, down to the stem's point), mirrored for the left; the top's dip
        // in the middle at y 52
        static readonly (float HalfWidth, float Y)[] _visorTrace =
        {
            (80, 27), (75.5f, 46), (65.5f, 54), (54.5f, 62), (42, 70), (31.5f, 78), (25, 84), (21.5f, 89), (20, 94),
            (18.5f, 102), (16, 114), (13.5f, 126), (10.5f, 138), (8, 146), (4.5f, 150.5f), (0, 154),
        };
        const float VisorDip = 52;

        // the visor in the icon's 32 grid (y down, its middle at 16, 16) as the icon masks were drawn from it (scale 0.19,
        // top at 4): left wing tip, the dip, the right side down to the point, then the left side back up
        static List<(float X, float Y)> VisorOutline()
        {
            const float scale = 0.19f, top = 4;
            var right = _visorTrace.Select(p => (X: 16 + p.HalfWidth * scale, Y: (p.Y - 27) * scale + top)).ToList();
            var outline = new List<(float X, float Y)> { (32 - right[0].X, right[0].Y), (16, (VisorDip - 27) * scale + top) };
            outline.AddRange(right);
            for (int i = right.Count - 2; i >= 1; i--) outline.Add((32 - right[i].X, right[i].Y));
            return outline;
        }

        // the visor as flat triangles facing +z (the camera side), cut from its traced outline (not the icon's pixels, so
        // its edges run smooth) by ear clipping. Texcoords a flat projection as the affinity emblem's
        static List<RenderInstruction> Emblem(float faceZ, RenderInstruction? color, int texWidth, int texHeight)
        {
            // model units, y up: one icon pixel = Unit, the icon's middle at the origin
            List<(float X, float Y)> poly = VisorOutline().Select(p => ((p.X - 16) * Unit, (16 - p.Y) * Unit)).ToList();
            float area = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                (float ax, float ay) = poly[i];
                (float bx, float by) = poly[(i + 1) % poly.Count];
                area += ax * by - bx * ay;
            }
            if (area < 0) poly.Reverse(); // counter-clockwise from +z, or it's back-face culled
            static float Cross((float X, float Y) a, (float X, float Y) b, (float X, float Y) c)
                => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            var list = new List<RenderInstruction> { new(InstructionCode.MTX_RESTORE, 0) };
            if (color != null) list.Add(color);
            list.Add(new(InstructionCode.NORMAL, 511u << 20)); // (0, 0, 1)
            list.Add(new(InstructionCode.BEGIN_VTXS, 0)); // separate triangles
            var left = Enumerable.Range(0, poly.Count).ToList();
            while (left.Count > 2)
            {
                int ear = -1;
                for (int i = 0; i < left.Count && ear < 0; i++)
                {
                    (float X, float Y) a = poly[left[(i + left.Count - 1) % left.Count]], b = poly[left[i]], c = poly[left[(i + 1) % left.Count]];
                    if (Cross(a, b, c) <= 0) continue; // a reflex corner (or a straight one)
                    bool inside = false;
                    foreach (int k in left)
                    {
                        (float X, float Y) p = poly[k];
                        if (p == a || p == b || p == c) continue;
                        if (Cross(a, b, p) >= 0 && Cross(b, c, p) >= 0 && Cross(c, a, p) >= 0)
                        {
                            inside = true;
                            break;
                        }
                    }
                    if (!inside) ear = i;
                }
                if (ear < 0) ear = 0; // only straight corners left: dropping one draws nothing
                (float X, float Y) pa = poly[left[(ear + left.Count - 1) % left.Count]], pb = poly[left[ear]], pc = poly[left[(ear + 1) % left.Count]];
                if (Cross(pa, pb, pc) > 0)
                {
                    foreach ((float mx, float my) in new[] { pa, pb, pc })
                    {
                        list.Add(new(InstructionCode.TEXCOORD, TexCoord(mx, my, texWidth, texHeight)));
                        list.Add(new(InstructionCode.VTX_16, Fx(mx) | Fx(my) << 16, Fx(faceZ)));
                    }
                }
                left.RemoveAt(ear);
            }
            list.Add(new(InstructionCode.END_VTXS));
            return list;
        }

        static uint Fx(float v) => (uint)(int)MathF.Round(v * 4096) & 0xFFFF;

        static uint TexCoord(float x, float y, int w, int h)
        {
            float u = 0.5f - x * 1.45f, v = 0.5f + y * 1.07f; // the affinity emblem's mapping
            return ((uint)(int)MathF.Round(u * w * 16) & 0xFFFF) | ((uint)(int)MathF.Round(v * h * 16) & 0xFFFF) << 16;
        }
    }
}
