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
    //   arenas    11  2026-10-31: ArenaNav jumps up overhanging ledges (TakeOff) + long pocket walks: Mining Plaza's upper floors
    //                 and east bridge in the bots' graph (141 -> 195 nodes), Spider Complex +94 walk links, rest unchanged
    //   arenas    12  2026-10-31: ArenaNav DoubleRise 3.9 -> 3.6 (double-jump ledge links a bot can't make dropped; a Noxus
    //                 bot stuck at Mining Plaza's 3.7 ledge), Mining Plaza 195 -> 199 nodes
    //   arenas    13  2026-10-31: collision marks faces that aren't Echoes floor material (CollisionFlags.Bit12), which a
    //                 player can't stand on below a 0.85 slope (Mining Plaza's centre structure was climbable)
    //   arenas    14  2026-10-31: Mining Plaza's Luminoth gate opens mid-match (arena.json Gate: gate/hologram/glow passes, collision,
    //                 Echoes' Gate Control timeline; pillar.json the gate's dust + sounds)
    //   arenas    15  2026-10-31: Mining Plaza's gate jingle (recipe "jingle": Audio/evt_x_event_00.dsp -> pillar_gate_jingle.wav)
    //   arenas    16  2026-11-01: Mining Plaza's lore hologram and translator rings spin (arena.json Statics SpinXf/Axis/Degrees/Period)
    //   arenas    17  2026-11-01: Mining Plaza's sand crates (arena.json SandCrates: ready/flinch frame passes, collision, debris
    //                 pieces; pillar.json the crates' trail/flash/dust/debris effects + break/bounce sounds)
    //   arenas    18  2026-11-01: Mining Plaza's bot graph rides the shipped placement file's jump pads (and its spawns anchor it)
    //   arenas    19  2026-11-01: Mining Plaza's graph adds node data sets for octolith carriers, never routed over the placed jump pads
    //   arenas    20  2026-11-01: Mining Plaza's sand crates baked at 30 fps (was 12): smoother sway and flinch
    //   bodies     2  2026-10-19: the Scout Drone bodies' Prime deploy alert (fx/sfx_632.wav; MusyX Loop renders its four beeps)
    public static class ImporterVersions
    {
        static readonly Dictionary<string, int> Kinds = new(StringComparer.Ordinal)
        {
            ["guns"] = 1,
            ["hd"] = 1,
            ["bodies"] = 2,
            ["arenas"] = 20,
            ["music"] = 1,
        };

        public static int For(string kind) => Kinds.TryGetValue(kind, out int v) ? v : 1;
    }
}
