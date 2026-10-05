# Third-party notices

What the Second Hunt app (the APK) contains besides its own code, and the notices each licence asks us to pass on. The
app ships **no game data**: everything it shows comes from the user's own game files at run time. The in-app
RECOMP SETTINGS > CREDITS screen shows this file.

Update this file whenever a package is added, removed or upgraded (`PackageReference` in any `src/*.csproj` that the
Android app references), and check the APK's `lib/*/libaot-*.dll.so` list against it before each public build.

| Component | Version | Licence | In the APK as |
|---|---|---|---|
| MphRead (upstream of this fork), NoneGiven | fork of 0.35.0.0 | MIT | `MphRead.dll` (with our additions, also MIT: `NOTICE-fork.md`) |
| NcsfPlay (from NCSF), Naram "CyberBotX" Qashat, via MphRead | (vendored, modified) | MIT (see its section for provenance) | `NcsfPlay.dll` |
| dsdecmp (LZ10), Barubary; swav2wav, loveemu; mph-model-viewer (COLLADA export), McKay42 | (adapted by upstream MphRead) | MIT | inside `MphRead.dll` |
| in_xsf, Naram Qashat (CyberBotX) | (partly, via NcsfPlay) | BSD 3-clause | inside `NcsfPlay.dll` |
| OpenTK | 4.9.4 | MIT | `OpenTK.*.dll` |
| MoonSharp | 2.0.0 | BSD 3-clause (parts MIT) | `MoonSharp.Interpreter.dll` |
| SixLabors.ImageSharp | 3.1.12 | Six Labors Split License 1.0 (Apache 2.0 for open-source use) | `SixLabors.ImageSharp.dll` |
| SoundFlow | 1.4.1 | MIT | `SoundFlow.dll` |
| miniaudio (bundled by SoundFlow) | (SoundFlow 1.4.1) | MIT or Unlicense (used under MIT) | `libminiaudio.so` |
| CommunityToolkit.HighPerformance | 8.4.2 | MIT | `CommunityToolkit.HighPerformance.dll` |
| lzokay, Jack Andersen (AxioDL) | 2018 (ported to C#) | MIT | ported into `MphRecomp.Core.dll` (`Import/Retro/Lzo1x.cs`: LZO1X decompression for the Echoes / Prime 3 imports) |
| System.IO.Hashing, .NET runtime, .NET for Android | 10.0.8 / 9.0 | MIT | the `System.*`, `Mono.*`, `Java.Interop` assemblies and the runtime `.so` files |

Format references (no code shipped from them unless the entry says so):

| Project | Licence | Used for |
|---|---|---|
| PrimeWorldEditor (Aruki, AxioDL Team) | MIT | the Retro file layouts our `MphRecomp.Core/Import/Retro*` readers follow (via our own Python reference scripts). **Code translated from it: `Import/Retro/Anim.cs` only** (CAnimationLoader, LibCommon CBitStreamInWrapper, CBone::UpdateTransform; board S11), so its notice below applies; the other readers are our own parsers of the layouts it reads |
| metaforce (AxioDL) | MIT | the Prime 3 ANIM layout (`Import/Retro3/Anim3.cs`, a clean re-implementation) and CMDL material flag names (`Import/Retro/Cmdl.cs`); no code taken |
| Dolphin's `docs/WiaAndRvz.md` | (documentation) | the RVZ/WIA format; no Dolphin code was taken (`Import/Disc/WiaReader.cs`) |
| RFC 8878 (Zstandard), the LZMA and bzip2 format descriptions | (specifications) | our own decoders in `Import/Disc/` |

---

## MphRead

Upstream of this fork; also covers our additions (`NOTICE-fork.md`). Its README's acknowledgements (dsgraph,
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

## SixLabors.ImageSharp

Used under the Apache License, Version 2.0 branch of the Six Labors Split License, which applies to software under an
open-source licence and to individuals or companies under USD 1M annual gross revenue (section 2 below); this project
meets both (it is free and MIT-licensed). Apache License 2.0: https://www.apache.org/licenses/LICENSE-2.0

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

## miniaudio (bundled by SoundFlow as `libminiaudio.so`)

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

## System.IO.Hashing, the .NET runtime and .NET for Android

MIT, Copyright (c) .NET Foundation and Contributors. Their own third-party notices:
https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT and
https://github.com/dotnet/android/blob/main/THIRD-PARTY-NOTICES.TXT

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
