using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRecomp.Multiplayer;

namespace MphRecomp.Frontend
{
    // a seat's round SELECT HUNTER icon on ONLINE PLAY's lobby and the REPLAYS pages (owner 2026-10-10: TROOPER showed
    // Weavel's icon): a hunter's own ROM icon; TROOPER / DARK SAMUS: their skin's icon (MultiplayerMenu.SkinIcon) where
    // this device has its picture, else the base hunter's ROM icon inside its rim in the look-alike's colours (LookAlikes,
    // D5: the same colours that device draws the seat's body in; made from MPH's own icon, nothing travels between players)
    public sealed partial class RecompMenus
    {
        // (false: nothing drawn, the ROM icon missing)
        private bool SeatIcon(MatchPlayer p, float x, float top, float d, int tag)
        {
            bool extra = p.Character != null || Characters.Of(p.Hunter, p.Skin) is { HasOriginal: false };
            if (extra && Multiplayer?.SkinIcon(_drawMenu!, p.Skin) is int skin and >= 0)
            {
                Quad(skin, x, top, x + d, top - d, 0, 0, 1, 1, 1, 1, 1, 1, tag);
                return true;
            }
            string model = @"characterselect\" + (p.Hunter == Hunter.Guardian ? "samus" : p.Hunter.ToString().ToLowerInvariant());
            LookAlikes.Look look = extra ? LookAlikes.For(p) : LookAlikes.Look.None;
            if (look != LookAlikes.Look.None && LookAlikeIcon(model, look) is int tex and >= 0)
            {
                Quad(tex, x, top, x + d, top - d, 0, 0, 1, 1, 1, 1, 1, 1, tag);
                return true;
            }
            if (Still(model).Count == 0) return false;
            Model(model, x, top, d, d, tag);
            return true;
        }

        // the ROM icon's piece of its texture (the model's UVs), the inside of the rim through the look's colour map
        private int LookAlikeIcon(string model, LookAlikes.Look look)
        {
            List<WidgetTri> tris = Still(model).Where(t => t.TextureId >= 0).ToList();
            if (tris.Count == 0) return -1;
            int id = tris[0].TextureId;
            var textures = _drawMenu!.Widgets.Textures;
            return textures.GetOrAdd($"online lookalike {model} {look}", () =>
            {
                UiTexture src = textures[id];
                IEnumerable<UiVertex> vs = tris.Where(t => t.TextureId == id).SelectMany(t => new[] { t.A, t.B, t.C });
                int x0 = (int)MathF.Round(vs.Min(v => v.U) * src.Width), x1 = (int)MathF.Round(vs.Max(v => v.U) * src.Width);
                int y0 = (int)MathF.Round(vs.Min(v => v.V) * src.Height), y1 = (int)MathF.Round(vs.Max(v => v.V) * src.Height);
                if (x1 - x0 < 2 || y1 - y0 < 2) (x0, y0, x1, y1) = (0, 0, src.Width, src.Height);
                (int w, int h, byte[] px) = Crop(src, x0, y0, x1, y1);
                // the rim's inside: r < 12.2 of a 32 px icon (the ROM's ring starts just past it), its orange edge pixels kept
                float inner = 12.2f / 32 * MathF.Min(w, h), edge = 10.5f / 32 * MathF.Min(w, h);
                // the troopers' near-black armour reads as a blot at icon size: the source a little brighter first
                float gain = look == LookAlikes.Look.DarkSamus ? 1 : 1.45f;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float dx = x + 0.5f - w / 2f, dy = y + 0.5f - h / 2f;
                        float r2 = dx * dx + dy * dy;
                        if (r2 >= inner * inner) continue;
                        int o = (y * w + x) * 4;
                        if (r2 >= edge * edge && px[o] > 150 && px[o] > px[o + 1] * 1.6f && px[o + 2] < 90) continue;
                        int C(int v) => Math.Min(31, (int)(v * gain * 31 / 255));
                        ushort c = (ushort)(C(px[o]) | C(px[o + 1]) << 5 | C(px[o + 2]) << 10);
                        ushort m = LookAlikes.MapColor(c, look);
                        px[o] = (byte)((m & 0x1F) * 255 / 31);
                        px[o + 1] = (byte)((m >> 5 & 0x1F) * 255 / 31);
                        px[o + 2] = (byte)((m >> 10 & 0x1F) * 255 / 31);
                    }
                }
                return (w, h, px);
            }).Id;
        }
    }
}
