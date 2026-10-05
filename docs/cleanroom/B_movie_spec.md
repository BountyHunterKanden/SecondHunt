# Clean-room spec B: VX movie decoder

Written 2026-10-05 by the spec author (who has read the code being replaced). The implementer works only from this file,
the files named under "What you may read", and the public ITU-T H.264 standard. Nothing here is code from the replaced
implementation: it states required behaviour as bit layouts and arithmetic, and the names are new.

Goal: new source for the decoder of the game's `.vx` movies (video and audio), behind the same public class the game
already calls (`MphRead.Formats.VxDecoder`, Part 7). The output must be **identical** to the current decoder's: every
frame's RGB24 bytes and every audio block's 16-bit samples, so the rules below are exact, including rounding, the order
in which samples are read and written, and what is left in memory between frames.

All 59 movie files in Metroid Prime Hunters are 256 x 192 pixels, 15 frames a second, quantiser 31 or 35, one mono audio
stream at 22,050 Hz, at most 7,602 payload bytes a frame, about 11.5 audio blocks a frame.

---

## Part 0. Conventions

- **Integers.** Every quantity is a 32-bit signed integer, and every addition, subtraction and multiplication wraps on
  overflow (C#'s default unchecked `int`). Don't widen to 64 bits anywhere; the audio part in particular depends on
  products being formed in 32 bits.
- **a ÷ b** is division rounding toward zero (C# `/` on `int`): 7 ÷ 2 = 3, −7 ÷ 2 = −3, −1 ÷ 2 = 0.
- **a >> n** is an arithmetic shift (rounds toward minus infinity). It's only used where it says so.
- **clamp8(v)** = v limited to 0..255. **clamp16(v)** = v limited to −32768..32767.
- **low8(v)** = the low 8 bits of v (v & 0xFF), i.e. what C# `(byte)v` stores. Used where a value is stored without
  clamping.
- **Planes.** A picture has a luma plane **Y** (width W x height H samples) and two chroma planes **U** and **V**
  ((W/2) x (H/2)). Row 0 is the top.
- **Luma coordinates.** All positions in this spec are luma pixel coordinates (x right, y down), also for chroma. A
  plane is accessed through its **unit** u: 1 for Y, 2 for U and V. The sample "at (x, y)" of a plane is the one at row
  **y ÷ u**, column **x ÷ u**. Because ÷ rounds toward zero, on a chroma plane the coordinate −1 lands on row or column
  0. Any row or column outside the plane after this mapping is a decoding error (throw). Never clamp or wrap a
  coordinate.
- **Errors.** "Error" means: throw an exception (MphRead's `ProgramException` is preferred; the message is free) and
  stop decoding. A valid MPH file never hits one.

---

## Part 1. File format

### 1.1 Header (48 bytes, little-endian)

| Offset | Type | Field |
|---|---|---|
| 0 | 4 bytes | signature `VXDS` (not checked) |
| 4 | int32 | frame count |
| 8 | int32 | width W |
| 12 | int32 | height H |
| 16 | int32 | frame rate, 16.16 fixed point (`FrameRate` = value / 65536 as a `decimal`) |
| 20 | int32 | quantiser Q |
| 24 | int32 | audio sample rate |
| 28 | int32 | audio stream count |
| 32 | int32 | largest frame record size (unused) |
| 36 | int32 | offset of the audio tables (1.2) |
| 40 | int32 | offset of the seek table |
| 44 | int32 | seek table entry count |

Errors: stream count above 1; W or H not a multiple of 16; Q below 12 or above 161.

### 1.2 Audio tables (at the offset in the header; read even when the stream count is 0)

In order, little-endian:
1. Three **codebooks**, each 64 entries of 8 int16 values (book 0 first; within a book, entry 0 first; within an entry,
   value 0 first). 3,072 bytes.
2. Eight uint16 **scale factors**.
3. Eight int32 **base filter values**.
4. One int32 **initial scale**.

### 1.3 Seek table

Seek-table-count entries of (int32 frame number, int32 byte offset). Not used; it may be skipped.

### 1.4 Frame records

Frame records start at byte 48 and follow each other with no gaps, one per frame:

| Field | Meaning |
|---|---|
| uint16 LE | S = byte count of everything after this field (the next field plus the payload) |
| uint16 LE | A = number of audio blocks in this frame |
| S − 2 bytes | payload (always an even count) |

### 1.5 Reading the payload

The payload is a sequence of **16-bit little-endian words**. The bitstream takes the words in file order and reads
each from bit 15 down to bit 0. So the first payload byte holds the 8 bits read second, and the second byte the 8 bits
read first.

Codes:
- **u(n)**: n bits, the first one read is the most significant. u(0) = 0.
- **ue**: unsigned Exp-Golomb (H.264 §9.1): count the 0 bits before the next 1 bit (z of them; the 1 is consumed), read
  u(z) = b; the value is 2^z − 1 + b.
- **se**: signed Exp-Golomb (H.264 §9.1.1): k = ue; the value is (k + 1) ÷ 2 when k is odd and −(k ÷ 2) when k is even
  (0, 1, −1, 2, −2, ...).
- **align**: skip bits until the position (counted from the start of the payload) is a multiple of 16.

A payload holds: the video part (Part 3), **align**, then the A audio blocks back to back (Part 6). A valid file never
reads past the end of its payload; what happens if one does is not specified.

---

## Part 2. Decoder state

### 2.1 Picture buffers

Each decoder instance has two modes, chosen by the static flag `UseStaticBuffers` (Part 7):

- **Recycled mode** (flag on, the default; what the game uses). The instance owns **four picture sets**, each a Y plane
  of 256 x 192 and U and V planes of 128 x 96, created zero-filled with the instance and kept for its lifetime (also
  across movies). Frame n of a decode (n counted from 0 at every `Decode` call) uses set **n mod 4**. When a frame takes
  its set, its **U and V planes are filled with 0 and its Y plane is left as it is**: Y still holds the luma of
  whatever frame last used that set, which may be from an earlier movie decoded by the same instance. This matters:
  some predictions can read luma samples the current frame hasn't written yet (4.5), and they must see those old
  values.
- **Fresh mode** (flag off; only the export tools use it). Every frame gets new zero-filled planes of the file's W x H.

### 2.2 Reference pictures

R0, R1, R2 are the pictures of the three most recently decoded frames, R0 the newest. All three are absent at the start
of every `Decode` call. After a frame is decoded: R2 ← R1, R1 ← R0, R0 ← this frame. Using an absent reference is an
error.

### 2.3 Per-frame maps (all reset to zero at the start of every frame)

- **Luma count map**: one small integer per 4 x 4 luma block. The cell for luma position (x, y) is (x ÷ 4, y ÷ 4).
- **Chroma count map**: one per 8 x 8 luma area. The cell for (x, y) is (x ÷ 8, y ÷ 8).
  Because ÷ rounds toward zero, the position x = −1 gives cell column 0 and y = −1 gives cell row 0, so at the left or
  top frame edge a "neighbour" lookup lands on the block's **own** cell, with whatever that cell holds at that moment.
- **Vector map**: one vector (vx, vy) per macroblock (16 x 16), indexed by macroblock column and row. Lookups outside
  the frame (left of column 0, above row 0, right of the last column) give (0, 0).

### 2.4 Audio state (reset at the start of every `Decode` call)

- **E**: excitation history, 256 integers, all 0. E[0..127] holds the excitation of the block before last, E[128..255]
  the last block's.
- **L**: the last 8 output samples, all 0 (L[7] is the most recent).
- **F**: filter accumulator, 8 integers, all 0.
- **P**: the previous block's filter coefficients, 8 integers, all 0.
- The previous block's scale: none.
- Audio block counter: 0.

The **sample ring** (60 slots of 128 int16 samples) is zeroed by `Reset()` only (Part 7).

---

## Part 3. Video part of a frame

Macroblocks (16 x 16) are decoded in raster order: rows from the top, left to right within a row. For the macroblock
at column mx, row my (luma origin (16·mx, 16·my)):

1. **Predicted vector** p = (median of the three x components, median of the three y components) of the vector map
   cells left (mx − 1, my), above (mx, my − 1) and above-right (mx + 1, my − 1). Median = the middle of the three values
   after sorting.
2. Decode the **block** (16·mx, 16·my, 16, 16) with p (3.1).

After the last macroblock: **align**.

### 3.1 Block

A block is (X, Y, W, H) in luma coordinates, plus the macroblock's predicted vector p, which is handed down unchanged to
every block inside the macroblock. Read **c = ue**:

| c | Prediction | Residual |
|---|---|---|
| 0 | split vertically | no |
| 1 | copy from R0 at p | no |
| 2 | split horizontally | no |
| 3 | copy from R0 with explicit vector and offsets (4.2) | no |
| 4 | copy from R0 at p + delta | no |
| 5 | copy from R1 at p + delta | no |
| 6 | copy from R2 at p + delta | no |
| 7 | gradient fill with offsets (4.3) | no |
| 8 | split vertically | yes |
| 9 | copy from R1 at p | no |
| 10 | copy from R0 with explicit vector and offsets | yes |
| 11 | whole-block intra (4.4) | no |
| 12 | copy from R0 at p | yes |
| 13 | split horizontally | yes |
| 14 | copy from R2 at p | no |
| 15 | 4 x 4 intra (4.5) | no |
| 16 | copy from R0 at p + delta | yes |
| 17 | copy from R1 at p + delta | yes |
| 18 | copy from R2 at p + delta | yes |
| 19 | 4 x 4 intra | yes |
| 20 | copy from R1 at p | yes |
| 21 | copy from R2 at p | yes |
| 22 | whole-block intra | yes |
| 23 | gradient fill with offsets | yes |
| 24 or more | error | |

- **Split vertically**: error if W = 2. Decode (X, Y, W/2, H), then (X + W/2, Y, W/2, H), both with p.
- **Split horizontally**: error if H = 2. Decode (X, Y, W, H/2), then (X, Y + H/2, W, H/2), both with p.
- **Copy** codes: 4.1.

Then, after the prediction (for splits: after both halves):
- codes with a residual: decode the residual of the block (5.1);
- codes without one: **clear the counts** of the block (5.4) when
  - c = 0: W = 8 and H ≥ 8;
  - c = 2: W ≥ 8 and H = 8;
  - any other code: W ≥ 8 and H ≥ 8;

  otherwise leave the count maps alone.

Blocks can be any size from 16 down to 2 on each side, independently.

---

## Part 4. Prediction

"Write v at (x, y)" means store v in the plane's sample at (x, y) (Part 0 mapping). "The positions" of a block on a
plane of unit u are the luma positions (X + i·u, Y + j·u) for 0 ≤ i·u < W, 0 ≤ j·u < H: one per sample of that plane.
Each prediction writes every one of its block's positions on the plane(s) it covers.

### 4.1 Copy (codes 1, 4, 5, 6, 9, 12, 14, 16-18, 20, 21)

1. Vector v = p for the "at p" codes. For the "p + delta" codes: dx = se, then dy = se; v = (p.x + dx, p.y + dy).
2. Store v in the vector map cell of the macroblock that contains (X, Y). Later stores in the same macroblock in the
   same frame overwrite it.
3. With reference Rk as listed: at every Y position (x, y): write Rk.Y's sample at (x + v.x, y + v.y). At every U
   position (x, y) (unit 2): write Rk.U's sample at luma coordinate (x + v.x, y + v.y), which is row (y + v.y) ÷ 2,
   column (x + v.x) ÷ 2. The same for V. No bounds check beyond Part 0's (out-of-plane = error).

### 4.2 Copy with offsets (codes 3, 10)

1. vx = se, vy = se. Error unless X + vx ≥ 0, X + vx + W ≤ width, Y + vy ≥ 0 and Y + vy + H ≤ height.
2. dY = se; error unless −65536 ≤ dY < 65536; dY = 2·dY. Then the same for dU, then for dV (read, check, double).
3. Using R0: at every Y position (x, y) write clamp8(R0.Y sample at (x + vx, y + vy) + dY). At every U position write
   clamp8(R0.U sample at luma (x + vx, y + vy) + dU); the same for V with dV.
4. The vector map is not touched.

### 4.3 Gradient fill

**Gradient(plane, u, X, Y, W, H, o)**, all reads and writes through the unit-u mapping:
1. a = sample at (X − 1, Y + H − 1); b = sample at (X + W − 1, Y − 1). Write low8((a + b + 1) ÷ 2 + o) at
   (X + W − 1, Y + H − 1).
2. **Fill(X, Y, W, H)**, defined recursively:
   - W = u and H = u: nothing.
   - W = u, H > u: t = sample at (X, Y − 1); d = sample at (X, Y + H − 1). Write (t + d) ÷ 2 at (X, Y + H/2 − 1).
     Then Fill(X, Y, W, H/2) and Fill(X, Y + H/2, W, H/2).
   - H = u, W > u: l = sample at (X − 1, Y); r = sample at (X + W − 1, Y). Write (l + r) ÷ 2 at (X + W/2 − 1, Y). Then
     Fill(X, Y, W/2, H) and Fill(X + W/2, Y, W/2, H).
   - Otherwise, in exactly this order:
     1. read BL = (X − 1, Y + H − 1), TR = (X + W − 1, Y − 1), BR = (X + W − 1, Y + H − 1);
     2. bm = (BL + BR) ÷ 2, write it at (X + W/2 − 1, Y + H − 1);
     3. rm = (TR + BR) ÷ 2, write it at (X + W − 1, Y + H/2 − 1);
     4. centre: if exactly one of "W is 4u or 16u" and "H is 4u or 16u" is true, c = (sample at (X − 1, Y + H/2 − 1)
        + rm) ÷ 2; otherwise c = (sample at (X + W/2 − 1, Y − 1) + bm) ÷ 2. Write c at (X + W/2 − 1, Y + H/2 − 1);
     5. Fill the four quarters, each W/2 x H/2: top-left, top-right, bottom-left, bottom-right.

   W/2 and H/2 are halves in luma units (on chroma, a block 2 luma wide is one sample wide, so u = 2 means "one
   sample"). The read/write order matters at the frame's left and top edges on chroma, where coordinate −1 lands inside
   the block (Part 0).

**Codes 7 and 23**: for Y (u = 1), then U (u = 2), then V (u = 2): o = se; error unless −65536 ≤ o < 65536;
Gradient(plane, u, X, Y, W, H, 2·o). (Read the plane's offset, fill that plane, then the next.)

### 4.4 Whole-block intra (codes 11, 22)

Read m = ue and predict Y (u = 1): 0 Vertical, 1 Horizontal, 2 Mean, 3 Gradient(Y, 1, X, Y, W, H, 0); 4 or more: error.
Then the chroma mode (4.6).

On a plane of unit u, over the block's positions:
- **Vertical**: at each position (x, y) write the sample at (x, Y − 1).
- **Horizontal**: at each position (x, y) write the sample at (X − 1, y).
- **Mean**: let sT = W/2 + the sum of the samples at (X + i, Y − 1) for every i from 0 to W − 1, and sL = H/2 + the sum of
  the samples at (X − 1, Y + j) for every j from 0 to H − 1. The sums step through **every luma coordinate**, so on a
  chroma plane each chroma sample is counted twice. Then
  - X ≠ 0 and Y ≠ 0: m = ((sT ÷ W) + (sL ÷ H) + 1) ÷ 2;
  - X = 0, Y ≠ 0: m = sT ÷ W;
  - X ≠ 0, Y = 0: m = sL ÷ H;
  - X = 0, Y = 0: m = 128.

  Write m at every position.

### 4.5 4 x 4 intra (codes 15, 19)

The block's luma is cut into (W ÷ 4) x (H ÷ 4) sub-blocks of 4 x 4 (none at all when W or H is 2). They are predicted
one at a time in raster order within the block (top row first, left to right), each written to Y before the next is
predicted. The residual (if any) is added only after the whole block, chroma included, is predicted. For the sub-block
in column i, row j of the block, with luma origin (x0, y0) = (X + 4i, Y + 4j):

1. **Predicted mode**: a = the mode chosen for sub-block (i, j − 1), b = the mode chosen for (i − 1, j). Only
   sub-blocks of this block count: when j = 0 or i = 0 that neighbour is "none", even if another block lies there. Both
   none: 2. One none: the other one. Otherwise min(a, b).
2. Flag = u(1). Flag 1: mode = predicted. Flag 0: r = u(3); mode = r if r < predicted, otherwise r + 1.
3. Predict the sub-block with H.264's Intra_4x4 prediction for that mode (ITU-T H.264 §8.3.1.2.1 to §8.3.1.2.9,
   restated below), with these differences:
   - The neighbour samples are always read straight from the current Y plane, at the moment of prediction:
     p[x, −1] = Y at (x0 + x, y0 − 1) for x = −1..7 and p[−1, y] = Y at (x0 − 1, y0 + y) for y = 0..3. There is **no**
     availability rule and **no** substitution: p[4..7, −1] are read even when they lie in a sub-block or block not
     yet decoded in this frame (they then hold whatever the plane holds: earlier predictions, or stale luma, 2.1).
     A neighbour outside the plane is an error. Each mode reads exactly the samples its equations use.
   - DC availability is decided by the sub-block's position in the **frame** only (x0 ≠ 0, y0 ≠ 0).
4. Write the 16 samples into Y.

Equations, for x, y = 0..3 (pred[x, y] goes to Y at (x0 + x, y0 + y)); ">>" is the arithmetic shift, the operands are
never negative here:

- **0 Vertical**: p[x, −1].
- **1 Horizontal**: p[−1, y].
- **2 DC**: x0 ≠ 0 and y0 ≠ 0: (Σ_{k=0..3} p[k, −1] + Σ_{k=0..3} p[−1, k] + 4) >> 3. Only x0 ≠ 0: (Σ p[−1, k] + 2) >> 2.
  Only y0 ≠ 0: (Σ p[k, −1] + 2) >> 2. Neither: 128.
- **3 Diagonal down-left**: x = y = 3: (p[6, −1] + 3·p[7, −1] + 2) >> 2. Otherwise
  (p[x+y, −1] + 2·p[x+y+1, −1] + p[x+y+2, −1] + 2) >> 2.
- **4 Diagonal down-right**: x > y: (p[x−y−2, −1] + 2·p[x−y−1, −1] + p[x−y, −1] + 2) >> 2. x < y:
  (p[−1, y−x−2] + 2·p[−1, y−x−1] + p[−1, y−x] + 2) >> 2. x = y: (p[0, −1] + 2·p[−1, −1] + p[−1, 0] + 2) >> 2.
- **5 Vertical-right**, z = 2x − y, s = x − (y >> 1):
  z = 0, 2, 4, 6: (p[s−1, −1] + p[s, −1] + 1) >> 1;
  z = 1, 3, 5: (p[s−2, −1] + 2·p[s−1, −1] + p[s, −1] + 2) >> 2;
  z = −1: (p[−1, 0] + 2·p[−1, −1] + p[0, −1] + 2) >> 2;
  z = −2, −3: (p[−1, y−1] + 2·p[−1, y−2] + p[−1, y−3] + 2) >> 2.
- **6 Horizontal-down**, z = 2y − x, s = y − (x >> 1):
  z = 0, 2, 4, 6: (p[−1, s−1] + p[−1, s] + 1) >> 1;
  z = 1, 3, 5: (p[−1, s−2] + 2·p[−1, s−1] + p[−1, s] + 2) >> 2;
  z = −1: (p[−1, 0] + 2·p[−1, −1] + p[0, −1] + 2) >> 2;
  z = −2, −3: (p[x−1, −1] + 2·p[x−2, −1] + p[x−3, −1] + 2) >> 2.
- **7 Vertical-left**, s = x + (y >> 1): y = 0, 2: (p[s, −1] + p[s+1, −1] + 1) >> 1; y = 1, 3:
  (p[s, −1] + 2·p[s+1, −1] + p[s+2, −1] + 2) >> 2.
- **8 Horizontal-up**, z = x + 2y, s = y + (x >> 1): z = 0, 2, 4: (p[−1, s] + p[−1, s+1] + 1) >> 1; z = 1, 3:
  (p[−1, s] + 2·p[−1, s+1] + p[−1, s+2] + 2) >> 2; z = 5: (p[−1, 2] + 3·p[−1, 3] + 2) >> 2; z > 5: p[−1, 3].

After the luma sub-blocks: the chroma mode (4.6).

### 4.6 Chroma mode (after the luma of codes 11, 15, 19, 22)

m = ue, applied to U and then to V (u = 2, the block in luma coordinates): 0 Mean, 1 Horizontal, 2 Vertical,
3 Gradient(plane, 2, X, Y, W, H, 0); 4 or more: error. (Note the order differs from the luma list in 4.4.)

---

## Part 5. Residual

### 5.1 Areas

For a block (X, Y, W, H) with a residual: for each 8 x 8 area with origin (ax, ay) = (X + 8i, Y + 8j), with j over
0 ≤ 8j < H in the outer loop and i over 0 ≤ 8i < W in the inner loop (so a block narrower or shorter than 8 still gets
one area at its origin, reaching 8 luma samples past it):

1. k = ue; error if k > 31. The **coded flags** are row k of this table (bit 0 = 1, ..., bit 4 = 16):

   | k | flags | k | flags | k | flags | k | flags |
   |---|---|---|---|---|---|---|---|
   | 0 | 0 | 8 | 5 | 16 | 9 | 24 | 24 |
   | 1 | 8 | 9 | 12 | 17 | 6 | 25 | 18 |
   | 2 | 4 | 10 | 3 | 18 | 30 | 26 | 17 |
   | 3 | 2 | 11 | 16 | 19 | 27 | 27 | 28 |
   | 4 | 1 | 12 | 14 | 20 | 26 | 28 | 20 |
   | 5 | 31 | 13 | 13 | 21 | 29 | 29 | 19 |
   | 6 | 15 | 14 | 11 | 22 | 23 | 30 | 22 |
   | 7 | 10 | 15 | 7 | 23 | 21 | 31 | 25 |

2. In this order: the luma 4 x 4 blocks at (ax, ay) [bit 0], (ax + 4, ay) [bit 1], (ax, ay + 4) [bit 2],
   (ax + 4, ay + 4) [bit 3], then the chroma pair [bit 4].

   - **Luma block at (x, y)**, flag set: nC = (luma count at (x − 1, y) + luma count at (x, y − 1) + 1) ÷ 2, using the
     map's current values and the cell rule of 2.3. t = Residual4x4(nC, Y, u = 1, x, y) (5.2). Set the luma count at
     (x, y) to t. Flag clear: set the luma count at (x, y) to 0.
   - **Chroma pair**, flag set: nC = (chroma count at (ax − 1, ay) + chroma count at (ax, ay − 1) + 1) ÷ 2.
     tU = Residual4x4(nC, U, u = 2, ax, ay); then tV = Residual4x4(nC, V, u = 2, ax, ay) with the same nC. Set the
     chroma count at (ax, ay) to (tU + tV + 1) ÷ 2. Flag clear: set the chroma count at (ax, ay) to 0.

   Each count is set right after its block, so the next block's nC already sees it.

### 5.2 Residual4x4(nC, plane, u, x, y)

Returns TotalCoeff; adds a 4 x 4 residual to the samples at (x + u·col, y + u·row), row and col 0..3.

1. **coeff_token**: decode with the code table for nC's range: 0-1, 2-3, 4-7, 8 or more (Appendix A.1; these are
   H.264 Table 9-5's four non-chroma-DC columns). This gives TotalCoeff T (0..16) and TrailingOnes T1 (0..3). If T = 0,
   return 0; the plane is untouched.
2. **total_zeros**: if T < 16, Z = decode with the table for T (Appendix A.2; H.264 Tables 9-7 and 9-8, 4 x 4 blocks).
   If T = 16, Z = 0.
3. **Levels**, T of them, in decoding order (the highest scan position first). Keep zerosLeft = Z and a suffix length
   len = 0 (fresh for every call). For each level n = 1..T:
   - n ≤ T1: one bit; level = +1 if it's 0, −1 if it's 1.
   - Otherwise (this is VX's own level code, **not** H.264's):
     - prefix = the number of 0 bits before the next 1 bit (the 1 is consumed);
     - suffix = u(11) if prefix = 15, otherwise u(len);
     - M = (prefix << len) + suffix + 1;
     - if len < 5 and M is greater than the limit for len, len = len + 1. Limits: 3 (len 0), 6 (len 1), 12 (len 2),
       24 (len 3), 48 (len 4). len never goes above 5;
     - then one sign bit: level = −M if it's 1, otherwise M.
   - After each level except the last: if zerosLeft > 0, run = decode run_before with the table for zerosLeft
     (Appendix A.3; the "> 6" table when zerosLeft is 7 or more); zerosLeft = zerosLeft − run. If zerosLeft = 0, read
     nothing.
4. **Placement**, as in H.264 §9.2.4: the first decoded level goes to scan position T + Z − 1; below it come its
   run_before zeros, then the second level, and so on; after the last level, the remaining zerosLeft positions down to 0
   are zero, and positions T + Z .. 15 are zero.
5. **Scan position → (row, col)** (H.264's 4 x 4 frame zig-zag): 0 (0,0), 1 (0,1), 2 (1,0), 3 (2,0), 4 (1,1), 5 (0,2),
   6 (0,3), 7 (1,2), 8 (2,1), 9 (3,0), 10 (3,1), 11 (2,2), 12 (1,3), 13 (2,3), 14 (3,2), 15 (3,3).
6. **Scaling**: d[row][col] = level × w(row, col), with w = vm << (Q ÷ 6), m = Q mod 6, Q = the header's quantiser, and
   vm the H.264 normalisation table (§8.5.9), picked by the position:

   | m | row and col both even | both odd | otherwise |
   |---|---|---|---|
   | 0 | 10 | 16 | 13 |
   | 1 | 11 | 18 | 14 |
   | 2 | 13 | 20 | 16 |
   | 3 | 14 | 23 | 18 |
   | 4 | 16 | 25 | 20 |
   | 5 | 18 | 29 | 23 |

   (This equals H.264's 4 x 4 dequantisation with flat scaling lists and qP = Q, carried on past qP 51; there is no
   separate DC path.)
7. **Inverse transform**: H.264 §8.5.12.2, rows first, then columns, **except that the two halvings round toward zero
   (÷ 2), not with >> 1**:
   - each row r: e0 = d[r][0] + d[r][2]; e1 = d[r][0] − d[r][2]; e2 = (d[r][1] ÷ 2) − d[r][3];
     e3 = d[r][1] + (d[r][3] ÷ 2); f[r][0] = e0 + e3; f[r][1] = e1 + e2; f[r][2] = e1 − e2; f[r][3] = e0 − e3;
   - each column c: g0 = f[0][c] + f[2][c]; g1 = f[0][c] − f[2][c]; g2 = (f[1][c] ÷ 2) − f[3][c];
     g3 = f[1][c] + (f[3][c] ÷ 2); h[0][c] = g0 + g3; h[1][c] = g1 + g2; h[2][c] = g1 − g2; h[3][c] = g0 − g3;
   - r[row][col] = (h[row][col] + 32) >> 6.
8. **Add**: for each row, col: the sample at (x + u·col, y + u·row) = clamp8(sample + r[row][col]).
9. Return T.

### 5.3 Code tables and decoding

A table maps bit strings to symbols. Decoding reads bits one at a time until the bits read so far equal one of the
table's strings (the tables are prefix-free). A table-driven or tree decoder is expected; anything that gives the same
symbol for the same bits is fine.

### 5.4 Clear the counts

For each 8 x 8 area of the block (the same iteration as 5.1, but nothing is read), set the luma counts at (ax, ay),
(ax, ay + 4), (ax + 4, ay), (ax + 4, ay + 4) and the chroma count at (ax, ay) to 0.

---

## Part 6. Audio blocks

Each block reads whole 16-bit words (the bitstream is word-aligned by then): two header words **h1**, **h2**, then N
pulse words.

1. **Header** (bit 0 = least significant):
   - i0 = h1 bits 0-5 (codebook 0 entry); sIdx = h1 bits 6-8 (scale factor index); **back** = h1 bits 9-15;
   - i2 = h2 bits 0-5 (codebook 2 entry); i1 = h2 bits 6-11 (codebook 1 entry); **pack** = h2 bits 12-13;
     **start** = h2 bits 14-15.
2. **Pulse words**: N = 8, 5, 4, 3 for pack 0, 1, 2, 3. Read N words w[0..N−1].
3. **Pulse values** q[], in this order:
   - pack 0: for each word w[0..7] in turn, five 3-bit fields taken from bits 15-13, 12-10, 9-7, 6-4, 3-1, each
     giving 2·f − 7 (40 values). Then two more: f = 4·(bit 0 of w[0]) + 2·(bit 0 of w[1]) + (bit 0 of w[2]), and
     f = 4·(bit 0 of w[3]) + 2·(bit 0 of w[4]) + (bit 0 of w[5]), each 2·f − 7. 42 values. (Bit 0 of w[6] and w[7] is
     unused.)
   - pack 1, 2, 3: for each word in turn, eight 2-bit fields from bits 15-14 down to 1-0, each giving 2·f − 3.
     N·8 values (40, 32, 24).
4. **Scale**: base = the initial scale if back = 127, otherwise the previous audio block's scale (the previous block of
   this movie, also across frames; none is an error). scale = (base × scale factor[sIdx]) ÷ 8192. Remember it for the
   next block.
5. **Spacing** = 3, 3, 4, 5 for pack 0, 1, 2, 3.
6. **Excitation** x[0..127]:
   - if back ≤ 125: x[i] = (E[i + 127 − back] × g(i)) ÷ 16 with g(i) = min(8, i + 1, 128 − i);
   - if back is 126 or 127: x[i] = 0.

   Then add the pulses: for k = 0 .. count − 1, x[start + k·spacing] += q[k] × scale. (Every pulse lands inside the
   block.)
7. **Filter accumulator**: if back = 127, F = the base filter values. Then for each i = 0..7:
   F[i] += codebook0[i0][i] + codebook1[i1][i] + codebook2[i2][i].
8. **Coefficients** a[0..7]: start with all 0. For i = 0..7, in order: take a copy c of a; for each j = 0..i−1:
   a[j] += (c[i − 1 − j] × F[i]) ÷ 32768; then a[i] = F[i]. Afterwards, for each i: a[i] = a[i] ÷ (−2).
9. **Four coefficient sets** S0..S3 (8 values each):
   - back = 127: all four equal a.
   - otherwise, element by element: S3 = a; S1 = (P + S3) ÷ 2; S0 = (P + S1) ÷ 2; S2 = (S1 + S3) ÷ 2.
10. **Synthesis**, output y[0..127] in order: for sample i, with S = S_(i ÷ 32):
    acc = x[i] × 16384 + Σ_{j=0..7} y[i − 1 − j] × S[j], where y[n] for n < 0 means L[n + 8] (the previous block's
    last samples, y[−1] = L[7]). y[i] = clamp16(acc ÷ 16384). The sum wraps in 32 bits like everything else (Part 0).
11. **State update**: E[0..127] ← E[128..255], then E[128..255] ← x (the excitation after the pulses). L ← y[120..127].
    P ← a (the halved values from step 8).
12. **Output**: the block is the audio counter's block number b (from 0 at the start of the movie, counting all blocks
    of all frames); its 128 samples go into ring slot b mod 60. Then the counter goes up by one.

---

## Part 7. Colour, API and threading

### 7.1 Colour conversion

For each pixel (x, y): Yv = the Y sample, u' = the U sample at (x, y) − 128, v' = the V sample at (x, y) − 128 (Part 0
mapping, so row y ÷ 2, column x ÷ 2). R = clamp8(Yv + 2·v'); G = clamp8(Yv − (u' ÷ 2) − v'); B = clamp8(Yv + 2·u').

### 7.2 The public class

`public class VxDecoder` in namespace `MphRead.Formats`. Callers: `src/MphRead/Formats/Movie.cs` (lines 1-449,
desktop playback), `src/MphRecomp.Core/Media/MoviePlayer.cs` (Android and the tools), `src/MphRead.Tools/MovieTest.cs`,
`src/MphRead.Tools/Program.cs`. Required members (more are fine):

| Member | Behaviour |
|---|---|
| `static VxDecoder Instance1 { get; }`, `Instance2` | two long-lived instances (top and bottom screen) |
| `static bool UseStaticBuffers { get; set; }` | the mode switch of 2.1, shared by all instances; starts true |
| `static int SampleBufferCount` (get) | 60 |
| `void Reset()` | forget this instance's decoded frames (GetImage returns false until new frames are decoded), FramesQueued = 0, UseStaticBuffers = true, zero this instance's sample ring. Leaves FrameCount, FrameRate, AudioSampleRate, AudioFrameTotal and the picture sets as they are |
| `Task Decode(string filePath, bool writeFiles = false, CancellationToken token = default)` | opens the file for reading and decodes it with the file name = `Path.GetFileName(filePath)` |
| `Task Decode(byte[] data, string filename, bool writeFiles = false, CancellationToken token = default)` | decodes from memory |
| `Task Decode(Stream stream, string filename, bool writeFiles = false, CancellationToken token = default)` | the main decode loop (below); the stream is closed when it ends |
| `int FramesQueued` (get) | frames decoded but not yet taken by GetImage; safe to read from any thread |
| `int FrameCount` (get) | from the header, set when the header is read |
| `decimal FrameRate` (get) | from the header |
| `int AudioSampleRate` (get) | from the header |
| `int FrameWidth`, `int FrameHeight` (get) | from the header |
| `bool GetImage(int frameIndex, byte[] texture)` | see below |
| `int AudioFrameTotal` (get) | audio blocks decoded so far in the current decode; set to 0 when a header is read, not by Reset |
| `ReadOnlySpan<short> GetAudioBuffer(int index)` | the 128 samples of ring slot index mod 60 |
| `Task Export(string filePath)`, `Task ExportAll()` | the export tools (7.4) |

### 7.3 Decode loop

`Decode(stream, filename, writeFiles, token)`:
1. If writeFiles: create the folder `Paths.Combine(Paths.Export, <filename without extension>)`.
2. Set FramesQueued to 0; the next picture set is set 0 (2.1); the ring position goes back to slot 0.
3. Read the header (1.1) and set FrameCount, FrameRate, AudioSampleRate, FrameWidth, FrameHeight; read the audio
   tables (1.2), optionally the seek table; check the errors of 1.1. Reset the references (2.2) and the audio state
   (2.4); AudioFrameTotal = 0; start a new empty list of decoded frames.
4. If writeFiles and the stream count is at least 1: open `audio.wav` in the folder and write 44 zero bytes.
5. For each frame:
   1. If not writeFiles: while FramesQueued ≥ 4 and the token isn't cancelled, wait asynchronously (poll about every
      millisecond). Then, in both modes: if the token is cancelled, return normally (no exception, no further output).
   2. Read the record (1.4), take the frame's picture (2.1), reset the maps (2.3), decode the video (Part 3), then the
      A audio blocks (Part 6).
   3. AudioFrameTotal += A. Then add the frame to the decoded list. Then increment FramesQueued **atomically** (GetImage
      decrements it from another thread; a lost update would stall both sides). Publish in this order so a reader that
      sees the new FramesQueued also sees the frame and its audio.
   4. Shift the references (2.2).
   5. If writeFiles and recycled mode: save this frame's PNG now (7.4). If writeFiles and A > 0: append the A blocks'
      samples to audio.wav, each as 16-bit little-endian, block by block.
6. If writeFiles and AudioFrameTotal > 0: go back to the start of audio.wav and write its header with
   `SoundRead.WriteWavHeader(writer, (uint)(AudioFrameTotal * 128), (ushort)AudioSampleRate, WaveFormat.PCM16)`
   (`MphRead.Formats.Sound`).
7. If writeFiles and fresh mode: save every frame's PNG now, in order.

Recycled mode holds four picture sets and frame n uses set n mod 4. That is safe because of the 4-frame wait in 5.1:
the set of frame n is reused by frame n + 4, which is only decoded after the consumer has taken frame n.

**`GetImage(frameIndex, texture)`** (called from the display thread while decoding continues): if no frame has been
decoded since the last Reset or the current Decode's header, or frameIndex ≥ the number of frames decoded so far,
return false and change nothing. Otherwise convert that frame's picture (7.1) into `texture` as RGB24: pixel (x, y) at
byte offset **(y · 256 + x) · 3** (R, G, B) — the row stride is always 256 pixels, whatever the movie's width — then
atomically decrement FramesQueued and return true. Each successful call decrements once. (In recycled mode a frame's
entry shows whatever its picture set holds now; callers take frames in order within the 4-frame window, so that's
always the frame itself.)

**`GetAudioBuffer(index)`**: the samples of ring slot index mod 60 (an index of a block 60 or more behind the newest
one shows a newer block; the callers keep up).

### 7.4 Export tools

- **Files**: in `Paths.Combine(Paths.Export, <movie name without extension>)`: one PNG per frame named by its index,
  4 digits with leading zeros (`0000.png`, `0001.png`, ...), an `SixLabors.ImageSharp` `Image<Rgb24>` of the movie's
  W x H with the colours of 7.1, saved with `SaveAsPng`; and `audio.wav` (7.3). `File.OpenWrite` for the WAV is fine.
- **`Export(filePath)`**: Reset(); the path is `Paths.Combine(Paths.FileSystem, "movies", filePath)` if that file
  exists, else `Paths.Combine(Paths.FileSystem, filePath)` if that exists, else filePath as given; write the line
  "Exporting..."; UseStaticBuffers = false; Decode(path, writeFiles: true); write "Done.".
- **`ExportAll()`**: Reset(); UseStaticBuffers = false; list the files of `Paths.Combine(Paths.FileSystem, "movies")`;
  for each whose extension is exactly ".vx", write "Exporting {n} of {number of files listed}: {file name}" and
  Decode(path, writeFiles: true); then write "Done.". (Console output.)

### 7.5 Speed

The game decodes both screens live on an Android handheld at 15 fps alongside the game. The new decoder must not be
slower than the one it replaces (the proof run measures both). No allocation per sample or per block in the hot loops;
decode codes with lookup tables or a tree, not by building strings or hashing bits.

---

## Part 8. What to write

All new files. Don't edit existing files; the integrator wires them in and removes the old decoder.

- Put the code in a new folder `src/MphRead/Formats/Vx/`, as many files as you like. `VxDecoder` goes in namespace
  `MphRead.Formats`; everything else should be `internal` and in namespace `MphRead.Formats.Vx`, with your own names.
- C# at the project's default language version, nullable enabled, 4-space indent, `//` comments in plain English, matching the surrounding code. The project
  compiles unchecked (the default); keep it that way and don't add `checked`.
- MphRead helpers you may call: `Paths` (`src/MphRead/Formats/Formats.cs`), `SoundRead.WriteWavHeader` and
  `WaveFormat` (`src/MphRead/Formats/Sound.cs`), `ProgramException` (`src/MphRead/AppInfo.cs`), and SixLabors.ImageSharp.

### What you may read

This spec; the ITU-T H.264 standard (public, from itu.int); general C# / .NET documentation; and in the staging tree
you're given: the playback part of `src/MphRead/Formats/Movie.cs` (what's left of it), `src/MphRecomp.Core/Media/MoviePlayer.cs`,
`src/MphRead.Tools/MovieTest.cs`, `src/MphRead.Tools/Program.cs`, the three helper files above, and any `.csproj`.

**Don't open:** any earlier version of the VX decoder, the project's git history, any other VX / Mobiclip / Actimagine
decoder or encoder in any language (for example actimagine, Gericom's ffmpeg patch or MobiclipDecoder, or ffmpeg's own
H.264 decoder sources), upstream MphRead online, or anything under the spec author's `handoffs/` folder or scratchpad.
If you think you need one of them, stop and say what you need instead.

### Done means

- `dotnet build src/MphRead/MphRead.csproj -c Release` and `dotnet build src/MphRead.Tools/MphRead.Tools.csproj -c Release`
  succeed in the staging tree with your files.
- If the staging tree has the game's movie files, `MphRead.Tools.exe -movietest` passes for every movie (all frames
  shown in order, the audio block count matches, no errors), and a contact sheet of one movie
  (`-movietest 01 sheet`) looks like a normal video.
- A short report: your files, the order in which a frame is decoded, and every place this spec could be read two ways
  (say which reading you chose).

---

## Appendix A. Code tables

Bit strings are written first bit first. They are H.264's (ITU-T H.264 Tables 9-5, 9-7, 9-8, 9-10).

### A.1 coeff_token (TotalCoeff, TrailingOnes) by nC range

| T | T1 | 0 ≤ nC < 2 | 2 ≤ nC < 4 | 4 ≤ nC < 8 | 8 ≤ nC |
|---|---|---|---|---|---|
| 0 | 0 | 1 | 11 | 1111 | 000011 |
| 1 | 0 | 000101 | 001011 | 001111 | 000000 |
| 1 | 1 | 01 | 10 | 1110 | 000001 |
| 2 | 0 | 00000111 | 000111 | 001011 | 000100 |
| 2 | 1 | 000100 | 00111 | 01111 | 000101 |
| 2 | 2 | 001 | 011 | 1101 | 000110 |
| 3 | 0 | 000000111 | 0000111 | 001000 | 001000 |
| 3 | 1 | 00000110 | 001010 | 01100 | 001001 |
| 3 | 2 | 0000101 | 001001 | 01110 | 001010 |
| 3 | 3 | 00011 | 0101 | 1100 | 001011 |
| 4 | 0 | 0000000111 | 00000111 | 0001111 | 001100 |
| 4 | 1 | 000000110 | 000110 | 01010 | 001101 |
| 4 | 2 | 00000101 | 000101 | 01011 | 001110 |
| 4 | 3 | 000011 | 0100 | 1011 | 001111 |
| 5 | 0 | 00000000111 | 00000100 | 0001011 | 010000 |
| 5 | 1 | 0000000110 | 0000110 | 01000 | 010001 |
| 5 | 2 | 000000101 | 0000101 | 01001 | 010010 |
| 5 | 3 | 0000100 | 00110 | 1010 | 010011 |
| 6 | 0 | 0000000001111 | 000000111 | 0001001 | 010100 |
| 6 | 1 | 00000000110 | 00000110 | 001110 | 010101 |
| 6 | 2 | 0000000101 | 00000101 | 001101 | 010110 |
| 6 | 3 | 00000100 | 001000 | 1001 | 010111 |
| 7 | 0 | 0000000001011 | 00000001111 | 0001000 | 011000 |
| 7 | 1 | 0000000001110 | 000000110 | 001010 | 011001 |
| 7 | 2 | 00000000101 | 000000101 | 001001 | 011010 |
| 7 | 3 | 000000100 | 000100 | 1000 | 011011 |
| 8 | 0 | 0000000001000 | 00000001011 | 00001111 | 011100 |
| 8 | 1 | 0000000001010 | 00000001110 | 0001110 | 011101 |
| 8 | 2 | 0000000001101 | 00000001101 | 0001101 | 011110 |
| 8 | 3 | 0000000100 | 0000100 | 01101 | 011111 |
| 9 | 0 | 00000000001111 | 000000001111 | 00001011 | 100000 |
| 9 | 1 | 00000000001110 | 00000001010 | 00001110 | 100001 |
| 9 | 2 | 0000000001001 | 00000001001 | 0001010 | 100010 |
| 9 | 3 | 00000000100 | 000000100 | 001100 | 100011 |
| 10 | 0 | 00000000001011 | 000000001011 | 000001111 | 100100 |
| 10 | 1 | 00000000001010 | 000000001110 | 00001010 | 100101 |
| 10 | 2 | 00000000001101 | 000000001101 | 00001101 | 100110 |
| 10 | 3 | 0000000001100 | 00000001100 | 0001100 | 100111 |
| 11 | 0 | 000000000001111 | 000000001000 | 000001011 | 101000 |
| 11 | 1 | 000000000001110 | 000000001010 | 000001110 | 101001 |
| 11 | 2 | 00000000001001 | 000000001001 | 00001001 | 101010 |
| 11 | 3 | 00000000001100 | 00000001000 | 00001100 | 101011 |
| 12 | 0 | 000000000001011 | 0000000001111 | 000001000 | 101100 |
| 12 | 1 | 000000000001010 | 0000000001110 | 000001010 | 101101 |
| 12 | 2 | 000000000001101 | 0000000001101 | 000001101 | 101110 |
| 12 | 3 | 00000000001000 | 000000001100 | 00001000 | 101111 |
| 13 | 0 | 0000000000001111 | 0000000001011 | 0000001101 | 110000 |
| 13 | 1 | 000000000000001 | 0000000001010 | 000000111 | 110001 |
| 13 | 2 | 000000000001001 | 0000000001001 | 000001001 | 110010 |
| 13 | 3 | 000000000001100 | 0000000001100 | 000001100 | 110011 |
| 14 | 0 | 0000000000001011 | 0000000000111 | 0000001001 | 110100 |
| 14 | 1 | 0000000000001110 | 00000000001011 | 0000001100 | 110101 |
| 14 | 2 | 0000000000001101 | 0000000000110 | 0000001011 | 110110 |
| 14 | 3 | 000000000001000 | 0000000001000 | 0000001010 | 110111 |
| 15 | 0 | 0000000000000111 | 00000000001001 | 0000000101 | 111000 |
| 15 | 1 | 0000000000001010 | 00000000001000 | 0000001000 | 111001 |
| 15 | 2 | 0000000000001001 | 00000000001010 | 0000000111 | 111010 |
| 15 | 3 | 0000000000001100 | 0000000000001 | 0000000110 | 111011 |
| 16 | 0 | 0000000000000100 | 00000000000111 | 0000000001 | 111100 |
| 16 | 1 | 0000000000000110 | 00000000000110 | 0000000100 | 111101 |
| 16 | 2 | 0000000000000101 | 00000000000101 | 0000000011 | 111110 |
| 16 | 3 | 0000000000001000 | 00000000000100 | 0000000010 | 111111 |

(T1 is at most min(T, 3); the rows listed are all the symbols.)

### A.2 total_zeros, by TotalCoeff (value: code)

| T | codes |
|---|---|
| 1 | 0: 1, 1: 011, 2: 010, 3: 0011, 4: 0010, 5: 00011, 6: 00010, 7: 000011, 8: 000010, 9: 0000011, 10: 0000010, 11: 00000011, 12: 00000010, 13: 000000011, 14: 000000010, 15: 000000001 |
| 2 | 0: 111, 1: 110, 2: 101, 3: 100, 4: 011, 5: 0101, 6: 0100, 7: 0011, 8: 0010, 9: 00011, 10: 00010, 11: 000011, 12: 000010, 13: 000001, 14: 000000 |
| 3 | 0: 0101, 1: 111, 2: 110, 3: 101, 4: 0100, 5: 0011, 6: 100, 7: 011, 8: 0010, 9: 00011, 10: 00010, 11: 000001, 12: 00001, 13: 000000 |
| 4 | 0: 00011, 1: 111, 2: 0101, 3: 0100, 4: 110, 5: 101, 6: 100, 7: 0011, 8: 011, 9: 0010, 10: 00010, 11: 00001, 12: 00000 |
| 5 | 0: 0101, 1: 0100, 2: 0011, 3: 111, 4: 110, 5: 101, 6: 100, 7: 011, 8: 0010, 9: 00001, 10: 0001, 11: 00000 |
| 6 | 0: 000001, 1: 00001, 2: 111, 3: 110, 4: 101, 5: 100, 6: 011, 7: 010, 8: 0001, 9: 001, 10: 000000 |
| 7 | 0: 000001, 1: 00001, 2: 101, 3: 100, 4: 011, 5: 11, 6: 010, 7: 0001, 8: 001, 9: 000000 |
| 8 | 0: 000001, 1: 0001, 2: 00001, 3: 011, 4: 11, 5: 10, 6: 010, 7: 001, 8: 000000 |
| 9 | 0: 000001, 1: 000000, 2: 0001, 3: 11, 4: 10, 5: 001, 6: 01, 7: 00001 |
| 10 | 0: 00001, 1: 00000, 2: 001, 3: 11, 4: 10, 5: 01, 6: 0001 |
| 11 | 0: 0000, 1: 0001, 2: 001, 3: 010, 4: 1, 5: 011 |
| 12 | 0: 0000, 1: 0001, 2: 01, 3: 1, 4: 001 |
| 13 | 0: 000, 1: 001, 2: 1, 3: 01 |
| 14 | 0: 00, 1: 01, 2: 1 |
| 15 | 0: 0, 1: 1 |

### A.3 run_before, by zerosLeft (value: code)

| zerosLeft | codes |
|---|---|
| 1 | 0: 1, 1: 0 |
| 2 | 0: 1, 1: 01, 2: 00 |
| 3 | 0: 11, 1: 10, 2: 01, 3: 00 |
| 4 | 0: 11, 1: 10, 2: 01, 3: 001, 4: 000 |
| 5 | 0: 11, 1: 10, 2: 011, 3: 010, 4: 001, 5: 000 |
| 6 | 0: 11, 1: 000, 2: 001, 3: 011, 4: 010, 5: 101, 6: 100 |
| > 6 | 0: 111, 1: 110, 2: 101, 3: 100, 4: 011, 5: 010, 6: 001, 7: 0001, 8: 00001, 9: 000001, 10: 0000001, 11: 00000001, 12: 000000001, 13: 0000000001, 14: 00000000001 |
