# M0 — Codebase survey & runtime/tools split plan

> Produced before moving any files, per kit §13(2). This is a **plan**, not an
> executed refactor. Confidence is marked per row: **[R]** ships in the APK
> (runtime), **[T]** desktop-only tool, **[B]** boundary/split-me, **[?]** confirm
> during execution by reading the file.
>
> Baseline verified: fork of MphRead `0.35.0.0` builds clean on .NET SDK 9.0.316
> (`dotnet build src/MphRead.sln` → 0 warnings, 0 errors; the solution file was
> later renamed to `src/MphRecomp.sln`).

## ✅ EXECUTED 2026-08-10 — 2-project split (builds 0 warn / 0 err)

`src/MphRead` is now a **runtime library**; new **`src/MphRead.Tools`** is the
desktop **exe** that references it. Same `MphRead` namespaces span both assemblies
(no `using` churn); the tools reach runtime internals via
`[InternalsVisibleTo("MphRead.Tools")]` declared in `AppInfo.cs`. Whole solution
builds clean; `LICENSE` untouched. Not committed (working tree) — awaiting review.

**Moved out to MphRead.Tools:** `Program.cs` (CLI entry), `Test.cs`, `Testing/*`
(8 harnesses), `Utility/Analyzer.cs`, `Utility/Console.cs`, `Utility/Output.cs`.

**Kept in runtime — genuinely entangled, deferred to the M1 finer (sim/presentation)
split, each with the reason the compiler/deps proved:**
- `Menu.cs` — holds runtime save/settings state (`SaveSlot`, `NeededSave`,
  `Apply{Adventure,Multiplayer}Settings`) read by GameState/Renderer/SceneSetup.
  → M1: extract that state into a runtime type; move the console-prompt shell to tools.
- `Selection.cs` — editor picking woven into EntityBase/RoomEntity/Renderer (~25 calls).
- `Export/{Images,Collada,Scripting}.cs` — exporters, but called by Renderer
  (screenshot/record), HudInfo (texture dump), Read/Collada (auto-export).
  → M1: split runtime helpers (screenshot/record/SaveTexture) from the pure exporters.
- `Utility/Repack{Model,Entity,Collision}.cs` — the `Repack` partial class; runtime
  `Read.RepackHook` depends on it. → M1: extract the hook's runtime part.

**Refactor applied to break the runtime→tool leaks the compiler found:**
`Program.Version` and `ProgramException` (both used throughout the runtime) moved
into runtime `AppInfo.cs`; the ~11 `Program.Version` references repointed to
`AppInfo.Version`.

## Layout

Two projects under `src/`:

- **`MphRead`** — `net9.0`, `Exe`. Packages: `OpenTK 4.9.4` (GL/windowing/audio),
  `SixLabors.ImageSharp 3.1.12` (texture/PNG). References `NcsfPlay`.
- **`NcsfPlay`** — NDS sequenced-music (SDAT/SSEQ/SBNK/SWAV) library + player +
  ReplayGain. Referenced by `MphRead`.

The Android build will add a third project (`net9.0-android` app / GLES surface)
in M1 and, ideally, split the sim and presentation into separate assemblies to
make Rule 1 compiler-enforced.

## Classification — `src/MphRead`

### Clearly runtime (ships in APK) **[R]**

| File / dir | Role |
|---|---|
| `GameState.cs` | Sim: match/transition/escape state, **save serialization (JSON)** |
| `Memory.cs`, `MemoryArrays.cs`, `MemoryClasses.cs` | Sim: game data structures / DS-layout state (MemoryClasses is the big one, ~339 KB) |
| `Entities/` (incl. `CamSeq/`, `Enemies/`) | Sim: entity behaviour, enemies, camera sequences |
| `Entities/Players/PlayerEntity.cs`, `PlayerProcess.cs`, `PlayerAi.cs` | Sim: player + bot logic |
| `Formats/` | Parsers for MPH file formats — needed to load extracted assets |
| `Metadata/` (`Weapons.cs`, `Rooms.cs`, `Enemies.cs`, `Player.cs`, `Metadata.cs`, `SoundMeta.cs`, `FrontendMeta.cs`) | Hardcoded gameplay constants — **the M2–M4 data-extraction targets** |
| `Sound/Music.cs`, `Sound/Sfx.cs` | Runtime audio |
| `Utility/Extract.cs` | **The ROM extractor** (first-launch flow wraps this) |
| `Utility/Archive.cs`, `Utility/Compress.cs`, `Utility/Parser.cs` | ARC handling + LZ (de)compress + parsing — extraction/loading |
| `Utility/Rng.cs` | Sim RNG |
| `Read.cs` | Core binary reader (shared, but runtime-essential) |
| `SceneSetup.cs`, `Scene.cs` | Scene/entity assembly (**[B]** touches presentation — audit) |
| `Strings.cs`, `Features.cs`, `Messaging.cs` | Game strings / feature flags / in-game messaging (**[?]**) |

### Presentation (runtime, to be modernized for GLES in M1) **[R]**

| File | Role |
|---|---|
| `Renderer.cs` (~207 KB) | The renderer — GLES port + VBO-once/batching target |
| `Shaders.cs` | GL shaders |
| `HUD/HudInfo.cs` | HUD |
| `Entities/Players/PlayerHud.cs` | Player HUD (presentation) |
| `Entities/Players/PlayerInput.cs` | Input scheme (presentation — Trilogy twin-stick replaces DS touch) |

> Note: `Entities/Players/` deliberately straddles the boundary — `PlayerEntity`/
> `PlayerProcess`/`PlayerAi` are sim, `PlayerHud`/`PlayerInput` are presentation.
> When the sim/presentation assembly split lands, this folder is split with it.

### Clearly desktop tools (never in APK) **[T]**

| File / dir | Role |
|---|---|
| `Export/Collada.cs`, `Export/Images.cs`, `Export/Scripting.cs` | COLLADA/PNG export + Blender Python scripting |
| `Testing/` (all `Test*.cs`) | Dev/test harnesses |
| `Utility/RepackModel.cs`, `RepackEntity.cs`, `RepackCollision.cs` | Content repackers (authoring) |
| `Utility/Analyzer.cs` | Dev analysis |
| `Utility/Console.cs`, `Utility/Output.cs` | Desktop console I/O / export output (**[?]** confirm Output.cs) |
| `Menu.cs` | Desktop console menu front-end (Android gets its own UI) |
| `Program.cs` | Desktop CLI entry point (Android app has its own entry) |
| `Test.cs`, `Selection.cs` | Test entry / editor entity-picking (**[?]** confirm Selection.cs isn't reused by scan visor) |

### `Formats/Movie.cs` **[B]**
ActImagine VX cutscene decoding. Runtime **only if** we play FMVs; otherwise a
tool. Decide when cutscene support is scoped.

## Classification — `src/NcsfPlay`

- Core NC/ + Player/ (SDAT/SSEQ/SBNK/SWAV parse + playback) → **[R]** runtime music.
- `ReplayGain/` → **[?]** likely export/normalization tooling; confirm whether the
  runtime player needs gain data or only the exporter does.

## Delete-or-relocate task list (execute after this plan is reviewed)

1. Create `runtime/` and `tools/` (or two assemblies `MphRecomp.Sim`,
   `MphRecomp.Presentation`, plus a `tools/` project) — decide the exact shape in M1.
2. Move all **[T]** files to `tools/`. Nothing in `tools/` may be referenced by
   the APK project.
3. Resolve every **[B]**/**[?]** by reading the file; update this table.
4. Break `Program.cs`/`Menu.cs` desktop-console assumptions out of the runtime
   path (the Android entry point replaces them).
5. Add an analyzer/boundary so sim code cannot reference presentation types
   (Rule 1 enforcement).
6. **Do not touch `LICENSE`.** Keep it at repo root. Keep README acknowledgements.

## Android first-launch extractor wrapper (kit §13(3))

**What exists (desktop):** `Program.Main` → `ConsoleSetup.Run()` →
`CheckSetup(args)` gates on extracted data (min extract version `0.19.0.0`) →
`Extract.Setup(romPath)`:

- reads `RomHeader`, validates game code ∈ {`AMHE`,`AMHP`,`AMHJ`} with revision ∈
  {0,1} (see `Ver` enum / `docs/ROM_VERSIONS.md`);
- extracts RomFS + `.arc` archives, LZ-decompresses (`LZBackward.Decompress`);
- writes `paths.txt` mapping each `Ver` → extracted root (generated, not committed).

**Android wrapper design (M0 deliverable, implemented in M1):**

1. On launch, check app-private storage for an extracted-cache marker + version.
   Absent → enter first-launch flow.
2. User selects their ROM via Storage Access Framework (content URI); stream it to
   a temp path. **No bundled/downloaded ROM, ever.**
3. Call a wrapped `Extract.Setup` with output rooted at app-private storage
   (`Context.getFilesDir()` / `getExternalFilesDir`). Refactors required:
   - replace `Console.WriteLine` progress with a UI progress callback;
   - route all `Paths.*` writes to app-private dirs, not CWD.
4. On success: header game-code + revision recognized (`Ver` matched), asset tree
   browsable, write cache marker `{ version, extractVersion }`. Offer to delete the
   temp ROM copy.
5. Failure modes surfaced to UI: unrecognized header, unsupported revision (not in
   `Ver`), truncated/failed decompress, insufficient storage.

**M0 exit criteria (from kit §9):** extractor produces a verified, browsable asset
tree from the owner's ROM; a format→converter status doc exists; the runtime tree
contains no desktop tooling.

## Open questions for execution

1. Assembly shape: two projects (runtime/tools) vs. three (sim/presentation/tools)?
   Recommendation: three, so Rule 1 is compiler-enforced — decide at M1 start.
2. `Selection.cs`, `Output.cs`, `Features.cs`, `Messaging.cs`, `NcsfPlay/ReplayGain`
   — confirm R/T by reading.
3. FMV (`Movie.cs`) — in scope for playback, or tool-only?
