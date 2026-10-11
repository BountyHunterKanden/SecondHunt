using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MphRecomp.Import;
using MphRecomp.Import.Disc;

// IMPORT GAMES' imported extras (owner 2026-10-08: "do the disc banners and renders"): a picked GameCube disc's own
// banner -- the 96 x 32 picture the GameCube's menu shows for it, opening.bnr on the disc -- shown in place of the game's
// name on its card and in its page's "your disc" box. Read once a pick, on a worker (an .rvz is decompressed to get at
// it), and kept as <root>/import/banner_<pick>.png; a disc with no GameCube banner (a Wii disc: its banner is another
// format) is remembered as banner_<pick>.none, so it isn't opened again. Nothing here is needed for the pages' own look.
namespace MphRecomp.Frontend
{
    public static class ImportBanners
    {
        public const int Width = 96, Height = 32;

        // per pick token: the PNG's path, "" = none, absent = not looked at yet; a token being read is in s_reading
        private static readonly ConcurrentDictionary<string, string> s_known = new();
        private static readonly ConcurrentDictionary<string, bool> s_reading = new();

        // the pick's banner PNG, or null (none, or not read yet: the first call starts the read)
        public static string? For(string? root, ImportPicked? pick)
        {
            if (root == null || pick == null) return null;
            if (s_known.TryGetValue(pick.Token, out string? known)) return known.Length > 0 ? known : null;
            string png = CachePath(root, pick.Token, ".png"), none = CachePath(root, pick.Token, ".none");
            if (File.Exists(png))
            {
                s_known[pick.Token] = png;
                return png;
            }
            if (File.Exists(none))
            {
                s_known[pick.Token] = "";
                return null;
            }
            if (s_reading.TryAdd(pick.Token, true))
            {
                Task.Run(() =>
                {
                    try
                    {
                        s_known[pick.Token] = Read(pick, png, none) ? png : "";
                    }
                    finally
                    {
                        s_reading.TryRemove(pick.Token, out _);
                    }
                });
            }
            return null;
        }

        private static string CachePath(string root, string token, string ext)
        {
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(token));
            return Path.Combine(root, "import", "banner_" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + ext);
        }

        // true = the PNG was written; false = no banner (marked .none, unless the disc couldn't be read at all this time)
        private static bool Read(ImportPicked pick, string png, string none)
        {
            byte[]? rgba = null;
            try
            {
                using Stream? s = ImportHost.Open(pick.Token);
                if (s == null) return false;
                using IFileSource disc = DiscImage.Open(s);
                if (disc.Contains("opening.bnr")) rgba = Decode(disc.ReadFile("opening.bnr"));
            }
            catch (WiiKeyRequiredException)
            {
                // a plain Wii image: its banner isn't a GameCube one either way
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                Console.Error.WriteLine("import banner: " + pick.Name + " unreadable (" + ex.Message + ")");
                return false;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(png)!);
                if (rgba == null)
                {
                    File.WriteAllBytes(none, Array.Empty<byte>());
                    return false;
                }
                PngOut.Write(png, rgba, Width, Height);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("import banner: not cached (" + ex.Message + ")");
                return false;
            }
        }

        // opening.bnr ("BNR1" / "BNR2"): the picture at 0x20, 96 x 32 RGB5A3 in 4 x 4 tiles; null for anything else
        public static byte[]? Decode(byte[] bnr)
        {
            if (bnr.Length < 0x20 + Width * Height * 2) return null;
            string magic = Encoding.ASCII.GetString(bnr, 0, 4);
            if (magic != "BNR1" && magic != "BNR2") return null;
            var rgba = new byte[Width * Height * 4];
            int at = 0x20;
            for (int ty = 0; ty < Height; ty += 4)
            {
                for (int tx = 0; tx < Width; tx += 4)
                {
                    for (int y = ty; y < ty + 4; y++)
                    {
                        for (int x = tx; x < tx + 4; x++, at += 2)
                        {
                            int v = bnr[at] << 8 | bnr[at + 1], o = (y * Width + x) * 4;
                            if ((v & 0x8000) != 0)
                            {
                                rgba[o] = Five(v >> 10);
                                rgba[o + 1] = Five(v >> 5);
                                rgba[o + 2] = Five(v);
                                rgba[o + 3] = 255;
                            }
                            else
                            {
                                int a = v >> 12 & 7;
                                rgba[o] = (byte)((v >> 8 & 15) * 17);
                                rgba[o + 1] = (byte)((v >> 4 & 15) * 17);
                                rgba[o + 2] = (byte)((v & 15) * 17);
                                rgba[o + 3] = (byte)(a << 5 | a << 2 | a >> 1);
                            }
                        }
                    }
                }
            }
            return rgba;
        }

        private static byte Five(int v) => (byte)((v & 31) << 3 | (v & 31) >> 2);
    }
}
