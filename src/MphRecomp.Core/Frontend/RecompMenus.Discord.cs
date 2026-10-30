using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using MphRecomp.Update;

// The Hunters Outpost Discord invite (owner 2026-10-21: "a faint transparent discord icon ... linking here"; then, for
// Discord's brand rules, "one of the icons they suggest ... at the bottom of the help section in the app tab in settings";
// picked A): a DISCORD row under HELP on the settings APP tab, its button our own menu button with Discord's white logo
// (symbol + wordmark) in it, as Discord's "Flexible" button style allows (our background; the logo #FFFFFF at full
// strength, never edited, recoloured or distorted). A press asks "join the hunters outpost discord?"; yes opens the invite
// in the Discord app if it's installed, else the browser (UpdateHost.OpenUrl, see DiscordInviteCode). The logo is drawn
// once in code from the path in Discord's own logo file (discord.com/branding, Discord-Logo-White.svg; no shipped asset).
// Hooks: AppSections + ButtonText + SectionRows (RecompMenus.SettingsPages.cs), the button draw (RecompMenus.Controls.cs).
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private const string DiscordLabel = "discord";
        private const string DiscordInvite = "https://discord.gg/WkvJ5ddjye";
        // the logo's height in menu units: 20 of the button's ~51 screen lines at 1080, the logo-to-button ratio of Discord's
        // own button in its SDK guide (owner 2026-10-22: 16 lines was "a little small")
        private const float DiscordLogoH = SecH * 20 / 51;

        private RowModel DiscordRow() => new()
        {
            Label = DiscordLabel,
            Description = "join the hunters outpost, second hunt's discord server.",
            Value = () => "OPEN",
            Step = _ => AskDiscord(),
            Action = true
        };

        // the logo centred in the row's button (drawn over its empty cell)
        private void DrawDiscordLogo(float x0, float top, float x1, float bottom, int tag)
        {
            UiTexture t = _drawMenu!.Widgets.Textures.GetOrAdd("discord logo", () => DiscordLogo.Draw(DiscordLogo.TexelsHigh));
            float h = DiscordLogoH * DiscordLogo.TexelsHigh / (DiscordLogo.TexelsHigh - 2), w = h * t.Width / t.Height;
            float cx = (x0 + x1) / 2, cy = (top + bottom) / 2;
            Quad(t.Id, cx - w / 2, cy + h / 2, cx + w / 2, cy - h / 2, 0, 0, 1, 1, 1, 1, 1, 1, tag);
        }

        private void AskDiscord()
        {
            if (Import.Popup == null) UpdateHost.OpenUrl?.Invoke(DiscordInvite);
            else Import.Popup.Ask("join the hunters outpost discord?", yes =>
            {
                if (yes) UpdateHost.OpenUrl?.Invoke(DiscordInvite);
            });
        }

        // the code of a Discord invite link (discord.gg/CODE, discord.com/invite/CODE), else null: each platform's
        // UpdateHost.OpenUrl takes these to the Discord app when it's installed (Android: the com.discord package; Windows:
        // its discord:// link), so it opens on the app's own invite screen with no browser step; else the browser
        public static string? DiscordInviteCode(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u) || u.Scheme != Uri.UriSchemeHttps) return null;
            string host = u.Host.ToLowerInvariant(), path = u.AbsolutePath.Trim('/');
            if (host != "discord.gg")
            {
                if ((host != "discord.com" && host != "www.discord.com") || !path.StartsWith("invite/", StringComparison.Ordinal))
                    return null;
                path = path.Substring("invite/".Length);
            }
            return path.Length > 0 && path.IndexOf('/') < 0 ? path : null;
        }
    }


    // Discord's logo, white on clear: the path filled (non-zero, as the SVG), supersampled, boxed down to texels. Straight
    // alpha, one clear texel round the logo. TexelsHigh = the logo at twice its 1080-line size (+ the clear rows), so
    // bigger screens stay sharp.
    internal static class DiscordLogo
    {
        // Discord-Logo-White.svg (discord.com/branding), view box 635.303 x 96
        private const string Path = "M81.15,0c-1.2376,2.1973-2.3489,4.4704-3.3591,6.794-9.5975-1.4396-19.3718-1.4396-28.9945,0-.985-2.3236-2.1216-4.5967-3.3591-6.794-9.0166,1.5407-17.8059,4.2431-26.1405,8.0568C2.779,32.5304-1.6914,56.3725.5312,79.8863c9.6732,7.1476,20.5083,12.603,32.0505,16.0884,2.6014-3.4854,4.8998-7.1981,6.8698-11.0623-3.738-1.3891-7.3497-3.1318-10.8098-5.1523.9092-.6567,1.7932-1.3386,2.6519-1.9953,20.281,9.547,43.7696,9.547,64.0758,0,.8587.7072,1.7427,1.3891,2.6519,1.9953-3.4601,2.0457-7.0718,3.7632-10.835,5.1776,1.97,3.8642,4.2683,7.5769,6.8698,11.0623,11.5419-3.4854,22.3769-8.9156,32.0509-16.0631,2.626-27.2771-4.496-50.9172-18.817-71.8548C98.9811,4.2684,90.1918,1.5659,81.1752.0505l-.0252-.0505ZM42.2802,65.4144c-6.2383,0-11.4159-5.6575-11.4159-12.6535s4.9755-12.6788,11.3907-12.6788,11.5169,5.708,11.4159,12.6788c-.101,6.9708-5.026,12.6535-11.3907,12.6535ZM84.3576,65.4144c-6.2637,0-11.3907-5.6575-11.3907-12.6535s4.9755-12.6788,11.3907-12.6788,11.4917,5.708,11.3906,12.6788c-.101,6.9708-5.026,12.6535-11.3906,12.6535ZM168.8397,13.3144h37.696c9.0857,0,16.7722,1.4176,23.0594,4.2529,5.7886,2.4336,10.7039,6.5642,14.098,11.8473,3.1773,5.219,4.8055,11.2338,4.6947,17.3429.0698,6.1372-1.6218,12.1656-4.8743,17.3705-3.6281,5.4984-8.8163,9.7874-14.8989,12.3168-6.6647,3.0378-14.9265,4.5521-24.7854,4.5428h-34.9896V13.3144ZM203.4426,63.769c6.1216,0,10.8255-1.5281,14.1118-4.5843,3.3712-3.2692,4.9505-7.7222,4.9295-12.5239.018-4.4454-1.3802-8.5919-4.3909-11.7368-2.9457-2.9273-7.3781-4.3956-13.2972-4.4048h-11.7921v33.2498h10.4389ZM304.8351,80.8772c-4.9731-1.2461-9.7304-3.2338-14.1118-5.896v-16.0449c3.8252,2.7822,8.1322,4.8327,12.7034,6.0479,5.0007,1.5258,10.1955,2.321,15.4236,2.3612,1.8044.0963,3.6072-.2152,5.2747-.9113,1.1875-.6076,1.7812-1.3808,1.7812-2.1817.0257-.9045-.3242-1.7792-.9666-2.4164-1.1029-.8483-2.3974-1.4125-3.7696-1.6432l-11.5987-2.6097c-6.6463-1.5465-11.364-3.6868-14.1532-6.4207-2.831-2.8554-4.3338-6.7677-4.1424-10.7841-.0466-3.6001,1.2648-7.0857,3.6729-9.7623,2.8551-3.0202,6.4556-5.2348,10.4389-6.4207,5.126-1.6112,10.4794-2.3806,15.8516-2.2783,5.015-.0471,10.0161.5374,14.885,1.7398,3.9328.9318,7.7095,2.4295,11.2121,4.4462v15.1888c-3.2847-1.9057-6.8158-3.3506-10.4941-4.2943-3.9853-1.0686-8.0941-1.6071-12.2201-1.6017-6.0571,0-9.0857,1.031-9.0857,3.093-.0206.9388.5235,1.7985,1.3808,2.1817,1.6549.7036,3.3889,1.2044,5.1642,1.4913l9.6656,1.7398c6.2781,1.1046,10.959,3.0378,14.0428,5.7994s4.6303,6.8028,4.6395,12.1235c.0999,5.6933-2.8276,11.0123-7.6911,13.9737-5.0905,3.4152-12.3582,5.1182-21.8029,5.109-5.4311.0084-10.8411-.6737-16.1002-2.0298ZM373.2677,78.8474c-5.1675-2.3744-9.5228-6.2159-12.5239-11.0464-2.7994-4.7371-4.2331-10.1566-4.1424-15.6583-.0834-5.4984,1.4273-10.9033,4.3495-15.5617,3.1147-4.7547,7.5487-8.4976,12.7586-10.7703,6.3346-2.7602,13.197-4.0987,20.1045-3.9215,9.6656,0,17.6881,2.0298,24.0674,6.0893v17.7157c-2.4269-1.6315-5.0782-2.9013-7.8706-3.7696-3.126-.9868-6.3877-1.4761-9.6656-1.4498-5.9927,0-10.6828,1.0954-14.0704,3.2863-4.7612,2.6573-6.4668,8.6712-3.8095,13.4324.8658,1.5513,2.1342,2.8403,3.6714,3.731,3.2863,2.2185,8.0547,3.3277,14.3051,3.3277,3.2263.0077,6.4362-.4575,9.5275-1.3808,2.8197-.8085,5.5283-1.9634,8.0639-3.4382v17.122c-7.4714,4.3561-16.0008,6.5637-24.6474,6.3793-6.9299.1891-13.8114-1.2089-20.1182-4.0872ZM441.949,78.8474c-5.2255-2.3861-9.6579-6.2195-12.7724-11.0464-2.9351-4.7259-4.4585-10.1922-4.3909-15.755-.0938-5.4969,1.4334-10.8997,4.3909-15.5341,3.131-4.7042,7.5442-8.413,12.7172-10.6874,12.7067-5.1553,26.9224-5.1553,39.6291,0,5.1526,2.2571,9.5478,5.9477,12.662,10.6322,2.9377,4.6515,4.4496,10.0611,4.3495,15.5617.0686,5.5572-1.4396,11.02-4.3495,15.755-3.0805,4.8302-7.4922,8.6665-12.7034,11.0464-12.6216,5.4128-26.9108,5.4128-39.5324,0v.0276ZM471.3187,61.6978c2.4137-2.4917,3.6925-5.8688,3.5349-9.3342.1688-3.4345-1.114-6.7817-3.5349-9.2238-2.6241-2.4065-6.1113-3.6469-9.6656-3.4382-3.5512-.1868-7.0301,1.0508-9.6656,3.4382-2.4114,2.4472-3.6883,5.7922-3.521,9.2238-.1564,3.4627,1.1166,6.8376,3.521,9.3342,2.6107,2.4366,6.0995,3.7075,9.6656,3.521,3.5693.2077,7.0662-1.0662,9.6656-3.521ZM556.6247,25.4655v20.9054c-2.8855-1.7219-6.2118-2.5619-9.569-2.4164-5.1504,0-9.1271,1.5741-11.8887,4.6947s-4.1424,7.9672-4.1424,14.5399v17.7848h-23.6808V24.4299h23.1975v17.9505c1.2887-6.5726,3.3692-11.4193,6.2412-14.5399,2.818-3.1056,6.9108-4.7373,11.0464-4.6947,3.0644-.0065,6.1462.7207,8.7958,2.3197ZM635.303,11.3813v69.5925h-23.6808v-12.7034c-1.7859,4.5225-4.9942,8.3415-9.1409,10.8807-4.5777,2.6081-9.7844,3.9073-15.0508,3.7558-4.9558.1154-9.834-1.2448-14.0152-3.9077-4.0491-2.6304-7.2746-6.3488-9.3066-10.7288-2.2171-4.8348-3.3213-10.1054-3.2311-15.4236-.156-5.514,1.0186-10.9844,3.4244-15.9483,2.2114-4.5467,5.6625-8.3759,9.9556-11.0464,4.4131-2.6736,9.4912-4.0472,14.6503-3.9629,11.2674,0,18.8387,4.8973,22.7142,14.6918V11.3813h23.6808ZM608.1425,61.3664c2.4403-2.4019,3.7583-5.7192,3.6315-9.1409.1037-3.3205-1.218-6.5267-3.6315-8.8095-5.5847-4.5468-13.5947-4.5468-19.1794,0-2.4067,2.3349-3.707,5.5831-3.5763,8.9338-.1227,3.3818,1.1916,6.6575,3.6177,9.0167,2.5628,2.3823,5.9772,3.6316,9.4723,3.4658,3.5576.1965,7.0435-1.0534,9.6656-3.4658ZM268.3542,9.2411c6.5202,0,11.8059,4.7478,11.8059,10.6046s-5.2857,10.6046-11.8059,10.6046-11.8059-4.7478-11.8059-10.6046,5.2857-10.6046,11.8059-10.6046ZM256.5345,37.7547c7.5551,3.1753,16.0705,3.1753,23.6256,0v43.5229h-23.6256v-43.5229Z";
        private const float ViewW = 635.303f, ViewH = 96;
        internal const int TexelsHigh = 40 + 2;
        private const int Ss = 4;

        // a texture `texels` high: the logo (texels - 2) high, one clear texel each side
        internal static (int Width, int Height, byte[] Rgba) Draw(int texels)
        {
            float logoH = texels - 2, scale = logoH * Ss / ViewH;
            int ow = (int)MathF.Ceiling(ViewW * logoH / ViewH) + 2, oh = texels;
            int w = ow * Ss, h = oh * Ss;
            float ox = (w - ViewW * scale) / 2, oy = (h - ViewH * scale) / 2;
            List<List<Vector2>> shapes = Flatten(Path);
            foreach (List<Vector2> s in shapes)
            {
                for (int i = 0; i < s.Count; i++) s[i] = new Vector2(s[i].X * scale + ox, s[i].Y * scale + oy);
            }
            float[] m = Coverage(shapes, w, h);
            byte[] rgba = new byte[ow * oh * 4];
            for (int y = 0; y < oh; y++)
            {
                for (int x = 0; x < ow; x++)
                {
                    float a = 0;
                    for (int v = 0; v < Ss; v++)
                    {
                        for (int u = 0; u < Ss; u++) a += m[(y * Ss + v) * w + x * Ss + u];
                    }
                    int o = (y * ow + x) * 4;
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = 255;
                    rgba[o + 3] = (byte)MathF.Round(Math.Clamp(a / (Ss * Ss), 0, 1) * 255);
                }
            }
            return (ow, oh, rgba);
        }

        // the path as closed polylines (view box units): M L H V C S Q A Z, absolute and relative
        private static List<List<Vector2>> Flatten(string d)
        {
            var shapes = new List<List<Vector2>>();
            List<Vector2>? cur = null;
            Vector2 pos = default, start = default, ctrl = default;
            int p = 0;
            char cmd = 'M', last = ' ';
            while (true)
            {
                Skip();
                if (p >= d.Length) break;
                if (char.IsLetter(d[p])) cmd = d[p++];
                bool rel = char.IsLower(cmd);
                Vector2 o = rel ? pos : Vector2.Zero;
                char up = char.ToUpperInvariant(cmd);
                switch (up)
                {
                case 'M':
                    pos = start = o + Pair();
                    cur = new List<Vector2> { pos };
                    shapes.Add(cur);
                    cmd = rel ? 'l' : 'L'; // more pairs after M are lines
                    break;
                case 'L': To(o + Pair()); break;
                case 'H': To(new Vector2((rel ? pos.X : 0) + Num(), pos.Y)); break;
                case 'V': To(new Vector2(pos.X, (rel ? pos.Y : 0) + Num())); break;
                case 'C':
                {
                    Vector2 c1 = o + Pair(), c2 = o + Pair(), e = o + Pair();
                    Cubic(c1, c2, e);
                    break;
                }
                case 'S':
                {
                    Vector2 c1 = last is 'C' or 'S' ? 2 * pos - ctrl : pos, c2 = o + Pair(), e = o + Pair();
                    Cubic(c1, c2, e);
                    break;
                }
                case 'Q':
                {
                    Vector2 c = o + Pair(), e = o + Pair();
                    Cubic(pos + 2f / 3 * (c - pos), e + 2f / 3 * (c - e), e);
                    ctrl = c;
                    break;
                }
                case 'A':
                {
                    float rx = Num(), ry = Num(), rot = Num();
                    bool large = Flag(), sweep = Flag();
                    Arc(rx, ry, rot, large, sweep, o + Pair());
                    break;
                }
                case 'Z':
                    pos = start;
                    cur = null;
                    break;
                default:
                    throw new FormatException($"path command '{cmd}'");
                }
                last = up;
            }
            return shapes;

            void Skip()
            {
                while (p < d.Length && (d[p] == ' ' || d[p] == ',' || char.IsWhiteSpace(d[p]))) p++;
            }
            float Num()
            {
                Skip();
                int s = p;
                if (p < d.Length && (d[p] == '-' || d[p] == '+')) p++;
                bool dot = false;
                while (p < d.Length && (char.IsDigit(d[p]) || (d[p] == '.' && !dot)))
                {
                    if (d[p] == '.') dot = true;
                    p++;
                }
                if (p < d.Length && (d[p] == 'e' || d[p] == 'E'))
                {
                    p++;
                    if (p < d.Length && (d[p] == '-' || d[p] == '+')) p++;
                    while (p < d.Length && char.IsDigit(d[p])) p++;
                }
                return float.Parse(d.AsSpan(s, p - s), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            Vector2 Pair()
            {
                float x = Num();
                return new Vector2(x, Num());
            }
            // an arc flag is one digit, packed against what follows ("00-4.88")
            bool Flag()
            {
                Skip();
                return d[p++] == '1';
            }
            void To(Vector2 e)
            {
                if (cur == null)
                {
                    cur = new List<Vector2> { pos };
                    shapes.Add(cur);
                }
                cur.Add(e);
                pos = e;
            }
            void Cubic(Vector2 c1, Vector2 c2, Vector2 e)
            {
                float len = (c1 - pos).Length() + (c2 - c1).Length() + (e - c2).Length();
                int n = Math.Clamp((int)MathF.Ceiling(len * 8), 4, 64);
                Vector2 b = pos;
                for (int i = 1; i <= n; i++)
                {
                    float t = i / (float)n, u = 1 - t;
                    To(i == n ? e : u * u * u * b + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * e);
                }
                ctrl = c2;
            }
            // SVG's endpoint arc, through its centre form
            void Arc(float rx, float ry, float rotDeg, bool large, bool sweep, Vector2 e)
            {
                rx = MathF.Abs(rx);
                ry = MathF.Abs(ry);
                if (rx == 0 || ry == 0 || e == pos)
                {
                    To(e);
                    return;
                }
                float phi = rotDeg * MathF.PI / 180, cs = MathF.Cos(phi), sn = MathF.Sin(phi);
                float dx = (pos.X - e.X) / 2, dy = (pos.Y - e.Y) / 2;
                float x1 = cs * dx + sn * dy, y1 = -sn * dx + cs * dy;
                float lam = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
                if (lam > 1)
                {
                    rx *= MathF.Sqrt(lam);
                    ry *= MathF.Sqrt(lam);
                }
                float num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1, den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
                float coef = (large != sweep ? 1 : -1) * MathF.Sqrt(MathF.Max(0, num / den));
                float cx1 = coef * rx * y1 / ry, cy1 = -coef * ry * x1 / rx;
                float cx = cs * cx1 - sn * cy1 + (pos.X + e.X) / 2, cy = sn * cx1 + cs * cy1 + (pos.Y + e.Y) / 2;
                float ux = (x1 - cx1) / rx, uy = (y1 - cy1) / ry, vx = (-x1 - cx1) / rx, vy = (-y1 - cy1) / ry;
                float th = MathF.Atan2(uy, ux), dth = MathF.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
                if (!sweep && dth > 0) dth -= 2 * MathF.PI;
                else if (sweep && dth < 0) dth += 2 * MathF.PI;
                int n = Math.Clamp((int)MathF.Ceiling(MathF.Abs(dth) * MathF.Max(rx, ry) * 8), 2, 64);
                for (int i = 1; i <= n; i++)
                {
                    float a = th + dth * i / n;
                    To(i == n ? e : new Vector2(cx + cs * rx * MathF.Cos(a) - sn * ry * MathF.Sin(a), cy + sn * rx * MathF.Cos(a) + cs * ry * MathF.Sin(a)));
                }
            }
        }

        // non-zero coverage per work texel: four scanlines per row, exact along each
        private static float[] Coverage(List<List<Vector2>> shapes, int w, int h)
        {
            const int rows = 4;
            float[] m = new float[w * h];
            var xs = new List<(float X, int Dir)>();
            for (int y = 0; y < h; y++)
            {
                for (int r = 0; r < rows; r++)
                {
                    float sy = y + (r + 0.5f) / rows;
                    xs.Clear();
                    foreach (List<Vector2> s in shapes)
                    {
                        for (int i = 0; i < s.Count; i++)
                        {
                            Vector2 a = s[i], b = s[(i + 1) % s.Count];
                            if (a.Y <= sy && b.Y > sy) xs.Add((a.X + (sy - a.Y) / (b.Y - a.Y) * (b.X - a.X), 1));
                            else if (b.Y <= sy && a.Y > sy) xs.Add((a.X + (sy - a.Y) / (b.Y - a.Y) * (b.X - a.X), -1));
                        }
                    }
                    xs.Sort((p, q) => p.X.CompareTo(q.X));
                    int wind = 0;
                    for (int i = 0; i + 1 < xs.Count; i++)
                    {
                        wind += xs[i].Dir;
                        if (wind == 0) continue;
                        float x0 = Math.Clamp(xs[i].X, 0, w), x1 = Math.Clamp(xs[i + 1].X, 0, w);
                        for (int x = (int)x0; x < w && x < x1; x++)
                        {
                            float cov = MathF.Min(x + 1, x1) - MathF.Max(x, x0);
                            if (cov > 0) m[y * w + x] += cov / rows;
                        }
                    }
                }
            }
            return m;
        }
    }
}
