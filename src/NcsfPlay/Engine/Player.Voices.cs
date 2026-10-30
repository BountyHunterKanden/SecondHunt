using NCSFCommon;
using NCSFPlayer.Engine;

namespace NCSFPlayer;

// Spec 7.2-7.4: commit, push (lane -> voices) and the per-tick voice update.
public sealed partial class Player
{
    /// <summary>Spec 7.2.</summary>
    static void Commit(Voice v)
    {
        if (!v.AnyPending)
            return;

        if (v.PendingStop)
            v.Enabled = false;

        if (v.PendingStart)
        {
            v.OutLevel = 0;
            v.OutDivider = 0;
            v.OutPan = 0;
            v.OutDuty = 0;
            v.OutLoopMode = 0;
            v.OutFormat = 0;
            v.Enabled = false;

            v.OutPan = v.LastPan;
            v.OutLevel = (byte)(v.PackedVolume & 255);
            v.OutDivider = (byte)(v.PackedVolume >> 8);
            switch (v.Kind)
            {
            case VoiceKind.Sample:
            {
                var sample = v.Sample!;
                v.OutFormat = (byte)(sample.WaveType & 3);
                v.OutLoopMode = (byte)(sample.Loop != 0 ? 1 : 2);
                v.OutLoopStart = sample.LoopOffset;
                v.OutLoopLength = sample.LoopLength;
                v.OutEnd = v.OutLoopStart + v.OutLoopLength;
                v.OutSample = sample;
                break;
            }
            case VoiceKind.Square:
                v.OutFormat = 3;
                v.OutDuty = (byte)v.Duty;
                break;
            case VoiceKind.Noise:
                v.OutFormat = 3;
                break;
            }
            v.Enabled = true;
            v.ClearPending();
        }
        else
        {
            if (v.PendingVolume)
            {
                v.OutLevel = (byte)(v.PackedVolume & 255);
                v.OutDivider = (byte)(v.PackedVolume >> 8);
            }
            if (v.PendingPan)
                v.OutPan = v.LastPan;
        }
    }

    /// <summary>Spec 7.3.</summary>
    void Push(Track lane, bool release)
    {
        int vol = lane.Mute ? -32768
            : Tables.DecibelSquare(lane.Volume) + Tables.DecibelSquare(lane.Expression) +
              Tables.DecibelSquare(this.masterVolume) + this.SequenceVolume;
        if (vol < -32768)
            vol = -32768;
        int pit = (lane.Bend * (lane.BendRange << 6)) >> 7;
        int pn = Math.Clamp((int)lane.Pan, -128, 127);

        foreach (int n in lane.Owned)
        {
            var v = this.voices[n];
            if (v.Phase != EnvelopePhase.Release)
            {
                v.LaneVolume = (short)vol;
                v.LanePitch = (short)pit;
                v.LanePan = (sbyte)pn;
                v.ModTarget = lane.ModTarget;
                v.ModSpeed = lane.ModSpeed;
                v.ModDepth = lane.ModDepth;
                v.ModRange = lane.ModRange;
                v.ModDelay = lane.ModDelay;
                if (v.Length == 0 && release)
                {
                    v.Priority = 1;
                    VoiceRelease(v);
                }
            }
        }
    }

    /// <summary>Spec 7.4.</summary>
    void Update(Voice v)
    {
        if (!v.Active)
            return;

        if (v.StartPending)
        {
            v.PendingStart = true;
            v.StartPending = false;
            v.Enabled = false;
        }
        else if (!v.Enabled)
        {
            VoiceEnd(v);
            return;
        }

        int vol = Tables.DecibelSquare(v.Velocity);
        int pit = (v.Key - v.RootKey) * 64;

        // envelope
        switch (v.Phase)
        {
        case EnvelopePhase.Attack:
            v.EnvLevel = -(((-v.EnvLevel) * v.AttackCoef) >> 8);
            if (v.EnvLevel == 0)
                v.Phase = EnvelopePhase.Decay;
            break;
        case EnvelopePhase.Decay:
        {
            int sus = Tables.DecibelSquare(v.SustainLevel) << 7;
            v.EnvLevel -= v.DecayCoef;
            if (v.EnvLevel <= sus)
            {
                v.EnvLevel = sus;
                v.Phase = EnvelopePhase.Sustain;
            }
            break;
        }
        case EnvelopePhase.Sustain:
            break;
        case EnvelopePhase.Release:
            v.EnvLevel -= v.ReleaseCoef;
            break;
        }
        vol += v.EnvLevel >> 7;

        // sweep
        int w = 0;
        if (v.SweepPitch != 0 && v.SweepCount < v.SweepLength)
        {
            w = (int)((long)v.SweepPitch * (v.SweepLength - v.SweepCount) / v.SweepLength);
            if (v.AutoSweep)
                v.SweepCount += 1;
        }
        pit += w;

        vol += v.LaneVolume;
        pit += v.LanePitch;

        // modulation
        int value = v.ModDepth == 0 || v.ModDelayCount < v.ModDelay ? 0 : Tables.Sine(v.ModPhase >> 8) * v.ModDepth * v.ModRange;
        int m = value;
        if (value != 0)
        {
            long x = value;
            switch (v.ModTarget)
            {
            case 1:
                x *= 60;
                break;
            case 0:
            case 2:
                x <<= 6;
                break;
            }
            m = (int)(x >> 14);
        }
        if (v.ModDelayCount < v.ModDelay)
            v.ModDelayCount += 1;
        else
        {
            uint t = (uint)((v.ModPhase + (v.ModSpeed << 6)) >> 8);
            while (t >= 128)
                t -= 128;
            v.ModPhase = (ushort)(v.ModPhase + (v.ModSpeed << 6));
            v.ModPhase &= 0xFF;
            v.ModPhase |= (ushort)(t << 8);
        }

        int pn = 0;
        switch (v.ModTarget)
        {
        case 1:
            if (vol > -32768)
                vol += m;
            break;
        case 2:
            pn += m;
            break;
        case 0:
            pit += m;
            break;
        }
        pn += v.BasePan;
        pn += v.LanePan;

        if (v.Phase == EnvelopePhase.Release && vol <= -723)
        {
            v.SetPendingStopOnly();
            VoiceEnd(v);
            return;
        }

        ushort pv = Tables.Pack(vol);
        ushort timer = Tables.Timer(v.BaseTimer, pit);
        if (v.Kind == VoiceKind.Square)
            timer &= 0xFFFC;
        pn = Math.Clamp(pn + 64, 0, 127);

        if (pv != v.PackedVolume)
        {
            v.PackedVolume = pv;
            v.PendingVolume = true;
        }
        if (timer != v.LastTimer)
        {
            v.LastTimer = timer;
            v.Increment = (33514000.0 / (this.SampleRate * 2.0)) / timer;
        }
        if (this.ExactPitch && v.Kind == VoiceKind.Sample)
        {
            double x = Math.Clamp(v.BaseTimer * Math.Pow(2.0, (double)(-pit) / 768.0), 16.0, 65535.0);
            v.Increment = (33514000.0 / (this.SampleRate * 2.0)) / x;
        }
        if (pn != v.LastPan)
        {
            v.LastPan = (byte)pn;
            v.PendingPan = true;
        }
    }
}
