# Map editor ("MAP MAKER"): plan

Status: **E0 built and tried 2026-10-23. Decision: cubes, plus add brushes at E4, carve brushes later.** Owner asked for an in-game map editor:
- Sauerbraten-style blocks, usable to build a map from scratch.
- Blender-file import for meshes.
- A hybrid where imported maps open in the same editor.
- "The most user friendly, intuitive UI and controls" in Second Hunt's style.
- Keyboard and mouse first, plus touch (with a real mouse/trackpad option) and gamepad.

Sources:
- Research on this date: Sauerbraten editref, Fruity Prime's MapGen (private repo), and a UX survey of ~20 editors and builders.
- A codebase survey: file references below.

Open questions are in section 11; decisions get written back here.

---

## 1. What the player gets

A **MAP MAKER** entry in the front end. From it the player can:

1. **Start from scratch**: an empty world with a floor, a spawn, and the grid.
2. **Start from an imported map**: a Blender file (`.glb`/`.gltf`, `.dae`, `.obj`), or an arena from a game they imported (Hunters, First Hunt, Echoes). It becomes a **locked base layer** to build around and on.
3. **Edit in the game**:
   - Fly around, push and pull blocks, slope corners, paint textures, paint volumes (lava, water, invisible wall, kill zone).
   - Place spawns, items, jump pads, teleporters and lights.
4. **Press PLAY** to drop into the map at the cursor as your hunter, with optional bots. Press again to come back to exactly where you were.
5. **Save**, then pick the map in any local or LAN match like any other arena. Online sharing comes later (section 9).

## 2. Architecture (where code lives)

Follows the shared app layer + thin shells rule.

| Layer | Contents | Project |
|---|---|---|
| **Map document** | Layers: Blocks (octree), Imported meshes (locked, per-piece hide/delete), Volumes, Markers, Settings (name, sky, fog, music, kill height, modes). Undo history. Versioned save file. | `MphRecomp.Core/Arenas/Editor/` |
| **Mesher** | Octree -> triangles. Sauerbraten edge model, 8 steps per edge. Hidden faces culled, coplanar faces merged (10-25: `BlockMesher.Build(..., merge: true)` for the picture, the counter and the compile; whole flat faces in one plane with one texture join into rectangles of at most 64 units; picking keeps the per-leaf triangles; a blank starter map is 12 triangles, was 4352 before the merge). Rebuilds in chunks so an edit costs milliseconds. | Core |
| **Compiler** | Document -> room files through the existing writers. Then `Metadata.AddHostRoom` with its own id range (1201+). | Core |
| **Importers** | glTF (new reader), Collada (`Core/Assets/DaeModel.cs`, exists), OBJ (new, small), game arenas (from the import folders). Later: Quake brushes -> blocks. | Core |
| **Editor screen** | Camera, cursor, tools, ghost previews, gizmos, panels, hint bar. | `MphRecomp.App/Editor/` (an `IGlScreen`) |
| **Pointer layer** (new) | One cursor abstraction fed by mouse, direct touch, virtual trackpad, gamepad snap-cursor, or gyro. Gives a ray into the world. | App, with raw events from each shell |
| **UI** | `UiDrawList` + `MenuFont` + ROM widget art, drawn by `UiGlDrawer`. This is the one UI path both platforms share. | App |
| **Shell plumbing** | Keyboard, mouse wheel, raw touch points, gamepad (already partly in `DesktopInput.cs` / `MatchTouchOverlay.cs`). | `SecondHunt.Desktop`, `MphRead.Android` |

How the compiler builds each room file:

| Output | Built by |
|---|---|
| `_Model.bin` / `_Anim.bin` | `ArenaModelWriter.Write` |
| `_Collision.bin` | `RepackCollision.PackMphCollision`, one entry per triangle with terrain and flags |
| `_Ent.bin` | `Repack.PackEntities` |
| Bot nav | `ArenaNav.Build` / `Write` |
| Thumbnail | `ArenaThumbnail` |

### The map file

`<name>.shmap` is a zip. A version number is in it from day one, and old maps upgrade on load.

It contains:
- `map.json`: settings, markers, volumes, imported-piece edits.
- `blocks.bin`: the octree.
- `meshes/`: imported user meshes.
- `textures/`: user textures, if any.
- `thumb.png`

**Game-arena bases are stored as a reference, not copied.** A map stores "base = Echoes Pipeline" plus the edits. It never stores Nintendo geometry, so it is safe to share: the receiver rebuilds the base from their own imported game. This matches the ship-safe rule that everything comes from user imports.

## 3. Blocks (Sauerbraten model, friendlier)

**The world is an octree in a cube.**
- The world is 2^N units on a side. The proposed default is about the size of the largest MP arena, to be checked against Echoes/MP arenas in E1.
- Grid sizes run from big rough blocks down to fine detail. The grid size is always shown on screen.

**Each block can take these edits:**
- Solid or empty.
- Its 12 edges, each with two 0-8 steps, which give slopes, wedges and bevels.
- 6 face textures.
- An optional material volume.

**Push/pull:**
- Pushing or pulling a selected face adds or removes one grid step.
- A smaller grid splits blocks automatically.
- A "merge" pass (Sauerbraten's remip) joins identical children back into bigger blocks.

**Corners:** pushing a corner, or a whole face's corners, makes slopes. Handles show which corner is live.

**Volumes:** lava, water, acid, invisible wall, bullet-through wall, kill zone, no-bot zone. They are drawn as tinted boxes in the editor and compile to collision terrain and flags (`Terrain`, `Beams`).

**Heightfield brush** (later): Sauerbraten's terrain mode, with round, square and smooth brushes for hills.

## 4. Imported meshes (the hybrid)

An imported mesh becomes **pieces**: one per Blender object, or per material for single-object files.

Each piece can be:
- shown or hidden
- deleted
- moved, turned or scaled as a whole
- assigned a surface type

A piece cannot be reshaped vertex by vertex.

**Surface type from the material name** (the Fruity convention, which works in Blender with no addon):
- `lava`, `water`, `ice`, `nobeams`, `invisible`, `noclip`
- Combined forms such as `metal_nobeams`.
- Anything else is plain solid.

**Units:** 1 Blender metre = 1 Hunters unit by default, with a scale slider on import. A preview shows a Samus-height figure beside the mesh before you accept.

**"Convert to blocks"**, per piece (E8): voxelizes at the chosen grid size and makes slopes from the edge model.
- Good for boxy maps and Quake brushes.
- Curved meshes come out stair-stepped, and a preview shows that before you accept.
- The original piece is kept, hidden, so the convert can be undone.

**Blender question answered:**
- Blender opens glTF, OBJ, FBX and Collada natively. Our cartridge, Echoes and First Hunt rooms can be exported to Collada with MphRead's exporter, edited in Blender, and brought back.
- Quake `.map`/`.bsp` need a Blender addon. Our own Quake path (E8) skips Blender.

## 4b. Elements, panels and Hunters-size walls (owner 2026-10-23)

### Elements library

A browsable list (pictures, search, by game/room/kind) of every map object in the player's imported games. The player grabs one and places it in their map.

**Two kinds:**
- **Object elements**: things that are entities in the game.
  - Doors, force fields, platforms, jump pads, teleporters, crates (Echoes), artifacts and octolith bases, item spawns.
  - Placed as markers with full properties. They compile through `Repack.PackEntities`, as today.
- **Geometry elements**: pieces cut out of a room model.
  - Glass windows with the sweeping animated texture, pillars, consoles, railings, light panels.
  - Cut per node/mesh/material.
  - They keep their texture animation (UV scroll, palette/frame anims from `_Anim.bin`). The textured renderer must play these.

**Stored by reference**, never copied: game + room + node/mesh ids. Each player rebuilds the element from their own import, which keeps it ship-safe.

**Wall-fit elements** (windows, doors, wall panels): they know their "socket", meaning opening size, wall thickness, and the wall style they came from (texture + thickness).
- Dropping one on a block wall carves the opening automatically and fills it.
- A ghost shows the fit: green, or red with a reason ("wall too thin", "not enough room").
- Grabbing an element also offers its **source wall style** as a paint/wall preset, so the wall it sits in matches where it came from.

**Ways to grab:**
- From the browser.
- An eyedropper on a game arena loaded as a base.
- "Copy to my map" from any imported room viewed in the editor.

### Panels (flat walls and floors)

- A panel is a thin block that lives in the block grid: it snaps to a cell face and has a thickness setting, down to a sliver.
- It is editable with every block tool: push/pull, corners, paint, carve.
- Under the hood it's a block with its edges squashed to a slab. This is the Sauerbraten edge model, so it needs no separate system.
- Panels can be drawn by dragging out a rectangle on any grid plane.

### Hunters-size kit (match the base game's dimensions)

**Survey (E1):** measure the base game's rooms:
- door openings for each door type (regular, morph ball, boss, thin)
- the walls doors sit in (height, thickness)
- corridor widths, ceiling heights, step and ledge heights, jump-reachable heights

**Pick the default grid** so these are whole numbers of grid cells. A standard door wall is then N x M blocks with no fiddling.

**A "Hunters kit" palette** of ready-sized prefabs, all made of normal editable blocks and panels plus element sockets:
- door wall (per door type)
- corridor section
- room shell
- ramp at standard slope
- step set

### Surfaces and hazards (owner 2026-10-23)

Every surface the engine knows is a paintable **surface type**, separate from the texture. Painting lava rock can still be plain rock underfoot, or real lava.

**Surface types:** every `Terrain` value (`Formats/Enums.cs:192`):
- Metal, Orange/Green/Blue holo, Ice, Snow, Sand, Rock, Lava, Acid, Gorea.
- Each brings its own footstep sound, beam impact and splash.
- Ice brings its slipperiness.

**Hazards, the ones that hurt:**
- **Lava** (`Terrain.Lava`, the player's `OnLava` path, Spire immune).
- **Acid / damaging** (the `CollisionFlags.Damaging` flag, the player's `OnAcid` path).
- Damage amounts stay vanilla. The editor sets the same terrain and flags the game rooms use, checked against how vanilla lava and acid rooms set them, so they hurt exactly like the base game, and bots already avoid lava (`PlayerAi`).

**Other dangers:**
- **Kill height** (falling out of the map) is a map setting with a visible plane in the editor.
- **Kill / no-bot zones** are volumes.

**Hazard elements** in the elements library:
- Ready-made hazard pieces from the imported games: lava pools and flows, acid pools, with their animated textures, glow and sound.
- They place like any element and carry their surface type with them.

**A clear look in the editor:**
- A "show surfaces" overlay tints every face by its surface type.
- Hazards get a warning stripe.
- PLAY's check lists spawns that sit on or next to a hazard.

### Everything in a map is selectable (owner 2026-10-23)

Rule: **anything that exists in a game map can be selected, browsed in the elements library, placed, and edited in your map. Enemies are the only exception, and they come later.**

**Every placed-thing type** (`EntityType`, `Formats/Enums.cs`), each with a property panel built on the 26 existing `*EntityEditor` classes:

| Type | |
|---|---|
| Platform | including moving platforms, with their paths drawn |
| Object | scenery objects and effects |
| PlayerSpawn | |
| Door | |
| ItemSpawn | |
| TriggerVolume and AreaVolume | links drawn to what they fire |
| JumpPad | arc drawn |
| MorphCamera | |
| OctolithFlag, FlagBase, NodeDefense | |
| Teleporter | pairs linked |
| LightSource | |
| Artifact | |
| CameraSequence | |
| ForceField | |
| Halfturret | |

**Room geometry pieces** are covered by the elements library above.

**Portals:**
- **Teleporters** (visible portals between two points) are linked pairs, drawn live, with a "walk through" preview.
- **Room portals** (the invisible doorways the game uses to split a room into parts and skip drawing the ones you can't see) are needed for big maps on the Odin, since `RepackCollision.cs:82` doesn't compute them yet:
  - generated automatically at doors and narrow openings
  - viewable and movable in a "show portals" overlay
  - placeable by hand

**Hologram barriers and other pass-through walls:** any face or piece can take the collision behaviour flags the game rooms use (`CollisionFlags`, `Formats/Collision.cs:357`). They are offered as presets:
- **Hologram barrier** (walk through it, it stops shots): `IgnorePlayers`, usually with a holo surface type and an animated holo texture.
- **Shoot-through wall**: `IgnoreBeams`.
- **Beam-reflecting wall**: `ReflectBeams`.
- **No scan**: `IgnoreScan`.

The holo barriers from the base game also appear as ready-made elements. The editor shows pass-through faces see-through with a dotted outline, so they're obvious.

**Room layers and flags (later, custom campaign):**
- Every entity already carries a `LayerMask` (`Formats/Entity.cs:21`): which layer of the room it belongs to. Multiplayer uses layers for player count and mode; the campaign uses them for story states.
- The editor will show layers as a list with checkboxes ("show layer 3 only") and let you set each object's layers.
- Later, for the campaign maker: story/event flags (set by triggers, artifacts, bosses; read by doors and layer switches) as an editable list, with "set by" / "used by" links drawn in the editor.
- In multiplayer maps the mode layers are filled in automatically from the map's mode markers (section 8), so players never have to think about them.

**Enemies (later):** EnemySpawn and enemy instances use the same marker system, so adding them later is the same pattern. That, plus doors, triggers, camera sequences and artifacts, is the base for a **custom campaign maker**. Noted as a future phase after sharing (E11+); not planned in detail now.

## 5. Look and rendering (the biggest new piece)

**Today's limitation:** the compiled DS room model is untextured and vertex-coloured. Textured arena looks (and skies) only come from the Retro HD writer.

**Plan:**
- **Editor view:** a new textured block renderer in App. Chunked meshes, rebuilt live, with simple baked corner lighting (ambient occlusion + light markers, as Fruity does) and a fog/sky setting.
- **Play view:** the same renderer, or a generalized HD bundle writer, so the arena looks the same in a match as in the editor. Decided in E1 after reading `ArenaHdWriter` / `CampaignArena`.
- **Texture palette (owner 10-23):**
  - **Default set:** MPH textures pulled from the player's own Hunters import at import time. Nothing ships. One texture is picked per category we'd want: floor plate, wall panel, trim, grate, rock, metal, glass, glow strip, lava, water, and so on. The list is drawn up in E1 by surveying the room textures.
  - **More textures:** Echoes and First Hunt textures join the palette when those games are imported.
  - **Tiling, not checkerboards:**
    - UVs are world-space and planar per face (triplanar on slopes), so a texture continues seamlessly across neighbouring blocks instead of restarting on each one.
    - Each texture gets a scale setting (world units per repeat), and the default is picked per texture.
    - Default-set picks must tile cleanly: we check the edges, and skip atlas or one-off decal textures.
    - Later options: per-face offset/rotate in Paint, and random 90-degree rotation for rock and ground textures.
  - **Custom hand-made set:** a plan for later. The owner draws it by hand; not now.

## 5b. E1 results (built 2026-10-23, PC)

**The document.** A map is a `.shmap` zip: `map.json` (`MapDocument`: version, name, camera, grid) and `blocks.bin` (the block tree with per-face texture ids, "SMB2"). It is written to `name.tmp` and moved over the target, so a crash can't leave half a file. A file from a newer build is refused; unknown zip entries are ignored; `Upgrade()` is where version steps go. E0's `map.smm` ("SMM1") and the first block format still load. Code: `Core/Arenas/Editor/MapDocument.cs`; the screen saves with Ctrl+S only (10-25: PLAY no longer saves, the status line shows * UNSAVED).

**The compile.** `BlockMesher` -> triangles -> `ArenaModelWriter` (DS model, textured, scrolling) + `RepackCollision` (one polygon per triangle) + entities + `Metadata.AddHostRoom` (room 1201 "MAP_MAKER_TEST"), played as Solo Battle. The test map (a few rooms and ledges) compiled to 4588 triangles and a 239 KB model.

**Default texture set** (all taken from the player's own import, nothing ships; `TextureLibrary`): 1 Floor plate, 2 Floor tile, 3 Grating, 4 Rock, 5 Rust plate, 6 Trim stripe (MP7 Processor Core); 7 Wall panel (MP1 Sanctorus); 8 Lava (MP7, slides in S); 9 Gold water (MP3 Proving Ground, slides in T); 10 Ice, 11 Snow (MP9 Cryochasm); 12 Glow strip, 13 Amber glow, 14 Sand stone (MP3). There is no blue water in the MP room textures (MP3, MP5, MP9 checked), so the water is the real gold liquid and is named for it. Unpainted faces use the auto rule: floor = Floor tile, wall = Wall panel, ceiling = Floor plate.
- UVs: planar along each face's axis, anchored at the world origin, 16 texels per world unit (`TexelsPerUnit`), so a texture runs on across neighbouring blocks. The writer subtracts whole repeats per triangle to keep texel coordinates small.
- Sliding textures use a texcoord animation group (240 steps = 8 s, whole repeats per loop, so the loop is seamless) in `mm{n}_Anim.bin`; the editor mirrors it with a shader offset. Verified in a Solo match: lava slides sideways, water vertically.

**Hunters dimensions** (`-mmsurvey dims`, from game constants and the 15 MP rooms' collision):
- Doors: every type has a 4.2-wide opening (port half-width 2.1). Height 3.4 (standard, morph ball, thin) or 6.4 (boss). Lock offsets 1.4 / 0.7 / 3.5 / 1.4, radii 2.4 / 1.0 / 3.5 / 2.0.
- Hunters: collision radius 0.5 (biped), alt (morph ball) radius 0.4-0.63; body spans -0.5 to +1.1 around the position. Jump apex 2.54 for all eight (jump speed 0.30, gravity -0.019 per frame).
- Rooms: footprints 23-102 wide, up to 135 deep; 698-2922 collision polygons per room (mean about 1700).
- Ceiling clearance over the big floors: mostly 4.5 (70 samples), 3.5, 6.0, 7.5-10, with a tail to 21 and more. A 4.5 ceiling is the common indoor height; a door plus a head of room.
- Wall to wall: the narrow end is 1.5-3.5 (corridors, 3.5 the commonest), the bulk 5-9.5, open halls 12-48.
- Gaps between floor levels in a room: 0.25, 0.5, 0.75 the commonest; 1.0 and up thin out to 7.75. Ledges above 2.5 can't be jumped.

**Default grid: 1 unit** (grid index 2, was 2 units in E0). One unit is the player's diameter and the smallest morph ball tunnel; 4 grid steps. Whole numbers at that grid: jump ledge 2, corridor 4, standard ceiling 4-5, room 8+. The door opening 4.2 x 3.4 doesn't land on whole units, so the E4 door-wall kit cuts a 4.25 x 3.5 gap (17 x 14 steps, 0.25 grid) which covers the door port with at most 0.1 to spare; 6.5 high for a boss door. Walls: no shipped door has a fixed wall thickness the model needs, 1 unit is plenty.

**Budgets.** Found 10-25 on the Odin: the original `wc01` collision format stores a face's start in the point-index list as 16 bits and each triangle takes 4 entries (3 + the closing point), so past 16,383 triangles the indices wrapped and the floor stopped being solid (a 30k map: smooth to draw, fell through the floor, only the pillars solid). Fixed 10-25 (owner GO, idea from Fruity Prime) with a wide variant `wc32`: the high 16 bits of a face's point start go in the header field PaddingA (zero in every cartridge file) and grid entries are 8 bytes (32-bit count + start). `RepackCollision` writes `wc32` only when the point-index or grid list passes 65,535; vanilla-size files stay `wc01` byte for byte, and the reader (`Formats/Collision.cs`) takes both. Faces, points and planes are still ushort-indexed, so the hard limit is now `MapCompiler.TriangleHardLimit` = 65,535 triangles; past it only the render-only perf option (collision thinned) builds. The packer also got dictionary point/plane lookups and per-face cell ranges (same output, 25 s -> about 1 s at 68k). The editor and compile give a heads-up past 30,000 triangles (`MapCompiler.TriangleSoftLimit`, set 10-25 from the measurements below; was 15,000, and before that 3000 = the biggest DS room, 2922 collision polygons, but the imported Echoes arena M01 already plays with 14,377 triangles). Measured on PC (blank map + n x n F9-style pillars, no textures): ~10k merged triangles compile in about 3.5 s, ~15k in 4 s; the Odin (AYN, 10-25) ran every perf map up to 60k (about 68k triangles) smooth, with fast loads and smooth morphs once wc32 was in; a GTX 1650 PC holds a steady 60 fps at 13k, 42k and 68k (GPU 1-2 ms steady, max 3 ms, one-off 30-60 ms first-frame warm-up). The Intel UHD 630 was not measured (owner skipped it). The editor status line shows the merged count and an OVER THE LIMIT note (10-25), `LastBudgetWarning` and a console line remain. Where the Odin starts to stutter is measured in E3 (nothing is installed in E1). Hard limits met so far:
- 4.12 fixed point: no limit in practice. The writer picks a power-of-two model scale so every coordinate fits; the world is 128 units across.
- Texel range: texture coordinates are 12.4 fixed (about +-2048 texels); a single face larger than ~128 units of texture could clamp. Faces are far smaller than that.
- Texcoord translation LUT is 4.12 per step, so slides are exact to 1/4096 of a repeat.

**Open (E1 leftovers, not blocking):** the game's texgen None -> Texcoord overwrite for materials without texgen (we write the coordinates directly and it looks right); translucent A5I3 textures (alpha < 0.5 is discarded in the editor, kept in the model); per-texture scale setting (all textures are 16 texels per unit now).

**Owner requests from the E1 check (10-25, E2+ work):**
- Per-texture / per-face tiling size, so a texture can repeat across fewer, smaller blocks.
- A categorized texture library (walls, floors, glass, liquids, ...). Glass is missing; many MP textures are not in the 14.
- "Gold water" is really a wall texture laid over shallow recesses in campaign; make it a grabbable wall element once elements exist (windows etc.).
- Lava gets a damage toggle (vanilla lava hurts); the editor must know how lava works in game.
- An element library: pre-set elements made of several parts (window, lava pit, ...). Lava pit: grab it, place it, drag its edges and sides to shape it; the editor expands it like drawing a pit and slopes every bank down to the lava automatically (as in Alinos Gateway, where the ground dips off a slope wherever it meets lava); ground around it defaults to the texture it sits on.
- Slopes shallower than 45 degrees: one push is 1/8 of the block (about 7 degrees); chain neighbouring corners at 1, 2, 3 pushes, or use a bigger grid. A "set slope angle" tool may be worth it.

## 6. UI and controls

These rules are synthesized from Sauerbraten, SketchUp, TrenchBroom, Portal 2 Puzzle Maker, Mario Maker 2, Halo Forge, Fortnite Creative, Minecraft, Dreams, Townscaper, Trackmania and Valheim/Enshrouded.

### 6.1 Ten rules every screen follows

1. **A ghost preview before every action.** A see-through result shows at the cursor: green when it will work, red with a reason when it won't. Nothing changes until you confirm.
2. **A hint bar that always says what your buttons do right now.** It changes with the tool and the input device, and shows the right glyphs for keys, Xbox/Odin buttons or touch.
3. **No hidden state.** Grid size, current tool, current texture, snap step and layer always show in one compact top strip.
4. **Undo everything.** Every action, including settings and marker edits, with a history list. Autosave keeps snapshots too, so work can't be lost.
5. **Hard to fail.** Invalid actions refuse with a reason and never break the map. PLAY runs a check first (no spawn, holes to the void) and offers "go there".
6. **One verb per input.** No "hold a letter and scroll" combos. Power-user aliases (Sauerbraten's keys) are an opt-in profile.
7. **Never lost.** Frame the selection (F), return to the last edit, reset the camera, and a top-down overview map.
8. **Snap is visible.** A grid is drawn near the cursor. Axis locks are colour-coded (red/green/blue as SketchUp does). A drop line shows where a marker lands.
9. **Things place themselves sensibly.** Markers snap to the surface under the cursor and face away from walls. Connections (teleporter pairs, jump-pad arcs) are drawn live.
10. **Teach by doing.** A 2-minute interactive first-run lesson teaches one verb per step and can be skipped. A held "?" overlay shows every control, and hovering a tool shows its short description.

### 6.2 Look

- Second Hunt's front-end style: ROM font (`MenuFont`), the MARM panel and row widgets, the existing colours and sounds.
- Panels open on demand and are never permanently docked, so the 3D view stays clear.
- **Top strip:** tool, grid, texture, layer, budget meter.
- **Bottom:** the hint bar.
- **Left:** the tool rail. Big icons, 1-9 hotkeys.
- Radial menus for palettes (textures, markers, volumes).
- A side panel for properties: labelled fields with arrows and sliders, the same rows as the options menus.

### 6.3 Tools (the tool rail)

| Tool | What it does |
|---|---|
| **Build** | Push/pull faces, add or remove blocks |
| **Shape** | Push corners and edges for slopes |
| **Paint** | Textures per face, with eyedropper |
| **Volume** | Lava, water, invisible wall, and so on |
| **Place** | Markers: spawns, items, pads, teleporters, lights, octolith/flag bases per mode |
| **Select** | Box select, move, rotate, mirror, copy/paste, save as prefab |
| **Pieces** | Imported mesh pieces: hide, delete, move, surface type, convert to blocks |
| **Map** | Settings: name, sky, fog, music, modes, kill height |

### 6.4 Keyboard + mouse (default)

**Mode and camera**

| Input | Action |
|---|---|
| Tab | Editor menu |
| F5 | PLAY from the cursor; F5 or Esc comes back |
| Hold right mouse | Look around |
| WASD | Fly |
| Space / C | Up / down |
| Shift | Fast |
| Scroll while holding right mouse | Fly speed |
| F | Frame the selection |
| Home | Reset the camera |
| M | Overview map |

**Selecting and editing**

| Input | Action |
|---|---|
| Left click | Use the tool on the face under the cursor |
| Left drag | Box select / paint stroke |
| Shift / Ctrl | Add to / remove from the selection |
| Esc | Clear the selection |
| Scroll | Push/pull the selected face one grid step |
| Drag the face handle | Same as scroll, with a live distance readout; type a number + Enter for an exact distance |
| Alt + click | Select the block under the cursor instead of the grid-size box (Sauerbraten passthrough) |

**Grid, textures and palette**

| Input | Action |
|---|---|
| [ ] | Grid size |
| Middle click | Eyedropper: texture or volume under the cursor |
| T | Texture browser (pictures, search, recents) |
| 1-9 | Tools and hotbar |
| Q | Radial palette for the current tool |

**Transform and commands**

| Input | Action |
|---|---|
| R / Shift+R | Rotate 90 degrees |
| X | Mirror (axis follows the view) |
| Ctrl+C / V / X / D | Copy / paste / cut / duplicate; paste follows the cursor as a ghost until you click |
| Del | Delete |
| Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z | Undo, redo |
| Ctrl+S | Save |
| Ctrl+G | Save as prefab |
| H / Alt+H | Hide / show all |
| L | Lock |
| Hold ? | Every control |

Everything can be rebound through `ControlBinds`.

### 6.5 Gamepad (Xbox layout; the Odin pad is the same)

**Moving and pointing**
- Left stick flies, right stick looks.
- LT/RT go down/up, and L3 is boost.
- The **cursor is the screen-centre crosshair snapped to the face under it**.
- D-pad steps the selection one block, or nudges a held thing by one grid step.

**Main buttons**
- **A** uses the tool (place, push).
- **B** removes; hold it, with a progress ring, for big removals. It also goes back.
- **X** grabs the selection. Sticks then move it, bumpers rotate it by the snap step, and X drops it.
- **Y** opens the radial palette: stick + release.
- **LB/RB** cycle tools.

**Push/pull and shortcuts**
- Hold A + right stick up/down to push/pull, or tap RT/LT while a face is selected.
- **View** undoes; hold View to redo.
- **Menu** opens the editor menu.
- **R3** toggles the gyro cursor (optional, with a clutch).
- **L3+R3** or a menu item starts PLAY.

### 6.6 Touch: three modes, switched with a chip on screen (one tap)

**A. Direct** (default)
- Tap a face to use the tool, and drag to paint a stroke.
- Long-press with a ring removes.
- One finger on empty space/sky orbits, two fingers pan, pinch zooms.
- A magnifier appears above your finger after a moment, so the finger never hides the target.
- Push/pull is a big arrow handle on the selected face; drag it.

**B. Trackpad mouse** (your idea)
- The screen becomes a laptop trackpad driving a real cursor, with acceleration and an optional coast.
- Tap = click, two-finger tap = right click/menu, two-finger drag = scroll (push/pull).
- Tap-then-drag or long-press = drag (box select, grab).
- Three-finger drag = orbit.
- Best for precision.

**C. Split** (Minecraft style)
- The left thumb gets a fly stick, and the right side aims a centre crosshair.
- Use/remove buttons sit on the right edge.
- The default when a gamepad is attached and you touch the screen.

**Rules for all three**
- Touch and pad work at the same time, and the last one used owns the cursor.
- Buttons are at least 48 dp and sit in the thumb arcs. They can be moved and resized, reusing our touch-layout editing.
- Destructive actions need a hold or are one undo away.
- Undo, Tool, Palette and PLAY are always on screen.

## 7. PLAY (test) and back

**PLAY** compiles changed chunks, writes the room files, re-registers (`AddHostRoom`), and starts a Solo-arena match:
- At the cursor (or at a chosen spawn), as your hunter.
- Optionally with bots, which use `ArenaNav`, built in the background.

Returning restores the editor camera, selection and tool exactly.

**Speed:** there is no live geometry swap in the engine, so PLAY is a fresh scene. Targets: under 2 s on PC and under 4 s on the Odin for a mid-size map, measured in E3. If it's too slow, the fallback is to keep the match scene and hot-swap the room. That's a bigger job.

## 8. Fitting the game

- **Modes:** mode markers per mode (flag bases, octolith, nodes, bounty). The map lists which modes it supports, filled in automatically from its markers.
- **Budgets:** a meter for triangles, collision entries and markers. It warns before Odin performance or 4.12 fixed-point limits are hit; real numbers are measured in E1.
- **Large maps:** later, rooms/portals from block regions to cut drawing on the Odin.
- **Public builds:** editor rooms go through the import-folder path (`RecompSettings.PublicBuild` gates staged arenas). The whole MAP MAKER is dev-build only until the owner gates it public (Q6).

## 9. Sharing (later; where Fruity stops short)

1. Local + LAN: the host sends the `.shmap` (small, since bases are references) when a match uses a map a guest lacks.
2. Online: the same message over our relay/server. A map hash goes in the join check so nobody plays a different copy. Fruity has no such check.
3. Maybe a browse page.

## 10. Milestones

Each step ends with something the owner can try on the device.

| # | Milestone | Owner can try |
|---|---|---|
| **E0** | Throwaway prototype (PC): octree + mesher + fly cam + push/pull + PLAY in Solo, no UI polish. **Decides cubes-vs-brushes before anything else is built.** | Build a room, play it (BUILT + owner-tried 10-23, PC: `SecondHunt.exe --screen mapmaker`. **Owner decision 10-23: cubes stay the main tool; add-only brushes with presets (wedge, cylinder, arch, stairs: free angles, compiled into the same model + collision) join at E4; carve brushes (cutting blocks) later, only if wanted**) |
| E1 | Document + versioned `.shmap` + compiler to room files + textured block renderer (world-space tiling, texture animation) + MPH default texture set picked + Hunters dimension survey + budgets measured | Save/load, textured blocks (BUILT 10-23, PC: `SecondHunt.exe --screen mapmaker`; results in section 5b) |
| E2 | Full PC tool set: Build/Shape/Paint/Volume/Select, undo history, hint bar, top strip, rails/radials, SH look | Keyboard + mouse editor |
| E3 | PLAY polish: from cursor, bots, return-to-camera, pre-play check, timing on the Odin | Fast test loop |
| E4 | Add-brush Shape tool (presets, free rotate, face/edge drag, clip plane) + Place tool + mode markers + connections + Map settings + surface types + hazards (lava/acid/kill height) + surface overlay + panels + Hunters-size kit (from the E1 survey) + all non-enemy entity types with property panels + teleporter links + pass-through presets (hologram barrier, shoot-through, reflect) | Real playable arenas |
| E5 | Gamepad scheme + pointer layer (snap cursor, radials) | Odin with the pad |
| E6 | Touch: Direct, Trackpad and Split modes, magnifier, movable buttons | Odin/phone touch |
| E7 | Import: glTF/DAE/OBJ pieces, material-name surfaces, scale preview, game-arena bases by reference + geometry elements (cut from rooms, animated textures kept, wall sockets auto-carve) | Blender maps + arena bases |
| E8 | Convert-to-blocks + Quake brush import + heightfield brush + room portals (auto + manual) | Hybrid complete |
| E9 | First-run lesson, "?" overlay, prefabs, autosave snapshots | Polish |
| E10 | Sharing: LAN, then online + map hash | Play custom maps together |
| E11+ | Later: enemies + campaign rooms (custom campaign maker) + room layers editor + story/event flags | - |

## 11. Open questions for the owner

Owner answers 2026-10-23:
- **Platforms:** PC first (E0-E4), then gamepad (E5) and touch (E6). Every milestone still runs on the Odin.
- **Game-arena bases:** yes, stored by reference (never Nintendo geometry in the file).
- **Start:** E0 throwaway PC prototype first. Dev builds only; menu name MAP MAKER.
- **Textures:** the default set is MPH textures from the player's import (one per category), tiled with world-space UVs so neighbouring blocks don't look like a checkerboard. A hand-made custom set comes later, made by the owner; no new art now.

## 12. Code references (survey 2026-10-23)

**Arena pipeline**
- Writers and registration: `MphRecomp.Core/Arenas/EchoesArena.cs` 300-379 (collision/entities), 603-625 (writers), 945-1066 (register).
- `Metadata.AddHostRoom` (`MphRead/Metadata/Metadata.cs:1598`), `RemoveHostRoom` (`:1612`).
- `ArenaModelWriter.Write` (`ArenaModelWriter.cs:23`), `RepackCollision.PackMphCollision` (`RepackCollision.cs:87/93`), `Repack.PackEntities` (`RepackEntity.cs:26`), `ArenaNav.Build/Write` (`ArenaNav.cs:168/541`).
- `MatchArenas.All` (`Multiplayer/MatchArenas.cs:36`).

**Match start**
- `CampaignHost.StartMatch` (`Multiplayer/MatchStart.cs:16`), `SceneSetup.LoadGame` (`MphRead/SceneSetup.cs:15`).
- Solo arena: `MphRecomp.App/Campaign/SoloArena.cs`.
- Free camera: `MatchReplay.FlyFreeCam` (`:1088`), `Scene.HostView`.

**UI**
- `UiDrawList`, `UiGlDrawer` (`App/Ui/UiGlDrawer.cs:88`), `MenuFont` (`Core/Frontend/MenuText.cs:67`), `MenuEngine.TryRect` (`:367`), `PauseOverlay`.

**Input**
- `PadState` (`App/Platform/LaunchArgs.cs:27`), `DesktopInput.cs`, `ControlBinds.cs:32`, `InputMap.cs`, `MatchTouchOverlay.cs`.
- No pointer abstraction exists yet.

**Mesh and editing references**
- Mesh import: `Core/Assets/DaeModel.cs`. Export: `MphRead/Export/Collada.cs:43`.
- Marker pattern: `Arenas/PickupPlacement.cs` (live `AddEntity`, json save).

**Risks**
- No live room swap.
- Per-room side tables and caches keyed by name/id (refresh together).
- The DS model is untextured (new renderer needed).
- 4.12 limits.
- No portals means no culling.
- Nav build time on big maps.
