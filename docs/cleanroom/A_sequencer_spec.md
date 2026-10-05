# Clean-room specification A: DS sequenced-music engine

This document specifies the engine that plays the game's sequenced music: the sequence interpreter, the 16 voices it
drives, their envelopes and modulation, and the per-sample mixer the output stream calls. An implementer builds it from
this document alone. Every name in it is new except the handful of public names other code already calls (Part 9).

The result must be **bit-identical** to the current engine: the same stereo float samples for the same inputs and calls.
Where a rule below looks odd, it is deliberate: it reproduces observable behaviour, rounding and order of operations
included. Do not "fix" anything.

---

## Part 0. Conventions

- **Integers.** `u8/s8/u16/s16/u32/s32/u64` are unsigned/signed widths. Arithmetic is done in 32-bit signed integers
  (`int`) unless stated, and wraps on overflow. "Store as s16" means keep the low 16 bits as a signed value (C# cast).
- **Division** of integers truncates toward zero (C# `/`). `>>` on a signed value is an arithmetic shift (floor).
  A shift count is used as C# uses it: masked to 5 bits for 32-bit operands and 6 bits for 64-bit operands.
- **Floats.** `float` is IEEE single, `double` is IEEE double. Each expression is evaluated exactly in the order written,
  one operation at a time, in the stated type (no fused multiply-add, no reassociation). C# evaluates `a * b * c` as
  `(a * b) * c`; this document writes the parentheses where it matters.
- **Rounding to nearest** (`round(x)`) means `floor(x + 0.5)` in double, unless stated.
- **Clamp(x, lo, hi)** = `min(max(x, lo), hi)`.
- Bits are numbered from 0 (least significant).
- "Tick" means one engine tick (Part 2): 64 x 2728 cycles of the DS's 33,514,000 Hz sound clock (about 5.2095 ms).
- The engine is single-threaded: callers never call it from two threads at once.

## Part 1. Inputs (existing readers, unchanged)

The engine reads the game's sound data through the existing reader classes in `src/NcsfPlay/NC/` (namespace
`NCSFCommon.NC`). They stay as they are. What the engine uses:

- `SSEQ.Data` (`ReadOnlyMemory<byte>`): the sequence program (byte offsets below are into this).
- `SBNK.Entries[program]` (`SBNKInstrumentEntry`): `Record` (u8) and `Instruments` (array of `SBNKInstrument`).
- `SBNKInstrument`: `Record` (u8), `LowNote`, `HighNote`, `SWAV` (u16: a sample index, or for square-wave instruments
  the duty value), `SWAR` (u16: wave archive slot 0-3), `NoteNumber` (root key), `AttackRate`, `DecayRate`,
  `SustainLevel`, `ReleaseRate`, `Pan` (all u8).
- `SWAR.SWAVs[i]` (`SWAV`): one sample.
- `SWAV`: `WaveType` (0 = PCM8, 1 = PCM16, 2 = IMA-ADPCM), `Loop` (non-zero = loops), `Time` (u16, the sample's base
  timer), `LoopOffset` and `LoopLength` (u32, **already in decoded sample units**), and `Data` (`ReadOnlySpan<float>`,
  the decoded samples in -1..1). `LoopOffset + LoopLength` is the sample's length in decoded samples.

## Part 2. Overview

- **Voices.** 16 voices, numbered 0-15, like the DS's 16 sound channels. A voice plays one note of one of three
  **kinds**: *sample* (PCM/ADPCM, any voice), *square* (voices 8-13 only) or *noise* (voices 14 and 15 only).
- **Lanes.** A sequence runs up to 16 **lanes** (the SSEQ "tracks"), each a small interpreter with its own read
  position, parameters and list of voices it owns. Lanes are addressed by **slot** 0-15; a slot holds a lane or nothing.
- **Engine.** One engine instance = one sequence player: tempo, master volume, priority, 32 variables, the bank, four
  wave archive slots, 16 slots, 16 voices.
- **Tick** (`RunTick`, Part 7.1), in this order:
  1. **Commit** each voice's pending output changes (voices 0..15).
  2. **Sequencer clock**: advance the tempo accumulator; run zero or more **lane steps** (each steps every slot 0..15).
  3. **Push** each lane's parameters to its voices (slots 0..15).
  4. **Update** each voice: envelope, sweep, modulation, final volume, pitch and pan (voices 0..15).
- **Mixing** (`MixFrame`, Part 8): between ticks, the output stream asks for stereo frames one at a time; each frame
  takes one output sample from every sounding voice and advances it.

## Part 3. Tables and constants

None of these tables are stored in the code as data except the two short constant lists in 3.6. Build the others once
(static) from their formulas. Self-checks are given so the implementer can confirm the build (`sum` = sum of entries;
`wsum` = sum of (index + 1) x entry).

### 3.1 Pitch fraction table `P[i]`, i = 0..767 (u16)
`P[i] = round(65536 * (2^(i / 768) - 1))` computed in double (`Math.Pow(2, i / 768.0)`).
Self-check: P[0] = 0, P[1] = 59, P[767] = 65418, sum = 22,248,808, wsum = 11,748,945,286.

### 3.2 Attenuation-to-volume table `V[k]`, k = 0..723 (u8)
Let `a = k - 723` (so a runs -723..0; units are tenths of a decibel). Let `s` = 4 if a < -240, 2 if a < -120,
1 if a < -60, else 0, and `m = 2^s` (16, 4, 2 or 1).
`V[k] = min(127, round(128 * 10^(a / 200) * m))` in double, **except** that `V[602] = V[662] = V[722] = 126`
(the last entry before each divider step; the formula gives 127 there).
Self-check: V[0] = 0, V[1] = 1, V[482] = 127, V[483] = 32, V[723] = 127, sum = 30,610, wsum = 16,515,296.

### 3.3 Level-to-decibel table `D[x]`, x = 0..127 (s16)
`D[0] = -32768`; for x >= 1, `D[x] = max(-722, round(400 * log10(x / 127)))` in double.
Self-check: D[1] = -722, D[2] = -721, D[3] = -651, D[127] = 0, sum = -54,129, wsum = -754,298.

**Decibel-square function** `dB(n)` (n is 0..255): if bit 7 of n is set, use n = 127; return `D[n]`.

### 3.4 Quarter-sine table `S[i]`, i = 0..32 (u8)
`S[i] = round(127 * sin(pi * i / 64))` in double. Self-check: S[1] = 6, S[3] = 19, S[32] = 127, sum = 2,653,
wsum = 57,454.

**Sine lookup** `sinv(x)` for x = 0..127: x < 32 -> `S[x]`; x < 64 -> `S[64 - x]`; x < 96 -> `-S[x - 64]`;
else `-S[32 - (x - 96)]`.

### 3.5 Square-wave shape
For duty `d` (0..7) and step `j` (0..7): output `+1.0f` if `d < 7` and `j >= 7 - d`, else `-1.0f`. (Duty d is high for
d + 1 of 8 steps; duty 7 is constantly low.)

### 3.6 Driver constants (facts of the DS sound driver; also present in the game's own ARM7 binary)
- **Voice search order** (16 entries): `4, 5, 6, 7, 2, 0, 3, 1, 8, 9, 10, 11, 14, 12, 15, 13`.
- **Fast-attack table** `A[j]`, j = 0..18 (u8): `0, 1, 5, 14, 26, 38, 51, 63, 73, 84, 92, 100, 109, 116, 123, 127,
  132, 137, 143`.

### 3.7 Envelope coefficient functions
- **Attack coefficient** `atk(r)` for r = 0..127 (u8): r < 109 -> `255 - r`; else `A[127 - r]`. (Inputs above 127 never
  occur in valid data.)
- **Rate coefficient** `rate(r)` for r = 0..255 (u16): if bit 7 of r is set, use r = 0. Then r = 127 -> `65535`;
  r = 126 -> `15360`; r < 50 -> `2r + 1`; else `7680 / (126 - r)` (integer division).

### 3.8 Volume packing
`pack(v)` for an attenuation v (int): clamp v to -723..0; code `c` = 3 if v < -240, 2 if v < -120, 1 if v < -60,
else 0; result (u16) = `V[v + 723] | (c << 8)`. The low byte is the **level** (0..127), the high byte the **divider
code** (0..3).

**Divider factor** `f(c)` (float): c = 1 -> `0.5f`, 2 -> `0.25f`, 3 -> `0.0625f`, otherwise `1f`.

**Scale-by-127ths** `s7(x, n)` (float x, n = 0..127): n = 127 -> `x`; else `(x * (float)n) * 0.0078125f`.

### 3.9 Timer from pitch
`timer(base, p)` (base u16, p int pitch in 1/64 semitone units) -> u16:
1. `q = -p`, `oct = 0`. While q < 0: `oct -= 1; q += 768`. While q >= 768: `oct += 1; q -= 768`.
2. `r` (u64) = `(P[q] + 65536) * base`.
3. `sh = oct - 16`. If sh <= 0: `r = r >> (-sh)` (u64 shift; count masked to 6 bits). Else if sh < 32: if any of the
   bits of r at positions `32 - sh` and above are set, return 65535; else `r = r << sh`. Else return 65535.
4. Return clamp(r, 16, 65535).

### 3.10 Random numbers
One generator shared by the whole process (all engines), state u32 starting at `0x12345678`. Each draw:
`state = state * 1664525 + 1013904223` (wrapping), result = `state >> 16` (0..65535).

## Part 4. State

### 4.1 Engine
| Field | Type | Initial (construction) |
|---|---|---|
| tempo | u16 | 120 |
| tempoRatio (public `TempoRatio`) | u16 | 256 |
| tempoAcc | u16 | 240 |
| masterVolume | u8 | 127 |
| enginePriority | u8 | 64 |
| vars[32] | s16 | all -1 |
| slots[16] | lane or none | none |
| lanes[16] | lane objects | 16 fresh lanes |
| voices[16] | voice objects | 16 fresh voices |
| bank (`Bank`) | SBNK | none |
| archives[4] (`SetWaveArchive`) | SWAR | none |
| `SequenceVolume` | s16 | 0 |
| `ChannelMask` | u16 | 0 |
| `SampleRate` | u32 | 0 |
| `Interpolation` | enum | None |
| `TrackMutes` | u16 | 0 |
| `Smoothing`, `ExactPitch` | bool | false |

### 4.2 Lane
| Field | Type | Set by `laneInit` (4.4) |
|---|---|---|
| program data | `ReadOnlyMemory<byte>` | none |
| pos | int (-1 = not running) | -1 |
| flags: noteWait, tie, finishWait, portamento, cond | bool | noteWait = true, cond = true, tie = finishWait = portamento = false |
| inUse | bool | unchanged by laneInit |
| program | u16 | 0 |
| lanePriority | u8 | 64 |
| `Volume` (public) | u8 | 127 |
| expression | u8 | 127 |
| pan | s8 | 0 |
| bend | s8 | 0 |
| bendRange | u8 | 2 |
| transpose | s8 | 0 |
| attackOverride, decayOverride, sustainOverride, releaseOverride | u8 | 255 each (255 = no override) |
| portaKey | u8 | 60 |
| portaTime | u8 | 0 |
| sweep | s16 | 0 |
| modulation: target, speed, depth, range, delay | u8, u8, u8, u8, u16 | 0 (pitch), 16, 0, 1, 0 |
| wait | int | 0 |
| return stack: 3 positions + 3 loop counts (u8) | | depth = 0 (contents left as they were) |
| owned voices | ordered list of voice numbers | emptied |
| `Mute` (public) | bool | unchanged by laneInit |

### 4.3 Voice
Control and parameters:

| Field | Type | Notes |
|---|---|---|
| number | 0-15 | fixed |
| active | bool | |
| startPending | bool | a note was started; the voice update turns it into a pending start (7.4) |
| autoSweep | bool | |
| pending: stop, start, volume, pan | bools | output changes waiting for the next commit (7.2) |
| kind | sample / square / noise | |
| phase | attack / decay / sustain / release | |
| envLevel | int | |
| attackCoef | u8 | |
| decayCoef, releaseCoef | u16 | |
| sustainLevel | u8 | |
| priority | u8 | initial 0 |
| key, rootKey, velocity | u8 | |
| basePan | s8 | |
| lanePan | s8 | |
| laneVolume, lanePitch | s16 | |
| length | int | ticks left, -1 = until released |
| sweepPitch | s16 | |
| sweepCount, sweepLength | int | |
| mod: target, speed, depth, range, delay | as the lane's | |
| modDelayCount, modPhase | u16 | |
| packedVolume | u16 | initial 0 |
| lastTimer | u16 | initial 0; kept across notes |
| lastPan | u8 | initial 0; kept across notes |
| sample | SWAV | |
| baseTimer | u16 | |
| duty | int | |
| owner | lane or none | |

Output state (what the mixer reads):

| Field | Type | Notes |
|---|---|---|
| enabled | bool | initial false |
| outLevel | u8 | 0..127 |
| outDivider | u8 | 0..3 |
| outPan | u8 | 0..127 |
| outDuty | u8 | |
| outFormat | u8 | 0..2 = sample formats, 3 = square/noise |
| outLoopMode | u8 | 1 = loops, 2 = one shot, 0 = cleared |
| outLoopStart, outLoopLength, outEnd | u32 | outEnd = outLoopStart + outLoopLength |
| outSample | SWAV | |
| position | double | initial 0 |
| increment | double | initial 0 |
| noiseShift | u16 | |
| noiseOut | float | initial 0 |
| noiseSteps | u32 | initial 0 |
| window | padded sample copy (8.3) or none | |
| ramp state | gainL, gainR, stepL, stepR, targetL, targetR (float); rampInc, rampIncStep, targetInc (double); rampLeft (int); wasOn (bool) | all 0 / false |

### 4.4 Common operations

**laneInit(lane):** apply the "Set by laneInit" column of 4.2.

**voiceRelease(v):** `v.phase = release`.

**voiceFree(v):** `v.owner = none`.

**voiceEnd(v)** (a voice stops for good):
1. If `v.owner` is none: `v.priority = 0`. Otherwise, with `L = v.owner`: `v.priority = 0`, `v.owner = none`, and remove
   the first occurrence of v's number from L's owned list.
2. `v.packedVolume = 0`; `v.active = false`.

**detach(v)** (another note takes the voice over): if `v.owner` is a lane, remove the first occurrence of v's number
from that lane's owned list (nothing else).

**laneReleaseVoices(L, r)** (r = -1 or 0..255):
1. `push(L, false)` (Part 7.3).
2. For each voice number in L's owned list, in list order: if that voice is active: if r >= 0, `releaseCoef = rate(r & 255)`;
   `priority = 1`; voiceRelease.

**laneFreeVoices(L):** for each voice in L's owned list: voiceFree. Then empty the list.

**laneStop(L):** `pos = -1`; laneReleaseVoices(L, -1); laneFreeVoices(L).

**slotStop(i):** if slot i holds lane L: laneStop(L); `L.inUse = false`; slot i = none.

**engineStop:** slotStop(i) for i = 0..15.

## Part 5. The sequence program

### 5.1 Reading
All reads advance `pos`. `u8`: one byte. `u16`: two bytes, little-endian. `u24`: three bytes, little-endian.
`vlq`: repeat { b = u8; value = (value << 7) | (b & 0x7F) } while bit 7 of b is set, starting from value = 0 (32-bit).

**Value read** `value(kind)` returns an int:
- `byte`: u8. `word`: u16 (0..65535). `vlq`: vlq.
- `var`: `vars[u8]` (index 0..31 in valid data; sign-extended s16).
- `rand`: `lo = (s16)u16; hi = (s16)u16; n = hi - lo + 1; r = draw();` result = `((r * n) >> 16) + lo` (32-bit
  wrapping multiply, arithmetic shift).

Operands are always read (and random numbers always drawn) even when the command itself is skipped (5.3).

### 5.2 Starting a sequence: `Begin(sseq, seqVolume)`
1. engineStop.
2. Reset: tempo = 120, tempoRatio = 256, tempoAcc = 240, masterVolume = 127, enginePriority = 64, all slots = none,
   all vars = -1, every lane `inUse = false`, `SequenceVolume = seqVolume`.
3. Every voice v: `pending = none` (all four false); `enabled = false`; `outLevel = outDivider = outPan = outDuty =
   outLoopMode = outFormat = 0`; `v.active = true`. (Nothing else in the voice changes. The voices are marked active
   on purpose: the first voice update ends every voice that no note claimed, 7.4 step 2.)
4. Slot 0 gets a lane: take the lowest-numbered lane with `inUse = false`, set `inUse = true`, laneInit, data =
   `sseq.Data`, pos = 0.
5. Read `u8` at pos. If it is `0xFE`: read `mask = u16 >> 1`; for slot j = 1, 2, ... while mask != 0: if bit 0 of mask is
   set, give slot j a lane the same way (inUse, laneInit; its pos stays -1, it doesn't run until opened, 5.3 `0x93`);
   then `mask >>= 1`, j += 1. Otherwise move pos back one byte.

`Begin` does not run a tick; the caller does.

### 5.3 One lane step: `laneStep(L)` -> bool (false = the lane ended)
1. For each voice number in L's owned list (list order): if `length > 0`, `length -= 1`. If `!autoSweep` and
   `sweepCount < sweepLength`, `sweepCount += 1`.
2. If `finishWait`: if the owned list is not empty, return true; else `finishWait = false`.
3. If `wait > 0`: `wait -= 1`; if now `wait > 0`, return true.
4. While `wait == 0` and `!finishWait`: run one command (below). If the command was an executed `0xFF`, return false.
5. Return true.

(A negative `wait` stops the loop in step 4 and is never counted down again: the lane stalls. That's the existing
behaviour.)

**Running one command:**
1. `c = u8`; `run = true`; `kind = none`.
2. If c = `0xA2`: `c = u8`; `run = cond`.
3. If c = `0xA0`: `c = u8`; `kind = rand`.
4. If c = `0xA1`: `c = u8`; `kind = var`.
   (Each prefix is recognised once, in this order only.)
5. **Note** (c < 0x80): `vel = u8`; `len = value(kind or vlq)`. If run:
   `key = clamp(c + transpose, 0, 127)`; `playNote(L, key, vel, len > 0 ? len : -1)` (Part 6); `portaKey = key`;
   if `noteWait`: `wait = len`, and if `len == 0`, `finishWait = true`.
6. Otherwise by the high nibble of c:

**0x80-0x8F:** `n = value(kind or vlq)`. If run: `0x80` -> `wait = n`. `0x81` -> if n < 65536, `program = (u16)n`.
Other codes: nothing.

**0x90-0x9F:** (kind is ignored)
- `0x93`: `s = u8; off = u24`. If run: if slot s (s <= 15) holds a lane M and M is not L: laneStop(M), then M's data =
  L's data, M's pos = off.
- `0x94`: `off = u24`. If run: `pos = off`.
- `0x95`: `off = u24`. If run and depth < 3: push pos onto the return stack (depth += 1), `pos = off`.
- other codes: no operands, nothing.

**0xC0-0xDF:** `n = (u8)value(kind or byte)` (keep the low 8 bits). If run:

| Code | Effect |
|---|---|
| 0xC0 | `pan = (s8)(n - 64)` |
| 0xC1 | `Volume = n` |
| 0xC2 | `masterVolume = n` |
| 0xC3 | `transpose = (s8)n` |
| 0xC4 | `bend = (s8)n` |
| 0xC5 | `bendRange = n` |
| 0xC6 | `lanePriority = n` |
| 0xC7 | `noteWait = (n != 0)` |
| 0xC8 | `tie = (n != 0)`; then laneReleaseVoices(L, -1); laneFreeVoices(L) |
| 0xC9 | `portaKey = (u8)(n + transpose)`; `portamento = true` |
| 0xCA | modulation depth = n |
| 0xCB | modulation speed = n |
| 0xCC | modulation target = n (0 = pitch, 1 = volume, 2 = pan; any other value = no target) |
| 0xCD | modulation range = n |
| 0xCE | `portamento = (n != 0)` |
| 0xCF | `portaTime = n` |
| 0xD0 | `attackOverride = n` |
| 0xD1 | `decayOverride = n` |
| 0xD2 | `sustainOverride = n` |
| 0xD3 | `releaseOverride = n` |
| 0xD4 | if depth < 3: the stack entry at depth gets position = pos (after the operand) and loop count = n; depth += 1 |
| 0xD5 | `expression = n` |
| others | nothing (operand already read) |

**0xE0-0xEF:** `n = (s16)value(kind or word)`. If run: `0xE0` -> modulation delay = `(u16)n`. `0xE1` -> `tempo = (u16)n`.
`0xE3` -> `sweep = n`. Others: nothing.

**0xB0-0xBF:** `i = u8`; `n = (s16)value(kind or word)`. If run: `x = vars[i]` (s16), then:

| Code | x becomes (store as s16) |
|---|---|
| 0xB0 | n |
| 0xB1 | x + n |
| 0xB2 | x - n |
| 0xB3 | x * n |
| 0xB4 | if n != 0: x / n (truncating); else unchanged |
| 0xB5 | if n >= 0: x << n; else x >> (-n). The shift is on the sign-extended 32-bit value with the count masked to 5 bits |
| 0xB6 | `neg = n < 0`; if neg, `n = (s16)(-n)`; `r = (draw() * (n + 1)) >> 16`; if neg, `r = -r`; x = r |
| 0xB8 | unchanged; `cond = (x == n)` |
| 0xB9 | unchanged; `cond = (x >= n)` |
| 0xBA | unchanged; `cond = (x > n)` |
| 0xBB | unchanged; `cond = (x <= n)` |
| 0xBC | unchanged; `cond = (x < n)` |
| 0xBD | unchanged; `cond = (x != n)` |
| others | unchanged |

then `vars[i] = x` (always, even unchanged).

**0xF0-0xFF:** no operands. If run: `0xFD` -> if depth != 0: depth -= 1, `pos` = the stack position at depth.
`0xFC` -> if depth != 0: `k` = the loop count at depth - 1; if k != 0: k -= 1, and if k is now 0: depth -= 1 and
stop here. Otherwise store k back at depth - 1 and `pos` = the stack position at depth - 1. (A count of 0 loops
forever.) `0xFF` -> the lane ends (laneStep returns false). Others (including `0xFE` here): nothing.

**0xA0-0xAF** left over after the prefixes: no operands, nothing.

## Part 6. Notes

### 6.1 Finding the instrument: `instrument(program, key)`
If program >= the bank's entry count: none. Let `E = bank.Entries[program]`, `I = E.Instruments`. By `E.Record`:
- 1, 2, 3, 5: `I[0]`.
- 16 (drum table): if key < `I[0].LowNote` or key > `I[last].HighNote`: none; else `I[key - I[0].LowNote]`.
- 17 (key split): the first `I[j]` (in order) with `key <= I[j].HighNote`; none if there is none.
- others: none.

### 6.2 Choosing a voice: `claim(mask, prio)` -> voice or none
1. `best = none`. For each voice number n in the search order (3.6) with bit n of mask set, with V = voice n:
   if best is none, `best = V`; else if `V.priority <= best.priority` and (`V.priority != best.priority` or
   `louder(best, V)`), `best = V`.
   `louder(A, B)`: let `a = (A.packedVolume & 255) << 4` shifted right by `{0, 1, 2, 4}[A.packedVolume >> 8]`, and `b`
   the same for B; true if a > b.
2. If best is none or `prio < best.priority`: return none.
3. detach(best); `best.pending = {stop}` (stop only; the others cleared); `best.active = false`.
4. **Reset** best: `owner = L` (the calling lane); `length = sweepLength = sweepCount = 0`; `priority = (u8)prio`
   (low 8 bits); `packedVolume = 127`; `startPending = false`; `autoSweep = true`; `key = rootKey = 60`;
   `velocity = 127`; `basePan = lanePan = 0`; `laneVolume = lanePitch = sweepPitch = 0`;
   `attackCoef = atk(127)`; `sustainLevel = 127`; `decayCoef = rate(127)`; `releaseCoef = rate(127)`;
   modulation = target 0, speed 16, depth 0, range 1, delay 0.
5. Return best.

### 6.3 Starting a voice: `startVoice(V, instr, key, vel, len)` -> bool
1. `rel = instr.ReleaseRate`. If rel == 255: `len = -1`, `rel = 0`.
2. By `instr.Record`:
   - 1 (sample): `S = archives[instr.SWAR].SWAVs[instr.SWAV]`; `kind = sample`; `position = -11` if `S.WaveType == 2`,
     else `-3`; `sample = S`; `baseTimer = S.Time`.
   - 2 (square): fail if V's number is not 8..13. `kind = square`; `position = -1`; `duty = instr.SWAV`;
     `baseTimer = 8006`.
   - 3 (noise): fail if V's number is not 14 or 15. `kind = noise`; `position = -1`; `noiseShift = 0x7FFF`;
     `baseTimer = 8006`.
   - others: fail.
   Then (all three kinds) **arm**: `envLevel = -92544`; `phase = attack`; `length = len`; `modPhase = modDelayCount = 0`;
   `startPending = true`; `active = true`.
3. `key = key`; `rootKey = instr.NoteNumber`; `velocity = vel`; `attackCoef = atk(instr.AttackRate)`;
   `sustainLevel = instr.SustainLevel`; `decayCoef = rate(instr.DecayRate)`; `releaseCoef = rate(rel)`;
   `basePan = (s8)(instr.Pan - 64)`. Return true.

(A failed start changes nothing in step 2 or 3.)

### 6.4 Playing a note: `playNote(L, key, vel, len)`
1. `V = none`. If `L.tie` and L's owned list is not empty: `V` = the voice at the front of the list; `V.key = key`;
   `V.velocity = vel`.
2. If V is none:
   1. `instr = instrument(L.program, key)`; if none, stop.
   2. `mask` by `instr.Record`: 1 -> `0xFFFF`; 2 -> `0x3F00`; 3 -> `0xC000`; others: stop.
   3. `mask &= ChannelMask`.
   4. `V = claim(mask, enginePriority + L.lanePriority)`; if none, stop.
   5. If `!startVoice(V, instr, key, vel, L.tie ? -1 : len)`: `V.priority = 0`; voiceFree(V); stop.
   6. Insert V's number at the front of L's owned list.
3. Overrides: if `attackOverride != 255`, `V.attackCoef = atk(attackOverride)`; if `decayOverride != 255`,
   `V.decayCoef = rate(decayOverride)`; if `sustainOverride != 255`, `V.sustainLevel = sustainOverride`; if
   `releaseOverride != 255`, `V.releaseCoef = rate(releaseOverride)`.
4. `V.sweepPitch = L.sweep`. If `L.portamento`: `V.sweepPitch = (s16)(V.sweepPitch + (s16)((portaKey - key) << 6))`.
5. If `portaTime != 0`: `V.sweepLength = (portaTime * portaTime * |V.sweepPitch|) >> 11`. Else: `V.sweepLength = len`;
   `V.autoSweep = false`.
6. `V.sweepCount = 0`.

## Part 7. Ticks

### 7.1 `RunTick()`
1. For each voice 0..15: **commit** (7.2).
2. **Clock:** `n = 0`; while `tempoAcc >= 240`: `tempoAcc -= 240`; `n += 1`. Repeat n times: **lane pass** (each slot
   i = 0..15 in order: if slot i holds lane L and `L.pos != -1`: `L.Mute = (bit i of TrackMutes set)`; if
   `!laneStep(L)`: slotStop(i)). Then `tempoAcc = (u16)(tempoAcc + ((tempo * tempoRatio) >> 8))`.
3. For each slot i = 0..15 holding a lane L: `push(L, true)` (7.3).
4. For each voice 0..15: **update** (7.4).

Lanes see each other's changes immediately (a lane opened by slot 0 runs in the same pass if its slot is higher).

### 7.2 Commit (voice V)
If any of V's pending flags is set:
1. If `stop`: `enabled = false`.
2. If `start`: clear `outLevel, outDivider, outPan, outDuty, outLoopMode, outFormat` (to 0) and `enabled`; then
   `outPan = lastPan`; `outLevel = packedVolume & 255`; `outDivider = packedVolume >> 8`; by kind:
   - sample: `outFormat = sample.WaveType & 3`; `outLoopMode = sample.Loop != 0 ? 1 : 2`;
     `outLoopStart = sample.LoopOffset`; `outLoopLength = sample.LoopLength`; `outEnd = outLoopStart + outLoopLength`;
     `outSample = sample`.
   - square: `outFormat = 3`; `outDuty = (u8)duty`.
   - noise: `outFormat = 3`.
   Then `enabled = true` and clear all four pending flags.
3. Otherwise (no start): if `volume`: `outLevel = packedVolume & 255`, `outDivider = packedVolume >> 8`. If `pan`:
   `outPan = lastPan`. **The pending flags stay set** in this case (they are only cleared by a start).

### 7.3 Push (lane L to its voices): `push(L, release)`
1. `vol = L.Mute ? -32768 : dB(L.Volume) + dB(L.expression) + dB(masterVolume) + SequenceVolume`; if vol < -32768,
   vol = -32768.
2. `pit = (bend * (bendRange << 6)) >> 7`.
3. `pn = clamp(pan, -128, 127)`.
4. For each voice number in L's owned list (list order), voice V: if `V.phase != release`: `V.laneVolume = (s16)vol`;
   `V.lanePitch = (s16)pit`; `V.lanePan = (s8)pn`; V's modulation (target, speed, depth, range, delay) = L's; and if
   `V.length == 0` and `release`: `V.priority = 1`; voiceRelease(V).

### 7.4 Update (voice V)
If `!V.active`, do nothing. Otherwise:
1. If `startPending`: `pending.start = true`; `startPending = false`; `enabled = false`.
2. Else if `!enabled`: voiceEnd(V); stop here.
3. `vol = dB(velocity)`; `pit = (key - rootKey) * 64`.
4. **Envelope** (advances the envelope; returns e):
   - attack: `envLevel = -(((-envLevel) * attackCoef) >> 8)`; if `envLevel == 0`, phase = decay.
   - decay: `sus = dB(sustainLevel) << 7`; `envLevel -= decayCoef`; if `envLevel <= sus`: `envLevel = sus`,
     phase = sustain.
   - sustain: nothing.
   - release: `envLevel -= releaseCoef`.
   e = `envLevel >> 7`. `vol += e`.
5. **Sweep** (returns w): if `sweepPitch != 0` and `sweepCount < sweepLength`: w = `(s64)sweepPitch * (sweepLength -
   sweepCount) / sweepLength` (64-bit, truncating), and if `autoSweep`, `sweepCount += 1`. Else w = 0. `pit += w`.
6. `vol += laneVolume`; `pit += lanePitch`.
7. **Modulation** (returns m):
   1. Value: if `depth == 0` or `modDelayCount < delay`: 0. Else `sinv(modPhase >> 8) * depth * range` (int).
   2. If that value is not 0: target volume -> multiply by 60; target pitch or pan -> multiply by 64 (`<< 6`); other
      targets: unchanged. Then `>> 14` (64-bit arithmetic shift). m = the result (as int).
   3. Advance: if `modDelayCount < delay`: `modDelayCount += 1`. Else: `t = (modPhase + (speed << 6)) >> 8` (u32);
      while t >= 128: t -= 128; `modPhase = (u16)(modPhase + (speed << 6))`; `modPhase &= 0xFF`;
      `modPhase |= (u16)(t << 8)`.
8. `pn = 0`. By target: volume -> if `vol > -32768`, `vol += m`; pan -> `pn += m`; pitch -> `pit += m`; other: nothing.
9. `pn += basePan`; `pn += lanePan`.
10. If `phase == release` and `vol <= -723`: `pending = {stop}` (stop only, others cleared); voiceEnd(V); stop here.
11. `pv = pack(vol)`. `t = timer(baseTimer, pit)`; if kind = square, `t &= 0xFFFC`. `pn = clamp(pn + 64, 0, 127)`.
12. If `pv != packedVolume`: `packedVolume = pv`; `pending.volume = true`.
13. If `t != lastTimer`: `lastTimer = t`; `increment = (33514000.0 / (SampleRate * 2.0)) / t` (double).
14. If `ExactPitch` and kind = sample: `x = clamp(baseTimer * Math.Pow(2.0, (double)(-pit) / 768.0), 16.0, 65535.0)`;
    `increment = (33514000.0 / (SampleRate * 2.0)) / x`.
15. If `pn != lastPan`: `lastPan = (u8)pn`; `pending.pan = true`.

Note: `increment` changes immediately (step 13/14); level and pan reach the output only at the next commit.

## Part 8. Mixing

### 8.1 `MixFrame(voiceMutes)` -> (float left, float right)
`L = 0f`, `R = 0f`. For each voice V in 0..15:
1. If `!V.active` or `!V.enabled`: skip V.
2. `muted = bit (V's number) of voiceMutes`.
3. `x = muted ? 0f : produce(V)` (8.2).
4. `advance(V)` (8.4). (Even when muted.)
5. If not muted:
   - if `Smoothing`: `L += x * V.gainL`; `R += x * V.gainR` (the gains as they are after step 4);
   - else: `x = s7(x, outLevel) * f(outDivider)`; `L += s7(x, (u8)(127 - outPan))`; `R += s7(x, outPan)`.
Return (L, R).

### 8.2 `produce(V)` -> float
1. If `position < 0`: return 0.
2. If `outFormat != 3` (a sample):
   - Interpolation None: return `outSample.Data[(int)position]` (truncation).
   - Interpolation Sinc: return `sinc(V)` (8.3).
3. If V's number < 8: return 0. If V's number < 14 (square): return the square-wave shape (3.5) for duty `outDuty` and
   step `(int)position & 7`.
4. Noise: let `p = (u32)position`. If `noiseSteps != p`: for `i = noiseSteps; i < p; i++`: if bit 0 of `noiseShift`
   is set: `noiseShift = (noiseShift >> 1) ^ 0x6000`, `noiseOut = -1f`; else `noiseShift >>= 1`, `noiseOut = 1f`.
   Then `noiseSteps = p`. Return `noiseOut`. (`noiseSteps` and `noiseOut` are not reset when a new note starts.)

### 8.3 Sinc interpolation
**Static tables** (float, 65,537 entries each, index i = 0..65536): `x_i = i / 8192` (exact in float; accumulate
`x += 1f / 8192` from 0, or compute `i * (1f / 8192)`; both are exact). `y_i = x_i / 8`.
- `K[i] = x_i == 0 ? 1f : MathF.Sin(x_i * MathF.PI) / (x_i * MathF.PI)` (the multiply `x_i * MathF.PI` is a float
  multiply, done separately for the numerator's argument and the denominator). (`x_i < 8` for every i except 65536,
  where `K[65536] = 0`.)
- `W[i] = (0.40897f + (0.5f * MathF.Cos(MathF.PI * y_i))) + (0.09103f * MathF.Cos((2f * MathF.PI) * y_i))`.

**Window** (made in 8.4, held by the voice): a float array of `n + 16` entries where n = `outSample.Data.Length`:
entries 0..7 = `Data[0]`; entries 8..n+7 = `Data`; entries n+8..n+15 = if `outLoopMode == 1`: `Data[outLoopStart ..
outLoopStart + 7]`, else 0.

**sinc(V):**
1. `ip = (int)position`; `frac = position - ip` (double).
2. `shift = (int)Math.Floor(frac * 8192)`.
3. `step = increment > 1 ? (int)(8192 / increment) : 8192`. (`increment` is the voice's own value from 7.4, not the
   smoothed one.)
4. `adj = shift * step / 8192` (int, truncating).
5. Kernel: `ksum = 0f`. For `j = 8` down to `-7`: `k_j = K[|adj - j * step|] * W[|shift - j * 8192|]`; `ksum += k_j`
   (in this order: j = 8, 7, ..., -7).
6. `acc = 0f`. For `j = -7` up to `8`: `acc += window[ip + j + 8] * k_j` (in this order).
7. Return `acc / ksum`.

### 8.4 `advance(V)`
1. `next = position + (Smoothing ? rampInc : increment)`.
2. If `Smoothing` and `rampLeft > 0`: `rampLeft -= 1`; if `rampLeft == 0`: `gainL = targetL`, `gainR = targetR`,
   `rampInc = targetInc`; else: `gainL += stepL`, `gainR += stepR`, `rampInc += rampIncStep`.
3. If `outFormat != 3` and Interpolation is not None and `position < 0` and `next >= 0`: make the window (8.3).
4. `position = next`.
5. If `outFormat != 3` and `position >= outEnd`: if `outLoopMode == 1`: while `position >= outEnd`:
   `position -= outLoopLength`. Else: voiceEnd(V); window = none.

### 8.5 `BeginRamps(samplesPerTick)` (smoothing; the stream calls it after each tick and once before the first frame)
For each voice V (0..15):
1. `g = s7(1f, outLevel) * f(outDivider)`; `targetL = s7(g, (u8)(127 - outPan))`; `targetR = s7(g, outPan)`;
   `targetInc = increment`.
2. `on = V.active && V.enabled`.
3. If `on` and not `wasOn`: `gainL = targetL`; `gainR = targetR`; `rampInc = targetInc`; `rampLeft = 0`.
   Else: `stepL = (targetL - gainL) / samplesPerTick`; `stepR = (targetR - gainR) / samplesPerTick` (float);
   `rampIncStep = (targetInc - rampInc) / samplesPerTick` (double); `rampLeft = samplesPerTick`.
4. `wasOn = on`.

## Part 9. Public interface

The engine lives in the `NcsfPlay` project (`src/NcsfPlay/`, net9.0, nullable enabled, implicit usings). Public types
and members (exact names; everything else internal or private):

```csharp
namespace NCSFPlayer
{
    public enum Interpolation { None, Sinc }

    public sealed class Player
    {
        public const float TickSeconds = 174592f / 33514000f;   // 64 x 2728 cycles at 33,514,000 Hz, in float
        public static short DecibelSquare(int level);            // dB(level), 3.3

        public uint SampleRate { get; set; }
        public Interpolation Interpolation { get; set; }
        public ushort ChannelMask { get; set; }
        public ushort TrackMutes { get; set; }
        public ushort TempoRatio { get; set; }
        public bool Smoothing { get; set; }
        public bool ExactPitch { get; set; }
        public short SequenceVolume { get; set; }
        public NCSFCommon.NC.SBNK Bank { get; set; }
        public void SetWaveArchive(int slot, NCSFCommon.NC.SWAR archive);   // slot 0..3

        public void Begin(NCSFCommon.NC.SSEQ sseq, short sequenceVolume);   // 5.2
        public void RunTick();                                              // 7.1
        public void BeginRamps(int samplesPerTick);                         // 8.5
        public (float Left, float Right) MixFrame(ushort voiceMutes);       // 8.1
        public void Stop();                                                 // engineStop
        public NCSFCommon.Track? GetTrack(int slot);   // the lane in slot (0..15), else null (any other value: null)
    }
}

namespace NCSFCommon
{
    public sealed class Track          // a lane
    {
        public byte Volume { get; set; }   // the lane's volume (5.3 0xC1, 7.3)
        public bool Mute { get; set; }     // 7.1 reassigns it from TrackMutes whenever the lane is stepped
    }
}
```

`SequenceVolume` can change at any time; voices pick it up at the next push. `TempoRatio` set before `Begin` is reset
to 256 by `Begin`. `Track` objects are owned by the engine; callers may read and set `Volume` and `Mute` between calls.

**Callers (read these, don't change them except where Part 10 says):** `src/NcsfPlay/Player/NCSFPlayerStream.cs` (the
output stream: builds the engine, runs ticks on its sample clock, calls `MixFrame`), `src/MphRead.Android/GameAudio.cs`,
`src/MphRead.Android/MusicTestActivity.cs`, `src/MphRead/Sound/Music.cs`, `src/MphRead.Tools/AudioLevels.cs`.

## Part 10. What to write, what not to open, when it's done

**Write** (new files only): the engine in `src/NcsfPlay/Engine/` (any number of files; suggested split: tables,
engine/sequence, lane interpreter, voice, mixer). Namespaces: the public types as in Part 9, internals in
`NCSFPlayer.Engine`.

**Already done for you:** the old engine files are removed from your copy, and `NCSFPlayerStream.cs` already calls the
Part 9 interface. Until your engine exists the tree doesn't compile; that's expected.

**Don't open:** any earlier version of this engine (the project's git history, any `*.orig`, backups); NCSF or in_xsf
sources outside your copy; pret's decompilations (pokediamond or any other); DeSmuME, melonDS or any other DS
emulator's sound code; any other SSEQ/SDAT player (FSS, sseq2mid, VGMTrans, nitro players, etc.). Use only this
specification, the files in your copy, GBATEK, and the .NET documentation. Don't use the web for anything about DS
sound.

**Done when:**
1. `src/NcsfPlay`, `src/MphRead` and `src/MphRead.Tools` build in Release with no new warnings in your files.
2. The Part 3 self-checks pass (assert them in a static constructor or a test).
3. A smoke run renders a few sequences without exceptions: `MphRead.Tools.exe -audiolevels` (or a small
   `NCSFPlayerStream` loop of your own) produces non-silent, non-clipped output.
4. You report every place where this specification could be read more than one way and which reading you chose.

Bit-exactness against the old engine is checked separately after you finish (a harness that you won't see).
