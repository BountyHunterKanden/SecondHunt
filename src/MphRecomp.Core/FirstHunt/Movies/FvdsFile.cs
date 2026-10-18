using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace MphRecomp.FirstHunt.Movies
{
    // A First Hunt movie file (data/movies/*.fv, docs/FIRST_HUNT_PORT.md P6). Header 0x24 bytes: "FVDS", u32 frame count,
    // u32 width 256, u32 height 192, u32 fps 16.16 (0xEFFFB, ~15), u32 audio rate 32768, u32 data size, u32 largest
    // chunk, u32 0x1200. Then one chunk per frame: u32 size, i32 previous (relative), u32 next (relative), u32 frame
    // index, then size bytes = u32 video size, video, u32 audio size, audio (8 or 9 40-byte records). Size 0 ends it.
    // Each movie is a pair: "<name>-15fps-up-left.avi.fv" plays on the top screen and the left speaker,
    // "-down-right" on the bottom screen and the right speaker (the cart's player, 0x2036c4c / 0x20370f0).
    public sealed class FvdsFile
    {
        public readonly struct Frame
        {
            public readonly int VideoOffset, VideoLength, AudioOffset, AudioLength;

            public Frame(int videoOffset, int videoLength, int audioOffset, int audioLength)
            {
                VideoOffset = videoOffset;
                VideoLength = videoLength;
                AudioOffset = audioOffset;
                AudioLength = audioLength;
            }
        }

        public byte[] Data { get; }
        public int Width { get; }
        public int Height { get; }
        public int Fps16 { get; } // 16.16
        public int AudioRate { get; }
        public IReadOnlyList<Frame> Frames { get; }

        public double FramesPerSecond => Fps16 / 65536.0;

        private FvdsFile(byte[] data, int width, int height, int fps16, int audioRate, List<Frame> frames)
        {
            Data = data;
            Width = width;
            Height = height;
            Fps16 = fps16;
            AudioRate = audioRate;
            Frames = frames;
        }

        // null if the file isn't a well-formed FVDS movie
        public static FvdsFile? Read(byte[] data)
        {
            if (data.Length < 0x24 || data[0] != 'F' || data[1] != 'V' || data[2] != 'D' || data[3] != 'S') return null;
            int U(int at) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
            int width = U(0x08), height = U(0x0c);
            if (width != FvdsVideo.Width || height != FvdsVideo.Height) return null;
            var frames = new List<Frame>();
            int p = 0x24;
            while (p + 24 <= data.Length)
            {
                int size = U(p);
                if (size == 0) break;
                int end = p + 16 + size;
                int vsize = U(p + 16);
                int v = p + 20;
                if (size < 8 || end > data.Length || vsize < 8 || v + vsize + 4 > end) return null;
                int asize = U(v + vsize);
                int a = v + vsize + 4;
                if (asize < 0 || a + asize != end) return null;
                frames.Add(new Frame(v, vsize, a, asize));
                p = end;
            }
            return new FvdsFile(data, width, height, U(0x10), U(0x14), frames);
        }
    }
}
