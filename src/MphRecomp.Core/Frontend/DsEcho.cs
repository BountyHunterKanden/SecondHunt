using System;
using System.Diagnostics;

namespace MphRecomp.Frontend
{
    // The DS sound engine's capture echo (ARM9 NNS_SndCaptureStartEffect callback, rev 1 0x207016c / rev 0 0x206f918),
    // which the game runs over its whole output, music and sound effects alike, on the ship screens only: the in-ship
    // menu (mode 12 init, rev 1 0x2111558) and the planet select (0x21056c8) select preset 0, the briefing preset 1 (no
    // echo, 0x2106134). It doesn't run in game, on the title, main menu or file select (BizHawk USA rev 1, 2026-10-01: the
    // callback is never called there). It is what carries the planet select's ship thrust on after its stop: ~1.5 s of
    // 125 ms repeats. Traced and measured: handoffs/start-menu-ui-rebuild_2026-10-01-files/sound_census/thrust/report.md.
    //
    // Per DS sample (32.73 kHz, in 256-sample blocks): A <- L + R; x = A[-pre*512] + B[-comb*512] * fb;
    // B <- 5-tap low-pass(x); L' = L*dry + B[-tapL*512]*wet, R' likewise with tapR (all /4096). Here it runs at the output's
    // own rate with the delays scaled in time (512 DS samples = 15.64 ms) and a 15-tap filter with the DS filter's response.
    // The control is shared by every output (the SFX mixer and the music player each run a DsEcho of their own; the
    // echo is linear, so the sum is the DS's echo of the mix).
    public sealed class DsEcho
    {
        // a row of the ROM's table (rev 1 0x020CBB64 / rev 0 0x020CB2DC: flag, dry, wet, pre, comb, fb, firType,
        // firCutoff, tapL, tapR); Full = flag 1: a preset with flag 0 only moves dry and wet
        public readonly record struct Preset(bool Full, int Dry, int Wet, int Pre, int Comb, int Feedback, int TapL, int TapR);

        public static readonly Preset[] Presets =
        {
            new(true, 4096, 2048, 0, 8, 3072, 1, 4),
            new(false, 4096, 0, 0, 0, 0, 0, 0),
            new(true, 4096, 800, 6, 7, 3200, 0, 3),
        };

        // what a DsEcho applies right now
        private readonly record struct Params(bool Running, float Dry, float Wet, float Feedback, int Pre, int Comb, int TapL, int TapR);

        // the DS rate the delays are counted at: 4096 samples measured 125.2 ms in melonDS (the code asks for 32000 Hz)
        private const double DsRate = 32728;
        // SetEchoPreset's crossfade: 30 frames
        private const double FadeSeconds = 0.5;

        private static readonly object _gate = new();
        private static Params _from, _to;
        private static long _changedAt;
        private static int _generation;

        // SetEchoPreset(index) (rev 1 0x2071e40 / rev 0 0x20715ec): from where it is now to the preset over 30 frames
        // (dry, wet and feedback move; the delays of a full preset switch at once). Starts the effect if it isn't running.
        public static void SetPreset(int index)
        {
            Preset p = Presets[Math.Clamp(index, 0, Presets.Length - 1)];
            lock (_gate)
            {
                Params now = CurrentLocked();
                if (!now.Running)
                {
                    // a fresh effect starts dry (no echo yet) and fades in
                    now = new Params(true, 1, 0, p.Feedback / 4096f, p.Pre, p.Comb, p.TapL, p.TapR);
                    _generation++;
                }
                _from = now;
                _to = p.Full
                    ? new Params(true, p.Dry / 4096f, p.Wet / 4096f, p.Feedback / 4096f, p.Pre, p.Comb, p.TapL, p.TapR)
                    : now with { Dry = p.Dry / 4096f, Wet = p.Wet / 4096f };
                if (p.Full)
                {
                    _from = _from with { Pre = p.Pre, Comb = p.Comb, TapL = p.TapL, TapR = p.TapR };
                }
                _changedAt = Stopwatch.GetTimestamp();
            }
        }

        // the effect torn down (the game leaves the ship screens): no echo, and the next start begins with empty buffers
        public static void Stop()
        {
            lock (_gate)
            {
                _from = _to = default;
                _generation++;
            }
        }

        public static bool Running
        {
            get { lock (_gate) { return _to.Running; } }
        }

        private static Params CurrentLocked()
        {
            if (!_to.Running) return default;
            double t = Math.Min(1, (Stopwatch.GetTimestamp() - _changedAt) / (double)Stopwatch.Frequency / FadeSeconds);
            if (t >= 1) return _to;
            float k = (float)t;
            return _to with
            {
                Dry = _from.Dry + (_to.Dry - _from.Dry) * k,
                Wet = _from.Wet + (_to.Wet - _from.Wet) * k,
                Feedback = _from.Feedback + (_to.Feedback - _from.Feedback) * k
            };
        }

        private readonly int _unit, _lenA, _lenB;
        private readonly float[] _a, _b;
        private readonly float[] _fir, _hist;
        private int _ia, _ib, _gen = -1;

        public DsEcho(int sampleRate)
        {
            _unit = Math.Max(1, (int)Math.Round(512 * sampleRate / DsRate));
            _lenA = 12 * _unit; // the DS's A ring: 6144 samples
            _lenB = 8 * _unit; // B: 4096
            _a = new float[_lenA];
            _b = new float[_lenB];
            _fir = DesignFilter(sampleRate);
            _hist = new float[_fir.Length];
        }

        // The DS filter (read from RAM, preset 0's 4 kHz: 651, 921, 1024, 921, 651 / 4096 at 32.73 kHz) has the response
        // H(f) = (1024 + 1842 cos w + 1302 cos 2w) / 4096, w = 2 pi f / 32728; nothing above the DS's Nyquist. A 15-tap
        // symmetric FIR at this rate with that response (frequency sampling, no window: a Hann window cost 11% of the
        // gain inside the feedback loop and the tail fell 29 dB/s instead of the DS's ~19), then scaled to the DS's DC
        // gain exactly (4168/4096): within 1% of the DS's response up to 4 kHz at 48 kHz (Tools -sndecho checks the tail).
        private static float[] DesignFilter(int rate)
        {
            const int m = 7;
            var h = new double[m + 1];
            const int steps = 4096;
            for (int k = 0; k <= m; k++)
            {
                double sum = 0;
                for (int s = 0; s < steps; s++)
                {
                    double nu = Math.PI * (s + 0.5) / steps; // 0..pi at this rate
                    double f = nu / (2 * Math.PI) * rate;
                    double target = 0;
                    if (f < DsRate / 2)
                    {
                        double w = 2 * Math.PI * f / DsRate;
                        target = (1024 + 1842 * Math.Cos(w) + 1302 * Math.Cos(2 * w)) / 4096;
                    }
                    sum += target * Math.Cos(k * nu);
                }
                h[k] = sum / steps;
            }
            double dc = h[0];
            for (int k = 1; k <= m; k++) dc += 2 * h[k];
            double scale = 4168 / 4096.0 / dc;
            var taps = new float[2 * m + 1];
            for (int k = -m; k <= m; k++)
            {
                taps[k + m] = (float)(h[Math.Abs(k)] * scale);
            }
            return taps;
        }

        // planar buffers (the SFX mixer)
        public void Process(float[] left, float[] right, int count)
        {
            Params p;
            int gen;
            lock (_gate)
            {
                p = CurrentLocked();
                gen = _generation;
            }
            if (!p.Running) return;
            if (gen != _gen)
            {
                Array.Clear(_a);
                Array.Clear(_b);
                Array.Clear(_hist);
                _ia = _ib = 0;
                _gen = gen;
            }
            // the DS callback returns untouched (and leaves its rings alone) with no wet signal at full dry
            if (p.Wet == 0 && p.Dry == 1) return;
            for (int i = 0; i < count; i++)
            {
                (left[i], right[i]) = Step(p, left[i], right[i]);
            }
        }

        // interleaved stereo (the music player)
        public void ProcessInterleaved(float[] lr, int frames)
        {
            Params p;
            int gen;
            lock (_gate)
            {
                p = CurrentLocked();
                gen = _generation;
            }
            if (!p.Running) return;
            if (gen != _gen)
            {
                Array.Clear(_a);
                Array.Clear(_b);
                Array.Clear(_hist);
                _ia = _ib = 0;
                _gen = gen;
            }
            if (p.Wet == 0 && p.Dry == 1) return;
            for (int i = 0; i < frames; i++)
            {
                (lr[2 * i], lr[2 * i + 1]) = Step(p, lr[2 * i], lr[2 * i + 1]);
            }
        }

        private (float L, float R) Step(in Params p, float l, float r)
        {
            _a[_ia] = l + r;
            float x = _a[(_ia - p.Pre * _unit % _lenA + _lenA) % _lenA] + _b[(_ib - p.Comb * _unit % _lenB + _lenB) % _lenB] * p.Feedback;
            Array.Copy(_hist, 0, _hist, 1, _hist.Length - 1);
            _hist[0] = x;
            float y = 0;
            for (int k = 0; k < _fir.Length; k++)
            {
                y += _fir[k] * _hist[k];
            }
            _b[_ib] = y;
            float outL = l * p.Dry + _b[(_ib - p.TapL * _unit % _lenB + _lenB) % _lenB] * p.Wet;
            float outR = r * p.Dry + _b[(_ib - p.TapR * _unit % _lenB + _lenB) % _lenB] * p.Wet;
            _ia = (_ia + 1) % _lenA;
            _ib = (_ib + 1) % _lenB;
            return (outL, outR);
        }
    }
}
