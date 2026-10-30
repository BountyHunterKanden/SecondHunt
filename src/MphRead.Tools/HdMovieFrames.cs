using System;
using System.IO;
using System.Linq;
using System.Threading;
using MphRead.Formats;
using MphRecomp.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // Every frame of one of the ROM's movies as a PNG, to study the pre-rendered hunters (the opening movie shows each one,
    // in detail, from many angles) as references for their trophies' rest poses. Top screen above the bottom one.
    //   MphRead.Tools.dll -movieframes <name|Movie> [--from N] [--to N] [--out dir]     (default out: extract_out/movies/<name>_frames)
    internal static class HdMovieFrames
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string one = args[1];
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            int from = int.Parse(Arg("--from", "0")), to = int.Parse(Arg("--to", int.MaxValue.ToString()));
            MoviePlayer player = Enum.TryParse(one, ignoreCase: true, out Movie m) && !char.IsDigit(one[0]) ? MoviePlayer.Open(m) : MoviePlayer.Open(one);
            string outDir = Arg("--out", Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "extract_out", "movies", player.Name + "_frames")));
            Directory.CreateDirectory(outDir);
            int saved = 0;
            using (player)
            {
                var audio = new short[4096]; int lastVersion = 0; var start = DateTime.UtcNow;
                while (!player.Finished && (DateTime.UtcNow - start).TotalSeconds < 900)
                {
                    int before = player.FrameIndex;
                    player.Update(player.Started ? 1 / 15.0 : 0);
                    if (player.FrameVersion != lastVersion)
                    {
                        lastVersion = player.FrameVersion;
                        int f = player.FrameIndex;
                        if (f >= from && f <= to) { Save(Path.Combine(outDir, $"{f:0000}.png"), player.TopFrame, player.DualScreen ? player.BottomFrame : null); saved++; }
                        if (f > to) break;
                    }
                    while (player.ReadAudio(audio) > 0) { }
                    if (player.FrameIndex == before) Thread.Sleep(1);
                }
                Console.WriteLine($"{player.Name}: {player.FrameCount} frames @{player.FrameRate:0.##} fps, {saved} saved to {outDir}{(player.Error != null ? ", ERROR " + player.Error : "")}");
            }
        }

        static void Save(string path, byte[] top, byte[]? bottom)
        {
            int w = MoviePlayer.Width, h = MoviePlayer.Height;
            using var image = new Image<Rgb24>(w, bottom != null ? h * 2 : h);
            void Blit(byte[] rgb, int y0)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++) { int i = (y * w + x) * 3; image[x, y0 + y] = new Rgb24(rgb[i], rgb[i + 1], rgb[i + 2]); }
            }
            Blit(top, 0);
            if (bottom != null) Blit(bottom, h);
            image.SaveAsPng(path);
        }
    }
}
