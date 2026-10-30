# Third-party notices

What the Second Hunt app (the Android APK and the Windows build) contains besides its own code, and the notices each
licence asks to have passed on. The app ships **no game data**: everything it shows comes from the user's own game files at
run time. The in-app RECOMP SETTINGS > CREDITS screen shows this file.

Update this file whenever a package is added, removed or upgraded (`PackageReference` in any `src/*.csproj` that the
Android app or the Windows app references), and check the APK's `lib/*/libaot-*.dll.so` list and the Windows zip's files
against it before each public build.

| Component | Version | Licence | In the app as |
|---|---|---|---|
| MphRead (upstream of this fork), NoneGiven | fork of 0.35.0.0 | MIT | `MphRead.dll` (with this fork's additions, also MIT: `NOTICE-fork.md`) |
| NcsfPlay (from NCSF), Naram "CyberBotX" Qashat, via MphRead | (vendored, modified) | MIT (see its section for provenance) | `NcsfPlay.dll` |
| dsdecmp (LZ10), Barubary; swav2wav, loveemu; mph-model-viewer (COLLADA export), McKay42 | (adapted by upstream MphRead) | MIT | inside `MphRead.dll` |
| in_xsf, Naram Qashat (CyberBotX) | (partly, via NcsfPlay) | BSD 3-clause | inside `NcsfPlay.dll` |
| OpenTK | 4.9.4 | MIT | `OpenTK.*.dll` |
| MoonSharp | 2.0.0 | BSD 3-clause (parts MIT) | `MoonSharp.Interpreter.dll` |
| Concentus (C# port of the Opus codec), Logan Stromberg and the Opus authors | 2.2.2 | BSD 3-clause (the Opus licence) | `Concentus.dll` (online voice chat, milestone O8: `MphRecomp.Core/Online/Voice/`) |
| SixLabors.ImageSharp | 3.1.12 | Six Labors Split License 1.0 (Apache 2.0 for open-source use) | `SixLabors.ImageSharp.dll` |
| SoundFlow | 1.4.1 | MIT | `SoundFlow.dll` |
| miniaudio (bundled by SoundFlow) | (SoundFlow 1.4.1) | MIT or Unlicense (used under MIT) | `libminiaudio.so` (Android), `miniaudio.dll` (Windows) |
| GLFW (bundled by OpenTK as OpenTK.redist.glfw) | 3.4 | zlib | `glfw3.dll` (Windows only: the window, input and GL context) |
| SDL (Simple DirectMedia Layer), Sam Lantinga and the SDL contributors | 3.4.14 | zlib | `SDL3.dll` (Windows only: gamepads and their gyro) |
| CommunityToolkit.HighPerformance | 8.4.2 | MIT | `CommunityToolkit.HighPerformance.dll` |
| lzokay, Jack Andersen (AxioDL) | 2018 (ported to C#) | MIT | ported into `MphRecomp.Core.dll` (`Import/Retro/Lzo1x.cs`: LZO1X decompression for the Echoes / Prime 3 imports) |
| System.IO.Hashing, .NET runtime, .NET for Android, .NET Windows Desktop (Windows Forms, System.Drawing) | 10.0.8 / 9.0 | MIT | Android: the `System.*`, `Mono.*`, `Java.Interop` assemblies and the runtime `.so` files; Windows: inside `SecondHunt.exe` (a self-contained .NET app) |

Art made for Second Hunt by others, shipped with their permission:

| Work | By | Permission | In the app as |
|---|---|---|---|
| Dark Samus stand-in (low-poly model, an edit of the game's Samus) | Limoki | given to the owner 2026-10-27 to ship in the app, credited as "Limoki" | `recipes/standin/DarkSamus/` inside `MphRecomp.Core.dll`: her geometry as edits to the user's own Samus model plus her texture layout and paint (no game geometry or pixels), built at run time by `Multiplayer/DarkSamusStandin.cs` |
| Dark Samus stand-in morph ball (her own sphere and paint, not made from the game's art) | Limoki | given to the owner with the model, credited as "Limoki" | `recipes/standin/DarkSamusBall/` inside `MphRecomp.Core.dll`: her sphere and textures as plain data, drawn on the user's own Samus morph ball frame (its nodes and roll animation) by `Multiplayer/DarkSamusStandin.cs` |

The online server (`src/SecondHunt.Server/`, run on the project's own VPS; not part of the app) also uses:

| Component | Version | Licence | Used as |
|---|---|---|---|
| LDNOOBW, "List of Dirty, Naughty, Obscene, and Otherwise Bad Words" (English list), Shutterstock and contributors | fetched 2026-10-09 (403 entries), unmodified | CC BY 4.0 | `src/SecondHunt.Server/wordlists/ldnoobw_en.txt`, deployed as the server's `words.txt`: the word filter for online names and typed chat (milestone O7). See its section below |
| Microsoft.Data.Sqlite | 9.0.9 | MIT | the server's database (`src/SecondHunt.Server/Store.cs`) |
| SQLitePCLRaw.bundle_e_sqlite3 (SourceGear) with SQLite's `e_sqlite3` | 2.1.10 | Apache 2.0 (SQLite itself: public domain) | the native SQLite engine under Microsoft.Data.Sqlite |

Format references (no code shipped from them unless the entry says so):

| Project | Licence | Used for |
|---|---|---|
| PrimeWorldEditor (Aruki, AxioDL Team) | MIT | the Retro file layouts the `MphRecomp.Core/Import/Retro*` readers follow (via this project's own Python reference scripts). **Code translated from it: `Import/Retro/Anim.cs` only** (CAnimationLoader, LibCommon CBitStreamInWrapper, CBone::UpdateTransform; board S11), so its notice below applies; the other readers are this project's own parsers of the layouts it reads |
| metaforce (Metaforce Contributors, AxioDL; a reverse-engineered re-implementation of Metroid Prime's engine) | MIT | Prime 1 behaviour re-implemented in this project's own code from reading metaforce's source: particle effects (`Campaign/RetroParticles.cs`: CElementGen, CUVEAnimTexture), swooshes (`Campaign/RetroSwoosh.cs`: CParticleSwoosh), the PART / SWHC readers (`Import/Retro/Part.cs`: CParticleDataFactory), the animation event layout (`Import/Effects/BodyFx.cs`: CAnimPOIData, CPOINode), the sound id lookup (`Import/Retro/MusyX.cs`: CSfxManager::TranslateSFXID), the Scout Drone laser's look and sweep (`App/Campaign/CampaignRenderer.DroneFx.cs`: CDroneLaser; `MphRead/Entities/Players/PlayerDroneLaser.cs`: CDrone::UpdateLaser); and layouts only: the Prime 3 ANIM (`Import/Retro3/Anim3.cs`, a clean re-implementation) and the CMDL material flag names (`Import/Retro/Cmdl.cs`). Its notice is below |
| Fruity Prime (liveteklol; another MphRead fork) | MIT | the netcode of served online matches, ported from its C++ `src/MphRead.Native/Mods/Network` (master 610dc5b, 2026-10-20) into this project's C#: owner-reported positions and the correction rule, the latest-wins intent channel, remote player smoothing, shooter-side hit claims with a 45-frame rewind, client hit and death prediction, and packet fencing (`MphRecomp.Core/Multiplayer/Net/`: `FruitySync.cs`, `ClientPosition.cs`, `ClientPrediction.cs`, `HitClaims.cs`, `RemoteSmoothing.cs`, `NetMatchSession.cs`; `MphRead/Entities/Players/`: `PlayerHitClaims.cs`, `PlayerNetPosition.cs`, `PlayerNetPredict.cs`; `SecondHunt.MatchHost/Program.Netcode.cs`; `SecondHunt.Server/HubServed.cs`). Map: `docs/FRUITY_PORT.md`. Its notice is below |
| amuse (Antidote / Jackoalan, AxioDL) | MIT | the MusyX SoundMacro command layout `Import/Retro/MusyX.cs` reads (the decoder and `DspAdpcm.cs` are this project's own; every sound and its coefficients come from the user's disc). Its notice is below |
| Dolphin's `docs/WiaAndRvz.md` | GPL-2.0-or-later (the Dolphin repository's default) | the RVZ/WIA format's facts; no Dolphin code or text was taken (`Import/Disc/WiaReader.cs`) |
| Retro Modding Wiki (wiki.axiodl.com), WiiBrew | CC BY 4.0 / wiki | file-layout facts (`Import/Retro/Ancs.cs`, `Mrea.cs`, `Retro3/Anim3.cs`; `Import/Disc/WiiDisc.cs`); no text taken |
| RFC 8878 (Zstandard), the LZMA and bzip2 format descriptions | (specifications) | this project's own decoders in `Import/Disc/` |
| Switchbrew wiki (switchbrew.org: XCI, NCA, PartitionFS/HFS0, RomFS, IVFC, BKTR patch RomFS, Ticket pages) | wiki | file-layout facts for this project's own Switch game-file reader (`Import/Switch/`: .xci / .nsp, NCA decryption, RomFS, update patches); no code from any other implementation used, no text taken |
| Prime 4 Model Dumper (LogicFox15's fork of KillzXGaming's MPR-Model-Dumper) | none stated | Prime 4's pak, model, material and texture layouts, read by the clean-room spec author only to write the code-free spec `docs/cleanroom/G_beyond_spec.md` (CR6, owner 2026-10-17 option 1). Its readers in `Import/Beyond/` (`BeyondArchive`, `BeyondUnpack`, `BeyondForm`, `BeyondModelFile`, `BeyondMaterialInstance`, `BeyondTexture`, `TegraBlockLinear` and the texture decoders; the `Chpr*` skeleton readers are older, worked out from the dump's own bytes) were written from that spec by an implementer who never saw the tool's code or output; no code, text or names were taken, and the tool is not in the repository |
| Khronos Data Format Specification (ASTC), Microsoft's BC7 format documentation | (specifications) | this project's own ASTC and BC7 decoders (`Import/Beyond/AstcBlockDecoder.cs`, `Bc7BlockDecoder.cs`); the 8-bit rounding of an ASTC result matches what common ASTC decoders give (found by comparing results, no code read) |
| NumPy, OpenBLAS | BSD 3-clause | behaviour only: `Import/Hd/BeyondTrophy.cs` reproduces the results of this project's own Python converter, so it sorts as `numpy.argsort`'s default introsort does and solves 3x3 systems in OpenBLAS's order of operations (worked out by testing their results; no code taken) |

---

## MphRead

Upstream of this fork; also covers this fork's additions (`NOTICE-fork.md`). Its README's acknowledgements (dsgraph,
Chemical, McKay42, Barubary's dsdecmp, loveemu's swav2wav, Gericom, CharlesVanEeckhout, CyberBotX's NCSF,
hackyourlife) are kept in `README.md`.

```text
MIT License

Copyright (c) 2020 NoneGiven

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## NcsfPlay

The DS sequenced-music player the game's music runs on: Naram "CyberBotX" Qashat's NCSF C# libraries
(https://github.com/CyberBotX/NCSF, MIT), vendored by upstream MphRead as `src/NcsfPlay` and modified by MphRead and by
this project (volume and timing fixes). It plays the music from the user's own ROM; no music ships with the app.

Provenance:
- The sound archive readers (`NC/`: SDAT, SSEQ, SBNK, SWAR, SWAV), the NCSF/PSF container and the stream wrapper are
  CyberBotX's code; `Player/NCSFFile.cs` and `Player/NCSFPlayerStream.cs` are partly based on his in_xsf player
  (https://github.com/CyberBotX/in_xsf, BSD 3-clause, same author).
- The sequencer, voice, envelope and mixing logic (`Engine/`) is this project's own clean-room implementation
  (2026-10-05, see `docs/cleanroom/README.md`). It replaced the sequencer files that came with upstream MphRead, which
  were adapted from the Pokémon Diamond decompilation by pret and from DeSmuME; none of that code remains. It plays
  every song exactly as the replaced files did.
- Upstream's ReplayGain loudness analyzer (`ReplayGain/`, a port of the LGPL-2.1 `gain_analysis.c`) was never called by
  the game and has been removed; no code from it remains.

```text
The MIT License (MIT)

Copyright (c) 2013-2026 Naram "CyberBotX" Qashat

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## dsdecmp (Barubary)

https://github.com/Barubary/dsdecmp. Upstream MphRead's LZ10 compression routines (`MphRead/Utility/Compress.cs`, `LZUtil`) are based on it; they ship in `MphRead.dll`.

```text
MIT License

Copyright (c) 2026 Barubary

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## loveemu-lab: swav2wav (loveemu)

https://github.com/loveemu/loveemu-lab. Upstream MphRead's SWAV (DS sound sample) conversion is based on its swav2wav; it ships in `MphRead.dll`.

```text
MIT License

Copyright (c) 2017 loveemu

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## mph-model-viewer (McKay42)

https://github.com/McKay42/mph-model-viewer. Upstream MphRead's COLLADA export method (`MphRead/Export/Collada.cs`) is based on it; it ships in `MphRead.dll`. (The licence file names no holder beyond the year.)

```text
MIT License

Copyright (c) 2017 McKay42

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## in_xsf (CyberBotX)

https://github.com/CyberBotX/in_xsf. NcsfPlay's `Player/NCSFFile.cs` and `Player/NCSFPlayerStream.cs` are partly based on
it (same author as NCSF).

```text
Copyright 2013-2021 Naram Qashat

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## OpenTK

```text
Copyright (c) 2006 - 2020 Stefanos Apostolopoulos <stapostol@gmail.com> for the Open Toolkit library.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## MoonSharp

```text
Copyright (c) 2014-2016, Marco Mastropaolo
All rights reserved.

Parts of the string library are based on the KopiLua project (https://github.com/NLua/KopiLua)
Copyright (c) 2012 LoDC

Visual Studio Code debugger code is based on code from Microsoft vscode-mono-debug project (https://github.com/Microsoft/vscode-mono-debug).
Copyright (c) Microsoft Corporation - released under MIT license.

Remote Debugger icons are from the Eclipse project (https://www.eclipse.org/).
Copyright of The Eclipse Foundation

The MoonSharp icon is (c) Isaac, 2014-2015

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of the {organization} nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Concentus (Opus)

Voice chat's codec (`MphRecomp.Core/Online/Voice/VoiceCodec.cs`): the managed Concentus package only, no native
libraries. Opus is also covered by royalty-free patent licences from its contributors (opus-codec.org/license).

```text
Copyright (c) by various holding parties, including (but not limited to): 
Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation,
Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry,
Logan Stromberg. All rights are reserved by their respective holders.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.


This repository and its redistributable packages contain independently compiled
versions of the Opus C reference library, which is maintained by Xiph.org and the
Opus open-source contributors. The source code for these libraries is freely available
at https://gitlab.xiph.org/xiph/opus/-/tags/v1.5.2, and all binaries are being
redistributed to you under the same terms of the general Opus license dictated above.
```

## SixLabors.ImageSharp

Licensed to this project under the **Apache License, Version 2.0**: the Six Labors Split License (below) grants Apache
2.0 when the Work is consumed "in software licensed under an Open Source or **Source Available** license" (section 2,
first bullet), which is exactly this project's posture. The grant also holds independently under section 2's third
bullet (a Direct Package Dependency used by a for-profit individual with under 1M USD annual gross revenue). The
Apache License 2.0 text follows the Split License.

```text
Six Labors Split License
Version 1.0, June 2022
Copyright (c) Six Labors

TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

1. Definitions.

   "You" (or "Your") shall mean an individual or Legal Entity exercising permissions granted by this License.

   "Source" form shall mean the preferred form for making modifications, including but not limited to software source
    code, documentation source, and configuration files.

   "Object" form shall mean any form resulting from mechanical transformation or translation of a Source form, including
    but not limited to compiled object code, generated documentation, and conversions to other media types.

   "Work" (or "Works") shall mean any Six Labors software made available under the License, as indicated by a
   copyright notice that is included in or attached to the work.

   "Direct Package Dependency" shall mean any Work in Source or Object form that is installed directly by You.

   "Transitive Package Dependency" shall mean any Work in Object form that is installed indirectly by a third party
    dependency unrelated to Six Labors.

2. License

   Works in Source or Object form are split licensed and may be licensed under the Apache License, Version 2.0 or a
   Six Labors Commercial Use License.

   Licenses are granted based upon You meeting the qualified criteria as stated. Once granted,
   You must reference the granted license only in all documentation.

   Works in Source or Object form are licensed to You under the Apache License, Version 2.0 if.

   - You are consuming the Work in for use in software licensed under an Open Source or Source Available license.
   - You are consuming the Work as a Transitive Package Dependency.
   - You are consuming the Work as a Direct Package Dependency in the capacity of a For-profit company/individual with
     less than 1M USD annual gross revenue.
   - You are consuming the Work as a Direct Package Dependency in the capacity of a Non-profit organization
     or Registered Charity.

   For all other scenarios, Works in Source or Object form are licensed to You under the Six Labors Commercial License
   which may be purchased by visiting https://sixlabors.com/pricing/.
```

Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0):

```text
                              Apache License
                        Version 2.0, January 2004
                     http://www.apache.org/licenses/

TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

1. Definitions.

   "License" shall mean the terms and conditions for use, reproduction,
   and distribution as defined by Sections 1 through 9 of this document.

   "Licensor" shall mean the copyright owner or entity authorized by
   the copyright owner that is granting the License.

   "Legal Entity" shall mean the union of the acting entity and all
   other entities that control, are controlled by, or are under common
   control with that entity. For the purposes of this definition,
   "control" means (i) the power, direct or indirect, to cause the
   direction or management of such entity, whether by contract or
   otherwise, or (ii) ownership of fifty percent (50%) or more of the
   outstanding shares, or (iii) beneficial ownership of such entity.

   "You" (or "Your") shall mean an individual or Legal Entity
   exercising permissions granted by this License.

   "Source" form shall mean the preferred form for making modifications,
   including but not limited to software source code, documentation
   source, and configuration files.

   "Object" form shall mean any form resulting from mechanical
   transformation or translation of a Source form, including but
   not limited to compiled object code, generated documentation,
   and conversions to other media types.

   "Work" shall mean the work of authorship, whether in Source or
   Object form, made available under the License, as indicated by a
   copyright notice that is included in or attached to the work
   (an example is provided in the Appendix below).

   "Derivative Works" shall mean any work, whether in Source or Object
   form, that is based on (or derived from) the Work and for which the
   editorial revisions, annotations, elaborations, or other modifications
   represent, as a whole, an original work of authorship. For the purposes
   of this License, Derivative Works shall not include works that remain
   separable from, or merely link (or bind by name) to the interfaces of,
   the Work and Derivative Works thereof.

   "Contribution" shall mean any work of authorship, including
   the original version of the Work and any modifications or additions
   to that Work or Derivative Works thereof, that is intentionally
   submitted to Licensor for inclusion in the Work by the copyright owner
   or by an individual or Legal Entity authorized to submit on behalf of
   the copyright owner. For the purposes of this definition, "submitted"
   means any form of electronic, verbal, or written communication sent
   to the Licensor or its representatives, including but not limited to
   communication on electronic mailing lists, source code control systems,
   and issue tracking systems that are managed by, or on behalf of, the
   Licensor for the purpose of discussing and improving the Work, but
   excluding communication that is conspicuously marked or otherwise
   designated in writing by the copyright owner as "Not a Contribution."

   "Contributor" shall mean Licensor and any individual or Legal Entity
   on behalf of whom a Contribution has been received by Licensor and
   subsequently incorporated within the Work.

2. Grant of Copyright License. Subject to the terms and conditions of
   this License, each Contributor hereby grants to You a perpetual,
   worldwide, non-exclusive, no-charge, royalty-free, irrevocable
   copyright license to reproduce, prepare Derivative Works of,
   publicly display, publicly perform, sublicense, and distribute the
   Work and such Derivative Works in Source or Object form.

3. Grant of Patent License. Subject to the terms and conditions of
   this License, each Contributor hereby grants to You a perpetual,
   worldwide, non-exclusive, no-charge, royalty-free, irrevocable
   (except as stated in this section) patent license to make, have made,
   use, offer to sell, sell, import, and otherwise transfer the Work,
   where such license applies only to those patent claims licensable
   by such Contributor that are necessarily infringed by their
   Contribution(s) alone or by combination of their Contribution(s)
   with the Work to which such Contribution(s) was submitted. If You
   institute patent litigation against any entity (including a
   cross-claim or counterclaim in a lawsuit) alleging that the Work
   or a Contribution incorporated within the Work constitutes direct
   or contributory patent infringement, then any patent licenses
   granted to You under this License for that Work shall terminate
   as of the date such litigation is filed.

4. Redistribution. You may reproduce and distribute copies of the
   Work or Derivative Works thereof in any medium, with or without
   modifications, and in Source or Object form, provided that You
   meet the following conditions:

   (a) You must give any other recipients of the Work or
       Derivative Works a copy of this License; and

   (b) You must cause any modified files to carry prominent notices
       stating that You changed the files; and

   (c) You must retain, in the Source form of any Derivative Works
       that You distribute, all copyright, patent, trademark, and
       attribution notices from the Source form of the Work,
       excluding those notices that do not pertain to any part of
       the Derivative Works; and

   (d) If the Work includes a "NOTICE" text file as part of its
       distribution, then any Derivative Works that You distribute must
       include a readable copy of the attribution notices contained
       within such NOTICE file, excluding those notices that do not
       pertain to any part of the Derivative Works, in at least one
       of the following places: within a NOTICE text file distributed
       as part of the Derivative Works; within the Source form or
       documentation, if provided along with the Derivative Works; or,
       within a display generated by the Derivative Works, if and
       wherever such third-party notices normally appear. The contents
       of the NOTICE file are for informational purposes only and
       do not modify the License. You may add Your own attribution
       notices within Derivative Works that You distribute, alongside
       or as an addendum to the NOTICE text from the Work, provided
       that such additional attribution notices cannot be construed
       as modifying the License.

   You may add Your own copyright statement to Your modifications and
   may provide additional or different license terms and conditions
   for use, reproduction, or distribution of Your modifications, or
   for any such Derivative Works as a whole, provided Your use,
   reproduction, and distribution of the Work otherwise complies with
   the conditions stated in this License.

5. Submission of Contributions. Unless You explicitly state otherwise,
   any Contribution intentionally submitted for inclusion in the Work
   by You to the Licensor shall be under the terms and conditions of
   this License, without any additional terms or conditions.
   Notwithstanding the above, nothing herein shall supersede or modify
   the terms of any separate license agreement you may have executed
   with Licensor regarding such Contributions.

6. Trademarks. This License does not grant permission to use the trade
   names, trademarks, service marks, or product names of the Licensor,
   except as required for reasonable and customary use in describing the
   origin of the Work and reproducing the content of the NOTICE file.

7. Disclaimer of Warranty. Unless required by applicable law or
   agreed to in writing, Licensor provides the Work (and each
   Contributor provides its Contributions) on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
   implied, including, without limitation, any warranties or conditions
   of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
   PARTICULAR PURPOSE. You are solely responsible for determining the
   appropriateness of using or redistributing the Work and assume any
   risks associated with Your exercise of permissions under this License.

8. Limitation of Liability. In no event and under no legal theory,
   whether in tort (including negligence), contract, or otherwise,
   unless required by applicable law (such as deliberate and grossly
   negligent acts) or agreed to in writing, shall any Contributor be
   liable to You for damages, including any direct, indirect, special,
   incidental, or consequential damages of any character arising as a
   result of this License or out of the use or inability to use the
   Work (including but not limited to damages for loss of goodwill,
   work stoppage, computer failure or malfunction, or any and all
   other commercial damages or losses), even if such Contributor
   has been advised of the possibility of such damages.

9. Accepting Warranty or Additional Liability. While redistributing
   the Work or Derivative Works thereof, You may choose to offer,
   and charge a fee for, acceptance of support, warranty, indemnity,
   or other liability obligations and/or rights consistent with this
   License. However, in accepting such obligations, You may act only
   on Your own behalf and on Your sole responsibility, not on behalf
   of any other Contributor, and only if You agree to indemnify,
   defend, and hold each Contributor harmless for any liability
   incurred by, or claims asserted against, such Contributor by reason
   of your accepting any such warranty or additional liability.

END OF TERMS AND CONDITIONS
```

## SoundFlow

```text
MIT License

Copyright (c) 2025 LSXPrime

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the “Software”), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## miniaudio (bundled by SoundFlow as `libminiaudio.so` / `miniaudio.dll`)

From SoundFlow's third-party notices:

```text
MiniAudio
License: MIT or Unlicense (Dual Licensed)
Author: David Reid

(Notice used under MIT terms)
Copyright (c) David Reid

Permission is hereby granted, free of charge, to any person obtaining a copy of 
this software and associated documentation files (the "Software"), to deal in 
the Software without restriction, including without limitation the rights to 
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies 
of the Software, and to permit persons to whom the Software is furnished to do 
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all 
copies or substantial portions of the Software.
```

## CommunityToolkit.HighPerformance

```text
# .NET Community Toolkit

Copyright © .NET Foundation and Contributors

All rights reserved.

## MIT License (MIT)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NON-INFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## GLFW (bundled by OpenTK as `glfw3.dll`, Windows only)

```text
Copyright (c) 2002-2006 Marcus Geelnard

Copyright (c) 2006-2019 Camilla Löwy

This software is provided 'as-is', without any express or implied
warranty. In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would
   be appreciated but is not required.

2. Altered source versions must be plainly marked as such, and must not
   be misrepresented as being the original software.

3. This notice may not be removed or altered from any source
   distribution.
```

## SDL (`SDL3.dll`, Windows only)

```text
Copyright (C) 1997-2026 Sam Lantinga <slouken@libsdl.org>

This software is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
```

## System.IO.Hashing, the .NET runtime, .NET for Android and the .NET Windows Desktop runtime

MIT, Copyright (c) .NET Foundation and Contributors. Their own third-party notices:
https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT,
https://github.com/dotnet/android/blob/main/THIRD-PARTY-NOTICES.TXT and
https://github.com/dotnet/winforms/blob/main/THIRD-PARTY-NOTICES.TXT

```text
Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## PrimeWorldEditor (format reference; code in `Import/Retro/Anim.cs`)

```text
Copyright (c) 2015-2019 Aruki
Copyright (c) 2019-2021 AxioDL Team

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## metaforce (Prime 1 particle, swoosh, animation event, sound lookup and Scout Drone laser behaviour)

https://github.com/AxioDL/metaforce. See the format-references table for the files that follow it.

```text
The MIT License

Copyright (c) 2015-2026 Metaforce Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Fruity Prime (served online match netcode)

https://github.com/liveteklol/Fruity-Prime. See the format-references table for the files ported from it. Its licence file
names only NoneGiven, the holder it inherits from MphRead.

```text
MIT License

Copyright (c) 2020 NoneGiven

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## amuse (`Import/Retro/MusyX.cs`, SoundMacro command layout)

amuse, AxioDL's MusyX audio tools (https://gitlab.axiodl.com/AxioDL/amuse; mirror https://github.com/derselbst/amuse).

```text
The MIT License

Copyright (c) 2015-2018 Antidote / Jackoalan

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## lzokay (`Import/Retro/Lzo1x.cs`)

```text
The MIT License

Copyright (c) 2018 Jack Andersen

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Sun Microsystems CCITT G.721 reference code (`MphRecomp.Core` `Music/RsfMusic.cs`, the G721 decoder)

A C# port of the decoder in Sun's `g72x.c` / `g721.c` (Prime 1's `.rsf` music files are G.721), under its notice:

```text
This source code is a product of Sun Microsystems, Inc. and is provided
for unrestricted use.  Users may copy or modify this source code without
charge.

SUN SOURCE CODE IS PROVIDED AS IS WITH NO WARRANTIES OF ANY KIND INCLUDING
THE WARRANTIES OF DESIGN, MERCHANTIBILITY AND FITNESS FOR A PARTICULAR
PURPOSE, OR ARISING FROM A COURSE OF DEALING, USAGE OR TRADE PRACTICE.

Sun source code is provided with no support and without any obligation on
the part of Sun Microsystems, Inc. to assist in its use, correction,
modification or enhancement.

SUN MICROSYSTEMS, INC. SHALL HAVE NO LIABILITY WITH RESPECT TO THE
INFRINGEMENT OF COPYRIGHTS, TRADE SECRETS OR ANY PATENTS BY THIS SOFTWARE
OR ANY PART THEREOF.

In no event will Sun Microsystems, Inc. be liable for any lost revenue
or profits or other special, indirect and consequential damages, even if
Sun has been advised of the possibility of such damages.

Sun Microsystems, Inc.
2550 Garcia Avenue
Mountain View, California  94043
```

## LDNOOBW English word list (server only: `src/SecondHunt.Server/wordlists/ldnoobw_en.txt`)

"List of Dirty, Naughty, Obscene, and Otherwise Bad Words", English list, by Shutterstock and the project's contributors:
https://github.com/LDNOOBW/List-of-Dirty-Naughty-Obscene-and-Otherwise-Bad-Words (file `en`). Licensed under the Creative
Commons Attribution 4.0 International licence (CC BY 4.0): https://creativecommons.org/licenses/by/4.0/. Used unmodified
(the owner's own additions and exceptions live in a separate file on the server, `words_owner.txt`). It is used only by
the online server's word filter (`src/SecondHunt.Server/WordFilter.cs`); the app never contains or loads it.
