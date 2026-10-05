using NCSFCommon;
using NCSFCommon.NC;
using NCSFPlayer.Engine;

namespace NCSFPlayer;

// Spec Part 6: instrument lookup, voice choice, voice start and note playing.
public sealed partial class Player
{
    /// <summary>Spec 6.1.</summary>
    SBNKInstrument? FindInstrument(int program, int key)
    {
        var entries = this.Bank.Entries;
        if (program >= entries.Length)
            return null;
        var entry = entries[program];
        var instruments = entry.Instruments;
        switch (entry.Record)
        {
        case 1:
        case 2:
        case 3:
        case 5:
            return instruments[0];
        case 16:
            if (key < instruments[0].LowNote || key > instruments[^1].HighNote)
                return null;
            return instruments[key - instruments[0].LowNote];
        case 17:
            foreach (var instrument in instruments)
                if (key <= instrument.HighNote)
                    return instrument;
            return null;
        default:
            return null;
        }
    }

    static int Loudness(Voice v) => ((v.PackedVolume & 255) << 4) >> Tables.DividerShift[v.PackedVolume >> 8];

    /// <summary>Spec 6.2.</summary>
    Voice? Claim(int mask, int prio, Track lane)
    {
        Voice? best = null;
        foreach (int n in Tables.SearchOrder)
        {
            if (((mask >> n) & 1) == 0)
                continue;
            var v = this.voices[n];
            if (best is null)
                best = v;
            else if (v.Priority <= best.Priority && (v.Priority != best.Priority || Loudness(best) > Loudness(v)))
                best = v;
        }
        if (best is null || prio < best.Priority)
            return null;

        Detach(best);
        best.SetPendingStopOnly();
        best.Active = false;

        best.Owner = lane;
        best.Length = 0;
        best.SweepLength = 0;
        best.SweepCount = 0;
        best.Priority = (byte)prio;
        best.PackedVolume = 127;
        best.StartPending = false;
        best.AutoSweep = true;
        best.Key = 60;
        best.RootKey = 60;
        best.Velocity = 127;
        best.BasePan = 0;
        best.LanePan = 0;
        best.LaneVolume = 0;
        best.LanePitch = 0;
        best.SweepPitch = 0;
        best.AttackCoef = Tables.Attack(127);
        best.SustainLevel = 127;
        best.DecayCoef = Tables.Rate(127);
        best.ReleaseCoef = Tables.Rate(127);
        best.ModTarget = 0;
        best.ModSpeed = 16;
        best.ModDepth = 0;
        best.ModRange = 1;
        best.ModDelay = 0;
        return best;
    }

    /// <summary>Spec 6.3.</summary>
    bool StartVoice(Voice v, SBNKInstrument instrument, byte key, byte vel, int len)
    {
        int rel = instrument.ReleaseRate;
        if (rel == 255)
        {
            len = -1;
            rel = 0;
        }

        switch (instrument.Record)
        {
        case 1:
        {
            var sample = this.archives[instrument.SWAR]!.SWAVs[instrument.SWAV];
            v.Kind = VoiceKind.Sample;
            v.Position = sample.WaveType == 2 ? -11 : -3;
            v.Sample = sample;
            v.BaseTimer = sample.Time;
            break;
        }
        case 2:
            if (v.Number < 8 || v.Number > 13)
                return false;
            v.Kind = VoiceKind.Square;
            v.Position = -1;
            v.Duty = instrument.SWAV;
            v.BaseTimer = 8006;
            break;
        case 3:
            if (v.Number != 14 && v.Number != 15)
                return false;
            v.Kind = VoiceKind.Noise;
            v.Position = -1;
            v.NoiseShift = 0x7FFF;
            v.BaseTimer = 8006;
            break;
        default:
            return false;
        }

        // arm
        v.EnvLevel = -92544;
        v.Phase = EnvelopePhase.Attack;
        v.Length = len;
        v.ModPhase = 0;
        v.ModDelayCount = 0;
        v.StartPending = true;
        v.Active = true;

        v.Key = key;
        v.RootKey = instrument.NoteNumber;
        v.Velocity = vel;
        v.AttackCoef = Tables.Attack(instrument.AttackRate);
        v.SustainLevel = instrument.SustainLevel;
        v.DecayCoef = Tables.Rate(instrument.DecayRate);
        v.ReleaseCoef = Tables.Rate(rel);
        v.BasePan = (sbyte)(instrument.Pan - 64);
        return true;
    }

    /// <summary>Spec 6.4.</summary>
    void PlayNote(Track lane, byte key, byte vel, int len)
    {
        Voice? v = null;
        if (lane.Tie && lane.Owned.Count != 0)
        {
            v = this.voices[lane.Owned[0]];
            v.Key = key;
            v.Velocity = vel;
        }

        if (v is null)
        {
            var instrument = this.FindInstrument(lane.Program, key);
            if (instrument is null)
                return;
            int mask;
            switch (instrument.Record)
            {
            case 1:
                mask = 0xFFFF;
                break;
            case 2:
                mask = 0x3F00;
                break;
            case 3:
                mask = 0xC000;
                break;
            default:
                return;
            }
            mask &= this.ChannelMask;
            v = this.Claim(mask, this.enginePriority + lane.LanePriority, lane);
            if (v is null)
                return;
            if (!this.StartVoice(v, instrument, key, vel, lane.Tie ? -1 : len))
            {
                v.Priority = 0;
                VoiceFree(v);
                return;
            }
            lane.Owned.Insert(0, v.Number);
        }

        if (lane.AttackOverride != 255)
            v.AttackCoef = Tables.Attack(lane.AttackOverride);
        if (lane.DecayOverride != 255)
            v.DecayCoef = Tables.Rate(lane.DecayOverride);
        if (lane.SustainOverride != 255)
            v.SustainLevel = lane.SustainOverride;
        if (lane.ReleaseOverride != 255)
            v.ReleaseCoef = Tables.Rate(lane.ReleaseOverride);

        v.SweepPitch = lane.Sweep;
        if (lane.Portamento)
            v.SweepPitch = (short)(v.SweepPitch + (short)((lane.PortaKey - key) << 6));

        if (lane.PortaTime != 0)
            v.SweepLength = (lane.PortaTime * lane.PortaTime * Math.Abs((int)v.SweepPitch)) >> 11;
        else
        {
            v.SweepLength = len;
            v.AutoSweep = false;
        }
        v.SweepCount = 0;
    }
}
