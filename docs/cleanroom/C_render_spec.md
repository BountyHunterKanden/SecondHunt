# Clean-room spec C: scene shading and translucency layering

Written 2026-10-04 by the spec author (who has read the code being replaced). The implementer works only from this file,
the files named under "What you may read", public GL/GLSL documentation and GBATEK
(https://problemkaputt.de/gbatek.htm, "DS 3D" chapters). Nothing here is code from the replaced implementation: it
states required behaviour as maths and GL state, and the names are new.

Goal: new source for (1) the scene vertex and fragment shaders, desktop GLSL 1.20 and GLSL ES 3.00, and (2) the
per-frame layered draw that composites solid, decal and translucent surfaces with DS-style "same polygon ID doesn't
blend over itself" behaviour. The output must be **identical** to the current renderer's, so the rules below are exact,
including the order of floating-point operations.

---

## Part 1. Shaders

### 1.1 Interface (fixed; the C# side binds these names)

Uniforms, identical names in both shader pairs:

| Name | Type | Meaning |
|---|---|---|
| `u_lit` | bool | lighting on for this draw |
| `u_textured` | bool | sample the texture |
| `u_vertexColors` | bool | use per-vertex colours (off = white) |
| `u_fogOn` | bool | apply fog |
| `u_light0Dir`, `u_light1Dir` | vec3 | light directions, used as supplied by the C# side |
| `u_light0Color`, `u_light1Color` | vec3 | light colours |
| `u_matDiffuse`, `u_matAmbient`, `u_matSpecular`, `u_matEmission` | vec3 | material colours |
| `u_fogColor` | vec4 | fog colour |
| `u_fogNear`, `u_fogFar` | float | fog start / end, in window depth (0..1) |
| `u_projection`, `u_view` | mat4 | projection, view |
| `u_billboard` | mat4 | billboard rotation (identity when not billboarding) |
| `u_texMatrix` | mat4 | texture matrix |
| `u_texgen` | int | 0 none, 1 texcoord, 2 normal, 3 vertex |
| `u_bones` | mat4[32] | per-vertex transform palette |
| `u_texture` | sampler2D | unit 0 |
| `u_colorOverrideOn`, `u_colorOverride` | bool, vec4 | force a colour |
| `u_paletteOverrideOn`, `u_paletteOverride` | bool, vec4 | replace the texel's rgb |
| `u_matAlpha` | float | material alpha, 0..1 |
| `u_polyMode` | int | 0 modulate, 1 decal, 2 toon/highlight (3 shadow is drawn like 0) |
| `u_toonRamp` | vec3[32] | toon table |
| `u_alphaPass` | int | **ES only.** 0 keep all, 1 keep only fragments with alpha exactly 1, 2 keep only alpha < 1 |

Vertex inputs:
- **Desktop (GLSL 1.20, compatibility profile):** built-ins. Position `gl_Vertex`, normal `gl_Normal`, colour
  `gl_Color`, texcoord `gl_MultiTexCoord0.xy`. The **bone index** rides in `gl_MultiTexCoord0.z`: index = integer part
  (truncation) of that value. Output `gl_FragColor`.
- **ES (GLSL ES 3.00, `precision highp float; precision highp int;` in both stages):** `layout(location = 0)` vec3
  position, `1` vec3 normal, `2` vec4 colour, `3` vec2 texcoord, `4` float bone index; index = integer part of
  (value + 0.5). Names of attributes and varyings are yours.
- **ES colour sentinel:** an input colour whose alpha is greater than 1.5 means "no per-vertex colour": use
  (`u_matDiffuse`, 1) as the input colour instead. Everything below uses the colour after this substitution. (Desktop
  gets the same effect from GL's current-colour state; no sentinel there.)

### 1.2 Vertex stage

Let P = position, N = normal, C = input colour (rgba), T = texcoord (s, t), B = `u_bones[index]`.

1. Model matrix **M = B × u_billboard**.
2. Clip position = **((u_projection × u_view) × M) × (P, 1)**: multiply the matrices first, left to right, then the
   vector.
3. Working colour **W = C** if `u_vertexColors`, else (1, 1, 1, 1).
4. Normal **n = normalize(mat3(M) × N)** (upper-left 3×3 of M).
5. Colour output:
   - If `u_lit`:
     - Diffuse source **Dm** = `u_matDiffuse`, ambient source **Am** = `u_matAmbient`. Exception: if **C.a is exactly
       0** (the display list supplied a diffuse colour through the vertex colour), Dm = W.rgb and Am = (0, 0, 0).
       Note the test is on C (the input colour), the value taken is W.
     - For each light k = 0, 1, with direction L and colour K:
       - d = max(0, −(L · n))
       - h = (L + (0, 0, −1)) / 2
       - s = max(0, (−h) · n), then s = s × s
       - term = ((`u_matSpecular` × K) × s) + ((Dm × K) × d) + (Am × K): the three products as written (colour times
         colour first, then the scalar), and the three terms summed left to right.
     - Output colour = (min((term0 + term1) + `u_matEmission`, 1) per channel, alpha 1).
   - Else: output colour = (W.rgb, 1).
6. Texcoord output:
   - If not `u_textured`: (0, 0).
   - `u_texgen` 0 or 1: the xy of **u_texMatrix × (T.s, T.t, 0, 1)**.
   - `u_texgen` 2 or 3: let **G** = u_texMatrix, except for mode 2:
     G = transpose(((u_texMatrix × V) × R)) with V = `u_view` if `u_lit` else identity, and R = the 4×4 matrix built
     from the upper-left 3×3 of **B** (the bone matrix, without the billboard), padded with identity. Source vector
     **S** = (N, 1) for mode 2, (P, 1) for mode 3 (the raw inputs). With G[c][r] meaning column c, row r:
     - u = 4-component dot product of S with (G[0][0], G[0][1], G[0][2], T.s)
     - v = 4-component dot product of S with (G[1][0], G[1][1], G[1][2], T.t)

### 1.3 Fragment stage

Inputs: colour c (rgba, interpolated), texcoord. Let A denote the running alpha.

1. If `u_textured`:
   - texel t = sample of `u_texture`; if `u_paletteOverrideOn`, t = (`u_paletteOverride`.rgb, t.a).
   - `u_polyMode` 1 (decal): per channel rgb = (t × t.a) + (c × (1 − t.a)); A = `u_matAlpha` × c.a.
   - `u_polyMode` 2 (toon/highlight): r = `u_toonRamp`[int(c.r × 31.0)] (float multiply, then truncate);
     rgb = (t.rgb × c.r) + r; A = (`u_matAlpha` × t.a) × c.a.
   - otherwise (modulate): rgb = c.rgb × t.rgb; A = c.a × (`u_matAlpha` × t.a).
   - If `u_colorOverrideOn`: rgb = `u_colorOverride`.rgb; A = A × `u_colorOverride`.a.
2. Else if `u_colorOverrideOn`: the result is `u_colorOverride` (rgb and alpha as given).
3. Else: the result is (`u_toonRamp`[int(c.r × 31.0)], c.a) when `u_polyMode` is 2, else c; then A = A × `u_matAlpha`.
4. **ES only:** `u_alphaPass` 1: drop the fragment if A < 1.0. `u_alphaPass` 2: drop it if A ≥ 1.0. (Before fog.)
5. Fog, if `u_fogOn`: z = window depth of the fragment (`gl_FragCoord.z`). f = 0; if z ≥ `u_fogFar`, f = 1; else if
   z > `u_fogNear`, f = (((z − u_fogNear) / (u_fogFar − u_fogNear)) × 124.0) / 128.0 (left to right; 124/128 is the top
   of MPH's fog density table). Then per channel rgb = (rgb × (1 − f)) + (`u_fogColor`.rgb × f). Alpha unchanged.
6. Output (rgb, A).

### 1.4 Exactness

Use exactly these operations and groupings. Forms that are exact in IEEE float are fine (×0.5 for ÷2, `-dot(a,b)` vs
`dot(-a,b)`, swapping the two operands of one multiply). Don't reassociate sums or products, don't factor
(e.g. `K × (S×s + D×d + A)`), don't precompute products on the CPU. The proof run compares old and new output as
32-bit floats, per pixel.

---

## Part 2. Layered draw (per frame)

### 2.1 Inputs

Three ordered lists of items, built by the scene each frame:
- **N**: the non-decal items (includes items whose material is translucent);
- **D**: the decal items;
- **T**: the translucent items (may share items with N or D).

Each item has an integer **polygon ID** ≥ 1 (it can exceed 255), and a caller-supplied draw action that sets the item's
uniforms and issues its geometry. Fragment alpha "A" below is the shader's output alpha.

The buffers on entry: colour, depth (1.0) and an 8-bit stencil (0) were cleared by the caller.

### 2.2 What the frame must produce (per pixel)

1. Solid surfaces of N: fragments with A exactly 1, nearest wins.
2. Decals: every fragment of D, pulled toward the viewer by a polygon offset, depth test "nearer or equal", blended
   over (source alpha, one minus source alpha), writing depth.
3. Marking: each pixel gets an "owner" ID. For each item of T in order, its A < 1 fragments that are nearer-or-equal
   than the depth buffer (solid + decal depth, plus depth written by earlier marking fragments) **and** whose ID
   (taken as min(ID, 255), the stencil's range) is greater than the pixel's current owner become the owner and write
   their depth. No colour.
4. The depth buffer is rebuilt from scratch from N's A = 1 fragments only (decals and marking depth are dropped).
5. Back layer: for each item of T in order, its A < 1 fragments at pixels whose owner ≠ min(ID, 255), depth tested
   nearer-or-equal against the rebuilt depth, blended, no depth writes.
6. Front layer: the same for pixels whose owner = min(ID, 255).

The normative part is the state table below. The per-pixel description above is only there to explain it.

### 2.3 Phases and exact GL state

The draw is a sequence of phases. **Each phase has a separate "begin" (sets state) and "draw items" step**, because on
Android the caller runs extra draws (the HD gun, suit and arena) between begin and the items, and after the items. Those
extra draws can leave state changed (for example depth writes off, or colour writes on), and the original behaviour
depends on that. So **a begin step must set exactly the state listed for it, and nothing else**, and item steps must
only do the per-item operation listed. "Desktop also" rows are set only on the desktop platform.

"Alpha filter" is a platform operation:
- desktop: Off = disable the fixed-function alpha test; SolidOnly = enable it with func EQUAL, ref 1.0;
  SeeThroughOnly = enable it with func LESS, ref 1.0. (Enabling an already enabled test is fine.)
- ES: write `u_alphaPass` (0 / 1 / 2) on the scene program, through a uniform location the caller provides.

| Step | State set (both platforms) | Desktop also | Per item, before its draw |
|---|---|---|---|
| Solid: begin | alpha filter SolidOnly; depth func LESS; stencil test on; stencil write mask 0xFF; stencil op (fail ZERO, depth-fail ZERO, pass ZERO); stencil func ALWAYS, ref 0, mask 0xFF | colour writes on (rgba); depth writes on | none (list N) |
| Solid: end | alpha filter Off | | |
| Decal: begin | polygon-offset-fill on; polygon offset factor −1, units −1; depth func LEQUAL | blending on; blend func (SRC_ALPHA, ONE_MINUS_SRC_ALPHA) | none (list D) |
| Decal: end | polygon offset factor 0, units 0; polygon-offset-fill off | | |
| Marking: begin | alpha filter SeeThroughOnly; colour writes off; stencil op (KEEP, KEEP, REPLACE) | | stencil func GREATER, ref = ID, mask 0xFF (list T) |
| Depth rebuild: begin | clear the depth buffer (only depth); stencil op (KEEP, KEEP, KEEP); stencil func ALWAYS, ref 0, mask 0xFF; alpha filter SolidOnly | | none (list N) |
| Back layer: begin | alpha filter SeeThroughOnly; colour writes on; depth writes off; depth func LEQUAL | stencil op (KEEP, KEEP, KEEP) | stencil func NOTEQUAL, ref = ID, mask 0xFF (list T) |
| Front layer: begin | (nothing) | stencil op (KEEP, KEEP, KEEP) | stencil func EQUAL, ref = ID, mask 0xFF (list T) |
| Finish | depth writes on; alpha filter Off; stencil test off | | |

Pass the raw ID as the stencil reference (GL clamps it to 255 itself). Phases run in the table's order. Steps not
listed (for example a separate "end" for marking) do nothing.

---

## Part 3. What to write

All new files. Don't edit existing files; the integrator wires them in.

1. `src/MphRead/Rendering/SceneShaderSource.cs`: `namespace MphRead.Rendering`, `public static class
   SceneShaderSource` with four `public static string` properties or fields: `DesktopVertex`, `DesktopFragment`
   (GLSL 1.20), `EsVertex`, `EsFragment` (GLSL ES 3.00). Your own structure, function and variable names, and comments.
2. `src/MphRead/Rendering/LayeredDraw.cs`: `namespace MphRead.Rendering`. A platform abstraction for the state
   operations in 2.3 (an interface of your design), and a generic class or static class that runs the phases over a
   generic item type with a caller-supplied polygon-ID getter and draw action. It must expose **each phase's begin, its
   item step, and its end where there is one, as separate calls**, plus one convenience call that runs the whole frame
   in order with no extra draws (the desktop renderer uses that).
3. `src/MphRead/Rendering/DesktopLayerGl.cs`: the desktop implementation of your abstraction, with OpenTK 4.9
   (`OpenTK.Graphics.OpenGL`, compatibility profile; `GL.AlphaFunc`, `GL.StencilFunc`, and so on).
4. `src/MphRead.Android/GlesLayerGl.cs`: the Android implementation, with `Android.Opengl.GLES30` for phase state and
   `GlNative` (`src/MphRead.Android/GlNative.cs`, P/Invoke straight to the driver) for the per-item stencil-func call;
   it receives the `u_alphaPass` location from its constructor (or a setter). `GlNative` has `StencilFunc`; if you need
   another P/Invoke, add it in your own file, not in `GlNative.cs`.

Keep the style of the surrounding code (C# 12, nullable on, 4-space indent, `//` comments in plain English).

### What you may read

This spec; GBATEK; the OpenGL / GLSL / OpenTK / Android GLES docs; and in the staging tree you're given:
`src/MphRead.Android/GlNative.cs`, any `.csproj`, and other files only for build settings and namespaces. **Don't
open:** `src/MphRead/Renderer.cs`, `src/MphRead/Shaders.cs`, `src/MphRead.Android/CampaignActivity.cs`,
`src/MphRead.Tools/Testing/TestPrint.cs`, anything under the spec author's scratchpad, git history, or any third-party
model-viewer source (upstream MphRead, mph-viewer, dsgraph and others) online or on disk. If you think you need one of
them, stop and say what you need instead.

### Done means

- `MphRead.csproj` builds in the staging tree with your files (`dotnet build src/MphRead/MphRead.csproj -c Release`).
- The Android project builds with your adapter (command below).
- A short report: the files, your abstraction's members, the public call sequence for one frame, and anything in this
  spec you found ambiguous.
