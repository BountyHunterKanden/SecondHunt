# Vanilla in-ship menu and in-game pause screen

Research notes for rebuilding two screens MphRead has never implemented (it quits to
its console menu at the ship hatch, and has no pause/map screen at all). ROM is USA
rev 0 (`mphdump.nds`). Oracle = mstan's static recomp (`MphHuntersRecomp-oracle`) run
headless via `nds_runner.exe --serve`. All findings are tagged **CONFIRMED** (from ROM
data or an oracle capture) or **INFERRED** (from MphRead code comments / reasoning,
not yet directly observed).

Reference PNGs: `extract_out/vanilla_ref/01..16_*.png` (gitignored). List at the end.

---

## 1. The in-ship (gunship cockpit) menu

### 1a. Flow, confirmed as far as the oracle run reached

New game -> Adventure Mode -> file select -> **CONFIRMED** (oracle, PNGs 01-04):
Title "TOUCH TO START" -> mode select (ADVENTURE MODE / MULTIPLAYER MODE / OPTIONS,
all touch icons) -> file select (A/B/C, "CREATE NEW GAME") -> touch a slot -> checkmark
confirm popup "CREATE A NEW GAME IN THIS SLOT?" -> "SAVING TO THE GAME CARD..." -> back
to file select, now showing 0% complete -> touch the file again to actually start.

Story intro -> **CONFIRMED** (PNGs 05-06): a text-scroll briefing ("MISSION FILE
79109", quoted transmission, "DATA CONFIRMATION", "ROGUE CONTRACT" numbered
objectives, "POTENTIAL COMPLICATIONS") with a touch "SKIP" button bottom-right. This
reuses the same string entries as the `ShipInSpace` table (see 1b) — the intro and the
star map's "scanning a planet" screen are the same presentation code with different
text.

Flight to the planet -> **CONFIRMED** (PNGs 07-09): after the intro, control moves to
an interactive first-person **cockpit view** (not a video) — HUD frame, a radar
"scope" on the bottom screen tracking the destination, and once close enough a red
touch button **"LAND SHIP"** appears bottom-left. This is CameraSequence cockpit id
103 (`unit2_land_cockpit.bin` for Celestial Archives; see 1c for the full id table).

Landing -> **CONFIRMED** (PNG in scratchpad, not copied — a dark exterior shot of the
ship silhouetted against the planet): tapping LAND SHIP starts the pre-rendered
`Movie.CALanding` FMV (`Renderer.cs` line ~451-465, triggered on room load of a
landing room, `FadeOutInBlack` then `FadeOutWhite` over 5/30s, `AfterMovie.StartGame`).
This matches the code exactly, and the oracle frame we captured during the fade looks
like real FMV content, not the engine's normal lighting.

Arrival -> **CONFIRMED** (PNG 10): first-person control begins inside the Celestial
Archives landing bay (room id 45, `UNIT2_LAND`), with an on-screen "CELESTIAL
ARCHIVES" area-name banner and the normal gameplay HUD (energy, ammo, radar). The
landing bay itself is a small open silo with the gunship parked above/inside it
(PNGs 11-13); the room also has a "Scan Visor" tutorial console right near the spawn
(PNG 14, a `DialogType.Okay` popup: "TAP AND HOLD THE BUTTON CENTERED AT THE BOTTOM OF
THE TOUCH SCREEN TO ACTIVATE THE SCAN VISOR" — useful as a real reference for the
dialog-box chrome, since the hatch Yes/No box uses the same box/typewriter/button
system, just with YES/NO buttons instead of one OK).

**Still not reached after a second, longer attempt** (follow-up session): walking the
last stretch to the exact hatch trigger and confirming the Yes/No prompt and in-ship
menu pages visually. The rest of section 1 is reconstructed from ROM string tables and
MphRead's own code/comments (both very precise, just not visually confirmed).

Follow-up session notes (PNGs 15-16):
- Re-ran the boot with a single batched script instead of one round-trip per screen
  (known-good touch coordinates/timings from the first session) — reaching first
  control at the landing bay took one tool call instead of ~20.
- Tried `read_mem` to shortcut navigation: MphRead's own legacy live-memory tool
  (`src/MphRead/Memory.cs`) has absolute EWRAM addresses for the player struct for two
  *other* ROM builds (`amhp1`, `a76e`; e.g. `players: 0x20DB034` for amhp1) plus the
  relative field layout (`CPlayer.Pos` at struct offset `0x1C`, `Energy` at `0xDA`,
  stride `0xF30` per player slot) in `MemoryClasses.cs`. Neither address table is for
  our ROM (AMHE0 / USA rev 0); a probe read at the amhp1 address returned mostly zero
  bytes with a couple of plausible-looking EWRAM pointers nearby, which isn't enough
  to trust as the real struct without a proper signature scan. **Not pursued further**
  — finding the correct AMHE0 address would need its own scan (e.g. the same
  byte-signature technique `Memory.cs`'s `SetBaseAddress` uses, adapted to search NDS
  address space via `read_mem` instead of `ReadProcessMemory`), which didn't fit this
  session's budget.
- Fell back to visual small-step probing per the coordinator's tip. New finding: the
  ship's front/nose is a **solid, climbable hull surface**, not the hatch. Walking
  straight at it from spawn (no turning) reliably reaches it, but at close range the
  camera alternates between a normal first-person view and a fixed third-person shot
  of the hunter standing on the orange hull (PNG 16) — almost certainly just the
  camera pulling back to avoid clipping through the hull, not a cutscene. Pressing A
  there does nothing (briefly flips back to first person, no dialog). Also confirmed
  (PNG 15) the camera auto-tilts to look straight up at the ship when standing
  directly under it — likely just aim-assist/look-at-target behavior, not a trigger.
- Turning away from the ship to search for another way in (a ladder/door at its base
  or side) did not relocate it: D-pad turning did not behave like a fixed
  degrees-per-frame rotation across separate presses (large turns in the same
  direction did not visibly accumulate consistently, and the room turned out to be
  larger/more branching than the first session's map suggested), so dead-reckoning
  turns lost track of the ship's bearing entirely. Ran out of reasonable budget
  mid-search, standing in an unidentified part of the landing bay.
- **Conclusion for a third attempt**: don't approach the ship head-on (that path is a
  climbable-hull dead end). From spawn, try angling slightly left or right *before*
  reaching the hull, to trace its base at ground level looking for a ramp/ladder/door,
  and screenshot after every single turn press (even short ones) rather than trusting
  turn duration to predict heading.

### 1a (continued). Exact flow, reconstructed from ROM + code — CONFIRMED text, INFERRED sequencing

1. Proximity to a `Message.ShipHatch` trigger (an entity in the room's message queue,
   `GameState.cs` line 765) fires **without needing a button** — `PlayerEntity.Main
   .ShowDialog(DialogType.YesNo, messageId: 1)`. **CONFIRMED** exact text (from
   `GameMessages.bin`, entry `100M`, prefix `N`):
   `"HUNTER GUNSHIP\nenter your ship?"` — i.e. title line "HUNTER GUNSHIP", body line
   "enter your ship?". This is a touch Yes/No box (`PlayerDialog.cs`
   `DialogButton.Yes/No`, bottom-screen buttons, text types in over ~0.5s before the
   buttons accept input).
2. **YES** (`GameState.cs` line 665-690, `EnterShip()`): checkpoint/boss-flag
   bookkeeping, then per the owner's stated rule ("saving plays no cutscenes; the
   take-off movie plays only when actually launching") the **in-ship menu** should
   open here with no cutscene. **INFERRED**: MphRead's own current placeholder code
   does NOT do this — it immediately starts `Movie.{Planet}Takeoff` and quits
   (`AfterMovieAction.EndGame`). That is a known simplification, not vanilla; it
   exists only because MphRead never built the ship interior.
   **NO**: dialog closes, player resumes control (`RETURN_TO_SHIP_NO` sfx).
3. **In-ship menu**, **CONFIRMED** text/options from `stringTables/ShipOnGround.bin`
   (44 entries, full dump below) — main menu items: `SAVE GAME`, `EXIT SHIP`,
   `LAUNCH SHIP`, a location display (e.g. `CELESTIAL ARCHIVES`), `energy`, `weapons`,
   `location`, `back`, `weapon select`, `options`, `logbook`.
   - `SAVE GAME` -> prompt `"SAVE GAME: are you sure?"` -> `yes`/`no` -> `"SAVING..."`
     -> `"SAVE COMPLETE"`. **No movie**, matching the owner's rule.
   - `EXIT SHIP` -> prompt `"EXIT SHIP: are you sure?"` -> returns to on-foot
     gameplay in the landing room (no movie either — you're not leaving the planet).
   - `LAUNCH SHIP` -> prompt `"LAUNCH SHIP: are you sure?"` -> this is the one path
     that should play the take-off cutscene, then hand off to the `ShipInSpace` star
     map (below). Also generically: `"do you want to SAVE GAME?"` (likely the
     auto-prompt when launching/exiting with unsaved progress) and shared
     `yes`/`no`/`ok` button labels.
   - `weapon select` -> drag-and-drop UI: `"drag weapon to slot to equip"` /
     `"no weapons to equip"`.
   - `logbook` -> categories `lore` / `bioform` / `object` / `equipment`; states
     `"select a category"`, `"select entry"`, `"complete"`, `"NO ENTRIES FOUND"`,
     and a `kills` counter. This is MphRead's existing `StorySave.Logbook` /
     `GetLogbookCount` data (`GameState.cs` ~1835-1890) — the data model already
     exists, only the ship-menu UI to browse it doesn't.
   - `options` (in-ship copy of the same settings menu as the front end):
     `control type`, `sensitivity`, `stylus mode right/left`, `dual mode right/left`,
     `look invert`, `on`/`off`.
4. **LAUNCH SHIP confirmed** -> take-off cutscene, then `ShipInSpace.bin`'s star map:
   **CONFIRMED** text: `"scanning\nALIMBIC CLUSTER"` while plotting a course, an
   `"enemy"` label (hostile-territory marker), then per-destination mission text
   (`"---MISSION FILE #---"`, quoted transmission, `"---DATA CONFIRMATION---"`,
   `"---ROGUE CONTRACT---"`, `"---POTENTIAL COMPLICATIONS---"`,
   `"UPLOADING KNOWN COORDINATES_"` with a `skip` button) leading to the interactive
   cockpit view and a `"LAND\nSHIP"` button — i.e. **this is the exact same
   presentation we captured live for the opening flight** (section 1a above), just
   reused for every subsequent planet trip. That is strong confirmation the two share
   one code path.

### 1b. ROM data locations — CONFIRMED

- `files/AMHE0/stringTables/ShipOnGround.bin` (+ `stringTables_fr/de/it/jp/sp`
  siblings) — the in-ship-on-a-planet menu: main options, save/exit/launch prompts,
  options submenu, logbook browser, weapon-select strings. 44 entries, ~1.2KB, decoded
  in full during this session (format: 4-byte count header, then 12-byte records
  `{id[4], offset:u32, length:u16, speed:u8, category:u8}`, matching
  `RawStringTableEntry` in `src/MphRead/Formats/RawFormats.cs:580` and
  `Strings.ReadStringTable` in `src/MphRead/Strings.cs:20`).
- `files/AMHE0/stringTables/ShipInSpace.bin` — the star map / in-flight mission
  briefing: "scanning", "LAND SHIP", "enemy", and the full mission-briefing text
  block. 17 entries, ~1.2KB.
- `files/AMHE0/stringTables/GameMessages.bin` — entry `100M` is the exact hatch
  Yes/No text (`"HUNTER GUNSHIP\nenter your ship?"`); entry `200M` is the Game Over
  prompt (`"ENERGY DEPLETED\ncontinue from last checkpoint?"`), confirming the prefix
  scheme (`N` = needs Yes/No confirmation, `P` = pickup notifications).
- `files/AMHE0/stringTables/LocationNames.bin` — planet names (`ALINOS`,
  `CELESTIAL ARCHIVES`, `VESPER DEFENSE OUTPOST`, `ARCTERRA`, `OUBLIETTE`) plus every
  room's internal codename + display title (e.g. `Unit1_land\alinos gateway`,
  `crystalroom\alimbic cannon control room`) — this is also the source for the pause
  map's room-name readout (section 2b).
- Landing-room ids (also in `MphRecomp.Core/Campaign/ShipNavigation.cs:74-78`):
  Alinos=27 (`UNIT1_LAND`), Celestial Archives=45 (`UNIT2_LAND`), Vesper Defense
  Outpost=65 (`UNIT3_LAND`), Arcterra=77 (`UNIT4_LAND`), Oubliette=89 (`Gorea_Land`).
- Cockpit camera sequences, `src/MphRead/Entities/CamSeq/CameraSequence.cs:697-765`:
  loop ids 102-106 (`unit{1..4}_land_cockpit.bin`, the in-flight approach loop used
  for both the opening flight and every star-map relaunch), landing ids 114-121+
  (`unit{N}_land_cockpit_land.bin` / `..._takeoff.bin`), Gorea variants 168-170.
  `CameraSequence.cs:51` (`_cockpitLoops`) and `:62` mark these as looping/blocking
  camera sequences.
- FMV assets: `Metadata/FrontendMeta.cs:1315-1354`'s `Movie` enum —
  `{Planet}Landing` / `{Planet}Takeoff` per planet, triggered from
  `Renderer.cs:449-466` (landing, on room load) and `GameState.cs:670-690` (take-off,
  on hatch YES — currently MphRead's own placeholder path).
- No frontend/MARM involvement: the ship menu and star map are **in-game UI**, not
  part of `frontend/metroidhunters.bin`. The MARM dump
  (`extract_out/frontend/menus_AMHE0.md`) only covers title-screen/file-select/options
  menus (confirmed — grepped, no ship/hatch/launch strings there).

### 1c. What MphRead/our code already has that helps

- `GameState.cs`: the full hatch-trigger state machine already exists —
  `Message.ShipHatch` handling (line 765), `PromptType.ShipHatch`/`GameOver`,
  `EnterShip()` (line 890, checkpoint/boss-flag bookkeeping),
  `Menu.NeededSave`/`SaveFromShip`/`SaveFromExit` (save-on-quit policy),
  `StorySave.Logbook` + `GetLogbookCount` (the logbook's actual data model).
- `PlayerDialog.cs`: the Yes/No/Okay dialog box renderer (box, typewriter text,
  touch buttons) already exists and is exactly the widget the hatch prompt and every
  in-ship confirm ("are you sure?") needs — it's a data-driven `ShowDialog(type,
  messageId)` call away.
- `MphRecomp.Core/Campaign/ShipNavigation.cs` + `CampaignSaves.cs`: planet
  metadata (names, landing rooms, unlock rules from `StorySave.Areas`),
  `CampaignEnd.Classify` (tells apart hatch-YES vs. an ending vs. a quit), and the
  JSON save format — all real game-state plumbing, reusable as-is.
- `src/MphRead.Android/CampaignShip.cs`: an existing **stand-in** ship-loop UI
  (its own header comment says so explicitly) — a plain text panel with
  SavePrompt -> StarMap -> GameOver pages. It is **not** vanilla-shaped (it merges
  save+star-map into one screen and has no weapon-select/options/logbook pages), but
  its state machine (`OnHostEnded`, `LaunchSession`, `SaveShip`) is exactly the hook
  point a real vanilla-styled UI would replace the text panel of.
- Renderer.cs already has the FMV (`StartMovie`) and CameraSequence (cockpit loop)
  plumbing used by both landing and (per code) take-off.

### 1d. Proposed build plan

1. Confirm the exact hatch-trigger position and the in-ship menu's visuals/layout by
   returning to the oracle with a shorter, more targeted drive (see "next steps"
   below) — this is the one piece this session did not get first-hand.
2. Reuse `PlayerDialog`'s Yes/No box verbatim for the hatch prompt (text already
   known) and for every in-ship "are you sure?" confirm.
3. Build the in-ship menu as a new UI state (replacing `CampaignShip.cs`'s stand-in
   panel) driven by `ShipOnGround.bin`'s option list, `ShipNavigation`/`CampaignSaves`
   for star-map data, and `StorySave.Logbook`/weapon slots for the logbook and
   weapon-select pages.
4. Fix `GameState.cs`'s hatch-YES handler so it opens the in-ship menu directly
   (no movie), and only plays `Movie.{Planet}Takeoff` when `LAUNCH SHIP` is actually
   confirmed from that menu — matching the owner's stated rule and the ROM's
   `"LAUNCH SHIP: are you sure?"` prompt.
5. Route `LAUNCH SHIP` -> take-off movie/cockpit -> the `ShipInSpace` star-map flow
   (already effectively prototyped by the opening-flight capture in this session).

---

## 2. The in-game pause screen (map + legend)

Budget for this screen was intentionally small (ship flow was prioritized per
coordinator instruction) — this is ROM/code research only, no oracle capture.

### 2a. What's confirmed to NOT exist yet in MphRead

- `GameState.cs` already has a `MenuPause` boolean and `PauseMenu()`/`UnpauseMenu()`
  methods (lines 44, 110-116) that are **completely unused elsewhere in the
  codebase** (grepped — zero callers) — i.e. a reserved-but-unwired hook for exactly
  this screen, distinct from `DialogPause` (used by Yes/No/Okay boxes).
- No Start-button handling exists anywhere in `PlayerInput.cs`/`PlayerHud.cs` to
  toggle it, and no map/legend drawing code exists (`PlayerHud.cs` grepped for
  Pause/Map/Legend: nothing).

### 2b. ROM data locations — CONFIRMED

- `src/MphRead/HUD/HudInfo.cs` lines ~805-843 (a test asset list, `TestObjects`)
  names the exact sprite archives for this screen, all under `_archives/spSamus/`:
  - `map_quit.bin` — the on-screen "quit map" button.
  - `map_portal.bin` — door/portal marker icon.
  - `map_crystalbig.bin`, `map_crystalred.bin` — octolith/artifact markers.
  - `map_art_1.bin` .. `map_art_8.bin` — numbered artifact icons (per-area artifact
    count, 1-8, matches `ArtifactTotal` logic already in `ShipNavigation.cs`).
  - **`map_legendOthers.bin`, `map_legendDoors.bin`** — this is the bottom-screen
    legend itself: door-type icons and a second sheet for everything else
    (items/hazards/etc.).
  - `rad_NodesOG.bin`, `rad_NodesRB.bin` — likely map/radar node markers
    (Octolith/Guardian, Red/Blue, naming guessed — not confirmed).
  These archives are asset names only (2D sprite sheets); none of them are wired to
  any drawing code outside this test list (grepped — no other references).
- `files/AMHE0/stringTables/LocationNames.bin` — per-room display titles (e.g.
  `Unit1_c0\echo hall`, `Unit1_b1\biodefense chamber 02`) keyed by internal room
  codename; this is almost certainly the source for the room-name readout the
  vanilla map shows for whichever room is under the cursor/view. Not yet consumed by
  any MphRead code (`grep` shows it declared in `Strings.cs` but never read).
- The 3D map itself (the rotatable top-screen view) is **not confirmed** to be a
  separate simplified model — it may just be the room's own render geometry under an
  orbiting camera; this needs an oracle session (in-game, Start-button pause) to
  settle, which this session did not reach.

### 2c. What MphRead already has that helps

- `StorySave.Logbook`/`CheckLogbook`/scan tracking (`GameState.cs` ~1630-1890) and
  `RoomState`/`StorySave.CheckVisitedRoom` — the underlying per-room/per-item state
  the map would visualize already exists.
- `ShipNavigation.Status`/`StarMap` (octolith/artifact counts per area) is the same
  shape of data a full-game map summary would need, just planet-level instead of
  room-level.
- No existing 3D orbit/free camera helper was found in the codebase (grepped for
  OrbitCamera/FreeCamera/DebugCamera — none), so the touch-rotate 3D map view will
  need a new camera mode, likely alongside the existing `CameraMode` enum in
  `Renderer.cs`.

### 2d. Proposed build plan (outline only, low confidence — small budget spent here)

1. Wire `Start` to `GameState.PauseMenu()`/`UnpauseMenu()` (currently dead code) as
   the actual pause toggle, gated the same way `ApplyPause()` already gates dialog
   pause (respects `CamSeqFlags.BlockInput`).
2. Load `map_legendDoors.bin`/`map_legendOthers.bin` as the merged page's bottom
   legend (owner's plan: one 16:9 page, legend pinned at the bottom rather than
   split across two DS screens).
3. Reuse the room's existing render geometry for the 3D map pane, under a new
   touch-orbit camera; pull the room-name label from `LocationNames.bin` keyed off
   whichever room the cursor is currently over.
4. Confirm all of the above against a real oracle capture (Start pause, drag to
   rotate, legend icon meanings) before committing to layout — this session did not
   get a single real frame of this screen, so everything here is a starting point,
   not a spec.

---

## Next steps for a follow-up oracle session (ship flow)

Two sessions of blind navigation have not reached the hatch trigger. The boot-to-
landing path is now fast and reliable (one batched script, see follow-up notes above)
— the remaining problem is purely the last ~10-20m of walking inside the landing bay.
For a third attempt:
- Don't walk straight into the ship's nose — that's a confirmed climbable-hull dead
  end (PNG 16), not the hatch.
- Prefer the bottom-screen touch-drag look (confirmed responsive in the first
  session) over D-pad turning for changing heading — D-pad turn amount did not track
  held-frame count predictably (see follow-up notes), so dead-reckoning turns are
  unreliable, whereas a touch-drag's amount is at least directly proportional to drag
  distance.
- Screenshot after every single turn adjustment (even a handful of frames), not just
  after movement, so the ship is never lost off-screen for more than one step.
- If still stuck, consider properly resolving the AMHE0 `read_mem` addresses first: 
  adapt `Memory.cs`'s `SetBaseAddress` byte-signature scan (search for
  `FF DE FF E7 FF DE FF E7 FF DE FF E7` preceded by two zero bytes) to NDS address
  space via repeated `read_mem` calls, to find the real player-struct base for this
  ROM; then `CPlayer.Pos`/`Energy` offsets from `MemoryClasses.cs` should read
  directly and turn this into steering by coordinates instead of screenshots.

## Reference PNGs (`extract_out/vanilla_ref/`)

01. Title screen ("TOUCH TO START")
02. Mode select (Adventure/Multiplayer/Options)
03. File select (A/B/C, create new game)
04. "CREATE A NEW GAME IN THIS SLOT?" confirm
05. Mission-file briefing text (matches `ShipInSpace.bin`)
06. Briefing screen with the touch "SKIP" button
07. Cockpit flight HUD (interior view, en route)
08. Cockpit HUD with "CELESTIAL ARCHIVES" label + radar
09. Cockpit HUD with the red "LAND SHIP" touch button
10. First control at Celestial Archives landing bay (HUD visible)
11. Landing bay with the gunship visible ahead
12. Ship from a front-on angle on its pad
13. Close approach to the ship (third-person moment mid-navigation)
14. In-game dialog box reference (Scan Visor tutorial "OK" popup — same box/button
    chrome the hatch Yes/No prompt uses)
15. Camera auto-tilting straight up while standing directly under the ship
16. Third-person view reached by walking into the ship's nose (climbable hull —
    confirmed dead end, not the hatch)
