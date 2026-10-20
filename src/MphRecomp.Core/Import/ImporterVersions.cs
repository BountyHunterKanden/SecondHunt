using System;
using System.Collections.Generic;

namespace MphRecomp.Import
{
    // MR1 V0 (docs/MULTIPLAYER_RELIABILITY.md section 2): one number per import kind. Bump a kind's number whenever a
    // change to its importer changes what it writes; every item made by an older number then reads as stale (UPDATE on
    // IMPORT GAMES) and isn't used until it's imported again (ImportedContent.Usable, owner decision D3). Each import
    // stamps the number into <root>/import/<game>.json ("importer"); a missing stamp reads as 1, so installs from before
    // the stamps aren't forced to re-import (D4).
    //
    // History (newest last; one line per bump):
    //   all kinds  1  the importers as of 2026-10-10 (stamps introduced): guns, hd, bodies (incl. fx/), arenas (incl.
    //                 ArenaNav v2 + pockets), music
    //   arenas     2  2026-10-10 (T6 + T7): Spires' pillars drawn (Info.Statics: actor models solid in the collision but
    //                 not in the render mesh); ArenaNav v3: NavLinks (jump/double/gap/boost links), Pipeline flooded cave
    //                 in the graph (96 -> 183 nodes)
    //   arenas     3  2026-10-16: Shooting Gallery's manned turret (EchoesTurret: arena.json Turret, hd/turret.json, the
    //                 turret's, hologram's and slot ring's placed passes in arena.bin)
    //   arenas     4  2026-10-16: the turret's Echoes sounds (EchoesTurret.ImportSounds: hd/turret_<role>.wav, turret.json
    //                 Sounds; the recipe's audioPak, AudioGrp.pak)
    //   arenas     5  2026-10-16: the turret's Echoes effects, shot look, seat HUD and hologram glow (EchoesTurret.ImportFx /
    //                 ImportHud: hd/turret_fx/; the recipe's hudPak, NoARAM.pak), the slot's hint cameras, the Player Kill box
    //   arenas     6  2026-10-16: the turret lift's solid base (arena.json Turret BaseMin/BaseMax/BaseRise/BaseTime, Echoes'
    //                 'Dummy Base' PLAT)
    //   arenas     7  2026-10-16: the turret effects' model particles (PMDL: the lift rings' and smoke's CMDLs placed in
    //                 arena.bin, turret.json ModelPasses)
    //   arenas     8  2026-10-16: Shooting Gallery's grates break in Echoes' own debris (turret.json Fx role grate, kind
    //                 debris: the DebrisExtended pieces' CMDLs placed in arena.bin, their death PART; sound turret_grate.wav)
    //   arenas     9  2026-10-17: Sidehopper Station's grates break the same way (a turret.json of no pieces: grate Fx + sound
    //                 only); the turret's animation events' real durations (ball glow on while raised + ballglowdown, muzzle 2,
    //                 smoke 10 frames)
    //   bodies     2  2026-10-19: the Scout Drone bodies' Prime deploy alert (fx/sfx_632.wav; MusyX Loop renders its four beeps)
    public static class ImporterVersions
    {
        static readonly Dictionary<string, int> Kinds = new(StringComparer.Ordinal)
        {
            ["guns"] = 1,
            ["hd"] = 1,
            ["bodies"] = 2,
            ["arenas"] = 9,
            ["music"] = 1,
        };

        public static int For(string kind) => Kinds.TryGetValue(kind, out int v) ? v : 1;
    }
}
