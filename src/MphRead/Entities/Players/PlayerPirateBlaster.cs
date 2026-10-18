using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Formats;
using MphRead.Hud;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: the Pirate Blaster on the player (Metadata/PirateBlaster.cs; owner 2026-10-09). A shot is a 3-bolt burst:
    // TryFireWeapon fires bolt 1 (it pays the burst's 5 UA and starts the 20-frame cooldown), then UpdatePirateBlasterBurst
    // fires bolts 2 and 3 BurstGap frames apart, each aimed fresh. The burst stops on death, morphing, a weapon switch or
    // being grabbed. Sim only (replays and network matches run it the same everywhere).
    public partial class PlayerEntity
    {
        // the app's affinity weapon for this seat (the Trooper: the Pirate Blaster); null = the hunter's own
        public BeamType? HostAffinityBeam { get; set; }

        public BeamType AffinityBeam => HostAffinityBeam ?? Weapons.GetAffinityBeam(Hunter);

        // recomp: the app draws every seat's blaster shots in Prime 1's look (the regular Space Pirate's, from the Trooper
        // import: app CampaignRenderer.BlasterFx.cs): BeamProjectileEntity.Spawn sets those shots' HostOwnLook. Draw only.
        public static bool HostOwnPirateBlasterLook { get; set; }
        // ... and plays Prime's shot sound per bolt (MphRead's isn't played: PlayBeamShotSfx) and its hit sound (the shots'
        // HostOwnSfx)
        public static bool HostOwnPirateBlasterSfx { get; set; }
        public static bool HostOwnPirateBlasterHitSfx { get; set; }
        // recomp: where the first-person muzzle flash is drawn (the gun's muzzle, or the host's arm cannon barrel:
        // HostFxMuzzle), so the app can draw its blaster bolts from there. Read only.
        public Vector3 HostFxMuzzlePosition => FxMuzzlePos;
        // recomp: the Pirate Blaster's own weapon key (PlayerControls.PirateBlaster) works; off for a replay recorded before
        // it (MphRecomp MatchSettings.PirateBlasterKey: a guest's blaster pick then did nothing on the host). Default on
        public bool HostPirateBlasterKey { get; set; } = true;
        // recomp: the weapon in the third slot (vanilla: the touch screen's third icon, the Omega Cannon while it's held)
        public BeamType HostThirdSlotWeapon => _weaponSlots[2];

        // the weapon menu's slots (owner 2026-10-09: a 7-sector ring): 7 = the vanilla six + the Pirate Blaster last, in a
        // multiplayer match with the blaster on (MatchStart: MatchSettings.PirateBlaster; old replays read it as off).
        // The campaign and older matches keep the DS six. Read by UpdateWeaponSelect (the DS radial's sectors), the ring
        // (PlayerWeaponRing.cs) and the app's stick-to-sector math (MphRecomp CampaignHost.WheelSector)
        public static bool HostSevenWeaponSlots { get; set; }

        public static int WeaponSlotCount => HostSevenWeaponSlots && GameState.Multiplayer ? 7 : 6;

        // the weapon in DS radial slot i (0 = Volt Driver ... 5 = Shock Coil, 6 = the Pirate Blaster)
        public static BeamType WeaponSlotBeam(int slot) => slot == 6 ? BeamType.PirateBlaster : (BeamType)(slot == 0 ? 1 : slot + 2);

        // bolts of the current burst fired so far (0 = no burst)
        private int _burstShots = 0;

        // TryFireWeapon: a blaster shot went out (bolt 1)
        private void OnPirateBlasterShot(WeaponInfo weapon)
        {
            _burstShots = weapon.Beam == BeamType.PirateBlaster ? 1 : 0;
            if (_burstShots == 1)
            {
                HostPirateBlasterBolts++;
                OnSquadMarkBurst(); // the Trooper's Squad Mark counts this burst's hits (PlayerSquadMark.cs)
            }
        }

        // every frame after ProcessInput; _timeSinceShot counts from bolt 1 (0 on the frame it went out)
        private void UpdatePirateBlasterBurst()
        {
            if (_burstShots == 0)
            {
                return;
            }
            int gap = PirateBlaster.BurstGap * 2; // todo: FPS stuff
            if (_burstShots >= PirateBlaster.BurstBolts || _health == 0 || IsAltForm || AttachedEnemy != null
                || CurrentWeapon != BeamType.PirateBlaster || EquipInfo.Weapon.Beam != BeamType.PirateBlaster
                || _timeSinceShot > (PirateBlaster.BurstBolts - 1) * gap)
            {
                _burstShots = 0;
                return;
            }
            if (_timeSinceShot < _burstShots * gap)
            {
                return;
            }
            Vector3 shotVec = _aimPosition - _muzzlePos;
            if (_disruptedTimer > 0)
            {
                shotVec.X += Fixed.ToFloat((int)Rng.GetRandomInt2(24576) - 12288);
                shotVec.Y += Fixed.ToFloat((int)Rng.GetRandomInt2(24576) - 12288);
                shotVec.Z += Fixed.ToFloat((int)Rng.GetRandomInt2(24576) - 12288);
            }
            shotVec = shotVec.Normalized();
            WeaponInfo curWeapon = EquipInfo.Weapon;
            bool affinity = IsPrimeHunter || curWeapon == PirateBlaster.WeaponMPAffinity;
            EquipInfo.Weapon = affinity ? PirateBlaster.BoltMPAffinity : PirateBlaster.BoltMP;
            BeamSpawnFlags flags = BeamSpawnFlags.NoMuzzle;
            if (_doubleDmgTimer > 0)
            {
                flags |= BeamSpawnFlags.DoubleDamage;
            }
            else if (IsPrimeHunter)
            {
                flags |= BeamSpawnFlags.PrimeHunter;
            }
            BeamResultFlags result = BeamProjectileEntity.Spawn(this, EquipInfo, _muzzlePos, shotVec, flags, NodeRef, _scene);
            EquipInfo.Weapon = curWeapon;
            _burstShots++;
            if (result == BeamResultFlags.NoSpawn)
            {
                return;
            }
            HostPirateBlasterBolts++;
            if (IsMainPlayer)
            {
                HudOnFiredShot();
            }
            if (_muzzleEffect != null)
            {
                _scene.UnlinkEffectEntry(_muzzleEffect);
                _muzzleEffect = null;
            }
            int effectId = Metadata.MuzzleEffectIds[PirateBlaster.BaseIndex(CurrentWeapon)];
            _muzzleEffect = _scene.SpawnEffectGetEntry(effectId, _gunVec2, _gunVec1, FxMuzzlePos);
            // recomp (owner 2026-10-10): with the Prime look the Power Beam's tip blast isn't drawn: the Prime bolt's own glow,
            // starting small at the tip (the app's BlasterFx), is the flash (Prime's pirate has no muzzle sprite: its shot clip
            // only lights the pirate, PART d05a2515)
            if (_muzzleEffect != null && (!IsMainPlayer || HostOwnPirateBlasterLook))
            {
                _muzzleEffect.SetDrawEnabled(false);
            }
            PlayBeamShotSfx(BeamType.PirateBlaster, charged: false, continuous: false, homing: false, amountA: 0);
        }

        // the weapon menu's icon for the blaster (slot 6), new art for rad_wepsel (32x32, 8x8 tiles): three bolts of a
        // burst, tips up-right. '#' = the edge, 'o' = inside. Each hunter's set has its own look (Samus: one flat colour per
        // weapon; Kanden/Spire/Weavel: an edge colour + a fill), so the colours come from that set's Imperialist frame (4,
        // the red family, nearest the blaster's orange-red): the index most used on its edge pixels and inside them
        // (public: ONLINE PLAY's browse rows draw it as the "no extra pickups" icon)
        public static readonly string[] _blasterIconMask = new string[]
        {
            "................................",
            "................................",
            "................................",
            "..............#.................",
            "..........#####.................",
            ".........#oooo#.................",
            "........#ooooo#........###......",
            ".......#ooooo#......###oo#......",
            "......#oooooo#.....#ooooo#......",
            ".....#oooooo#.....#ooooo#.......",
            ".....#ooooo#.....#oooooo#.......",
            "....#ooooo#.....#ooooooo#.......",
            "....#oooo#......#oooooo#........",
            "...#ooo##......#oooooo#.........",
            "...#o##........#ooooo#..........",
            "...##.........#oooo##...........",
            "..............#oo##.............",
            ".............#o##........####...",
            "..............#........##oo#....",
            "......................#oooo#....",
            ".....................#ooooo#....",
            "....................#oooooo#....",
            "...................#oooooo#.....",
            "..................#oooooo#......",
            "..................#ooooo#.......",
            ".................#ooooo#........",
            ".................#ooo##.........",
            "................#oo##...........",
            "................###.............",
            "................................",
            "................................",
            "................................",
        };

        public static IReadOnlyList<byte> PirateBlasterSelectIcon(HudObject weaponSelect)
        {
            int w = weaponSelect.Width;
            int h = weaponSelect.Height;
            (byte edge, byte fill) = IconColors(weaponSelect, frame: 4);
            var data = new byte[w * h];
            for (int y = 0; y < h && y < _blasterIconMask.Length; y++)
            {
                string row = _blasterIconMask[y];
                for (int x = 0; x < w && x < row.Length; x++)
                {
                    byte value = row[x] == '#' ? edge : (row[x] == 'o' ? fill : (byte)0);
                    data[(y / 8) * (w / 8) * 64 + (x / 8) * 64 + (y % 8) * 8 + (x % 8)] = value;
                }
            }
            return data;
        }

        // the HUD's weapon icon for the blaster (hud_weaponicon, next to the ammo bar), new art: the three bolts. Most
        // hunters' icons are a tile in the weapon's colour with its symbol cut out (a rim colour on the cut's left edge);
        // Trace's and Sylux's are the bare symbol. The icon is built from that set's Imperialist icon (the tile shape,
        // rim, where the symbol sits) in the blaster's own colour (its beam's 255,99,33) in a palette slot no frame uses.
        private static readonly string[] _blasterHudMask = new string[]
        {
            "....###......",
            "...####..###.",
            "..####..####.",
            ".####..#####.",
            ".###...####..",
            "###...####...",
            "......##...##",
            "..........###",
            ".........####",
            "........####.",
            ".......####..",
            ".......###...",
            ".......#.....",
        };

        // the animation index of the blaster's frame (added after the file's frames by SetUpWeaponIcon)
        private int _blasterWeaponIconAnim = -1;

        private void SetUpWeaponIcon(HudObject weaponIcon)
        {
            (IReadOnlyList<byte> frame, IReadOnlyList<ColorRgba> palette) = PirateBlasterWeaponIcon(weaponIcon);
            int size = weaponIcon.Width * weaponIcon.Height;
            int count = weaponIcon.CharacterData.Count;
            var data = new byte[count + size];
            for (int i = 0; i < count; i++)
            {
                data[i] = weaponIcon.CharacterData[i];
            }
            for (int i = 0; i < size; i++)
            {
                data[count + i] = frame[i];
            }
            _weaponIconInst.SetCharacterData(data, _scene);
            _weaponIconInst.SetPaletteData(palette, _scene);
            _weaponIconInst.SetAnimationFrames(weaponIcon.AnimParams);
            var frames = new List<int>(_weaponIconInst.AnimFrames!);
            _blasterWeaponIconAnim = frames.Count;
            frames.Add(count / size);
            _weaponIconInst.AnimFrames = frames;
        }

        // the weapon icon's animation index for a beam (the vanilla ones: the beam's index)
        private int WeaponIconAnim(BeamType beam)
        {
            return beam == BeamType.PirateBlaster && _blasterWeaponIconAnim >= 0
                ? _blasterWeaponIconAnim
                : PirateBlaster.BaseIndex(beam);
        }

        public static (IReadOnlyList<byte> Frame, IReadOnlyList<ColorRgba> Palette) PirateBlasterWeaponIcon(HudObject weaponIcon)
        {
            int w = weaponIcon.Width;
            int h = weaponIcon.Height;
            int size = w * h;
            int image = weaponIcon.AnimParams.Count > 4 ? weaponIcon.AnimParams[4].ImageIndex : 0; // the Imperialist
            var src = new byte[w, h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    src[x, y] = weaponIcon.CharacterData[image * size + TiledIndex(w, x, y)];
                }
            }
            // the blaster's colour: the first palette slot after the used ones (no frame uses it)
            var used = new bool[16];
            for (int i = 0; i < weaponIcon.CharacterData.Count; i++)
            {
                used[weaponIcon.CharacterData[i] & 15] = true;
            }
            int top = 15;
            while (top > 1 && !used[top])
            {
                top--;
            }
            var palette = new ColorRgba[weaponIcon.PaletteData.Count];
            for (int i = 0; i < palette.Length; i++)
            {
                palette[i] = weaponIcon.PaletteData[i];
            }
            // transparent pixels reachable from the border are outside; the rest are the symbol's cut
            var outside = new bool[w, h];
            var stack = new Stack<(int, int)>();
            for (int x = 0; x < w; x++)
            {
                stack.Push((x, 0));
                stack.Push((x, h - 1));
            }
            for (int y = 0; y < h; y++)
            {
                stack.Push((0, y));
                stack.Push((w - 1, y));
            }
            while (stack.Count > 0)
            {
                (int x, int y) = stack.Pop();
                if (x < 0 || y < 0 || x >= w || y >= h || outside[x, y] || src[x, y] != 0)
                {
                    continue;
                }
                outside[x, y] = true;
                stack.Push((x + 1, y));
                stack.Push((x - 1, y));
                stack.Push((x, y + 1));
                stack.Push((x, y - 1));
            }
            bool Outside(int x, int y) => x < 0 || y < 0 || x >= w || y >= h || outside[x, y];
            var counts = new int[256];
            int holes = 0;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (outside[x, y])
                    {
                        continue;
                    }
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                    if (src[x, y] == 0)
                    {
                        holes++;
                    }
                    else
                    {
                        counts[src[x, y]]++;
                    }
                }
            }
            // the tile's colour (the most pixels) and the hunter's rim colour (the most of the rest)
            byte fill = (byte)Array.IndexOf(counts, counts.Max());
            counts[fill] = 0;
            byte rim = counts.Max() > 0 ? (byte)Array.IndexOf(counts, counts.Max()) : fill;
            byte blaster = fill;
            if (top < 15)
            {
                blaster = (byte)(top + 1);
                palette[blaster] = new ColorRgba(255, 99, 33, 255); // the blaster's beam colour (RGB555 31,12,4)
            }
            var dst = new byte[w, h];
            bool tile = holes >= 8;
            if (tile)
            {
                // the tile without its symbol, in the blaster's colour; the rim along the tile's edge (pixels of another
                // colour within 2 of the outside, not touching the old cut) stays
                bool Hole(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && !outside[x, y] && src[x, y] == 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (outside[x, y])
                        {
                            continue;
                        }
                        bool nearOutside = false;
                        for (int dy = -2; dy <= 2 && !nearOutside; dy++)
                        {
                            for (int dx = -2; dx <= 2 && !nearOutside; dx++)
                            {
                                nearOutside = Outside(x + dx, y + dy);
                            }
                        }
                        bool byCut = Hole(x - 1, y) || Hole(x + 1, y) || Hole(x, y - 1) || Hole(x, y + 1);
                        byte value = src[x, y];
                        dst[x, y] = value != 0 && value != fill && nearOutside && !byCut ? value : blaster;
                    }
                }
            }
            // the symbol, centred where the Imperialist's sits (the tile's centre, or the bare symbol's); by halfway between its
            // box and its weight (the mean of its pixels): the bolts lean up-right, so a centred box reads up-right (owner
            // 10-10) and a centred weight reads down-left
            int mw = _blasterHudMask[0].Length;
            int mh = _blasterHudMask.Length;
            float sumX = 0, sumY = 0;
            int count = 0;
            for (int my = 0; my < mh; my++)
            {
                for (int mx = 0; mx < mw; mx++)
                {
                    if (_blasterHudMask[my][mx] == '#')
                    {
                        sumX += mx + 0.5f;
                        sumY += my + 0.5f;
                        count++;
                    }
                }
            }
            int ox = (int)MathF.Round((minX + maxX + 1) / 2f - (mw / 2f + sumX / count) / 2);
            int oy = (int)MathF.Round((minY + maxY + 1) / 2f - (mh / 2f + sumY / count) / 2);
            for (int my = 0; my < mh; my++)
            {
                for (int mx = 0; mx < mw; mx++)
                {
                    int x = ox + mx;
                    int y = oy + my;
                    if (_blasterHudMask[my][mx] == '#' && x >= 0 && y >= 0 && x < w && y < h)
                    {
                        dst[x, y] = tile ? (byte)0 : blaster;
                    }
                }
            }
            if (tile)
            {
                // the cut's shading: the rim colour on the tile pixel left of a cut
                for (int y = 0; y < h; y++)
                {
                    for (int x = 1; x < w; x++)
                    {
                        if (dst[x, y] == 0 && !outside[x, y] && dst[x - 1, y] == blaster)
                        {
                            dst[x - 1, y] = rim;
                        }
                    }
                }
            }
            var result = new byte[size];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    result[TiledIndex(w, x, y)] = dst[x, y];
                }
            }
            return (result, palette);
        }

        // a pixel's place in 8x8-tiled character data (HudObjectInstance.DoTexture's layout)
        private static int TiledIndex(int width, int x, int y)
        {
            return (y / 8) * (width / 8) * 64 + (x / 8) * 64 + (y % 8) * 8 + (x % 8);
        }

        private static (byte Edge, byte Fill) IconColors(HudObject obj, int frame)
        {
            int w = obj.Width;
            int h = obj.Height;
            int Pixel(int x, int y)
            {
                if (x < 0 || y < 0 || x >= w || y >= h)
                {
                    return 0;
                }
                return obj.CharacterData[frame * w * h + (y / 8) * (w / 8) * 64 + (x / 8) * 64 + (y % 8) * 8 + (x % 8)];
            }
            var edges = new int[256];
            var fills = new int[256];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int value = Pixel(x, y);
                    if (value == 0)
                    {
                        continue;
                    }
                    bool edge = Pixel(x - 1, y) == 0 || Pixel(x + 1, y) == 0 || Pixel(x, y - 1) == 0 || Pixel(x, y + 1) == 0;
                    (edge ? edges : fills)[value]++;
                }
            }
            int edgeIndex = Array.IndexOf(edges, edges.Max());
            int fillIndex = fills.Max() > 0 ? Array.IndexOf(fills, fills.Max()) : edgeIndex;
            if (edges.Max() == 0)
            {
                edgeIndex = fillIndex;
            }
            return ((byte)edgeIndex, (byte)fillIndex);
        }

        // test hooks (Tools -pirateblastertest): pick up the blaster's item (or the affinity item), bolts fired, ammo
        public void HostPickUpWeapon(ItemType item)
        {
            PickUpWeapon(item);
        }

        public int HostPirateBlasterBolts { get; private set; }

        public int HostAmmo(int type) => _ammo[type];
    }
}
