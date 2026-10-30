using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MphRead.Formats.Sound;
using MphRead.Formats.Vx;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead.Formats
{
    // Decoder for the game's VX movies (video and audio). Decode runs on a background task and stays at most four
    // frames ahead of the consumer, which takes frames in order with GetImage (from another thread) and reads audio
    // blocks from a 60-block ring with GetAudioBuffer.
    public class VxDecoder
    {
        public static VxDecoder Instance1 { get; } = new VxDecoder();
        public static VxDecoder Instance2 { get; } = new VxDecoder();

        // on (the default): each instance recycles four picture sets; off: every frame gets new planes (export tools)
        public static bool UseStaticBuffers { get; set; } = true;

        private const int RingSlots = 60;
        public static int SampleBufferCount => RingSlots;

        private const int HeaderSize = 48;
        private const int PictureSets = 4;
        private const int MaxQueued = 4;
        private const int TextureStride = 256;
        private const int SetWidth = 256;
        private const int SetHeight = 192;

        private readonly VxPicture[] _sets = new VxPicture[PictureSets];
        private readonly short[] _ring = new short[RingSlots * VxAudio.BlockSize];
        private readonly VxBitReader _bits = new VxBitReader();
        private readonly VxVideo _video;
        private readonly VxAudio _audio = new VxAudio();
        private byte[] _payload = new byte[8192];

        private FrameList _frames = new FrameList(0, 0, 0);
        private int _framesQueued;
        private int _audioFrameTotal;
        // number of the next audio block (counts every block of the movie)
        private int _audioBlock;

        public int FramesQueued => Volatile.Read(ref _framesQueued);
        public int FrameCount { get; private set; }
        public decimal FrameRate { get; private set; }
        public int AudioSampleRate { get; private set; }
        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }
        public int AudioFrameTotal => Volatile.Read(ref _audioFrameTotal);

        // The frames decoded in the current decode, in order. The decoder adds to it while GetImage reads it from
        // another thread: an entry (and a grown array) is published before the count that makes it visible.
        private sealed class FrameList
        {
            public VxPicture[] Items;
            public int Count;
            public readonly int Width;
            public readonly int Height;

            public FrameList(int capacity, int width, int height)
            {
                Items = new VxPicture[capacity];
                Width = width;
                Height = height;
            }

            public void Add(VxPicture picture)
            {
                int count = Count;
                VxPicture[] items = Items;
                if (count == items.Length)
                {
                    var bigger = new VxPicture[Math.Max(16, items.Length * 2)];
                    Array.Copy(items, bigger, count);
                    bigger[count] = picture;
                    Volatile.Write(ref Items, bigger);
                }
                else
                {
                    items[count] = picture;
                }
                Volatile.Write(ref Count, count + 1);
            }
        }

        public VxDecoder()
        {
            for (int i = 0; i < PictureSets; i++)
            {
                _sets[i] = new VxPicture(SetWidth, SetHeight);
            }
            _video = new VxVideo(_bits);
        }

        // Forgets the decoded frames and the queue, switches back to recycled buffers and silences the sample ring.
        public void Reset()
        {
            Volatile.Write(ref _frames, new FrameList(0, 0, 0));
            Interlocked.Exchange(ref _framesQueued, 0);
            UseStaticBuffers = true;
            Array.Clear(_ring);
        }

        // Converts a decoded frame to RGB24 (row stride 256 pixels) and releases one queue slot.
        public bool GetImage(int frameIndex, byte[] texture)
        {
            FrameList frames = Volatile.Read(ref _frames);
            int count = Volatile.Read(ref frames.Count);
            if (count == 0 || frameIndex < 0 || frameIndex >= count)
            {
                return false;
            }
            VxPicture picture = Volatile.Read(ref frames.Items)[frameIndex];
            picture.ToRgb(texture, frames.Width, frames.Height, TextureStride);
            Interlocked.Decrement(ref _framesQueued);
            return true;
        }

        // the 128 samples of ring slot index mod 60
        public ReadOnlySpan<short> GetAudioBuffer(int index)
        {
            int slot = index % RingSlots;
            if (slot < 0)
            {
                slot += RingSlots;
            }
            return new ReadOnlySpan<short>(_ring, slot * VxAudio.BlockSize, VxAudio.BlockSize);
        }

        public async Task Decode(string filePath, bool writeFiles = false, CancellationToken token = default)
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await Decode(stream, Path.GetFileName(filePath), writeFiles, token);
        }

        public async Task Decode(byte[] data, string filename, bool writeFiles = false, CancellationToken token = default)
        {
            await Decode(new MemoryStream(data, writable: false), filename, writeFiles, token);
        }

        public async Task Decode(Stream stream, string filename, bool writeFiles = false, CancellationToken token = default)
        {
            using (stream)
            {
                string folder = "";
                if (writeFiles)
                {
                    folder = Paths.Combine(Paths.Export, Path.GetFileNameWithoutExtension(filename));
                    Directory.CreateDirectory(folder);
                }
                bool recycled = UseStaticBuffers;
                Interlocked.Exchange(ref _framesQueued, 0);
                int nextSet = 0;
                _audioBlock = 0;

                // header
                var header = new byte[HeaderSize];
                stream.ReadExactly(header);
                int frameCount = BitConverter.ToInt32(header, 4);
                int width = BitConverter.ToInt32(header, 8);
                int height = BitConverter.ToInt32(header, 12);
                int frameRate = BitConverter.ToInt32(header, 16);
                int quantiser = BitConverter.ToInt32(header, 20);
                int sampleRate = BitConverter.ToInt32(header, 24);
                int streamCount = BitConverter.ToInt32(header, 28);
                int audioTableOffset = BitConverter.ToInt32(header, 36);
                FrameCount = frameCount;
                FrameRate = frameRate / 65536m;
                AudioSampleRate = sampleRate;
                FrameWidth = width;
                FrameHeight = height;
                // audio tables (read even without an audio stream); the seek table isn't needed
                var tables = new byte[VxAudio.TableBytes];
                stream.Position = audioTableOffset;
                stream.ReadExactly(tables);
                _audio.ReadTables(tables);
                if (streamCount > 1)
                {
                    throw new ProgramException($"VX: {streamCount} audio streams are not supported.");
                }
                if (width % 16 != 0 || height % 16 != 0)
                {
                    throw new ProgramException($"VX: frame size {width} x {height} is not a multiple of 16.");
                }
                if (quantiser < 12 || quantiser > 161)
                {
                    throw new ProgramException($"VX: quantiser {quantiser} is out of range.");
                }
                _video.Start(width, height, quantiser);
                _audio.ResetState();
                Volatile.Write(ref _audioFrameTotal, 0);
                var frames = new FrameList(Math.Clamp(frameCount, 0, 4096), width, height);
                Volatile.Write(ref _frames, frames);
                stream.Position = HeaderSize;

                BinaryWriter? wav = null;
                try
                {
                    if (writeFiles && streamCount >= 1)
                    {
                        wav = new BinaryWriter(File.Create(Paths.Combine(folder, "audio.wav")));
                        wav.Write(new byte[44]);
                    }
                    var record = new byte[4];
                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        if (!writeFiles)
                        {
                            while (Volatile.Read(ref _framesQueued) >= MaxQueued && !token.IsCancellationRequested)
                            {
                                await Task.Delay(1);
                            }
                        }
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }
                        // frame record
                        stream.ReadExactly(record);
                        int size = record[0] | (record[1] << 8);
                        int blocks = record[2] | (record[3] << 8);
                        int payloadBytes = size - 2;
                        if (payloadBytes < 0)
                        {
                            throw new ProgramException($"VX: frame {frame} record is too short.");
                        }
                        if (_payload.Length < payloadBytes)
                        {
                            _payload = new byte[payloadBytes * 2];
                        }
                        stream.ReadExactly(_payload, 0, payloadBytes);
                        VxPicture picture;
                        if (recycled)
                        {
                            // the set keeps the luma of the frame that used it last
                            picture = _sets[nextSet];
                            nextSet = (nextSet + 1) % PictureSets;
                            Array.Clear(picture.U);
                            Array.Clear(picture.V);
                        }
                        else
                        {
                            picture = new VxPicture(width, height);
                        }
                        _bits.Start(_payload, payloadBytes);
                        // video (shifts the references when done), then the audio blocks
                        _video.DecodeFrame(picture);
                        for (int b = 0; b < blocks; b++)
                        {
                            int offset = (_audioBlock % RingSlots) * VxAudio.BlockSize;
                            _audio.DecodeBlock(_bits, _ring, offset);
                            _audioBlock++;
                            if (wav != null)
                            {
                                for (int i = 0; i < VxAudio.BlockSize; i++)
                                {
                                    wav.Write(_ring[offset + i]);
                                }
                            }
                        }
                        // publish: audio total, then the frame, then the queue count
                        Volatile.Write(ref _audioFrameTotal, _audioFrameTotal + blocks);
                        frames.Add(picture);
                        Interlocked.Increment(ref _framesQueued);
                        if (writeFiles && recycled)
                        {
                            SavePng(picture, width, height, folder, frame);
                        }
                    }
                    if (wav != null && _audioFrameTotal > 0)
                    {
                        wav.Flush();
                        wav.Seek(0, SeekOrigin.Begin);
                        SoundRead.WriteWavHeader(wav, (uint)(_audioFrameTotal * 128), (ushort)AudioSampleRate, WaveFormat.PCM16);
                    }
                    if (writeFiles && !recycled)
                    {
                        for (int i = 0; i < frames.Count; i++)
                        {
                            SavePng(frames.Items[i], width, height, folder, i);
                        }
                    }
                }
                finally
                {
                    wav?.Dispose();
                }
            }
        }

        private static void SavePng(VxPicture picture, int width, int height, string folder, int index)
        {
            var rgb = new byte[width * height * 3];
            picture.ToRgb(rgb, width, height, width);
            using Image<Rgb24> image = Image.LoadPixelData<Rgb24>(rgb, width, height);
            image.SaveAsPng(Paths.Combine(folder, $"{index:D4}.png"));
        }

        public async Task Export(string filePath)
        {
            Reset();
            string path = Paths.Combine(Paths.FileSystem, "movies", filePath);
            if (!File.Exists(path))
            {
                path = Paths.Combine(Paths.FileSystem, filePath);
                if (!File.Exists(path))
                {
                    path = filePath;
                }
            }
            Console.WriteLine("Exporting...");
            UseStaticBuffers = false;
            await Decode(path, writeFiles: true);
            Console.WriteLine("Done.");
        }

        public async Task ExportAll()
        {
            Reset();
            UseStaticBuffers = false;
            string[] files = Directory.GetFiles(Paths.Combine(Paths.FileSystem, "movies"));
            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                if (Path.GetExtension(file) == ".vx")
                {
                    Console.WriteLine($"Exporting {i + 1} of {files.Length}: {Path.GetFileName(file)}");
                    await Decode(file, writeFiles: true);
                }
            }
            Console.WriteLine("Done.");
        }
    }
}
