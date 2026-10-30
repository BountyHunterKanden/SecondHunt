using System.Buffers.Binary;
using System.Runtime.InteropServices;
using CommunityToolkit.HighPerformance;
using NCSFCommon.NC;
using NCSFPlayer;

namespace NCSF123;

// Partially based on my XSFPlayer and XSFPlayer_NCSF classes from my in_xsf C++ project.
public class NCSFPlayerStream : Stream
{
    const float CheckSilenceBias = 4096; // Equivalent of 0x8000000 for a 16-bit integer sample
    const float CheckSilenceLevel = 0.000213623046875f; // Equivalent of 7 for a 16-bit integer sample

    readonly NCSFFile ncsf;
    uint sampleRate; // ncsf-change: both can change mid-song (ChangeQuality)
    Interpolation interpolation;
    uint detectedSilenceSample = 0;
    uint detectedSilenceSec = 0;
    uint skipSilenceOnStartSec;
    readonly uint initialSkipSilenceOnStartSec;
    int lengthSample;
    int fadeSample;
    float prevSampleL = NCSFPlayerStream.CheckSilenceBias;
    float prevSampleR = NCSFPlayerStream.CheckSilenceBias;
    readonly int defaultLengthInMS;
    readonly int defaultFadeInMS;
    int lengthInMS;
    int fadeInMS;
    readonly VolumeType volumeType;
    readonly PeakType peakType;
    readonly bool playForever;
    readonly float volumeMultiplier;
    readonly ushort channelMutes;
    readonly ushort trackMutes;
    readonly bool ignoreVolume;
    SDAT sdat = null!;
    readonly List<byte> sdatData = [];
    uint sseq;
    readonly Player player = new();
    public Player Player => player;
    float secondsPerSample;
    int samplesIntoPlayback;

    public NCSFCommon.TagList Tags => this.ncsf.Tags;
    public float VolumeModification { get; set; }

    // ncsf-change: optional output stage, all off by default (= hardware behaviour).
    /// <summary>
    /// Cutoff of a one-pole high-pass on the output, 0 = off. Some samples carry a DC offset, which the DS's analog
    /// output (AC coupled) never passed on; this removes it the same way.
    /// </summary>
    public float DcBlockHz { get; set; }
    /// <summary>
    /// Gain applied to the mix before the output clamp, to give loud passages headroom.
    /// </summary>
    public float OutputGain { get; set; } = 1;
    /// <summary>
    /// Round peaks above <see cref="SoftLimitKnee" /> off smoothly instead of hard clipping them at full scale.
    /// </summary>
    public bool SoftLimit { get; set; }
    public const float SoftLimitKnee = 0.9f;
    /// <summary>
    /// A look-ahead peak limiter instead of the soft limit: it sees each peak <see cref="LimiterLookAheadSeconds" />
    /// early and turns the whole (stereo-linked) mix down just enough, then lets it recover, so loud passages keep their
    /// level without waveform distortion. Adds the look-ahead as latency.
    /// </summary>
    public bool Limiter { get; set; }
    public const float LimiterCeiling = 0.98f;
    public const float LimiterLookAheadSeconds = 0.002f;
    public const float LimiterReleaseSeconds = 0.08f;
    /// <summary>
    /// Corner frequency of a high-shelf EQ on the output, 0 = off. With interpolation the samples play back band-limited,
    /// without the extra brightness the DS's sample-and-hold playback adds; the shelf can restore that tonal balance.
    /// </summary>
    public float HighShelfHz { get; set; }
    /// <summary>
    /// Gain of the high shelf in dB.
    /// </summary>
    public float HighShelfDb { get; set; }
    /// <summary>
    /// Apply the SDAT's per-sequence volume (SSEQ INFO "volume") the way the game's NITRO sound-archive player does: as
    /// the player's initial volume, through the squared decibel curve (<see cref="Player.DecibelSquare" />), instead of
    /// FSS's linear Cnv_Scale. Off = the old behaviour.
    /// Measured against vanilla (BizHawk/melonDS SOUNDxCNT, 2026-10-01): with the linear curve every channel of CHUTNEY
    /// (vol 106) and GREY (106) played 1.6-1.7 dB, and SHIP (86) 3.6 dB, louder than on the DS; DRONE (127) matched.
    /// </summary>
    public bool SquaredSeqVolume
    {
        get => this.squaredSeqVolume;
        set
        {
            this.squaredSeqVolume = value;
            this.player.SequenceVolume = this.SeqVolumeDb(); // voices pick it up on the next tick
        }
    }
    bool squaredSeqVolume;
    int sseqInfoVolume = 0x7F;
    /// <summary>
    /// Run the sequencer at the DS's exact tick rate (one tick per 64 x 2728 ARM7 cycles = 5.2095 ms): the sample count
    /// carries its fraction from tick to tick. Off = the old behaviour, which restarted the count at 0 on each tick, so
    /// every tick lasted a whole number of samples rounded UP (32728 Hz: 171 instead of 170.50, 0.29% slow; 48 kHz: 251
    /// instead of 250.06, 0.38% slow) -- DRONE, the boot logos' cue, ended ~0.1 s late against the logos (2026-10-01).
    /// </summary>
    public bool ExactTempo { get; set; }
    double tickPhase;
    short SeqVolumeDb() => this.squaredSeqVolume ? Player.DecibelSquare(this.sseqInfoVolume)
        : NCSFCommon.NCSF.ConvertScale(this.sseqInfoVolume);

    readonly double[] dcIn = new double[2];
    readonly double[] dcOut = new double[2];
    float dcCoefficientHz;
    double dcCoefficient;
    float shelfHz, shelfDb;
    double shelfB0, shelfB1, shelfB2, shelfA1, shelfA2;
    readonly double[] shelfState = new double[8]; // x1, x2, y1, y2 per channel
    // limiter state: per-sample required gains, their windowed minimum (box-filtered), and the delayed audio
    int limitLength;
    float[] limitTargets = [], limitMins = [], limitDelayL = [], limitDelayR = [];
    int limitPos;
    double limitMinSum;
    float limitGain = 1;
    float limitRelease;
    int samplesPerTick;
    bool smoothingPrimed;

    public NCSFPlayerStream(string path, uint sampleRate, Interpolation interpolation, uint skipSilenceOnStartSec, int defaultLengthInMS,
        int defaultFadeInMS, VolumeType volumeType, PeakType peakType, bool playForever, float volumeMultiplier, ushort channelMutes,
        ushort trackMutes, bool ignoreVolume)
    {
        this.ncsf = new(path);
        this.sampleRate = sampleRate;
        this.interpolation = interpolation;
        this.initialSkipSilenceOnStartSec = skipSilenceOnStartSec;
        this.defaultLengthInMS = defaultLengthInMS;
        this.defaultFadeInMS = defaultFadeInMS;
        this.volumeType = volumeType;
        this.peakType = peakType;
        this.playForever = playForever;
        this.volumeMultiplier = volumeMultiplier;
        this.channelMutes = channelMutes;
        this.trackMutes = trackMutes;
        this.ignoreVolume = ignoreVolume;
        this.Load();
    }

    public override bool CanRead => true;

    public override bool CanSeek => !this.playForever;

    public override bool CanWrite => false;

    public override long Length => this.playForever ? throw new NotSupportedException() : (this.lengthSample + this.fadeSample) << 3;

    public override long Position { get; set; }

    public override void Flush() => throw new NotImplementedException();

    void GenerateSamples(Span<float> buf)
    {
        int offset = 0;
        int samples = buf.Length >> 1;
        bool smoothing = this.player.Smoothing;
        if (smoothing && !this.smoothingPrimed)
        {
            // voices started by the load-time tick need their ramp state before the first sample
            this.player.BeginRamps(this.samplesPerTick);
            this.smoothingPrimed = true;
        }
        for (int smpl = 0; smpl < samples; ++smpl)
        {
            ++this.samplesIntoPlayback;

            // one output sample from every sounding voice (muted voices still advance)
            (float leftChannel, float rightChannel) = this.player.MixFrame(this.channelMutes);

            buf[offset] = leftChannel;
            buf[offset + 1] = rightChannel;
            offset += 2;

            bool tick;
            if (this.ExactTempo)
            {
                // samplesIntoPlayback counted this sample above; the phase keeps the remainder of the tick length
                this.tickPhase += 1;
                double tickSamples = (double)Player.TickSeconds * this.sampleRate;
                tick = this.tickPhase >= tickSamples;
                if (tick) this.tickPhase -= tickSamples;
            }
            else
            {
                tick = this.samplesIntoPlayback * this.secondsPerSample >= Player.TickSeconds;
            }
            if (tick)
            {
                this.player.RunTick();
                this.samplesIntoPlayback = 0;
                if (smoothing)
                    this.player.BeginRamps(this.samplesPerTick);
            }
        }
    }

    void Load()
    {
        this.LoadNCSF();

        this.sdat = new();
        this.sdat.Read(this.ncsf.FilePath, this.sdatData.AsSpan(), this.sseq);
        this.player.ChannelMask = this.sdat.Player?.ChannelMask ?? 0xFFFF;
        this.player.SampleRate = this.sampleRate;
        this.player.Interpolation = this.interpolation;
        this.player.TrackMutes = this.trackMutes;
        var sseqToPlay = this.sdat.SSEQs[0];
        this.sseqInfoVolume = sseqToPlay.Info!.Volume == 0 ? 0x7F : sseqToPlay.Info!.Volume;
        this.player.Begin(sseqToPlay, this.SeqVolumeDb());
        this.player.Bank = this.sdat.SBNKs[0];
        for (int i = 0, j = 0; i < 4; ++i)
            if (this.player.Bank.Info!.WaveArchives[i] != 0xFFFF)
                this.player.SetWaveArchive(i, this.sdat.SWARs[j++]);
        // One tick at the start just to skip having a bunch of silent samples before the first player tick.
        this.player.RunTick();
        this.secondsPerSample = 1.0f / this.sampleRate;
        this.samplesPerTick = (int)float.Ceiling(Player.TickSeconds / this.secondsPerSample);

        this.lengthInMS = this.ncsf.GetLengthMS(this.defaultLengthInMS);
        this.fadeInMS = this.ncsf.GetFadeMS(this.defaultFadeInMS);
        this.lengthSample = (int)(this.lengthInMS * this.sampleRate / 1000);
        this.fadeSample = (int)(this.fadeInMS * this.sampleRate / 1000);

        this.VolumeModification = this.ignoreVolume ? 1 : this.ncsf.GetVolume(this.volumeType, this.peakType) * this.volumeMultiplier;

        this.skipSilenceOnStartSec = this.initialSkipSilenceOnStartSec;
        this.detectedSilenceSample = this.detectedSilenceSec = 0;
        this.Position = 0;
        this.prevSampleL = this.prevSampleR = NCSFPlayerStream.CheckSilenceBias;
    }

    /// <summary>
    /// ncsf-change: switch the output sample rate and interpolation without restarting the song. Call it between Reads
    /// (from the thread that reads): the song carries on from the same point, with every rate-based count (tick phase,
    /// song length, filters, limiter recovery) rescaled to the new rate.
    /// </summary>
    public void ChangeQuality(uint sampleRate, Interpolation interpolation)
    {
        if (sampleRate == this.sampleRate && interpolation == this.interpolation)
            return;
        double s = (double)sampleRate / this.sampleRate;
        this.player.ChangeOutput(sampleRate, interpolation);
        this.tickPhase *= s;
        this.samplesIntoPlayback = (int)Math.Round(this.samplesIntoPlayback * s);
        this.Position = (long)Math.Round((this.Position >> 3) * s) << 3;
        this.detectedSilenceSample = (uint)(this.detectedSilenceSample * s);
        this.sampleRate = sampleRate;
        this.interpolation = interpolation;
        this.secondsPerSample = 1.0f / this.sampleRate;
        this.samplesPerTick = (int)float.Ceiling(Player.TickSeconds / this.secondsPerSample);
        this.lengthSample = (int)(this.lengthInMS * this.sampleRate / 1000);
        this.fadeSample = (int)(this.fadeInMS * this.sampleRate / 1000);
        // the DC blocker and shelf recompute their coefficients for the new rate on the next sample; the limiter keeps
        // its look-ahead line (resizing it mid-song would drop or repeat audio), only its recovery speed follows the rate
        this.dcCoefficientHz = 0;
        this.shelfHz = 0;
        if (this.limitLength != 0)
            this.limitRelease = 1 - float.Exp(-1 / (NCSFPlayerStream.LimiterReleaseSeconds * this.sampleRate));
    }

    /// <summary>
    /// ncsf-change: change the output stage and the engine's smoothing/exact pitch mid-song (between Reads, from the
    /// thread that reads). A filter or limiter that comes back on starts from fresh state rather than from whatever it
    /// held when it was last used; the caller masks the switch itself with a short fade.
    /// </summary>
    public void ChangeFixes(float dcBlockHz, bool limiter, bool smoothing, bool exactPitch, float highShelfHz, float highShelfDb)
    {
        if (dcBlockHz > 0 && this.DcBlockHz <= 0)
        {
            Array.Clear(this.dcIn);
            Array.Clear(this.dcOut);
            this.dcCoefficientHz = 0;
        }
        if (highShelfHz > 0 && this.HighShelfHz <= 0)
        {
            Array.Clear(this.shelfState);
            this.shelfHz = 0;
        }
        if (limiter != this.Limiter)
        {
            this.limitLength = 0; // Limit() builds a new look-ahead line at the current rate
            this.limitPos = 0;
            this.limitGain = 1;
        }
        this.DcBlockHz = dcBlockHz;
        this.Limiter = limiter;
        this.HighShelfHz = highShelfHz;
        this.HighShelfDb = highShelfDb;
        this.player.ChangeFixes(smoothing, exactPitch);
    }

    void LoadNCSF() => this.RecursiveLoadNCSF(this.ncsf, 1);

    void MapNCSF(NCSFFile ncsfToLoad)
    {
        var reservedSection = ncsfToLoad.ReservedSection;
        var programSection = ncsfToLoad.ProgramSection;

        if (reservedSection.Length != 0)
            this.sseq = BinaryPrimitives.ReadUInt32LittleEndian(reservedSection);

        if (programSection.Length != 0)
            this.MapNCSFSection(programSection);
    }

    void MapNCSFSection(ReadOnlySpan<byte> section)
    {
        int size = BinaryPrimitives.ReadInt32LittleEndian(section[0x08..]);
        if (this.sdatData.Count < size)
        {
            bool empty = this.sdatData.Count == 0;
            CollectionsMarshal.SetCount(this.sdatData, size);
            if (empty)
                this.sdatData.AsSpan().Clear();
        }
        section.CopyTo(this.sdatData.AsSpan());
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int pos = offset;
        var bufFloat = buffer.AsSpan(offset, count).Cast<byte, float>();
        int bufSize = bufFloat.Length >> 1;
        while (pos < bufSize)
        {
            int remain = bufSize - pos;
            this.GenerateSamples(bufFloat[(pos << 1)..]);
            if (this.skipSilenceOnStartSec != 0)
            {
                int skipOffset = 0;
                for (int ofs = 0; ofs < remain; ++ofs)
                {
                    float sampleL = bufFloat[2 * (pos + ofs)];
                    float sampleR = bufFloat[2 * (pos + ofs) + 1];
                    bool silence = sampleL + NCSFPlayerStream.CheckSilenceBias + NCSFPlayerStream.CheckSilenceLevel - this.prevSampleL <=
                            NCSFPlayerStream.CheckSilenceLevel * 2 &&
                        sampleR + NCSFPlayerStream.CheckSilenceBias + NCSFPlayerStream.CheckSilenceLevel - this.prevSampleR <=
                            NCSFPlayerStream.CheckSilenceLevel * 2;

                    if (silence)
                    {
                        if (++this.detectedSilenceSample >= this.sampleRate)
                        {
                            this.detectedSilenceSample -= this.sampleRate;
                            ++this.detectedSilenceSec;
                            if (this.skipSilenceOnStartSec != 0 && this.detectedSilenceSec >= this.skipSilenceOnStartSec)
                            {
                                this.skipSilenceOnStartSec = this.detectedSilenceSec = 0;
                                skipOffset = ofs;
                            }
                        }
                    }
                    else
                    {
                        this.detectedSilenceSample = this.detectedSilenceSec = 0;
                        if (this.skipSilenceOnStartSec != 0)
                        {
                            this.skipSilenceOnStartSec = 0;
                            skipOffset = ofs;
                        }
                    }

                    this.prevSampleL = sampleL + NCSFPlayerStream.CheckSilenceBias;
                    this.prevSampleR = sampleR + NCSFPlayerStream.CheckSilenceBias;
                }

                if (this.skipSilenceOnStartSec == 0)
                {
                    if (skipOffset != 0)
                    {
                        bufFloat[((pos + skipOffset) << 1)..].CopyTo(bufFloat[pos..]);
                        pos += remain - skipOffset;
                    }
                    else
                        pos += remain;
                }
            }
            else
                pos += remain;
        }

        long currentSample = this.Position >> 3;
        // Detect end of song
        if (!this.playForever)
        {
            if (currentSample >= this.lengthSample + this.fadeSample)
                return 0;
            if (currentSample + bufSize >= this.lengthSample + this.fadeSample)
                bufSize = (int)(this.lengthSample + this.fadeSample - currentSample);
        }

        if (this.Limiter)
            for (int ofs = 0; ofs < bufSize; ++ofs)
            {
                (float left, float right) = this.Limit(this.PreLimit(bufFloat[2 * ofs], 0), this.PreLimit(bufFloat[2 * ofs + 1], 1));
                bufFloat[2 * ofs] = float.Clamp(left, -1, 1);
                bufFloat[2 * ofs + 1] = float.Clamp(right, -1, 1);
            }
        else if (this.DcBlockHz > 0 || this.OutputGain != 1 || this.SoftLimit || this.HighShelfHz > 0)
            for (int ofs = 0; ofs < bufSize; ++ofs)
            {
                bufFloat[2 * ofs] = this.OutputStage(bufFloat[2 * ofs], 0);
                bufFloat[2 * ofs + 1] = this.OutputStage(bufFloat[2 * ofs + 1], 1);
            }
        else
            for (int ofs = 0; ofs < bufSize; ++ofs)
            {
                bufFloat[2 * ofs] = float.Clamp(bufFloat[2 * ofs] * this.VolumeModification, -1, 1);
                bufFloat[2 * ofs + 1] = float.Clamp(bufFloat[2 * ofs + 1] * this.VolumeModification, -1, 1);
            }

        // Fading
        if (!this.playForever && this.fadeSample != 0 && currentSample + bufSize >= this.lengthSample)
            for (int ofs = 0; ofs < bufSize; ++ofs)
                if (currentSample + ofs >= this.lengthSample && currentSample + ofs < this.lengthSample + this.fadeSample)
                {
                    int scale = (int)((this.lengthSample + this.fadeSample - (currentSample + ofs)) * 0x10000 / this.fadeSample);
                    bufFloat[2 * ofs] = float.ScaleB(bufFloat[2 * ofs] * scale, -16);
                    bufFloat[2 * ofs + 1] = float.ScaleB(bufFloat[2 * ofs + 1] * scale, -16);
                }
                else if (currentSample + ofs >= this.lengthSample + this.fadeSample)
                    bufFloat.Slice(2 * ofs, 2).Clear();

        this.Position += bufSize << 3;
        return bufSize << 3;
    }

    // ncsf-change: DC blocker -> high shelf -> gain -> soft limit (or the usual clamp)
    float OutputStage(float sample, int channel)
    {
        float result = this.PreLimit(sample, channel);
        if (!this.SoftLimit)
            return float.Clamp(result, -1, 1);
        float magnitude = float.Abs(result);
        if (magnitude <= NCSFPlayerStream.SoftLimitKnee)
            return result;
        float over = (magnitude - NCSFPlayerStream.SoftLimitKnee) / (1 - NCSFPlayerStream.SoftLimitKnee);
        return float.CopySign(NCSFPlayerStream.SoftLimitKnee + (1 - NCSFPlayerStream.SoftLimitKnee) * float.Tanh(over), result);
    }

    // ncsf-change: look-ahead limiter on a stereo frame. The required gain for each incoming frame (ceiling / peak) goes
    // through a minimum over the look-ahead window, then a box filter of the same length: when a peak reaches the end
    // of the delay line, every value averaged into its gain is at or below what it needs, so it never overshoots, and
    // the gain ramps down smoothly over the look-ahead. Recovery afterwards is exponential (LimiterReleaseSeconds).
    (float Left, float Right) Limit(float left, float right)
    {
        if (this.limitLength == 0)
        {
            this.limitLength = int.Max(1, (int)(NCSFPlayerStream.LimiterLookAheadSeconds * this.sampleRate));
            this.limitTargets = new float[this.limitLength];
            this.limitMins = new float[this.limitLength];
            this.limitDelayL = new float[this.limitLength];
            this.limitDelayR = new float[this.limitLength];
            this.limitTargets.AsSpan().Fill(1);
            this.limitMins.AsSpan().Fill(1);
            this.limitMinSum = this.limitLength;
            this.limitRelease = 1 - float.Exp(-1 / (NCSFPlayerStream.LimiterReleaseSeconds * this.sampleRate));
        }
        float peak = float.Max(float.Abs(left), float.Abs(right));
        float target = peak > NCSFPlayerStream.LimiterCeiling ? NCSFPlayerStream.LimiterCeiling / peak : 1;
        int pos = this.limitPos;
        this.limitTargets[pos] = target;
        float windowMin = 1;
        foreach (float t in this.limitTargets)
            windowMin = float.Min(windowMin, t);
        this.limitMinSum += windowMin - this.limitMins[pos];
        this.limitMins[pos] = windowMin;
        float boxed = (float)(this.limitMinSum / this.limitLength);
        this.limitGain = boxed < this.limitGain ? boxed : this.limitGain + (boxed - this.limitGain) * this.limitRelease;
        // the delay line holds the last limitLength frames; the oldest one is the frame this gain was built for
        int oldest = (pos + 1) % this.limitLength;
        float outL = this.limitDelayL[oldest], outR = this.limitDelayR[oldest];
        this.limitDelayL[pos] = left;
        this.limitDelayR[pos] = right;
        this.limitPos = oldest;
        return (outL * this.limitGain, outR * this.limitGain);
    }

    float PreLimit(float sample, int channel)
    {
        double x = sample;
        if (this.DcBlockHz > 0)
        {
            if (this.dcCoefficientHz != this.DcBlockHz)
            {
                this.dcCoefficient = double.Exp(-2 * double.Pi * this.DcBlockHz / this.sampleRate);
                this.dcCoefficientHz = this.DcBlockHz;
            }
            double y = x - this.dcIn[channel] + this.dcCoefficient * this.dcOut[channel];
            this.dcIn[channel] = x;
            this.dcOut[channel] = y;
            x = y;
        }
        if (this.HighShelfHz > 0)
        {
            if (this.shelfHz != this.HighShelfHz || this.shelfDb != this.HighShelfDb)
            {
                // RBJ cookbook high shelf, shelf slope 1
                double a = double.Pow(10, this.HighShelfDb / 40);
                double w = 2 * double.Pi * this.HighShelfHz / this.sampleRate;
                double cos = double.Cos(w), alpha = double.Sin(w) / 2 * double.Sqrt(2);
                double sqrtA2Alpha = 2 * double.Sqrt(a) * alpha;
                double a0 = (a + 1) - (a - 1) * cos + sqrtA2Alpha;
                this.shelfB0 = a * ((a + 1) + (a - 1) * cos + sqrtA2Alpha) / a0;
                this.shelfB1 = -2 * a * ((a - 1) + (a + 1) * cos) / a0;
                this.shelfB2 = a * ((a + 1) + (a - 1) * cos - sqrtA2Alpha) / a0;
                this.shelfA1 = 2 * ((a - 1) - (a + 1) * cos) / a0;
                this.shelfA2 = ((a + 1) - (a - 1) * cos - sqrtA2Alpha) / a0;
                this.shelfHz = this.HighShelfHz;
                this.shelfDb = this.HighShelfDb;
            }
            Span<double> s = this.shelfState.AsSpan(channel * 4, 4);
            double y = this.shelfB0 * x + this.shelfB1 * s[0] + this.shelfB2 * s[1] - this.shelfA1 * s[2] - this.shelfA2 * s[3];
            s[1] = s[0];
            s[0] = x;
            s[3] = s[2];
            s[2] = y;
            x = y;
        }
        return (float)x * this.VolumeModification * this.OutputGain;
    }

    void RecursiveLoadNCSF(NCSFFile ncsfToLoad, int level)
    {
        if (level <= 10 && ncsfToLoad.Tags.Contains("_lib"))
            this.RecursiveLoadNCSF(new(Path.Combine(Path.GetDirectoryName(ncsfToLoad.FilePath)!, ncsfToLoad.Tags["_lib"].Value)),
                level + 1);
        this.MapNCSF(ncsfToLoad);

        int n = 2;
        bool found;
        do
        {
            found = false;
            string libTag = $"_lib{n++}";
            if (ncsfToLoad.Tags.Contains(libTag))
            {
                found = true;
                this.RecursiveLoadNCSF(new(Path.Combine(Path.GetDirectoryName(ncsfToLoad.FilePath)!, ncsfToLoad.Tags[libTag].Value)),
                    level + 1);
            }
        } while (found);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (this.playForever)
            throw new NotImplementedException();
        else
        {
            // Align offset so it is always at a proper byte value for the 32-bit stereo floating-point samples.
            offset >>= 3;
            offset <<= 3;
            if (origin == SeekOrigin.Current)
                offset += this.Position;
            else if (origin == SeekOrigin.End)
                offset += this.Length;
            if (offset < this.Position)
            {
                this.Terminate();
                this.Load();
            }
            Span<byte> dummyBuffer = stackalloc byte[0x1000];
            while (offset - this.Position > 0x1000)
                _ = this.Read(dummyBuffer);
            if (offset - this.Position > 0)
                _ = this.Read(dummyBuffer[..(int)(offset - this.Position)]);
            return offset;
        }
    }

    public override void SetLength(long value) => throw new NotImplementedException();

    void Terminate() => this.player.Stop();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
}
