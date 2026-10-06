using System;
using System.Buffers.Binary;
using System.IO;

namespace MphRecomp.Import.Retro
{
    /// <summary>One parsed Echoes music stream ("RS\0\x03", big-endian, block-interleaved DSP-ADPCM).</summary>
    public sealed class RetroStream
    {
        private const int HeaderSize = 0x60;
        private const int BlockSize = 0x8F00;
        private const int SamplesPerUnit = 56; // one 32-byte unit = 4 frames

        private readonly byte[] _file;
        private readonly short[] _coefficients = new short[32];
        private readonly long _fullGroupBytes; // F x K, per channel
        private readonly long _tailShare;      // T, per channel

        public RetroStream(byte[] file)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (file.Length < HeaderSize)
            {
                throw new InvalidDataException("File is shorter than the stream header.");
            }
            if (!HasSignature(file))
            {
                throw new InvalidDataException("Not an RS stream (bad signature).");
            }
            uint channels = ReadU32(file, 0x04);
            if (channels != 1 && channels != 2)
            {
                throw new InvalidDataException("Channel count must be 1 or 2.");
            }
            uint rate = ReadU32(file, 0x0C);
            if (rate == 0)
            {
                throw new InvalidDataException("Sample rate is 0.");
            }
            if (rate > int.MaxValue)
            {
                throw new InvalidDataException("Sample rate is out of range.");
            }

            _file = file;
            Channels = (int)channels;
            SampleRate = (int)rate;

            long dataLength = file.Length - HeaderSize;
            long groupBytes = (long)BlockSize * Channels;
            long fullGroups = dataLength / groupBytes;
            _fullGroupBytes = fullGroups * BlockSize;
            _tailShare = (dataLength - fullGroups * groupBytes) / Channels;

            long frameCount = (_fullGroupBytes + _tailShare) / DspAdpcm.FrameBytes;
            if (frameCount < 1)
            {
                throw new InvalidDataException("Sound data is too short to hold a frame per channel.");
            }
            FrameCount = frameCount;

            // Damaged headers could claim more samples than the data holds.
            long capacity = frameCount * DspAdpcm.SamplesPerFrame;
            long headerCount = ReadU32(file, 0x08);
            SampleCount = (int)Math.Min(Math.Min(headerCount, capacity), int.MaxValue);

            for (int i = 0; i < 32; i++)
            {
                _coefficients[i] = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan(0x20 + 2 * i, 2));
            }

            LoopStart = 0;
            LoopEnd = SampleCount;
            if (ReadU32(file, 0x14) != 0)
            {
                long start = ReadU32(file, 0x18) / DspAdpcm.FrameBytes * (long)DspAdpcm.SamplesPerFrame;
                long endUnit = ReadU32(file, 0x1C) / DspAdpcm.FrameBytes * (long)DspAdpcm.SamplesPerFrame;
                long end = Math.Min(SampleCount, endUnit + SamplesPerUnit);
                if (start < end)
                {
                    Loops = true;
                    LoopStart = (int)start;
                    LoopEnd = (int)end;
                }
            }
        }

        public int Channels { get; }

        public int SampleRate { get; }

        /// <summary>Samples per channel, limited to what the data can actually hold.</summary>
        public int SampleCount { get; }

        public bool Loops { get; }

        /// <summary>First looped sample (0 when not looping).</summary>
        public int LoopStart { get; }

        /// <summary>Exclusive end of the looped range (<see cref="SampleCount"/> when not looping).</summary>
        public int LoopEnd { get; }

        internal long FrameCount { get; }

        public static bool IsRetroStream(ReadOnlySpan<byte> start)
        {
            if (start.Length < 8 || !HasSignature(start))
            {
                return false;
            }
            uint channels = BinaryPrimitives.ReadUInt32BigEndian(start.Slice(4, 4));
            return channels == 1 || channels == 2;
        }

        public ReadOnlySpan<short> Coefficients(int channel)
        {
            if ((uint)channel >= (uint)Channels)
            {
                throw new ArgumentOutOfRangeException(nameof(channel));
            }
            return _coefficients.AsSpan(16 * channel, 16);
        }

        public RetroStreamReader OpenReader() => new RetroStreamReader(this);

        /// <summary>The 8 bytes of one channel's frame, resolved through the block interleave.</summary>
        internal ReadOnlySpan<byte> Frame(int channel, long frame)
        {
            long q = frame * DspAdpcm.FrameBytes;
            long offset;
            if (q < _fullGroupBytes)
            {
                long group = q / BlockSize;
                offset = HeaderSize + group * BlockSize * Channels + (long)channel * BlockSize + (q - group * BlockSize);
            }
            else
            {
                offset = HeaderSize + _fullGroupBytes * Channels + channel * _tailShare + (q - _fullGroupBytes);
            }
            return _file.AsSpan((int)offset, DspAdpcm.FrameBytes);
        }

        private static bool HasSignature(ReadOnlySpan<byte> data) =>
            data[0] == 0x52 && data[1] == 0x53 && data[2] == 0x00 && data[3] == 0x03;

        private static uint ReadU32(byte[] file, int offset) =>
            BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset, 4));
    }

    /// <summary>Plays a <see cref="RetroStream"/> into interleaved PCM16, decoding frame by frame.</summary>
    public sealed class RetroStreamReader
    {
        private readonly RetroStream _stream;
        private readonly int _channels;
        private readonly DspAdpcm.ChannelState[] _states;
        private readonly short[] _decoded; // one frame per channel, channel c at [14c, 14c+14)
        private long _position;
        private long _decodedFrame = -1; // -1: nothing valid in _decoded
        private bool _finished;

        internal RetroStreamReader(RetroStream stream)
        {
            _stream = stream;
            _channels = stream.Channels;
            _states = new DspAdpcm.ChannelState[_channels];
            _decoded = new short[DspAdpcm.SamplesPerFrame * _channels];
        }

        /// <summary>Index of the next sample to be played.</summary>
        public long Position => _position;

        public bool Finished => _finished;

        public void Restart()
        {
            _position = 0;
            _decodedFrame = -1;
            _finished = false;
            Array.Clear(_states);
        }

        /// <summary>Writes up to <paramref name="frames"/> interleaved sample frames; returns how many.</summary>
        public int Read(Span<short> destination, int frames)
        {
            if (frames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frames));
            }
            if (destination.Length < (long)frames * _channels)
            {
                throw new ArgumentException("Destination is too small for the requested frames.", nameof(destination));
            }

            bool loops = _stream.Loops;
            long end = loops ? _stream.LoopEnd : _stream.SampleCount;
            int written = 0;

            while (written < frames)
            {
                if (_position >= end)
                {
                    if (!loops)
                    {
                        _finished = true;
                        break;
                    }
                    Jump();
                }

                long frame = _position / DspAdpcm.SamplesPerFrame;
                int within = (int)(_position - frame * DspAdpcm.SamplesPerFrame);
                if (frame != _decodedFrame)
                {
                    DecodeAt(frame, end);
                }

                long frameEnd = Math.Min(frame * DspAdpcm.SamplesPerFrame + DspAdpcm.SamplesPerFrame, end);
                int run = (int)Math.Min(frameEnd - _position, frames - written);

                if (_channels == 1)
                {
                    _decoded.AsSpan(within, run).CopyTo(destination.Slice(written, run));
                }
                else
                {
                    int dst = written * 2;
                    for (int i = 0; i < run; i++)
                    {
                        destination[dst++] = _decoded[within + i];
                        destination[dst++] = _decoded[DspAdpcm.SamplesPerFrame + within + i];
                    }
                }

                written += run;
                _position += run;
            }

            // Report the end as soon as the last sample has gone out, not only on the next call.
            if (!loops && _position >= end)
            {
                _finished = true;
            }
            else if (loops && _position >= end)
            {
                Jump();
            }
            return written;
        }

        // Back to the loop start with the decoder state carried over unchanged.
        private void Jump()
        {
            _position = _stream.LoopStart;
            _decodedFrame = -1;
        }

        private void DecodeAt(long frame, long end)
        {
            // Stop the history on the last played sample when the play range ends inside this frame.
            long first = frame * DspAdpcm.SamplesPerFrame;
            int count = (int)Math.Min(DspAdpcm.SamplesPerFrame, end - first);
            for (int c = 0; c < _channels; c++)
            {
                DspAdpcm.DecodePartial(_stream.Frame(c, frame), _stream.Coefficients(c), ref _states[c],
                    _decoded.AsSpan(c * DspAdpcm.SamplesPerFrame, DspAdpcm.SamplesPerFrame), count);
            }
            _decodedFrame = frame;
        }
    }
}
