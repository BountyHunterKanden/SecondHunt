using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Sound;
using MphRead.Sound;
using MphRecomp.Campaign;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace MphRead
{
    // -landingperf [room=UNIT2_LAND] [steps=240]: owner queue #37b (one ~0.2 s dip right after skipping the CALanding movie
    // on the Odin, 10-04 17:25: the room's first draw uploading 114 meshes + 31 textures, then sim 71 ms in 3 steps, then
    // 79 more meshes + 31 textures). Hosts the landing movie the way the Android campaign does (Scene.HostPlaysMovies: no
    // steps while it plays, SkipMovie hands back), skips it at once, then times each step and prints, per step: the camera
    // sequence, the fade, music calls, sounds started (a sample's FIRST use = the Odin's SFX host decodes it on the sim
    // thread: timed here with MphRead's own decode), and what a renderer would upload on its first draw of the items
    // (new mesh list ids, new or re-versioned textures).
    internal static partial class CampaignSim
    {
        private sealed class MusicProbe : MusicPlayer.IHost
        {
            public readonly List<string> Calls = new();
            public void Load(SeqId seqId, ushort tracks, float volume, Action loaded)
            {
                Calls.Add($"Load({seqId})");
                loaded();
            }
            public void Play(float volume) => Calls.Add("Play");
            public void Pause() => Calls.Add("Pause");
            public void Stop() => Calls.Add("Stop");
            public SoundFlow.Enums.PlaybackState State { get; private set; } = SoundFlow.Enums.PlaybackState.Stopped;
            public float Volume { get; set; } = 1;
            public ushort Tracks { get; set; } = 0xFFFF;
            public ushort Tempo { get; set; }
            public NCSFCommon.Track? GetTrack(int index) => null;
        }

        // what GameAudio's AndroidSfxPlayer would decode on the sim thread (GetPcm: once per sample id, for a sample, every
        // entry of a DGN, every sample entry of a script), timed with the same decode (SoundSample.WaveData)
        private sealed class SfxProbe : SfxInstanceBase
        {
            private readonly IReadOnlyList<SoundSample> _samples = SoundRead.ReadSoundSamples();
            private readonly IReadOnlyList<DgnFile> _dgn = SoundRead.ReadDgnFiles();
            private readonly IReadOnlyList<SfxScriptFile> _scripts = SoundRead.ReadSfxScriptFiles();
            private readonly HashSet<int> _decoded = new();
            public readonly List<string> Started = new();
            public double DecodeMs;
            public int Decodes;

            private void Decode(int sampleId)
            {
                if (sampleId < 0 || sampleId >= _samples.Count || !_decoded.Add(sampleId))
                {
                    return;
                }
                long t = Stopwatch.GetTimestamp();
                int bytes = _samples[sampleId].WaveData.Value.Length;
                double ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                DecodeMs += ms;
                Decodes++;
                Started.Add($"      decode sample {sampleId} ({bytes / 1024} KB PCM) {ms:0.00} ms");
            }

            public override int PlaySample(int id, SoundSource? source, bool? loop, bool noUpdate,
                float recency, bool sourceOnly, bool cancellable)
            {
                Started.Add($"    sample {id} {(SfxId)id}");
                Decode(id);
                return -1;
            }

            public override void PlayDgn(int id, SoundSource? source, bool loop, bool noUpdate,
                float recency, bool cancellable, float amountA, float amountB)
            {
                int dgnId = id & 0x3FFF;
                Started.Add($"    dgn {dgnId}");
                if (dgnId < _dgn.Count)
                {
                    foreach (DgnFileEntry e in _dgn[dgnId].Entries)
                    {
                        Decode((int)e.SfxId);
                    }
                }
            }

            public override void PlayScript(int id, SoundSource? source, bool noUpdate,
                float recency, bool sourceOnly, bool cancellable)
            {
                int scriptId = id & 0x3FFF;
                Started.Add($"    script {scriptId}");
                if (scriptId < _scripts.Count)
                {
                    foreach (SfxScriptEntry e in _scripts[scriptId].Entries)
                    {
                        if ((e.SfxData & 0x8000) == 0)
                        {
                            Decode(e.SfxData & 0x3FFF);
                        }
                    }
                }
            }
        }

        // "jit": the methods the runtime JIT-compiles during each slow step (MethodLoadVerbose events), grouped by type --
        // first-run code is JIT-compiled on the Odin too (Release = profiled AOT, which covers start-up only)
        private sealed class JitListener : System.Diagnostics.Tracing.EventListener
        {
            public readonly List<(DateTime Time, string Method)> Methods = new();

            protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
            {
                if (source.Name == "Microsoft-Windows-DotNETRuntime")
                {
                    EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x10);
                }
            }

            protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
            {
                if (e.EventName != null && e.EventName.StartsWith("MethodLoadVerbose") && e.Payload != null && e.PayloadNames != null)
                {
                    string ns = e.Payload[e.PayloadNames.IndexOf("MethodNamespace")]?.ToString() ?? "";
                    string name = e.Payload[e.PayloadNames.IndexOf("MethodName")]?.ToString() ?? "";
                    lock (Methods)
                    {
                        Methods.Add((e.TimeStamp.ToUniversalTime(), ns + "::" + name));
                    }
                }
            }
        }

        public static void LandingPerf(string[] args)
        {
            using JitListener? jit = args.Contains("jit") ? new JitListener() : null;
            var stepTimes = new List<(int Frame, DateTime Start, DateTime End, double Ms)>();
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 && !args[1].All(Char.IsDigit) ? args[1] : "UNIT2_LAND";
            int after = args.Skip(1).FirstOrDefault(a => a.All(Char.IsDigit)) is string n ? Int32.Parse(n) : 240;
            // as CampaignRenderer sets them
            Scene.HostPlaysMovies = true;
            Scene.HostPredecodeTextures = true;
            RoomEntity.HostTransitionGc = false;
            var music = new MusicProbe();
            MusicPlayer.IHost? musicBefore = MusicPlayer.Host;
            MusicPlayer.Host = music;
            int frame = 0;
            Scene.HostEntityInitTrace = msg => Console.WriteLine($"  f{frame,5}   entity set-up: {msg}");
            var sfx = new SfxProbe();
            try
            {
                var start = Stopwatch.StartNew();
                using CampaignHost host = CampaignHost.Start(room, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
                Scene scene = host.Scene;
                Console.WriteLine($"  {room} started in {start.ElapsedMilliseconds} ms; music calls so far: {String.Join(", ", music.Calls)}");
                music.Calls.Clear();
                var meshesSeen = new HashSet<int>();
                var texSeen = new Dictionary<int, int>();
                int serial = 0, stepsAfter = -1;
                string lastState = "";
                double total = 0, worst = 0;
                while (frame < 3000 && !host.Ended && (stepsAfter < 0 || stepsAfter < after))
                {
                    if (scene.MoviePlaying && scene.HostMovieSerial != serial)
                    {
                        serial = scene.HostMovieSerial;
                        Console.WriteLine($"  f{frame,5} movie {scene.HostMovie} starts: skipped at once (the Odin holds the sim meanwhile); "
                            + $"registered so far {scene.HostMeshes.Count} meshes ({scene.HostMeshes.Count(m => m.IsRoom)} room), "
                            + $"{scene.HostTextures.Count} textures ({scene.HostTextures.Values.Sum(x => (long)x.Width * x.Height) * 4 / 1024} KB)");
                        scene.SkipMovie();
                        stepsAfter = 0;
                    }
                    if (Sfx.Instance != sfx)
                    {
                        Sfx.SetHost(sfx); // a room load puts the silent instance back (CampaignRenderer.ReassertSfxHost)
                    }
                    int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
                    int registered = scene.HostMeshes.Count, texRegistered = scene.HostTextures.Count;
                    double decodeBefore = sfx.DecodeMs;
                    DateTime wallStart = DateTime.UtcNow;
                    long t = Stopwatch.GetTimestamp();
                    host.Step(default);
                    double ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                    if (stepsAfter >= 0 && ms > 3)
                    {
                        stepTimes.Add((frame, wallStart, DateTime.UtcNow, ms));
                    }
                    double decodeMs = sfx.DecodeMs - decodeBefore;
                    // a renderer's first draw of these items: mesh lists and textures it hasn't uploaded yet
                    int newMeshes = 0, newTex = 0, reTex = 0;
                    bool drawing = !scene.MoviePlaying;
                    if (drawing)
                    {
                        foreach (IReadOnlyList<RenderItem> list in new[] { scene.OpaqueItems, scene.DecalItems, scene.TranslucentItems })
                        {
                            foreach (RenderItem item in list)
                            {
                                if (item.ListId > 0 && meshesSeen.Add(item.ListId))
                                {
                                    newMeshes++;
                                }
                                if (item.HasTexture && scene.HostTextures.TryGetValue(item.TextureBindingId, out Scene.HostTexture? tex))
                                {
                                    if (!texSeen.TryGetValue(item.TextureBindingId, out int version))
                                    {
                                        newTex++;
                                    }
                                    else if (version != tex.Version)
                                    {
                                        reTex++;
                                    }
                                    texSeen[item.TextureBindingId] = tex.Version;
                                }
                            }
                        }
                    }
                    CameraSequence? seq = CameraSequence.Current;
                    string state = $"camseq {(seq == null ? "none" : $"{seq.SequenceId} {seq.Name}")}, fade {scene.FadeType}, "
                        + $"movie {(scene.MoviePlaying ? "on" : "off")}, music {Music.CurrentSeq}";
                    bool interesting = stepsAfter >= 0 && (ms > 1.5 || newMeshes + newTex + reTex > 0 || sfx.Started.Count > 0
                        || music.Calls.Count > 0 || state != lastState || GC.CollectionCount(0) != gc0);
                    if (interesting)
                    {
                        Console.WriteLine($"  f{frame,5} (+{stepsAfter,3}) step {ms,6:0.00} ms (sfx decode {decodeMs:0.00}); {state}; "
                            + $"draw-new meshes {newMeshes}, textures {newTex} new + {reTex} re-versioned; registered meshes "
                            + $"+{scene.HostMeshes.Count - registered}, textures +{scene.HostTextures.Count - texRegistered}; "
                            + $"gc0 +{GC.CollectionCount(0) - gc0}, gc2 +{GC.CollectionCount(2) - gc2}"
                            + (music.Calls.Count > 0 ? $"; music: {String.Join(", ", music.Calls)}" : ""));
                        foreach (string s in sfx.Started)
                        {
                            Console.WriteLine(s);
                        }
                    }
                    lastState = state;
                    music.Calls.Clear();
                    sfx.Started.Clear();
                    if (stepsAfter >= 0)
                    {
                        total += ms;
                        worst = Math.Max(worst, ms);
                        stepsAfter++;
                    }
                    frame++;
                }
                Console.WriteLine($"  {stepsAfter} steps after the movie: {total:0.0} ms total, worst {worst:0.0} ms; "
                    + $"{sfx.Decodes} first-use sample decodes, {sfx.DecodeMs:0.0} ms; {meshesSeen.Count} meshes, {texSeen.Count} textures drawn");
                if (jit != null)
                {
                    System.Threading.Thread.Sleep(1500); // the runtime's events reach the listener in batches
                    foreach ((int f, DateTime from, DateTime to, double ms) in stepTimes)
                    {
                        List<string> methods;
                        lock (jit.Methods)
                        {
                            methods = jit.Methods.Where(m => m.Time >= from && m.Time <= to).Select(m => m.Method).ToList();
                        }
                        Console.WriteLine($"  f{f,5}: step {ms:0.0} ms, {methods.Count} methods JIT-compiled; by type:");
                        foreach (var g in methods.GroupBy(m => m.Split("::")[0]).OrderByDescending(g => g.Count()).Take(25))
                        {
                            Console.WriteLine($"    {g.Count(),4} {g.Key}: {String.Join(", ", g.Select(m => m.Split("::")[1]).Distinct().Take(8))}");
                        }
                    }
                }
            }
            finally
            {
                Scene.HostPlaysMovies = false;
                Scene.HostPredecodeTextures = false;
                RoomEntity.HostTransitionGc = true;
                Scene.HostEntityInitTrace = null;
                MusicPlayer.Host = musicBefore;
            }
        }
    }
}
