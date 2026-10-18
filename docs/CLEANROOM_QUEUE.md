# Clean-room queue (2026-10-07)

Owner, 2026-10-07: document every piece of the dev app that still needs work to be clean, make a queue of them, and
work through it. This file is that queue: what each item is and what "done" means.

Kept out of `docs/cleanroom/` on purpose: that folder is published with the release, and this one names internal
tools, fan rigs and the owner's devices. When an item that used the spec/implementer process is done, its record goes
into `docs/cleanroom/README.md` like A-D.

## 1. What "clean" means here

A piece is clean when both are true:

1. **In-app.** The app makes it from the user's own game files, with code in this repo plus ship-safe recipes
   (`docs/IMPORT_PIPELINE.md` rules 1-5). No PC script, third-party tool, hand edit or `adb push` in the path.
   Proven by `MphRead.Tools -importcheck` against today's reference outputs in `brawl_extract/Converted/` (text and
   JSON equal, floats within tolerance, textures pixel for pixel), recipes passing the lint, and an import on the
   Odin.
2. **Our own code.** Nothing copied from a project whose licence doesn't allow it. MIT/BSD sources are credited in
   `THIRD_PARTY_NOTICES.md`. Where a format is today read only by a third-party tool (the Prime 4 Model Dumper,
   BrawlCrate, wit), or code was found to follow an unlicensed or copyleft source, the reader is written **clean-room**:
   a spec author writes behaviour with no code and new names, a separate implementer writes the code from the spec
   alone, and a harness proves old and new give identical results (the A-D process, `docs/cleanroom/README.md`).

"Port" below means our own Python prototype (written in this project, citing only MIT format references) rewritten as
Core C#. The Python's references were checked on 2026-10-07: `prime_model.py`, `prime_tex.py`, `prime3_model.py`,
`prime3_char.py` cite PrimeWorldEditor / metaforce (MIT) as format references; `prime_to_trophy.py`, `make_pieces.py`,
`own_pieces.py`, `fighter_transfer.py` and the rig post-steps cite nothing outside the project. The one LZO case that did
follow GPL code was replaced in board item S12 (2026-10-04).

## 2. Already clean (for reference)

| Piece | How |
|---|---|
| MPH itself: rooms, campaign, HUD, menus, cutscenes, music, sound | C# in-app from the ROM. Movie decoder (B), music sequencer (A), transparency rendering (C) are clean-room |
| Disc reading: GC ISO, Wii ISO/WBFS (user's key file), WIA/RVZ, zstd/LZMA/bzip2 | `Import/Disc`, clean-room from format docs |
| Retro readers: PAK (+ LZO via lzokay, MIT), CMDL, CSKR, CINF, ANCS, ANIM, TXTR, MREA; Prime 3 PAK/CMDL/SKIN/CHAR/ANIM | `Import/Retro`, `Import/Retro3`. `Retro/Anim.cs` is a credited MIT port of PrimeWorldEditor (CR9) |
| Prime 1 / Echoes / Corruption first-person guns (10) | `Import/Guns`, byte-identical `-importcheck guns`. In-app once the IMPORT GAMES page exists (CR8) |
| Beyond animations + skeleton (CHPR) | `Import/Beyond/Chpr*`, worked out from the owner's dump |
| Echoes arena music decoder (RS03 / DSP-ADPCM) | clean-room D |
| GX material programs (Brawl `.brres`, Retro materials) and the GX interpreter | `Mdl0Gx`, `Mdl0Colors`, `GxTev`, `GxShader`: own code, compared against BrawlLib/Dolphin output only |

## 3. Not shipped at all (stay PC / comparison only)

`hd/KandenRest`, `hd/SyluxRest` (community fan-rig meshes), `MP3ZeroSuit` (dropped 10-01), anything under
`rig_work/*/dropped/`. CR7 adds a guard so no import can produce them.

## 4. The queue

Order: what the next release needs first (Beta 2 = HD Prime 1 suits, body + ball, decided 2026-10-04), then the
items that retire third-party tools, then the rest.

| # | Item | Made today by | Not clean because | Fix | Process | Board refs |
|---|---|---|---|---|---|---|
| **CR1** | **Prime 1 suits + balls**: `hd/MP1PowerSuit, MP1VariaSuit, MP1GravitySuit, MP1FusionSuit, Phazonsuit` and their balls (`MP1*SuitBall`, `PhazonsuitBall`, `SamusBall` = MP1's `Fusion_Ball`) | `prime_to_trophy.py` (mesh, PNGs, gx.json, joints, `--layout` retro skin, `--static` balls) + Tools `-hdrig` / `-rigscan --bind` + rig post-steps, then `adb push` | PC Python + push | Core C# converter on the existing Retro readers; `hd` import kind in `ImportService`; recipes `recipes/hd/<Name>.json` (character name, scale, ball model + material set + core colour, source sha1) from each output's `.convert.json`; rig caches made at import (`TrophyRigIO.LoadOrFit`); `-importcheck hd` vs `Converted/`; recipe lint `-recipecheck` (S16) | port | S21, S24, S25, S16 |
| **CR2** | **Echoes arenas + arena music** (6 arenas: room model, collision, entities, nav, HD look, cannons -> pads; the 84 music streams copied to `_archives/echoes_music/`) | Tools `-echoesarena import` on the PC (C#: `EchoesArena`, `ArenaHdWriter`, `ArenaCannons`), then the files staged onto the device | PC step + staging; MPH-mode objects (nodes, octoliths, bases) placed by hand | `mp2` import kind running the same C# on the device from the user's disc; recipes `recipes/arenas/mp2/*.json` (drop-mode objects, scale, source sha1); `-importcheck arenas` vs today's staged files | wiring (code is ours; music decoder is clean-room D) | S37, S38, S18. **DONE on the PC 2026-10-07** (kinds `arenas` + `music`, recipes/arenas/*, recipes/music/; log, section 6) |
| **CR3** | **Rest of Prime 1 / Echoes / Corruption characters**: MP1 troopers + pirate, MP2 suits / Dark Samus / commandos / troopers, MP3 suits / Dark Samus + their balls | as CR1, plus `prime3_char.py` / `prime3_model.py` for Corruption materials | PC Python + push | CR1's converter plus the Corruption material path (`Material3Gx` exists) and Echoes' bone layout | port | S21, S24 |
| **CR4** | **Brawl trophies**: Kanden, Noxus, Samus, Spire, Sylux, Trace, Weavel (mesh, textures, `.brres` looks) | `wit` extracts the disc, **BrawlCrate** exports `.dae` + textures; `Mdl0Gx` (ours) reads the looks | two third-party tools | Brawl game def on `WiiDisc`; our own reader for the trophy archives, BRRES **MDL0 geometry** and **TEX0**; writer to the layout the app reads | **clean-room** (spec from the files + public format descriptions; implementer never opens BrawlLib/BrawlCrate/wit source; harness vs today's BrawlCrate output) | S22 |
| **CR5** | **Brawl rig recipes**: joints (MIA-seeded or hand-placed), own-rule numbers (`own/<T>.own.txt`), pieces, `rest_place` facings, Rig Studio chunks, `DolKanden.posed.txt`, Samus `fighter_transfer` (needs the MP1 disc too) | `make_pieces.py`, `own_pieces.py`, `fighter_transfer.py`, `lean_fix` / `foot_fix` / `crotch_fix`, hand files in `brawl_extract/` | PC Python; hand data outside `recipes/` | port the scripts to Core; move the numbers into `recipes/brawl/<T>/` with source sha1 and no per-vertex data (rule 2 + section 6 of IMPORT_PIPELINE) | port | S26, S21 (post-steps) |
| **CR6** | **Prime 4 (Beyond)**: suits, Sylux, pirates, their balls, and the 6 MP4 guns | **LogicFox15's Prime 4 Model Dumper** DLLs (glTF + textures) + `mp4_to_trophy.py` + `mp4_batch.sh`; guns via test-only adapters over the dumper (`Gun4TestSources.cs`) | third-party tool | our own RomFS pak, SMDL/CMDL model, MATI and TXTR readers (compression, Tegra swizzle, BCn) implementing `Import/Beyond/BeyondSources.cs`; port `mp4_to_trophy`; register game `mp4` (folder source) + `modelSha1` in the MP4 gun recipes | **clean-room** (spec from the owner's own dump; implementer never opens the dumper or any other Switch tool's code; harness vs today's dumper output) | S23, S17  **Sylux DONE 2026-10-17** (owner option 1: spec `docs/cleanroom/G_beyond_spec.md` written from the dumper's source by the spec author; `Import/Beyond/` readers by a separate implementer from the spec alone): `recipes/hd/MP4Sylux.json` (game `mp4`, RomFS folder, `assetsSha1` revision check) imports through `Import/Hd/BeyondTrophy.cs` + `HdRigBuild`; `-importcheck hd MP4Sylux` PASS, every file (both rig caches too) identical to `brawl_extract/Converted/MP4Sylux`. Suits, pirates, balls and the 6 guns still use the dumper (guns need `IBeyondModelSource` / `IBeyondLookSource` over the new readers) |
| **CR7** | **Rigs at import + guards** for every character | Tools `TrophyRigger` on the PC, `*.mphrig` pushed | PC step + push | run `TrophyRigIO.LoadOrFit` and the variant builds at import (`CampaignSuit` and `MenuSamus` only read caches today); guard: no import produces KandenRest / SyluxRest / MP3ZeroSuit | port | S25, S26 |
| **CR8** | **IMPORT GAMES page** (the user-facing part of all of the above) | none (API ready) | without it nothing is reachable in the app | the page on `ImportService`: games + status, SAF document/tree pickers, Wii key file, progress + cancel, re-import, hash-mismatch message | UI work (owner GO pending, D8) | S32 |
| **CR9** | **`Retro/Anim.cs`** (Prime 1/2 animation decode) | C#, translated from PrimeWorldEditor via `prime_anim.py` | licence-adherent (MIT, credited); listed only because it's the one translated reader | optional clean-room rewrite if the owner wants no translated code at all. Also confirm `Mrea.cs`'s research prototypes (`mp2_mrea.py`, `scly.py`) cite only format references (S11 didn't cover them) | owner's call | S11 |

Every item's done bar is section 1. Items marked port can be checked straight against the Python's outputs; items marked
clean-room need the spec, a separate implementer and an equivalence harness, like A-D.

## 4a. Owner decisions (2026-10-07, after the queue was written)

- **Brawl trophies are out for now** (the owner is still working on their rigs and isn't happy with any yet); they are wanted
  later. BrawlCrate and wit are PC tools whose output files we read; neither is in our code or the app, so there is no
  licence issue until a Brawl import is built. **CR4 and CR5 are parked** until then. Focus: Samus from the Prime games.
- **CR7** folds into CR1 / CR3: rigs are already made at import for the Prime 1 suits; the fan-mesh guard waits for Brawl
  imports (every fan-made mesh we have is a Brawl hunter: KandenRest, SyluxRest, nemonic's T-pose Kanden, the Tpose trophies).
- **CR8, the IMPORT GAMES screen: GO** (board decision D8). It belongs to the Start menu and UI thread (board S32); the owner
  was given a starter prompt for it.
- **CR9 is not needed for licensing** (MIT, credited); a rewrite would be the owner's call only.
- Licensing stands as: A-D replaced or avoided everything found to follow another project's code; the rest of this queue
  is the owner's in-app rule, not licensing. Licensing returns only when a Brawl (CR4) or Prime 4 (CR6) reader is built.
- Order now: CR1 (finish: recipe lint, Odin import through CR8), CR3, CR2 + CR8, CR6 later; CR4 / CR5 parked.

## 4b. For the owner: things to know, not yet acknowledged (2026-10-07 08:25, updated 09:00)

Acknowledged already: the import is slower on the Odin than on the PC (owner: "the import speed is trivial").

1. **Jump pads ship as tuned numbers (CR2).** The Echoes cannons' jump-pad launches are solved on the PC by firing them
   in a real MphRead match (`ArenaCannons.Tune`); the app doesn't re-solve them, it takes them from
   `recipes/arenas/<Key>.json` (allowed as tuning values, IMPORT_PIPELINE rule 2). If the pads are ever re-tuned with
   `-echoesarena import`, run `-importcheck arenas --write-recipes` and then `-importcheck arenas`, or the app keeps
   the old pads. A recipe whose pads don't sit on the disc's cannon slots is refused, not imported wrong.
2. **Public builds can't import yet.** IMPORT GAMES is hidden in public builds (with the samus suit row), so arenas,
   music, suits and cannons are dev-build only until that page is shown in a public build (owner's call).
   **Owner's call made 2026-10-07 ~12:45** (to the Start menu and UI thread, after its clean-room check of everything the
   page makes): public builds show IMPORT GAMES, the samus suit and the hd menu art; the first-person arm cannons stay
   held (not offered for import, never loaded in a public campaign). Takes effect with the next public release.
3. **Imported on the Odin (2026-10-07 08:29-08:48, through the IMPORT GAMES
   screen and the real file picker):** Prime 1 21/21 from `mp1.rvz` in 46 s (peak ~770 MB), Echoes 22/22 from
   `echoes_widescreen.iso` in 148 s (~1.04 GB), Corruption 8/8 from `trilogy.rvz` in 22 s (~960 MB); no errors. Pulled
   and compared with the PC: guns, balls, music, `.dae` / `.gx.json` / PNGs byte-identical; the retro rigs (what the
   campaign plays) equal bone for bone. A Battle in the imported Sidehopper Station loads the imported arena + music
   (120 fps), and the campaign loads the imported Power Suit gun, body and ball. Left: your own look at them.
4. **The Odin still has the PC-staged arenas** in its ROM tree (`InstallStaged`); an import replaces them in the game's
   room table, so a match after importing uses the import. The staging folder `<external files>/arenas` is also where
   the import keeps `arenas/<Key>/`; staging now copies only its `_archives/` and `levels/` folders.
5. **Other files changed for CR2** (2026-10-07):
   MphRead `Metadata/Rooms.cs` (upstream file: an optional `hostFolder` constructor argument), Echoes Arenas'
   `EchoesArena.cs` / `ArenaMusic.cs` / `CampaignArena.cs` / `MainActivity.cs` Arenas(), Campaign's `ImportService.cs` /
   `CampaignLaunch.cs` / Tools `ImportCheck.cs`, the UI thread's `ImportGames.cs` / `RecompMenus.cs`.
6. **Echoes arenas still have no nodes, octoliths or flag bases** (they wait for the drop mode, MP2 plan phase P3):
   Battle, Survival and Prime Hunter only.
7. **Rig differences you haven't judged (CR1/CR3):** the imported rigs differ from the PC references by the stray-point
   pass (Fusion 1, Gravity 4, Varia 4, Phazon 8, MP2 Dark Samus 2 vertices) and MP1 Space Pirate's 2 seam points
   (hip -> knee). Judge by eye on the Odin after importing.
8. **Prime 3 importer:** looks for a character's parts only in the paks its recipe lists (the Python looked in all);
   identical for the 3 characters today, a future Prime 3 item could differ (the golden check would catch it).
9. **MEMORY.md was compacted** (2026-10-07 ~05:00, on the hook's request): 7 long index lines moved verbatim into their
   topic files with short hooks; other threads may want to adjust their hooks. Backup:
   `handoffs/clean-room-queue_2026-10-07_0730-files/MEMORY.md.before-compaction`.
10. **Nothing is committed** since 06ba39e: all of CR1, CR3, the lint and CR2 sit uncommitted (commits are your call).
11. **Rigid rigs: 3 vertices tie (accepted, no change).** The Odin's `rigid` rigs of MP2 Dark Suit (1 vertex) and MP2
    Light Suit (2) put those vertices on the other bone of a left/right pair than the PC. They sit exactly on the
    body's centre line (x = 0), so both bones are equally near and last-bit maths differences between the PC and the
    Odin's ARM chip (bind poses within 5e-7) decide the side. Weights are equal; the retro rigs are identical; one
    centre-line vertex on either side looks the same. Making it identical would mean changing the hunter rigger's tie
    rule, which re-fits every cached rig (Brawl ones included), so it's left as is.
12. **CR6 (Prime 4) needs your call: an unknown compression.** Surveyed 2026-10-07 on your RomFS dump (our own scripts reading the files only; the dumper's code not opened). The pak layer is easy: RFRM chunks
    (24-byte headers), a PACK with a table of 60-byte entries (type, GUID, offset, sizes) and a META table; no
    pak-level compression. But every texture's and model's GPU data starts with a codec code (3 for models, 12 / 13 / 14
    for textures) and is compressed (zlib can't shrink it). It isn't deflate, zlib, gzip, LZMA, XZ, Brotli, zstd or LZ4,
    and has no Oodle-style block header. The dumper ships no codec library, so its author wrote that decoder from their
    own study of the game. Options: (a) work the codec out ourselves from the files (we have compressed data and, through
    the dumper's raw output, some decompressed results; uncertain length, possibly days), then the usual spec ->
    separate implementer -> harness; (b) if it turns out to be a licensed commercial codec, a reimplementation is a
    legal question, not just work; (c) keep Prime 4 on the dumper, PC-only (dev app), and leave it out of in-app
    imports. Nothing else in CR6 starts until you pick. (Survey scripts: `handoffs/clean-room-queue_cr6-survey_2026-10-07/`.)

## 5. Out of scope

- PC-only authoring tools that only produce recipe numbers and never run in the app: Pose Studio, Rig Studio, the HD
  viewer (`RenderActivity`), the MIA auto-rigger. Their saves become recipes (CR5).
- The movie gallery, ERASE ALL, speaker modes and other WORK IN PROGRESS rows: features, not clean-room work.

## 6. Log

- 2026-10-07: queue written; CR1 started.
- 2026-10-07: **CR1 in-app converter + rigs done on the PC.** Core `Import/Hd/`: `HdRecipe`, `RetroTrophy` (port of
  `prime_to_trophy.py`'s first pass), `RetroLayout` (port of its `--layout`, incl. `--rigid-head-band`), `HdRigBuild`
  (the rigid + retro rig variants at import, on MPH's own Samus model), `HdImportKind` ("hd"); 11 recipes in
  `recipes/hd/`; Tools `-importcheck hd`. Result: 11/11 PASS against `brawl_extract/Converted/`: every `.dae`,
  `.joints.txt`, `.convert.json`, PNG and `.rigid.mphrig` byte-identical, 10/11 `.gx.json` byte-identical (Phazon's
  UV-copy fit within 2e-12), Power Suit's `.retro.txt` + `.retro.mphrig` byte-identical. Fusion, Gravity, Varia and
  Phazon's reference layouts were made on 09-27, before the script's stray-point pass: the import differs from them by
  exactly those strays (1 / 4 / 4 / 8 vertices, knee points joining the ankle), and fed the old reference layout it
  rebuilds their retro rigs byte-identical. All 11 import in 45 s on the PC. Left for CR1: the recipe lint (S16) and
  an import on the Odin (needs a dev entry that runs `ImportService`, or the IMPORT GAMES page, CR8).
- 2026-10-07: **Recipe lint done (S16).** `MphRead.Tools -recipecheck [--recipes dir] [-v]` (`RecipeCheck.cs`), exit
  code 1 on any failure; run it before each commit. Every file under `recipes/` must be strict JSON with no bulk data
  (numeric arrays and numeric-row tables up to 64, up to 4096 numbers per file, no base64/hex runs, data: URIs, or
  long or number-packed strings). Import recipes must also name a known game, parse with their kind's own reader, and
  give every source file the importer reads a 40-digit SHA-1 (`IImportKind.Sources`; the mp4 guns, whose importer
  can't hash yet, must carry a `...Sha1` field). Recipes for KandenRest, SyluxRest and MP3ZeroSuit fail. Result: 28/28
  pass; 17 planted bad files all fail. Left for CR1: the Odin import.
- 2026-10-07: **CR3 slice 1 done on the PC: Prime 1's pirates and every Echoes item.** 17 new `recipes/hd/`: MP1
  Power / Ice / Wave / Plasma troopers and the Space Pirate; MP2 Varia / Dark / Light suits (cannon `dc235a39` at
  GUN_LCTR), Dark Samus, pirate and dark pirate troopers, pirate and dark commandos, and the Varia / Dark / Light /
  Dark Samus balls. Their parameters come from the original batch commands in the session transcripts, and their paks
  are the ones the reference converter actually read (traced). `HdRecipe` gained `cmdl` (a static model by its own
  CMDL id: Echoes' suit balls). The Space Pirate has 5 rig vertices on coincident model points of different bones (a
  hip / knee seam): the importer takes the lowest-index point and logs them; the reference's SciPy KD-tree took
  whichever point its tree layout reached first, which no index rule reproduces. The golden check accepts a
  difference only at such a logged vertex (2 of the 5 differ). `-importcheck hd all`: 28/28 PASS (mp1 16 items in
  52 s, mp2 12 in 35 s). Next: slice 2, Prime 3 (Varia, PED, Dark Samus and their balls; the `prime3_char.py`
  material path and game `mp3` in `HdImportKind`).
- 2026-10-07: **CR3 done on the PC (slice 2: Prime 3).** `RetroTrophy.Convert3` ports `prime3_char.py`'s
  `load_chars` onto Core's Retro3 readers: the CHARs sharing one skeleton (Samus = top + bottom + gun) merged into one
  model, each part's skin in its model's vertex order with the split-copy vote (`Skin3`), its texture passes as GX
  programs (`Material3Gx`). Both games now hand the GX conversion one material type with 64-bit texture ids
  (`RetroTrophy.Mat`); Prime 1 / Echoes output is unchanged. `HdRecipe` takes `chars` (the CHAR ids) and a 64-bit
  `cmdl`; `HdImportKind` imports game `mp3` from the Trilogy tree or disc image. 6 new recipes: MP3 Varia, PED, Dark
  Samus and their balls (today's Python reproduces these references exactly; MP3ZeroSuit stays out). Result:
  `-importcheck hd all` 34/34 PASS (mp3: 6 items in 23 s, every file byte-identical including the rigs); MP3 Varia
  straight from `trilogy.rvz` passes in 8.4 s; `-recipecheck` 51/51. Left for CR1 + CR3: an import on the Odin through
  the IMPORT GAMES page (CR8). The Prime 1 pirates' and Echoes' never-made extras (elite pirates, the Fusion tubes
  graft) aren't in `Converted/` and aren't mapped to a rig, so they aren't in the queue.
- 2026-10-07: **CR2 done on the PC, on the Odin 08:03 (not imported there yet).** Two new import kinds in Core
  `Import/Echoes/`: `arenas` (recipes/arenas/<Key>.json -> `<root>/arenas/<Key>/`: the room files and the HD look of one
  arena, from `Metroid6.pak`, by the same `EchoesArena.Import` the PC's `-echoesarena import` runs) and `music`
  (recipes/music/EchoesArenaMusic.json -> `<root>/music/EchoesArenaMusic/`: the 8 RS03 streams copied as they are; the
  clean-room D decoder plays them). The jump pads' launches are solved on the PC against MphRead's own physics
  (`ArenaCannons.Tune`) and ship as recipe tuning values (pad, launch, lock, alt-form flag, dropped; plus the run's
  landing numbers so arena.json comes out the same); the import checks each recipe pad sits on the same cannon slot.
  The game reads the import folder where it is: `EchoesArena.ImportRoot` (set by the campaign launch and the dev launcher)
  -> `RegisterImported` (RoomMetadata's new `hostFolder`: absolute paths), `HdDir`, `ArenaMusic` (imported tracks first);
  an imported arena replaces a staged one, so the PC staging stays a dev path only. Result: `-importcheck arenas`
  7/7 PASS, all 6 arenas + the music byte-identical to today's staged files (529 files, 95 MB), and a Battle starts in
  each from the import folder; the IMPORT GAMES screen on the PC imports Echoes' 22 items in 72 s with the same output;
  `-recipecheck` 58/58. Recipes are rewritten from a new tuning run by `-importcheck arenas --write-recipes`.
  Left: the owner's import on the Odin (Echoes page, 6 arenas + "arena music"), then a match in an imported arena.
- 2026-10-07: **CR1, CR2, CR3 imported on the Odin** (08:29-08:48, through the
  IMPORT GAMES screen; numbers in section 4b item 3). Same output as the PC except 3 centre-line vertices of the MP2
  Dark / Light suits' rigid rigs (section 4b item 11). Re-run on the PC afterwards (same tree):
  `-importcheck arenas all` 7/7, `-importcheck hd all` 34/34. Left: the owner's look on the Odin.
- 2026-10-07: **CR6 surveyed, blocked on a compression codec** (section 4b item 12). Pak layer
  (RFRM / PACK / TOCC / ADIR / META) mapped from the files; texture and model GPU data use an unidentified codec (codes
  3, 12, 13, 14) that none of the standard codecs decode. Owner's call before anything is built.
