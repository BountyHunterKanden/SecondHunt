using System.Collections.Generic;
using MphRead.Formats;
using MphRead.Hud;

namespace MphRead.Entities
{
    // recomp: the Hyper Beam's weapon icons (owner 2026-11-16 picked Y1, a visor in the shape of the owner's own reference
    // drawing: two up-swept wings over a long tapering spike, exactly symmetric), new art in the blaster's way (PlayerPirateBlaster.cs): the weapon menu's slot 7 icon and the HUD's weapon icon
    public partial class PlayerEntity
    {
        // the weapon menu's icon (30x24 centred in rad_wepsel's 32x32): '#' = the edge, 'o' = inside, in the set's Judicator
        // colours (frame 5, the blue family, nearest the Hyper Beam's blue)
        public static readonly string[] _hyperIconMask = new string[]
        {
            "#............................#",
            "#####....................#####",
            "#oooo###..............###oooo#",
            ".#oooooo###........###oooooo#.",
            "..#oooooooo###..###oooooooo#..",
            "...#oooooooooo##oooooooooo#...",
            "....##oooooooooooooooooo##....",
            "......#oooooooooooooooo#......",
            ".......##oooooooooooo##.......",
            ".........#oooooooooo#.........",
            "..........#oooooooo#..........",
            "...........#oooooo#...........",
            "...........#oooooo#...........",
            "...........#oooooo#...........",
            "...........#oooooo#...........",
            "............#oooo#............",
            "............#oooo#............",
            "............#oooo#............",
            "............#oooo#............",
            ".............#oo#.............",
            ".............#oo#.............",
            ".............#oo#.............",
            ".............#oo#.............",
            "..............##..............",
        };

        // the HUD icon's symbol: the same visor raster-scaled to 13 (extract_out/dark_samus_hyper/hyper_icons5.py, Y1)
        private static readonly string[] _hyperHudMask = new string[]
        {
            ".............",
            ".............",
            "###.......###",
            ".###########.",
            "...#######...",
            "....#####....",
            ".....###.....",
            ".....###.....",
            ".....###.....",
            ".....###.....",
            "......#......",
            ".............",
            ".............",
        };

        // the Hyper Beam's blue (Metadata/HyperBeam.cs's beam colour)
        private static readonly ColorRgba _hyperIconColor = new ColorRgba(33, 132, 255, 255);

        public static IReadOnlyList<byte> HyperBeamSelectIcon(HudObject weaponSelect)
        {
            return MaskSelectIcon(weaponSelect, _hyperIconMask, colorFrame: 5);
        }

        public static (IReadOnlyList<byte> Frame, IReadOnlyList<ColorRgba> Palette) HyperBeamWeaponIcon(HudObject weaponIcon)
        {
            return CutWeaponIcon(weaponIcon, _hyperHudMask, colorSlot: 2);
        }
    }
}
