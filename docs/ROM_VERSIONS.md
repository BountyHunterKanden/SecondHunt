# ROM versions

> Which Metroid Prime Hunters ROM the app needs, and why. Section 3 lists every difference between the two USA
> revisions, measured from both dumps on 2026-09-30. Section 6 is the decision that is still **the owner's call**.

## 1. Identification (from the DS header)

`Utility/Extract.cs` reads `RomHeader` and matches on two fields:

| Field | Header offset | Notes |
|---|---|---|
| Game code | `0x0C`, 4 ASCII chars | e.g. `AMHE` = Metroid Prime Hunters, Americas |
| Region | `0x1D`, 1 byte | informational |
| Version (revision) | `0x1E`, 1 byte | `0x00` = rev 0, `0x01` = rev 1 |

The header keys the asset paths. A whole-file SHA-1 is the only definitive check of a clean, unmodified dump.

### Supported version keys (`Ver`, in `Formats/Formats.cs`)

| Key | Game code | Rev | Region | Helper |
|---|---|---|---|---|
| `AMHE0` | AMHE | 0 | US / Americas | `IsMphAmericas` |
| `AMHE1` | AMHE | 1 | US / Americas | `IsMphAmericas` |
| `AMHP0` | AMHP | 0 | Europe | `IsMphEurope` |
| `AMHP1` | AMHP | 1 | Europe | `IsMphEurope` |
| `AMHJ0` | AMHJ | 0 | Japan | `IsMphJapan` |
| `AMHJ1` | AMHJ | 1 | Japan | `IsMphJapan` |
| `AMHK0` | AMHK | 0 | Korea | `IsMphKorea` |

Default when unset: `Paths.MphKey = Ver.AMHE0`. **`Paths.ChooseMphPath()` prefers any rev 1 extraction over rev 0**
(AMHE1 > AMHP1 > AMHJ1 > AMHK0 > AMHE0 ...). That is why the Odin runs rev 1 even though `rom.nds` there is rev 0.
`Extract.Setup` accepts only `{AMHE, AMHP, AMHJ} x {0, 1}`, not `AMHK`.

## 2. The two USA dumps

| | USA rev 0 ("1.0") | USA rev 1 ("1.1") |
|---|---|---|
| No-Intro name | Metroid Prime - Hunters (USA, Australia) | Metroid Prime - Hunters (USA) (Rev 1) |
| SHA-1 | `90164d1ac127ee5f9815ea4ae7de798c7b5fc629` | `a8679d4f8dcb445cbe946a424241ef44901b0caf` |
| Where | owner's cart dump `mphdump.nds` (repo root, also `/sdcard/Games/NDS/` on the Odin) | owner's file `/sdcard/Games/NDS/Metroid Prime - Hunters (USA) (Rev 1).nds` on the Odin. This is the source of the app's private AMHE1 extraction |
| Files in NitroFS | 2761 | 2828 |
| ARM9 (decompressed) | 907,736 B | 909,912 B |
| SDK strings | identical (NitroSDK, WiFi 1.0.20102, DWC RC1PLUS, VX, CPS/SSL) | identical |

The mstan oracle (MetroidPrimeHuntersRecomp) accepts only rev 0. It is a *static recompilation*, so it must pin one
binary. **This project is different: the game logic is MphRead's C#, and the ROM supplies only assets and data tables.**
A revision difference reaches the app only if it is in a file we load.

## 3. USA rev 0 vs rev 1: every difference

Method: unpack both ROMs (NitroFS + ARM9 + 18 overlays, BLZ-decompressed), hash every file, then align every code
binary word by word, tolerating relocation. Every changed function was diffed side by side as normalized disassembly.
Changed level/archive/string files were decoded field by field with MphRead's own struct layouts. The scripts are in
`tools/romdiff/` (section 7).

### 3a. Data files (NitroFS): 2514 identical, 247 changed, 67 added, 0 removed

| What | Rev 1 change | Who it affects |
|---|---|---|
| **Models, animations, textures, room geometry, collision, node data, `aiPersonalityData`, `sound_data.sdat` (all music + SFX), movies** | **identical** | nobody |
| **Multiplayer arena entity files (all `mp*`, `ctf*`, `e3Level`, `unit*_Land` MP layers)** | **identical** | nobody |
| `levels/entities/unit1_RM6_Ent.bin` (Alinos, **Elder Passage**) | 1 trigger changed | campaign (3c) |
| `levels/entities/Unit3_C2_Ent.bin` (Vesper Defense Outpost, **Cortex CPU**) | 4 objects + 1 trigger changed, 3 triggers added | campaign (3c) |
| `levels/entities/Unit4_RM2_Ent.bin` (Arcterra, **Subterranean**) | elevator platform + 2 triggers changed | campaign (3c) |
| `archives/localSamus.arc` | only `bg_top_ovl.bin` (a top-screen HUD overlay image) differs. SamusGun model/anim and all other HUD pieces are identical | HUD art only |
| `stringTables/` (English) | 3 strings: tutorial "lower-right corner" -> "lower corner"; Imperialist tip "press the r button" -> "press the ZOOM button"; scan title "BINARY SUBSCRIPTURE ARTIFACT" -> "BINARY SUBSCRIPTURE" | text only |
| `stringTables_fr/gr/it/sp/jp` | translation fixes; new JP ship-screen text images | other languages only |
| `frontend/metroidhunters.bin` (menus) | adds the `toplogoR` ("TM") widget to the logo pages; other differences are string/item renumbering (the UI session handles both) | UI session |
| `frontend/metroidhunters_text_*.bin` | strings renumbered (rev 0: 728 strings, rev 1: 629); new `metroidhunters_text_en-gb.bin` | UI session |
| `frontend/single_metroidhunters*` (download-play menus), `gamerscard/`, `main menu/`, `logo_screen/`, `tostart/` anims/models | UI anim/art tweaks (mostly same size, new values) | UI only |
| `data/sound/*METROIDHUNTERS_IDS.DAT` | menu-engine sound-ID tables, grown for the extra menu items | menus only |
| `hud/Kanji_01.bin` (new) | Japanese Kanji font table, loaded by new ARM9 code `TextLoadKanjiTable` | Japanese text only |
| new `stage/*_lorez_Model.bin`, `multiplayer/any_*`, `multiplayer/deathmatch_*`, `stage/mp5_*` anims | **not referenced by name anywhere in rev 1's code or menu files** (leftover build assets) | nobody |

### 3b. Code (ARM9 + overlays)

Overlay roles from their embedded strings: ov0 = front end (menus, ship screens, records, sound test, Wi-Fi lobby),
ov1 = movie player, **ov2 = in-game core (doors, items, pickups)**, **ov3 = multiplayer mode objects (CTF / Bounty /
Nodes)**, ov4 = Nintendo WFC (NAS/DWC), ov6/ov7 = front-end/Wi-Fi helpers, **ov8 = adventure (artifacts, map, camera
sequences)**, **ov9 = bot AI + node data**, ov10-13 = enemies / Gorea, **ov15 = adventure objects (force fields,
switches)**.

| Module | Result |
|---|---|
| ov1, ov3, ov5, ov9, ov10, ov11, ov12, ov13, ov14, ov15, ov16, ov17 | **identical** apart from relocation (MP objects, bot AI, enemies, Gorea, adventure objects) |
| ov6 (Thumb, 170 KB) | 2,835 differing words, **all relocation** (2,056 pointers, 274 Thumb calls, 505 ARM calls). No logic change |
| ov2 (in-game core) | one routine: three fixed-size memory pools are now sized from free memory. No gameplay change |
| ov8 (adventure) | 4 functions, all map / pause-screen UI: room-label formatting, a language check, map struct offsets |
| ov4, ov7 | text-ID renumbering only |
| ov0 (front end) | 202 functions touched: 64 are pure text-ID renumbering; the rest are menu layout, records/stats screens, ship screens, sound-test lists, Japanese text support (checked by referenced data and strings, not instruction by instruction) |
| ARM9 | 73 functions touched, all read side by side. Detail below |

ARM9, grouped:
- **Text engine (most of the churn).** The text-draw functions (0x02049380-0x0204BF10) were rewritten for Kanji
  support. Every HUD/menu caller lost one argument and switched to a new font object. A few text positions/spacings
  moved by 1-6 px. Strings: `"%02d / %d:%02d"` became `"%02d/%d:%02d"`, plus German/Spanish/Italian system-message
  capitalisation.
- **Player update (`CPlayer` process, MphRead `ProcessPlayer`).** The Wi-Fi disconnect check gained "only in
  multiplayer". Rev 0 runs it in single player too.
- **HUD timings.** HUD message 117 is shown for 90 frames (rev 0: 75), and so is the beam "wrong weapon" warning
  (`Message.ShowWarning`, string 40; rev 0: 75).
- **Nintendo WFC friend/rival roster bug fix.** Rev 0 used a 32-bit `1 << n` on a 64-bit slot mask, so slots 32-59
  were mis-tracked. Rev 1 uses a byte bit-array. (Also documented by melonPrimeDS, which patches rev 0 for it.)
- **Memory.** Bigger main-memory arena (`0x2e400` -> `0x3c800`), arena setup for download-play boot, overlay bss
  rebalanced (ov0 -14 KB, ov4 +14 KB). An error path in a file loader now frees its buffer (leak fix). The archive
  loader now zeroes the global frame counter before one load.
- **Logbook.** Entry struct field offsets/lengths changed for localisation, and two byte reads changed from signed to
  unsigned.
- **Download play.** Banner size and its signature/key blob changed.
- **Not changed:** weapon tables, hunter stats, physics constants, damage, item tables, the room table, SFX tables
  (the 10 ARM9/overlay tables MphRead reads per version are byte-identical).

### 3c. Campaign differences (the only gameplay data that differs)

| Room | Rev 0 | Rev 1 |
|---|---|---|
| **Subterranean** (Arcterra, `Unit4_RM2`) | The elevator (platform 4, `unit4_mover3`) **starts asleep** (`StartSleep \| UseRoomState`, reverse type 2 = stop at the end). It wakes only when threshold trigger 33 receives 3 `Trigger` messages from enemy spawners 25/30/32 (`PlatformWakeup`) | The elevator **starts awake**: standing on it activates it (its own player-collision message), it cycles (reverse type 1) with a 150-frame stop delay, and trigger 33 sends `Activate`. **You can ride it without clearing the 3 spawners.** (Trigger 27 moved 1/4096 unit, which is nothing) |
| **Elder Passage** (Alinos, `unit1_RM6`) | Trigger 36 (needs trigger-state bit 0; activates **teleporter 19**) is in layers FirstVisit + **Escape** + 3 | The same trigger is in layers FirstVisit + **Cleared** + 3, and 0.48 units higher. **The teleporter turns on during the escape in rev 0, and after the area is cleared in rev 1** (the speedrun "portal stays active on English versions" / escape-skip difference) |
| **Cortex CPU** (Vesper Defense Outpost, `Unit3_C2`) | The secret room's 4 `GhostSwitch` objects (22-25; scanning all 4 fires threshold trigger 26 -> camera sequence 40) **start live** (state 1, scannable as the room loads) | They **start dormant** (state 0, not scannable). Entry trigger 29 (which locks force field 27 behind you) is enlarged and now also fires a new relay chain (triggers 43 -> 44 -> 45) that activates all 4. **The switches come on only once you are inside.** Likely a sequence-break/soft-lock fix |

Everything else in the campaign (all other rooms, enemies, bosses, items, doors, scans) is byte-identical. MphRead
handles both versions of these three rooms: platform sleep/wake, relays, and state-bit triggers are all implemented,
and an activated object becomes scannable (`ObjectEntity.UpdateState`).

### 3d. Which revision MphRead (our game logic) follows

Every rev 0/1 code change in logic that MphRead reimplements matches the **rev 1** form:
- `PlayerProcess.cs:24`: `if (GameState.Multiplayer && ...)`, the disconnect check (rev 1).
- `PlayerProcess.cs:908`: `ShowDialog(..., messageId: 117, param1: 90, ...)` (rev 1; rev 0 = 75).
- `BeamProjectileEntity.cs:660`: `ShowWarning ... 40, 90 * 2` (rev 1; rev 0 = 75).

This fits MphRead's own defaults: `Menu.cs` defaults to `AMHE1`, and its memory tools target `AMHP1`. The other
rev 1 code fixes (WFC roster, DS memory arenas, download play, Kanji text) are in systems the app does not run.
**So the app already plays like 1.1 everywhere except the three campaign rooms, and those follow whichever ROM was
imported.**

### 3e. Multiplayer

Nothing a match uses differs: arena entities, models, collision, MP mode objects (ov3), bot AI and node data (ov9,
`aiPersonalityData`), weapon/hunter tables, SFX. The only per-player differences are menu text/art and one HUD overlay
image, which affect only what each player sees locally. **A rev 0 player and a rev 1 player can share a match.** The
handshake should compare app/protocol version + mods + a hash of the match-relevant files, **not** the ROM revision.
(`docs/MULTIPLAYER.md` section 1 says the revisions "differ in weapon and item data". That is not the case.)

## 4. What 1.1 "fixes", and whether we get them

| Rev 1 change | In the app? |
|---|---|
| Disconnect check only in multiplayer; longer HUD message / warning timers | **yes**, via MphRead, regardless of ROM |
| Subterranean elevator, Elder Passage teleporter, Cortex CPU switches | **only with a rev 1 ROM** today (data). Can be made ROM-independent (section 6) |
| WFC friend-roster bitset, memory arenas, download-play banner, Kanji text engine, leak fix | not applicable: the app has its own netcode, memory and text |
| Text/translation fixes | rev 1 ROM only; English: 3 strings |

## 5. Not examined: EU / JP / KR

No dumps on hand. MphRead's offset tables show overlay9_2 layout: EU rev 0 matches US rev 0, while JP rev 0/1, EU rev 1
and US rev 1 match each other, so the code families likely split the same way. Those regions also bring other
languages, JP/KR fonts and the KR sorted scan log, which the rebuilt front end doesn't cover. Recommend USA-only for now.

## 6. Decision (owner's call, pending)

Options:
1. **Require USA rev 0 only** (what mstan does). Matches the owner's cart and the oracle. Loses nothing in code (we
   don't run ROM code) but gives everyone the 1.0 campaign rooms, and turns away rev 1 owners for no technical reason.
2. **Require USA rev 1 only.** The app is then 1.1 throughout, but the owner's own cart and the oracle (both rev 0)
   would be rejected.
3. **Accept both USA revisions, play as 1.1 for everyone (recommended).** Verify the import by whole-ROM SHA-1 against
   the two hashes in section 2. For a rev 0 import, apply the rev 1 values for the three campaign rooms (a few entity
   fields, described in our own data like a recipe, so no Nintendo data is shipped). The game then plays identically
   whichever cart was dumped, saves are portable, and multiplayer matches anyone. An optional "1.0 campaign rooms"
   setting could keep the rev 0 behaviour for speedrunners.
4. Accept both and let each ROM decide its campaign rooms (today's behaviour). Works, but two players with the same
   app get different campaigns, and the PC sweeps must run on both.

Under any option: use the ROM the user imported, not `ChooseMphPath()`'s rev 1 preference. Keep the PC dev/test
target on rev 0 (owner's cart + oracle), and add rev 1 to the campaign sweeps.

## 7. Reproducing the diff (`tools/romdiff/`, Python 3 + capstone)

```
python ndsunpack.py <rev0.nds> u/AMHE0
python ndsunpack.py <rev1.nds> u/AMHE1
python fsdiff.py                      # file-level diff of the two trees
python codediff.py                    # aligns ARM9 + overlays (caches ops_<mod>.pkl; ~3 min)
python funcdiff.py arm9               # changed functions, classified
python sbs.py arm9:0200EEAC           # side-by-side normalized disassembly of one function
python entdiff.py unit1_RM6_Ent.bin Unit3_C2_Ent.bin Unit4_RM2_Ent.bin
python entdecode.py show Unit4_RM2_Ent.bin 4 33
python arcdiff.py u/AMHE0/fs/archives/localSamus.arc u/AMHE1/fs/archives/localSamus.arc
```
Run them from one working folder; they expect `u/AMHE0` and `u/AMHE1` next to them.

## 8. Owner's cartridge (pinned 2026-08-10)

Dump: `mphdump.nds` (67,108,864 bytes = 64 MiB, untrimmed). Title `MP HUNTERS`, game code **`AMHE`**, maker `01`,
revision byte **`0x00`**. CRC32 `7FE4554A`, MD5 `b4c8a9398866b49c7be17d75736a223b`,
SHA-1 `90164d1ac127ee5f9815ea4ae7de798c7b5fc629` -> `Ver` key **`AMHE0`**.
