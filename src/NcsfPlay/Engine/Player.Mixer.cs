using NCSFPlayer.Engine;

namespace NCSFPlayer;

// Spec Part 8: per-sample mixing, sample production, sinc interpolation, voice advance and smoothing ramps.
public sealed partial class Player
{
    /// <summary>Spec 8.1: one stereo output frame from every sounding voice.</summary>
    public (float Left, float Right) MixFrame(ushort voiceMutes)
    {
        float left = 0f;
        float right = 0f;
        bool smoothing = this.Smoothing;
        for (int i = 0; i < 16; i++)
        {
            var v = this.voices[i];
            if (!v.Active || !v.Enabled)
                continue;
            bool muted = ((voiceMutes >> i) & 1) != 0;
            float x = muted ? 0f : this.Produce(v);
            this.Advance(v);
            if (!muted)
            {
                if (smoothing)
                {
                    left += x * v.GainL;
                    right += x * v.GainR;
                }
                else
                {
                    x = Tables.Scale127(x, v.OutLevel) * Tables.Divider(v.OutDivider);
                    left += Tables.Scale127(x, (byte)(127 - v.OutPan));
                    right += Tables.Scale127(x, v.OutPan);
                }
            }
        }
        return (left, right);
    }

    /// <summary>Spec 8.2.</summary>
    float Produce(Voice v)
    {
        if (v.Position < 0)
            return 0f;

        if (v.OutFormat != 3)
        {
            if (this.Interpolation == Interpolation.None)
                return v.OutSample!.Data[(int)v.Position];
            return Sinc(v);
        }

        if (v.Number < 8)
            return 0f;
        if (v.Number < 14)
            return Tables.Square(v.OutDuty, (int)v.Position & 7);

        uint p = (uint)v.Position;
        if (v.NoiseSteps != p)
        {
            for (uint i = v.NoiseSteps; i < p; i++)
            {
                if ((v.NoiseShift & 1) != 0)
                {
                    v.NoiseShift = (ushort)((v.NoiseShift >> 1) ^ 0x6000);
                    v.NoiseOut = -1f;
                }
                else
                {
                    v.NoiseShift >>= 1;
                    v.NoiseOut = 1f;
                }
            }
            v.NoiseSteps = p;
        }
        return v.NoiseOut;
    }

    /// <summary>Spec 8.3 sinc(V).</summary>
    static float Sinc(Voice v)
    {
        float[] window = v.Window!;
        float[] kernel = SincTables.Kernel;
        float[] taper = SincTables.Window;

        int ip = (int)v.Position;
        double frac = v.Position - ip;
        int shift = (int)Math.Floor(frac * 8192);
        int step = v.Increment > 1 ? (int)(8192 / v.Increment) : 8192;
        int adj = shift * step / 8192;

        Span<float> k = stackalloc float[16];
        float ksum = 0f;
        for (int j = 8; j >= -7; j--)
        {
            float kj = kernel[Math.Abs(adj - j * step)] * taper[Math.Abs(shift - j * 8192)];
            k[j + 7] = kj;
            ksum += kj;
        }

        float acc = 0f;
        for (int j = -7; j <= 8; j++)
            acc += window[ip + j + 8] * k[j + 7];
        return acc / ksum;
    }

    /// <summary>Spec 8.3: the padded sample copy the sinc reads from.</summary>
    static void MakeWindow(Voice v)
    {
        var sample = v.OutSample!;
        ReadOnlySpan<float> data = sample.Data;
        int n = data.Length;
        float[] window = new float[n + 16];
        for (int i = 0; i < 8; i++)
            window[i] = data[0];
        data.CopyTo(window.AsSpan(8));
        if (v.OutLoopMode == 1)
            for (int i = 0; i < 8; i++)
                window[n + 8 + i] = data[(int)v.OutLoopStart + i];
        v.Window = window;
    }

    /// <summary>Spec 8.4.</summary>
    void Advance(Voice v)
    {
        bool smoothing = this.Smoothing;
        double next = v.Position + (smoothing ? v.RampInc : v.Increment);

        if (smoothing && v.RampLeft > 0)
        {
            v.RampLeft -= 1;
            if (v.RampLeft == 0)
            {
                v.GainL = v.TargetL;
                v.GainR = v.TargetR;
                v.RampInc = v.TargetInc;
            }
            else
            {
                v.GainL += v.StepL;
                v.GainR += v.StepR;
                v.RampInc += v.RampIncStep;
            }
        }

        if (v.OutFormat != 3 && this.Interpolation != Interpolation.None && v.Position < 0 && next >= 0)
            MakeWindow(v);

        v.Position = next;

        if (v.OutFormat != 3 && v.Position >= v.OutEnd)
        {
            if (v.OutLoopMode == 1)
            {
                while (v.Position >= v.OutEnd)
                    v.Position -= v.OutLoopLength;
            }
            else
            {
                VoiceEnd(v);
                v.Window = null;
            }
        }
    }

    /// <summary>
    /// Switch the output sample rate and interpolation mid-song (between frames), keeping every voice where it is: the
    /// per-frame increments and the remaining smoothing ramps are rescaled to the new rate, and switching to sinc gives
    /// each sounding sample voice the window it would have made when it started. Not part of the spec: a song played
    /// at one setting from start to end never calls this.
    /// </summary>
    public void ChangeOutput(uint sampleRate, Interpolation interpolation)
    {
        if (sampleRate == this.SampleRate && interpolation == this.Interpolation)
            return;
        double s = (double)this.SampleRate / sampleRate; // increments are source samples per output frame
        double frames = (double)sampleRate / this.SampleRate;
        bool toSinc = interpolation != Interpolation.None && this.Interpolation == Interpolation.None;
        for (int i = 0; i < 16; i++)
        {
            var v = this.voices[i];
            v.Increment *= s;
            v.TargetInc *= s;
            v.RampInc *= s;
            if (v.RampLeft > 0)
            {
                int left = (int)Math.Round(v.RampLeft * frames);
                if (left <= 0)
                {
                    v.GainL = v.TargetL;
                    v.GainR = v.TargetR;
                    v.RampInc = v.TargetInc;
                    v.RampLeft = 0;
                }
                else
                {
                    v.StepL = (v.TargetL - v.GainL) / left;
                    v.StepR = (v.TargetR - v.GainR) / left;
                    v.RampIncStep = (v.TargetInc - v.RampInc) / left;
                    v.RampLeft = left;
                }
            }
            else
                v.RampIncStep *= s;
            // a voice past its lead-in has no window yet (or a stale one) when it started without interpolation
            if (toSinc && v.OutFormat != 3 && v.OutSample is not null && v.Position >= 0)
                MakeWindow(v);
        }
        this.SampleRate = sampleRate;
        this.Interpolation = interpolation;
    }

    /// <summary>
    /// Turn smoothing and exact pitch on or off mid-song (like <see cref="ChangeOutput" />, not part of the spec):
    /// smoothing starts from the voices' current levels instead of ramping from stale ones, and every voice's increment
    /// is recomputed on the next tick.
    /// </summary>
    public void ChangeFixes(bool smoothing, bool exactPitch)
    {
        if (exactPitch != this.ExactPitch)
        {
            foreach (var v in this.voices)
                v.LastTimer = 0; // a timer never matches 0, so the next update sets the increment again
            this.ExactPitch = exactPitch;
        }
        if (smoothing && !this.Smoothing)
            foreach (var v in this.voices)
            {
                float g = Tables.Scale127(1f, v.OutLevel) * Tables.Divider(v.OutDivider);
                v.TargetL = v.GainL = Tables.Scale127(g, (byte)(127 - v.OutPan));
                v.TargetR = v.GainR = Tables.Scale127(g, v.OutPan);
                v.TargetInc = v.RampInc = v.Increment;
                v.RampLeft = 0;
                v.WasOn = v.Active && v.Enabled;
            }
        this.Smoothing = smoothing;
    }

    /// <summary>Spec 8.5: smoothing ramps, called after each tick and once before the first frame.</summary>
    public void BeginRamps(int samplesPerTick)
    {
        for (int i = 0; i < 16; i++)
        {
            var v = this.voices[i];
            float g = Tables.Scale127(1f, v.OutLevel) * Tables.Divider(v.OutDivider);
            v.TargetL = Tables.Scale127(g, (byte)(127 - v.OutPan));
            v.TargetR = Tables.Scale127(g, v.OutPan);
            v.TargetInc = v.Increment;

            bool on = v.Active && v.Enabled;
            if (on && !v.WasOn)
            {
                v.GainL = v.TargetL;
                v.GainR = v.TargetR;
                v.RampInc = v.TargetInc;
                v.RampLeft = 0;
            }
            else
            {
                v.StepL = (v.TargetL - v.GainL) / samplesPerTick;
                v.StepR = (v.TargetR - v.GainR) / samplesPerTick;
                v.RampIncStep = (v.TargetInc - v.RampInc) / samplesPerTick;
                v.RampLeft = samplesPerTick;
            }
            v.WasOn = on;
        }
    }
}
