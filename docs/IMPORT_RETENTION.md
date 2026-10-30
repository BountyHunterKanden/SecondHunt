# Import retention: delete the ROM and discs after the first import

Built by Thread G, 2026-10-23. **Owner GO 2026-10-23:** "go, 500 MB is fine". Requirement: "I want all of this to be done before it goes in an update. no partial work."

## 1. What the player gets

**Owner redesign 2026-10-24 (final decisions):** the kept copy is called **IMPORT FILES** on screen. It holds everything connected to each import item, so a later change to any existing item rebuilds without the disc. The disc is needed again only for something new (a new character, enemy, arena...).

- **Opt-in.** A game's first import (and its first import after DELETE ALL) asks "keep the import files too? about <size> more", explains that the disc image can then be deleted, and warns that something new in a later update may need the disc again. The success message repeats that.
- **Free space** is checked before the run. If the import fits but the import files don't, the prompt says how much more is needed and offers the import without them. If even the import may not fit, it asks before going ahead.
- **We never delete the player's disc image ourselves.**
- An update that changes an importer rebuilds from the import files. Only an item they lack shows the red "DISC" badge ("this version of the game needs your <game> disc again"); picking the disc adds what it needs.
- Export/restore of the import files is not built (decided 2026-10-23).

## 2. How it works

Every importer reads its game only through `IFileSource` (`Import/FileSource.cs`): the disc readers, `FolderSource`, `EditionSource`. Retention is three wrappers around that interface, all in `MphRecomp.Core/Import/SourceKeep.cs`:

- **`RecordingSource`** wraps the disc during an import and records every read as a (path, byte range) set per file.
- **`SourceKeep.Store`** writes `<root>/source/<game>/files/<disc path>` and a `source.json` manifest, last, so a crash mid-copy leaves no manifest and no kept copy.
- **`KeptSource`** reads them back as an `IFileSource`. Sparse files carry a `.ranges` sidecar. A read outside the kept ranges throws `SourceMissingException`, which the import turns into the item message "needs the disc again: <path> isn't in the kept files" (status Failed, shown as DISC).

**Size rule (owner):** the touched files are kept whole if they total at most 500 MB (`ImportService.KeepWholeLimit`), else only the byte ranges read. The importers read through the same `IFileSource` calls either way, so the rebuild code doesn't care which form it gets.

**Cover every offered item, not only the ones built.** `ImportService.KeepSourceFiles` runs after each import. For items this run didn't make it either keeps the files the kind declares in `IImportKind.Sources(recipe)` (when every item to cover declares some and they fit the whole-file budget), or runs the importer into a scratch folder with output discarded just to record its reads (`.keeping`, removed afterwards). `KeepFilter` is set by the app to the recipes it actually offers (`ImportGames.Offered`). The manifest's `covered` map records `recipeId -> "recipeSha1:importerVersion"`, so a later import only adds what isn't covered yet.

**Rebuild.** `ImportService.RebuildFromKept(gameId, progress, ct, only)` runs `ImportFrom` on a `KeptSource`. `ImportGames.NextUpdate` does a kept-first pass: a stale or new item of a game with a kept copy is rebuilt from it, and only items that come back "needs the disc again" fall through to the disc prompt. `ImportAll`/`ImportOne` prefer the kept copy the same way, and an Update with the disc image gone (`DiscGone`) uses it automatically.

**Editions.** The manifest stores the edition id (the Echoes widescreen, Trilogy copies and so on), so a rebuild maps the kept paths through the same `EditionSource`.

**Trilogy.** Kept data is per game (mp1/mp2/mp3), each from its own part of the disc.

## 3. UI

- **Game page strip**: the space that CHOOSE DISC (PICK/CHANGE) had is split. PICK/CHANGE stays on top and behaves as before. Under it is **KEEP IMPORT FILES** with the size ("about N mb" estimated, or the kept size). A on it makes the copy from the disc (it asks for the disc if none is picked or it's gone), or adds what DISC-badged items need.
- While the import files are whole, the button is greyed as **FILES KEPT · <size>**. A still explains what they are. It goes active again when the copy is deleted or an item needs the disc (`ImportGames.KeepDone`).
- **DELETE ALL** (game page, and IMPORT GAMES for every game) deletes the imported items **and** the import files. Its confirmation says so, with the size (`ImportGames.ClearGame` / `ClearEverything`). A single item's X leaves the import files alone.
- **DELETE FILES** (DELETE IMPORT FILES), the corner button left of DELETE ALL, on the game page and on IMPORT GAMES (all games). It deletes only the import files: what was imported stays, but updating it needs the disc again. When the disc image is gone too, the warning says outright that nothing can be updated or imported until the disc is picked again (`ImportGames.DeleteImportFiles`).
- The SOURCE FILES tile and the fallback rows page's "source files" row are gone.
- **UPDATE ALL** says it rebuilds from the kept import files or the disc.
- **DISC badge**: unchanged meaning.
- A keep that fails (out of space) leaves the import intact; the disc just stays needed (`ImportReport.KeepError`).
- Size estimates for the prompt (`ImportGames.Estimate`) come from §5. A public build leaves out the Prime songs, so its estimates are lower.

## 4. Android

- The hidden 64 MiB `FilesDir/rom.nds` is deleted as soon as the extract ends (`RomSetupActivity.cs`, `MainActivity.cs`; a dev-pushed external `rom.nds` is left alone). Nothing reads it afterwards: MPH's `Extract.Setup` tree (NitroFS files, `ftc/`, the decompressed `_bin/`) holds everything gameplay and the importers need.
- `android:hasFragileUserData="true"` in `AndroidManifest.xml` makes Android ask whether to keep the app's data when the app is uninstalled. Uninstall or "Clear storage" still wipes the kept copy otherwise, which is why the UI text says to keep the disc if the player might reinstall.
- Windows keeps the same data in its app data folder; the kept copy is never exported, shared, synced or put in replays or mods.

## 5. Measured sizes (PC, 2026-10-23 and family mode 2026-10-24)

`-importcheck keep <game> <disc>` (`MphRead.Tools/ImportCheckKeep.cs`) imports a game with retention on, reports what was kept, then rebuilds **every** item into a second scratch folder from the kept copy alone (the disc is never opened) and compares the two item trees file by file, byte for byte.

**Family mode (2026-10-24, the keep behaviour now):** the import files hold every archive resource an item touched plus the closure of the ids/GUIDs those link to (`SourceFamily.Expand`), and whole files for single-file items, so a changed importer that reaches further into an item's family still rebuilds without the disc.

| Game (image) | Items | Family kept | Whole files read | Kept rebuild vs disc import |
|---|---|---|---|---|
| Prime 1 (`mp1.iso`) | 77 | 523.3 MB | about the same | 77/77 identical |
| Prime 2 (`echoes_retail.rvz`) | 69 | 675.5 MB | about the same | 69/69 identical |
| Prime 3 (`trilogy.rvz`) | 54 | 225.2 MB | 2457.7 MB | 54/54 identical |
| Prime 4 (`prime4.xci`) | 10 | 60.2 MB (reads only, 2026-10-29; family would be 5784.8 MB) | 5215.4 MB | 10/10 identical |

Notes:
- The music tracks are raw `.dsp` files straight off the disc (Prime 1: 177 MB, Prime 2: 178 MB) and make up about a quarter to a third of those totals. A public build leaves out the Prime songs.
- Prime 4 keeps only the byte ranges the Sylux import reads (owner 2026-10-29, `SourceFamily.CloseBeyondArchives = false`, and never whole files): its links reach nearly all of Preload and Worlds; see §7.
- Earlier byte-range numbers (2026-10-23, superseded): Prime 1 523 MB, Prime 2 676 MB (widescreen disc 503 MB, 26/26), Prime 3 218 MB, Prime 4 60.2 MB, Brawl 103.4 MB whole files (2/2).
- The golden check also validates each kind's declared `Sources()`: a kind that reads something it doesn't declare shows up as a failing item in this check.

## 6. Ship-safe

The kept files are the player's own data, made from their own disc on their own device, exactly like the import folders. Nothing ships; recipes still hold only ids and hashes. This is listed in `project_ship_safe_audit` terms: no asset enters the build or the public repo.

## 7. Known gaps

- **Prime 4 family size (decided 2026-10-29: reads only).** Family mode on `prime4.xci` read 60.2 MB but kept 5784.8 MB: the GUID link closure reaches 29858 resources (6648 of them only by link), nearly all of Preload (3672 MB) and Worlds (2112 MB). Owner: "keep only what is needed for sylux prime 4 import for the files copies". Beyond archives now keep the reads only (60.2 MB in 4 sparse files, `-importcheck keep mp4` 10/10 identical, 2026-10-29); a later Prime 4 importer that reads more needs the disc again. `CloseBeyond` stays in the code behind the switch. The other games' families are fine (§5).
- A successful `RebuildFromKept` doesn't refresh the manifest's `covered` stamps. The next disc import re-covers the items; it costs nothing at runtime.
- The Odin footprint is the same data; not re-measured on device.
