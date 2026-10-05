using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // Tests MphRecomp.Media.MoviePlayer (the ROM's VX movies through MphRead's decoder):
    //   -movietest              every movie in MphRead's table, as fast as the decoder goes: all frames shown in
    //                           order, audio samples == decoded audio blocks, no errors; decode speed
    //   -movietest <name|Movie> [realtime] [sheet]
    //                           one movie ("01" or CALanding); realtime = a 60 Hz loop with the real clock (checks the
    //                           audio queue never runs dry after the start); sheet = contact sheet PNG + audio WAV in
    //                           extract_out/movies/
    internal static class MovieTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string outDir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "extract_out", "movies"));
            bool realtime = args.Contains("realtime");
            bool sheet = args.Contains("sheet");
            string? one = args.Skip(1).FirstOrDefault(a => a != "realtime" && a != "sheet");
            int pass = 0, fail = 0;
            void Check(string name, bool ok)
            {
                if (ok) pass++; else fail++;
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            }
            if (one == "campaign")
            {
                Campaign(Check);
            }
            else if (one == "framing")
            {
                Framing();
            }
            else if (one == "gunframe")
            {
                GunFrame();
            }
            else if (one != null)
            {
                MoviePlayer player = Enum.TryParse(one, ignoreCase: true, out Movie m) && !Char.IsDigit(one[0])
                    ? MoviePlayer.Open(m) : MoviePlayer.Open(one);
                Play(player, realtime, sheet ? outDir : null, Check);
            }
            else
            {
                foreach (Movie movie in Enum.GetValues<Movie>())
                {
                    if (movie == Movie.None || Metadata.MovieFiles[(int)movie] == null)
                    {
                        continue;
                    }
                    Play(MoviePlayer.Open(movie), realtime: false, sheetDir: null, Check);
                }
            }
            Console.WriteLine($"  {pass} passed, {fail} failed");
        }

        private static void Play(MoviePlayer player, bool realtime, string? sheetDir, Action<string, bool> check)
        {
            using (player)
            {
                var sw = Stopwatch.StartNew();
                var audio = new short[4096];
                long audioSamples = 0;
                int shownFrames = 0;
                int lastVersion = 0;
                bool inOrder = true;
                int lastIndex = -1;
                int dryAfterStart = 0;
                int maxQueued = 0;
                var sheetFrames = new System.Collections.Generic.List<(int Index, byte[] Top, byte[] Bottom)>();
                var wav = sheetDir != null ? new MemoryStream() : null;
                double step = realtime ? 1 / 60.0 : 1 / 15.0;
                long nextTick = 0;
                while (!player.Finished && sw.Elapsed.TotalSeconds < 600)
                {
                    int before = player.FrameIndex;
                    player.Update(realtime || player.Started ? step : 0);
                    if (player.FrameVersion != lastVersion)
                    {
                        lastVersion = player.FrameVersion;
                        shownFrames = player.FrameVersion;
                        // FrameVersion counts frames shown; every frame shown once, in order <=> version == index + 1
                        inOrder &= player.FrameVersion == player.FrameIndex + 1 && player.FrameIndex > lastIndex;
                        lastIndex = player.FrameIndex;
                        if (sheetDir != null && player.FrameCount > 0 && player.FrameIndex % Math.Max(1, player.FrameCount / 11) == 0)
                        {
                            sheetFrames.Add((player.FrameIndex, (byte[])player.TopFrame.Clone(), (byte[])player.BottomFrame.Clone()));
                        }
                    }
                    maxQueued = Math.Max(maxQueued, player.QueuedAudioSamples);
                    if (realtime)
                    {
                        // an audio device drains at the sample rate: take one tick's worth
                        int want = player.Started ? (int)(player.AudioSampleRate * player.AudioChannels * step) : 0;
                        int got = player.ReadAudio(audio.AsSpan(0, Math.Min(want, audio.Length)));
                        if (player.Started && player.Position > 0.5 && got < want && player.Position < player.Duration - 0.5)
                        {
                            dryAfterStart++;
                        }
                        audioSamples += got;
                        wav?.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(audio.AsSpan(0, got)));
                        nextTick += (long)(step * Stopwatch.Frequency);
                        long wait = nextTick - sw.ElapsedTicks;
                        if (wait > 0)
                        {
                            Thread.Sleep(TimeSpan.FromTicks(wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
                        }
                    }
                    else
                    {
                        int got;
                        while ((got = player.ReadAudio(audio)) > 0)
                        {
                            audioSamples += got;
                            wav?.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(audio.AsSpan(0, got)));
                        }
                        if (player.FrameIndex == before)
                        {
                            Thread.Sleep(1); // decoder hasn't produced the next frame yet
                        }
                    }
                }
                int got2;
                while ((got2 = player.ReadAudio(audio)) > 0)
                {
                    audioSamples += got2;
                    wav?.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(audio.AsSpan(0, got2)));
                }
                double secs = sw.Elapsed.TotalSeconds;
                int blocks = player.DualScreen ? Math.Max(VxDecoder.Instance1.AudioFrameTotal, VxDecoder.Instance2.AudioFrameTotal)
                    : VxDecoder.Instance1.AudioFrameTotal;
                long expectedAudio = (long)blocks * 128 * player.AudioChannels;
                string label = $"{player.Name,-24} {(player.DualScreen ? "2 screens" : "1 screen ")} {player.FrameCount,4} frames "
                    + $"@{player.FrameRate:0.##} fps ({player.Duration:0.0} s), audio {player.AudioSampleRate} Hz x{player.AudioChannels}";
                if (realtime)
                {
                    check($"{label}: played in {secs:0.0} s real time, {shownFrames} frames shown, audio dry ticks mid-movie {dryAfterStart}, "
                        + $"max queued {maxQueued} samples", player.Error == null && lastIndex == player.FrameCount - 1 && dryAfterStart == 0);
                }
                else
                {
                    check($"{label}: {shownFrames} frames in order {inOrder}, audio {audioSamples}/{expectedAudio} samples, "
                        + $"decoded at {player.FrameCount / secs:0} fps{(player.Error != null ? ", ERROR " + player.Error : "")}",
                        player.Error == null && inOrder && shownFrames == player.FrameCount && audioSamples == expectedAudio);
                }
                if (sheetDir != null)
                {
                    Directory.CreateDirectory(sheetDir);
                    WriteSheet(Path.Combine(sheetDir, $"{player.Name}_sheet.png"), sheetFrames, player.DualScreen);
                    WriteWav(Path.Combine(sheetDir, $"{player.Name}.wav"), wav!.ToArray(), player.AudioSampleRate, player.AudioChannels);
                    Console.WriteLine($"    wrote {player.Name}_sheet.png ({sheetFrames.Count} frames) and {player.Name}.wav to {sheetDir}");
                }
            }
        }

        // In-game movies with Scene.HostPlaysMovies, hosted the way the Android campaign does it: the sim is not stepped
        // while a movie plays; when the player finishes, SkipMovie hands back to MphRead's after-fade/after-action.
        private static void Campaign(Action<string, bool> check)
        {
            Scene.HostPlaysMovies = true;
            try
            {
                var played = new System.Collections.Generic.List<Movie>();
                // runs one step, or plays the scene's movie to its end first; returns false once the session ended
                bool Step(MphRecomp.Campaign.CampaignHost host, MphRecomp.Campaign.CampaignInput input, ref int serial)
                {
                    if (host.Scene.MoviePlaying && host.Scene.HostMovieSerial != serial)
                    {
                        serial = host.Scene.HostMovieSerial;
                        played.Add(host.Scene.HostMovie);
                        using MoviePlayer movie = MoviePlayer.Open(host.Scene.HostMovie);
                        var sw = Stopwatch.StartNew();
                        while (!movie.Finished && sw.Elapsed.TotalSeconds < 60)
                        {
                            int before = movie.FrameIndex;
                            movie.Update(movie.Started ? 1 / 15.0 : 0);
                            if (movie.FrameIndex == before)
                            {
                                Thread.Sleep(1);
                            }
                        }
                        host.Scene.SkipMovie();
                    }
                    host.Step(input);
                    return !host.Ended;
                }
                var none = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                // 1) a new game lands on Celestial Archives: the landing movie, then the landing camera sequence
                int serial = 0;
                using (var host = MphRecomp.Campaign.CampaignHost.Start("UNIT2_LAND"))
                {
                    int f = 0;
                    for (; f < 1500 && Step(host, none, ref serial); f++)
                    {
                    }
                    check($"new game: {String.Join(", ", played)} played, movie over {!host.Scene.MoviePlaying}, "
                        + $"player spawned (energy {host.Player.Health}) after {f} steps",
                        played.SequenceEqual(new[] { Movie.CALanding }) && !host.Scene.MoviePlaying && host.Player.Health > 0);
                    // 2) YES at the ship hatch: the take-off movie, then the session ends as boarding the ship
                    played.Clear();
                    foreach (EntityBase e in host.Scene.Entities)
                    {
                        if (e is AreaVolumeEntity area && area.Data.InsideMessage == Message.ShipHatch)
                        {
                            // inside its volume (the entity's own position can be outside it)
                            var volume = (CollisionVolume)typeof(AreaVolumeEntity).GetField("_volume",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(area)!;
                            host.PlacePlayer(volume.GetCenter(), -OpenTK.Mathematics.Vector3.UnitZ);
                        }
                    }
                    for (f = 0; f < 2000; f++)
                    {
                        var input = none;
                        input.DialogButton = host.DialogPaused && host.Dialog == DialogType.YesNo && f % 10 == 0
                            ? HostDialogButton.Yes : HostDialogButton.None;
                        if (!Step(host, input, ref serial))
                        {
                            break;
                        }
                    }
                    MphRecomp.Campaign.CampaignEnd end = MphRecomp.Campaign.ShipNavigation.Classify(host);
                    check($"boarding: {String.Join(", ", played)} played, then {end}",
                        played.SequenceEqual(new[] { Movie.CATakeoff }) && end == MphRecomp.Campaign.CampaignEnd.EnteredShip);
                }
                // 3) chained movies: the bad ending's two parts, then the end of the game
                played.Clear();
                serial = 0;
                using (var host = MphRecomp.Campaign.CampaignHost.Start("Gorea_b1", setupSave: s => s.BossFlags = BossFlags.All))
                {
                    for (int i = 0; i < 60; i++) Step(host, none, ref serial);
                    played.Clear();
                    host.Scene.StartMovies(Movie.BadEndingPart1, Movie.BadEndingPart2, FadeType.FadeOutInWhite, 45 / 30f,
                        FadeType.FadeOutBlack, 0, AfterMovie.EndGame);
                    int f = 0;
                    for (; f < 3000 && Step(host, none, ref serial); f++)
                    {
                    }
                    MphRecomp.Campaign.CampaignEnd end = MphRecomp.Campaign.ShipNavigation.Classify(host);
                    check($"bad ending: {String.Join(", ", played)} played, then {end}",
                        played.SequenceEqual(new[] { Movie.BadEndingPart1, Movie.BadEndingPart2 }) && end == MphRecomp.Campaign.CampaignEnd.Ending);
                }
            }
            finally
            {
                Scene.HostPlaysMovies = false;
            }
        }

        // How each movie uses its 256x192 frames: rows/columns that stay dark in EVERY frame (letterboxing inside the
        // 4:3 picture), and for two-screen movies whether top and bottom form one continuous picture (the seam between
        // them is compared with ordinary neighbouring rows inside a screen, per frame).
        //   -movietest framing
        private static void Framing()
        {
            const int W = MoviePlayer.Width, H = MoviePlayer.Height;
            const int Dark = 24; // max luma (0-255) a row/column may reach and still count as a black bar
            Console.WriteLine("  movie                     screen  dark rows top/bottom  dark cols left/right   seam");
            foreach (Movie movie in Enum.GetValues<Movie>())
            {
                if (movie == Movie.None || Metadata.MovieFiles[(int)movie] == null)
                {
                    continue;
                }
                using MoviePlayer player = MoviePlayer.Open(movie);
                var rowMax = new int[2, H];
                var colMax = new int[2, W];
                double seamDiff = 0, innerDiff = 0;
                int seamFrames = 0, continuousFrames = 0, comparedFrames = 0;
                int lastVersion = 0;
                var sw = Stopwatch.StartNew();
                while (!player.Finished && sw.Elapsed.TotalSeconds < 120)
                {
                    int before = player.FrameIndex;
                    player.Update(player.Started ? 1 / 15.0 : 0);
                    player.ReadAudio(new short[8192]);
                    if (player.FrameVersion == lastVersion)
                    {
                        if (player.FrameIndex == before) Thread.Sleep(1);
                        continue;
                    }
                    lastVersion = player.FrameVersion;
                    for (int s = 0; s < (player.DualScreen ? 2 : 1); s++)
                    {
                        byte[] f = s == 0 ? player.TopFrame : player.BottomFrame;
                        for (int y = 0; y < H; y++)
                        {
                            for (int x = 0; x < W; x++)
                            {
                                int i = (y * W + x) * 3;
                                int luma = (f[i] * 299 + f[i + 1] * 587 + f[i + 2] * 114) / 1000;
                                if (luma > rowMax[s, y]) rowMax[s, y] = luma;
                                if (luma > colMax[s, x]) colMax[s, x] = luma;
                            }
                        }
                    }
                    if (player.DualScreen)
                    {
                        // |top's last row - bottom's first row| vs the typical |row - next row| near the bottom of the top
                        // screen, on frames with some detail at the seam
                        double seam = 0, inner = 0, energy = 0;
                        for (int x = 0; x < W; x++)
                        {
                            for (int c = 0; c < 3; c++)
                            {
                                int a = player.TopFrame[((H - 1) * W + x) * 3 + c];
                                int b = player.BottomFrame[(0 * W + x) * 3 + c];
                                int a2 = player.TopFrame[((H - 2) * W + x) * 3 + c];
                                seam += Math.Abs(a - b);
                                inner += Math.Abs(a - a2);
                                energy += a + b;
                            }
                        }
                        if (energy / (W * 6) > 20)
                        {
                            comparedFrames++;
                            seamDiff += seam / (W * 3);
                            innerDiff += inner / (W * 3);
                            // continuous: the jump across the seam is about as small as within the picture
                            if (seam <= inner * 2 + W * 3 * 4)
                            {
                                continuousFrames++;
                            }
                        }
                        seamFrames++;
                    }
                }
                string Bars(int s, bool rows)
                {
                    int n = rows ? H : W;
                    int lead = 0, trail = 0;
                    while (lead < n && (rows ? rowMax[s, lead] : colMax[s, lead]) <= Dark) lead++;
                    while (trail < n - lead && (rows ? rowMax[s, n - 1 - trail] : colMax[s, n - 1 - trail]) <= Dark) trail++;
                    return $"{lead,3}/{trail,-3}";
                }
                for (int s = 0; s < (player.DualScreen ? 2 : 1); s++)
                {
                    string seamText = s == 1 || !player.DualScreen ? "" : comparedFrames == 0 ? "(dark at the seam)"
                        : $"continuous in {100 * continuousFrames / comparedFrames}% of {comparedFrames} frames "
                          + $"(seam jump {seamDiff / comparedFrames:0.0} vs in-picture {innerDiff / comparedFrames:0.0})";
                    Console.WriteLine($"  {(s == 0 ? player.Name : ""),-25} {(s == 0 ? "top   " : "bottom")}  "
                        + $"{Bars(s, true),-20}  {Bars(s, false),-20} {seamText}");
                }
            }
        }

        // Timing reference for on-device logs: from a new game at Celestial Gateway, how many sim steps until MphRead
        // first draws the first-person SamusGun (CampaignActivity logs "gun: GL ready" on that frame), with the landing
        // movie skipped by MphRead (movies off) and played by the host (movies on; the sim is held while it plays).
        //   -movietest gunframe
        private static void GunFrame()
        {
            foreach (bool movies in new[] { false, true })
            {
                Scene.HostPlaysMovies = movies;
                try
                {
                    using var host = MphRecomp.Campaign.CampaignHost.Start("UNIT2_LAND", collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
                    var none = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                    int serial = 0, steps = 0, movieStep = -1, firstGun = -1;
                    double movieSeconds = 0;
                    for (; steps < 3000 && firstGun < 0; steps++)
                    {
                        if (host.Scene.MoviePlaying && host.Scene.HostMovieSerial != serial)
                        {
                            serial = host.Scene.HostMovieSerial;
                            movieStep = steps;
                            using MoviePlayer movie = MoviePlayer.Open(host.Scene.HostMovie);
                            var sw = Stopwatch.StartNew();
                            while (!movie.Finished && sw.Elapsed.TotalSeconds < 30)
                            {
                                movie.Update(movie.Started ? 1 / 60.0 : 0);
                                movie.ReadAudio(new short[4096]);
                                if (movie.Started)
                                {
                                    movieSeconds += 1 / 60.0;
                                }
                                Thread.Sleep(1);
                            }
                            host.Scene.SkipMovie();
                        }
                        host.Step(none);
                        if (MphRecomp.Campaign.GunViewmodel.TryMphLightColour(host.Scene, out _))
                        {
                            firstGun = steps;
                        }
                    }
                    Console.WriteLine($"  movies {(movies ? "on " : "off")}: movie at step {movieStep} ({movieSeconds:0.00} s of movie clock), "
                        + $"first SamusGun draw at step {firstGun} = {firstGun / 60.0:0.00} s of sim at 60 steps/s"
                        + (movies ? $" -> on a device: ~{movieSeconds + firstGun / 60.0:0.0} s from launch to the gun" : ""));
                }
                finally
                {
                    Scene.HostPlaysMovies = false;
                }
            }
            // CampaignActivity's frame loop in virtual time (95 fps, 60 steps/s fixed step, at most 4 steps a frame),
            // with the landing movie holding the sim for its length: time from launch to the first gun draw when the
            // step backlog is banked during the hold (before the fix) and when it is dropped (after)
            foreach (bool bankDuringHold in new[] { true, false })
            {
                Scene.HostPlaysMovies = true;
                try
                {
                    using var host = MphRecomp.Campaign.CampaignHost.Start("UNIT2_LAND", collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
                    var none = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                    const double frame = 1 / 95.0;
                    double time = 0, accum = 0, holdUntil = -1, movieAt = -1;
                    int serial = 0;
                    double gunAt = -1;
                    while (gunAt < 0 && time < 60)
                    {
                        time += frame;
                        bool hold = time < holdUntil;
                        accum += frame;
                        int steps = 0;
                        while (accum >= 1 / 60.0 && steps < 4 && !hold)
                        {
                            accum -= 1 / 60.0;
                            steps++;
                            host.Step(none);
                            if (host.Scene.MoviePlaying && host.Scene.HostMovieSerial != serial)
                            {
                                serial = host.Scene.HostMovieSerial;
                                movieAt = time;
                                holdUntil = time + 4.0; // CALanding: 60 frames at 15 fps
                                host.Scene.SkipMovie(); // the sim sees the movie end when the hold ends (it isn't stepped until then)
                                hold = true;
                            }
                            if (MphRecomp.Campaign.GunViewmodel.TryMphLightColour(host.Scene, out _))
                            {
                                gunAt = time;
                                break;
                            }
                        }
                        if (hold && !bankDuringHold)
                        {
                            accum = 0;
                        }
                    }
                    Console.WriteLine($"  device loop, backlog {(bankDuringHold ? "banked during the movie (before the fix)" : "dropped during the movie (fix)   ")}: "
                        + $"movie at {movieAt:0.00} s, first gun draw {gunAt - movieAt:0.00} s after the movie started");
                }
                finally
                {
                    Scene.HostPlaysMovies = false;
                }
            }
        }

        private static void WriteSheet(string path, System.Collections.Generic.List<(int Index, byte[] Top, byte[] Bottom)> frames, bool dual)
        {
            int cols = 4;
            int rows = (frames.Count + cols - 1) / cols;
            int cellH = dual ? MoviePlayer.Height * 2 + 4 : MoviePlayer.Height;
            using var image = new Image<Rgb24>(cols * (MoviePlayer.Width + 4), Math.Max(1, rows) * (cellH + 4));
            for (int f = 0; f < frames.Count; f++)
            {
                int ox = f % cols * (MoviePlayer.Width + 4);
                int oy = f / cols * (cellH + 4);
                void Blit(byte[] rgb, int y0)
                {
                    for (int y = 0; y < MoviePlayer.Height; y++)
                    {
                        for (int x = 0; x < MoviePlayer.Width; x++)
                        {
                            int i = (y * MoviePlayer.Width + x) * 3;
                            image[ox + x, oy + y0 + y] = new Rgb24(rgb[i], rgb[i + 1], rgb[i + 2]);
                        }
                    }
                }
                Blit(frames[f].Top, 0);
                if (dual)
                {
                    Blit(frames[f].Bottom, MoviePlayer.Height + 4);
                }
            }
            image.SaveAsPng(path);
        }

        private static void WriteWav(string path, byte[] pcm, int rate, int channels)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write("RIFF"u8.ToArray());
            w.Write(36 + pcm.Length);
            w.Write("WAVEfmt "u8.ToArray());
            w.Write(16);
            w.Write((short)1);
            w.Write((short)channels);
            w.Write(rate);
            w.Write(rate * channels * 2);
            w.Write((short)(channels * 2));
            w.Write((short)16);
            w.Write("data"u8.ToArray());
            w.Write(pcm.Length);
            w.Write(pcm);
        }
    }
}
