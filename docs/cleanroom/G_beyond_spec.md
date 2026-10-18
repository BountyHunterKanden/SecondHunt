# G: Metroid Prime 4: Beyond model, material and texture reader, specification

For the clean-room reader behind the `mp4` import, first for the MP4 Sylux skin. The player's own Switch RomFS dump
holds the game's assets in `.pak` archives. This reader takes, from those archives, a skinned model's meshes, its
materials' texture and colour parameters, and those textures decoded to RGBA. The app's existing converters
(`IBeyondModelSource` in `Import/Beyond/BeyondSources.cs`, and the Sylux look conversion) build on it.

Written by the spec author from the bytes of the player's own files, checked by a throw-away prototype against them.
It contains no code. Every name in it is new.

**Conventions.** All numbers in these files are little-endian. "u8/u16/u32/u64" are unsigned, "s64" signed, "f16" an
IEEE half, "f32" an IEEE single. A "tag" is 4 ASCII bytes, written here as text (`RFRM`). A "GUID" is 16 bytes, read as
a u32, two u16s and 8 bytes taken in order. Its text form is the usual lower-case `8-4-4-4-12` hex. For example, the
bytes `c5 75 3f 2c e0 19 0e 43 bf 24 7d 8d 0a 19 65 1f` are `2c3f75c5-19e0-430e-bf24-7d8d0a19651f`. Offsets are in
bytes, and "at +N" means N bytes after the start of the structure being described. ⌈a/b⌉ is a divided by b rounded up,
and ⌊a/b⌋ rounded down.

**Sizes.** `Preload/DuplicateData.pak` is 3.85 GB and holds 21,276 assets. The reader works on a seekable, read-only
stream. It reads an archive's table of contents (about 1.3 MB for that file) and then only the assets asked for.

**Status marks.** **[checked]** means the rule was confirmed on Sylux's files, either bit for bit against the
reference outputs or by decoding to exactly the expected length. **[unchecked]** means the rule is stated for
completeness but no Sylux file uses it. A reader may refuse anything **[unchecked]** with a clear error instead of
supporting it.

## 1. Forms and chunks

Every asset, and the archive itself, is a **form**: a 32-byte header followed by chunks.

| at | type | meaning |
|---|---|---|
| +0x00 | tag | `RFRM` |
| +0x04 | s64 | length of everything after this 32-byte header |
| +0x0C | u64 | (not needed) |
| +0x14 | tag | the form's kind (`PACK`, `SMDL`, `MATI`, `TXTR`, ...) |
| +0x18 | u32 | version A |
| +0x1C | u32 | version B |

A **chunk** starts with a 24-byte header:

| at | type | meaning |
|---|---|---|
| +0x00 | tag | the chunk's kind |
| +0x04 | s64 | the chunk's size S |
| +0x0C | u32 | (not needed) |
| +0x10 | s64 | data skip K |

The chunk's data starts at chunk start + 24 + K. The next chunk starts at chunk start + 24 + S. An asset's chunks
start right after its form header (at +32) and run to the end of the asset. **[checked]**

## 2. Archive (`.pak`)

### 2.1 Layout

The file starts with a form of kind `PACK` (versions 1/1 in every file seen). At +32 is a second form header, of kind
`TOCC` (versions 4/4). Its length field T covers the table-of-contents chunks, which start at file offset 64 and run
to 64 + T. The chunks are `ADIR`, `META` and `STRG`, in that order. A reader rejects a file whose first form isn't
`RFRM`/`PACK`. **[checked]**

### 2.2 Asset directory (`ADIR`)

The data is a u32 count N followed by N 60-byte entries:

| at | type | meaning |
|---|---|---|
| +0x00 | tag | asset kind (`SMDL`, `MATI`, `TXTR`, `CHPR`, ...) |
| +0x04 | GUID | asset id |
| +0x14 | u32 | version A |
| +0x18 | u32 | version B |
| +0x1C | s64 | offset of the stored bytes, from the start of the archive file |
| +0x24 | s64 | unpacked length U |
| +0x2C | s64 | stored length P |
| +0x34 | u64 | (not needed) |

An id can appear more than once. The first entry with a given id is the one used. **[checked]**

**Reading an asset.** Read P bytes at the entry's offset. If U equals P, those bytes are the asset. Otherwise they are
one packed buffer (section 3) that unpacks to U bytes. Either way the result is a form (section 1) of the entry's kind.
**[checked]** Packed assets are common in the archives. All 217 packed assets of `DuplicateData.pak` under 8 MB unpacked to valid
forms of their kind (codes 12, 13, 14 and 1). All of
Sylux's assets (model, character file, materials, textures) are stored unpacked. Their GPU buffers are packed
inside them instead (4.1, 6.2).

### 2.3 Side data (`META`)

Some assets carry **side data**: a small block outside the asset that says where the asset's GPU buffers sit and how
they are packed. The `META` chunk's data starts with a u32 count M. M 20-byte records follow, each a GUID and a u32
offset R. That asset's side data starts at (the `META` chunk's data start) + 4 + R, measured from the start of the
archive file. Its length isn't stored. A reader takes the bytes from that position up to the next larger side-data
position in the same chunk, or up to the chunk's end. The first record for an id wins. **[checked]**

### 2.4 Names (`STRG`)

A u32 count, then per record: a tag, a GUID, a u32 byte length L and L bytes of ASCII name. Not needed by the reader.
A reader may expose the names for messages.

## 3. Packed buffers

A packed buffer is a u32 **packing code** followed by the packed data, so the data is the stored length minus 4. The
caller always knows the unpacked length O, and unpacking stops once exactly O bytes have been written. Every copy below
writes forward, one byte at a time, so a source range may overlap the bytes being written; that repeats them. A copy
that would start before the output's first byte, a read past the end of the packed data, or ending with fewer than O
bytes written makes the buffer **damaged**, and the reader reports an error.

| code | packing |
|---|---|
| 0 | none: the first O bytes of the data **[unchecked]** |
| 1, 2, 3 | flagged copy coding with a unit of 1, 2 or 4 bytes (3.1). Code 1 **[checked]**, codes 2 and 3 **[unchecked]** |
| 12, 13, 14 | bit-coded copy coding with a unit of 1, 2 or 4 bytes (3.2) **[checked]** |
| 15 | a zlib stream (RFC 1950) **[unchecked]** |
| other | not supported: report the code |

Let g = 0, 1 or 2 for the three variants of a family (codes 1/12 → g = 0, 2/13 → 1, 3/14 → 2), and let the unit
u = 2^g bytes.

### 3.1 Flagged copy coding (codes 1-3)

The data is a sequence of items. Before every group of 8 items comes one flag byte, read only when another item is
needed. Its bits, from the highest (0x80) down, say what each item of the group is:
- **bit clear:** the item is one unit, u bytes copied straight from the data to the output.
- **bit set:** the item is a copy. Read two bytes c0, c1.
  - The number of units is ⌊c0/16⌋ + 3 − g.
  - The distance is ((c0 mod 16) × 256 + c1) × u bytes.
  - Copy that many units × u bytes from the output position minus the distance.

### 3.2 Bit-coded copy coding (codes 12-14)

The whole data is one bit stream, read from the first byte to the last and, within each byte, from the highest bit
(0x80) to the lowest. Three fixed prefix-code tables (appendix A) turn bit strings into numbers 0-255. Until O bytes
have been written, read one bit:
- **0:** a literal unit. Read u bytes, each one as the next 8 bits, highest bit first. Write them.
- **1:** a copy. Read three numbers in this order, each with its own table: a length number L (table A.1), a low
  distance number D_low (A.2), and a high distance number D_high (A.3).
  - The distance is (D_high × 256 + D_low) × u bytes.
  - The length is (L + 3 − g) × u bytes.
  - Copy that many bytes from the output position minus the distance. A distance of 0 makes the buffer damaged.

**The tables are canonical prefix codes.** Appendix A lists, for each code length, the numbers having that length, in
the order their codes are given out. Codes are assigned as follows:
1. Start with code value 0 at the shortest length.
2. Hand out values in list order, adding 1 after each.
3. When moving from length a to a longer length b, shift the next value left by b − a bits first.

For example, the length table's first entries are: 0 → `0`, 1 → `10`, 2 → `1100`, 3 → `11010`. Decoding reads bits
one at a time until the bits read so far equal the code of some number of that length. All three tables are complete
(their code lengths satisfy Kraft's sum = 1), so every bit string decodes. **[checked]**

## 4. Skinned model (`SMDL`)

Form kind `SMDL`. Sylux's archive entry gives versions 0xB1/0xB5. The model's GPU buffers are described by its
side data (section 2.3).

### 4.1 Side data

| at | type | meaning |
|---|---|---|
| +0 | u32 | (5 for Sylux; not needed) |
| +4 | u32 | (offset of the `GPU ` chunk; not needed) |
| +8 | u32 | read-region count NR |
| | NR × 8 | each: u32 length, u32 offset of the region in the asset |
| | u32 | vertex-buffer count NV |
| | NV × 20 | each a **buffer record**: u32 region number, u32 offset in that region, u32 stored length, u32 unpacked length, u32 (not needed) |
| | u32 | index-buffer count NI |
| | NI × 20 | buffer records, the same layout |

Buffer data: take the region's offset plus the record's offset within the asset. The stored bytes there are a packed
buffer (section 3) of the stored length, which unpacks to the unpacked length. The unpacked vertex buffers are
numbered 0..NV−1 and the index buffers 0..NI−1, in record order. **[checked]** Sylux has one region (offset 0x359,
length 1,663,945), four vertex buffers and two index buffers, all packed with codes 14 or 12.

### 4.2 Chunks

The asset's chunks are `SKHD`, `HEAD`, `MTRL`, `MESH`, `VBUF`, `IBUF` and `GPU `, in that order. `SKHD`, `HEAD` and
`GPU ` aren't read as chunks. The GPU data is reached through the side data. Bytes left over at the end of a chunk's
data are ignored (Sylux's `MESH` has 13).

**`MTRL`, the material slots.** The data starts with the tag `MSHP`, then:
- u32 slot count NM
- u32 (8; not needed)
- u32 count k, then k × u32 (not needed)
- NM 38-byte slot records, each the material-instance GUID (a `MATI` asset, section 5), a second GUID (the base
  material; not needed) and 6 bytes (not needed).

A material slot number is an index into these records. **[checked]** A reader rejects any other leading tag.

**`MESH`, the draw records and levels of detail.** In order:
1. u32 record count NE.
2. NE 16-byte **draw records**:

   | at | type | meaning |
   |---|---|---|
   | +0 | u16 | material slot |
   | +2 | u8 | vertex layout number (into `VBUF`) |
   | +3 | u8 | index buffer number |
   | +4 | u32 | first index |
   | +8 | u32 | index count (a multiple of 3: a triangle list) |
   | +12 | u16, u16 | (not needed) |

3. ⌈NE/4⌉ bytes, then ⌈NE/8⌉ bytes (not needed).
4. u32 count NS, then NS × u16: the **record list**, draw-record numbers.
5. u8 level count NL.
6. NL × 5 **ranges**, each a u32 start and a u32 count into the record list.
7. u32 flag. If it is 1, NL × f32 follow (switch distances; not needed).

The **records of level l** are the draw-record numbers listed by its 5 ranges, going through the ranges in order and
each range from start to start + count − 1. Each number counts once, at its first appearance. A draw record's **levels**
are the level numbers l, ascending, that list it, each counted once. **[checked]**

Sylux: 3 records, record list [0, 1, 2, 0, 1, 2], one level with ranges (0,3) (3,3) (6,0) (3,3) (3,3). So level 0's
records are 0, 1, 2, and each record's levels are [0].

**`VBUF`, the vertex layouts.** A u32 layout count, then per layout:
- u32 attribute count NA
- NA 20-byte attributes: u32 stream s, u32 offset, u32 stride, u32 value format, u32 meaning
- u32 (not needed), u8 (not needed)

Stream s of layout j is unpacked vertex buffer **2j + s**. Vertex v's attribute bytes start at offset + v × stride in
that buffer. Two attributes may share the same bytes (Sylux's layout 1 reads one half4 as two meanings). **[checked]**

**`IBUF`, the index formats.** A u32 count, then one u32 per index buffer: 1 = u16 indices, 2 = u32 indices.
**[checked]**

### 4.3 Value formats and meanings

| format | read as | gives (x, y, z, w) |
|---|---|---|
| 20 | 2 × f16 | (a, b, 0, 0) **[unchecked]** |
| 21 | 4 × u8 | each / 255 **[unchecked]** |
| 22 | 4 × u8 | each as a whole number **[checked]** |
| 30 | 4 × u16 | each / 65535 **[checked]** |
| 34 | 4 × f16 | as stored **[checked]** |
| 37 | 3 × f32 | (a, b, c, 0) **[unchecked]** |
| 40 | 4 × f32 | as stored **[unchecked]** |

The divisions are done in single precision (value converted to f32, then divided by the f32 constant).

| meaning | what | used as |
|---|---|---|
| 0 | position | x y z |
| 1 | normal | x y z |
| 2 | tangent | (read and compared for merging; not output) |
| 5 | texture coordinates | x y = UV set 0; z w = UV set 1 (only when the format has 4 values) |
| 6 | texture coordinates | x y = UV set 2 |
| 9 | colour | x y z w (when absent: 1 1 1 1) **[unchecked]** |
| 10 | skin slots | 4 numbers into the skinned-bone table (4.6) |
| 11 | skin weights | 4 weights, one per slot |

Other meanings are read (so they count for merging, 4.5) but not output.

Sylux, layout 0 (records 1, 2) and layout 1 (record 0): position f16×4 (stream 0, stride 8); normal, tangent and UV
f16×4, skin slots u8×4 and weights u16×4 (stream 1, stride 36). Layout 1 also has meaning 6 on the same bytes as
meaning 5.

### 4.4 Building one draw mesh

For a draw record r:
1. Its indices are those at positions [first, first + count) of its index buffer, read in the buffer's format.
2. Let lo and hi be the smallest and largest of them. The mesh's **raw vertices** are vertices lo..hi of r's vertex
   layout, all of them, used or not, in order. Raw index = stored index − lo.
3. Each raw vertex's attributes are decoded per 4.3.

### 4.5 The output conventions

The reader's output must equal, value for value, what the reference export produced. These rules are what make it
equal. All arithmetic is single precision unless stated otherwise. **[checked]** Every rule here matched Sylux's
3 meshes, all 77,597 output vertices and 33,578 triangles, bit for bit.

1. **Merging.** Go through the raw vertices in order. A raw vertex whose decoded attribute values (every attribute the
   layout has, all 4 values each, compared as exact bit patterns) equal those of an earlier raw vertex becomes that
   earlier vertex. Otherwise it becomes a new output vertex, numbered in order of first appearance. Output indices are
   the raw indices mapped through this. Sylux's record 0 has 70,911 raw vertices, which merge to 70,794. Records 1 and
   2 merge none (2,375 and 4,428).
2. **Positions** and **UV sets** are output as decoded.
3. **Normals** are normalised: len = √(x² + y² + z²), the sum and the square root in single precision, then x/len,
   y/len and z/len. A zero-length normal is output as (0, 0, 0). **[unchecked]**
4. **Skin.** A slot whose decoded weight is exactly 0 is dropped. The kept slots keep their slot order. With k kept
   slots of weights w₁..w_k:
   - Each slot i < k gets qᵢ = the whole number nearest to (wᵢ × 100), with ties to even. The product is computed in
     single precision, and the rounding is applied to its exact value.
   - Slot k gets q_k = 100 − (q₁ + ... + q_{k−1}).
   - Each output weight is (f32) qᵢ × 0.01f.

   The output is the list of (bone name, weight) pairs for the kept slots, in slot order. The bone name is the
   skinned-bone table's entry (4.6) for the slot's number.
5. **Naming and numbering.** Two selections:
   - **top level:** the records of level 0, in that order.
   - **every record:** all draw records in record order.

   A mesh's **number** k is its position in the selection, from 0. Its **name** is `Mesh<k>_LOD<levels>_MatID<slot>`.
   `<levels>` is the record's level numbers joined by `_` (Sylux: `0`), or `None` if no level lists the record. For
   example: `Mesh0_LOD0_MatID0`, or `Mesh3_LOD0_1_MatID2`. The importer uses top level. Every record exists so that the
   gun converter (Gun4Builder) can name meshes of other levels.

### 4.6 The skinned-bone table

The skin slot numbers index a table of bone names that comes from the model's character file (`CHPR`). The project
already reads that file: `MphRecomp.Import.Beyond.Chpr` (`Chpr.cs`, our own reader). Build the table like this:
1. Take each render context in order, and each of its bone ids in order.
2. For each id b, find the abs node whose id's low 16 bits equal b. Its name is the table's next entry.

If no abs node matches, use the entry `?` and report it. Sylux's table has 86 entries. **[checked]**

Sylux's character file is `CHPR` `91605ed3-3323-519e-9862-f84fbe77f781` in
`Worlds/Suits/Chrono Tower/ct_2020_bossArena.pak`.

## 5. Material instance (`MATI`)

Form kind `MATI` (form versions 37/38 for Sylux). Its one chunk, `MDAT`, holds in order:
1. Two GUIDs, then 2 bytes (not needed).
2. u32, u32 (not needed). Then a u32 count NX, then NX × 10 bytes (not needed).
3. **Curves.**
   - A u8 group count.
   - Per group, a u8 mask. Per set bit of the mask (any order; they are all alike), one curve:
     - u32 key count
     - per key: f32, f32, u8 a, u8 b. Then 8 more bytes if a = 5, and 8 more if b = 5.
     - after the keys: 11 bytes.
   - None of this is needed. It is only skipped.
4. A u8 count NY, then NY × 32 bytes (not needed).
5. **Parameters.** A u32 count NP, then NP parameters. Each is a tag, a u8 format, and a value:

| format | value |
|---|---|
| 2 | 1 × f32 |
| 3, 4, 10, 12 | 4 × f32 (a colour or vector) |
| 5, 6, 7, 8 | a texture: GUID of a `TXTR` asset, u32 UV set number, GUID (not needed; all `ff` for Sylux) |
| 32, 48, 64, ... (multiples of 16 from 32) | u8, u16, then format/16 × (4 × f32) |
| other | not known: stop reading parameters here, keep those already read, and report it |

Bytes after the last parameter are ignored. **[checked]** Sylux's 3 materials each hold, in order:
- ICAN, NMAP, BCLR and METL (textures)
- DIFC, ICNC, ICMC and BKLT (format 3)
- XCH0 (48) and BKLG (32)

Material 0's ICAN uses UV set 1. Every other texture uses UV set 0.

## 6. Texture (`TXTR`)

Form kind `TXTR` (form versions 65/66 for Sylux). Its pixels live in a **surface** that the side data says how to
assemble.

### 6.1 Header chunk (`HEAD`)

| at | type | meaning |
|---|---|---|
| +0x00 | u32 | shape: 1 = flat 2D (the only one supported; others are **[unchecked]**) |
| +0x04 | u32 | pixel format (6.4) |
| +0x08 | u32 | width |
| +0x0C | u32 | height |
| +0x10 | u32 | depth |
| +0x14 | 4 bytes | (not needed) |
| +0x18 | u32 | mip level count |

The rest isn't needed.

### 6.2 Side data and the surface

| at | type | meaning |
|---|---|---|
| +0 | 3 × u32 | (not needed) |
| +12 | u32 | (not needed) |
| +16 | u32 | (not needed) |
| +20 | u32 | surface length Z |
| +24 | u32 | read-region count NR |
| +28 | NR × 9 | each: u8 (not needed), u32 offset in the asset, u32 length |
| then | 12 × u32 | the two placements, fields f0 to f11 below |

Start with a zeroed byte array of length Z, the surface. Then:

**First placement**, when f1 > 0. Read f1 bytes at the offset of region f0.
- If f1 equals f3, copy them to the surface at f2.
- Otherwise they are a packed buffer that unpacks to f3 bytes. Write those at f2.

(f4 is not needed.)

**Second placement**, when f6 > 0. Let B be region f5's offset, n6 = f6 the bytes the region holds, d = f7 its
surface offset, m = f9 the unpacked length, c = f10 the packed length and p = f11 the packed start. f8 is not needed.
1. If p < n6: read c bytes at B + p. If c equals m, they are plain bytes; otherwise they are a packed buffer that
   unpacks to m bytes. Write the m bytes at surface offset d.
2. If p > 0: copy the p plain bytes at B to surface offset d + m.
3. If p + c < n6: copy the n6 − (p + c) plain bytes at B + p + c to surface offset d + p + m.

All of a placement's writes land inside the surface. A write that wouldn't fit is an error. **[checked]** All 12 Sylux
textures use both placements, with p between 1 and 86.

### 6.3 The top mip level's layout

The surface holds every mip level in the Tegra block-linear arrangement. The reader needs only the top level, which
starts at surface offset 0. Let (bw, bh) be the format's block size in pixels and Bb its bytes per block (6.4). Then:
- W = ⌈width/bw⌉ and H = ⌈height/bh⌉ blocks;
- the row length R = W × Bb bytes;
- G = ⌈R/64⌉ groups across.

A **group** is 512 bytes holding a 64-byte × 8-row tile. The **block height** K, in groups, depends on t = H + ⌊H/2⌋:

| t | K |
|---|---|
| 128 or more | 16 |
| 64 to 127 | 8 |
| 32 to 63 | 4 |
| 16 to 31 | 2 |
| below 16 | 1 |

Byte x (0 ≤ x < R) of block row y (0 ≤ y < H) of the linear top level, at linear offset y × R + x, comes from this
surface offset:

  (⌊y / 8K⌋ × G + ⌊x/64⌋) × 512K + ⌊(y mod 8K)/8⌋ × 512 + I(x mod 64, y mod 8)

where the position inside a group is

  I(a, b) = ⌊a/32⌋ × 256 + ⌊b/2⌋ × 64 + ⌊(a mod 32)/16⌋ × 32 + (b mod 2) × 16 + (a mod 16).

**[checked]** All 12 Sylux textures match. As a check: Sylux's 2048×2048 ASTC 5×5 texture has W = H = 410, K = 16,
G = 103, and with all 12 mip levels laid out this way it fills its surface length 4,515,328 exactly.

### 6.4 Pixel formats

| code | format | block | bytes |
|---|---|---|---|
| 12, 13 | RGBA 8-bit (13 = sRGB) | 1×1 | 4 |
| 53-66 | ASTC, linear | see below | 16 |
| 67-80 | ASTC, sRGB | see below | 16 |
| 83, 84 | BC7 (84 = sRGB) | 4×4 | 16 |
| 20, 21 | BC1 (21 = sRGB) | 4×4 | 8 |
| 22, 23 | BC2 (23 = sRGB) | 4×4 | 16 |
| 24, 25 | BC3 (25 = sRGB) | 4×4 | 16 |
| 26, 27 | BC4 (26 unsigned, 27 signed) | 4×4 | 8 |
| 28, 29 | BC5 (28 unsigned, 29 signed) | 4×4 | 16 |
| 81, 82 | BC6H **[unchecked]**: not decoded | 4×4 | 16 |
| other | not supported: report the code | | |

ASTC block sizes, for code 53 + i (linear) or 67 + i (sRGB), i = 0..13:
4×4, 5×4, 5×5, 6×5, 6×6, 8×5, 8×6, 8×8, 10×5, 10×6, 10×8, 10×10, 12×10, 12×12.

Sylux uses ASTC 5×4 (54), 5×5 sRGB (69), 8×5 (58), 8×5 sRGB (72), and BC7 (83) and BC7 sRGB (84). Samus's Psychic
glove (the MP4PsychicGlove gun) uses BC3 sRGB (25).

**Shape.** Only shape 1 (flat 2D) is decoded. Shape 5 is an array of 2D slices (the Legacy suit's 874f4b1c has 3);
it is not a map a model conversion reads: the reference conversion found no single picture for it and went without,
so a conversion treats a texture of any other shape as absent (no error) **[checked]**: MP4LegacySuit's gun equals
the reference that way.

### 6.5 Decoding the top level to RGBA

Decode block by block from the linear top level. Blocks run left to right and rows of blocks top to bottom, so block
(i, j) covers pixels from (i × bw, j × bh). Crop to width × height: edge blocks may overhang (2048/5 = 409.6, so the
last column of blocks is partly outside). The output is width × height × 4 bytes R G B A, row 0 first, as decoded:
no flip, and no colour-space conversion. An sRGB format decodes to the same bytes as its linear twin. **[checked]**
The 12 Sylux textures, all formats above, equal the reference images exactly.

- **RGBA 8-bit:** the bytes as they are.
- **BC1-BC5:** the standard block formats as published in Microsoft's Direct3D block-compression documentation
  (BC1 = DXT1, BC2 = DXT3, BC3 = DXT5, BC4 = ATI1 / one channel, BC5 = ATI2 / two channels):
  - BC1: two RGB565 endpoints expanded to 8 bits by repeating the top bits; c0 > c1 (as 16-bit numbers) gives 4 colours
    (the two thirds), otherwise 3 colours plus transparent black (index 3 = 0, 0, 0, 0);
  - BC2: 64 bits of 4-bit alpha (expanded × 17) and then a BC1 colour block always read in its 4-colour mode;
  - BC3: an 8-byte BC4-style alpha block (a0 > a1 gives 8 values, otherwise 6 plus 0 and 255) and then a BC1 colour
    block always read in its 4-colour mode;
  - BC4: one channel, output as grey (R = G = B = the value, A = 255) **[unchecked]**; BC5: two channels as R and G,
    B = 0, A = 255 **[unchecked]**;
  - the rounding of the interpolated values **[checked]**: the colour thirds are taken on the expanded 8-bit values
    and rounded down, ⌊(2 × c0 + c1) / 3⌋ and ⌊(c0 + 2 × c1) / 3⌋; the alpha sevenths and fifths are rounded to
    nearest, ⌊((7 − i) × a0 + i × a1 + 3) / 7⌋ and ⌊((5 − i) × a0 + i × a1 + 2) / 5⌋. The harness compared the 4
    BC3 sRGB (25) textures of universeRoom.pak that have a reference image
    (brawl_extract/mp4/tex/universeRoom/<id>.png): all equal exactly, their colour blocks with c0 ≤ c1 included.
    Rounding the thirds to nearest instead left every texture off by 1 in up to a quarter of its colour bytes. No
    BC1, BC2, BC4 or BC5 texture has a reference image, so BC1's half (taken the same way, ⌊(c0 + c1) / 2⌋) is
    **[unchecked]**.
- **BC7:** the standard BC7 block format, as published in Microsoft's Direct3D 11 BC7 documentation:
  - 8 modes, partitions, index precision, anchor fix-ups and the p-bits;
  - endpoint expansion to 8 bits by repeating the top bits;
  - the 2-, 3- and 4-bit index weight tables: (64 − w) × e0 + w × e1 + 32, divided by 64 rounding down;
  - the channel rotation of modes 4 and 5.

  Modes without alpha give A = 255. A block with no valid mode (first byte 0) decodes to all zero **[unchecked]**.
- **ASTC:** the LDR profile of the Khronos ASTC format (Khronos Data Format Specification, ASTC chapter), 2D blocks:
  - block modes and the weight grid, including dual-plane;
  - integer sequence decoding with bits, trits and quints;
  - colour endpoint modes and their unquantisation;
  - partitions and the partition hash;
  - weight unquantisation and infill;
  - void-extent blocks.

  The **8-bit result** for each channel is taken as follows. **[checked]** The harness compared all 8 Sylux ASTC
  textures. Keeping the high byte of C instead left every texture off by 1 in up to half its bytes.
  - expand each 8-bit endpoint e to 16 bits as e × 257, for the linear and sRGB formats alike;
  - interpolate C = ⌊(C0 × (64 − w) + C1 × w + 32) / 64⌋;
  - if C = 65535, output 255. Otherwise output ⌊255 × (C / 65536) + 0.5⌋, computed in double precision (C / 65536
    first, then × 255, then + 0.5);
  - a void-extent block outputs the high byte of its stored 16-bit colour **[unchecked]**: no Sylux texture has one.

  A reserved or malformed block decodes to magenta (255, 0, 255, 255) **[unchecked]**.

## 7. What the reader offers

New files, all in `src/MphRecomp.Core/Import/Beyond/`, namespace `MphRecomp.Import.Beyond`. The names below are a
suggestion; the shape is what matters.

- **`BeyondArchive`** (an `IDisposable`): opened on a seekable `Stream`. It has:
  - the entry list: kind, id, versions, offset, unpacked and stored length, side-data position (or none);
  - lookup by id;
  - read an asset's bytes (2.2);
  - read its side data (2.3), empty when it has none.
- **`BeyondUnpack`**: unpack a packed buffer to a given length (section 3). It throws on a damaged buffer or an
  unknown code.
- **`BeyondModelFile`**: parse an `SMDL` asset plus its side data. It exposes:
  - the material slots (MATI ids);
  - the draw records with their levels;
  - the level record lists;
  - a method that builds the meshes of a selection (4.5.5) given the skinned-bone table.

  Each built mesh, **`BeyondModelMesh`**, holds:
  - number, name, levels, material slot;
  - positions (3 per vertex);
  - normals (3, normalised);
  - UV sets 0, 1 and 2 (2 per vertex each, or null when absent);
  - colours (4, default 1);
  - skin: 4 slots per vertex, holding table indices and output weights, with dropped slots as index 0, weight 0;
  - the triangle list.

  A **`ToBeyondMesh`** turns it into the existing `BeyondMesh`:
  - `Lod` = its first level, or −1;
  - `InfluencesPerVertex` = 4;
  - `JointNames` = the skinned-bone table.
- **`BeyondSkinTable`**: the skinned-bone table from a parsed `Chpr` (4.6).
- **`BeyondMaterialInstance`**: parse a `MATI` asset into its parameter list (section 5). Each parameter has: tag,
  format, its floats, and for a texture its id and UV set.
- **`BeyondTexture`**: parse a `TXTR` asset plus its side data into shape, format, width, height, mip count and the
  surface (6.2). It decodes the top level to RGBA (6.3-6.5).
- **`BeyondPakModelSource`**: an `IBeyondModelSource` over the above, for an archive set, a model id and a character
  file id.

Errors are exceptions with a message that names the asset id and what was wrong. Nothing in this reader writes files,
uses globals or depends on the platform. It runs the same in the Windows tools and on Android.

## 8. Known values for Sylux (for the implementer's own checks)

RomFS: the player's dump of Metroid Prime 4: Beyond (title 010019A01E2F2000).

- Model: `SMDL` `2c3f75c5-19e0-430e-bf24-7d8d0a19651f` in `Preload/DuplicateData.pak` (stored unpacked, 0x359 +
  1,663,945 bytes long). Its side data is 148 bytes up to the end of the index-buffer records.
- Material slots: `50ae4942-707b-4cc6-ad22-16736972f006`, `beb8789e-6249-465a-a927-7490d16d2c8e`,
  `75953a3c-f2e6-4098-9c3d-86894ed77677`, all in `DuplicateData.pak`.
- Draw records:

  | record | slot | layout | index buffer | first index | index count |
  |---|---|---|---|---|---|
  | 0 | 0 | 1 | 1 (u32) | 0 | 78,936 |
  | 1 | 1 | 0 | 0 (u16) | 0 | 4,815 |
  | 2 | 2 | 0 | 0 (u16) | 4,815 | 16,983 |

- Top-level meshes: `Mesh0_LOD0_MatID0` (70,794 vertices), `Mesh1_LOD0_MatID1` (2,375), `Mesh2_LOD0_MatID2` (4,428).
- Textures, all in `DuplicateData.pak`:

  | material | texture | id | format | size |
  |---|---|---|---|---|
  | 0 | BCLR | `fed91da4-1eea-4025-b22f-31c67c137c10` | 69 | 2048² |
  | 0 | METL | `2bd4567b-14ac-4fbe-9d45-a8c2107bda71` | 58 | 2048² |
  | 0 | NMAP | `1808900d-d8ab-43d6-a9b3-74a7be2822e1` | 54 | 2048² |
  | 0 | ICAN | `3457cf0b-5c26-4fd6-8bc0-76a86536fa18` | 72 | 8×1024 |
  | 1 | BCLR | `594206ea-4ead-4fb2-9652-9e13fb823d7e` | 69 | 512² |
  | 1 | METL | `6b0f007a-9692-42db-a54a-7cbfd8016614` | 58 | 512² |
  | 1 | NMAP | `e98b1e4f-3da9-4496-a531-367830eb14c0` | 54 | 512² |
  | 1 | ICAN | `b23b9fd8-e988-4eb5-80fd-02d080552bc3` | 72 | 512² |
  | 2 | BCLR | `e62e6df6-4259-4c22-83d3-f5d7be733be4` | 84 | 512² |
  | 2 | METL | `cfb371ac-d665-409d-ad63-d86ff6b3c53e` | 83 | 512² |
  | 2 | NMAP | `39818857-6ae1-49f6-a842-6637ba1745b5` | 83 | 512² |
  | 2 | ICAN | `c5ddd4cd-25e8-424a-b03f-ce6c5904caa3` | 84 | 512² |

- `fed91da4`: surface length 4,515,328. Regions (offset, length): (0, 166), (166, 847,240), (847,406, 1,810,196).
  Fields f0 to f11: 1, 847240, 2689600, 1825728, 23, 2, 1810196, 0, 2689600, 2688832, 1809428, 47.

## Appendix A. The prefix-code tables of section 3.2

These three tables are fixed facts of the format: every asset packed with codes 12-14 uses them. Each row lists the
numbers with that code length, in the order their codes are given out (3.2).

### A.1 The length table (256 symbols, code lengths 1 to 18, Kraft sum 1)

| code length | symbols, in code order |
|---|---|
| 1 | 0 |
| 2 | 1 |
| 4 | 2 |
| 5 | 3 4 5 |
| 6 | 6 7 9 |
| 7 | 8 255 |
| 8 | 10 |
| 9 | 11 12 13 14 15 |
| 10 | 16 17 19 |
| 11 | 18 20 21 22 30 31 47 63 71 |
| 12 | 23 24 25 27 32 33 35 38 39 51 62 |
| 13 | 26 28 29 34 36 37 40 41 43 46 48 54 55 64 65 70 72 94 95 103 123 126 127 131 |
| 14 | 42 44 45 49 50 52 53 56 57 59 61 66 67 69 73 74 78 79 83 86 87 96 97 102 111 142 143 159 199 247 |
| 15 | 58 60 68 75 76 77 80 81 82 88 91 93 98 99 104 105 106 107 110 118 119 125 128 129 130 134 135 144 147 163 165 175 176 179 183 185 189 190 191 239 243 246 249 251 252 253 |
| 16 | 84 85 89 90 92 100 101 108 109 112 113 115 117 120 121 122 124 132 133 136 137 139 141 145 146 148 149 150 151 155 158 160 164 166 167 171 172 173 174 177 180 181 182 184 186 187 195 198 201 203 206 207 211 215 219 222 223 227 229 230 231 235 238 240 241 244 245 248 250 254 |
| 17 | 114 116 138 140 152 153 154 156 157 161 162 168 169 170 178 188 192 193 194 196 197 200 202 204 205 208 209 210 212 213 214 216 220 221 224 225 228 232 233 236 237 242 |
| 18 | 217 218 226 234 |

### A.2 The low-distance table (256 symbols, code lengths 5 to 12, Kraft sum 1)

| code length | symbols, in code order |
|---|---|
| 5 | 0 8 16 24 32 |
| 6 | 4 12 18 36 40 48 56 64 72 80 88 96 104 112 120 128 136 144 152 160 168 176 184 192 200 208 216 224 232 240 248 |
| 7 | 1 20 28 44 52 60 68 76 84 92 100 108 116 124 132 140 156 164 172 180 188 196 204 212 228 244 252 |
| 8 | 2 6 14 54 148 220 236 |
| 9 | 3 9 10 22 30 42 66 90 126 162 198 |
| 10 | 5 7 11 15 23 26 34 38 46 50 58 62 70 74 78 82 86 94 98 102 106 110 114 118 122 130 134 138 142 146 150 154 158 166 170 174 178 182 186 190 194 202 206 210 214 218 222 226 230 234 238 242 246 250 254 255 |
| 11 | 13 17 21 25 27 31 33 39 41 45 47 49 55 57 63 65 69 71 73 79 81 87 89 95 97 103 105 107 109 111 113 119 121 127 129 135 137 143 145 151 153 159 161 167 169 175 177 183 185 191 193 199 201 207 209 215 217 223 225 231 233 239 241 247 249 |
| 12 | 19 29 35 37 43 51 53 59 61 67 75 77 83 85 91 93 99 101 115 117 123 125 131 133 139 141 147 149 155 157 163 165 171 173 179 181 187 189 195 197 203 205 211 213 219 221 227 229 235 237 243 245 251 253 |

### A.3 The high-distance table (256 symbols, code lengths 2 to 10, Kraft sum 1)

| code length | symbols, in code order |
|---|---|
| 2 | 0 |
| 5 | 1 2 7 |
| 6 | 3 4 5 6 8 14 15 16 |
| 7 | 9 10 11 12 13 17 18 19 20 23 24 28 30 31 32 48 |
| 8 | 21 22 25 26 27 29 33 34 35 36 37 38 39 40 41 42 43 44 45 46 47 49 50 51 53 54 55 56 62 63 64 65 79 80 88 95 96 176 |
| 9 | 52 57 58 59 60 61 66 67 68 69 70 71 72 73 74 75 76 77 78 81 82 83 84 85 86 87 89 90 91 92 93 94 97 98 99 100 101 102 103 104 105 106 107 108 109 110 111 112 113 114 115 116 118 119 120 121 125 126 127 128 129 130 134 135 136 137 143 144 159 160 168 175 177 192 |
| 10 | 117 122 123 124 131 132 133 138 139 140 141 142 145 146 147 148 149 150 151 152 153 154 155 156 157 158 161 162 163 164 165 166 167 169 170 171 172 173 174 178 179 180 181 182 183 184 185 186 187 188 189 190 191 193 194 195 196 197 198 199 200 201 202 203 204 205 206 207 208 209 210 211 212 213 214 215 216 217 218 219 220 221 222 223 224 225 226 227 228 229 230 231 232 233 234 235 236 237 238 239 240 241 242 243 244 245 246 247 248 249 250 251 252 253 254 255 |

