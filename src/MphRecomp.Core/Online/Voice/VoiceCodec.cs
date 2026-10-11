using System;
using Concentus;
using Concentus.Enums;

namespace MphRecomp.Online.Voice
{
    // Opus through Concentus (a pure C# port of libopus, BSD-3-Clause; the same code on Android and Windows). VOIP mode,
    // 16 kbps VBR, in-band FEC for 10% loss (a lost frame comes back at lower quality from the next one). No DTX: silence
    // isn't sent at all (VoiceChat's gate), and Concentus can't decode DTX's 1-byte frames.
    public sealed class VoiceEncoder
    {
        private readonly IOpusEncoder _enc;

        public VoiceEncoder()
        {
            OpusCodecFactory.AttemptToUseNativeLibrary = false; // managed only: no native probing on Android
            _enc = OpusCodecFactory.CreateEncoder(VoiceFormat.Rate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _enc.Bitrate = VoiceFormat.Bitrate;
            _enc.Complexity = 5;
            _enc.UseVBR = true;
            _enc.UseInbandFEC = true;
            _enc.PacketLossPercent = 10;
            _enc.UseDTX = false;
            _enc.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        }

        // one FrameSamples frame -> its Opus bytes (the count written)
        public int Encode(short[] pcm, Span<byte> output) => _enc.Encode(pcm, VoiceFormat.FrameSamples, output, output.Length);

        // a new talk spurt: no prediction from the last one
        public void Reset() => _enc.ResetState();
    }

    public sealed class VoiceDecoder
    {
        private readonly IOpusDecoder _dec;

        public VoiceDecoder()
        {
            OpusCodecFactory.AttemptToUseNativeLibrary = false;
            _dec = OpusCodecFactory.CreateDecoder(VoiceFormat.Rate, 1);
        }

        // a received frame; fec: data is the NEXT frame and this rebuilds the lost one before it from its FEC part
        public int Decode(ReadOnlySpan<byte> data, short[] pcm, bool fec = false)
        {
            if (data.Length < 2)
            {
                return Conceal(pcm);
            }
            try
            {
                return _dec.Decode(data, pcm, VoiceFormat.FrameSamples, fec);
            }
            catch (OpusException)
            {
                return Conceal(pcm);
            }
        }

        // a lost frame with nothing to rebuild it from: the decoder's own concealment (fades out over a few frames)
        public int Conceal(short[] pcm)
        {
            try
            {
                return _dec.Decode(ReadOnlySpan<byte>.Empty, pcm, VoiceFormat.FrameSamples, false);
            }
            catch (OpusException)
            {
                Array.Clear(pcm);
                return VoiceFormat.FrameSamples;
            }
        }

        public void Reset() => _dec.ResetState();
    }
}
