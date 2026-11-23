using MphRecomp.Holiday;
using NCSF123;

namespace MphRecomp.App;

// HOLIDAY MODE's sleigh bells over an MPH song (Core Music/HolidayBells.cs): the NCSF stream hands over its sequence
// ticks and the frame each took effect from, and the bells go on top of its finished output. Runs on the music pump.
internal sealed class HolidayBellsOverlay : IStreamOverlay
{
    readonly HolidayBells.Voices _bells;
    int _rate;

    HolidayBellsOverlay(HolidayBells.Song song, int rate)
    {
        _bells = new HolidayBells.Voices(song);
        _rate = rate;
    }

    public static HolidayBellsOverlay? For(int seqId, int rate) =>
        HolidayBells.ForSeq(seqId) is HolidayBells.Song song ? new HolidayBellsOverlay(song, rate) : null;

    public void Restart(long sequenceTicks, long frame)
    {
        _bells.RestartTicks(sequenceTicks);
        _bells.Ticks(sequenceTicks, frame, _rate);
    }

    public void Tick(long sequenceTicks, long frame) => _bells.Ticks(sequenceTicks, frame, _rate);

    public void Mix(System.Span<float> stereo, long firstFrame, uint sampleRate)
    {
        _rate = (int)sampleRate;
        _bells.Mix(stereo, firstFrame, _rate, 1, clamp: true);
    }

    public void Rescale(double scale)
    {
        _rate = (int)System.Math.Round(_rate * scale);
        _bells.Rescale(scale);
    }
}
