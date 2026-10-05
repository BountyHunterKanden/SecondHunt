# File formats → parser / converter status (M0 exit criterion)

> Inventory of the Nintendo DS / MPH file formats the engine understands, which
> **parser** loads each (runtime — ships in the APK) and which **converter /
> exporter** handles it (desktop tool — never in the APK). "Runtime-needed"
> answers: does the on-device game need this format at play time?
>
> Source of truth is the code as of fork of MphRead `0.35.0.0`. Reverse-engineering
> credit per the upstream README Acknowledgements is noted where a format's
> support derives from a specific project — that chain is preserved verbatim in
> `LICENSE`-adjacent notices and must stay intact.

## Containers & compression

| Format | Purpose | Parser (runtime) | Converter/tool | Runtime? | RE credit |
|---|---|---|---|---|---|
| ROM (NDS) | The cartridge image the user supplies | `Utility/Extract.cs` (`RomHeader`, `Extract.Setup`) | — | first-launch extract only | — |
| ARC archive | MPH's packed asset archives | `Utility/Archive.cs`, `Read.ExtractArchive` | (repack: none needed) | yes (load/extract) | McKay42 mph-arc-extractor |
| LZ10 compression | LZ-compressed files inside RomFS/archives | `Utility/Compress.cs` (`LZ10.Decompress`, `LZBackward.Decompress`) | — | yes (extract/load) | Barubary dsdecmp |

## Graphics

| Format | Purpose | Parser (runtime) | Converter/tool | Runtime? | RE credit |
|---|---|---|---|---|---|
| Model (BinModel) | 3D models (`*_Model.bin` etc.) | `Formats/Model.cs` (`ModelInstance`), `Formats/NodeData.cs`, `Formats.cs` (`Node`) | `Export/Collada.cs` (`ExportModel` → COLLADA `.dae`); `Export/Scripting.cs` (Blender Python) | yes (render) | dsgraph; Chemical's BinModel docs; McKay42 mph-model-viewer (COLLADA); hackyourlife mph-viewer (transparency) |
| Textures | Palettized DS textures within models | (with model parse) | `Export/Images.cs` (→ PNG, via SixLabors.ImageSharp) | yes (render) | — |
| Animations | Node/material/texture animations | `Formats/Model.cs` (anim groups) | `Export/Scripting.cs` (Blender import) | yes (render) | — |
| Effects / particles | Particle & effect definitions | `Formats/Effects.cs` (`TimeValues`) | — | yes (render) | — |
| Culling / portals | Node visibility, portals | `Formats/Culling.cs` (`NodeRef`) | — | yes (render; 16:9 audit target) | — |
| HUD / 2D layers | Visor frame, reticle, menus art | `HUD/HudInfo.cs`, `Formats/Frontend.cs` | `Export/Images.cs` (`ExportHudLayers`/`ExportHudObjects`) | yes (HUD) | — |

## World / gameplay data

| Format | Purpose | Parser (runtime) | Converter/tool | Runtime? | RE credit |
|---|---|---|---|---|---|
| Rooms | Room table + per-room model/entity/collision refs | `Metadata/Rooms.cs`, `Formats/*` | — | yes | — |
| Entities (placement) | Entity/enemy placement per room | `Formats/Entity.cs` (`EntityHeader`), `EntityClass.cs`, `EntityEnemy.cs`, `Enums.cs` (`EntityType`) | `Utility/RepackEntity.cs` (author/repack) | yes (load) | — |
| Collision | Room/entity collision geometry | `Formats/Collision.cs` (`EntityCollision`), `CollisionDetection.cs` | `Utility/RepackCollision.cs` | yes (physics) | — |
| AI personality | Bot/enemy AI parameters | `Formats/AiPersonality.cs`, `Entities/Players/PlayerAi.cs`, `Metadata/Enemies.cs` | — | yes (AI) | — |

## Audio

| Format | Purpose | Parser (runtime) | Converter/tool | Runtime? | RE credit |
|---|---|---|---|---|---|
| SDAT | Sound data container | `Formats/Sound.cs` (`SdatHeader`), `NcsfPlay/NC/SDAT.cs` | — | yes | — |
| SSEQ / SBNK / SWAR / SWAV | Sequenced music + banks + wave samples | `NcsfPlay/` (NC/ parse + Engine/ playback, clean-room 2026-10-05, see `docs/cleanroom/`; Player/ stream wrapper), `Sound/Music.cs` | `Formats/Sound.cs` `SoundRead.ExportSamples` (→ WAV) | yes (music) | CyberBotX NCSF; loveemu swav2wav |
| SFX | Sound effects (MPH + First Hunt) | `Sound/Sfx.cs`, `Formats/Sound.cs`, `Formats/FhSound.cs` | `SoundRead.Export*` (WAV) | yes (SFX) | loveemu swav2wav |
| ReplayGain | Loudness normalization for NCSF playback | `NcsfPlay/ReplayGain/` (used by `Player/NCSFFile.cs`) | — | yes (music) | — |

## Video

| Format | Purpose | Parser (runtime) | Converter/tool | Runtime? | RE credit |
|---|---|---|---|---|---|
| ActImagine VX | Full-motion cutscenes | `Formats/Vx/` (`VxDecoder`, clean-room 2026-10-05, see `docs/cleanroom/`); playback `Formats/Movie.cs` | `MphRead.Tools -movietest` | yes (cutscenes play on device) | format information: Gericom ffmpeg patch; CharlesVanEeckhout actimagine decoder (MphRead's original decoder; ours is written from `docs/cleanroom/B_movie_spec.md`) |

## Text

| Format | Purpose | Parser (runtime) | Converter/tool | Runtime? | RE credit |
|---|---|---|---|---|---|
| Strings / text | In-game strings, fonts | `Strings.cs`, `MphRead.Text` | — | yes | — |

## Save data (our format, not a ROM format)

| Format | Purpose | Read/write | Notes |
|---|---|---|---|
| Save (JSON) | Player progression / sim state | `GameState.cs` via `System.Text.Json` | Gets an explicit `formatVersion`; **sim-state only** — see `ARCHITECTURE.md` §2. Presentation prefs live in a separate settings file. |

## Summary status

- **Parsing (load):** complete for every gameplay-relevant format — this is the
  value MphRead brings and why we forked. Nothing needs to be re-derived for M0.
- **Converters/exporters:** COLLADA/PNG/WAV/Blender exporters exist and are
  **desktop-tool-only** — they move to `tools/` in the split and never ship in the
  APK.
- **Runtime extraction path:** ROM → (LZ10 decompress) → (ARC extract) → per-`Ver`
  asset tree, all present in `Utility/{Extract,Compress,Archive}.cs`. The M0
  Android wrapper (see `docs/M0_survey_and_split_plan.md`) drives this from a
  first-launch UI into app-private storage.
- **ActImagine VX cutscenes:** decided, they play on device. The decoder was
  replaced with a clean-room one on 2026-10-05 (`docs/cleanroom/README.md`).
