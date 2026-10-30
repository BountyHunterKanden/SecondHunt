using NCSFCommon;
using NCSFCommon.NC;
using NCSFPlayer.Engine;

namespace NCSFPlayer;

/// <summary>Sample interpolation used by the mixer (spec 8.2).</summary>
public enum Interpolation
{
    None,
    Sinc,
}

/// <summary>
/// One sequence player (spec Part 2): tempo, master volume, priority, 32 variables, the bank, four wave archive slots,
/// 16 lane slots and 16 voices. Single-threaded: callers never call one instance from two threads at once.
/// </summary>
public sealed partial class Player
{
    /// <summary>One engine tick: 64 x 2728 cycles at 33,514,000 Hz, in float.</summary>
    public const float TickSeconds = 174592f / 33514000f;

    /// <summary>dB(level), spec 3.3.</summary>
    public static short DecibelSquare(int level) => Tables.DecibelSquare(level & 0xFF);

    public uint SampleRate { get; set; }
    public Interpolation Interpolation { get; set; } = Interpolation.None;
    public ushort ChannelMask { get; set; }
    public ushort TrackMutes { get; set; }
    public ushort TempoRatio { get; set; } = 256;
    public bool Smoothing { get; set; }
    public bool ExactPitch { get; set; }
    public short SequenceVolume { get; set; }
    public SBNK Bank { get; set; } = null!;

    ushort tempo = 120;
    ushort tempoAcc = 240;
    byte masterVolume = 127;
    byte enginePriority = 64;
    readonly short[] vars = new short[32];
    readonly Track?[] slots = new Track?[16];
    readonly Track[] lanes = new Track[16];
    readonly Voice[] voices = new Voice[16];
    readonly SWAR?[] archives = new SWAR?[4];

    static Player() => Tables.EnsureBuilt();

    public Player()
    {
        Array.Fill(this.vars, (short)-1);
        for (int i = 0; i < 16; i++)
        {
            this.lanes[i] = new Track();
            this.voices[i] = new Voice(i);
        }
    }

    public void SetWaveArchive(int slot, SWAR archive) => this.archives[slot] = archive;

    /// <summary>The lane in a slot (0..15), else null.</summary>
    public Track? GetTrack(int slot) => slot is >= 0 and < 16 ? this.slots[slot] : null;

    /// <summary>engineStop (spec 4.4).</summary>
    public void Stop()
    {
        for (int i = 0; i < 16; i++)
            this.SlotStop(i);
    }

    /// <summary>Spec 5.2. Does not run a tick.</summary>
    public void Begin(SSEQ sseq, short sequenceVolume)
    {
        this.Stop();

        this.tempo = 120;
        this.TempoRatio = 256;
        this.tempoAcc = 240;
        this.masterVolume = 127;
        this.enginePriority = 64;
        Array.Clear(this.slots);
        Array.Fill(this.vars, (short)-1);
        foreach (var lane in this.lanes)
            lane.InUse = false;
        this.SequenceVolume = sequenceVolume;

        foreach (var v in this.voices)
        {
            v.ClearPending();
            v.Enabled = false;
            v.OutLevel = 0;
            v.OutDivider = 0;
            v.OutPan = 0;
            v.OutDuty = 0;
            v.OutLoopMode = 0;
            v.OutFormat = 0;
            v.Active = true;
        }

        var first = this.TakeLane();
        first.Data = sseq.Data;
        first.Pos = 0;
        this.slots[0] = first;

        if (ReadU8(first) == 0xFE)
        {
            int mask = ReadU16(first) >> 1;
            for (int j = 1; mask != 0; j++)
            {
                if ((mask & 1) != 0)
                    this.slots[j] = this.TakeLane();
                mask >>= 1;
            }
        }
        else
            first.Pos -= 1;
    }

    /// <summary>The lowest-numbered lane not in use, marked in use and initialised.</summary>
    Track TakeLane()
    {
        foreach (var lane in this.lanes)
            if (!lane.InUse)
            {
                lane.InUse = true;
                lane.Init();
                return lane;
            }
        throw new InvalidOperationException("No free sequence lane.");
    }

    /// <summary>Spec 7.1.</summary>
    public void RunTick()
    {
        for (int i = 0; i < 16; i++)
            Commit(this.voices[i]);

        int n = 0;
        while (this.tempoAcc >= 240)
        {
            this.tempoAcc -= 240;
            n += 1;
        }
        for (int pass = 0; pass < n; pass++)
            for (int i = 0; i < 16; i++)
            {
                var lane = this.slots[i];
                if (lane is not null && lane.Pos != -1)
                {
                    lane.Mute = ((this.TrackMutes >> i) & 1) != 0;
                    if (!this.LaneStep(lane))
                        this.SlotStop(i);
                }
            }
        this.tempoAcc = (ushort)(this.tempoAcc + ((this.tempo * this.TempoRatio) >> 8));

        for (int i = 0; i < 16; i++)
            if (this.slots[i] is Track lane)
                this.Push(lane, true);

        for (int i = 0; i < 16; i++)
            this.Update(this.voices[i]);
    }

    // ---- common operations (spec 4.4) ----

    static void VoiceRelease(Voice v) => v.Phase = EnvelopePhase.Release;

    static void VoiceFree(Voice v) => v.Owner = null;

    static void VoiceEnd(Voice v)
    {
        var owner = v.Owner;
        v.Priority = 0;
        if (owner is not null)
        {
            v.Owner = null;
            owner.Owned.Remove(v.Number);
        }
        v.PackedVolume = 0;
        v.Active = false;
    }

    static void Detach(Voice v) => v.Owner?.Owned.Remove(v.Number);

    void LaneReleaseVoices(Track lane, int r)
    {
        this.Push(lane, false);
        foreach (int n in lane.Owned)
        {
            var v = this.voices[n];
            if (v.Active)
            {
                if (r >= 0)
                    v.ReleaseCoef = Tables.Rate(r & 255);
                v.Priority = 1;
                VoiceRelease(v);
            }
        }
    }

    void LaneFreeVoices(Track lane)
    {
        foreach (int n in lane.Owned)
            VoiceFree(this.voices[n]);
        lane.Owned.Clear();
    }

    void LaneStop(Track lane)
    {
        lane.Pos = -1;
        this.LaneReleaseVoices(lane, -1);
        this.LaneFreeVoices(lane);
    }

    void SlotStop(int i)
    {
        if (this.slots[i] is Track lane)
        {
            this.LaneStop(lane);
            lane.InUse = false;
            this.slots[i] = null;
        }
    }
}
