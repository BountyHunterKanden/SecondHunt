# D: Echoes streamed music, specification

This is the specification for a new decoder: the music streams of Metroid Prime 2: Echoes (GameCube), for the Echoes
multiplayer arenas. Nothing is being replaced. The facts below were measured on the files themselves (the 84 streams in
the disc's `Audio` folder, every one checked) plus the GameCube's DSP-ADPCM sound format, a fact of the console's sound
hardware. The decoding maths was checked bit for bit against the decoder state that Metroid Prime's own (standard
Nintendo) stream headers store at their loop points.

The specification contains no code. The implementer writes the decoder from this document alone.

## 1. What the decoder does

It turns one stream file into 16-bit PCM, one or two channels, at the file's sample rate (32,000 Hz in every file), and
plays it in a loop when the file says so. The app's music player pulls samples from it a block at a time, in real time.

## 2. The file

All numbers are big-endian. The file is a 96-byte (0x60) header followed by the sound data.

### 2.1 Header

| Offset | Size | Meaning |
|---|---|---|
| 0x00 | 4 | Signature: the bytes `52 53 00 03` (the letters "RS", then 0, then 3) |
| 0x04 | 4 | Channel count: 1 (mono) or 2 (stereo; channel 0 is left, channel 1 is right) |
| 0x08 | 4 | Sample count per channel (N) |
| 0x0C | 4 | Sample rate in Hz |
| 0x10 | 4 | End unit: the per-channel byte offset (see 2.2) of the 32-byte unit that holds the last sample. Informational; the decoder doesn't need it |
| 0x14 | 4 | Loop flag: 0x00010000 for a looping stream, 0 otherwise. Treat any non-zero value as looping |
| 0x18 | 4 | Loop start: a per-channel byte offset, always a multiple of 32 (0 when not looping) |
| 0x1C | 4 | Loop end unit: the per-channel byte offset of the 32-byte unit that holds the last looped sample, a multiple of 32 (equal to the end unit when not looping) |
| 0x20 | 32 | Channel 0's 16 coefficients: signed 16-bit numbers, read as 8 pairs (pair p = coefficients 2p and 2p+1) |
| 0x40 | 32 | Channel 1's 16 coefficients, the same way (all zero in a mono file) |

Observed on the disc: 68 stereo and 16 mono streams, all 32,000 Hz; 47 loop, 37 don't. The largest coefficient
magnitude is 4043.

### 2.2 Sound data and its interleave

Each channel's data, taken on its own, is a run of 8-byte frames (section 3). Frame f of a channel sits at that
channel's byte offset 8f. These per-channel offsets are what the header's loop and end fields count in.

The channels are interleaved in blocks. Let D be the length of the sound data (file length minus 0x60), C the channel
count and K = 0x8F00 (36,608) the block size:

- The data starts with F full block groups, where F = floor(D / (K x C)). Group g holds channel 0's next K bytes, then
  channel 1's next K bytes (for stereo).
- The rest of the data, R = D - F x K x C bytes, is one last short group. Each channel gets the same share,
  T = R / C bytes, channel 0 first. R is always a whole multiple of C (in every file it is also a multiple of 32, so T
  is a multiple of 16).

So the per-channel byte offset q of channel c is found in the file at:

- if q < F x K: file offset 0x60 + floor(q / K) x K x C + c x K + (q mod K);
- otherwise: file offset 0x60 + F x K x C + c x T + (q - F x K), which requires q - F x K < T.

Each channel therefore has F x K + T bytes, which is floor((F x K + T) / 8) whole frames. The 1-channel case is the
same rule with C = 1 (the data is simply one channel's frames in order).

**Capacity.** A channel can hold at most (its frame count) x 14 samples. If the header's sample count is larger than
that, use the capacity instead. (No file on the disc needs this; it guards against damaged files.)

**Filler.** The last channel's final one to three frames are often filled with bytes 0xFF, past the end of real sound
data (the other channel's last frame holds real samples there). Section 3.3 says how to decode them.

## 3. Decoding a channel

### 3.1 A frame

A frame is 8 bytes:

- byte 0 is the frame header: the predictor index P = bits 4 to 6 (that is, (byte >> 4) AND 7), and the scale
  exponent E = bits 0 to 3 (byte AND 15). Bit 7 is never set in real sound data (see 3.3);
- bytes 1 to 7 hold 14 sample codes, 4 bits each, in order: byte 1's high 4 bits are code 0, its low 4 bits code 1,
  byte 2's high 4 bits code 2, and so on to byte 7's low 4 bits = code 13. Each code is a signed 4-bit number (two's
  complement: 0 to 7 stand for 0 to 7, 8 to 15 stand for -8 to -1).

Sample index i of a channel is code (i mod 14) of frame floor(i / 14).

### 3.2 A sample

Each channel keeps two history values, H1 (the previous output sample) and H2 (the one before it), both 0 at the start
of the stream. With the frame's coefficient pair A = coefficient 2P and B = coefficient 2P+1 of that channel, and the
code n (-8 to 7), each sample is:

1. X = n x 2^E x 2048 (equivalently n shifted left by E + 11 bits);
2. Y = X + 1024 + A x H1 + B x H2, computed exactly (64-bit integers are always safe; with the disc's coefficients
   32-bit signed integers never overflow, but use 64 bits anyway);
3. Z = floor(Y / 2048) (an arithmetic shift right by 11 bits, which rounds towards minus infinity);
4. the output sample is Z clamped to the range -32768 to 32767;
5. then H2 takes H1's value and H1 takes the output sample.

The 1024 before the division makes step 3 round to nearest, with halves rounded up.

### 3.3 Filler frames

A frame whose header byte has bit 7 set (a value of 0x80 or more) is filler, not sound. Each of its 14 samples is the
channel's most recent output sample (0 if the channel has output nothing yet), and the history (H1, H2) is left exactly
as it was. Without this rule, the 0xFF filler (predictor 7, exponent 15, codes of -1) would decode to a full-scale
click at the end of a stream, and at every loop of a stream that loops at its very end, such as the arenas' main
battle theme.

## 4. Playing a stream

Positions are sample indices of a channel (0 is the first sample); both channels always move together.

**Not looping** (loop flag 0): play samples 0 to N-1 once, then the stream has finished.

**Looping** (loop flag non-zero):

- loop start S = (loop start bytes / 8) x 14;
- loop end L = min(N, (loop end unit bytes / 8) x 14 + 56), where 56 is the samples in one 32-byte unit; L is exclusive;
- play samples 0 to L-1, then S to L-1 again and again, for ever.

At the jump from L-1 back to S, nothing is reset: each channel's history (H1, H2) and most recent output sample carry
on as if sample S came straight after sample L-1. (The file stores no history for the loop point, so carrying on is the
only choice that needs none.)

If the loop flag is set but S is not less than L, treat the stream as not looping.

The loop end field only gives the 32-byte unit, not the exact sample, so L can be up to 55 samples (1.7 ms) late. Where
the unit is the stream's last, as for the main battle theme, L is exactly the sample count.

## 5. The interface

New names, in the namespace `MphRecomp.Import.Retro`, project `MphRecomp.Core` (.NET 9, C#, nullable reference
types enabled, no unsafe code needed, no dependencies outside the .NET base library). Three public types:

**`DspAdpcm`** (static class): the codec on its own, for use on any GameCube DSP-ADPCM data.
- `DspAdpcm.ChannelState`: a public mutable struct holding one channel's decoder state: `Hist1` (int, H1), `Hist2`
  (int, H2) and `Held` (short, the most recent output sample, for filler frames). Its default value is the start state.
- `static void DecodeFrame(ReadOnlySpan<byte> frame, ReadOnlySpan<short> coefficients, ref ChannelState state,
  Span<short> output)`: decodes one frame (`frame` holds at least 8 bytes; `coefficients` holds the channel's 16
  coefficients; `output` receives exactly 14 samples) per sections 3.1 to 3.3, updating `state`.

**`RetroStream`** (sealed class): one parsed file.
- `RetroStream(byte[] file)`: parses and checks the header. Throws `InvalidDataException` (with a short reason) when
  the file is shorter than the header, the signature is wrong, the channel count isn't 1 or 2, the sample rate is 0, or
  the data is too short to hold even one frame per channel. Keeps a reference to `file` (doesn't copy it).
- `static bool IsRetroStream(ReadOnlySpan<byte> start)`: true when `start` holds at least 8 bytes, begins with the
  signature and the channel count is 1 or 2.
- Read-only properties: `int Channels`; `int SampleRate`; `int SampleCount` (N after the capacity limit of 2.2);
  `bool Loops` (looping per section 4, after the S < L check); `int LoopStart` and `int LoopEnd` (S and L when
  looping; 0 and `SampleCount` when not).
- `ReadOnlySpan<short> Coefficients(int channel)`: that channel's 16 coefficients.
- `RetroStreamReader OpenReader()`: a new reader at sample 0.

**`RetroStreamReader`** (sealed class): plays one stream from start to finish (or for ever, when it loops).
- `int Read(Span<short> destination, int frames)`: writes up to `frames` sample frames into `destination`,
  interleaved (each sample frame is `Channels` samples, channel 0 first), and returns how many sample frames it wrote.
  It writes fewer than asked only when a non-looping stream reaches its end; once finished it returns 0. A looping
  stream always fills the request. `destination` must hold at least `frames x Channels` samples; throw
  `ArgumentException` otherwise.
- `long Position`: the index of the next sample to be played (always between 0 and `SampleCount`; after a loop jump
  it is back near `LoopStart`).
- `bool Finished`: true once a non-looping stream has played its last sample.
- `void Restart()`: back to sample 0 with fresh decoder state, as a new reader would be.

Requirements on the reader: no memory allocated per `Read` call (allocate any working buffers once, when the reader is
created); it decodes frames as it goes rather than the whole file at once; it must keep up with real time with a large
margin on a phone (tens of times faster than real time is expected); it isn't shared between threads.

## 6. How it will be checked

Every output sample of every stream on the disc will be compared with an independent decoder written from this same
specification: one full play of each stream, and for looping streams the first three passes through the loop, read in
blocks of varying sizes. The codec will also be checked bit for bit against the decoder state that Metroid Prime's
standard stream headers store at their loop points.
