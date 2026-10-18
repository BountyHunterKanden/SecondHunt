using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace MphRecomp.Import.Brawl
{
    // An 8-bit red, green, blue, alpha colour as stored in the effect file.
    public readonly record struct BrawlRgba(byte R, byte G, byte B, byte A);

    // One colour-combiner stage of an emitter (spec 10.1 +0x9C..+0xEB), every value in hardware numbering.
    public sealed class BrawlTevStage
    {
        public byte[] ColourInputs { get; init; } = new byte[4];    // a, b, c, d
        public byte ColourOperation { get; init; }
        public byte ColourBias { get; init; }
        public byte ColourScale { get; init; }
        public byte ColourClamp { get; init; }
        public byte ColourOutput { get; init; }                     // output register
        public byte[] AlphaInputs { get; init; } = new byte[4];     // a, b, c, d
        public byte AlphaOperation { get; init; }
        public byte AlphaBias { get; init; }
        public byte AlphaScale { get; init; }
        public byte AlphaClamp { get; init; }
        public byte AlphaOutput { get; init; }
        public byte KonstColourSelect { get; init; }                // +0xE4 (0x0C-0x0F = constant registers 0-3)
        public byte KonstAlphaSelect { get; init; }                 // +0xE8 (0x1C-0x1F = constant registers 0-3 alpha)
    }

    // The emitter of an effect (spec 10.1). Positions in comments are counted from the emitter's start.
    public sealed class BrawlEmitter
    {
        public const uint FlagLifeIsMaximum = 0x04, FlagInheritScale = 0x20, FlagInheritRotation = 0x40,
            FlagChildInheritScale = 0x80, FlagChildInheritRotation = 0x100;
        public const ushort DrawDepthTest = 0x01, DrawDepthTest2 = 0x04, DrawNoClip = 0x08, DrawTexture1Used = 0x10,
            DrawHidden = 0x400, DrawSharedSize = 0x2000, DrawSharedScale = 0x4000;
        public const byte ShapeBillboard = 3, ShapeDirectional = 4;

        public uint GeneralFlags { get; init; }                     // +0x00
        public uint EmissionFlags { get; init; }                    // +0x04
        public ushort EmitterLife { get; init; }                    // +0x08, 0xFFFF = forever
        public ushort ParticleLife { get; init; }                   // +0x0A
        public sbyte ParticleLifeRandom { get; init; }              // +0x0C percent
        public sbyte ChildParticleFollow { get; init; }             // +0x0D percent: child particles follow the parent
        public sbyte EmitIntervalRandom { get; init; }              // +0x0E percent
        public sbyte EmitCountRandom { get; init; }                 // +0x0F percent
        public float EmitCount { get; init; }                       // +0x10 particles per emission
        public ushort EmitStartFrame { get; init; }                 // +0x14
        public ushort RunAheadFrames { get; init; }                 // +0x16
        public ushort EmitInterval { get; init; }                   // +0x18 frames between emissions
        public sbyte ParticleFollow { get; init; }                  // +0x1A percent: particles follow the emitter
        public sbyte ChildEmitterFollow { get; init; }              // +0x1B percent
        public float[] ShapeParams { get; init; } = new float[6];   // +0x1C
        public ushort EmitSubdivision { get; init; }                // +0x34
        public sbyte InitialSpeedRandom { get; init; }              // +0x36 percent
        public sbyte MomentumRandom { get; init; }                  // +0x37 percent
        public float SpeedFromCentre { get; init; }                 // +0x38
        public float SpeedFromYAxis { get; init; }                  // +0x3C
        public float SpeedRandomDirection { get; init; }            // +0x40
        public float SpeedAlongNormal { get; init; }                // +0x44
        public float NormalSpread { get; init; }                    // +0x48 radians
        public float SpeedAlongDirection { get; init; }             // +0x4C
        public float DirectionSpread { get; init; }                 // +0x50 radians
        public Vector3 Direction { get; init; }                     // +0x54 rotation angles, radians
        public Vector3 EmitterScale { get; init; }                  // +0x60
        public Vector3 EmitterRotation { get; init; }               // +0x6C radians
        public Vector3 EmitterTranslation { get; init; }            // +0x78
        public byte FadeNear { get; init; }                         // +0x84
        public byte FadeFar { get; init; }                          // +0x85
        public byte FadeMinEmission { get; init; }                  // +0x86
        public byte FadeAlpha { get; init; }                        // +0x87
        public uint RandomSeed { get; init; }                       // +0x88, 0 = new each time
        public byte[] Reserved8C { get; init; } = new byte[8];      // +0x8C (zero)
        public ushort DrawFlags { get; init; }                      // +0x94
        public byte AlphaCompare0 { get; init; }                    // +0x96 hardware comparison
        public byte AlphaCompare1 { get; init; }                    // +0x97
        public byte AlphaCombine { get; init; }                     // +0x98 (0 = and)
        public byte TevStageCount { get; init; }                    // +0x99 (1-4)
        public byte TevClamp { get; init; }                         // +0x9A
        public byte IndirectStage { get; init; }                    // +0x9B (0 = none)
        public BrawlTevStage[] TevStages { get; init; } = Array.Empty<BrawlTevStage>(); // always 4 (+0x9C..+0xEB)
        public byte BlendMode { get; init; }                        // +0xEC (1 = blend, 3 = subtract)
        public byte SrcFactor { get; init; }                        // +0xED
        public byte DstFactor { get; init; }                        // +0xEE
        public byte LogicOp { get; init; }                          // +0xEF
        public byte[] ColourSources { get; init; } = new byte[4];   // +0xF0 vertex colour, colour registers 1-3
        public byte[] KonstSources { get; init; } = new byte[4];    // +0xF4 constant registers 0-3
        public byte DepthCompare { get; init; }                     // +0xF8
        public byte AlphaFlickerKind { get; init; }                 // +0xF9
        public ushort AlphaFlickerPeriod { get; init; }             // +0xFA
        public byte AlphaFlickerRandom { get; init; }               // +0xFC
        public byte AlphaFlickerAmplitude { get; init; }            // +0xFD
        public byte LightingMode { get; init; }                     // +0xFE
        public byte LightKind { get; init; }                        // +0xFF
        public BrawlRgba LightAmbient { get; init; }                // +0x100
        public BrawlRgba LightDiffuse { get; init; }                // +0x104
        public float LightRadius { get; init; }                     // +0x108
        public Vector3 LightPosition { get; init; }                 // +0x10C
        public float[] IndirectMatrix { get; init; } = new float[6]; // +0x118
        public sbyte IndirectScaleExponent { get; init; }           // +0x130
        public sbyte IndirectPivotX { get; init; }                  // +0x131
        public sbyte IndirectPivotY { get; init; }                  // +0x132
        public byte Reserved133 { get; init; }                      // +0x133 (zero)
        public byte ParticleShape { get; init; }                    // +0x134 (3 = billboard, 4 = directional)
        public byte[] ShapeOptions { get; init; } = new byte[7];    // +0x135
        public float DepthOffset { get; init; }                     // +0x13C
        // the emitter's bytes as stored (its size E, normally 0x140)
        public byte[] Raw { get; init; } = Array.Empty<byte>();

        public bool ParticleLifeIsMaximum => (GeneralFlags & FlagLifeIsMaximum) != 0;
        public bool InheritScale => (GeneralFlags & FlagInheritScale) != 0;
        public bool InheritRotation => (GeneralFlags & FlagInheritRotation) != 0;
        public bool ChildInheritScale => (GeneralFlags & FlagChildInheritScale) != 0;
        public bool ChildInheritRotation => (GeneralFlags & FlagChildInheritRotation) != 0;
        public bool DepthTest => (DrawFlags & (DrawDepthTest | DrawDepthTest2)) != 0;
        public bool NoClip => (DrawFlags & DrawNoClip) != 0;
        public bool Texture1Used => (DrawFlags & DrawTexture1Used) != 0;
        public bool Hidden => (DrawFlags & DrawHidden) != 0;
        public bool SharedSize => (DrawFlags & DrawSharedSize) != 0;
        public bool SharedScale => (DrawFlags & DrawSharedScale) != 0;
    }

    // The particle of an effect (spec 10.2). Positions in comments are counted from the particle's start.
    public sealed class BrawlParticle
    {
        // layer 1 primary, layer 1 secondary, layer 2 primary, layer 2 secondary
        public BrawlRgba[] Colours { get; init; } = new BrawlRgba[4];           // +0x00
        public Vector2 Size { get; init; }                                      // +0x10 width, height
        public Vector2 Scale { get; init; }                                     // +0x18
        public Vector3 Rotation { get; init; }                                  // +0x20 radians
        public Vector2[] TextureScale { get; init; } = new Vector2[3];          // +0x2C textures 1-3
        public Vector3 TextureRotation { get; init; }                           // +0x44 textures 1, 2, 3 (x, y, z), radians
        public Vector2[] TextureTranslation { get; init; } = new Vector2[3];    // +0x50
        public byte[] Reserved68 { get; init; } = new byte[12];                 // +0x68 (zero)
        public ushort TextureWrap { get; init; }                                // +0x74
        public byte TextureFlip { get; init; }                                  // +0x76
        public byte AlphaRef0 { get; init; }                                    // +0x77
        public byte AlphaRef1 { get; init; }                                    // +0x78
        public byte[] RotationRandom { get; init; } = new byte[3];              // +0x79 percent per axis
        public Vector3 RotationOffset { get; init; }                            // +0x7C radians
        public string[] TextureNames { get; init; } = new string[3];            // +0x88 ("" for an empty name)
        // the particle's bytes as stored (its size P)
        public byte[] Raw { get; init; } = Array.Empty<byte>();
    }

    // One key of a track (spec 12).
    public sealed class BrawlTrackKey
    {
        public ushort Frame { get; init; }                          // +0x00
        public ushort Reserved02 { get; init; }                     // +0x02 (zero)
        public byte[] FlagsA { get; init; } = new byte[4];          // +0x04
        public byte[] FlagsB { get; init; } = new byte[4];          // +0x08
        // value type 0: the C colour bytes (padding left out). Other non-float value types (texture, child, unknown):
        // every byte after +0x0C. Null for value types 3 and 6.
        public byte[]? Bytes { get; init; }
        // value types 3 and 6: the C floats. Null otherwise.
        public float[]? Floats { get; init; }
        // every byte after +0x0C as stored (padding included)
        public byte[] RawValue { get; init; } = Array.Empty<byte>();
        // the whole key as stored
        public byte[] Raw { get; init; } = Array.Empty<byte>();
    }

    // One animation track (spec 11.1 header, 12 keys and parts).
    public sealed class BrawlEffectTrack
    {
        public const byte ValueBytes = 0, ValueFloats = 3, ValueTexture = 4, ValueChild = 5, ValueRotation = 6;
        public const byte ProcessSharedRandom = 0x04, ProcessEmitterTimed = 0x10, ProcessFitToLife = 0x80;

        public bool IsParticleTrack { get; init; }                  // from the particle table (false = emitter table)
        public int Index { get; init; }                             // place within its table
        public byte Magic { get; init; }                            // +0x00 (0xAC)
        public byte Kind { get; init; }                             // +0x01 (spec 11.2)
        public byte ValueType { get; init; }                        // +0x02
        public byte ComponentBits { get; init; }                    // +0x03
        public int ComponentCount { get; init; }                    // set bits of ComponentBits (C)
        public byte ProcessFlags { get; init; }                     // +0x04
        public byte LoopCount { get; init; }                        // +0x05
        public ushort RandomSeed { get; init; }                     // +0x06
        public ushort FrameCount { get; init; }                     // +0x08
        public ushort Reserved0A { get; init; }                     // +0x0A (zero)
        public uint KeysSize { get; init; }                         // +0x0C
        public uint RangesSize { get; init; }                       // +0x10
        public uint RandomSize { get; init; }                       // +0x14
        public uint NamesSize { get; init; }                        // +0x18
        public uint ExtraSize { get; init; }                        // +0x1C
        public int KeyCount { get; init; }                          // keys part +0x00 (0 when the part is empty)
        public ushort KeysReserved { get; init; }                   // keys part +0x02 (zero)
        public int KeySize { get; init; }                           // bytes per key (0 without keys)
        public IReadOnlyList<BrawlTrackKey> Keys { get; init; } = Array.Empty<BrawlTrackKey>();
        // keys-part bytes after the last key (none expected)
        public byte[] KeysTrailing { get; init; } = Array.Empty<byte>();
        public byte[] KeysPart { get; init; } = Array.Empty<byte>();
        public byte[] RangesPart { get; init; } = Array.Empty<byte>();
        public int RangeCount { get; init; }                        // ranges part +0x00 (0 when the part is empty)
        public byte[] RandomPart { get; init; } = Array.Empty<byte>();
        public byte[] NamesPart { get; init; } = Array.Empty<byte>();
        public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();
        public byte[] ExtraPart { get; init; } = Array.Empty<byte>();
        // the whole track as stored (header included)
        public byte[] Raw { get; init; } = Array.Empty<byte>();

        public bool SharedRandom => (ProcessFlags & ProcessSharedRandom) != 0;
        public bool EmitterTimed => (ProcessFlags & ProcessEmitterTimed) != 0;
        public bool FitToLife => (ProcessFlags & ProcessFitToLife) != 0;
    }

    // One effect of an effect file (spec 9).
    public sealed class BrawlEffect
    {
        public string Name { get; init; } = "";
        public int DataPosition { get; init; }                      // counted from the start of the directory
        public int DataSize { get; init; }
        public uint Reserved00 { get; init; }                       // +0x00 (zero)
        public int EmitterSize { get; init; }                       // E
        public int ParticleSize { get; init; }                      // P
        public BrawlEmitter Emitter { get; init; } = new();
        public BrawlParticle Particle { get; init; } = new();
        public int ParticleTrackCount { get; init; }
        public int ParticleTracksAtBirth { get; init; }             // particle table +0x02
        public int EmitterTrackCount { get; init; }
        public int EmitterTracksAtBirth { get; init; }              // emitter table +0x02
        public IReadOnlyList<BrawlEffectTrack> ParticleTracks { get; init; } = Array.Empty<BrawlEffectTrack>();
        public IReadOnlyList<BrawlEffectTrack> EmitterTracks { get; init; } = Array.Empty<BrawlEffectTrack>();
    }

    // A particle effect file (an archive entry's data starting "REFF"): its set name and every effect, in directory
    // order. Clean-room from docs/cleanroom/E_brawl_effects_spec.md (sections 3.1-3.3, 9-12).
    public sealed class BrawlEffectFile
    {
        public string SetName { get; }
        public int Version { get; }
        public IReadOnlyList<BrawlEffect> Effects { get; }
        readonly Dictionary<string, BrawlEffect> _byName;

        internal BrawlEffectFile(string setName, int version, List<BrawlEffect> effects)
        {
            SetName = setName;
            Version = version;
            Effects = effects;
            _byName = new Dictionary<string, BrawlEffect>(StringComparer.Ordinal);
            foreach (var e in effects) _byName.TryAdd(e.Name, e);   // a repeated name: the first one wins
        }

        public IReadOnlyDictionary<string, BrawlEffect> ByName => _byName;
        public BrawlEffect? Find(string name) => _byName.TryGetValue(name, out var e) ? e : null;
        public BrawlEffect this[string name] => _byName.TryGetValue(name, out var e) ? e
            : throw new KeyNotFoundException($"effect file {SetName}: no effect named {name}");

        public static BrawlEffectFile Read(ReadOnlyMemory<byte> data) => BrawlEffects.Read(data);
        public static BrawlEffectFile Read(byte[] data) => BrawlEffects.Read(new ReadOnlyMemory<byte>(data));
    }

    public static class BrawlEffects
    {
        public const int EmitterMinSize = 0x140;
        public const int ParticleFixedSize = 0x88;
        public const int TrackHeaderSize = 0x20;
        public const byte TrackMagic = 0xAC;

        public static BrawlEffectFile Read(byte[] data) => Read(new ReadOnlyMemory<byte>(data));

        public static BrawlEffectFile Read(ReadOnlyMemory<byte> data)
        {
            var all = data.Span;
            const string hp = "effect file header";
            BrawlBytes.Magic(all, 0, "REFF", hp);
            int bom = BrawlBytes.U16(all, 4, hp);
            if (bom != 0xFEFF)
            {
                throw new InvalidDataException($"{hp}: byte-order mark 0x{bom:X4}, only big-endian (0xFEFF) is read");
            }
            int version = BrawlBytes.U16(all, 6, hp);
            uint fileSize = BrawlBytes.U32(all, 8, hp);
            BrawlBytes.Need(all, 0, fileSize, hp);
            var d = all.Slice(0, (int)fileSize);
            int blockAt = BrawlBytes.U16(d, 0x0C, hp);

            const string bp = "effect block";
            BrawlBytes.Magic(d, blockAt, "REFF", bp);
            uint dirPos = BrawlBytes.U32(d, blockAt + 8L, bp);
            int setLen = BrawlBytes.U16(d, blockAt + 0x14L, bp);
            string setName = BrawlBytes.Ascii(d, blockAt + 0x18L, setLen, bp + " set name");

            const string dp = "effect directory";
            long dir = blockAt + 8L + dirPos;
            int count = BrawlBytes.U16(d, dir + 4, dp);
            var effects = new List<BrawlEffect>(count);
            long at = dir + 8;
            for (int i = 0; i < count; i++)
            {
                string ip = $"{dp} item {i}";
                int nameLen = BrawlBytes.U16(d, at, ip);
                string name = BrawlBytes.Ascii(d, at + 2, nameLen, ip + " name");
                at += 2 + nameLen;
                uint pos = BrawlBytes.U32(d, at, ip);
                uint size = BrawlBytes.U32(d, at + 4, ip);
                at += 8;
                string ep = $"effect {name}";
                BrawlBytes.Need(d, dir + pos, size, ep);
                effects.Add(ReadEffect(d.Slice((int)(dir + pos), (int)size), name, (int)pos, ep));
            }
            return new BrawlEffectFile(setName, version, effects);
        }

        static BrawlEffect ReadEffect(ReadOnlySpan<byte> e, string name, int position, string part)
        {
            uint reserved = BrawlBytes.U32(e, 0, part);
            uint emitterSize = BrawlBytes.U32(e, 4, part + " emitter size");
            if (emitterSize < EmitterMinSize)
            {
                throw new InvalidDataException($"{part}: emitter size 0x{emitterSize:X} is below 0x{EmitterMinSize:X}");
            }
            BrawlBytes.Need(e, 8, emitterSize, part + " emitter");
            var emitter = ReadEmitter(e.Slice(8, (int)emitterSize), part + " emitter");

            long pAt = 8L + emitterSize;
            uint particleSize = BrawlBytes.U32(e, pAt, part + " particle size");
            BrawlBytes.Need(e, pAt + 4, particleSize, part + " particle");
            var particle = ReadParticle(e.Slice((int)(pAt + 4), (int)particleSize), part + " particle");

            long tAt = pAt + 4 + particleSize;
            string ptp = part + " particle track table";
            int pCount = BrawlBytes.U16(e, tAt, ptp);
            int pBirth = BrawlBytes.U16(e, tAt + 2, ptp);
            var pSizes = TableSizes(e, tAt, pCount, ptp);
            long eTab = tAt + 4 + 8L * pCount;
            string etp = part + " emitter track table";
            int eCount = BrawlBytes.U16(e, eTab, etp);
            int eBirth = BrawlBytes.U16(e, eTab + 2, etp);
            var eSizes = TableSizes(e, eTab, eCount, etp);

            long at = eTab + 4 + 8L * eCount;
            var pTracks = new List<BrawlEffectTrack>(pCount);
            for (int i = 0; i < pCount; i++)
            {
                string tp = $"{part} particle track {i}";
                BrawlBytes.Need(e, at, pSizes[i], tp);
                pTracks.Add(ReadTrack(e.Slice((int)at, (int)pSizes[i]), true, i, tp));
                at += pSizes[i];
            }
            var eTracks = new List<BrawlEffectTrack>(eCount);
            for (int i = 0; i < eCount; i++)
            {
                string tp = $"{part} emitter track {i}";
                BrawlBytes.Need(e, at, eSizes[i], tp);
                eTracks.Add(ReadTrack(e.Slice((int)at, (int)eSizes[i]), false, i, tp));
                at += eSizes[i];
            }
            if (at != e.Length)
            {
                throw new InvalidDataException(
                    $"{part}: animation tables end at 0x{at:X}, but the effect's size is 0x{e.Length:X}");
            }

            return new BrawlEffect
            {
                Name = name,
                DataPosition = position,
                DataSize = e.Length,
                Reserved00 = reserved,
                EmitterSize = (int)emitterSize,
                ParticleSize = (int)particleSize,
                Emitter = emitter,
                Particle = particle,
                ParticleTrackCount = pCount,
                ParticleTracksAtBirth = pBirth,
                EmitterTrackCount = eCount,
                EmitterTracksAtBirth = eBirth,
                ParticleTracks = pTracks,
                EmitterTracks = eTracks,
            };
        }

        // a table's N track sizes (after its N run-time words)
        static uint[] TableSizes(ReadOnlySpan<byte> e, long table, int count, string part)
        {
            BrawlBytes.Need(e, table, 4 + 8L * count, part);
            var sizes = new uint[count];
            for (int i = 0; i < count; i++) sizes[i] = BrawlBytes.U32(e, table + 4 + 4L * count + 4L * i, part);
            return sizes;
        }

        static BrawlEmitter ReadEmitter(ReadOnlySpan<byte> m, string part)
        {
            var stages = new BrawlTevStage[4];
            for (int s = 0; s < 4; s++)
            {
                stages[s] = new BrawlTevStage
                {
                    ColourInputs = Bytes(m, 0x9C + 4 * s, 4, part),
                    ColourOperation = BrawlBytes.U8(m, 0xAC + 5 * s, part),
                    ColourBias = BrawlBytes.U8(m, 0xAC + 5 * s + 1, part),
                    ColourScale = BrawlBytes.U8(m, 0xAC + 5 * s + 2, part),
                    ColourClamp = BrawlBytes.U8(m, 0xAC + 5 * s + 3, part),
                    ColourOutput = BrawlBytes.U8(m, 0xAC + 5 * s + 4, part),
                    AlphaInputs = Bytes(m, 0xC0 + 4 * s, 4, part),
                    AlphaOperation = BrawlBytes.U8(m, 0xD0 + 5 * s, part),
                    AlphaBias = BrawlBytes.U8(m, 0xD0 + 5 * s + 1, part),
                    AlphaScale = BrawlBytes.U8(m, 0xD0 + 5 * s + 2, part),
                    AlphaClamp = BrawlBytes.U8(m, 0xD0 + 5 * s + 3, part),
                    AlphaOutput = BrawlBytes.U8(m, 0xD0 + 5 * s + 4, part),
                    KonstColourSelect = BrawlBytes.U8(m, 0xE4 + s, part),
                    KonstAlphaSelect = BrawlBytes.U8(m, 0xE8 + s, part),
                };
            }
            return new BrawlEmitter
            {
                GeneralFlags = BrawlBytes.U32(m, 0x00, part),
                EmissionFlags = BrawlBytes.U32(m, 0x04, part),
                EmitterLife = BrawlBytes.U16(m, 0x08, part),
                ParticleLife = BrawlBytes.U16(m, 0x0A, part),
                ParticleLifeRandom = S8(m, 0x0C, part),
                ChildParticleFollow = S8(m, 0x0D, part),
                EmitIntervalRandom = S8(m, 0x0E, part),
                EmitCountRandom = S8(m, 0x0F, part),
                EmitCount = BrawlBytes.F32(m, 0x10, part),
                EmitStartFrame = BrawlBytes.U16(m, 0x14, part),
                RunAheadFrames = BrawlBytes.U16(m, 0x16, part),
                EmitInterval = BrawlBytes.U16(m, 0x18, part),
                ParticleFollow = S8(m, 0x1A, part),
                ChildEmitterFollow = S8(m, 0x1B, part),
                ShapeParams = Floats(m, 0x1C, 6, part),
                EmitSubdivision = BrawlBytes.U16(m, 0x34, part),
                InitialSpeedRandom = S8(m, 0x36, part),
                MomentumRandom = S8(m, 0x37, part),
                SpeedFromCentre = BrawlBytes.F32(m, 0x38, part),
                SpeedFromYAxis = BrawlBytes.F32(m, 0x3C, part),
                SpeedRandomDirection = BrawlBytes.F32(m, 0x40, part),
                SpeedAlongNormal = BrawlBytes.F32(m, 0x44, part),
                NormalSpread = BrawlBytes.F32(m, 0x48, part),
                SpeedAlongDirection = BrawlBytes.F32(m, 0x4C, part),
                DirectionSpread = BrawlBytes.F32(m, 0x50, part),
                Direction = V3(m, 0x54, part),
                EmitterScale = V3(m, 0x60, part),
                EmitterRotation = V3(m, 0x6C, part),
                EmitterTranslation = V3(m, 0x78, part),
                FadeNear = BrawlBytes.U8(m, 0x84, part),
                FadeFar = BrawlBytes.U8(m, 0x85, part),
                FadeMinEmission = BrawlBytes.U8(m, 0x86, part),
                FadeAlpha = BrawlBytes.U8(m, 0x87, part),
                RandomSeed = BrawlBytes.U32(m, 0x88, part),
                Reserved8C = Bytes(m, 0x8C, 8, part),
                DrawFlags = BrawlBytes.U16(m, 0x94, part),
                AlphaCompare0 = BrawlBytes.U8(m, 0x96, part),
                AlphaCompare1 = BrawlBytes.U8(m, 0x97, part),
                AlphaCombine = BrawlBytes.U8(m, 0x98, part),
                TevStageCount = BrawlBytes.U8(m, 0x99, part),
                TevClamp = BrawlBytes.U8(m, 0x9A, part),
                IndirectStage = BrawlBytes.U8(m, 0x9B, part),
                TevStages = stages,
                BlendMode = BrawlBytes.U8(m, 0xEC, part),
                SrcFactor = BrawlBytes.U8(m, 0xED, part),
                DstFactor = BrawlBytes.U8(m, 0xEE, part),
                LogicOp = BrawlBytes.U8(m, 0xEF, part),
                ColourSources = Bytes(m, 0xF0, 4, part),
                KonstSources = Bytes(m, 0xF4, 4, part),
                DepthCompare = BrawlBytes.U8(m, 0xF8, part),
                AlphaFlickerKind = BrawlBytes.U8(m, 0xF9, part),
                AlphaFlickerPeriod = BrawlBytes.U16(m, 0xFA, part),
                AlphaFlickerRandom = BrawlBytes.U8(m, 0xFC, part),
                AlphaFlickerAmplitude = BrawlBytes.U8(m, 0xFD, part),
                LightingMode = BrawlBytes.U8(m, 0xFE, part),
                LightKind = BrawlBytes.U8(m, 0xFF, part),
                LightAmbient = Rgba(m, 0x100, part),
                LightDiffuse = Rgba(m, 0x104, part),
                LightRadius = BrawlBytes.F32(m, 0x108, part),
                LightPosition = V3(m, 0x10C, part),
                IndirectMatrix = Floats(m, 0x118, 6, part),
                IndirectScaleExponent = S8(m, 0x130, part),
                IndirectPivotX = S8(m, 0x131, part),
                IndirectPivotY = S8(m, 0x132, part),
                Reserved133 = BrawlBytes.U8(m, 0x133, part),
                ParticleShape = BrawlBytes.U8(m, 0x134, part),
                ShapeOptions = Bytes(m, 0x135, 7, part),
                DepthOffset = BrawlBytes.F32(m, 0x13C, part),
                Raw = m.ToArray(),
            };
        }

        static BrawlParticle ReadParticle(ReadOnlySpan<byte> p, string part)
        {
            BrawlBytes.Need(p, 0, ParticleFixedSize, part);
            var colours = new BrawlRgba[4];
            for (int i = 0; i < 4; i++) colours[i] = Rgba(p, 4 * i, part);
            var texScale = new Vector2[3];
            var texTrans = new Vector2[3];
            for (int i = 0; i < 3; i++)
            {
                texScale[i] = V2(p, 0x2C + 8 * i, part);
                texTrans[i] = V2(p, 0x50 + 8 * i, part);
            }
            var names = new string[3];
            long at = ParticleFixedSize;
            for (int i = 0; i < 3; i++)
            {
                string np = $"{part} texture name {i + 1}";
                int len = BrawlBytes.U16(p, at, np);
                names[i] = BrawlBytes.Ascii(p, at + 2, len, np);
                at += 2 + len;
            }
            return new BrawlParticle
            {
                Colours = colours,
                Size = V2(p, 0x10, part),
                Scale = V2(p, 0x18, part),
                Rotation = V3(p, 0x20, part),
                TextureScale = texScale,
                TextureRotation = V3(p, 0x44, part),
                TextureTranslation = texTrans,
                Reserved68 = Bytes(p, 0x68, 12, part),
                TextureWrap = BrawlBytes.U16(p, 0x74, part),
                TextureFlip = BrawlBytes.U8(p, 0x76, part),
                AlphaRef0 = BrawlBytes.U8(p, 0x77, part),
                AlphaRef1 = BrawlBytes.U8(p, 0x78, part),
                RotationRandom = Bytes(p, 0x79, 3, part),
                RotationOffset = V3(p, 0x7C, part),
                TextureNames = names,
                Raw = p.ToArray(),
            };
        }

        static BrawlEffectTrack ReadTrack(ReadOnlySpan<byte> t, bool isParticle, int index, string part)
        {
            string hp = part + " header";
            BrawlBytes.Need(t, 0, TrackHeaderSize, hp);
            byte magic = t[0];
            if (magic != TrackMagic)
            {
                throw new InvalidDataException($"{hp}: first byte 0x{magic:X2}, expected 0x{TrackMagic:X2}");
            }
            byte kind = t[1], valueType = t[2], bits = t[3];
            int c = 0;
            for (int b = 0; b < 8; b++) if ((bits & (1 << b)) != 0) c++;
            var sizes = new uint[5];
            long total = TrackHeaderSize;
            for (int i = 0; i < 5; i++)
            {
                sizes[i] = BrawlBytes.U32(t, 0x0C + 4 * i, hp);
                total += sizes[i];
            }
            if (total != t.Length)
            {
                throw new InvalidDataException(
                    $"{part}: header + five parts make 0x{total:X} bytes, but the track's size is 0x{t.Length:X}");
            }
            long at = TrackHeaderSize;
            var keysPart = t.Slice((int)at, (int)sizes[0]); at += sizes[0];
            var rangesPart = t.Slice((int)at, (int)sizes[1]); at += sizes[1];
            var randomPart = t.Slice((int)at, (int)sizes[2]); at += sizes[2];
            var namesPart = t.Slice((int)at, (int)sizes[3]); at += sizes[3];
            var extraPart = t.Slice((int)at, (int)sizes[4]);

            // keys (spec 12)
            string kp = part + " keys";
            int keyCount = 0, keySize = 0;
            ushort keysReserved = 0;
            var keys = new List<BrawlTrackKey>();
            byte[] trailing = Array.Empty<byte>();
            if (keysPart.Length > 0)
            {
                keyCount = BrawlBytes.U16(keysPart, 0, kp);
                keysReserved = BrawlBytes.U16(keysPart, 2, kp);
                long room = keysPart.Length - 4;
                if (keyCount > 0)
                {
                    keySize = valueType switch
                    {
                        BrawlEffectTrack.ValueBytes => 12 + ((c + 1) & ~1),
                        BrawlEffectTrack.ValueFloats or BrawlEffectTrack.ValueRotation => 12 + 4 * c,
                        _ => (int)(room / keyCount),
                    };
                    if (keySize < 12)
                    {
                        throw new InvalidDataException($"{kp}: {keyCount} keys don't fit in 0x{room:X} bytes");
                    }
                }
                BrawlBytes.Need(keysPart, 4, (long)keyCount * keySize, kp);
                for (int k = 0; k < keyCount; k++)
                {
                    var key = keysPart.Slice(4 + k * keySize, keySize);
                    var value = key.Slice(12);
                    byte[]? bytes = null;
                    float[]? floats = null;
                    if (valueType == BrawlEffectTrack.ValueBytes) bytes = value.Slice(0, c).ToArray();
                    else if (valueType is BrawlEffectTrack.ValueFloats or BrawlEffectTrack.ValueRotation)
                    {
                        floats = Floats(value, 0, c, $"{kp} key {k}");
                    }
                    else bytes = value.ToArray();
                    keys.Add(new BrawlTrackKey
                    {
                        Frame = BrawlBytes.U16(key, 0, kp),
                        Reserved02 = BrawlBytes.U16(key, 2, kp),
                        FlagsA = key.Slice(4, 4).ToArray(),
                        FlagsB = key.Slice(8, 4).ToArray(),
                        Bytes = bytes,
                        Floats = floats,
                        RawValue = value.ToArray(),
                        Raw = key.ToArray(),
                    });
                }
                trailing = keysPart.Slice(4 + keyCount * keySize).ToArray();
            }

            int rangeCount = rangesPart.Length >= 2 ? BrawlBytes.U16(rangesPart, 0, part + " ranges") : 0;

            // names: u16 count, u16 zero, count x u32 (run time), then count x (u16 length incl. zero, name)
            var names = new List<string>();
            if (namesPart.Length > 0)
            {
                string np = part + " names";
                int count = BrawlBytes.U16(namesPart, 0, np);
                long nAt = 4 + 4L * count;
                BrawlBytes.Need(namesPart, 0, nAt, np);
                for (int i = 0; i < count; i++)
                {
                    int len = BrawlBytes.U16(namesPart, nAt, $"{np} {i}");
                    names.Add(BrawlBytes.Ascii(namesPart, nAt + 2, len, $"{np} {i}"));
                    nAt += 2 + len;
                }
            }

            return new BrawlEffectTrack
            {
                IsParticleTrack = isParticle,
                Index = index,
                Magic = magic,
                Kind = kind,
                ValueType = valueType,
                ComponentBits = bits,
                ComponentCount = c,
                ProcessFlags = t[4],
                LoopCount = t[5],
                RandomSeed = BrawlBytes.U16(t, 6, hp),
                FrameCount = BrawlBytes.U16(t, 8, hp),
                Reserved0A = BrawlBytes.U16(t, 0x0A, hp),
                KeysSize = sizes[0],
                RangesSize = sizes[1],
                RandomSize = sizes[2],
                NamesSize = sizes[3],
                ExtraSize = sizes[4],
                KeyCount = keyCount,
                KeysReserved = keysReserved,
                KeySize = keySize,
                Keys = keys,
                KeysTrailing = trailing,
                KeysPart = keysPart.ToArray(),
                RangesPart = rangesPart.ToArray(),
                RangeCount = rangeCount,
                RandomPart = randomPart.ToArray(),
                NamesPart = namesPart.ToArray(),
                Names = names,
                ExtraPart = extraPart.ToArray(),
                Raw = t.ToArray(),
            };
        }

        static sbyte S8(ReadOnlySpan<byte> d, long o, string part) => unchecked((sbyte)BrawlBytes.U8(d, o, part));

        static byte[] Bytes(ReadOnlySpan<byte> d, long o, int n, string part)
        {
            BrawlBytes.Need(d, o, n, part);
            return d.Slice((int)o, n).ToArray();
        }

        static float[] Floats(ReadOnlySpan<byte> d, long o, int n, string part)
        {
            BrawlBytes.Need(d, o, 4L * n, part);
            var f = new float[n];
            for (int i = 0; i < n; i++) f[i] = BrawlBytes.F32(d, o + 4L * i, part);
            return f;
        }

        static Vector2 V2(ReadOnlySpan<byte> d, long o, string part) =>
            new(BrawlBytes.F32(d, o, part), BrawlBytes.F32(d, o + 4, part));

        static Vector3 V3(ReadOnlySpan<byte> d, long o, string part) =>
            new(BrawlBytes.F32(d, o, part), BrawlBytes.F32(d, o + 4, part), BrawlBytes.F32(d, o + 8, part));

        static BrawlRgba Rgba(ReadOnlySpan<byte> d, long o, string part)
        {
            BrawlBytes.Need(d, o, 4, part);
            return new BrawlRgba(d[(int)o], d[(int)o + 1], d[(int)o + 2], d[(int)o + 3]);
        }
    }
}
