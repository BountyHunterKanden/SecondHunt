using System;

namespace MphRecomp.Online.Voice
{
    // One speaker's jitter buffer: frames arrive on the network thread in any order and with uneven gaps; the playback
    // thread takes one every 20 ms. A talk spurt starts playing once Target frames are queued (or the spurt already
    // ended), so a late packet still makes it; Target follows the measured arrival jitter (RFC 3550's estimate) between
    // 3 frames (60 ms) and 12 (240 ms). A missing frame is rebuilt from the next one's FEC when that's here, else concealed;
    // a queue grown well past Target (a burst after a stall) drops its oldest frames so the delay doesn't stay high.
    public sealed class VoiceJitter
    {
        public enum Kind { Silent, Frame, Fec, Conceal }

        private const int Ring = 64;
        public const int MinTarget = 3, MaxTarget = 12;
        // the speaker is done after this many frames with nothing (and no end flag heard): 200 ms
        private const int MaxMissRun = 10;
        // a frame behind the last one played after this long with nothing heard is a sender whose count started over
        private const int RestartGapMs = 1000;

        private readonly object _lock = new();
        private readonly byte[]?[] _frames = new byte[Ring][];
        private readonly ushort[] _seqs = new ushort[Ring];
        private bool _playing, _any, _played, _endKnown;
        private ushort _next, _oldest, _newest, _endSeq, _lastPlayed;
        private int _missRun;
        // arrival jitter (ms), RFC 3550: J += (|D| - J) / 16 over consecutive packets' first frames
        private double _jitter;
        private long _lastArrival;
        private ushort _lastArrivalSeq;
        private bool _haveArrival;
        private long _lastPutMs;
        private bool _havePut;

        public int Target { get; private set; } = MinTarget;
        public double JitterMs => _jitter;
        // counters for the log and the tests
        public int Received, Late, Played, Fecs, Concealed, Dropped, Spurts, Restarts;

        public void Put(ushort seq, ReadOnlySpan<byte> frame, bool firstOfPacket, bool end, long nowMs)
        {
            lock (_lock)
            {
                Received++;
                if (StartedOver(seq, nowMs))
                {
                    // the sender's voice was rebuilt (a rejoin, a new game) and counts from 0 again: what we played of
                    // the old count says nothing about this one, so take it as a new stream rather than late
                    ClearLocked();
                    _playing = _played = false;
                    _haveArrival = false;
                    Restarts++;
                }
                _lastPutMs = nowMs;
                _havePut = true;
                if (firstOfPacket)
                {
                    // (not across a pause: seqs run on from one spurt to the next, the clock doesn't; nor across a long
                    // stall, which is the network's hiccup, not its jitter)
                    if (_haveArrival)
                    {
                        double d = (nowMs - _lastArrival) - VoiceWire.Diff(seq, _lastArrivalSeq) * (double)VoiceFormat.FrameMs;
                        if (Math.Abs(d) < 500)
                        {
                            _jitter += (Math.Abs(d) - _jitter) / 16;
                            Target = Math.Clamp(MinTarget + (int)Math.Ceiling(1.5 * _jitter / VoiceFormat.FrameMs), MinTarget, MaxTarget);
                        }
                    }
                    _lastArrival = nowMs;
                    _lastArrivalSeq = seq;
                    _haveArrival = true;
                }
                if (end)
                {
                    _haveArrival = false; // the spurt's last packet: the next one comes after a pause
                }
                if (_playing ? VoiceWire.Diff(seq, _next) < 0 : _played && VoiceWire.Diff(seq, _lastPlayed) <= 0)
                {
                    Late++;
                    return;
                }
                if (_playing && VoiceWire.Diff(seq, _next) >= Ring)
                {
                    // far ahead of where we are (a long stall): start over from here
                    ClearLocked();
                    _playing = false;
                }
                int i = seq % Ring;
                _frames[i] = frame.ToArray();
                _seqs[i] = seq;
                if (!_any)
                {
                    _oldest = _newest = seq;
                    _any = true;
                }
                else
                {
                    if (VoiceWire.Diff(seq, _newest) > 0) _newest = seq;
                    if (!_playing && VoiceWire.Diff(seq, _oldest) < 0) _oldest = seq;
                }
                if (end)
                {
                    _endKnown = true;
                    _endSeq = seq;
                }
                else if (_endKnown && VoiceWire.Diff(seq, _endSeq) > 0)
                {
                    _endKnown = false; // the next spurt (seqs run on across spurts): keep playing into it
                }
            }
        }

        // far behind where we are (more than the ring holds: no reordering does that), or behind the last frame played
        // after a second of silence (a stale copy comes right after its spurt, not that long after)
        private bool StartedOver(ushort seq, long nowMs)
        {
            if (_playing)
            {
                return VoiceWire.Diff(seq, _next) < -Ring;
            }
            if (!_played)
            {
                return false;
            }
            int d = VoiceWire.Diff(seq, _lastPlayed);
            return d < -Ring || (d <= 0 && _havePut && nowMs - _lastPutMs > RestartGapMs);
        }

        // the sender's spurt ended after lastSeq (an empty end packet: its last frames had already gone out)
        public void MarkEnd(ushort lastSeq)
        {
            lock (_lock)
            {
                _haveArrival = false;
                if (!_endKnown || VoiceWire.Diff(lastSeq, _endSeq) > 0)
                {
                    _endKnown = true;
                    _endSeq = lastSeq;
                }
            }
        }

        // the playback thread, every 20 ms: what to decode for this frame (data is the frame, or the next one's for Fec)
        public Kind Take(out byte[]? data)
        {
            data = null;
            lock (_lock)
            {
                if (!_playing)
                {
                    if (!_any)
                    {
                        return Kind.Silent;
                    }
                    int queued = VoiceWire.Diff(_newest, _oldest) + 1;
                    if (queued < Target && !_endKnown)
                    {
                        return Kind.Silent;
                    }
                    _playing = true;
                    _next = _oldest;
                    _missRun = 0;
                    Spurts++;
                }
                // a burst queued far more than needed: skip ahead to Target frames behind the newest
                int ahead = VoiceWire.Diff(_newest, _next) + 1;
                if (ahead > Target + 6)
                {
                    ushort to = (ushort)(_newest - Target + 1);
                    while (_next != to)
                    {
                        int k = _next % Ring;
                        if (_frames[k] != null && _seqs[k] == _next)
                        {
                            _frames[k] = null;
                            Dropped++;
                        }
                        _next++;
                    }
                }
                int i = _next % Ring;
                if (_frames[i] != null && _seqs[i] == _next)
                {
                    data = _frames[i];
                    _frames[i] = null;
                    Advance();
                    _missRun = 0;
                    Played++;
                    return Kind.Frame;
                }
                if (_endKnown && VoiceWire.Diff(_next, _endSeq) > 0)
                {
                    Stop();
                    return Kind.Silent;
                }
                if (++_missRun > MaxMissRun)
                {
                    Stop();
                    return Kind.Silent;
                }
                ushort after = (ushort)(_next + 1);
                int j = after % Ring;
                Advance();
                if (_frames[j] != null && _seqs[j] == after)
                {
                    data = _frames[j];
                    Fecs++;
                    return Kind.Fec;
                }
                Concealed++;
                return Kind.Conceal;
            }
        }

        public bool Playing
        {
            get { lock (_lock) return _playing; }
        }

        public void Reset()
        {
            lock (_lock)
            {
                ClearLocked();
                _playing = _played = false;
                _haveArrival = false;
                _havePut = false;
            }
        }

        private void Advance()
        {
            _lastPlayed = _next;
            _played = true;
            _next++;
        }

        // the spurt is over: what's left is from before it ended (nothing newer than its end, or stale)
        private void Stop()
        {
            _playing = false;
            ClearLocked();
        }

        private void ClearLocked()
        {
            Array.Clear(_frames);
            _any = false;
            _endKnown = false;
        }
    }
}
