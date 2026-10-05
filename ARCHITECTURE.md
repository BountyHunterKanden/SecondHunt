# ARCHITECTURE — mph-recomp

> Status: M0 foundation draft (kit v0.1.0 → v0.2.x). This document encodes the
> **non-negotiable structural rules** from the planning kit §6 as enforceable
> engineering constraints. Every change to this repo is expected to conform. If a
> change cannot conform, that is a design discussion, not a silent exception.
>
> This is a fork of **MphRead** (github.com/NoneGiven/MphRead, MIT © 2020
> NoneGiven). The upstream `LICENSE`, its README **Acknowledgements** /
> **Special Thanks** chains, and all copyright notices are preserved verbatim and
> must never be modified or removed. See `NOTICE-fork.md` (added, not a
> replacement for `LICENSE`) for the fork's own attribution posture.

---

## 0. What this project is (one paragraph)

An unofficial, legally-clean, highly-moddable native **Android** reimplementation
of *Metroid Prime Hunters* (Nintendo DS). It ships **zero game assets**: on first
launch the user points it at a ROM they legally own, and assets are extracted to
app-private storage. Default presentation is **single-screen "Trilogy" style**
(twin-stick, in-viewport HUD), baked into the core — not the DS dual-screen
layout. Primary device: **AYN Odin 2 Portal**.

---

## 1. The sim / presentation split (Rule 1 — the spine)

The codebase is split into two layers with a **one-way dependency**:

```
  presentation  ──depends on──▶  simulation
  (may read sim)                 (must NOT know presentation exists)
```

**Simulation layer** — the authoritative game: entity state, physics, collision,
AI, weapons, enemies, bosses, progression, RNG, and **saves**. It runs
identically no matter how the game is drawn or controlled. It must not reference
any camera, HUD, input-scheme, screen-layout, or renderer type.

**Presentation layer** — camera, HUD, reticle, input schemes, screen layout,
renderer, audio output. It observes and issues intent to the sim; it never *owns*
game state.

Why this is non-negotiable: **Legacy Mode (M6)** is defined as *a second
presentation layer over the identical sim*. And **save compatibility (Rule 6)**
falls out for free only if saves live entirely in the sim. Break this boundary
and both future milestones break with it.

Enforcement (to be wired in M1): the sim assembly/namespace must not compile
against presentation types. Target end-state is a project or analyzer boundary
(`MphRecomp.Sim` cannot reference `MphRecomp.Presentation`). Until the physical
split lands, treat the boundary as a review rule: **no `Renderer`, `Camera`,
`Hud*`, input-scheme, or GL type may be referenced from sim code.**

## 2. Mode-agnostic, versioned save format (Rule 2 + Rule 6)

- Saves already serialize via `System.Text.Json` (see `GameState.cs`). The fork
  keeps JSON and adds an explicit, monotonic **`formatVersion`** at the document
  root. A loader migrates older versions forward; it never guesses.
- The save document may contain **only simulation state**. Forbidden fields (a
  non-exhaustive tripwire list): camera mode, HUD layout, chosen input scheme,
  gyro on/off, screen/aspect settings, control bindings, renderer options. Those
  are presentation preferences and live in a **separate** settings file that
  saves never touch.
- Consequence guaranteed by construction: a save produced under Trilogy
  presentation loads byte-identically under Legacy presentation and vice-versa,
  because neither presentation ever wrote to it.
- Spec detail (fill in during M0 execution as the sim state is inventoried):
  - `formatVersion` : int, starts at `1`.
  - `game`         : sim state (progression, inventory, story flags, position,
    unlocked hunters/weapons, difficulty/version tag).
  - No `presentation` / `ui` / `input` keys — enforced by a serialization test.

## 3. Repo split: slim runtime core vs. desktop tools (Rule 3)

MphRead grew from a model viewer and carries desktop-only tooling the Android
build must never contain (COLLADA/Blender exporters, PNG/WAV exporters, repackers,
analyzers, test harnesses, console menus). The fork separates:

- `runtime/` — everything that ships in the APK: sim + presentation + the format
  parsers and the runtime asset **extractor**.
- `tools/`   — desktop-only: exporters, repackers, analyzers, the console
  front-end, test/dev harnesses.

The concrete file-by-file classification and the **delete-or-relocate** task list
live in [`docs/M0_survey_and_split_plan.md`](docs/M0_survey_and_split_plan.md).
No files are moved until that plan is reviewed.

## 4. Data-driven engine (Rule 4 — a standing practice, not one late task)

Gameplay constants currently live hardcoded in `Metadata/` (`Weapons.cs`,
`Rooms.cs`, `Enemies.cs`, `Player.cs`, …). The standing rule for **M2–M4**:
**whenever a milestone brings a system online, extract that system's constants at
that moment** into external definition files the engine loads and mods can
override. Weapon stats + combat constants come out in M2; enemies, bosses,
progression, physics constants in M3. The data tier (moddability tier b) is these
files; it is the difference between "reskinnable" and "anything imaginable."

## 5. Zero-allocation main loop (Rule 5)

GC hitches are the one real C# risk on-device and this discipline neutralizes
them. From the first line of new engine code: pooled objects, `struct`s where it
matters, no per-frame heap garbage in the update/render loop. **Exit criterion
carried from M1: per-frame allocation count in the main loop == 0.** New hot-path
code that allocates per frame is a defect, not a style nit.

## 6. GPU-lean renderer (Rule 6 of §6 / Requirement 2)

The GLES port (M1) modernizes MphRead's renderer: static geometry uploaded **once**
to VBOs (zero per-frame re-uploads), batched draws, sorted/minimized state
changes, and first-class **resolution + framerate scaling** so the device does
exactly as much work as asked. Prefer **direct GLES bindings over ANGLE**
(Silk.NET vs. hand-rolled bindings is an M1 decision). Widescreen (16:9 over the
game's ~4:3 authoring) requires an FOV / frustum-culling / portal-visibility audit
so geometry does not pop at screen edges.

---

## Moddability model (four tiers — the payoff of the rules above)

- **(a) Asset tier** — a layered virtual filesystem; mod folders override the
  extracted asset cache. Mods never touch the ROM or the cache; they overlay it.
- **(b) Data tier** — the definition files from Rule 4. The effort center.
- **(c) Script tier** — **MoonSharp** (pure-C# Lua) hooks on events, entity
  lifecycle, input, HUD, with an API that can spawn/modify/query the sim.
- **(d) Plugin tier** — full compiled C# assemblies for total conversions.

Plus a **baked-in mod manager UI** (enable / disable / order). All four tiers land
in M4; the data tier already exists from M2–M3.

## Tech stack (fixed decisions)

C# / .NET 9 (`net9.0-android`, AOT to native ARM) · OpenGL ES (direct bindings
preferred) · OpenAL Soft · **MoonSharp** Lua (not LuaJIT — no native toolchain on
Android) · APK pipeline reused from sibling **buusfury-recomp**: zipalign +
keytool + apksigner with **V1+V2+V3** signatures (V1-only self-signed APKs fail to
install on Odin-family devices — proven empirically).

## The rules as a review checklist

1. Does sim code reference any presentation type? → reject.
2. Does the change write presentation state into a save? → reject.
3. Did a desktop-only tool leak into `runtime/`? → reject.
4. Did a system come online without its constants extracted to data? → follow-up.
5. Does new main-loop code allocate per frame? → reject.
6. Does the renderer re-upload static geometry per frame? → reject.
7. Was a license text, copyright notice, or acknowledgement modified/removed? → reject.
