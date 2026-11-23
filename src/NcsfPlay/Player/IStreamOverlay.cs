namespace NCSF123;

/// <summary>
/// ncsf-change: a sound mixed over an <see cref="NCSFPlayerStream"/>'s finished output and clocked by its sequence
/// ticks. Frame numbers are generated frames since the song loaded (0 = the first frame, when tick 0 has run). All
/// calls come from the thread that reads the stream.
/// </summary>
public interface IStreamOverlay
{
    /// <summary>The song (re)started, or the overlay was attached: <paramref name="sequenceTicks"/> have run by <paramref name="frame"/>.</summary>
    void Restart(long sequenceTicks, long frame);

    /// <summary>A player tick ran: <paramref name="sequenceTicks"/> sequence ticks in all, taking effect from <paramref name="frame"/>.</summary>
    void Tick(long sequenceTicks, long frame);

    /// <summary>Add to the finished stereo output; <paramref name="firstFrame"/> is the generated frame its first frame holds.</summary>
    void Mix(System.Span<float> stereo, long firstFrame, uint sampleRate);

    /// <summary>The output rate changed by <paramref name="scale"/>: frame numbers already handed over scale with it.</summary>
    void Rescale(double scale);
}
