using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MphRecomp.Multiplayer.Net;

namespace MphRecomp.Online.Voice
{
    // What voice chat talks through: an online game's transport and members (OnlineMatchRoom: OnlineMatch.Current, from
    // the lobby through the match and back), or a test's.
    public interface IVoiceRoom
    {
        IDatagramTransport Transport { get; }
        // this device's friend code
        string MyCode { get; }
        // who's in the game as the server says (PlayerCard.Slot = their relay slot, from server 0.5.0)
        IReadOnlyList<PlayerCard> Members();
        bool Closed { get; }
    }

    public sealed class OnlineMatchRoom : IVoiceRoom
    {
        private readonly OnlineMatch _match;
        private PlayerCard[] _last = Array.Empty<PlayerCard>();

        public OnlineMatchRoom(OnlineMatch match) => _match = match;

        public IDatagramTransport Transport => _match.Link;
        public string MyCode => _match.Client.Me.Code;
        public bool Closed => _match.Disposed;

        public IReadOnlyList<PlayerCard> Members()
        {
            try
            {
                return _last = _match.Members.ToArray();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
                return _last; // the menus changed it mid-copy: last time's will do for 250 ms
            }
        }
    }

    // Voice chat for one online game (docs/ONLINE_PLAY.md section 8, decision 6, milestone O8): Opus frames over the match
    // transport on their own channel (VoiceWire, first byte MatchNetVoice.MsgVoice), straight to each player's relay slot,
    // never recorded. Voice goes both ways only with players it's on for (VoiceSettings: friends on, strangers off, per
    // player) and not muted (PlayerMutes, Social O7's one mute). Push to talk or open mic (a level gate with hangover).
    //
    // Threads, so nothing here ever waits on the game: the capture thread (the platform's) encodes and sends; the
    // transport's receive thread files frames into each speaker's VoiceJitter; our playback thread decodes, mixes and
    // blocks on its own output track (not the game's mixer); our control thread follows the members, starts and stops the
    // microphone and says hello. The game thread only flips flags (PushToTalk) and reads state (Speaking, Transmitting).
    public sealed class VoiceChat : IDisposable
    {
        // the one following OnlineMatch.Current (Start); null while there's no online game
        public static VoiceChat? Current { get; private set; }

        private const int MaxSlots = 16;
        private const int HelloEveryMs = 2000, ControlMs = 100;
        // push to talk: the microphone stays open this long after a release, so quick presses don't restart it
        private const int MicKeepMs = 3000, PttTailMs = 150;
        private const int SpeakingHoldMs = 250;

        private sealed class Speaker
        {
            public readonly int Slot;
            public string? Code;
            public bool Friend;
            public volatile bool Allowed;
            public readonly VoiceJitter Jitter = new();
            public readonly VoiceDecoder Decoder = new(); // playback thread only
            public long HeardMs; // playback: last frame with sound
            public bool Speaking;

            public Speaker(int slot) => Slot = slot;
        }

        private readonly IVoiceRoom _room;
        private readonly IDatagramTransport _net;
        private readonly Func<IVoiceCapture?>? _captureFactory;
        private readonly Func<IVoicePlayback?>? _playbackFactory;
        private readonly Speaker?[] _speakers = new Speaker?[MaxSlots];
        private readonly string?[] _hellos = new string?[MaxSlots];
        private readonly Thread _control, _playback;
        private readonly AutoResetEvent _wake = new(false);
        private volatile bool _closed;
        private string _myCode = "";
        private readonly byte[] _hello = new byte[40];

        // capture side (the capture thread, except where noted)
        private IVoiceCapture? _capture; // control thread
        private long _captureRetryAt;
        private readonly VoiceEncoder _encoder = new();
        private readonly byte[] _packet = new byte[VoiceWire.AudioHeader + VoiceFormat.FramesPerPacket * (1 + VoiceFormat.MaxFrameBytes)];
        private int _packetAt, _packetFrames;
        private ushort _seq, _packetSeq;
        private bool _sending;
        private float _floor = 200;
        private int _hang;
        private volatile bool _pttHeld;
        private long _pttReleasedAt = long.MinValue / 2;
        private long _lastSentMs = long.MinValue / 2;

        // counters (the log, -voicetest)
        public int PacketsSent, PacketsReceived, NotAllowed, BadPackets;
        public string? CaptureError { get; private set; }

        // fired on the playback thread when someone starts or stops being heard
        public event Action<string, bool>? SpeakingChanged;

        public VoiceChat(IVoiceRoom room, Func<IVoiceCapture?>? capture, Func<IVoicePlayback?>? playback)
        {
            _room = room;
            _net = room.Transport;
            _captureFactory = capture;
            _playbackFactory = playback;
            _net.Received += OnReceived;
            _control = new Thread(ControlLoop) { IsBackground = true, Name = "voice control" };
            _playback = new Thread(PlaybackLoop) { IsBackground = true, Name = "voice playback", Priority = ThreadPriority.AboveNormal };
            _control.Start();
            _playback.Start();
        }

        // ------------------------------------------------------------------ following OnlineMatch.Current

        private static Thread? _follower;
        private static readonly object FollowGate = new();

        // the menus' online lobby (hosted or joined), before OnlineMatch.Current is set at START: voice follows it first, so
        // the lobby has voice too and the match goes on with the same one (owner 2026-10-11: the lobby's TAP TO TALK "doesnt
        // appear to be real": OnlineMatch.Current is only set at START, so there was no voice in a lobby)
        public static volatile OnlineMatch? Lobby;

        // start following online games (once, at app start): a VoiceChat for each OnlineMatch.Current, on the devices in
        // VoiceDevices
        public static void Start()
        {
            lock (FollowGate)
            {
                if (_follower != null)
                {
                    return;
                }
                _follower = new Thread(Follow) { IsBackground = true, Name = "voice follow" };
                _follower.Start();
            }
        }

        private static void Follow()
        {
            OnlineMatch? followed = null;
            while (true)
            {
                try
                {
                    OnlineMatch? m = Lobby;
                    if (m == null || m.Disposed)
                    {
                        m = OnlineMatch.Current;
                    }
                    if (m != null && m.Disposed)
                    {
                        m = null;
                    }
                    if (m != followed)
                    {
                        VoiceChat? old = Current;
                        Current = null;
                        old?.Dispose();
                        followed = m;
                        if (m != null)
                        {
                            Current = new VoiceChat(new OnlineMatchRoom(m), VoiceDevices.Capture, VoiceDevices.Playback);
                            VoiceDevices.Write($"voice: on for game {m.Game.Id}, slot {m.Link.LocalSlot}, mode {VoiceSettings.Current.Mode}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    VoiceDevices.Write("voice: follow: " + ex);
                }
                Thread.Sleep(250);
            }
        }

        // ------------------------------------------------------------------ the game's side (any thread, never waits)

        // push to talk held (a controller button or the touch button); true when a voice chat is on in push-to-talk mode,
        // so the caller can keep that button for talking
        public static bool HoldToTalk(bool held)
        {
            VoiceChat? v = Current;
            if (v == null || VoiceSettings.Current.Mode != VoiceMode.PushToTalk)
            {
                return false;
            }
            v.PushToTalk(held);
            return true;
        }

        public void PushToTalk(bool held)
        {
            if (held == _pttHeld)
            {
                return;
            }
            if (!held)
            {
                Interlocked.Exchange(ref _pttReleasedAt, Environment.TickCount64);
            }
            _pttHeld = held;
            _wake.Set(); // the control thread opens the microphone now, not on its next tick
        }

        // this device's voice is going out (sent something in the last 200 ms)
        public bool Transmitting => Environment.TickCount64 - Interlocked.Read(ref _lastSentMs) < 200;
        public bool PushToTalkHeld => _pttHeld;
        public bool MicOpen => _capture?.Running == true;

        // that player is being heard now
        public bool Speaking(string code)
        {
            foreach (Speaker? s in _speakers)
            {
                if (s != null && s.Code == code)
                {
                    return s.Speaking;
                }
            }
            return false;
        }

        // voice with that player is on (both ways): the per-player choice, else friends / strangers, and not muted
        public bool Allowed(string code)
        {
            bool friend = _room.Members().FirstOrDefault(m => m.Code == code)?.Friend == true;
            return AllowedFor(code, friend);
        }

        public static bool AllowedFor(string code, bool friend)
        {
            VoiceSettings s = VoiceSettings.Current;
            if (s.Mode == VoiceMode.Off || code.Length == 0)
            {
                return false;
            }
            bool on = s.Player(code) ?? (friend ? s.Friends : s.Strangers);
            return on && !PlayerMutes.Current.Voice(code);
        }

        // the per-player choice (null = back to the friends / strangers default)
        public void SetAllowed(string code, bool? on)
        {
            VoiceSettings.Current.SetPlayer(code, on);
            _wake.Set();
        }

        public string Summary()
        {
            var parts = new List<string>();
            foreach (Speaker? s in _speakers)
            {
                if (s?.Code == null)
                {
                    continue;
                }
                VoiceJitter j = s.Jitter;
                parts.Add($"slot {s.Slot} {(s.Allowed ? "on" : "off")}{(s.Friend ? " friend" : "")}: in {j.Received} late {j.Late} "
                    + $"fec {j.Fecs} concealed {j.Concealed} dropped {j.Dropped} target {j.Target} jitter {j.JitterMs:0} ms");
            }
            return $"voice: mode {VoiceSettings.Current.Mode}, mic {(MicOpen ? "open" : "closed")}{(CaptureError != null ? $" ({CaptureError})" : "")}, "
                + $"sent {PacketsSent}, received {PacketsReceived}, not allowed {NotAllowed}, bad {BadPackets}"
                + (parts.Count > 0 ? "; " + string.Join("; ", parts) : "");
        }

        // ------------------------------------------------------------------ control thread

        private void ControlLoop()
        {
            long nextHello = 0, nextLog = Environment.TickCount64 + 30_000;
            while (!_closed)
            {
                try
                {
                    long now = Environment.TickCount64;
                    if (_room.Closed)
                    {
                        break;
                    }
                    bool anyAllowed = UpdateSpeakers();
                    // (every device, whatever its own cards say: another device's may lack the slots)
                    if (now >= nextHello && _myCode.Length > 0)
                    {
                        _net.SendAll(_hello.AsSpan(0, VoiceWire.WriteHello(_hello, _myCode)));
                        nextHello = now + HelloEveryMs;
                    }
                    UpdateCapture(now, anyAllowed);
                    if (now >= nextLog)
                    {
                        VoiceDevices.Write(Summary());
                        nextLog = now + 30_000;
                    }
                }
                catch (Exception ex) when (ex is not ThreadAbortException)
                {
                    VoiceDevices.Write("voice: control: " + ex.Message);
                }
                _wake.WaitOne(ControlMs);
            }
            StopCapture();
        }

        // who's in which slot (the server's cards; hellos only for slots the cards don't give, and only for a code that is
        // a member), friend or not, voice on or not
        private bool UpdateSpeakers()
        {
            _myCode = _room.MyCode;
            IReadOnlyList<PlayerCard> members = _room.Members();
            int mySlot = _net.LocalSlot;
            bool anyAllowed = false;
            for (int slot = 0; slot < MaxSlots; slot++)
            {
                PlayerCard? card = null;
                if (slot != mySlot)
                {
                    card = members.FirstOrDefault(m => m.Slot == slot);
                    if (card == null && Volatile.Read(ref _hellos[slot]) is string code)
                    {
                        // a member the cards give no slot (servers before 0.5.0), else someone the menus haven't listed yet:
                        // a stranger until they are (a hello can't make anyone a friend)
                        card = members.Any(m => m.Code == code && m.Slot != null) ? null
                            : members.FirstOrDefault(m => m.Code == code) ?? new PlayerCard { Code = code };
                    }
                    if (card != null && (card.Code.Length == 0 || card.Code == _myCode))
                    {
                        card = null;
                    }
                }
                Speaker? s = _speakers[slot];
                if (card == null)
                {
                    if (s != null)
                    {
                        s.Allowed = false;
                        _speakers[slot] = null;
                    }
                    continue;
                }
                if (s == null || s.Code != card.Code)
                {
                    s = new Speaker(slot) { Code = card.Code };
                    _speakers[slot] = s;
                }
                s.Friend = card.Friend;
                s.Allowed = AllowedFor(card.Code, card.Friend);
                anyAllowed |= s.Allowed;
            }
            return anyAllowed;
        }

        private void UpdateCapture(long now, bool anyAllowed)
        {
            VoiceMode mode = VoiceSettings.Current.Mode;
            bool want = anyAllowed && (mode == VoiceMode.OpenMic
                || mode == VoiceMode.PushToTalk && (_pttHeld || now - Interlocked.Read(ref _pttReleasedAt) < MicKeepMs));
            if (want && _capture == null && now >= _captureRetryAt && _captureFactory != null)
            {
                IVoiceCapture? capture = _captureFactory();
                if (capture == null)
                {
                    CaptureError = "no microphone on this platform";
                    _captureRetryAt = now + 5000;
                }
                else if (capture.Start(OnCapture))
                {
                    _capture = capture;
                    CaptureError = null;
                    VoiceDevices.Write("voice: microphone open");
                }
                else
                {
                    if (CaptureError != capture.Error)
                    {
                        VoiceDevices.Write("voice: microphone: " + capture.Error);
                    }
                    CaptureError = capture.Error;
                    capture.Dispose();
                    _captureRetryAt = now + 2000;
                }
            }
            else if (!want && _capture != null)
            {
                StopCapture();
            }
        }

        private void StopCapture()
        {
            IVoiceCapture? c = _capture;
            _capture = null;
            if (c != null)
            {
                c.Stop();
                c.Dispose();
                VoiceDevices.Write("voice: microphone closed");
            }
        }

        // ------------------------------------------------------------------ capture thread

        private void OnCapture(short[] pcm)
        {
            if (_closed)
            {
                return;
            }
            VoiceMode mode = VoiceSettings.Current.Mode;
            bool gate = Gate(pcm);
            bool talk = mode switch
            {
                VoiceMode.PushToTalk => _pttHeld || Environment.TickCount64 - Interlocked.Read(ref _pttReleasedAt) < PttTailMs,
                VoiceMode.OpenMic => gate,
                _ => false,
            };
            if (!talk)
            {
                if (_sending)
                {
                    SendPacket(end: true);
                    _sending = false;
                }
                return;
            }
            if (!_sending)
            {
                _encoder.Reset();
                _sending = true;
            }
            if (_packetFrames == 0)
            {
                _packetSeq = _seq;
                _packetAt = VoiceWire.AudioHeader;
            }
            int n = _encoder.Encode(pcm, _packet.AsSpan(_packetAt + 1, VoiceFormat.MaxFrameBytes));
            if (n <= 0)
            {
                return;
            }
            _packet[_packetAt] = (byte)n;
            _packetAt += 1 + n;
            _packetFrames++;
            _seq++;
            if (_packetFrames == VoiceFormat.FramesPerPacket)
            {
                SendPacket(end: false);
            }
        }

        // the open-mic gate: louder than 3.5x the room's noise floor (tracked down fast, up slowly), held 300 ms past the
        // last loud frame so word ends and short pauses go out
        private bool Gate(short[] pcm)
        {
            double sum = 0;
            foreach (short v in pcm)
            {
                sum += v * (double)v;
            }
            float rms = (float)Math.Sqrt(sum / pcm.Length);
            _floor = rms < _floor ? 0.9f * _floor + 0.1f * rms : Math.Min(_floor * 1.003f, rms);
            _floor = Math.Max(_floor, 30);
            if (rms > Math.Max(_floor * 3.5f, 350))
            {
                _hang = 15;
                return true;
            }
            if (_hang > 0)
            {
                _hang--;
                return true;
            }
            return false;
        }

        // to every player voice is on with (never SendAll: the relay would reach players it's off for)
        private void SendPacket(bool end)
        {
            if (_packetFrames == 0 && !end)
            {
                return;
            }
            if (_packetFrames == 0)
            {
                _packetSeq = _seq;
                _packetAt = VoiceWire.AudioHeader;
            }
            VoiceWire.WriteAudioHeader(_packet, _packetSeq, _packetFrames, end);
            ReadOnlySpan<byte> packet = _packet.AsSpan(0, _packetAt);
            bool sent = false;
            foreach (Speaker? s in _speakers)
            {
                if (s != null && s.Allowed)
                {
                    _net.Send(s.Slot, packet);
                    sent = true;
                }
            }
            if (sent)
            {
                Interlocked.Increment(ref PacketsSent);
                if (_packetFrames > 0)
                {
                    Interlocked.Exchange(ref _lastSentMs, Environment.TickCount64);
                }
            }
            _packetFrames = 0;
        }

        // ------------------------------------------------------------------ receive thread

        private void OnReceived(int from, ReadOnlySpan<byte> data)
        {
            if (data.Length < 2 || data[0] != MatchNetVoice.MsgVoice || _closed || from < 0 || from >= MaxSlots)
            {
                return;
            }
            if (data[1] == VoiceWire.Hello)
            {
                if (VoiceWire.ReadHello(data) is string code)
                {
                    Volatile.Write(ref _hellos[from], code);
                }
                return;
            }
            Interlocked.Increment(ref PacketsReceived);
            Speaker? s = _speakers[from];
            if (s == null || !s.Allowed)
            {
                Interlocked.Increment(ref NotAllowed);
                return;
            }
            long now = Environment.TickCount64;
            VoiceJitter jitter = s.Jitter;
            bool first = true;
            if (!VoiceWire.ReadAudio(data, (ushort seq, ReadOnlySpan<byte> frame, bool last, bool end) =>
                {
                    jitter.Put(seq, frame, first, end && last, now);
                    first = false;
                }, jitter.MarkEnd))
            {
                Interlocked.Increment(ref BadPackets);
            }
        }

        // ------------------------------------------------------------------ playback thread

        private void PlaybackLoop()
        {
            IVoicePlayback? output = null;
            try
            {
                output = _playbackFactory?.Invoke();
            }
            catch (Exception ex)
            {
                VoiceDevices.Write("voice: no output: " + ex.Message);
            }
            var pcm = new short[VoiceFormat.FrameSamples];
            var mix = new int[VoiceFormat.FrameSamples];
            var frame = new short[VoiceFormat.FrameSamples];
            long next = Environment.TickCount64;
            try
            {
                while (!_closed)
                {
                    MixFrame(pcm, mix, frame);
                    if (output != null)
                    {
                        output.Write(frame, frame.Length); // blocks: the track's pace is ours
                    }
                    else
                    {
                        next += VoiceFormat.FrameMs;
                        long wait = next - Environment.TickCount64;
                        if (wait > 0) Thread.Sleep((int)wait);
                        else if (wait < -200) next = Environment.TickCount64;
                    }
                }
            }
            catch (Exception ex) when (ex is not ThreadAbortException)
            {
                VoiceDevices.Write("voice: playback: " + ex.Message);
            }
            finally
            {
                output?.Dispose();
            }
        }

        // one 20 ms frame of everyone heard, mixed (also -voicetest's direct pull)
        public void MixFrame(short[] pcm, int[] mix, short[] frame)
        {
            Array.Clear(mix);
            float volume = VoiceSettings.Current.Volume;
            long now = Environment.TickCount64;
            foreach (Speaker? s in _speakers)
            {
                if (s == null)
                {
                    continue;
                }
                VoiceJitter.Kind kind = s.Jitter.Take(out byte[]? data);
                bool sound = false;
                if (kind != VoiceJitter.Kind.Silent && s.Allowed)
                {
                    int n = kind switch
                    {
                        VoiceJitter.Kind.Frame => s.Decoder.Decode(data, pcm),
                        VoiceJitter.Kind.Fec => s.Decoder.Decode(data, pcm, fec: true),
                        _ => s.Decoder.Conceal(pcm),
                    };
                    n = Math.Clamp(n, 0, pcm.Length);
                    for (int i = 0; i < n; i++)
                    {
                        mix[i] += (int)(pcm[i] * volume);
                    }
                    sound = kind != VoiceJitter.Kind.Conceal;
                }
                if (sound)
                {
                    s.HeardMs = now;
                }
                bool speaking = now - s.HeardMs < SpeakingHoldMs;
                if (speaking != s.Speaking)
                {
                    s.Speaking = speaking;
                    if (s.Code != null)
                    {
                        SpeakingChanged?.Invoke(s.Code, speaking);
                    }
                }
            }
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = (short)Math.Clamp(mix[i], short.MinValue, short.MaxValue);
            }
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            _net.Received -= OnReceived;
            _wake.Set();
            if (Current == this)
            {
                Current = null;
            }
            // the control thread closes the microphone; the playback thread its track (each within ~100 ms)
        }
    }
}
