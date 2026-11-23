using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MphRecomp.Replay
{
    // Live keyframes for a network match's guests (2026-11-21, owner: every new replay, local or online, seeks instantly
    // right away). Only the host runs the whole match, so only its recording is exact; before this a guest (and every
    // player of a served match) got it at the end and prepared its keyframes from then on (~1/3 of the match's length of
    // slow seeks). Now the host streams its recording to each guest that asks (ReplayLink KindLive) as it is played, and
    // the guest feeds it to its own prepare exactly as the host feeds its own (ReplayFeed): same file, same key, same
    // sidecar, every step's checksum checked by the prepare, so a guest's keyframes are exact or refused like the host's.
    //   blob: u8 version (1), u8 part, u32 stream (a guest's stream: new when it joins again), u32 seq (from 0), then
    //     part Head:    u8 the step layout's size (ReplayFeed.StepBytes), the recording so far (ReplayFile.Write)
    //     part Records: Deflate of the feed's records since the last blob (ReplayFeed: events, then steps)
    //     part End:     i32 the recording's steps (the match is over)
    // A guest says it takes the stream in its identity (ReplayLink.SendIdentity wantLive, a trailing byte an older host
    // doesn't read), so an older guest never gets one; an older host never sends one (the guest prepares on arrival as
    // before). The host's file at the end still goes as it did: the guest fills whatever the stream missed from it
    // (ReplayLiveMirror.Complete), and a sidecar that doesn't fit the saved file is prepared again (ReplayPrep.NeedsPrep).
    public static class ReplayLive
    {
        public const byte Version = 1;
        public const byte PartHead = 0, PartRecords = 1, PartEnd = 2;
        public const int HeadBytes = 1 + 1 + 4 + 4;
        // the host sends a guest its new steps this often (2 s: about 4 KB a guest with 7 remote seats' inputs)
        public const int SendEvery = 120;
        // a guest's records unpack to no more than this (a blob is SendEvery steps' worth, ~10 KB; one catching a guest
        // up, after it joined again, at most MaxBlobSteps: ~1 MB with 7 remote seats' inputs)
        public const int MaxRecordBytes = 4 << 20;
        public const int MaxBlobSteps = 3600;

        internal static byte[] Blob(byte part, uint stream, uint seq, ReadOnlySpan<byte> payload)
        {
            byte[] b = new byte[HeadBytes + payload.Length];
            b[0] = Version;
            b[1] = part;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), stream);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6), seq);
            payload.CopyTo(b.AsSpan(HeadBytes));
            return b;
        }
    }

    // what the sender sends through: the session's ReplayLink (and the PC test's loopback, -replaypreptest guest)
    public interface IReplayLiveLink
    {
        IReadOnlyList<(int Seat, int Generation)> SeatsWantingLive { get; }
        int LiveInFlight(int seat);
        void SendLive(int seat, byte[] blob);
    }

    // The host's side (an app hosting a LAN / online match, MatchReplay.cs; the served match host, Program.Replay.cs):
    // Step after each recorded step, End as the match ends (before the whole file goes). Game thread only (the recording's).
    public sealed class ReplayLiveSender
    {
        // a guest with this many blobs not yet acked waits (its link is slow or gone): the next blob has all the steps since
        const int MaxOut = 4;

        readonly ReplayFile _file;
        readonly IReplayLiveLink _link;
        readonly Dictionary<int, Seat> _seats = new();
        readonly MemoryStream _raw = new();
        // (seats at the same point share one blob: this send's, by where they're from)
        readonly Dictionary<(int Steps, int Events), (byte[] Blob, int ToSteps, int ToEvents)> _records = new();
        // the head, written once (the recording at the first send): a guest that joins later gets it and is caught up
        // from there, MaxBlobSteps a blob, so the game thread writes the file once a match however guests come and go
        byte[]? _head;
        int _headSteps, _headEvents;
        bool _ended;

        // what went, for the log
        public long Bytes { get; private set; }
        public int Blobs { get; private set; }

        sealed class Seat
        {
            public int Generation;
            public uint Stream, Seq;
            public int Steps, Events;
        }

        public ReplayLiveSender(ReplayFile file, IReplayLiveLink link)
        {
            _file = file;
            _link = link;
        }

        public void Step()
        {
            if (_ended) return;
            int n = _file.Steps.Count;
            if (n < ReplayPrep.KeySteps || n % ReplayLive.SendEvery != 0) return;
            Send(end: false);
        }

        public void End()
        {
            if (_ended) return;
            _ended = true;
            if (_file.Steps.Count >= ReplayPrep.KeySteps) Send(end: true);
        }

        void Send(bool end)
        {
            if (_file.Mods.Count > 0) return; // (a modded match has no keyframes: ReplayPrep.Worth)
            int n = _file.Steps.Count, events = _file.NetEvents.Count;
            _records.Clear();
            foreach ((int seatIndex, int generation) in _link.SeatsWantingLive)
            {
                try
                {
                    if (!_seats.TryGetValue(seatIndex, out Seat? seat) || seat.Generation != generation)
                    {
                        // (new, or its device joined again: from the start)
                        seat = new Seat { Generation = generation, Stream = (uint)Environment.TickCount ^ (uint)(generation << 20), Steps = -1 };
                        _seats[seatIndex] = seat;
                    }
                    if (_link.LiveInFlight(seatIndex) >= MaxOut && !end) continue;
                    if (seat.Steps < 0)
                    {
                        if (_head == null)
                        {
                            var ms = new MemoryStream();
                            ms.WriteByte(ReplayFeed.StepBytes);
                            _file.Write(ms);
                            _head = ms.ToArray();
                            _headSteps = n;
                            _headEvents = events;
                        }
                        Queue(seatIndex, seat, ReplayLive.PartHead, _head);
                        seat.Steps = _headSteps;
                        seat.Events = _headEvents;
                    }
                    // (one blob a send, but all of them at the end: the guest's stream ends whole)
                    while (n > seat.Steps || events > seat.Events)
                    {
                        byte[] records = Records(seat.Steps, seat.Events, out int toSteps, out int toEvents);
                        Queue(seatIndex, seat, ReplayLive.PartRecords, records);
                        seat.Steps = toSteps;
                        seat.Events = toEvents;
                        if (!end) break;
                    }
                    if (end)
                    {
                        Queue(seatIndex, seat, ReplayLive.PartEnd, BitConverter.GetBytes(n));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    Console.Error.WriteLine($"replay live: seat {seatIndex}: " + ex.Message);
                }
            }
        }

        void Queue(int seatIndex, Seat seat, byte part, byte[] payload)
        {
            byte[] blob = ReplayLive.Blob(part, seat.Stream, seat.Seq++, payload);
            _link.SendLive(seatIndex, blob);
            Bytes += blob.Length;
            Blobs++;
        }

        // the records from (step, event) on, up to MaxBlobSteps steps and the events before the last of them (an event's
        // Step is the step it comes before: one for a step not in this blob waits for the next)
        byte[] Records(int fromStep, int fromEvent, out int toSteps, out int toEvents)
        {
            int n = _file.Steps.Count;
            if (_records.TryGetValue((fromStep, fromEvent), out var had))
            {
                toSteps = had.ToSteps;
                toEvents = had.ToEvents;
                return had.Blob;
            }
            toSteps = Math.Min(n, fromStep + ReplayLive.MaxBlobSteps);
            toEvents = fromEvent;
            while (toEvents < _file.NetEvents.Count && (toSteps == n || _file.NetEvents[toEvents].Step < toSteps)) toEvents++;
            _raw.SetLength(0);
            using (var w = new BinaryWriter(_raw, Encoding.UTF8, leaveOpen: true))
            {
                for (int i = fromEvent; i < toEvents; i++) ReplayFeed.WriteEvent(w, _file.NetEvents[i]);
                for (int i = fromStep; i < toSteps; i++)
                {
                    w.Write(ReplayFeed.StepTag);
                    ReplayFeed.WriteStep(w, _file.Steps[i]);
                }
            }
            var o = new MemoryStream((int)_raw.Length / 3 + 64);
            using (var d = new DeflateStream(o, CompressionLevel.Fastest, leaveOpen: true)) d.Write(_raw.GetBuffer(), 0, (int)_raw.Length);
            byte[] blob = o.ToArray();
            _records[(fromStep, fromEvent)] = (blob, toSteps, toEvents);
            return blob;
        }
    }

    // A guest's side: the host's blobs (ReplayLink.LiveReceived, the link's receive thread) put in order and fed to this
    // device's own prepare (Begun: start it). Complete with the host's whole file when it arrives; End when the match is
    // left without one (the prepare finishes what it has).
    public sealed class ReplayLiveMirror : IDisposable
    {
        // blobs past a missing one kept waiting for it (the link gave up on it): past this many, the stream stops here
        const int MaxWaiting = 64;

        readonly string _dataDir;
        readonly object _lock = new();
        readonly SortedDictionary<uint, byte[]> _waiting = new();
        // (blobs before the head: the stream they're of isn't known yet)
        readonly List<byte[]> _early = new();
        uint? _stream;
        ReplayFile? _file;
        ReplayFeed? _feed;
        uint _next;
        bool _ended;

        // the feed has begun (its files written): start the prepare of (replay path, sidecar)
        public event Action<string, string>? Begun;
        public event Action<string>? Log;

        public int Steps
        {
            get { lock (_lock) return _file?.Steps.Count ?? 0; }
        }

        public ReplayLiveMirror(string dataDir)
        {
            _dataDir = dataDir;
        }

        public void Receive(byte[] blob)
        {
            (string, string)? begun = null;
            lock (_lock)
            {
                if (_ended || blob.Length < ReplayLive.HeadBytes) return;
                if (blob[0] != ReplayLive.Version)
                {
                    Stop($"a stream version {blob[0]} this build doesn't read");
                    return;
                }
                uint stream = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(2));
                uint seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(6));
                if (_stream == null)
                {
                    // (the stream is the first head's: one of an earlier join still going out is the same match's start,
                    // and the end checks what came against the host's file either way)
                    if (blob[1] != ReplayLive.PartHead || seq != 0)
                    {
                        if (_early.Count < MaxWaiting) _early.Add(blob);
                        return;
                    }
                    _stream = stream;
                    foreach (byte[] e in _early)
                    {
                        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(2)) == stream)
                            _waiting[System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(6))] = e;
                    }
                    _early.Clear();
                }
                if (stream != _stream || seq < _next) return;
                _waiting[seq] = blob;
                if (_waiting.Count > MaxWaiting)
                {
                    Stop($"blob {_next} never came");
                    return;
                }
                try
                {
                    while (!_ended && _waiting.Remove(_next, out byte[]? b))
                    {
                        _next++;
                        if (Apply(b)) begun = (_feed!.ReplayPath!, _feed.Sidecar!);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException
                    or InvalidOperationException or UnauthorizedAccessException)
                {
                    Stop(ex.Message);
                }
            }
            if (begun is (string path, string sidecar)) Begun?.Invoke(path, sidecar);
        }

        // true: the feed began now
        bool Apply(byte[] blob)
        {
            byte part = blob[1];
            var payload = new ReadOnlySpan<byte>(blob, ReplayLive.HeadBytes, blob.Length - ReplayLive.HeadBytes);
            if (part == ReplayLive.PartHead)
            {
                if (_file != null) throw new InvalidDataException("a second head");
                if (payload.Length < 1 || payload[0] != ReplayFeed.StepBytes)
                    throw new InvalidDataException("the host's step layout isn't this build's");
                ReplayFile file = ReplayFile.Read(new MemoryStream(blob, ReplayLive.HeadBytes + 1, payload.Length - 1, writable: false));
                if (file.Mods.Count > 0) throw new InvalidDataException("a modded match");
                if (ReplayPlayer.CheckBeforeStart(file) is string why) throw new InvalidDataException("refused: " + why);
                _file = file;
                _feed = new ReplayFeed(file, _dataDir);
                bool begun = _feed.Step();
                Log?.Invoke($"the host's recording streams in from step {file.Steps.Count}");
                return begun;
            }
            if (_file == null || _feed == null) throw new InvalidDataException("records before the head");
            if (part == ReplayLive.PartRecords)
            {
                using var d = new DeflateStream(new MemoryStream(blob, ReplayLive.HeadBytes, payload.Length, writable: false),
                    CompressionMode.Decompress);
                var raw = new MemoryStream();
                byte[] buf = new byte[16384];
                int got;
                while ((got = d.Read(buf, 0, buf.Length)) > 0)
                {
                    raw.Write(buf, 0, got);
                    if (raw.Length > ReplayLive.MaxRecordBytes) throw new InvalidDataException("records too big");
                }
                raw.Position = 0;
                ReadRecords(new BinaryReader(raw), raw.Length, _file);
                return _feed.Step();
            }
            if (part == ReplayLive.PartEnd)
            {
                int steps = BitConverter.ToInt32(payload);
                if (steps != _file.Steps.Count) throw new InvalidDataException($"ended at {steps} steps, {_file.Steps.Count} came");
                Log?.Invoke($"the host's recording streamed in whole: {steps} steps");
                End();
                return false;
            }
            throw new InvalidDataException($"part {part}");
        }

        static void ReadRecords(BinaryReader r, long length, ReplayFile file)
        {
            while (r.BaseStream.Position < length)
            {
                byte tag = r.ReadByte();
                if (tag == ReplayFeed.StepTag)
                {
                    file.Steps.Add(ReplayFeed.ReadStep(r));
                }
                else if (tag == ReplayFeed.EventTag)
                {
                    int step = r.ReadInt32();
                    byte seat = r.ReadByte();
                    var kind = (ReplayNetKind)r.ReadByte();
                    ushort len = r.ReadUInt16();
                    byte[]? input = len == 0xFFFF ? null : r.ReadBytes(len);
                    if (input != null && input.Length != len) throw new InvalidDataException("a cut-off event");
                    if (step < file.Steps.Count) throw new InvalidDataException("an event for a step already played");
                    file.NetEvents.Add(new ReplayNetEvent(step, seat, kind, input));
                }
                else throw new InvalidDataException($"record tag {tag}");
            }
        }

        // the host's whole file arrived: what the stream missed comes from it (when the stream so far is that file's start),
        // then the feed ends; the prepare finishes it and the sidecar fits the saved file
        public void Complete(ReplayFile whole)
        {
            lock (_lock)
            {
                if (_ended || _file == null || _feed == null)
                {
                    End();
                    return;
                }
                int n = _file.Steps.Count, e = _file.NetEvents.Count;
                // (a guest that left: the host's file is the match so far, which can end before what streamed in; the
                // keyframes of the stream fit it to its last step by their prefix hashes, sidecar v4, owner 11-22 -- this
                // was "doesn't start with what streamed in" and a prepare from step 0)
                int shared = Math.Min(n, whole.Steps.Count);
                bool prefix = true;
                for (int i = 0; prefix && i < shared; i++) prefix = whole.Steps[i].Checksum == _file.Steps[i].Checksum;
                if (!prefix)
                {
                    Stop("the host's file doesn't start with what streamed in");
                    return;
                }
                if (whole.Steps.Count < n)
                {
                    Log?.Invoke($"the host's file ends at step {whole.Steps.Count}, the stream at {n}: its keyframes fit it");
                    End();
                    return;
                }
                if (whole.NetEvents.Count < e)
                {
                    Stop("the host's file has fewer network events than streamed in");
                    return;
                }
                if (whole.Steps.Count > n || whole.NetEvents.Count > e)
                {
                    Log?.Invoke($"filled the stream from the host's file: steps {n} -> {whole.Steps.Count}");
                }
                for (int i = e; i < whole.NetEvents.Count; i++) _file.NetEvents.Add(whole.NetEvents[i]);
                for (int i = n; i < whole.Steps.Count; i++) _file.Steps.Add(whole.Steps[i]);
                End();
            }
        }

        void Stop(string why)
        {
            Log?.Invoke("live keyframes stopped: " + why);
            End();
        }

        // (the feed's last steps, then its end mark: the prepare finishes; idempotent)
        public void End()
        {
            lock (_lock)
            {
                if (_ended) return;
                _ended = true;
                _waiting.Clear();
                _early.Clear();
                _feed?.End();
            }
        }

        public void Dispose() => End();
    }
}
