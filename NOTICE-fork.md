# Fork attribution & license posture

This repository is a fork of **MphRead** by **NoneGiven**
(https://github.com/NoneGiven/MphRead), used under the **MIT License**.

- The upstream MIT License (MIT, Copyright (c) 2020 NoneGiven) is preserved
  **verbatim and unmodified**, as the MIT terms require, in the file
  `LICENSE-MphRead`. Its text is not edited in any way. (The repository's root
  `LICENSE` file now carries Second Hunt's own source-available license; the
  upstream MIT text was moved, byte-for-byte unchanged, to `LICENSE-MphRead` so
  that both licenses can sit side by side.)
- The upstream `README.md` **Acknowledgements** and **Special Thanks** sections
  (crediting dsgraph, Chemical's model format docs, McKay42's mph-model-viewer and
  mph-arc-extractor, Barubary's dsdecmp (LZ10), loveemu's swav2wav, Gericom's
  ffmpeg VX patch, CharlesVanEeckhout's actimagine decoder, CyberBotX's NCSF, and
  hackyourlife's mph-viewer) are preserved. Any redistribution of this fork
  keeps that chain intact.
- This file is an **addition**; it records the fork relationship and does not
  alter the upstream MIT terms in `LICENSE-MphRead`.

## This fork's additions

Second Hunt is **source-available** software — the source is published to be
read, studied, and built for personal use, but it is **not open source** and is
not licensed for redistribution.

Second Hunt's own code was published here under the MIT License up to October 20,
2026. Copies of that code keep that license, and this change cannot revoke it.
From October 20, 2026 on, the original Second Hunt code, art, and documents
authored by the copyright holder are Copyright (c) 2026 BountyHunterKanden, all
rights reserved, and are published under the **PolyForm Strict License 1.0.0**
(with an additional permission for personal modification), as set out in the
root `LICENSE` file. You may read, build, and privately modify that code for
your own personal, noncommercial use; you may not redistribute it in source or
modified form, and you may not redistribute builds of it except the official
unmodified releases.

The upstream MphRead code (`LICENSE-MphRead`), the pre-October-20-2026 Second
Hunt code, and the third-party libraries (`THIRD_PARTY_NOTICES.md`) are **not**
restricted by that license and stay under their own permissive licenses.

**Which license covers which file.** A file that was authored by the copyright
holder and first added on or after October 20, 2026 is under the root `LICENSE`.
A file that comes from upstream MphRead, or was modified from an upstream file,
keeps its MIT notice and is under `LICENSE-MphRead`. A file first published here
before October 20, 2026 stays MIT. Files that embed third-party code carry an
inline attribution header naming the source and its license (for example
`src/MphRecomp.Core/Import/Retro/Anim.cs` and `.../Lzo1x.cs`).

## Legal posture (project rule)

This project ships **no game assets**. Assets are extracted at runtime from a ROM
the user legally owns and supplies; they are never bundled, committed, or
downloaded by the app. No "Metroid", "Nintendo", or related trademark appears in
the app name ("Second Hunt"), icon, package id (`com.secondhunt.app`), or store-facing
branding. "mph-recomp" / `com.mphrecomp.app` is the internal development name.

The music player inherited from upstream (NcsfPlay, see `THIRD_PARTY_NOTICES.md`)
used to contain sequencer logic adapted from a community decompilation of the DS
SDK's sound driver. That sequencer is now a clean-room implementation, verified
sample for sample against the old one on every song. MphRead's VX movie
decoder and its transparency rendering were replaced the same way, each checked to give the same result.
