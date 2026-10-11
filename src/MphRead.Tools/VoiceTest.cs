using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;
using MphRecomp.Online.Voice;

namespace MphRead
{
    // -voicetest [loss=<0..1>] [jitter=<ms>] [delay=<ms>] (O8 voice chat, PC only, no network or sound device):
    //  1. codec: Opus (Concentus) round trip of a speech-like signal: bytes per frame, encode/decode time, level and shape kept
    //  2. jitter: VoiceJitter on simulated time with loss + jitter: frames heard (played or rebuilt by FEC), latency
    //  3. end to end: three VoiceChat devices on an in-memory relay (loss/jitter/delay), fake microphones and speakers in
    //     real time: friends hear each other, strangers get nothing sent (default off), a MUTE (PlayerMutes) silences within
    //     ~250 ms, push to talk sends only while held, mouth-to-ear latency
    // -voicetest live [server=wss://...]: two PC players in a game (listed ~10 s under version "voicetest") on a real server (default the live one),
    //  voice through its UDP relay (fresh identities = strangers, so strangers on for the test): A talks with an open mic,
    //  B must hear it; the slots come from the server's cards (0.5.0) or the hellos (older servers)
    internal static class VoiceTest
    {
        public static void Run(string[] args)
        {
            if (args.Contains("live"))
            {
                Live(args.FirstOrDefault(a => a.StartsWith("server="))?[7..] ?? "wss://play.secondhuntonline.com/v1/ws");
                return;
            }
            double loss = Arg(args, "loss", 0.05), jitter = Arg(args, "jitter", 40), delay = Arg(args, "delay", 60);
            int fails = 0;
            fails += Codec();
            fails += JitterSim(0, 0, 50, strict: true);
            fails += JitterSim(loss, jitter, delay, strict: false);
            fails += JitterSim(0.15, 80, delay, strict: false, label: "harsh");
            fails += EndToEnd(loss, jitter, delay);
            Console.WriteLine(fails == 0 ? "voicetest: PASS" : $"voicetest: FAIL ({fails})");
        }

        private static double Arg(string[] args, string name, double fallback)
        {
            string? a = args.FirstOrDefault(x => x.StartsWith(name + "="));
            return a != null && double.TryParse(a[(name.Length + 1)..], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;
        }

        private static int Check(bool ok, string what)
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            return ok ? 0 : 1;
        }

        // a voiced, speech-like signal: harmonics of a gliding pitch under a syllable envelope, pauses between words
        private static short Speech(long n, double amp = 6000)
        {
            double t = n / (double)VoiceFormat.Rate;
            double word = t % 1.0;
            if (word > 0.6)
            {
                return 0;
            }
            double f0 = 130 + 40 * Math.Sin(t * 2.1);
            double env = Math.Sin(Math.PI * word / 0.6) * (0.7 + 0.3 * Math.Sin(2 * Math.PI * 4 * t));
            double v = 0;
            for (int h = 1; h <= 12; h++)
            {
                double formant = Math.Exp(-Math.Pow((h * f0 - 600) / 400, 2)) + 0.5 * Math.Exp(-Math.Pow((h * f0 - 1700) / 500, 2));
                v += (0.15 + formant) / h * Math.Sin(2 * Math.PI * h * f0 * t + h);
            }
            return (short)Math.Clamp(amp * env * v, short.MinValue, short.MaxValue);
        }

        // ---------------------------------------------------------------- 1. codec

        private static int Codec()
        {
            Console.WriteLine("codec (Opus via Concentus, 16 kHz mono, 16 kbps, 20 ms frames):");
            var enc = new VoiceEncoder();
            var dec = new VoiceDecoder();
            int frames = 250; // 5 s
            var input = new short[frames * VoiceFormat.FrameSamples];
            var output = new short[input.Length];
            var pcm = new short[VoiceFormat.FrameSamples];
            var outFrame = new short[VoiceFormat.FrameSamples];
            var buf = new byte[VoiceFormat.MaxFrameBytes];
            long bytes = 0, maxBytes = 0;
            var sw = new Stopwatch();
            double encMs = 0, decMs = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int i = 0; i < pcm.Length; i++)
                {
                    pcm[i] = input[f * pcm.Length + i] = Speech(f * pcm.Length + i);
                }
                sw.Restart();
                int n = enc.Encode(pcm, buf);
                encMs += sw.Elapsed.TotalMilliseconds;
                bytes += n;
                maxBytes = Math.Max(maxBytes, n);
                sw.Restart();
                dec.Decode(buf.AsSpan(0, n), outFrame);
                decMs += sw.Elapsed.TotalMilliseconds;
                outFrame.CopyTo(output, f * pcm.Length);
            }
            // Opus delays the signal by its look-ahead: find the lag, then compare shape (correlation) and level
            int bestLag = 0;
            double best = -1;
            for (int lag = 0; lag < 600; lag += 2)
            {
                double c = Correlation(input, output, lag);
                if (c > best)
                {
                    best = c;
                    bestLag = lag;
                }
            }
            double level = Rms(output, bestLag, output.Length) / Math.Max(1, Rms(input, 0, input.Length - bestLag));
            Console.WriteLine($"  {bytes / (double)frames:0.0} B/frame avg ({bytes * 8 / (frames * 0.02) / 1000:0.0} kbps), max {maxBytes} B; "
                + $"encode {encMs / frames:0.000} ms, decode {decMs / frames:0.000} ms per frame (PC); lag {bestLag} samples, "
                + $"correlation {best:0.000}, level {20 * Math.Log10(level):+0.0;-0.0} dB");
            int fails = 0;
            fails += Check(maxBytes <= VoiceFormat.MaxFrameBytes, $"frames fit the wire's {VoiceFormat.MaxFrameBytes} B");
            fails += Check(bytes * 8 / (frames * 0.02) < 24000, "bitrate under 24 kbps");
            fails += Check(best > 0.6, "decoded speech keeps its shape (correlation > 0.6)");
            fails += Check(Math.Abs(20 * Math.Log10(level)) < 3, "level within 3 dB");
            fails += Check(encMs / frames < 5, "encode well under real time");
            // concealment and a malformed frame never throw
            int c1 = dec.Conceal(outFrame), c2 = dec.Decode(new byte[] { 1 }, outFrame);
            fails += Check(c1 == VoiceFormat.FrameSamples && c2 == VoiceFormat.FrameSamples, "conceal + bad frame give a frame");
            return fails;
        }

        private static double Correlation(short[] a, short[] b, int lag)
        {
            double ab = 0, aa = 0, bb = 0;
            for (int i = 0; i + lag < b.Length; i++)
            {
                ab += a[i] * (double)b[i + lag];
                aa += a[i] * (double)a[i];
                bb += b[i + lag] * (double)b[i + lag];
            }
            return ab / Math.Sqrt(Math.Max(1, aa * bb));
        }

        private static double Rms(short[] x, int from, int to)
        {
            double s = 0;
            for (int i = from; i < to; i++) s += x[i] * (double)x[i];
            return Math.Sqrt(s / Math.Max(1, to - from));
        }

        // ---------------------------------------------------------------- 2. jitter buffer, simulated time

        private static int JitterSim(double loss, double jitter, double delay, bool strict, string? label = null)
        {
            Console.WriteLine($"jitter buffer{(label != null ? $" ({label})" : "")}: loss {loss:0%}, jitter +-{jitter} ms, delay {delay} ms (simulated 30 s):");
            var rnd = new Random(7);
            var jb = new VoiceJitter();
            // the sender: talk spurts (2-4 s) and pauses; frames are 2-byte seqs, two to a packet, end flag on the last
            var arrivals = new List<(long At, ushort Seq, int Count, bool End)>();
            var sentAt = new Dictionary<ushort, long>();
            ushort seq = 0;
            int sent = 0;
            long t = 0;
            while (t < 30_000)
            {
                int spurt = rnd.Next(100, 200); // frames
                for (int f = 0; f < spurt; f += 2)
                {
                    int count = Math.Min(2, spurt - f);
                    bool end = f + count >= spurt;
                    long sendTime = t + (f + count) * VoiceFormat.FrameMs; // a packet leaves once its frames are captured
                    for (int k = 0; k < count; k++) sentAt[(ushort)(seq + k)] = t + (f + k) * VoiceFormat.FrameMs;
                    if (rnd.NextDouble() >= loss)
                    {
                        arrivals.Add((sendTime + (long)(delay + (rnd.NextDouble() * 2 - 1) * jitter), seq, count, end));
                    }
                    seq += (ushort)count;
                    sent += count;
                }
                t += spurt * VoiceFormat.FrameMs + rnd.Next(300, 1500);
            }
            arrivals.Sort((a, b) => a.At.CompareTo(b.At));
            int next = 0, heard = 0, fec = 0;
            var latencies = new List<double>();
            var frame = new byte[2];
            for (long now = 0; now < t + 2000; now += VoiceFormat.FrameMs)
            {
                while (next < arrivals.Count && arrivals[next].At <= now)
                {
                    var a = arrivals[next++];
                    for (int k = 0; k < a.Count; k++)
                    {
                        ushort s = (ushort)(a.Seq + k);
                        frame[0] = (byte)s;
                        frame[1] = (byte)(s >> 8);
                        jb.Put(s, frame, k == 0, a.End && k == a.Count - 1, now);
                    }
                }
                VoiceJitter.Kind kind = jb.Take(out byte[]? data);
                if (kind == VoiceJitter.Kind.Frame)
                {
                    ushort s = (ushort)(data![0] | data[1] << 8);
                    heard++;
                    latencies.Add(now - sentAt[s]);
                }
                else if (kind == VoiceJitter.Kind.Fec)
                {
                    heard++;
                    fec++;
                }
            }
            double heardPct = heard / (double)sent;
            double mean = latencies.Count > 0 ? latencies.Average() : 0, max = latencies.Count > 0 ? latencies.Max() : 0;
            Console.WriteLine($"  sent {sent} frames, heard {heard} ({heardPct:0.0%}; {fec} rebuilt by FEC), concealed {jb.Concealed}, "
                + $"late {jb.Late}, dropped {jb.Dropped}, spurts {jb.Spurts}; latency mean {mean:0} ms max {max:0} ms, final target {jb.Target}");
            int fails = 0;
            if (strict)
            {
                fails += Check(heard == sent && jb.Concealed == 0, "clean link: every frame heard, none concealed");
                fails += Check(max <= delay + (VoiceJitter.MinTarget + 1) * VoiceFormat.FrameMs + 40, "clean link: latency = delay + ~80 ms");
            }
            else
            {
                double expect = Math.Min(0.93, 1 - loss * 0.9);
                fails += Check(heardPct >= expect - 0.02, $"heard >= {expect - 0.02:0%} of what was sent");
                fails += Check(mean < delay + 220, $"mean latency < {delay + 220:0} ms");
            }
            return fails;
        }

        // ---------------------------------------------------------------- 3. end to end, real time

        private sealed class Hub : IDisposable
        {
            public readonly List<End> Ends = new();
            public readonly int[,] Count = new int[8, 8]; // voice packets from -> to
            private readonly PriorityQueue<(End To, int From, byte[] Data), long> _queue = new();
            private readonly object _lock = new();
            private readonly Random _rnd = new(3);
            private readonly Thread _thread;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private volatile bool _closed;
            public double Loss, Jitter, Delay;

            public Hub()
            {
                _thread = new Thread(Deliver) { IsBackground = true };
                _thread.Start();
            }

            public End Add(int slot)
            {
                var e = new End(this, slot);
                Ends.Add(e);
                return e;
            }

            public void Send(int from, int to, ReadOnlySpan<byte> data)
            {
                End? dest = Ends.FirstOrDefault(e => e.LocalSlot == to);
                lock (_lock)
                {
                    if (data.Length > 0 && data[0] == MatchNetVoice.MsgVoice && data.Length > 1 && data[1] == VoiceWire.Audio)
                    {
                        Count[from, to]++;
                    }
                    if (dest == null || _rnd.NextDouble() < Loss)
                    {
                        return;
                    }
                    long due = _clock.ElapsedMilliseconds + (long)Math.Max(0, Delay + (_rnd.NextDouble() * 2 - 1) * Jitter);
                    _queue.Enqueue((dest, from, data.ToArray()), due);
                }
            }

            private void Deliver()
            {
                while (!_closed)
                {
                    var ready = new List<(End, int, byte[])>();
                    lock (_lock)
                    {
                        while (_queue.TryPeek(out var item, out long due) && due <= _clock.ElapsedMilliseconds)
                        {
                            ready.Add(_queue.Dequeue());
                        }
                    }
                    foreach ((End to, int from, byte[] data) in ready)
                    {
                        to.Raise(from, data);
                    }
                    Thread.Sleep(1);
                }
            }

            public void Dispose() => _closed = true;
        }

        private sealed class End : IDatagramTransport
        {
            private readonly Hub _hub;
            public End(Hub hub, int slot)
            {
                _hub = hub;
                LocalSlot = slot;
            }
            public int LocalSlot { get; }
            public int MaxPayload => 1400;
            public event DatagramHandler? Received;
            public void Send(int slot, ReadOnlySpan<byte> payload) => _hub.Send(LocalSlot, slot, payload);
            public void SendAll(ReadOnlySpan<byte> payload)
            {
                foreach (End e in _hub.Ends.Where(e => e != this)) _hub.Send(LocalSlot, e.LocalSlot, payload);
            }
            public void Raise(int from, byte[] data) => Received?.Invoke(from, data);
            public void Dispose() { }
        }

        private sealed class Room : IVoiceRoom
        {
            public Room(IDatagramTransport t, string code, List<PlayerCard> members)
            {
                Transport = t;
                MyCode = code;
                _members = members;
            }
            private readonly List<PlayerCard> _members;
            public IDatagramTransport Transport { get; }
            public string MyCode { get; }
            public IReadOnlyList<PlayerCard> Members() => _members;
            public bool Closed => false;
        }

        // a microphone in real time: speech-like words (0.6 s on, 0.4 s off) over a little room noise; logs word onsets
        private sealed class FakeMic : IVoiceCapture
        {
            private Thread? _thread;
            private volatile bool _run;
            public readonly List<long> Onsets = new();
            public static readonly Stopwatch Clock = Stopwatch.StartNew();
            public bool Running => _run;
            public string? Error => null;

            public bool Start(Action<short[]> frame)
            {
                _run = true;
                _thread = new Thread(() =>
                {
                    var rnd = new Random(11);
                    var pcm = new short[VoiceFormat.FrameSamples];
                    long n = 0, start = Clock.ElapsedMilliseconds;
                    int frames = 0;
                    while (_run)
                    {
                        for (int i = 0; i < pcm.Length; i++, n++)
                        {
                            pcm[i] = (short)(Speech(n) + rnd.Next(-60, 60));
                            if (n % VoiceFormat.Rate == 0) lock (Onsets) Onsets.Add(Clock.ElapsedMilliseconds);
                        }
                        frame(pcm);
                        frames++;
                        long wait = start + frames * VoiceFormat.FrameMs - Clock.ElapsedMilliseconds;
                        if (wait > 0) Thread.Sleep((int)wait);
                    }
                }) { IsBackground = true };
                _thread.Start();
                return true;
            }

            public void Stop() => _run = false;
            public void Dispose() => _run = false;
        }

        // a speaker in real time: Write blocks like a track would; logs when sound starts after a quiet stretch
        private sealed class FakeSpeaker : IVoicePlayback
        {
            private long _start = -1, _written;
            private int _quiet = 100;
            public readonly List<long> Onsets = new();
            public long LoudFrames;

            public void Write(short[] samples, int count)
            {
                if (_start < 0) _start = FakeMic.Clock.ElapsedMilliseconds;
                double rms = Rms(samples, 0, count);
                if (rms > 800)
                {
                    LoudFrames++;
                    if (_quiet >= 5) lock (Onsets) Onsets.Add(FakeMic.Clock.ElapsedMilliseconds);
                    _quiet = 0;
                }
                else
                {
                    _quiet++;
                }
                _written += count;
                long due = _start + _written * 1000 / VoiceFormat.Rate - 40; // a 40 ms track buffer ahead of the "speaker"
                long wait = due - FakeMic.Clock.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
            }

            public void Dispose() { }
        }

        private static int EndToEnd(double loss, double jitter, double delay)
        {
            Console.WriteLine($"end to end (3 devices, in-memory relay, loss {loss:0%}, jitter +-{jitter} ms, delay {delay} ms, real time):");
            const string A = "111111111111", B = "222222222222", C = "333333333333";
            var hub = new Hub { Loss = loss, Jitter = jitter, Delay = delay };
            var cards = new Func<string, int, bool, PlayerCard>((code, slot, friend) => new PlayerCard { Code = code, Slot = slot, Friend = friend });
            // A and B are friends; C is a stranger to both
            var micA = new FakeMic();
            var spkB = new FakeSpeaker();
            var spkC = new FakeSpeaker();
            VoiceSettings.Current.SetMode(VoiceMode.OpenMic);
            VoiceSettings.Current.SetStrangers(false);
            VoiceSettings.Current.SetFriends(true);
            var va = new VoiceChat(new Room(hub.Add(0), A, new() { cards(A, 0, false), cards(B, 1, true), cards(C, 2, false) }), () => micA, () => new FakeSpeaker());
            var vb = new VoiceChat(new Room(hub.Add(1), B, new() { cards(A, 0, true), cards(B, 1, false), cards(C, 2, false) }), () => null, () => spkB);
            // C's cards from an old server (no slots): it learns slots from hellos
            var vc = new VoiceChat(new Room(hub.Add(2), C, new() { new PlayerCard { Code = A }, new PlayerCard { Code = B }, new PlayerCard { Code = C } }),
                () => null, () => spkC);
            int fails = 0;
            try
            {
                // open mic, 4 s: B hears A; nothing goes to C
                Thread.Sleep(4000);
                int toB = hub.Count[0, 1], toC = hub.Count[0, 2];
                long loudB = spkB.LoudFrames;
                List<double> lat = Latencies(micA.Onsets, spkB.Onsets);
                Console.WriteLine($"  open mic 4 s: A->B {toB} packets, A->C {toC}; B heard {loudB} loud frames, onsets {spkB.Onsets.Count}, "
                    + $"mouth-to-ear {(lat.Count > 0 ? $"{lat.Average():0} ms avg, {lat.Max():0} max" : "n/a")}");
                Console.WriteLine("  " + vb.Summary());
                fails += Check(toB >= 50, "open mic: A's words go to B (friend)");
                fails += Check(toC == 0, "open mic: nothing goes to C (stranger, off by default)");
                fails += Check(loudB >= 60, "B hears A");
                fails += Check(lat.Count >= 2 && lat.Average() < delay + 200, $"mouth-to-ear < {delay + 200:0} ms (avg)");
                fails += Check(spkC.LoudFrames == 0, "C hears nothing");
                fails += Check(vb.Speaking(A) || spkB.Onsets.Count > 0, "B shows A speaking");
                // the pauses between words aren't sent (the open-mic gate): about 60% of the time on air
                double duty = toB / (4000.0 / (VoiceFormat.FrameMs * VoiceFormat.FramesPerPacket));
                fails += Check(duty < 0.9, $"open-mic gate: {duty:0%} of the time on air (pauses not sent)");

                // B mutes A (Social O7's mute): silence within ~250 ms; A's packets are dropped at B
                PlayerMutes.Current.SetVoice(A, true);
                Thread.Sleep(400);
                long loudAtMute = spkB.LoudFrames;
                int dropped0 = vb.NotAllowed;
                Thread.Sleep(1500);
                Console.WriteLine($"  muted: B loud frames +{spkB.LoudFrames - loudAtMute}, dropped at B +{vb.NotAllowed - dropped0}");
                fails += Check(spkB.LoudFrames - loudAtMute == 0, "MUTE: B hears nothing from A");
                PlayerMutes.Current.SetVoice(A, false);

                // C turns A on (per player) but A still has C off: nothing either way until both are on
                VoiceSettings.Current.SetPlayer(A, true);
                Thread.Sleep(1000);
                fails += Check(hub.Count[0, 2] == 0, "one side on isn't enough: A still sends nothing to C");
                VoiceSettings.Current.SetPlayer(C, true);
                Thread.Sleep(1500);
                fails += Check(hub.Count[0, 2] > 10 && spkC.LoudFrames > 0, $"both on: C hears A ({hub.Count[0, 2]} packets)");
                VoiceSettings.Current.SetPlayer(A, null);
                VoiceSettings.Current.SetPlayer(C, null);

                // push to talk: only while held (+ a 150 ms tail)
                VoiceSettings.Current.SetMode(VoiceMode.PushToTalk);
                Thread.Sleep(500);
                int before = hub.Count[0, 1];
                Thread.Sleep(1000);
                int idle = hub.Count[0, 1] - before;
                va.PushToTalk(true);
                Thread.Sleep(1500);
                va.PushToTalk(false);
                Thread.Sleep(600);
                int held = hub.Count[0, 1] - before - idle;
                Console.WriteLine($"  push to talk: {idle} packets while not held, {held} for a 1.5 s hold (mic {(va.MicOpen ? "open" : "closed")})");
                fails += Check(idle == 0, "push to talk: nothing sent while not held");
                fails += Check(held >= 30 && held <= 45, "push to talk: ~1.5 s sent for a 1.5 s hold (incl. mic start)");
                Thread.Sleep(3200);
                fails += Check(!va.MicOpen, "push to talk: microphone closed 3 s after the release");
                Console.WriteLine("  " + va.Summary());
                Console.WriteLine("  " + vc.Summary());
            }
            finally
            {
                va.Dispose();
                vb.Dispose();
                vc.Dispose();
                hub.Dispose();
                VoiceSettings.Current.SetMode(VoiceMode.PushToTalk);
            }
            return fails;
        }

        // ---------------------------------------------------------------- live: through a real server's relay

        private static int Live(string server)
        {
            Console.WriteLine($"live voice through {server}:");
            var uri = new Uri(server);
            VoiceSettings.Current.SetMode(VoiceMode.OpenMic);
            VoiceSettings.Current.SetStrangers(true);
            OnlineClient? ca = null, cb = null;
            RelayTransport? ra = null, rb = null;
            VoiceChat? va = null, vb = null;
            int fails = 0;
            try
            {
                ca = OnlineClient.ConnectAsync(uri, OnlineIdentity.Create(), "voicetest A", "voicetest").GetAwaiter().GetResult();
                cb = OnlineClient.ConnectAsync(uri, OnlineIdentity.Create(), "voicetest B", "voicetest").GetAwaiter().GetResult();
                Console.WriteLine($"  server {ca.ServerVersion}; A = {ca.Me.Code}, B = {cb.Me.Code}");
                // listed for these ~10 s (invites need a friend), under a game version no app has, so QUICK MATCH skips it
                GameMsg ga = ca.RequestAsync<GameMsg>(new HostGameReq { Info = new GameInfo { Mode = "Battle", Arena = "voice test (PC)",
                    MaxPlayers = 2, Public = true, Friends = false, Rivals = false, Version = "voicetest" } }).GetAwaiter().GetResult();
                var membersA = new List<PlayerCard>(ga.Game.Members);
                ca.EventReceived += m =>
                {
                    if (m is MemberEvent e && e.Joined)
                    {
                        lock (membersA) membersA.Add(e.Player);
                    }
                };
                GameMsg gb = cb.RequestAsync<GameMsg>(new JoinGameReq { GameId = ga.Game.Id, Version = "voicetest" }).GetAwaiter().GetResult();
                var membersB = new List<PlayerCard>(gb.Game.Members);
                Console.WriteLine($"  game {ga.Game.Id}: A slot {ga.Relay.Slot}, B slot {gb.Relay.Slot}; B's cards: "
                    + string.Join(", ", membersB.Select(m => $"{m.Code} slot {(m.Slot?.ToString() ?? "-")}")));
                ra = RelayTransport.For(ga.Relay, uri);
                rb = RelayTransport.For(gb.Relay, uri);
                var mic = new FakeMic();
                var ear = new FakeSpeaker();
                va = new VoiceChat(new Room(ra, ca.Me.Code, membersA), () => mic, () => new FakeSpeaker());
                vb = new VoiceChat(new Room(rb, cb.Me.Code, membersB), () => null, () => ear);
                Thread.Sleep(7000);
                List<double> lat = Latencies(mic.Onsets, ear.Onsets);
                Console.WriteLine($"  B heard {ear.LoudFrames} loud frames, {ear.Onsets.Count} word onsets; mouth-to-ear "
                    + (lat.Count > 0 ? $"{lat.Average():0} ms avg, {lat.Min():0}-{lat.Max():0}" : "n/a") + $"; relay ping A {ra.PingMs} ms, B {rb.PingMs} ms");
                Console.WriteLine("  A " + va.Summary());
                Console.WriteLine("  B " + vb.Summary());
                fails += Check(va.PacketsSent > 50, "A sent its words over the relay");
                fails += Check(ear.LoudFrames > 100 && ear.Onsets.Count >= 3, "B heard A");
                fails += Check(vb.BadPackets == 0, "no malformed voice packets");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  FAIL " + ex.Message);
                fails++;
            }
            finally
            {
                va?.Dispose();
                vb?.Dispose();
                ra?.Dispose();
                rb?.Dispose();
                foreach (OnlineClient? c in new[] { ca, cb })
                {
                    try
                    {
                        c?.RequestAsync<OkMsg>(new LeaveGameReq()).Wait(3000);
                        c?.DisposeAsync().AsTask().Wait(3000);
                    }
                    catch (Exception) { }
                }
                VoiceSettings.Current.SetStrangers(false);
                VoiceSettings.Current.SetMode(VoiceMode.PushToTalk);
            }
            Console.WriteLine(fails == 0 ? "voicetest live: PASS" : $"voicetest live: FAIL ({fails})");
            return fails;
        }

        // each heard onset matched to the latest word onset before it
        private static List<double> Latencies(List<long> said, List<long> heard)
        {
            var result = new List<double>();
            lock (said)
            lock (heard)
            {
                foreach (long h in heard)
                {
                    long s = said.Where(x => x <= h).DefaultIfEmpty(-1).Max();
                    if (s >= 0 && h - s < 1000) result.Add(h - s);
                }
            }
            return result;
        }
    }
}
