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
        public static ulong StepsHash(ReplayFile file)
        {
            ulong h = 14695981039346656037UL;
            foreach (ReplayStep step in file.Steps)
            {
                for (int i = 0; i < 4; i++) h = (h ^ (byte)(step.Checksum >> (i * 8))) * 1099511628211UL;
            }
            return h;
        }

        public static string SidecarPath(string dataDir, ReplayFile file) => Path.Combine(Dir(dataDir), Key(file) + Extension);

        public static bool Worth(ReplayFile file) => file.Steps.Count >= MinSteps;

        // whether a prepare should start for the sidecar at `path`: not when it's complete or another process is writing it
        // (its .lock held: the viewer reads along); a cut-off one left by a stopped prepare goes first (a new prepare
        // rewrites it from the start, which would pull it from under a viewer already reading it), and so does a complete
        // one made for another match with the same key (`file` given: the saved replay it should fit)
        public static bool NeedsPrep(string path, ReplayFile? file = null)
        {
            try
            {
                using (KeyframeSidecar? s = KeyframeSidecar.Open(path))
                {
                    if (s?.Complete == true && (file == null || s.Fits(file))) return false;
                }
                if (!File.Exists(path)) return true;
                using (new FileStream(Path.ChangeExtension(path, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    File.Delete(path);
                }
                return true;
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
                if (KeyframeSidecar.Open(path) is KeyframeSidecar old)
                {
                    bool done = old.Complete && (more != null || old.Fits(file));
                    old.Dispose();
                    if (done) return null;
                }
                var player = new ReplayPlayer(file);
                CampaignHost host = player.Start(collectDrawItems: false, viewWidth: 256, viewHeight: 192);
                try
                {
                    if (player.CheckAfterStart() is string after) return "refused: " + after;
                    var kbase = ReplayCheckpoints.KeyframeBase(host);
                    var writer = new SimSnapshot.KeyframeWriter(kbase);
                    using var sidecar = KeyframeSidecar.Create(path, kbase);
                    var sw = new Stopwatch();
                    double work = 0;
                    long bytes = 0;
                    int written = 0;
                    SimSnapshot prev = kbase.Snap;
                    SimSnapshot? anchor = null;
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
                        if (pos % Every == 0)
                        {
                            SimSnapshot snap = SimSnapshot.Take(pos, ReplayCheckpoints.Roots(host), prev);
                            bool whole = anchor == null || pos % WholeEvery == 0;
                            byte[] raw = writer.Write(snap, whole ? null : anchor);
                            if (writer.Refused.Count > 0) return "can't write " + string.Join(", ", writer.Refused);
                            byte[] packed = Compress(raw);
                            sidecar.Append(pos, whole ? -1 : anchor!.Step, raw.Length, packed);
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

    // Step 3, the live feed (PREPARE REPLAYS: DURING MATCH): a local match's recording handed to the prepare as it is played,
    // so its keyframes are ready about when the match ends. The recorder writes the replay so far once it has the key's
    // steps, then appends the new steps about once a second; the prepare follows them (Follow, ReplayPrep.Run's `more`).
    //   <data>/replays/keyframes/live/<key>.mphreplay   the recording at its KeySteps (a normal replay file)
    //   <key>.steps   every later step, StepBytes each, in order; <key>.end   the match is over (the steps are all there)
    // Only between two processes of this build, so the step layout is its own (a missed field makes the prepare's check
    // fail, which only leaves the seeks to checkpoints). Network matches aren't fed (their events aren't in the steps).
    public sealed class ReplayFeed : IDisposable
    {
        public const int StepBytes = 37;
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
                string path = Path.Combine(dir, key + ReplayLibrary.Extension);
                string temp = path + ".part";
                _file.Save(temp);
                File.Delete(EndPath(path));
                _steps = new FileStream(StepsPath(path), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                File.Move(temp, path, overwrite: true);
                _written = n;
                ReplayPath = path;
                Sidecar = ReplayPrep.SidecarPath(_dataDir, _file);
                return true;
            }
            if (n - _written >= ReplayPrep.Every) Flush();
            return false;
        }

        void Flush()
        {
            if (_steps == null) return;
            int n = _file.Steps.Count;
            if (n <= _written) return;
            _buffer.SetLength(0);
            using (var w = new BinaryWriter(_buffer, Encoding.UTF8, leaveOpen: true))
            {
                for (int i = _written; i < n; i++) WriteStep(w, _file.Steps[i]);
            }
            _steps.Write(_buffer.GetBuffer(), 0, (int)_buffer.Length);
            _steps.Flush();
            _written = n;
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

        static void WriteStep(BinaryWriter w, in ReplayStep step)
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
            w.Write(step.Checksum);
        }

        static ReplayStep ReadStep(BinaryReader r)
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
                    while (_fs.Length - _fs.Position >= StepBytes)
                    {
                        _file.Steps.Add(ReadStep(_r));
                        added++;
                    }
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
    //   "MPHKFS1\0" int version, string build, int objects, int leaves, ulong signature, int every, int wholeEvery
    //   record: int step (-1 = the end: the sidecar is complete), int anchor (-1 = whole), int raw length, int length, bytes
    public sealed class KeyframeSidecar : IDisposable
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("MPHKFS1\0");
        const int Version = 2;
        readonly FileStream _fs;
        readonly bool _writing;
        long _scanned;
        public string Build { get; private set; } = "";
        public int ObjectCount { get; private set; }
        public int LeafCount { get; private set; }
        public ulong Signature { get; private set; }
        public bool Complete { get; private set; }
        // (complete) the replay's step count and ReplayPrep.StepsHash, from the end record
        public int EndSteps { get; private set; }
        public ulong EndHash { get; private set; }
        // whether a complete sidecar was made for this replay (not just one with the same key); one still being written fits
        public bool Fits(ReplayFile file) => !Complete || EndSteps == file.Steps.Count && EndHash == ReplayPrep.StepsHash(file);
        // step -> (anchor, raw length, offset, length)
        readonly SortedList<int, (int Anchor, int Raw, long Offset, int Length)> _records = new();
        public int Count => _records.Count;
        public IEnumerable<(int Step, int Anchor, int Raw, int Length)> Records =>
            _records.Select(r => (r.Key, r.Value.Anchor, r.Value.Raw, r.Value.Length));
        public long Length => _fs.Length;
        public int LastStep => _records.Count == 0 ? -1 : _records.Keys[^1];

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
            w.Flush();
            fs.Flush();
            return s;
        }

        public void Append(int step, int anchor, int rawLength, byte[] data)
        {
            var head = new byte[16];
            BitConverter.TryWriteBytes(head.AsSpan(0), step);
            BitConverter.TryWriteBytes(head.AsSpan(4), anchor);
            BitConverter.TryWriteBytes(head.AsSpan(8), rawLength);
            BitConverter.TryWriteBytes(head.AsSpan(12), data.Length);
            _fs.Write(head);
            _fs.Write(data);
            _fs.Flush();
        }

        public void Finish(int steps, ulong hash)
        {
            var head = new byte[16];
            BitConverter.TryWriteBytes(head.AsSpan(0), -1);
            BitConverter.TryWriteBytes(head.AsSpan(4), steps);
            BitConverter.TryWriteBytes(head.AsSpan(8), hash);
            _fs.Write(head);
            _fs.Flush();
        }

        // null: no sidecar, another build's, or unreadable
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

        // takes in the records written since the last look (a sidecar still being prepared)
        public void Refresh()
        {
            if (_writing || Complete) return;
            var head = new byte[16];
            while (true)
            {
                long len = _fs.Length;
                if (_scanned + 16 > len) return;
                _fs.Position = _scanned;
                _fs.ReadExactly(head);
                int step = BitConverter.ToInt32(head, 0);
                if (step < 0)
                {
                    EndSteps = BitConverter.ToInt32(head, 4);
                    EndHash = BitConverter.ToUInt64(head, 8);
                    Complete = true;
                    return;
                }
                int anchor = BitConverter.ToInt32(head, 4), raw = BitConverter.ToInt32(head, 8), length = BitConverter.ToInt32(head, 12);
                if (_scanned + 16 + length > len) return; // (still being written)
                _records[step] = (anchor, raw, _scanned + 16, length);
                _scanned += 16 + length;
            }
        }

        // the latest record at or before `step` whose anchor is here too (-1: none)
        public int Before(int step)
        {
            int best = -1;
            foreach ((int at, var rec) in _records)
            {
                if (at > step) break;
                if (rec.Anchor < 0 || _records.ContainsKey(rec.Anchor)) best = at;
            }
            return best;
        }

        public int AnchorOf(int step) => _records[step].Anchor;

        public byte[] Read(int step)
        {
            (_, int raw, long offset, int length) = _records[step];
            var data = new byte[length];
            _fs.Position = offset;
            _fs.ReadExactly(data);
            return ReplayPrep.Decompress(data, raw);
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
        readonly SimSnapshot.KeyframeLoader _loader;
        readonly string _path;
        readonly ReplayFile? _file;
        KeyframeSidecar? _sidecar;
        public bool Broken { get; private set; }
        public double LastLoadMs { get; private set; }
        public string? LastRefusal { get; private set; }

        public ReplayKeyframes(CampaignHost host, string path, ReplayFile? file = null)
        {
            _file = file;
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
                    Break($"the prepared match started differently (objects {_sidecar.ObjectCount} / {_base.ObjectCount}, "
                        + $"leaves {_sidecar.LeafCount} / {_base.LeafCount}, signature {_sidecar.Signature} / {_base.Signature})");
                }
            }
            else
            {
                _sidecar.Refresh();
            }
            if (_sidecar != null && !Broken && _file != null && !_sidecar.Fits(_file)) Break("prepared for another match with the same start");
            return _sidecar;
        }

        // the step a seek to `target` would land on (-1: none prepared)
        public int Before(int target) => Broken ? -1 : Sidecar()?.Before(target) ?? -1;

        public bool Complete => Sidecar()?.Complete == true;
        public int LastStep => Sidecar()?.LastStep ?? -1;

        // loads the keyframe at `step` (from Before); false = refused (the match may be half-loaded: restore or restart)
        public bool Load(int step)
        {
            KeyframeSidecar? s = Sidecar();
            if (Broken || s == null) return false;
            var sw = Stopwatch.StartNew();
            try
            {
                int anchor = s.AnchorOf(step);
                if (anchor >= 0 && _loader.Load(s.Read(anchor)) is string a)
                {
                    Break(a);
                    return false;
                }
                if (_loader.Load(s.Read(step)) is string r)
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
