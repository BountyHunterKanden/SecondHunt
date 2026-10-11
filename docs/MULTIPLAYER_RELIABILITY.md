# Multiplayer reliability: import versions and the MR queue

Owner, 2026-10-07: "We need all pieces of multiplayer to be as accurate and reliable as possible." Also: when a new
version changes an importer, the user is made to import again, and the same for the MPH ROM unpack.

This doc holds the plan for import versions (section 2) and the queue of open items (section 3, mirrored on the board
as "Multiplayer reliability queue", MR1-MR7). Nothing here is built yet unless its status says so. Written by Replay
mode (cont. 2) from what the code does today (section 1).

## 1. Where things stand (checked 2026-10-07)

- **Every device makes its own game data.** Nothing from the games ships with the app.
  - MPH: the ROM is unpacked (`RomSetup.Extract` -> MphRead `Extract.Setup`). The files are copies, so they're the
    same on every device. US rev 0 and rev 1 give byte-identical arena files (55/55 entity, collision and node files
    compared). LAN counts the two US revisions as one game (`LanSession.RomKeyFor`); any other ROM must match exactly.
  - Echoes arenas, music, suits, balls and guns: made by the in-app importers (`ImportService`, one `IImportKind` per
    kind, `recipes/<kind>/`). These are conversions, not copies: the arena importer works out positions and facings and
    writes them into the arena's files.
- **Nothing tracks an importer's code version.**
  - The ROM unpack writes `.extracted` (`version=<MphRead version>`, `rom=`, `files=`), and the hosts only check that it
    exists (`RomSetup.IsExtracted`). A changed unpack never reaches a device that already unpacked.
  - Item imports record each recipe's SHA-1 in `<root>/import/<gameId>.json`. An edited recipe shows the item as
    UPDATE on the IMPORT GAMES page, but that's optional and the old files keep being used. A change to the importer's
    code with the same recipe isn't noticed at all.
- **Two ways two devices' imports can differ:**
  1. *Platform maths.* The Echoes arena importer takes trig from `System.MathF` (spawn facings in `EchoesArena`, pad
     placement in `ArenaCannons.Find`), which comes from each platform's C library (Windows vs Android can differ in the
     last bit). The game itself uses our own trig since R2b (`MphRead/Utility/DetMath.cs`); the importers don't. Not
     seen yet: the Odin's import on 10-07 matched the PC's byte for byte.
  2. *Importer changes between app versions.* Whoever imported before a change keeps the old output.
  A different source disc isn't a third way: the importers only accept discs whose SHA-1s the recipes know.
- **What a difference does today.**
  - Replays: a replay plays only on matching arena files (the fingerprint in replay file format 2,
    `Replay/ReplayData.cs`); otherwise it's refused before the first step, naming the file.
  - LAN: the host runs the match and sends its state every frame. The guest runs its own copy so its controls respond
    at once: its player moves on its own input and is pulled back when it drifts too far, and only the host decides
    damage. Different files don't split the match (the host's version wins), but the guest sees rubber-banding near
    walls or floors that differ, hits that don't count (or the reverse), and jump pads that land wrong and snap back.
    Nothing compares the files at join today.

## 2. Plan: import versions and a forced re-import (MR1)

**Goal:** a device never plays on data made by an older importer. When an update changes an importer, the device
makes that data again before it's used. Devices on the same app version then hold the same data by construction
(MR2 takes care of the platform-maths case).

### V0. Versions and stamps (S)
- One table in Core, `Import/ImporterVersions.cs`: `MphUnpack` and one number per import kind (`arenas`, `music`,
  `hd`, `guns`, ...). Each change gets a history line, as `ReplayFile.CurrentSimVersion` does. Every number starts at 1,
  which means today's importers.
- Stamps:
  - `.extracted` gets `importer=<MphUnpack>`.
  - Each item's entry in `import/<gameId>.json` gets `"importer": <kind version>`.
  - A missing stamp reads as 1, so the day this ships nobody is forced to re-import (decision D4).

### V1. Item imports: stale means unusable (S-M)
- An item is stale when its kind's version or its recipe SHA-1 differs from the stamp.
- A stale item is treated as not imported:
  - an Echoes arena leaves the arena list;
  - a suit, ball or gun falls back to the game's own;
  - its derived caches (rig caches, `cache/`) are cleared with it.
- On start, if anything is stale, a message names what changed and offers IMPORT AGAIN. The remembered disc
  (`import/sources.json`) makes it one tap; if the file has moved, the picker opens.
- The new import goes to staging and is swapped in when complete. A failed or cancelled re-import leaves the item
  stale, never half-written.

### V2. The MPH unpack: re-unpack before the game starts (M)
- On start, a `.extracted` whose `importer` is older than `MphUnpack` means the game data is out of date. The game
  doesn't start until it's unpacked again (decision D1).
  - Android keeps `rom.nds` in its files folder, so this is automatic: an "updating game data" screen (the unpack
    takes about 15 s on the PC; a phone's time isn't measured).
  - Windows doesn't keep the ROM today. Either keep a copy from now on (64 MB, decision D2), or ask for the ROM.
- The new unpack goes to staging and is swapped in when complete. Saves, settings, `import/` and `mods/` aren't
  touched.
- If the new unpack changes a gameplay file other than the arena files the replay fingerprint covers, bump
  `ReplayFile.CurrentSimVersion` too (the golden replays catch it on the PC).

### V3. Developer guard: output can't change without a version bump (S)
- Item kinds: the `-importcheck` goldens already compare the in-app importer's output with the reference byte for
  byte. A failure tells you to bump the kind's version, and the reference isn't rewritten until you do, as
  `-replaygolden record` works.
- MPH: a new `-importcheck mph`. It unpacks the owner's ROM and compares every output file's SHA-1 with a manifest in
  `recipes/mph/<rom>.json`, with the same rule. A manifest holds hashes only, so it's ship-safe.

### Decisions for the owner (all four: yes, owner 2026-10-07 ~15:30, with the GO for MR1)
- **D1.** Stale MPH data blocks the game until it's unpacked again. Recommended: yes. It's automatic on Android and
  takes seconds.
- **D2.** Windows keeps a copy of the ROM (64 MB) in its data folder, so updates re-unpack without asking. Recommended:
  yes.
- **D3.** A stale imported item can't be used until it's imported again (asked once at start). Recommended: yes, as
  asked ("force").
- **D4.** Existing installs count as version 1, so nothing is forced the day this ships. Recommended: yes.

### Who builds it (to confirm)
- The import kinds and `ImportService`: Clean-room queue (Core `Import/`).
- The start-up prompt and the IMPORT GAMES page: Start menu and UI (`Frontend/ImportGames.cs`).
- The MPH re-unpack: `App/Platform/RomSetup.cs` plus the Android and Windows start-up (`GameActivity`, `DesktopApp`).
- LAN (MR3): the multiplayer menu thread.
- Replays (MR7): Replay mode.

## 3. The queue (MR1-MR7)

Same TAKEN / DONE convention as the board's other queues.

| # | Item | Done when | Thread (to confirm) | Needs |
|---|---|---|---|---|
| MR1 | Import versions and forced re-import (section 2, V0-V3) | a changed importer forces its data to be made again before use, on Android and Windows; the guard fails a changed output without a bump | Clean-room queue + Start menu and UI (+ RomSetup's owner) | GO + D1-D4 yes (owner 10-07 ~15:30) |
| MR2 | Echoes importer on our own trig (`DetMathF` in `EchoesArena` / `ArenaCannons`; the MphRead alias doesn't reach Core) | the arena files come out byte-identical on Windows and Android (Tools `-importcheck arenas` on the PC plus an Odin import compared), arenas version bumped | Echoes arenas (MP2 multiplayer import) | MR1 first, so the bump forces old imports out |
| MR3 | The join check compares what a match plays on: game logic version (done, protocol 5), importer versions, an arena fingerprint (`ReplayData.Fingerprint`, at START for the chosen arena), mods. It refuses with the reason. Replaces the ROM-key check as the board's cross-session note suggests. Online play, when built, uses the same check | a guest with different files or mods can't start a match and is told which | multiplayer menu (LanSession) + Multiplayer | MR1 for the versions; the fingerprint is ready |
| MR4 | Other regions: compare the European and Japanese multiplayer files with the US ones. If identical, let them play together (as rev 0 and rev 1); MR3's fingerprint makes this automatic. Also fix docs/MULTIPLAYER.md section 1 ("differ in weapon and item data" is wrong for US rev 0/1, already on the board) | each region's match files diffed and the join rule set from the result | ROM revisions / Multiplayer | the European and Japanese ROMs (the owner's own copies) |
| MR5 | MatchSim (Multiplayer's headless bot sim) runs with `Scene.HostDrawFreeSim`, so Spire's rocks don't leave phantom hits when no frame draws (found in R0) | a headless Spire match equals the drawn one (`-drawinvariance`) | Multiplayer | - |
| MR6 | Replays exact across devices (R2b device half) | the 9 goldens exact on the Odin and the Samsung (`tests/replays/device_check.sh verify`), and a device recording exact on the PC (`record`) | Replay mode | the Odin back on adb |
| MR7 | Replays name an older import: store the importer versions next to the fingerprint and say "recorded with an older import of this arena" instead of only "differs" | a replay from before a forced re-import is refused with that reason | Replay mode | MR1 V0 |
