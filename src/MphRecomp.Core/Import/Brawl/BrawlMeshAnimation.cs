using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace MphRecomp.Import.Brawl
{
    // A bone's pose at one frame: rotation in degrees (applied x, then y, then z).
    public readonly record struct BrawlBonePose(Vector3 Scale, Vector3 Rotation, Vector3 Translation);

    // A mesh animation (CHR0, version 4): per animated bone, nine components, each a fixed value or a curve.
    // Evaluate clamps the frame to the curves' keys; looping (frame modulo FrameCount) is the caller's job.
    // Clean-room from docs/cleanroom/E_brawl_effects_spec.md (section 7).
    public sealed class BrawlMeshAnimation
    {
        public string Name { get; }
        public int FrameCount { get; }
        public bool Loops { get; }
        // the animated bones' names, in record order
        public IReadOnlyList<string> Bones { get; }

        readonly Dictionary<string, Track> _tracks;

        BrawlMeshAnimation(string name, int frameCount, bool loops, List<string> bones, Dictionary<string, Track> tracks)
        {
            Name = name;
            FrameCount = frameCount;
            Loops = loops;
            Bones = bones;
            _tracks = tracks;
        }

        // the pose of `boneName` at `frame` (may be fractional), or null when this animation doesn't animate that bone
        public BrawlBonePose? Evaluate(string boneName, float frame)
        {
            if (!_tracks.TryGetValue(boneName, out var t)) return null;
            var c = t.Components;
            return new BrawlBonePose(
                new Vector3(c[0].At(frame), c[1].At(frame), c[2].At(frame)),
                new Vector3(c[3].At(frame), c[4].At(frame), c[5].At(frame)),
                new Vector3(c[6].At(frame), c[7].At(frame), c[8].At(frame)));
        }

        // every mesh animation in the file's "AnmChr(NW4R)" folder, in record order
        public static List<BrawlMeshAnimation> ReadAll(BrawlResFile file)
        {
            var r = new List<BrawlMeshAnimation>();
            foreach (var item in file.Folder(BrawlResFile.MeshAnimations))
            {
                r.Add(Read(file, item.Position));
            }
            return r;
        }

        // the CHR0 resource starting at `position` in the resource file
        public static BrawlMeshAnimation Read(BrawlResFile file, int position)
        {
            var d = file.Data.Span;
            string part = $"mesh animation at 0x{position:X}";
            BrawlBytes.Magic(d, position, "CHR0", part);
            BrawlBytes.Need(d, position, 0x28, part + " header");
            uint size = BrawlBytes.U32(d, position + 4L, part);
            BrawlBytes.Need(d, position, size, part);
            uint version = BrawlBytes.U32(d, position + 8L, part);
            uint folderPos = BrawlBytes.U32(d, position + 0x10L, part);
            int namePos = BrawlBytes.I32(d, position + 0x14L, part);
            string name = BrawlResFile.Name(d, position + (long)namePos, part);
            part = $"mesh animation {name}";
            if (version != 4)
            {
                throw new InvalidDataException($"{part}: version {version}, only version 4 is read");
            }
            int frames = BrawlBytes.U16(d, position + 0x1CL, part);
            bool loops = BrawlBytes.U32(d, position + 0x20L, part) == 1;

            var bones = new List<string>();
            var tracks = new Dictionary<string, Track>();
            if (folderPos != 0)
            {
                foreach (var (bname, at) in BrawlMesh.Records(d, position + (long)folderPos, part + " bone folder"))
                {
                    if (tracks.ContainsKey(bname))
                    {
                        throw new InvalidDataException($"{part}: bone {bname} has two tracks");
                    }
                    tracks[bname] = ReadTrack(d, at, $"{part} track {bname}");
                    bones.Add(bname);
                }
            }
            return new BrawlMeshAnimation(name, frames, loops, bones, tracks);
        }

        // ---- bone track (7.2) ----

        sealed class Track
        {
            // scale x y z, rotation x y z, translation x y z
            public readonly Curve[] Components = new Curve[9];
        }

        static Track ReadTrack(ReadOnlySpan<byte> d, long at, string part)
        {
            uint w = BrawlBytes.U32(d, at + 4, part);
            var t = new Track();
            long e = at + 8;
            // part p: 0 scale, 1 rotation, 2 translation
            for (int p = 0; p < 3; p++)
            {
                float neutral = p == 0 ? 1f : 0f;
                bool follow = (w >> (22 + p) & 1) != 0;
                if (!follow)
                {
                    for (int k = 0; k < 3; k++) t.Components[3 * p + k] = Curve.Fixed(neutral);
                    continue;
                }
                bool uniform = (w >> (4 + p) & 1) != 0;
                int kind = p switch
                {
                    0 => (int)(w >> 25 & 3),
                    1 => (int)(w >> 27 & 7),
                    _ => (int)(w >> 30 & 3),
                };
                int entries = uniform ? 1 : 3;
                for (int k = 0; k < entries; k++)
                {
                    bool isFixed = (w >> (13 + 3 * p + k) & 1) != 0;
                    string cp = $"{part} {(p == 0 ? "scale" : p == 1 ? "rotation" : "translation")} {"xyz"[k]}";
                    Curve c = isFixed
                        ? Curve.Fixed(BrawlBytes.F32(d, e, cp))
                        : ReadCurve(d, at + BrawlBytes.I32(d, e, cp), kind, cp);
                    e += 4;
                    if (uniform)
                    {
                        for (int j = 0; j < 3; j++) t.Components[3 * p + j] = c;
                    }
                    else
                    {
                        t.Components[3 * p + k] = c;
                    }
                }
            }
            return t;
        }

        // ---- curves (7.3) ----

        sealed class Curve
        {
            readonly float[] _frames, _values, _slopes;

            Curve(float[] frames, float[] values, float[] slopes)
            {
                _frames = frames;
                _values = values;
                _slopes = slopes;
            }

            public static Curve Fixed(float v) => new Curve(new[] { 0f }, new[] { v }, new[] { 0f });

            public static Curve Keys(float[] frames, float[] values, float[] slopes) => new Curve(frames, values, slopes);

            public float At(float f)
            {
                int n = _frames.Length;
                if (n == 1) return _values[0];
                if (!(f >= _frames[0])) return _values[0]; // before the first key (or NaN)
                if (f >= _frames[n - 1]) return _values[n - 1];
                // the last key at or before f; with equal-frame neighbours this is the later one
                int i = 0;
                while (i + 1 < n && _frames[i + 1] <= f) i++;
                float f0 = _frames[i], f1 = _frames[i + 1];
                float dd = f1 - f0;
                float t = (f - f0) / dd;
                float t2 = t * t, t3 = t2 * t;
                return (2 * t3 - 3 * t2 + 1) * _values[i]
                    + (t3 - 2 * t2 + t) * dd * _slopes[i]
                    + (-2 * t3 + 3 * t2) * _values[i + 1]
                    + (t3 - t2) * dd * _slopes[i + 1];
            }
        }

        static Curve ReadCurve(ReadOnlySpan<byte> d, long at, int kind, string part)
        {
            part += $" curve at 0x{at:X}";
            int count = BrawlBytes.U16(d, at, part);
            if (count == 0)
            {
                throw new InvalidDataException($"{part}: no keys");
            }
            var frames = new float[count];
            var values = new float[count];
            var slopes = new float[count];
            switch (kind)
            {
                case 1:
                case 2:
                {
                    float step = BrawlBytes.F32(d, at + 8, part);
                    float bas = BrawlBytes.F32(d, at + 12, part);
                    int ks = kind == 1 ? 4 : 6;
                    long k0 = at + 16;
                    BrawlBytes.Need(d, k0, (long)ks * count, part + " keys");
                    for (int i = 0; i < count; i++)
                    {
                        long o = k0 + (long)ks * i;
                        if (kind == 1)
                        {
                            uint v = BrawlBytes.U32(d, o, part);
                            frames[i] = v >> 24;
                            values[i] = bas + (v >> 12 & 0xFFF) * step;
                            int s = (int)(v & 0xFFF);
                            if (s >= 0x800) s -= 0x1000;
                            slopes[i] = s / 32f;
                        }
                        else
                        {
                            frames[i] = BrawlBytes.U16(d, o, part) / 32f;
                            values[i] = bas + BrawlBytes.U16(d, o + 2, part) * step;
                            slopes[i] = BrawlBytes.I16(d, o + 4, part) / 256f;
                        }
                    }
                    break;
                }
                case 3:
                {
                    long k0 = at + 8;
                    BrawlBytes.Need(d, k0, 12L * count, part + " keys");
                    for (int i = 0; i < count; i++)
                    {
                        long o = k0 + 12L * i;
                        frames[i] = BrawlBytes.F32(d, o, part);
                        values[i] = BrawlBytes.F32(d, o + 4, part);
                        slopes[i] = BrawlBytes.F32(d, o + 8, part);
                    }
                    break;
                }
                default:
                    throw new InvalidDataException($"{part}: curve kind {kind} not supported");
            }
            for (int i = 1; i < count; i++)
            {
                if (!(frames[i] >= frames[i - 1]))
                {
                    throw new InvalidDataException($"{part}: key {i} frame {frames[i]} before key {i - 1} frame {frames[i - 1]}");
                }
            }
            return Curve.Keys(frames, values, slopes);
        }
    }
}
