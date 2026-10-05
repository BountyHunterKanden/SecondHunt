# Clean-room rewrites

Code we found to follow another project's source too closely is replaced. One author writes a specification that
contains no code: behaviour as maths and GL state, with new names. A second author writes the new code from that
specification alone, without seeing the replaced code. The new code is then proven to give identical results before it
goes in.

## C: scene shading and translucency layering (2026-10-04)

**Why.** An audit compared MphRead's scene shaders (`Shaders.cs`) and its six-pass opaque / decal / translucent draw
(`Renderer.cs`), plus our Android port of both (`CampaignActivity.cs`), with hackyourlife/mph-viewer, which has no
licence. The shader lighting, toon and fog code and the pass sequence followed it closely, comments included.

**Process.**
1. **Specification** ([C_render_spec.md](C_render_spec.md)). Written by the spec author, who had read the replaced code.
   It states the required output as maths (including the order of floating-point operations, so results match bit for
   bit) and the GL state per phase. Every name in it is new.
2. **Implementation.** Written only from the specification, public GL/GLSL documentation and GBATEK, in a copy of the
   tree with the replaced code removed. The implementer didn't open the replaced files, mph-viewer or the project
   history, and only wrote new files. It reported eight places where the specification could be read two ways. The spec
   author checked each against the old behaviour, and all eight matched.
3. **Equivalence.** Old and new were run side by side on a desktop GPU (GeForce GTX 1650, driver 595.97):
   - vertex stage, desktop GLSL 1.20 and GLSL ES 3.00: 3 random seeds x 25,600 vertices with random uniforms;
   - fragment stage, both flavours: 3 seeds x 65,536 fragments, including the ES alpha-pass discards;
   - layered draw: 50 random scenes (160x120) with overlapping solid, decal and translucent quads and polygon IDs
     above 255; colour, depth and stencil compared, plus the GL state left behind. Run once with the new sequence and
     the old shader, and once fully new;
   - Android call order: 40 frames, with and without the arm-cannon hooks, compared call for call with the old pass
     block.

   Every comparison was bit-identical. The harness's self-test confirms it catches a one-ulp-scale change in a shader.
4. **Swap-in** (2026-10-04 21:56). New: `src/MphRead/Rendering/SceneShaderSource.cs`, `LayeredDraw.cs`,
   `DesktopLayerGl.cs`, `src/MphRead.Android/GlesLayerGl.cs`. Removed: the scene vertex and fragment shaders from
   `Shaders.cs` and from `CampaignActivity.cs`, both six-pass blocks, and an unused lighting helper in
   `MphRead.Tools/Testing/TestPrint.cs`. A search of the tree for the replaced code's distinctive names and comments
   finds nothing.

**Kept, and why.** These were compared too, and are facts of the hardware or the game, or MphRead's own code:
- the display-list decoders (`Renderer.cs`, Core `DsDisplayList.cs`, `GeometryBaker.cs`): the DS command format from
  GBATEK, written in MphRead's own structure and comments. A rewrite would come out the same;
- `Formats/Model.cs` animation interpolation: the same game routine, expressed differently;
- the render-mode rule in `Renderer.cs` `UpdateMaterial`: an idea, not code;
- texture decoding, including the A3I5 / A5I3 formulas, and the texture matrix constants: facts of the formats;
- the fog range computation: MphRead's own, from GBATEK.

**Not published.** The comparison harness and the audit notes contain the replaced code (needed to run old against
new), so they stay out of the repository.

## B: VX movie decoder (2026-10-05)

**Why.** An audit compared MphRead's VX movie decoder (the second half of `Formats/Movie.cs`, which decodes the
pictures and sound of the game's ActImagine VX cutscenes) with CharlesVanEeckhout/actimagine (GPL-3.0, itself a port
of Gericom's ffmpeg patch). The decoder followed it closely, in structure and in detail. MphRead's own playback code
(the first part of `Movie.cs`: timing, the two screens, textures) was not affected and stays.

**Process.**
1. **Specification** ([B_movie_spec.md](B_movie_spec.md)). Written by the spec author, who had read the replaced code.
   It states the file format, the decoder's state, every prediction, the residual coding, the audio block maths and
   the interface the rest of the app uses, with every rounding step pinned down so results match bit for bit. The code
   tables are given as bit strings from the H.264 standard. Every name in it is new.
2. **Implementation.** Written only from the specification, in a copy of the tree with the replaced decoder removed.
   The implementer didn't open the replaced code, actimagine, ffmpeg, any other VX decoder or the project history,
   didn't use the web, and only wrote new files. It reported eleven places where the specification could be read two
   ways. The spec author checked each against the old behaviour: all eleven match for the game's movies (four differ
   only for damaged files).
3. **Equivalence.** Old and new were run side by side over all 59 movie files in the game (12,864 video frames and
   147,739 audio blocks):
   - every decoded picture (RGB) and every audio sample, with the files decoded back to back on reused buffers, and
     again with fresh buffers;
   - the export path (frames as images plus the soundtrack as WAV): 61 files compared byte for byte.

   Everything was identical, in the staging copy and again in the real tree after the swap-in. The harness's self-test
   flips single bits in a movie and confirms each change is caught. The new decoder is 1.2 to 1.6 times slower than
   the old one on a desktop CPU, which is still more than 50 times faster than playback needs.
4. **Swap-in** (2026-10-05 01:08). New: `src/MphRead/Formats/Vx/` (seven files; the public `VxDecoder` class keeps the
   interface the playback code and tools use). Removed: the old decoder from `Formats/Movie.cs` (2,310 lines). A search
   of the tree for the replaced code's distinctive names finds nothing. `MphRead.Tools -movietest` passes for all 34
   cutscenes.

**Kept, and why.**
- MphRead's playback code in `Formats/Movie.cs`: MphRead's own;
- the facts of the format: the file layout, and the H.264-style transforms and code tables, which are published in
  the ITU-T H.264 standard.

**Not published.** As with C, the comparison harness and the audit notes contain the replaced code, so they stay out
of the repository.

## A: music sequencer (2026-10-05)

**Why.** The game's music is sequenced: a small program per song plays notes on the DS's sixteen sound channels. MphRead
plays it with NcsfPlay, CyberBotX's NCSF libraries (MIT). Their own headers say the sequencer part (the song program,
notes, envelopes and channel handling: `Player.cs`, `Track.cs`, `Channel.cs` and `Player/Player.cs`, `Player/Track.cs`,
`Player/Channel.cs`, `Player/SWAVWrapper.cs`) was adapted from pret's Pokémon Diamond decompilation, which has no
licence, with a few tables and helpers from DeSmuME. The rest of NcsfPlay was not affected and stays.

**Process.**
1. **Specification** ([A_sequencer_spec.md](A_sequencer_spec.md)). Written by the spec author, who had read the replaced
   code. It states the sequencer's state, every sequence command, how a note starts, the per-tick envelope,
   modulation, sweep and portamento updates, how a channel is chosen, and the mixer, with integer widths and every
   rounding step pinned down so the output matches sample for sample. The pitch, volume, decibel and sine tables are
   given as formulas, each checked entry by entry against the replaced tables. Two short lists can't come from a
   formula: the order in which channels are searched for a free one, and the 19 fast-attack envelope values. They are
   stated as facts of the DS sound driver; both are stored in the game's own ARM7 program. Every name in it is new.
2. **Implementation.** Written only from the specification, in a copy of the tree with the replaced files removed. The
   implementer didn't open the replaced code, the decompilation, DeSmuME, any other DS emulator or the project history,
   didn't use the web, and only wrote new files. It reported fifteen places where the specification could be read two
   ways. The spec author checked each against the old behaviour, and all fifteen match.
3. **Equivalence.** Old and new were run side by side over all 60 songs in the game, 120 seconds each, in the four ways
   the app plays music (desktop default; original sound; original plus our fixes, the Android default; high quality at
   48 kHz with sinc interpolation). Partway through each song the harness mutes tracks, changes a track's volume,
   speeds the tempo up and slows it down, and resets everything, the way the game and the settings do. That is
   1,052,467,200 stereo samples in all.

   Every sample was identical, in the staging copy and again in the real tree after the swap-in. The harness's
   self-test confirms it catches a 1.6% tempo change and the exact-pitch option being switched off. The new engine is
   faster than the old one (0.74 to 0.89 of its time on a desktop CPU).
4. **Swap-in** (2026-10-05 02:18). New: `src/NcsfPlay/Engine/` (eight files; the public player class keeps everything the
   app's music code uses, so that code is unchanged). Removed: the seven files above. NcsfPlay's stream wrapper
   (`Player/NCSFPlayerStream.cs`) now calls the new engine. A search of the tree for the replaced code's distinctive
   names finds nothing. `MphRead.Tools -audiolevels` and `-movietest` pass.

**Kept, and why.**
- CyberBotX's own code (MIT): the sound archive readers (`NC/`), the NCSF container (`NCSF.cs`, `Common.cs`,
  `TagList.cs`), and `Player/NCSFFile.cs` and `Player/NCSFPlayerStream.cs` (partly based on his in_xsf
  player, BSD 3-clause);
- the facts: the song, bank and wave formats and the sequence command set, the DS sound hardware as documented in
  GBATEK, and the two driver lists above.

**Removed instead of rewritten.** `ReplayGain/`, upstream's loudness analyzer, is a port of the LGPL-2.1
`gain_analysis.c`. Nothing called it (the extractor created one and never used it), so it was deleted (2026-10-05).

**Not published.** As with C and B, the comparison harness and the old-code snapshot contain the replaced code, so they
stay out of the repository.
