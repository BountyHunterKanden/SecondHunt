using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Editor;
using MphRead.Entities;
using MphRecomp.Arenas;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Retro;

namespace MphRead
{
    // Echoes multiplayer arenas on the PC (docs/MP2_MULTIPLAYER_IMPORT.md, phase 1):
    //   -echoesarena import [disc=<echoes .iso>] [arena=all|<name>] [scale=0.593]
    //       writes MphRead room files for the arena(s) into the desktop extraction (src/MphRead/bin/Debug/net9.0/files/AMHE0)
    //   -echoesarena sim <arena> [matchsim options: mode= bots= level= time= hunter= move]
    //       registers the imported arenas and runs a -matchsim bot match in one
    //   -echoesarena music [disc=<echoes .iso>] [wav=<dir>] [match=<arena>] [time=<s>]
    //       copies the arenas' music streams from the disc (as import does), prints each track's loop and level; wav=
    //       writes the first play + one loop of each; match= runs a headless Battle there with a logging stream output
    //       (time= its time limit, default 70 s) to show when ArenaMusic starts, switches at one minute and stops
    //   -echoesarena check [arena]
    //       the device's match start per arena x mode (Battle, Survival, Prime Hunter; Combat Hall as the control):
    //       CampaignHost.StartMatch with draw items, 300 frames, and every frame the room lookups the Android status
    //       line and map make from the scene's room id (Metadata.GetRoomById(host.RoomId) threw on the Odin 15:45)
    internal static class EchoesArenaTool
    {
        static readonly string DefaultDisc = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "brawl_extract", "prime", "echoes_widescreen.iso"));

        public static void Run(string[] args)
        {
            string verb = args.Length > 1 ? args[1] : "import";
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            if (verb == "sim")
            {
                int n = EchoesArena.Register(Paths.FileSystem);
                string name = args.Length > 2 ? args[2] : "Crossfire Chaos";
                EchoesArena.Def def = EchoesArena.Find(name) ?? throw new ArgumentException($"no Echoes arena {name}");
                Console.WriteLine($"{n} Echoes arenas registered");
                MatchSim.Run(new[] { "-matchsim", $"arena={def.Room}" }.Concat(args.Skip(3)).ToArray());
                return;
            }
            if (verb == "check")
            {
                Check(args.Length > 2 ? args[2] : null);
                return;
            }
            if (verb == "bots")
            {
                Bots(args.Length > 2 && !args[2].Contains('=') ? args[2] : null,
                    Arg(args, "time") is string t ? int.Parse(t) : 120, Arg(args, "bots") is string b ? int.Parse(b) : 3);
                return;
            }
            if (verb == "alloc")
            {
                Alloc(args.Length > 2 && !args[2].Contains('=') ? args[2] : null,
                    Arg(args, "time") is string t ? int.Parse(t) : 60, Arg(args, "bots") is string b ? int.Parse(b) : 3, Arg(args, "draw") != "0");
                return;
            }
            if (verb == "padtest")
            {
                PadTest(args.Length > 2 && !args[2].Contains('=') ? args[2] : null);
                return;
            }
            if (verb == "spec")
            {
                // the per-material fragment programs ArenaGxSpec writes for each imported arena (out=<dir>: one file each)
                string? outDir = Arg(args, "out");
                foreach (EchoesArena.Def d in EchoesArena.All)
                {
                    string json = Path.Combine(Paths.FileSystem, "_archives", d.Archive, ArenaHdWriter.Dir, ArenaHdWriter.Materials);
                    if (!File.Exists(json)) continue;
                    var sources = new Dictionary<string, List<string>>();
                    int none = 0;
                    foreach (MphRecomp.Assets.GxMaterial m in MphRecomp.Assets.GxJson.Read(json))
                    {
                        string? src = ArenaGxSpec.Frag(MphRecomp.Render.GxShader.Pack(m));
                        if (src == null) { none++; continue; }
                        if (!sources.TryGetValue(src, out List<string>? names)) sources[src] = names = new List<string>();
                        names.Add(m.Name);
                    }
                    Console.WriteLine($"{d.InGameName}: {sources.Count} distinct programs for {sources.Values.Sum(n => n.Count)} materials, {none} left on the interpreter");
                    if (outDir == null) continue;
                    Directory.CreateDirectory(outDir);
                    int i = 0;
                    foreach ((string src, List<string> names) in sources)
                    {
                        File.WriteAllText(Path.Combine(outDir, $"{d.Archive}_{i++:00}.frag"), $"// {string.Join(", ", names)}\n{src}");
                    }
                }
                return;
            }
            if (verb == "padinfo")
            {
                // the retail multiplayer arenas' jump pads (the defaults Echoes cannons copy)
                foreach (RoomMetadata meta in Metadata.RoomMetadata.Values.Where(m => m.Multiplayer && m.EntityPath != null).DistinctBy(m => m.EntityPath))
                {
                    foreach (Entity e in Read.GetEntities(meta.EntityPath!, -1, firstHunt: false).Where(e => e.Type == EntityType.JumpPad))
                    {
                        JumpPadEntityData d = ((Entity<JumpPadEntityData>)e).Data;
                        var ed = new JumpPadEntityEditor(e, d);
                        CollisionVolume v = ed.Volume;
                        string vol = v.Type == VolumeType.Cylinder ? $"cyl r {v.CylinderRadius:0.##} dot {v.CylinderDot:0.##} vec {v.CylinderVector} at {v.CylinderPosition}"
                            : v.Type == VolumeType.Sphere ? $"sphere r {v.SphereRadius:0.##} at {v.SpherePosition}" : $"box {v.BoxDot1:0.##}x{v.BoxDot2:0.##}x{v.BoxDot3:0.##} at {v.BoxPosition}";
                        Console.WriteLine($"{meta.InGameName,-22} pad {e.EntityId} at {e.Position} up {e.UpVector} facing {e.FacingVector} beam {ed.BeamVector} speed {ed.Speed:0.###} "
                            + $"lock {ed.ControlLockTime} cool {ed.CooldownTime} model {ed.ModelId} type {ed.BeamType} flags {ed.TriggerFlags} parent {ed.ParentId} u28 {ed.Unused28} active {ed.Active} | {vol}");
                    }
                }
                return;
            }
            if (verb == "preview")
            {
                EchoesArena.Register(Paths.FileSystem);
                foreach (EchoesArena.Def d in EchoesArena.All)
                {
                    if (args.Length > 2 && EchoesArena.Find(args[2]) != d) continue;
                    if (Metadata.GetRoomByName(d.Room).Item1 == null) continue;
                    string png = Path.Combine(Paths.FileSystem, "_archives", d.Archive, "preview.png");
                    Preview(d, png);
                    Console.WriteLine($"{d.InGameName}: {png}");
                }
                return;
            }
            if (verb == "music")
            {
                MusicVerb(args);
                return;
            }
            if (verb != "import")
            {
                Console.WriteLine($"-echoesarena: unknown verb {verb} (import, music, sim, check, bots, alloc, preview, padinfo, padtest, spec)");
                return;
            }
            string disc = Arg(args, "disc") ?? DefaultDisc;
            string which = Arg(args, "arena") ?? "all";
            float scale = Arg(args, "scale") is string s ? float.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : EchoesArena.DefaultScale;
            using FileStream fs = File.OpenRead(disc);
            var gc = new GcDisc(fs);
            if (gc.GameId != "G2ME01")
            {
                Console.WriteLine($"  note: {gc.GameId} is not the NTSC-U Echoes disc these arenas were checked on");
            }
            var pak = new Pak(gc.ReadFile("Metroid6.pak"), "Metroid6.pak");
            foreach (EchoesArena.Def def in EchoesArena.All)
            {
                if (which != "all" && EchoesArena.Find(which) != def) continue;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                EchoesArena.Import(pak, def, Paths.FileSystem, scale, Console.WriteLine);
                Console.WriteLine($"  ({sw.ElapsedMilliseconds} ms)");
            }
            ArenaMusic.Import(gc, Paths.FileSystem, Console.WriteLine);
        }

        static void MusicVerb(string[] args)
        {
            string disc = Arg(args, "disc") ?? DefaultDisc;
            if (File.Exists(disc))
            {
                using FileStream fs = File.OpenRead(disc);
                using var gc = new GcDisc(fs);
                ArenaMusic.Import(gc, Paths.FileSystem, Console.WriteLine);
            }
            string dir = ArenaMusic.Folder(Paths.FileSystem);
            string? wav = Arg(args, "wav");
            if (wav != null) Directory.CreateDirectory(wav);
            foreach (string file in ArenaMusic.Files)
            {
                string path = Path.Combine(dir, file);
                if (!File.Exists(path))
                {
                    Console.WriteLine($"  {file}: missing");
                    continue;
                }
                var stream = new RetroStream(File.ReadAllBytes(path));
                // level of the first play + one loop, as interleaved PCM16
                RetroStreamReader reader = stream.OpenReader();
                long frames = stream.Loops ? stream.LoopEnd + (long)(stream.LoopEnd - stream.LoopStart) : stream.SampleCount;
                var buf = new short[4096 * stream.Channels];
                var pcm = wav != null ? new List<short>() : null;
                double sumSq = 0;
                int peak = 0;
                long n = 0;
                for (long left = frames; left > 0;)
                {
                    int got = reader.Read(buf, (int)Math.Min(4096, left));
                    if (got == 0) break;
                    for (int i = 0; i < got * stream.Channels; i++)
                    {
                        sumSq += (double)buf[i] * buf[i];
                        peak = Math.Max(peak, Math.Abs((int)buf[i]));
                    }
                    n += got * stream.Channels;
                    pcm?.AddRange(buf.AsSpan(0, got * stream.Channels).ToArray());
                    left -= got;
                }
                static double Db(double v) => 20 * Math.Log10(Math.Max(v, 1e-9) / 32768);
                Console.WriteLine($"  {file}: {stream.Channels} ch {stream.SampleRate} Hz, {stream.SampleCount / (double)stream.SampleRate:0.0} s, "
                    + (stream.Loops ? $"loop {stream.LoopStart / (double)stream.SampleRate:0.00}-{stream.LoopEnd / (double)stream.SampleRate:0.00} s" : "no loop")
                    + $", peak {Db(peak):0.0} dBFS, RMS {Db(Math.Sqrt(sumSq / Math.Max(n, 1))):0.0} dBFS");
                if (pcm != null) WriteWav(Path.Combine(wav!, Path.ChangeExtension(file, ".wav")), pcm, stream.Channels, stream.SampleRate);
            }
            if (Arg(args, "match") is string arena)
            {
                MusicMatch(arena, Arg(args, "time") is string t ? float.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : 70);
            }
        }

        static void WriteWav(string path, List<short> pcm, int channels, int rate)
        {
            using var w = new BinaryWriter(File.Create(path));
            int bytes = pcm.Count * 2;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
            w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
            w.Write("data"u8); w.Write(bytes);
            foreach (short v in pcm) w.Write(v);
        }

        sealed class LogOutput : ArenaMusic.IOutput
        {
            public Func<long> Frame = () => 0;
            public void Start(RetroStream stream, float gain, float fadeIn, float delay, float oldFadeOut) =>
                Console.WriteLine($"    frame {Frame()}: Start {stream.SampleCount / (double)stream.SampleRate:0.0} s stream, gain {gain:0.00}, fade in {fadeIn} s, after {delay} s, old fades {oldFadeOut} s");
            public void Stop(float fadeOut) => Console.WriteLine($"    frame {Frame()}: Stop, fade {fadeOut:0.00} s");
        }

        // a Battle with bots at the arena, a short time limit, the music hook's calls logged with the frame they came on
        static void MusicMatch(string arena, float seconds)
        {
            EchoesArena.Register(Paths.FileSystem);
            EchoesArena.Def def = EchoesArena.Find(arena) ?? throw new ArgumentException($"no Echoes arena {arena}");
            var output = new LogOutput();
            ArenaMusic.Attach(output, m => Console.WriteLine($"    {m}"));
            var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, Hunter.Samus, 3, 1);
            settings.TimeLimitSeconds = seconds;
            Console.WriteLine($"  match at {def.InGameName}, time limit {seconds} s:");
            MphRecomp.Campaign.CampaignHost? host = null;
            output.Frame = () => host?.Frame ?? 0;
            host = MphRecomp.Campaign.CampaignHost.StartMatch(settings, collectDrawItems: false, viewWidth: 1920, viewHeight: 1080);
            var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            int limit = (int)((seconds + 20) * 60);
            for (int f = 0; f < limit && !host.Ended; f++) host.Step(input);
            Console.WriteLine($"  ended {host.Ended} at frame {host.Frame}");
            MphRead.Music.RoomHost = null;
        }

        static void Check(string? only)
        {
            int n = EchoesArena.Register(Paths.FileSystem);
            Console.WriteLine($"{n} Echoes arenas registered");
            var rooms = EchoesArena.All.Where(d => only == null || EchoesArena.Find(only) == d)
                .Select(d => (d.Room, d.InGameName)).Prepend(("MP3 PROVING GROUND", "Combat Hall")).ToList();
            GameMode[] modes = { GameMode.Battle, GameMode.Survival, GameMode.PrimeHunter };
            int failed = 0;
            foreach ((string room, string inGame) in rooms)
            {
                foreach (GameMode mode in modes)
                {
                    string result;
                    MphRecomp.Campaign.CampaignHost? host = null;
                    try
                    {
                        var settings = MphRecomp.Multiplayer.MatchSettings.Quick(mode, room, Hunter.Samus, 3, 1);
                        host = MphRecomp.Campaign.CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
                        var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                        string? status = null;
                        for (int f = 0; f < 300 && !host.Ended; f++)
                        {
                            host.Step(input);
                            // CampaignRenderer.MatchStatusText / Status / CampaignMap: every frame from the scene's id
                            RoomMetadata meta = Metadata.GetRoomById(host.RoomId) ?? throw new Exception($"GetRoomById({host.RoomId}) = null");
                            status = $"{meta.InGameName}  " + MphRecomp.Multiplayer.MatchStatus.Scoreboard();
                        }
                        (RoomMetadata? byName, int id) = Metadata.GetRoomByName(room);
                        RoomMetadata byId = Metadata.GetRoomById(host.RoomId)!;
                        if (byName != byId || id != host.RoomId || byId.InGameName != inGame)
                        {
                            throw new Exception($"room {room}: by name id {id}, scene id {host.RoomId}, by id {byId.Name}");
                        }
                        result = $"ok    id {host.RoomId}, {host.Frame} f, \"{status!.Split('\n')[0]}\"";
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        result = $"FAIL  {ex.GetType().Name}: {ex.Message}";
                    }
                    finally
                    {
                        host?.Dispose();
                    }
                    Console.WriteLine($"  {inGame,-20} {mode,-12} {result}");
                }
            }
            Console.WriteLine(failed == 0 ? $"check: all {rooms.Count * modes.Length} ok" : $"check: {failed} FAILED");
        }

        // owner (Odin, 2026-10-03): bots on the Echoes arenas "get stuck pivoting around in one spot shooting wildly". A Battle
        // with bots per arena (Combat Hall as the control; the human seat stands still): every 2 s window a bot is alive
        // throughout, its path length; a window under 3 units is "stuck". Prints the stuck share per arena and where.
        static void Bots(string? only, int seconds, int bots)
        {
            EchoesArena.Register(Paths.FileSystem);
            var rooms = EchoesArena.All.Where(d => only == null || EchoesArena.Find(only) == d)
                .Select(d => (d.Room, d.InGameName)).Prepend(("MP3 PROVING GROUND", "Combat Hall")).ToList();
            const int Window = 120;
            foreach ((string room, string inGame) in rooms)
            {
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, room, Hunter.Samus, bots, 2);
                settings.TimeLimitSeconds = seconds + 30;
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                int seats = settings.Players.Count;
                var path = new float[seats];
                var last = new OpenTK.Mathematics.Vector3[seats];
                var aliveAll = new bool[seats];
                int windows = 0, stuck = 0;
                var spots = new System.Collections.Generic.Dictionary<(int, int, int), int>();
                for (int i = 0; i < seats; i++) { last[i] = PlayerEntity.Players[i].Position; aliveAll[i] = true; }
                for (int f = 1; f <= seconds * 60 && !host.Ended; f++)
                {
                    host.Step(input);
                    for (int i = 0; i < seats; i++)
                    {
                        PlayerEntity p = PlayerEntity.Players[i];
                        bool alive = p.LoadFlags.TestFlag(LoadFlags.Active) && p.Health > 0;
                        if (!alive) aliveAll[i] = false;
                        else path[i] += (p.Position - last[i]).Length;
                        last[i] = p.Position;
                    }
                    if (f % Window != 0) continue;
                    for (int i = 0; i < seats; i++)
                    {
                        if (settings.Players[i].Bot && aliveAll[i] && f > Window)
                        {
                            windows++;
                            if (path[i] < 3)
                            {
                                stuck++;
                                OpenTK.Mathematics.Vector3 q = PlayerEntity.Players[i].Position;
                                var key = ((int)MathF.Round(q.X / 4) * 4, (int)MathF.Round(q.Y / 4) * 4, (int)MathF.Round(q.Z / 4) * 4);
                                spots[key] = spots.GetValueOrDefault(key) + 1;
                            }
                        }
                        path[i] = 0;
                        aliveAll[i] = true;
                    }
                }
                int kills = Enumerable.Range(0, seats).Sum(i => GameState.Kills[i]);
                Console.WriteLine($"  {inGame,-20} {bots} bots, {host.Frame / 60} s: stuck {stuck} of {windows} bot windows ({(windows > 0 ? 100.0 * stuck / windows : 0):0}%), kills {kills}"
                    + (spots.Count > 0 ? "; spots " + string.Join(", ", spots.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} x{kv.Value}")) : ""));
            }
        }

        // owner (Odin 20:37): a ~13 ms .NET gen0 GC in the sim every 4-7 s (sim alloc ~17 MB/s on Sidehopper). The device's
        // match per arena (Combat Hall as the control; draw items collected; the human seat stands still): MB allocated per
        // simulated second, gen0 GCs, and the types behind it (the runtime's allocation ticks, one per ~100 KB)
        static void Alloc(string? only, int seconds, int bots, bool draw)
        {
            EchoesArena.Register(Paths.FileSystem);
            var rooms = EchoesArena.All.Where(d => only == null || EchoesArena.Find(only) == d)
                .Select(d => (d.Room, d.InGameName)).Prepend(("MP3 PROVING GROUND", "Combat Hall")).ToList();
            using var ticks = new AllocTicks();
            foreach ((string room, string inGame) in rooms)
            {
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, room, Hunter.Samus, bots, 2);
                settings.TimeLimitSeconds = seconds + 60;
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings, collectDrawItems: draw, viewWidth: 1920, viewHeight: 1080);
                var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                for (int f = 0; f < 120 && !host.Ended; f++) host.Step(input);
                System.Threading.Thread.Sleep(1500); // the listener's events arrive late
                ticks.Clear();
                long a0 = GC.GetTotalAllocatedBytes(precise: true);
                int g0 = GC.CollectionCount(0), frames = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (; frames < seconds * 60 && !host.Ended; frames++) host.Step(input);
                if (draw && Environment.GetEnvironmentVariable("ALLOC_SPLIT") == "1")
                {
                    // the draw collection's parts run once more after each step, each measured on its own
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                    var parts = new (string Name, Action Run)[]
                    {
                        ("GetDrawItems", () => typeof(Scene).GetMethod("GetDrawItems", flags, Type.EmptyTypes)!.Invoke(host.Scene, null)),
                        ("RecordHud", () => typeof(Scene).GetMethod("RecordHud", flags)!.Invoke(host.Scene, null)),
                        ("DrawHudModels", () => PlayerEntity.Main.DrawHudModels()),
                        ("DrawHudObjects", () => PlayerEntity.Main.DrawHudObjects()),
                    };
                    var sums = new long[parts.Length];
                    for (int f = 0; f < 600 && !host.Ended; f++)
                    {
                        host.Step(input);
                        for (int i = 0; i < parts.Length; i++)
                        {
                            long b0 = GC.GetAllocatedBytesForCurrentThread();
                            parts[i].Run();
                            sums[i] += GC.GetAllocatedBytesForCurrentThread() - b0;
                        }
                    }
                    for (int i = 0; i < parts.Length; i++) Console.WriteLine($"      split {parts[i].Name,-15} {sums[i] / 600 / 1024.0:0.0} KB/frame");
                }
                double secs = frames / 60.0, ms = sw.Elapsed.TotalMilliseconds;
                long bytes = GC.GetTotalAllocatedBytes(precise: true) - a0;
                int gcs = GC.CollectionCount(0) - g0;
                System.Threading.Thread.Sleep(2000);
                Console.WriteLine($"  {inGame,-20} {secs:0} s: {bytes / 1048576.0 / secs:0.00} MB/s ({bytes / frames / 1024.0:0.0} KB/frame), gen0 GCs {gcs}, "
                    + $"sim {ms / frames:0.00} ms/frame, kills {Enumerable.Range(0, settings.Players.Count).Sum(i => GameState.Kills[i])}");
                foreach ((string type, long amount) in ticks.Top(10))
                {
                    Console.WriteLine($"      {amount / 1048576.0 / secs,6:0.00} MB/s  {type}");
                }
            }
        }

        // the runtime's GC allocation ticks summed by type
        sealed class AllocTicks : System.Diagnostics.Tracing.EventListener
        {
            readonly Dictionary<string, long> _by = new();

            protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
            {
                if (source.Name == "Microsoft-Windows-DotNETRuntime")
                {
                    EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
                }
            }

            protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
            {
                if (e.EventName?.StartsWith("GCAllocationTick") != true || e.PayloadNames == null || e.Payload == null) return;
                int t = e.PayloadNames.IndexOf("TypeName"), a = e.PayloadNames.IndexOf("AllocationAmount64");
                if (a < 0) a = e.PayloadNames.IndexOf("AllocationAmount");
                if (t < 0 || a < 0) return;
                string type = e.Payload[t] as string ?? "?";
                long amount = Convert.ToInt64(e.Payload[a]);
                lock (_by) _by[type] = _by.GetValueOrDefault(type) + amount;
            }

            public void Clear() { lock (_by) _by.Clear(); }

            public List<(string, long)> Top(int n)
            {
                lock (_by) return _by.OrderByDescending(kv => kv.Value).Take(n).Select(kv => (kv.Key, kv.Value)).ToList();
            }
        }

        // the arenas' jump pads fired the way the device fires them: pad data read back from the arena's entity file (no
        // reflection; the ControlLockTime the device gets), the Samus seat walking onto the pad from 3 units back, then
        // either letting go of the stick or holding it toward the target for the whole flight
        static void PadTest(string? only)
        {
            EchoesArena.Register(Paths.FileSystem);
            foreach (EchoesArena.Def d in EchoesArena.All)
            {
                if (only != null && EchoesArena.Find(only) != d) continue;
                string json = Path.Combine(Paths.FileSystem, "_archives", d.Archive, "arena.json");
                if (!File.Exists(json)) continue;
                EchoesArena.Info info = System.Text.Json.JsonSerializer.Deserialize<EchoesArena.Info>(File.ReadAllText(json))!;
                List<ArenaCannons.Cannon> cannons = info.Cannons.Where(c => !c.Dropped).ToList();
                if (cannons.Count == 0) continue;
                RoomMetadata meta = Metadata.GetRoomByName(d.Room).Item1!;
                Dictionary<short, JumpPadEntityEditor> file = Read.GetEntities(meta.EntityPath!, -1, firstHunt: false)
                    .Where(e => e.Type == EntityType.JumpPad)
                    .ToDictionary(e => e.EntityId, e => new JumpPadEntityEditor(e, ((Entity<JumpPadEntityData>)e).Data));
                Console.WriteLine($"{d.InGameName}:");
                var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, 1, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                using MphRecomp.Campaign.CampaignHost host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                for (int f = 0; f < 600 && !(PlayerEntity.Main.Health > 0 && PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Active)); f++) host.Step(idle);
                if (PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                foreach (ArenaCannons.Cannon c in cannons)
                {
                    if (!file.TryGetValue(c.EntityId, out JumpPadEntityEditor? ed))
                    {
                        Console.WriteLine($"  cannon '{c.Name}': pad {c.EntityId} NOT in the entity file");
                        continue;
                    }
                    OpenTK.Mathematics.Vector3 fileLaunch = ed.BeamVector.Normalized() * ed.Speed;
                    Console.WriteLine($"  cannon '{c.Name}' pad {c.EntityId}: file launch ({fileLaunch.X:0.00}, {fileLaunch.Y:0.00}, {fileLaunch.Z:0.00}) "
                        + $"{ed.Speed:0.00}/f lock {ed.ControlLockTime} | tuned ({c.Launch[0]:0.00}, {c.Launch[1]:0.00}, {c.Launch[2]:0.00}) lock {c.LockFrames}, "
                        + $"pad ({ed.Position.X:0.0}, {ed.Position.Y:0.0}, {ed.Position.Z:0.0}) target ({c.Target[0]:0.0}, {c.Target[1]:0.0}, {c.Target[2]:0.0})");
                    foreach (bool hold in new[] { false, true })
                    {
                        Console.WriteLine($"    {(hold ? "stick held toward the target" : "stick let go on launch      ")}: {WalkOn(host, ed.Position, c.TargetV, info.KillHeight, hold)}");
                    }
                }
            }
        }

        static string WalkOn(MphRecomp.Campaign.CampaignHost host, OpenTK.Mathematics.Vector3 pad, OpenTK.Mathematics.Vector3 target, float killHeight, bool hold)
        {
            PlayerEntity me = PlayerEntity.Main;
            var idle = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
            var press = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Buttons = MphRecomp.Campaign.CampaignButtons.Shoot | MphRecomp.Campaign.CampaignButtons.Jump };
            var walk = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(0, 1) };
            for (int f = 0; f < 900 && me.Health == 0; f++) host.Step(f % 2 == 0 ? press : idle);   // respawn after a fall
            for (int f = 0; f < 60 && me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad); f++) host.Step(idle);   // the last flight's push ends on landing
            var dir =new OpenTK.Mathematics.Vector3(target.X - pad.X, 0, target.Z - pad.Z).Normalized();
            OpenTK.Mathematics.Vector3 start = pad - dir * 3 + OpenTK.Mathematics.Vector3.UnitY * 0.3f;
            me.Reposition(start - me.Position, me.NodeRef);
            me.Reposition(me.Position, dir, me.NodeRef);
            me.Speed = OpenTK.Mathematics.Vector3.Zero;
            for (int f = 0; f < 30; f++) host.Step(idle);
            if (MathF.Abs(me.Position.Y - pad.Y) > 1 || me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad))
            {
                return $"no clean run-up 3 units back (at ({me.Position.X:0.0}, {me.Position.Y:0.0}, {me.Position.Z:0.0}))";
            }
            int walked = 0;
            for (; walked < 120 && !me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad); walked++) host.Step(walk);
            if (!me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)) return $"never launched (walked to ({me.Position.X:0.0}, {me.Position.Y:0.0}, {me.Position.Z:0.0}))";
            OpenTK.Mathematics.Vector3 from = me.Position;
            float apex = 0, far = 0;
            int frames = 0;
            string end = "still flying after 20 s";
            for (; frames < 1200; frames++)
            {
                host.Step(hold ? walk : idle);
                apex = MathF.Max(apex, me.Position.Y - from.Y);
                far = MathF.Max(far, (me.Position.Xz - from.Xz).Length);
                if (me.Health == 0 || me.Position.Y < killHeight) { end = "DIED"; break; }
                if (frames > 8 && me.Flags1.TestFlag(PlayerFlags1.Standing) && MathF.Abs(me.Speed.Y) < 0.05f) { end = "landed"; break; }
            }
            OpenTK.Mathematics.Vector3 p = me.Position;
            float across = (p.Xz - target.Xz).Length;
            return $"{end} at ({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}) after {frames} f: {across:0.0} across from the target, dy {p.Y - target.Y - 0.5f:+0.0;-0.0}, "
                + $"rose {apex:0.0}, went {far:0.0} out (pad->target {(target.Xz - from.Xz).Length:0.0})";
        }

        // the room model as MphRead loads it (its own reader, display lists decoded), drawn in software from the first
        // spawn at eye height: proves the DS stand-in end to end without a device
        static void Preview(EchoesArena.Def d, string png)
        {
            Model model = Read.GetRoomModelInstance(d.Room).Model;
            var tris = new System.Collections.Generic.List<(OpenTK.Mathematics.Vector3 P, OpenTK.Mathematics.Vector3 C)>();
            foreach (Mesh mesh in model.Meshes)
            {
                var color = OpenTK.Mathematics.Vector3.One;
                foreach (RenderInstruction ins in model.RenderInstructionLists[mesh.DlistId])
                {
                    if (ins.Code == InstructionCode.COLOR)
                    {
                        uint c = ins.Arguments[0];
                        color = new OpenTK.Mathematics.Vector3(c & 31, c >> 5 & 31, c >> 10 & 31) / 31f;
                    }
                    else if (ins.Code == InstructionCode.VTX_16)
                    {
                        static float S(uint v) => (short)(ushort)v / 4096f;
                        tris.Add((new OpenTK.Mathematics.Vector3(S(ins.Arguments[0]), S(ins.Arguments[0] >> 16), S(ins.Arguments[1])) * model.Scale, color));
                    }
                }
            }
            var spawn = Read.GetEntities(Metadata.GetRoomByName(d.Room).Item1!.EntityPath!, 0, firstHunt: false)
                .First(e => e.Type == EntityType.PlayerSpawn);
            var eye = spawn.Position + OpenTK.Mathematics.Vector3.UnitY * 1.4f;
            var fwd = spawn.FacingVector;
            var view = OpenTK.Mathematics.Matrix4.LookAt(eye, eye + fwd, OpenTK.Mathematics.Vector3.UnitY);
            const int W = 960, H = 540;
            var proj = OpenTK.Mathematics.Matrix4.CreatePerspectiveFieldOfView(OpenTK.Mathematics.MathHelper.DegreesToRadians(70), W / (float)H, 0.1f, 1000);
            var vp = view * proj;
            var depth = new float[W * H];
            Array.Fill(depth, float.MaxValue);
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(W, H, new SixLabors.ImageSharp.PixelFormats.Rgba32(40, 44, 52, 255));
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                var sp = new (float X, float Y, float Z)[3];
                bool behind = false;
                for (int k = 0; k < 3; k++)
                {
                    var c = new OpenTK.Mathematics.Vector4(tris[t + k].P, 1) * vp;
                    if (c.W < 0.1f) { behind = true; break; }
                    sp[k] = ((c.X / c.W * 0.5f + 0.5f) * W, (0.5f - c.Y / c.W * 0.5f) * H, c.W);
                }
                if (behind) continue;
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sp[0].X, MathF.Min(sp[1].X, sp[2].X))));
                int x1 = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(sp[0].X, MathF.Max(sp[1].X, sp[2].X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sp[0].Y, MathF.Min(sp[1].Y, sp[2].Y))));
                int y1 = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(sp[0].Y, MathF.Max(sp[1].Y, sp[2].Y))));
                float area = (sp[1].X - sp[0].X) * (sp[2].Y - sp[0].Y) - (sp[2].X - sp[0].X) * (sp[1].Y - sp[0].Y);
                if (MathF.Abs(area) < 1e-6f) continue;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float w0 = ((sp[1].X - px) * (sp[2].Y - py) - (sp[2].X - px) * (sp[1].Y - py)) / area;
                        float w1 = ((sp[2].X - px) * (sp[0].Y - py) - (sp[0].X - px) * (sp[2].Y - py)) / area;
                        float w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        float z = w0 * sp[0].Z + w1 * sp[1].Z + w2 * sp[2].Z;
                        if (z >= depth[y * W + x]) continue;
                        depth[y * W + x] = z;
                        var col = tris[t].C * w0 + tris[t + 1].C * w1 + tris[t + 2].C * w2;
                        img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgba32(col.X, col.Y, col.Z, 1);
                    }
                }
            }
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, png);
        }

        static string? Arg(string[] args, string key) =>
            args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..];
    }
}
