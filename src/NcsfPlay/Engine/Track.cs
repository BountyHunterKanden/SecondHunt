namespace NCSFCommon;

/// <summary>
/// One lane of the sequence player (an SSEQ "track"), spec 4.2. Owned by the engine; callers may read and set
/// <see cref="Volume" /> and <see cref="Mute" /> between engine calls.
/// </summary>
public sealed class Track
{
    internal Track()
    {
    }

    /// <summary>The lane's volume (command 0xC1, used by the push).</summary>
    public byte Volume { get; set; }

    /// <summary>Reassigned from the engine's TrackMutes whenever the lane is stepped.</summary>
    public bool Mute { get; set; }

    internal ReadOnlyMemory<byte> Data;
    internal int Pos = -1;
    internal bool NoteWait, Tie, FinishWait, Portamento, Cond;
    internal bool InUse;
    internal ushort Program;
    internal byte LanePriority;
    internal byte Expression;
    internal sbyte Pan;
    internal sbyte Bend;
    internal byte BendRange;
    internal sbyte Transpose;
    internal byte AttackOverride, DecayOverride, SustainOverride, ReleaseOverride;
    internal byte PortaKey;
    internal byte PortaTime;
    internal short Sweep;
    internal byte ModTarget, ModSpeed, ModDepth, ModRange;
    internal ushort ModDelay;
    internal int Wait;
    internal readonly int[] StackPos = new int[3];
    internal readonly byte[] StackCount = new byte[3];
    internal int Depth;

    /// <summary>Owned voice numbers, in order (front = index 0).</summary>
    internal readonly List<int> Owned = new(16);

    /// <summary>Spec 4.4 laneInit.</summary>
    internal void Init()
    {
        this.Data = default;
        this.Pos = -1;
        this.NoteWait = true;
        this.Cond = true;
        this.Tie = false;
        this.FinishWait = false;
        this.Portamento = false;
        this.Program = 0;
        this.LanePriority = 64;
        this.Volume = 127;
        this.Expression = 127;
        this.Pan = 0;
        this.Bend = 0;
        this.BendRange = 2;
        this.Transpose = 0;
        this.AttackOverride = 255;
        this.DecayOverride = 255;
        this.SustainOverride = 255;
        this.ReleaseOverride = 255;
        this.PortaKey = 60;
        this.PortaTime = 0;
        this.Sweep = 0;
        this.ModTarget = 0;
        this.ModSpeed = 16;
        this.ModDepth = 0;
        this.ModRange = 1;
        this.ModDelay = 0;
        this.Wait = 0;
        this.Depth = 0;
        this.Owned.Clear();
    }
}
