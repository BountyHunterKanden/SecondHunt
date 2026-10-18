using System;

namespace MphRecomp.Media
{
    // What a movie shows and plays, as the presenters (MphRecomp.App MoviePresenter / MovieAudio) use it: MPH's VX
    // movies (MoviePlayer) and First Hunt's FVDS movies (FirstHunt/Movies/FhMoviePlayer).
    public interface IMoviePlayback
    {
        string Name { get; }
        bool DualScreen { get; }
        // RGB24, 256 x 192, first row = top of the screen
        byte[] TopFrame { get; }
        byte[] BottomFrame { get; }
        // bumps whenever TopFrame/BottomFrame change
        int FrameVersion { get; }
        bool Started { get; }
        bool Finished { get; }
        int AudioSampleRate { get; }
        int AudioChannels { get; }

        // PCM16, interleaved when AudioChannels is 2. Safe to call from an audio thread. Returns samples written.
        int ReadAudio(Span<short> destination);

        // the DS channel volume (0..127) when the movie knows it; null: MPH's MOVIEVOLINFO.DAT by Name
        int? DsVolume => null;
    }
}
