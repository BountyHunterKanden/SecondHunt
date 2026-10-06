# Fork attribution & license posture

This repository is a fork of **MphRead** by **NoneGiven**
(https://github.com/NoneGiven/MphRead), used under the **MIT License**.

- The upstream `LICENSE` file (MIT, Copyright (c) 2020 NoneGiven) is preserved
  **verbatim and unmodified**, as the MIT terms require. It is not edited, moved,
  or replaced by this file.
- The upstream `README.md` **Acknowledgements** and **Special Thanks** sections
  (crediting dsgraph, Chemical's model format docs, McKay42's mph-model-viewer and
  mph-arc-extractor, Barubary's dsdecmp (LZ10), loveemu's swav2wav, Gericom's
  ffmpeg VX patch, CharlesVanEeckhout's actimagine decoder, CyberBotX's NCSF, and
  hackyourlife's mph-viewer) are preserved. Any redistribution of this fork
  keeps that chain intact.
- This file is an **addition**, not a substitute for `LICENSE`. It records the
  fork relationship; it does not alter upstream terms.

## This fork's additions

New files added by this fork (this document, `ARCHITECTURE.md`, files under
`docs/`, and the Second Hunt app and tools code) are contributed under the **same MIT
License** for continuity, unless a file's own header states otherwise. The MIT
copyright notice above continues to cover the upstream-derived code.

## Legal posture (project rule)

This project ships **no game assets**. Assets are extracted at runtime from a ROM
the user legally owns and supplies; they are never bundled, committed, or
downloaded by the app. No "Metroid", "Nintendo", or related trademark appears in
the app name ("Second Hunt"), icon, package id (`com.secondhunt.app`), or store-facing
branding. "mph-recomp" / `com.mphrecomp.app` is the internal development name.

The music player inherited from upstream (NcsfPlay, see `THIRD_PARTY_NOTICES.md`)
used to contain sequencer logic adapted from a community decompilation of the DS
SDK's sound driver. That sequencer is now a clean-room implementation, verified
sample for sample against the old one on every song (`docs/cleanroom/README.md`). MphRead's VX movie
decoder and its transparency rendering were replaced the same way, each checked to give the same result.
