using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MphRead;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer
{
    // The Pirate Blaster's pickup (item 23, model "pick_wpn_blaster"; board row "Pirate Blaster pickup art"). MPH's weapon
    // pickups are one kit: a dark backdrop disc, glow quads in the weapon's colour (vertex colours) and a flat emblem of
    // the weapon's symbol, all facing the camera. This one is a copy of one of the user's own weapon pickups (owner: "copy
    // and recolor one of the existing vanilla beams and then draw the three marks"): its backdrop and glows unchanged but
    // in the blaster's 255,99,33 (its HUD tile), and its emblem's shape replaced by the blaster's three marks (the shape of
    // PlayerPirateBlaster's menu icon), flat, at the emblem's own depth and in its own colour. No game data in the code:
    // every quad, texture and material comes from the user's ROM at load (Read.HostBuildModel). Drawing only: the item,
    // its pickup and the sim are MPH's.
    public static class BlasterPickup
    {
        public const string ModelName = "pick_wpn_blaster";

        // the looks (mockups for the owner's pick): the copy of A = the Judicator's pickup, B = the Imperialist's, C = the
        // Battlehammer's, D = the Volt Driver's, E = the Magmaul's, F = the Shock Coil's, G = the affinity pickup's
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

        public static Look Current { get; set; } = Look.A;

#pragma warning disable CA2255 // registers the model with MphRead's loader before any scene loads; no other caller needed
        [ModuleInitializer]
        internal static void Init()
        {
            Func<string, Model?>? previous = Read.HostBuildModel;
            Read.HostBuildModel = name => name == ModelName ? Build()
                : name.StartsWith(ModelName + "_", StringComparison.Ordinal) && Enum.TryParse(name[(ModelName.Length + 1)..], out Look look) ? Build(look)
                : previous?.Invoke(name);
            // dev pictures: MPH_DEV_BLASTER_LINEUP=1 draws each Pirate Blaster pickup in its own look, A to G by the order
            // they're first made (a placement file's order), the same look again where one respawns (never in a public build)
            if (Environment.GetEnvironmentVariable("MPH_DEV_BLASTER_LINEUP") == "1")
            {
                Func<ItemType, Vector3, string?>? previousItem = MphRead.Entities.ItemInstanceEntity.HostItemModel;
                MphRead.Entities.ItemInstanceEntity.HostItemModel = (type, pos) => type == ItemType.PirateBlaster
                    && !MphRecomp.Config.RecompSettings.PublicBuild ? LineupModel(pos) : previousItem?.Invoke(type, pos);
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

        // the blaster's colour (its beam and HUD tile)
        static (int R, int G, int B) Glow = (255, 99, 33);

        // the three bolts in the 32-pixel icon's grid (x right, y down; PlayerPirateBlaster._blasterIconMask), each a
        // slanted stroke from its tail (lower left) to its flat head (upper right)
        static readonly (float X, float Y)[][] Bolts =
        {
            new[] { (10f, 4f), (15f, 3f), (15f, 7f), (5f, 16f), (3f, 16f), (3f, 13f) },
            new[] { (20f, 7f), (23f, 6f), (26f, 6f), (25f, 12f), (15f, 19f), (13f, 18f) },
            new[] { (24f, 18f), (26f, 17f), (30f, 17f), (29f, 22f), (20f, 29f), (17f, 29f) },
        };

        // model units per icon pixel (the marks reach ~0.30 from the middle, as the vanilla emblems)
        const float Unit = 0.022f;

        // the marks' middle: their area centroid (the icon's box centre sits off it, toward the big upper right mark)
        static readonly (float X, float Y) Centre = AreaCentroid();

        static (float X, float Y) AreaCentroid()
        {
            float area = 0, cx = 0, cy = 0;
            foreach ((float X, float Y)[] bolt in Bolts)
            {
                for (int i = 0; i < bolt.Length; i++)
                {
                    (float x0, float y0) = bolt[i];
                    (float x1, float y1) = bolt[(i + 1) % bolt.Length];
                    float c = x0 * y1 - x1 * y0;
                    area += c;
                    cx += (x0 + x1) * c;
                    cy += (y0 + y1) * c;
                }
            }
            return (cx / (3 * area), cy / (3 * area));
        }


        // null: the kit isn't the shape this expects (MphRead then loads plain pick_wpn_all)
        public static Model? Build()
        {
            // dev pictures: MPH_DEV_BLASTER_PICKUP=A..D picks a look (never in a public build)
            if (!MphRecomp.Config.RecompSettings.PublicBuild
                && Enum.TryParse(Environment.GetEnvironmentVariable("MPH_DEV_BLASTER_PICKUP"), true, out Look look))
            {
                Current = look;
            }
            // dev pictures: MPH_DEV_BLASTER_GLOW=r,g,b tries another glow colour
            string[]? rgb = MphRecomp.Config.RecompSettings.PublicBuild ? null : Environment.GetEnvironmentVariable("MPH_DEV_BLASTER_GLOW")?.Split(',');
            if (rgb is { Length: 3 } && int.TryParse(rgb[0], out int r) && int.TryParse(rgb[1], out int g) && int.TryParse(rgb[2], out int b))
            {
                Glow = (r, g, b);
            }
            return Build(Current);
        }

        static Model? Build(Look look)
        {
            ModelMetadata? meta = Metadata.GetModelByName(KitOf(look));
            if (meta == null) return null;
            Model model = Read.ReadModelFile(ModelName, meta.ModelPath, null);
            if (model.RenderInstructionLists is not List<IReadOnlyList<RenderInstruction>> lists) return null;
            // the kit: one lit mesh (the emblem), the backdrop disc (material "...pick_wpn_m", left as it is), the rest glows,
            // all in the blaster's colour (white layers too: the Volt Driver's streaks take its glow's colour on screen)
            int emblem = -1;
            for (int i = 0; i < model.Meshes.Count; i++)
            {
                Mesh mesh = model.Meshes[i];
                Material m = model.Materials[mesh.MaterialId];
                if (m.Lighting != 0)
                {
                    if (emblem >= 0) return null;
                    emblem = i;
                }
                else if (!m.Name.EndsWith("pick_wpn_m", StringComparison.Ordinal))
                {
                    Recolor(lists, mesh.DlistId, Glow);
                }
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

        static uint Rgb15((int R, int G, int B) c) => (uint)(c.R * 31 / 255 | c.G * 31 / 255 << 5 | c.B * 31 / 255 << 10);

        // every COLOR command of the display list in this colour
        static void Recolor(List<IReadOnlyList<RenderInstruction>> lists, int dlist, (int R, int G, int B) color)
        {
            lists[dlist] = lists[dlist].Select(ins => ins.Code == InstructionCode.COLOR
                ? new RenderInstruction(InstructionCode.COLOR, Rgb15(color)) : ins).ToList();
        }

        // the marks as flat triangles facing +z (the camera side), each a fan around its middle; texcoords a flat
        // projection as the affinity emblem's
        static List<RenderInstruction> Emblem(float faceZ, RenderInstruction? color, int texWidth, int texHeight)
        {
            var list = new List<RenderInstruction> { new(InstructionCode.MTX_RESTORE, 0) };
            if (color != null) list.Add(color);
            list.Add(new(InstructionCode.NORMAL, 511u << 20)); // (0, 0, 1)
            list.Add(new(InstructionCode.BEGIN_VTXS, 0)); // separate triangles
            foreach ((float X, float Y)[] bolt in Bolts)
            {
                var ring = bolt.Select(p => ((p.X - Centre.X) * Unit, (Centre.Y - p.Y) * Unit)).ToArray();
                float mx = ring.Average(p => p.Item1), my = ring.Average(p => p.Item2);
                for (int i = 0; i < ring.Length; i++)
                {
                    (float ax, float ay) = ring[i];
                    (float bx, float by) = ring[(i + 1) % ring.Length];
                    // counter-clockwise seen from +z
                    bool ccw = (ax - mx) * (by - my) - (ay - my) * (bx - mx) > 0;
                    var tri = ccw
                        ? new[] { (mx, my, faceZ), (ax, ay, faceZ), (bx, by, faceZ) }
                        : new[] { (mx, my, faceZ), (bx, by, faceZ), (ax, ay, faceZ) };
                    foreach ((float x, float y, float z) in tri)
                    {
                        list.Add(new(InstructionCode.TEXCOORD, TexCoord(x, y, texWidth, texHeight)));
                        list.Add(new(InstructionCode.VTX_16, Fx(x) | Fx(y) << 16, Fx(z)));
                    }
                }
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
