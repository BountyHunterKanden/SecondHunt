using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MphRecomp.Campaign;

namespace MphRecomp.Replay
{
    // Thread B (instant seek, steps 2-4): a replay's prepared keyframes. Another process (Android: ReplayPrepService in
    // its own ':replayprep' process; desktop: the app started again with --replayprep) plays the replay headless and
    // writes portable keyframes (ReplayKeyframe.cs) to a sidecar file; the viewer seeks by loading the nearest one and
    // playing at most Every steps on, instead of re-running the match from a checkpoint it has already passed.
    //   <data>/replays/keyframes/<key>.mphkf   (key = this build + the replay's start; another build prepares its own)
    // Layout: a whole keyframe every WholeEvery steps (the anchor) and a delta from that anchor every Every steps between,
    // so a seek is one whole load + at most one delta + at most Every steps. Anything refused (a type the writer can't
    // write, a keyframe the viewer's match refuses, a check that fails after a load) falls back to the old checkpoints.
    public static class ReplayPrep
    {
        public const int Every = 60;
        public const int WholeEvery = 600;
        // shorter replays seek fast enough from checkpoints: not prepared
        public const int MinSteps = 600;
        public const string Extension = ".mphkf";

        public static string Dir(string dataDir) => Path.Combine(dataDir, "replays", "keyframes");

        // this build's code: keyframes name types and field layouts, so only the build that wrote them reads them
        public static string BuildId { get; } = string.Join("+", typeof(MphRead.Scene).Module.ModuleVersionId.ToString("N")[..12],
            typeof(ReplayPrep).Module.ModuleVersionId.ToString("N")[..12]);

        // the replay's identity: how it started and its first steps (a live match's feed has these after 5 s)
        public static string Key(ReplayFile file)
        {
            ulong h = 14695981039346656037UL;
            void Add(ReadOnlySpan<byte> b)
            {
                foreach (byte x in b) h = (h ^ x) * 1099511628211UL;
            }
            Add(Encoding.UTF8.GetBytes(BuildId));
            Add(Encoding.UTF8.GetBytes(file.Build));
            Add(BitConverter.GetBytes(file.Rng1));
            Add(BitConverter.GetBytes(file.Rng2));
            Add(BitConverter.GetBytes(file.AiAspect));
            Add(Encoding.UTF8.GetBytes(file.Data));
            Add(Encoding.UTF8.GetBytes(file.Settings.Arena + "/" + file.Settings.Mode + "/" + file.Settings.Players.Count));
            int n = Math.Min(KeySteps, file.Steps.Count);
            for (int i = 0; i < n; i++) Add(BitConverter.GetBytes(file.Steps[i].Checksum));
            return h.ToString("x16");
        }

        public const int KeySteps = 300;

        // all the replay's step checksums: a finished sidecar records it, as the key (its first KeySteps only, so a live
        // match has it early) can be shared by matches that only part later (bot matches from the same start)
        public static ulong StepsHash(ReplayFile file) => PrefixHash(file, file.Steps.Count);

        public const ulong HashSeed = 14695981039346656037UL;

        public static ulong HashStep(ulong h, uint checksum)
        {
            for (int i = 0; i < 4; i++) h = (h ^ (byte)(checksum >> (i * 8))) * 1099511628211UL;
            return h;
        }

        // the hash of the first `steps` step checksums: a keyframe at step k carries it for k (sidecar v4), so it fits every
        // replay whose first k steps are these (a guest's file cut short as it left, a rejoined stream, the whole match),
        // not only the one file it was prepared from (owner 11-22 "everyone in every case"; before, a sidecar fitted one
        // file exactly and any other with its key was prepared again from step 0)
        public static ulong PrefixHash(ReplayFile file, int steps)
        {
            ulong h = HashSeed;
            for (int i = 0; i < steps; i++) h = HashStep(h, file.Steps[i].Checksum);
            return h;
        }

        // [k] = PrefixHash(file, k * Every), k = 0 .. Steps.Count / Every
        public static ulong[] PrefixHashes(ReplayFile file)
        {
            int n = file.Steps.Count;
            var o = new ulong[n / Every + 1];
            ulong h = HashSeed;
            o[0] = h;
            for (int i = 0; i < n; i++)
            {
                h = HashStep(h, file.Steps[i].Checksum);
                if ((i + 1) % Every == 0) o[(i + 1) / Every] = h;
            }
            return o;
        }

        public static string SidecarPath(string dataDir, ReplayFile file) => Path.Combine(Dir(dataDir), Key(file) + Extension);

        // not a modded match (format 75 Mods): its gameplay mods' Lua state isn't in a keyframe, so it's only ever played
        // from the start with the mods loaded (docs/MOD_API.md �9)
        public static bool Worth(ReplayFile file) => file.Steps.Count >= MinSteps && file.Mods.Count == 0;

        // whether a prepare should start for the sidecar at `path`: not when its keyframes already cover `file` (given: the
        // saved replay) or another process is writing it (its .lock held: the viewer reads along; false then, see Busy).
        // `live`: a match just begun, whose feed writes this sidecar: always prepared (from its last keyframe that fits).
        // Nothing is deleted (v4, owner 11-22): the sidecar only grows, each keyframe known by its step and prefix hash, so
        // the prepare goes on from what an earlier one left (a guest that left and came back, an app restarted mid-match,
        // a cut-short file) and a reader already in it is never pulled from under
        public static bool NeedsPrep(string path, ReplayFile? file = null, bool live = false)
        {
            try
            {
                using (KeyframeSidecar? s = KeyframeSidecar.Open(path))
                {
                    if (!live && s != null && (file != null ? s.Covers(file) : s.Complete)) return false;
                }
                if (!File.Exists(path)) return true;
                return !Busy(path);
            }
            catch (IOException)
            {
                return false; // (being prepared)
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        // another process is preparing the sidecar at `path` (its .lock held)
        public static bool Busy(string path)
        {
            try
            {
                using (new FileStream(Path.ChangeExtension(path, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        // a live match's prepare, started now or, while another process still holds the sidecar (an earlier part of the
        // same match: the stream before a leave, the app before a restart, a cut-short file's prepare), as soon as that one
        // lets go: it then goes on from the keyframes that one wrote (owner 11-22: the rejoined guest got none before)
        public static void StartLive(string sidecar, Action start, Action<string>? log = null, int waitMs = ReplayFeed.StallMs + 15000)
        {
            if (!Busy(sidecar))
            {
                start();
                return;
            }
            log?.Invoke("keyframes: an earlier prepare of this match is finishing; this one goes on from it when it's done");
            System.Threading.Tasks.Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < waitMs)
                {
                    await System.Threading.Tasks.Task.Delay(250);
                    if (Busy(sidecar)) continue;
                    try
                    {
                        start();
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke("no live keyframes for this match: " + ex.Message);
                    }
                    return;
                }
                log?.Invoke($"no live keyframes for this match: the earlier prepare held it {waitMs / 1000} s");
            });
        }

        // the sidecars kept, all told (~4 MB a match-minute: about 50 minutes of matches)
        public const long KeepBytes = 200L << 20;

        // old sidecars go: past KeepBytes, the least recently written first (another build's are never read again)
        public static void Prune(string dataDir, long keepBytes = KeepBytes)
        {
            try
            {
                string dir = Dir(dataDir);
                if (!Directory.Exists(dir)) return;
                long total = 0;
                foreach (FileInfo f in new DirectoryInfo(dir).GetFiles("*" + Extension).OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    total += f.Length;
                    if (total <= keepBytes) continue;
                    try
                    {
                        f.Delete();
                        File.Delete(Path.ChangeExtension(f.FullName, ".lock"));
                        File.Delete(ReplayKeyframes.WalkPath(f.FullName));
                        File.Delete(f.FullName + ".walk-viewer");
                    }
                    catch (IOException)
                    {
                        // (being written or read: next time)
                    }
                }
                // a feed whose prepare never ran or stopped (the app closed mid-match): its files after a day
                string live = ReplayFeed.Dir(dataDir);
                if (Directory.Exists(live))
                {
                    foreach (FileInfo f in new DirectoryInfo(live).GetFiles())
                    {
                        if (f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)) continue;
                        try { f.Delete(); } catch (IOException) { }
                    }
                }
            }
            catch (Exception)
            {
                // (a sidecar is only a speed-up)
            }
        }

        public static byte[] Compress(byte[] data)
        {
            var o = new MemoryStream(data.Length / 4 + 64);
            using (var b = new BrotliStream(o, CompressionLevel.Fastest, leaveOpen: true)) b.Write(data);
            return o.ToArray();
        }

        public static byte[] Decompress(byte[] data, int rawLength)
        {
            var o = new byte[rawLength];
            using var b = new BrotliStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
            int at = 0;
            while (at < rawLength)
            {
                int n = b.Read(o, at, rawLength - at);
                if (n <= 0) throw new InvalidDataException("short keyframe");
                at += n;
            }
            return o;
        }

        // Plays `file` headless and writes its sidecar at `path`. `more` (a live match's feed): called when the steps run
        // out; adds steps to file.Steps (and its NetEvents) and returns false once the match has ended. `cancel`: stop
        // (the sidecar keeps what it has; the viewer uses those and checkpoints past them). null = done; else why it
        // stopped early. One process prepares a sidecar at a time (its .lock file).
        public static string? Run(ReplayFile file, string path, Func<bool>? cancel = null, Func<bool>? more = null,
            Action<string>? log = null)
        {
            string? why = ReplayPlayer.CheckBeforeStart(file);
            if (why != null) return "refused: " + why;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FileStream lockFile;
            try
            {
                lockFile = new FileStream(Path.ChangeExtension(path, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return "another process is preparing it";
            }
            using (lockFile)
            {
                if (more == null && KeyframeSidecar.Open(path) is KeyframeSidecar old)
                {
                    bool done = old.Covers(file);
                    old.Dispose();
                    if (done) return null;
                }
                string? first = RunLocked(file, path, cancel, more, log, resume: true, out bool resumed);
                // (a resumed prepare whose steps stop matching: once more from step 0, so a bad resume never sticks)
                if (resumed && first != null && first.StartsWith("differs", StringComparison.Ordinal))
                {
                    log?.Invoke($"the resumed prepare stopped ({first}): from step 0");
                    return RunLocked(file, path, cancel, more, log, resume: false, out _);
                }
                return first;
            }
        }

        // the latest whole keyframe of `old` that fits `file` (-1: none), its records' walk this host's
        static int ResumeStep(KeyframeSidecar old, SimSnapshot.KeyframeBase kbase, ReplayFile file, Func<bool>? more)
        {
            if (old.ObjectCount != kbase.ObjectCount || old.LeafCount != kbase.LeafCount || old.Signature != kbase.Signature) return -1;
            int want = old.LastWholeStep;
            if (want <= 0) return -1;
            // (a live feed: the steps up to it first -- a rejoined stream's catch-up comes in seconds)
            while (more != null && file.Steps.Count < want && more()) { }
            return old.LastWhole(PrefixHashes(file));
        }

        // (Run, holding the .lock) `resume`: go on from the sidecar's latest whole keyframe that fits the file, appending
        static string? RunLocked(ReplayFile file, string path, Func<bool>? cancel, Func<bool>? more, Action<string>? log,
            bool resume, out bool resumed)
        {
            resumed = false;
            {
                var player = new ReplayPlayer(file);
                CampaignHost host = player.Start(collectDrawItems: false, viewWidth: 256, viewHeight: 192);
                try
                {
                    if (player.CheckAfterStart() is string after) return "refused: " + after;
                    var kbase = ReplayCheckpoints.KeyframeBase(host);
                    var writer = new SimSnapshot.KeyframeWriter(kbase);
                    SimSnapshot prev = kbase.Snap;
                    SimSnapshot? anchor = null;
                    KeyframeSidecar? sidecarOrNull = null;
                    if (resume && KeyframeSidecar.Open(path) is KeyframeSidecar old)
                    {
                        using (old)
                        {
                            int at = ResumeStep(old, kbase, file, more);
                            if (at > 0)
                            {
                                ulong[] pre = PrefixHashes(file);
                                string? refused = new SimSnapshot.KeyframeLoader(kbase).Load(old.Read(at, pre));
                                if (refused != null)
                                {
                                    resumed = true; // (the caller runs it again from step 0, once this host is gone)
                                    return $"differs: the keyframe at step {at} didn't load ({refused})";
                                }
                                player.Rewind(at);
                                resumed = true;
                                log?.Invoke($"going on from the keyframe at step {at} (of {old.Count} prepared)");
                            }
                        }
                        if (resumed) sidecarOrNull = KeyframeSidecar.Append(path);
                    }
                    if (sidecarOrNull == null)
                    {
                        ReplayKeyframes.WriteWalk(path, kbase);
                        sidecarOrNull = KeyframeSidecar.Create(path, kbase);
                    }
                    using KeyframeSidecar sidecar = sidecarOrNull;
                    var sw = new Stopwatch();
                    double work = 0;
                    long bytes = 0;
                    int written = 0;
                    ulong prefix = PrefixHash(file, player.Position);
                    if (resumed)
                    {
                        // (its own whole keyframe at the step first: the deltas after are of this process's walk of it)
                        int pos0 = player.Position;
                        SimSnapshot snap = SimSnapshot.Take(pos0, ReplayCheckpoints.Roots(host), prev);
                        byte[] raw = writer.Write(snap, null);
                        if (writer.Refused.Count > 0) return "can't write " + string.Join(", ", writer.Refused);
                        byte[] packed = Compress(raw);
                        sidecar.Append(pos0, -1, raw.Length, packed, prefix);
                        bytes += packed.Length;
                        written++;
                        anchor = snap;
                        prev = snap;
                    }
                    while (true)
                    {
                        if (cancel?.Invoke() == true) return "cancelled";
                        if (host.Ended) break;
                        if (player.Finished)
                        {
                            if (more == null || !more()) break;
                            if (player.Finished) continue;
                        }
                        if (!player.TryNext(out CampaignInput input)) break;
                        sw.Restart();
                        host.Step(input);
                        if (!player.Check(host.Scene)) return $"differs from the recording at step {player.FailedAt}";
                        int pos = player.Position;
                        prefix = HashStep(prefix, file.Steps[pos - 1].Checksum);
                        if (pos % Every == 0)
                        {
                            SimSnapshot snap = SimSnapshot.Take(pos, ReplayCheckpoints.Roots(host), prev);
                            bool whole = anchor == null || pos % WholeEvery == 0;
                            byte[] raw = writer.Write(snap, whole ? null : anchor);
                            if (writer.Refused.Count > 0) return "can't write " + string.Join(", ", writer.Refused);
                            byte[] packed = Compress(raw);
                            sidecar.Append(pos, whole ? -1 : anchor!.Step, raw.Length, packed, prefix);
                            bytes += packed.Length;
                            written++;
                            if (whole) anchor = snap;
                            prev = snap;
                        }
                        work += sw.Elapsed.TotalMilliseconds;
                    }
                    sidecar.Finish(file.Steps.Count, StepsHash(file));
                    log?.Invoke($"prepared {written} keyframes to step {player.Position}: {bytes / 1024} KB, {work / 1000:0.0} s of work "
                        + $"({work / Math.Max(1, player.Position):0.00} ms a step)");
                    return null;
                }
                finally
                {
                    host.Dispose();
                }
            }
        }
    }

    // Step 3, the live feed (PREPARE REPLAYS: DURING MATCH): a match's recording handed to the prepare as it is played,
    // so its keyframes are ready about when the match ends. The recorder writes the replay so far once it has the key's
    // steps, then appends the new steps about once a second; the prepare follows them (Follow, ReplayPrep.Run's `more`).
    //   <data>/replays/keyframes/live/<key>.mphreplay   the recording at its KeySteps (a normal replay file)
    //   <key>.steps   every later step and network event, in order: a step = StepTag + StepBytes; a network match's event
    //                 (what its host applied to a remote seat, ReplayNetEvent) = EventTag, int step, u8 seat, u8 kind,
    //                 u16 length (0xFFFF = none) + that many bytes. A flush writes its new events before its new steps, so
    //                 the prepare has a step's events before it plays it.
    //   <key>.end     the match is over (the steps are all there)
    // Only between two processes of this build, so the step layout is its own (a missed field makes the prepare's check
    // fail, which only leaves the seeks to checkpoints). A network match's host feeds its events too (2026-11-15: before
    // that an online replay opened just after its match seeked slowly until the after-match prepare was done).
    public sealed class ReplayFeed : IDisposable
    {
        public const int StepBytes = 37;
        internal const byte StepTag = 0, EventTag = 1;
        internal const int EventHead = 1 + 4 + 1 + 1 + 2;
        // the prepare gives up on a feed that hasn't grown for this long (the recording app was closed)
        public const int StallMs = 30000;

        public static string Dir(string dataDir) => Path.Combine(ReplayPrep.Dir(dataDir), "live");

        static string StepsPath(string replayPath) => Path.ChangeExtension(replayPath, ".steps");
        static string EndPath(string replayPath) => Path.ChangeExtension(replayPath, ".end");

        public static bool IsLive(string replayPath) => File.Exists(StepsPath(replayPath));

        readonly ReplayFile _file;
        readonly string _dataDir;
        FileStream? _steps;
        int _written;
        int _eventsWritten;
        bool _ended;
        readonly MemoryStream _buffer = new(StepBytes * ReplayPrep.Every * 2);

        // (the begun feed's files, for ReplayPrepHost.Start)
        public string? ReplayPath { get; private set; }
        public string? Sidecar { get; private set; }

        public ReplayFeed(ReplayFile file, string dataDir)
        {
            _file = file;
            _dataDir = dataDir;
        }

        // after each recorded step: true once, when the feed has begun (its files are written: start the prepare)
        public bool Step()
        {
            if (_ended) return false;
            int n = _file.Steps.Count;
            if (_steps == null)
            {
                if (n < ReplayPrep.KeySteps) return false;
                string dir = Dir(_dataDir);
                Directory.CreateDirectory(dir);
                string key = ReplayPrep.Key(_file);
                // (owner 11-22 "everyone in every case": a feed's files are its own. A guest that left and rejoined, or
                // whose app restarted mid-match, feeds the same match again; the earlier feed's prepare (still running in
                // its own process) read the new files rewritten under it and stopped ("differs"), and held the lock so
                // the new one never started. Now the earlier feed is marked ended -- its prepare finishes what it has --
                // and the new prepare goes on from its keyframes, ReplayPrep.StartLive.)
                EndOthers(dir, key);
                string path = Path.Combine(dir, $"{key}-{DateTime.UtcNow.Ticks:x}{ReplayLibrary.Extension}");
                string temp = path + ".part";
                _file.Save(temp);
                File.Delete(EndPath(path));
                _steps = new FileStream(StepsPath(path), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                File.Move(temp, path, overwrite: true);
                _written = n;
                _eventsWritten = _file.NetEvents.Count;
                ReplayPath = path;
                Sidecar = ReplayPrep.SidecarPath(_dataDir, _file);
                return true;
            }
            if (n - _written >= ReplayPrep.Every) Flush();
            return false;
        }

        // the end mark on every other feed of this key still going (its recording app gone, or an earlier stream of the match)
        static void EndOthers(string dir, string key)
        {
            try
            {
                foreach (string steps in Directory.GetFiles(dir, key + "*.steps"))
                {
                    string replay = Path.ChangeExtension(steps, ReplayLibrary.Extension);
                    if (File.Exists(EndPath(replay))) continue;
                    File.WriteAllText(EndPath(replay), "-1");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // (it stalls out by itself, StallMs)
            }
        }

        void Flush()
        {
            if (_steps == null) return;
            int n = _file.Steps.Count;
            int events = _file.NetEvents.Count;
            if (n <= _written && events <= _eventsWritten) return;
            _buffer.SetLength(0);
            using (var w = new BinaryWriter(_buffer, Encoding.UTF8, leaveOpen: true))
            {
                for (int i = _eventsWritten; i < events; i++) WriteEvent(w, _file.NetEvents[i]);
                for (int i = _written; i < n; i++)
                {
                    w.Write(StepTag);
                    WriteStep(w, _file.Steps[i]);
                }
            }
            _steps.Write(_buffer.GetBuffer(), 0, (int)_buffer.Length);
            _steps.Flush();
            _written = n;
            _eventsWritten = events;
        }

        // the match is over (or left): the last steps, then the end mark
        public void End()
        {
            if (_ended) return;
            _ended = true;
            try
            {
                Flush();
                _steps?.Dispose();
                if (ReplayPath != null) File.WriteAllText(EndPath(ReplayPath), _written.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                // (the prepare stops when the feed stalls)
            }
            _steps = null;
        }

        public void Dispose() => End();

        // (the live stream's records, ReplayLive.cs: the same layout, between two devices of this build)
        internal static void WriteStep(BinaryWriter w, in ReplayStep step)
        {
            CampaignInput i = step.Input;
            w.Write(i.Move.X);
            w.Write(i.Move.Y);
            w.Write(i.AimDelta.X);
            w.Write(i.AimDelta.Y);
            w.Write((int)i.Buttons);
            w.Write((sbyte)i.SelectWeapon);
            w.Write(i.WeaponWheel.X);
            w.Write(i.WeaponWheel.Y);
            w.Write(i.DialogButton is MphRead.Entities.HostDialogButton b ? (sbyte)b : (sbyte)-1);
            w.Write(i.TouchDown);
            w.Write(i.TouchDx);
            w.Write(i.TouchDy);
            w.Write(i.MovePush);
            w.Write(i.MoveDir);
            w.Write(step.Checksum);
        }

        internal static void WriteEvent(BinaryWriter w, in ReplayNetEvent e)
        {
            w.Write(EventTag);
            w.Write(e.Step);
            w.Write(e.Seat);
            w.Write((byte)e.Kind);
            w.Write(e.Input == null ? (ushort)0xFFFF : (ushort)e.Input.Length);
            if (e.Input != null) w.Write(e.Input);
        }

        internal static ReplayStep ReadStep(BinaryReader r)
        {
            var i = new CampaignInput
            {
                Move = new System.Numerics.Vector2(r.ReadSingle(), r.ReadSingle()),
                AimDelta = new System.Numerics.Vector2(r.ReadSingle(), r.ReadSingle()),
                Buttons = (CampaignButtons)r.ReadInt32(),
                SelectWeapon = (MphRead.BeamType)r.ReadSByte(),
                WeaponWheel = new System.Numerics.Vector2(r.ReadSingle(), r.ReadSingle())
            };
            sbyte dialog = r.ReadSByte();
            i.DialogButton = dialog < 0 ? null : (MphRead.Entities.HostDialogButton)dialog;
            i.TouchDown = r.ReadBoolean();
            i.TouchDx = r.ReadSByte();
            i.TouchDy = r.ReadSByte();
            i.MovePush = r.ReadByte();
            i.MoveDir = r.ReadByte();
            return new ReplayStep { Input = i, Checksum = r.ReadUInt32() };
        }

        // (the prepare's process): `file` = the feed's replay file as loaded; More (ReplayPrep.Run's `more`) adds the steps
        // written since, waiting for them; false = the match is over and every step has been read (or the feed stalled /
        // was cancelled). Close after the Run: its files go (the saved replay has the same key, so the sidecar is its own).
        public static Follower Follow(ReplayFile file, string replayPath, Func<bool>? cancel = null) => new(file, replayPath, cancel);

        public sealed class Follower
        {
            readonly ReplayFile _file;
            readonly string _replayPath;
            readonly Func<bool>? _cancel;
            readonly FileStream _fs;
            readonly BinaryReader _r;
            readonly Stopwatch _idle = Stopwatch.StartNew();
            bool _closed;

            internal Follower(ReplayFile file, string replayPath, Func<bool>? cancel)
            {
                _file = file;
                _replayPath = replayPath;
                _cancel = cancel;
                _fs = new FileStream(StepsPath(replayPath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _r = new BinaryReader(_fs);
            }

            public bool More()
            {
                if (_closed) return false;
                while (true)
                {
                    bool ended = File.Exists(EndPath(_replayPath)); // (checked before reading: every step is in by then)
                    int added = 0;
                    while (ReadRecord()) added++;
                    if (added > 0)
                    {
                        _idle.Restart();
                        return true;
                    }
                    if (ended || _idle.ElapsedMilliseconds > StallMs || _cancel?.Invoke() == true)
                    {
                        Close(drain: false);
                        return false;
                    }
                    System.Threading.Thread.Sleep(250);
                }
            }

            // the next whole record (a step, or a network event), added to the file; false = none yet (a record still being
            // written is left for the next look)
            bool ReadRecord()
            {
                long left = _fs.Length - _fs.Position;
                if (left < 1) return false;
                long at = _fs.Position;
                byte tag = _r.ReadByte();
                if (tag == StepTag)
                {
                    if (left >= 1 + StepBytes)
                    {
                        _file.Steps.Add(ReadStep(_r));
                        return true;
                    }
                }
                else if (tag == EventTag)
                {
                    if (left >= EventHead)
                    {
                        int step = _r.ReadInt32();
                        byte seat = _r.ReadByte();
                        var kind = (ReplayNetKind)_r.ReadByte();
                        ushort length = _r.ReadUInt16();
                        int bytes = length == 0xFFFF ? 0 : length;
                        if (left >= EventHead + bytes)
                        {
                            byte[]? input = length == 0xFFFF ? null : _r.ReadBytes(bytes);
                            _file.NetEvents.Add(new ReplayNetEvent(step, seat, kind, input));
                            return true;
                        }
                    }
                }
                else throw new InvalidDataException("the feed's steps file is damaged");
                _fs.Position = at;
                return false;
            }

            // `drain`: the prepare got to the match's end in the sim; the recording app marks the feed's end just after
            // (its save), so wait for that before the files go (else its end mark would be left behind)
            public void Close(bool drain)
            {
                if (_closed) return;
                if (drain) while (More()) { }
                if (_closed) return;
                _closed = true;
                _r.Dispose();
                foreach (string f in new[] { _replayPath, StepsPath(_replayPath), EndPath(_replayPath) })
                {
                    try { File.Delete(f); } catch (Exception) { }
                }
            }
        }
    }

    // The sidecar file: a header (this build, the match's step-0 walk), then records appended as the shadow goes, each
    // flushed whole -- a reader takes the complete ones (a cut-off last record is left out) and can look again later.
    //   "MPHKFS1\0" int version, string build, int objects, int leaves, ulong signature, int every, int wholeEvery, identity
    //   record: int step, int anchor (-1 = whole), int raw length, int length, ulong prefix (ReplayPrep.PrefixHash of the
    //           steps before it), bytes
    //   end:    int -1, int steps, ulong StepsHash, ulong 0 (a prepare reached the end of those steps)
    // v4 (owner 11-22 "everyone in every case"): only ever appended. A keyframe is known by (step, prefix), so one sidecar
    // holds the keyframes of every replay with its key -- a guest's file cut short as it left, the stream it rejoined, the
    // whole match, two bot matches from the same start -- and a viewer takes only those whose prefix is its file's. A
    // later prepare goes on from the last whole one that fits (ReplayPrep.Run) and writes after the end mark.
    public sealed class KeyframeSidecar : IDisposable
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("MPHKFS1\0");
        const int Version = 4; // (4: prefix hash per record, append-only; 3: the step-0 walk's names, KeyframeIdentity)
        const int HeadBytes = 24;
        readonly FileStream _fs;
        readonly bool _writing;
        long _scanned;
        public string Build { get; private set; } = "";
        public int ObjectCount { get; private set; }
        public int LeafCount { get; private set; }
        public ulong Signature { get; private set; }
        public SimSnapshot.KeyframeIdentity? Identity { get; private set; }
        // the last record read is an end mark (a prepare finished; one going on from it writes after it)
        public bool Complete { get; private set; }
        // the end marks' (steps, StepsHash): the replays a prepare got to the end of
        readonly List<(int Steps, ulong Hash)> _ends = new();
        public int EndSteps => _ends.Count == 0 ? 0 : _ends[^1].Steps;
        // (step, prefix) -> (anchor, raw length, offset, length); a later copy of the same keyframe replaces the earlier
        readonly Dictionary<(int Step, ulong Prefix), (int Anchor, int Raw, long Offset, int Length)> _records = new();
        // step -> the latest record's prefix there (a viewer with no file to check against takes the latest)
        readonly SortedList<int, ulong> _latest = new();
        int _lastWhole = -1;

        // whether this sidecar has keyframes for `file` to its end: a prepare that finished it exactly, or a keyframe that
        // fits within two of the end (the seek to the last steps is then a checkpoint's few steps)
        public bool Covers(ReplayFile file) => Covers(file, ReplayPrep.PrefixHashes(file));

        public bool Covers(ReplayFile file, ulong[] prefixes)
        {
            int n = file.Steps.Count;
            if (n < ReplayPrep.Every * 3) return Complete;
            ulong whole = ReplayPrep.StepsHash(file);
            foreach ((int steps, ulong hash) in _ends)
            {
                if (steps == n && hash == whole) return true;
            }
            return Before(n, prefixes) >= n / ReplayPrep.Every * ReplayPrep.Every - 2 * ReplayPrep.Every;
        }

        // (tests) a finished prepare of exactly this file
        public bool Fits(ReplayFile file)
        {
            ulong whole = ReplayPrep.StepsHash(file);
            return _ends.Any(e => e.Steps == file.Steps.Count && e.Hash == whole);
        }

        public int Count => _records.Count;
        public IEnumerable<(int Step, int Anchor, int Raw, int Length)> Records =>
            _records.OrderBy(r => r.Value.Offset).Select(r => (r.Key.Step, r.Value.Anchor, r.Value.Raw, r.Value.Length));
        public long Length => _fs.Length;
        public int LastStep => _latest.Count == 0 ? -1 : _latest.Keys[^1];
        // the latest whole keyframe's step, of any replay with this key (-1: none)
        public int LastWholeStep => _lastWhole;

        KeyframeSidecar(FileStream fs, bool writing)
        {
            _fs = fs;
            _writing = writing;
        }

        public static KeyframeSidecar Create(string path, SimSnapshot.KeyframeBase kbase)
        {
            var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var s = new KeyframeSidecar(fs, writing: true);
            var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
            w.Write(Magic);
            w.Write(Version);
            w.Write(ReplayPrep.BuildId);
            w.Write(kbase.ObjectCount);
            w.Write(kbase.LeafCount);
            w.Write(kbase.Signature);
            w.Write(ReplayPrep.Every);
            w.Write(ReplayPrep.WholeEvery);
            SimSnapshot.KeyframeIdentity.Of(kbase).Write(w);
            w.Flush();
            fs.Flush();
            return s;
        }

        // a sidecar of this build, written on after its last whole record (a cut-off one a stopped prepare left goes)
        public static KeyframeSidecar Append(string path)
        {
            long end;
            using (KeyframeSidecar r = Open(path) ?? throw new InvalidDataException("no sidecar to go on with"))
            {
                end = r._scanned;
            }
            var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > end) fs.SetLength(end);
            fs.Position = end;
            return new KeyframeSidecar(fs, writing: true);
        }

        public void Append(int step, int anchor, int rawLength, byte[] data, ulong prefix)
        {
            var head = new byte[HeadBytes];
            BitConverter.TryWriteBytes(head.AsSpan(0), step);
            BitConverter.TryWriteBytes(head.AsSpan(4), anchor);
            BitConverter.TryWriteBytes(head.AsSpan(8), rawLength);
            BitConverter.TryWriteBytes(head.AsSpan(12), data.Length);
            BitConverter.TryWriteBytes(head.AsSpan(16), prefix);
            _fs.Write(head);
            _fs.Write(data);
            _fs.Flush();
        }

        public void Finish(int steps, ulong hash)
        {
            var head = new byte[HeadBytes];
            BitConverter.TryWriteBytes(head.AsSpan(0), -1);
            BitConverter.TryWriteBytes(head.AsSpan(4), steps);
            BitConverter.TryWriteBytes(head.AsSpan(8), hash);
            _fs.Write(head);
            _fs.Flush();
        }

        // null: no sidecar, another build's or version's, or unreadable
        public static KeyframeSidecar? Open(string path)
        {
            if (!File.Exists(path)) return null;
            FileStream fs;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException)
            {
                return null;
            }
            var s = new KeyframeSidecar(fs, writing: false);
            try
            {
                var r = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
                if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || r.ReadInt32() != Version) throw new InvalidDataException();
                s.Build = r.ReadString();
                if (s.Build != ReplayPrep.BuildId) throw new InvalidDataException();
                s.ObjectCount = r.ReadInt32();
                s.LeafCount = r.ReadInt32();
                s.Signature = r.ReadUInt64();
                if (r.ReadInt32() != ReplayPrep.Every || r.ReadInt32() != ReplayPrep.WholeEvery) throw new InvalidDataException();
                s.Identity = SimSnapshot.KeyframeIdentity.Read(r);
                s._scanned = fs.Position;
                s.Refresh();
                return s;
            }
            catch (Exception)
            {
                s.Dispose();
                return null;
            }
        }

        // takes in the records written since the last look (a sidecar still being prepared, or prepared on after its end)
        public void Refresh()
        {
            if (_writing) return;
            var head = new byte[HeadBytes];
            while (true)
            {
                long len = _fs.Length;
                if (_scanned + HeadBytes > len) return;
                _fs.Position = _scanned;
                _fs.ReadExactly(head);
                int step = BitConverter.ToInt32(head, 0);
                if (step < 0)
                {
                    _ends.Add((BitConverter.ToInt32(head, 4), BitConverter.ToUInt64(head, 8)));
                    Complete = true;
                    _scanned += HeadBytes;
                    continue;
                }
                int anchor = BitConverter.ToInt32(head, 4), raw = BitConverter.ToInt32(head, 8), length = BitConverter.ToInt32(head, 12);
                ulong prefix = BitConverter.ToUInt64(head, 16);
                if (length < 0 || step % ReplayPrep.Every != 0) return; // (damaged from here: what came before stands)
                if (_scanned + HeadBytes + length > len) return; // (still being written)
                _records[(step, prefix)] = (anchor, raw, _scanned + HeadBytes, length);
                _latest[step] = prefix;
                if (anchor < 0) _lastWhole = Math.Max(_lastWhole, step);
                Complete = false;
                _scanned += HeadBytes + length;
            }
        }

        // the record at `step` for a replay with these prefix hashes (null: any, the latest)
        bool Find(int step, ulong[]? prefixes, out (int Anchor, int Raw, long Offset, int Length) rec)
        {
            rec = default;
            ulong p;
            if (prefixes != null)
            {
                int k = step / ReplayPrep.Every;
                if (step % ReplayPrep.Every != 0 || k >= prefixes.Length) return false;
                p = prefixes[k];
            }
            else if (!_latest.TryGetValue(step, out p)) return false;
            return _records.TryGetValue((step, p), out rec);
        }

        // the latest record at or before `step` that fits (and whose anchor is here and fits too; -1: none)
        public int Before(int step, ulong[]? prefixes = null)
        {
            for (int at = step / ReplayPrep.Every * ReplayPrep.Every; at > 0; at -= ReplayPrep.Every)
            {
                if (!Find(at, prefixes, out var rec)) continue;
                if (rec.Anchor < 0 || Find(rec.Anchor, prefixes, out _)) return at;
            }
            return -1;
        }

        // the latest whole record that fits (-1: none)
        public int LastWhole(ulong[] prefixes)
        {
            for (int at = Math.Min(_lastWhole, (prefixes.Length - 1) * ReplayPrep.Every); at > 0; at -= ReplayPrep.Every)
            {
                if (Find(at, prefixes, out var rec) && rec.Anchor < 0) return at;
            }
            return -1;
        }

        public int AnchorOf(int step, ulong[]? prefixes = null) =>
            Find(step, prefixes, out var rec) ? rec.Anchor : throw new KeyNotFoundException($"no keyframe at step {step}");

        public byte[] Read(int step, ulong[]? prefixes = null)
        {
            if (!Find(step, prefixes, out var rec)) throw new KeyNotFoundException($"no keyframe at step {step}");
            var data = new byte[rec.Length];
            _fs.Position = rec.Offset;
            _fs.ReadExactly(data);
            return ReplayPrep.Decompress(data, rec.Raw);
        }

        public void Dispose() => _fs.Dispose();
    }

    // The viewer's side: its own step-0 base (taken as the match starts, before any step) and a loader; Seek puts the match
    // at the prepared keyframe nearest at or before a step. The caller then rewinds its ReplayPlayer to the returned step
    // and plays on checking every step, as after a checkpoint restore; a refusal (or a later check that fails) means the
    // match's state is no longer trusted: restore a checkpoint or restart.
    public sealed class ReplayKeyframes : IDisposable
    {
        readonly SimSnapshot.KeyframeBase _base;
        SimSnapshot.KeyframeLoader _loader;
        readonly string _path;
        readonly ReplayFile? _file;
        // (v4) the file's prefix hashes: only keyframes made from these first steps are this replay's (null: any)
        readonly ulong[]? _prefixes;
        KeyframeSidecar? _sidecar;
        public bool Broken { get; private set; }
        public double LastLoadMs { get; private set; }
        public string? LastRefusal { get; private set; }
        // (instant seek always) set when the prepared walk differed and was matched to this one by name (for the log)
        public string? MatchedByName { get; private set; }

        public ReplayKeyframes(CampaignHost host, string path, ReplayFile? file = null)
        {
            _file = file;
            _prefixes = file != null ? ReplayPrep.PrefixHashes(file) : null;
            _base = ReplayCheckpoints.KeyframeBase(host);
            _loader = new SimSnapshot.KeyframeLoader(_base);
            _path = path;
        }

        KeyframeSidecar? Sidecar()
        {
            if (Broken) return null;
            if (_sidecar == null)
            {
                _sidecar = KeyframeSidecar.Open(_path);
                if (_sidecar != null && (_sidecar.ObjectCount != _base.ObjectCount || _sidecar.LeafCount != _base.LeafCount
                    || _sidecar.Signature != _base.Signature))
                {
                    // (instant seek always, owner 11-21) a walk that differs is no longer refused whole: the two are
                    // matched by name (KeyframeIdentity); only a keyframe that needs what this copy lacks is refused, and every
                    // step after a load is still checked against the recording
                    string differs = $"objects {_sidecar.ObjectCount} / {_base.ObjectCount}, leaves {_sidecar.LeafCount} / {_base.LeafCount}";
                    var matching = Stopwatch.StartNew();
                    (SimSnapshot.KeyframeMap? map, string? why) = _sidecar.Identity != null
                        ? SimSnapshot.KeyframeMap.Build(_sidecar.Identity, _base) : (null, "no walk names in the sidecar");
                    if (map == null)
                    {
                        Break($"the prepared match started differently ({differs}) and couldn't be matched: {why}" + WalkDiff());
                    }
                    else
                    {
                        _loader = new SimSnapshot.KeyframeLoader(_base, map);
                        // (no walk diff here: describing ~16k objects would land on this first seek; the prepare's .walk
                        // stays beside the sidecar for a look by hand)
                        MatchedByName = $"the prepared walk differs ({differs}); matched by name in {matching.Elapsed.TotalMilliseconds:0} ms: {map.Summary}";
                    }
                }
                else if (_sidecar != null)
                {
                    try { File.Delete(WalkPath(_path)); } catch (IOException) { } // (it matched: the walk isn't needed)
                }
            }
            else
            {
                _sidecar.Refresh();
            }
            // (v4: a keyframe of another match with the same start is passed over by its prefix, not refused whole)
            return _sidecar;
        }

        // the step a seek to `target` would land on (-1: none prepared)
        public int Before(int target) => Broken ? -1 : Sidecar()?.Before(target, _prefixes) ?? -1;

        public bool Complete => Sidecar() is KeyframeSidecar s && (_file != null ? s.Covers(_file, _prefixes!) : s.Complete);
        public int LastStep => Sidecar()?.LastStep ?? -1;

        // loads the keyframe at `step` (from Before); false = refused (the match may be half-loaded: restore or restart)
        public bool Load(int step)
        {
            KeyframeSidecar? s = Sidecar();
            if (Broken || s == null) return false;
            var sw = Stopwatch.StartNew();
            try
            {
                int anchor = s.AnchorOf(step, _prefixes);
                if (anchor >= 0 && _loader.Load(s.Read(anchor, _prefixes)) is string a)
                {
                    Break(a);
                    return false;
                }
                if (_loader.Load(s.Read(step, _prefixes)) is string r)
                {
                    Break(r);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Break(ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            LastLoadMs = sw.Elapsed.TotalMilliseconds;
            return true;
        }

        // (11-05, the same refusal back over and over: "started differently" with nothing to say what) the prepare writes
        // its step-0 walk beside the sidecar; a viewer whose own walk differs writes its copy beside it (.walk-viewer) and
        // names the first lines that differ in the refusal, so the log says which state to keep out of the walk
        public static string WalkPath(string sidecar) => sidecar + ".walk";

        public static void WriteWalk(string sidecar, SimSnapshot.KeyframeBase kbase)
        {
            try
            {
                File.WriteAllLines(WalkPath(sidecar), kbase.Describe());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // (diagnostics only)
            }
        }

        string WalkDiff()
        {
            try
            {
                string[] mine = _base.Describe().ToArray();
                File.WriteAllLines(_path + ".walk-viewer", mine);
                string prepPath = WalkPath(_path);
                if (!File.Exists(prepPath)) return "; (no prepared walk to compare)";
                string[] theirs = File.ReadAllLines(prepPath);
                var sb = new StringBuilder();
                int shown = 0, differ = 0, n = Math.Max(mine.Length, theirs.Length);
                for (int i = 0; i < n; i++)
                {
                    string? p = i < theirs.Length ? theirs[i] : null, v = i < mine.Length ? mine[i] : null;
                    if (p == v) continue;
                    differ++;
                    if (shown++ < 12) sb.Append($"\n  prepared: {p ?? "(none)"}\n  viewer:   {v ?? "(none)"}");
                }
                // (the statics, as sets: a static walked on one side only)
                var ps = new HashSet<string>(theirs.Where(l => l.StartsWith("s ")));
                var vs = new HashSet<string>(mine.Where(l => l.StartsWith("s ")));
                foreach (string s in ps.Except(vs).Take(6)) sb.Append($"\n  prepared only: {s}");
                foreach (string s in vs.Except(ps).Take(6)) sb.Append($"\n  viewer only:   {s}");
                return $"; {differ} walk lines differ (walks: {prepPath}, {_path}.walk-viewer):" + sb;
            }
            catch (Exception ex)
            {
                return "; (walk diff failed: " + ex.Message + ")";
            }
        }

        public void Break(string why)
        {
            Broken = true;
            LastRefusal = why;
            _sidecar?.Dispose();
            _sidecar = null;
        }

        public void Dispose() => _sidecar?.Dispose();
    }
}
