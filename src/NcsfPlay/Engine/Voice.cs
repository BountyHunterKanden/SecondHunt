using NCSFCommon;
using NCSFCommon.NC;

namespace NCSFPlayer.Engine;

/// <summary>Spec 4.3: what a voice plays.</summary>
internal enum VoiceKind : byte
{
    Sample,
    Square,
    Noise,
}

/// <summary>Spec 4.3: envelope phase.</summary>
internal enum EnvelopePhase : byte
{
    Attack,
    Decay,
    Sustain,
    Release,
}

/// <summary>Spec 4.3: one of the 16 voices (control state + the output state the mixer reads).</summary>
internal sealed class Voice(int number)
{
    internal readonly int Number = number;

    // control and parameters
    internal bool Active;
    internal bool StartPending;
    internal bool AutoSweep;
    internal bool PendingStop, PendingStart, PendingVolume, PendingPan;
    internal VoiceKind Kind;
    internal EnvelopePhase Phase;
    internal int EnvLevel;
    internal byte AttackCoef;
    internal ushort DecayCoef, ReleaseCoef;
    internal byte SustainLevel;
    internal byte Priority;
    internal byte Key, RootKey, Velocity;
    internal sbyte BasePan;
    internal sbyte LanePan;
    internal short LaneVolume, LanePitch;
    internal int Length;
    internal short SweepPitch;
    internal int SweepCount, SweepLength;
    internal byte ModTarget, ModSpeed, ModDepth, ModRange;
    internal ushort ModDelay;
    internal ushort ModDelayCount, ModPhase;
    internal ushort PackedVolume;
    internal ushort LastTimer;
    internal byte LastPan;
    internal SWAV? Sample;
    internal ushort BaseTimer;
    internal int Duty;
    internal Track? Owner;

    // output state
    internal bool Enabled;
    internal byte OutLevel, OutDivider, OutPan, OutDuty, OutFormat, OutLoopMode;
    internal uint OutLoopStart, OutLoopLength, OutEnd;
    internal SWAV? OutSample;
    internal double Position;
    internal double Increment;
    internal ushort NoiseShift;
    internal float NoiseOut;
    internal uint NoiseSteps;
    internal float[]? Window;

    // smoothing ramps (8.5)
    internal float GainL, GainR, StepL, StepR, TargetL, TargetR;
    internal double RampInc, RampIncStep, TargetInc;
    internal int RampLeft;
    internal bool WasOn;

    internal bool AnyPending => this.PendingStop || this.PendingStart || this.PendingVolume || this.PendingPan;

    internal void SetPendingStopOnly()
    {
        this.PendingStop = true;
        this.PendingStart = false;
        this.PendingVolume = false;
        this.PendingPan = false;
    }

    internal void ClearPending()
    {
        this.PendingStop = false;
        this.PendingStart = false;
        this.PendingVolume = false;
        this.PendingPan = false;
    }
}
