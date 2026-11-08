<p><img src="images/hy-h2-acknowledgements.png" width="100%" alt="Acknowledgements"></p>

Second Hunt is built on **[MphRead](https://github.com/NoneGiven/MphRead)** by NoneGiven, a C# recreation of
_Metroid Prime Hunters_ from reverse engineering. None of this would exist without it.

The license text for everything the app contains is in [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md). You can
also read it in the app, under SECOND HUNT SETTINGS > APP > HELP > CREDITS.

## Thanks

- **[Fruity Prime](https://github.com/liveteklol/Fruity-Prime)** by liveteklol, another MphRead fork, for its netcode.
  Served online matches run on a port of it, with the server in charge, hits checked by the server and a longer lag
  compensation rewind.
- **Limoki**, for the low-poly Dark Samus and her morph ball. Dark Samus seats wear them when no Dark Samus is
  imported. The app builds them from your own game files plus Limoki's edits, so none of the game's art ships with it.
- **Everyone who opened an issue on GitHub** that has since been fixed or added:
  - [@MusicGoat91](https://github.com/MusicGoat91): QUIT SECOND HUNT? on the title screen
    ([#3](https://github.com/BountyHunterKanden/SecondHunt/issues/3)), typing on the on-screen keyboards with a PC
    keyboard ([#4](https://github.com/BountyHunterKanden/SecondHunt/issues/4)), and side mouse buttons repeating
    next/previous weapon ([#2](https://github.com/BountyHunterKanden/SecondHunt/issues/2))
  - [@drencoss](https://github.com/drencoss): visor and helmet opacity
    ([#1](https://github.com/BountyHunterKanden/SecondHunt/issues/1))
  - [@EliParker28](https://github.com/EliParker28): SDL3 controllers on Windows, Switch Pro Controller included
    ([#6](https://github.com/BountyHunterKanden/SecondHunt/issues/6))
  - [@JustinBenedick](https://github.com/JustinBenedick): separate X and Y look invert, for an inverted mouse
    ([#10](https://github.com/BountyHunterKanden/SecondHunt/issues/10))
  - [@Swordman14](https://github.com/Swordman14): asking for a static reticle, which turned into AIM VIEW
    ([#11](https://github.com/BountyHunterKanden/SecondHunt/issues/11))
- **Everyone on Discord**, including finRatman, Limoki and Yaba, and every player who has sent bug reports, logs,
  replays, screenshots and videos.

## MphRead's sources

MphRead credits these, and Second Hunt still uses what came from them:

- dsgraph
- Chemical's [model format notes](https://gitlab.com/ch-mcl/metroid-prime-hunters-file-document/-/blob/master/Model/BinModel.md)
- McKay42's [mph-model-viewer](https://github.com/McKay42/mph-model-viewer) and
  [mph-arc-extractor](https://github.com/McKay42/mph-arc-extractor)
- Barubary's [dsdecmp](https://github.com/Barubary/dsdecmp) (LZ10 compression)
- loveemu's [swav2wav](https://github.com/loveemu/loveemu-lab) (DS sound samples)
- Gericom's [ffmpeg patch](https://lists.ffmpeg.org/pipermail/ffmpeg-devel/2021-March/277774.html) and
  CharlesVanEeckhout's [actimagine decoder](https://github.com/CharlesVanEeckhout/actimagine) (the movies)
- CyberBotX's [NCSF](https://github.com/CyberBotX/NCSF) and [in_xsf](https://github.com/CyberBotX/in_xsf) (the
  game's music player)
- hackyourlife's [mph-viewer](https://github.com/hackyourlife/mph-viewer)

## Reading the Prime games

What Second Hunt learned about the Prime games' files came from these. Unless it says otherwise, no code was taken
from them.

| Project | Used for |
|---|---|
| [PrimeWorldEditor](https://github.com/AxioDL/PrimeWorldEditor) (Aruki, AxioDL) | Retro file layouts. Prime animation reading is translated from it. |
| [metaforce](https://gitlab.axiodl.com/AxioDL/metaforce) (AxioDL) | How Prime 1's particles, swooshes, animation events and Scout Drone laser behave |
| [amuse](https://gitlab.axiodl.com/AxioDL/amuse) (AxioDL) | The MusyX sound macro layout |
| [lzokay](https://github.com/AxioDL/lzokay) (Jack Andersen) | LZO decompression for the _Echoes_ and _Corruption_ imports, ported |
| [Retro Modding Wiki](https://wiki.axiodl.com) and WiiBrew | File layouts |
| Dolphin's RVZ/WIA notes | Reading RVZ and WIA discs |
| Switchbrew | Reading Switch game files |
| Sun Microsystems' G.721 decoder | Prime 1's music, ported |

## Libraries

| Library | Used for |
|---|---|
| [OpenTK](https://github.com/opentk/opentk) and [GLFW](https://github.com/glfw/glfw) | Drawing, and the window on Windows |
| [SDL](https://github.com/libsdl-org/SDL) | Controllers and their gyro on Windows |
| [SoundFlow](https://github.com/LSXPrime/SoundFlow) with [miniaudio](https://github.com/mackron/miniaudio) | Sound |
| [Concentus](https://github.com/lostromb/concentus) | Opus, for voice chat |
| [MoonSharp](https://github.com/moonsharp-devs/moonsharp) | Lua |
| [ImageSharp](https://github.com/SixLabors/ImageSharp) | Images |
| CommunityToolkit.HighPerformance and .NET | Everything else |

The online server also uses Microsoft.Data.Sqlite with SQLite, and the
[LDNOOBW](https://github.com/LDNOOBW/List-of-Dirty-Naughty-Obscene-and-Otherwise-Bad-Words) English word list for the
chat filter.
