using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead
{
    // recomp: First Hunt's own HUD (docs/FIRST_HUNT_PORT.md P3; fh_research p3_hud.md). The cart draws it with the DS 2D
    // engine, not models: OBJ sprites whose frames and 4bpp tiles are in overlay 0 (a graphics pack, loaded at 0x20adde0,
    // with the OBJ palettes at its start), and two tile fonts in arm9. Both come from the user's import; this reads them,
    // turns each sprite frame / glyph into pixels once, and records the HUD as quads through Scene.HostHudQuad, like
    // MphRead's own recorded HUD. Positions are the cart's (DS top screen, 256 x 192), placed on a wide screen by
    // height: things on the left keep their distance from the left edge, on the right from the right edge.
    //   - main HUD 0x20152bc: energy meter 0x2015860 (cfg 0x2072050), ammo meter (cfg 0x2072088), weapon icon 0x2071f34,
    //     DAMAGE (double damage), the morph ball row 0x2016008 (bombs, boost)
    //   - TIME 0x2016564, SCORE + combo 0x2016338, Survivor kills 0x20161ec
    //   - the end banner 0x202db00 (typewriter 0x202ac38, 1 character a frame), instead of the HUD
    //   - the visor frame (BG1-3, DrawVisorFrame) under the sprites
    // todo: the visor frame's hit effect, the weapon name panel and the HUD event animations (0x2014cf8),
    // the lock-on sprite, HUD sway, the bottom screen (radar, big score)
    public static class FirstHuntHud
    {
        private const int Arm9Address = 0x2004000;
        private const int Overlay0Address = 0x20adde0;

        // sprite records (arm9): {s16 x, s16 y, u8, u8 frame, u16 frames, frame OAM attrs, affine, tiles}
        private const int EnergyTank = 0x2071f48, EnergyBar = 0x2071d90, AmmoTank = 0x2071d7c, AmmoBar = 0x2071da4;
        private const int WeaponIcon = 0x2071f34, DamageIcon = 0x2071db8, BombIcon = 0x2071f20, BoostGauge = 0x2071df4;
        private const int EnergyConfig = 0x2072050, AmmoConfig = 0x2072088;
        private const int SmallFont = 0x2073b58, BigFont = 0x2073b48;
        private const int TextPalette = 15, EnergyPalette = 3, AmmoPalette = 4;

        private static byte[]? _arm9 = null;
        private static byte[]? _overlay0 = null;
        private static readonly Dictionary<long, Glyph> _cache = new Dictionary<long, Glyph>();

        public static bool Loaded => _arm9 != null && _overlay0 != null;

        // eased positions (cart 0x2013e38): energy row, the TIME/SCORE/DAMAGE row, the morph ball row; in DS pixels
        private static float _energyY = 25;
        private static float _rowY = 44;
        private static float _morphY = 190;
        private static int _bannerCounter = 0;

        private sealed class Glyph
        {
            public readonly Scene.HostHudSprite Sprite;
            public readonly int OffsetX;
            public readonly int OffsetY;

            public Glyph(Scene.HostHudSprite sprite, int offsetX, int offsetY)
            {
                Sprite = sprite;
                OffsetX = offsetX;
                OffsetY = offsetY;
            }
        }

        public static void Load(byte[] arm9, byte[] overlay0)
        {
            _arm9 = arm9;
            _overlay0 = overlay0;
            _cache.Clear();
            _visorFrame = null;
        }

        private static int Arm9Int(int address)
        {
            return BinaryPrimitives.ReadInt32LittleEndian(_arm9.AsSpan(address - Arm9Address));
        }

        private static ushort Arm9UShort(int address)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(_arm9.AsSpan(address - Arm9Address));
        }

        private static byte Arm9Byte(int address) => _arm9![address - Arm9Address];

        // a cart pointer into arm9 or overlay 0
        private static ReadOnlySpan<byte> Data(int address, int length)
        {
            if (address >= Overlay0Address && address - Overlay0Address + length <= _overlay0!.Length)
            {
                return _overlay0.AsSpan(address - Overlay0Address, length);
            }
            return _arm9.AsSpan(address - Arm9Address, length);
        }

        public static string Arm9String(int address)
        {
            var sb = new StringBuilder();
            for (int i = address - Arm9Address; i < _arm9!.Length && _arm9[i] != 0; i++)
            {
                sb.Append((char)_arm9[i]);
            }
            return sb.ToString();
        }

        // the main engine's OBJ palettes (overlay 0 +0): 16 x 16 RGB555, index 0 clear
        private static ColorRgba Color(int palette, int index)
        {
            if (index == 0)
            {
                return new ColorRgba(0, 0, 0, 0);
            }
            ushort c = BinaryPrimitives.ReadUInt16LittleEndian(_overlay0.AsSpan((palette * 16 + index) * 2));
            int r = c & 0x1F, g = c >> 5 & 0x1F, b = c >> 10 & 0x1F;
            return new ColorRgba((byte)(r << 3 | r >> 2), (byte)(g << 3 | g >> 2), (byte)(b << 3 | b >> 2), 255);
        }

        // 4bpp tiles, mapped 1D (tile after tile, row by row of tiles), to pixels
        private static Scene.HostHudSprite Tiles(ReadOnlySpan<byte> tiles, int width, int height, int palette, bool flipX = false, bool flipY = false)
        {
            var pixels = new ColorRgba[width * height];
            int across = width / 8;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int tile = y / 8 * across + x / 8;
                    int at = tile * 32 + y % 8 * 4 + x % 8 / 2;
                    int index = at < tiles.Length ? (x % 2 == 0 ? tiles[at] & 0xF : tiles[at] >> 4) : 0;
                    int px = flipX ? width - 1 - x : x;
                    int py = flipY ? height - 1 - y : y;
                    pixels[py * width + px] = Color(palette, index);
                }
            }
            return new Scene.HostHudSprite { Width = width, Height = height, Pixels = pixels };
        }

        private static readonly int[,] _objSizes =
        {
            { 8, 8 }, { 16, 16 }, { 32, 32 }, { 64, 64 },  // square
            { 16, 8 }, { 32, 8 }, { 32, 16 }, { 64, 32 },  // wide
            { 8, 16 }, { 8, 32 }, { 16, 32 }, { 32, 64 }   // tall
        };

        // one frame of a sprite record (0x2005dfc: OAM attrs = attrs[frame], tiles = tiles + frame * object bytes)
        private static Glyph SpriteFrame(int record, int frame)
        {
            long key = (long)record << 16 | (uint)frame;
            if (_cache.TryGetValue(key, out Glyph? glyph))
            {
                return glyph;
            }
            int attrs = Arm9Int(record + 0xC);
            int tiles = Arm9Int(record + 0x10);
            ReadOnlySpan<byte> oam = Data(attrs + frame * 8, 8);
            ushort attr0 = BinaryPrimitives.ReadUInt16LittleEndian(oam);
            ushort attr1 = BinaryPrimitives.ReadUInt16LittleEndian(oam[2..]);
            ushort attr2 = BinaryPrimitives.ReadUInt16LittleEndian(oam[4..]);
            int shape = attr0 >> 14 & 3, size = attr1 >> 14 & 3;
            int width = _objSizes[Math.Min(shape, 2) * 4 + size, 0];
            int height = _objSizes[Math.Min(shape, 2) * 4 + size, 1];
            int bytes = width * height / 2;
            bool affine = (attr0 & 0x100) != 0;
            Scene.HostHudSprite sprite = Tiles(Data(tiles + frame * bytes, bytes), width, height, attr2 >> 12,
                flipX: !affine && (attr1 & 0x1000) != 0, flipY: !affine && (attr1 & 0x2000) != 0);
            int offX = attr1 & 0x1FF;
            if (offX >= 0x100)
            {
                offX -= 0x200;
            }
            glyph = new Glyph(sprite, offX, (sbyte)(attr0 & 0xFF));
            _cache[key] = glyph;
            return glyph;
        }

        // a font record {u8* widths, u8* tiles, u32 obj attrs, s8 first char} (0x202aea0): small 8x8, big digits 16x16
        private static Glyph? Character(int font, int palette, char c)
        {
            long key = (long)font << 24 | (long)palette << 16 | c;
            if (_cache.TryGetValue(key, out Glyph? glyph))
            {
                return glyph;
            }
            int first = (sbyte)Arm9Byte(font + 0xC);
            int tiles = Arm9Int(font + 4);
            int size = font == BigFont ? 16 : 8;
            int index = c - first;
            if (index < 0 || index >= (font == BigFont ? 10 : 96))
            {
                return null;
            }
            int bytes = size * size / 2;
            glyph = new Glyph(Tiles(Data(tiles + index * bytes, bytes), size, size, palette), 0, 0);
            _cache[key] = glyph;
            return glyph;
        }

        private static int Advance(int font, char c)
        {
            if (font == BigFont)
            {
                return c == ' ' ? 12 : 16;
            }
            int index = c - (sbyte)Arm9Byte(font + 0xC);
            if (c == ' ' || index < 0 || index >= 96)
            {
                return 6;
            }
            return Arm9Byte(Arm9Int(font) + index);
        }

        private enum Anchor
        {
            Left,
            Centre,
            Right
        }

        private static Scene _scene = null!;
        private static float _scale;

        // a DS top-screen position to NDC: scaled by height, kept from the edge its side of the screen is on
        private static void Quad(Scene.HostHudSprite sprite, float x, float y, Anchor anchor, float alpha = 1)
        {
            float w = _scene.Size.X;
            float h = _scene.Size.Y;
            float px = anchor switch
            {
                Anchor.Left => x * _scale,
                Anchor.Right => w - (256 - x) * _scale,
                _ => w / 2 + (x - 128) * _scale
            };
            float py = y * _scale;
            float left = px / (w / 2) - 1;
            float right = (px + sprite.Width * _scale) / (w / 2) - 1;
            float top = 1 - py / (h / 2);
            float bottom = 1 - (py + sprite.Height * _scale) / (h / 2);
            _scene.HostHudQuad(sprite, left, right, top, bottom, alpha);
        }

        // a group (a meter, a label and its number) keeps one anchor, so its parts stay together
        private static Anchor? _group = null;

        private static Anchor AnchorOf(float x) => _group ?? (x < 96 ? Anchor.Left : x > 160 ? Anchor.Right : Anchor.Centre);

        private static void Sprite(int record, int frame, float x, float y)
        {
            Glyph glyph = SpriteFrame(record, frame);
            Quad(glyph.Sprite, x + glyph.OffsetX, y + glyph.OffsetY, AnchorOf(x));
        }

        // 0x202aea0: align 0 left, 2 centred (each line), else right (x = the right edge); line height 12 / 24
        private static void Text(int font, int palette, float x, float y, int align, string text, int count = Int32.MaxValue)
        {
            Anchor anchor = AnchorOf(x);
            string[] lines = text.Split('\n');
            int shown = 0;
            foreach (string line in lines)
            {
                int width = 0;
                foreach (char c in line)
                {
                    width += Advance(font, c);
                }
                float cx = align == 0 ? x : align == 2 ? x - width / 2 : x - width;
                foreach (char c in line)
                {
                    if (shown++ >= count)
                    {
                        return;
                    }
                    Glyph? glyph = c == ' ' ? null : Character(font, palette, c);
                    if (glyph != null)
                    {
                        Quad(glyph.Sprite, cx, y, anchor);
                    }
                    cx += Advance(font, c);
                }
                y += font == BigFont ? 24 : 12;
            }
        }

        private static float Ease(float value, float target, float step)
        {
            return value < target ? MathF.Min(value + step, target) : MathF.Max(value - step, target);
        }

        // Scene.RecordHud while a First Hunt level runs (once per 60 Hz step)
        public static void Record(Scene scene, PlayerEntity player)
        {
            if (!Loaded)
            {
                return;
            }
            _scene = scene;
            _scale = scene.Size.Y / 192f;
            FhMode mode = FirstHuntMission.Mode;
            bool morph = player.IsAltForm || mode == FhMode.Morphball;
            if (FirstHuntMission.Ending)
            {
                // the banner leaves the BGs as they were
                if (!morph)
                {
                    DrawVisorFrame();
                }
                DrawBanner();
                return;
            }
            _bannerCounter = 0;
            if (player.Health == 0)
            {
                return;
            }
            if (!morph)
            {
                DrawVisorFrame();
            }
            // todo: FPS stuff (the cart eases 1 px a frame, 2 for the morph row)
            _energyY = Ease(_energyY, morph ? 15 : 25, 0.5f);
            _rowY = Ease(_rowY, morph ? 24 : 44, 0.5f);
            _morphY = Ease(_morphY, morph ? 160 : 190, 1);
            if (morph)
            {
                DrawMorphRow(player);
            }
            else
            {
                Meter(238, 138, player.FirstHuntAmmoPool, AmmoPalette, AmmoConfig);
                // weapon icon frames (table 0x2071fec by slot): Power Beam 0, Missile 1, Electro Lob 2
                int frame = player.CurrentWeapon == BeamType.Missile ? 1 : player.CurrentWeapon == FirstHuntRules.ElectroLob ? 2 : 0;
                Sprite(WeaponIcon, frame, 218, 147);
            }
            if (player.FirstHuntDoubleDamage)
            {
                // 0x2015678: the icon (anim 0x2071d34) and DAMAGE; Survivor moves it under its kills
                int dx = mode == FhMode.Survivor ? -4 : 0, dy = mode == FhMode.Survivor ? 36 : 0;
                Sprite(DamageIcon, 0, 207 + dx, _rowY - 22 + dy);
                Text(SmallFont, TextPalette, 219 + dx, _rowY + 3 + dy, 2, Arm9String(0x2072168));
            }
            Meter(93, _energyY, player.Health, EnergyPalette, EnergyConfig);
            if (mode == FhMode.Survivor)
            {
                // 0x20161ec
                Text(BigFont, TextPalette, 20, _rowY - 16, 0, $"{FirstHuntMission.Kills[player.SlotIndex]:000}");
                Text(SmallFont, TextPalette, 20, _rowY + 3, 0, "total");
                Text(SmallFont, TextPalette, 20, _rowY + 11, 0, "kills");
            }
            else if (FirstHuntMission.Clock >= 0)
            {
                DrawTime(mode);
            }
            if (mode <= FhMode.Survivor)
            {
                DrawScore(player.SlotIndex);
            }
            else if (mode == FhMode.Multiplayer)
            {
                DrawMatchScore(player.SlotIndex);
            }
        }

        // the meter (0x2015860): five tanks of 100 above a bar of 9 pieces for the rest, with its number and label
        private static void Meter(float x, float y, int value, int palette, int config)
        {
            _group = AnchorOf(x + 36);
            bool horizontal = Arm9Int(config) != 0;
            int perTank = Arm9Int(config + 4);
            int tanks = Arm9Int(config + 8);
            int length = Arm9Int(config + 0xC);
            int step = Arm9Int(config + 0x10);
            int full = 0;
            int rest = value;
            while (full < tanks && rest >= perTank)
            {
                full++;
                rest -= perTank;
            }
            int fill = rest * 100000 / (perTank * 99000 / length); // (the cart's 72/99)
            if (fill == 0 && rest != 0)
            {
                fill = 1;
            }
            Text(SmallFont, palette, x + Arm9Int(config + 0x1C), y + Arm9Int(config + 0x20), 1, $"{rest:00}");
            int label = Arm9Int(config + 0x2C);
            if (label != 0)
            {
                Text(SmallFont, palette, x + Arm9Int(config + 0x24), y + Arm9Int(config + 0x28), 0, Arm9String(label));
            }
            int tankRecord = Arm9Int(config + 0x30);
            int barRecord = Arm9Int(config + 0x34);
            float tx = x + Arm9Int(config + 0x14);
            float ty = y + Arm9Int(config + 0x18);
            for (int i = 0; i < tanks * 2 - 1; i++)
            {
                Sprite(tankRecord, i % 2 == 1 ? 2 : i / 2 < full ? 1 : 0, tx, ty);
                if (horizontal)
                {
                    tx += step;
                }
                else
                {
                    ty -= step;
                }
            }
            int pieces = length / 8;
            for (int i = 0; i < pieces; i++)
            {
                int frame = i < fill / 8 ? 0 : i == fill / 8 ? 8 - (fill & 7) : 8;
                Sprite(barRecord, frame, x, y);
                if (horizontal)
                {
                    x += 8;
                }
                else
                {
                    y -= 8;
                }
            }
            _group = null;
        }

        private static void DrawMorphRow(PlayerEntity player)
        {
            // 0x2016008: three bombs (frame 1 while that many are ready) and "bombs", the boost gauge and "boost"
            float x = 213;
            for (int i = 3; i > 0; i--)
            {
                Sprite(BombIcon, player.FirstHuntBombs >= i ? 1 : 0, x, _morphY);
                x -= 18;
            }
            Text(SmallFont, TextPalette, 186, _morphY + 18, 0, Arm9String(0x2072150));
            // the gauge (frame 0 idle; the cart animates 1-4 while charging, 0x2071d64; here by the charge)
            Sprite(BoostGauge, player.FirstHuntBoostFrame, 29, _morphY);
            Text(SmallFont, TextPalette, 28, _morphY + 18, 0, Arm9String(0x2072158));
        }

        private static void DrawTime(FhMode mode)
        {
            // 0x2016564: the clock in DS frames; Morph Ball shows seconds and hundredths, the rest minutes and seconds
            int clock = Math.Max(FirstHuntMission.Clock, 0) / 2; // todo: FPS stuff
            int seconds = clock / 30;
            if (mode == FhMode.Morphball)
            {
                Text(BigFont, TextPalette, 46, _rowY - 16, 1, $"{seconds}");
                Text(SmallFont, TextPalette, 46, _rowY - 16, 0, $"{clock % 30 * 1000 / 300:00}");
            }
            else
            {
                Text(SmallFont, TextPalette, 20, _rowY - 16, 0, $"{seconds / 60}:");
                Text(BigFont, TextPalette, 32, _rowY - 16, 0, $"{seconds % 60:00}");
            }
            Text(SmallFont, TextPalette, 29, _rowY + 3, 0, "TIME");
        }

        private static void DrawScore(int slot)
        {
            // 0x2016338: the banked score, the running combo ("count x value" from two), SCORE
            Text(SmallFont, TextPalette, 240, _rowY - 16, 1, $"{FirstHuntMission.Score[slot]:00000}");
            int count = FirstHuntMission.ComboCount[slot];
            int value = FirstHuntMission.ComboValue[slot];
            if (value != 0)
            {
                Text(SmallFont, TextPalette, 219, _rowY - 7, 2, count > 1 ? $"{count}x{value}" : $"{value}");
            }
            Text(SmallFont, TextPalette, 219, _rowY + 3, 2, Arm9String(0x2072180));
        }

        // a deathmatch's score and place (the cart's bottom panel 0x2016c84: the score, rank + 1 "of" the player count),
        // here where the training games' score is, as the one screen has no bottom panel
        private static void DrawMatchScore(int slot)
        {
            int players = 0;
            for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
            {
                if (PlayerEntity.Players[i].LoadFlags.TestFlag(LoadFlags.Active))
                {
                    players++;
                }
            }
            Text(BigFont, TextPalette, 240, _rowY - 16, 1, $"{GameState.Points[slot]}");
            Text(SmallFont, TextPalette, 219, _rowY + 3, 2, $"{FirstHuntMission.Rank(slot) + 1} of {players}");
        }

        // The visor frame: three 4bpp text BGs of the main engine, between the 3D (BG0) and the sprites, set up with the
        // level by 0x202e0c0 from the pointer table at 0x202e360 {BG palettes 0-2, BG1 map, BG1 tiles, -, BG2 map, -, BG2
        // tiles, BG3 map, BG3 tiles}: BG1 the green brackets (256 x 256, 24 rows copied, scroll 0, alpha-blended over
        // the 3D at EVA 15 / EVB 0, so 15/16 bright), BG2 the grey helmet rim and BG3 the dark band under it (512 x 256
        // maps whose tile base and palette the loader adds, both scrolled to 128, 32: measured in BizHawk, the cart
        // never moves them). Drawn BG3, BG1, BG2 (priorities 2, 1, 0). The cart turns BG1-3 off in Morph Ball and while
        // dead (0x20153a8, 0x200bbf8), as Record does.
        private const int VisorFrameTable = 0x202e360;
        // where a wide screen splits the 256-wide frame: the edges keep to their sides of the screen like the HUD, the
        // middle stays centred, and the one-pixel columns at the seams stretch across the gaps
        private const int VisorSeamLeft = 80, VisorSeamRight = 176;
        private static Scene.HostHudSprite[]? _visorFrame = null;

        private static ColorRgba BgColor(int palette, int index)
        {
            ushort c = BinaryPrimitives.ReadUInt16LittleEndian(Data(palette + index * 2, 2));
            int r = c & 0x1F, g = c >> 5 & 0x1F, b = c >> 10 & 0x1F;
            return new ColorRgba((byte)(r << 3 | r >> 2), (byte)(g << 3 | g >> 2), (byte)(b << 3 | b >> 2), 255);
        }

        // one BG's visible 256 x 192 window into pixels (index 0 clear); map entries {tile 10, h flip, v flip, -}
        private static void DrawBg(ColorRgba[] pixels, int map, int tiles, int palette, int width, int scrollX, int scrollY,
            int rows, float brightness = 1)
        {
            for (int y = 0; y < 192; y++)
            {
                int my = (y + scrollY) % 256;
                if (my / 8 >= rows)
                {
                    continue;
                }
                for (int x = 0; x < 256; x++)
                {
                    int mx = (x + scrollX) % width;
                    ushort entry = BinaryPrimitives.ReadUInt16LittleEndian(Data(map + mx / 256 * 0x800 + (my / 8 * 32 + mx % 256 / 8) * 2, 2));
                    int px = (entry & 0x400) != 0 ? 7 - mx % 8 : mx % 8;
                    int py = (entry & 0x800) != 0 ? 7 - my % 8 : my % 8;
                    byte pair = Data(tiles + (entry & 0x3FF) * 32 + py * 4 + px / 2, 1)[0];
                    int index = px % 2 == 0 ? pair & 0xF : pair >> 4;
                    if (index != 0)
                    {
                        ColorRgba c = BgColor(palette, index);
                        pixels[y * 256 + x] = brightness == 1 ? c
                            : new ColorRgba((byte)(c.Red * brightness), (byte)(c.Green * brightness), (byte)(c.Blue * brightness), 255);
                    }
                }
            }
        }

        private static Scene.HostHudSprite Crop(ColorRgba[] pixels, int x0, int x1)
        {
            var part = new ColorRgba[(x1 - x0) * 192];
            for (int y = 0; y < 192; y++)
            {
                Array.Copy(pixels, y * 256 + x0, part, y * (x1 - x0), x1 - x0);
            }
            return new Scene.HostHudSprite { Width = x1 - x0, Height = 192, Pixels = part };
        }

        // [whole, left, middle, right, left seam column, right seam column]
        private static Scene.HostHudSprite[] VisorFrame()
        {
            if (_visorFrame != null)
            {
                return _visorFrame;
            }
            int Ptr(int index) => Arm9Int(VisorFrameTable + index * 4);
            var pixels = new ColorRgba[256 * 192];
            DrawBg(pixels, Ptr(9), Ptr(10), Ptr(2), 512, 128, 32, 32);
            DrawBg(pixels, Ptr(3), Ptr(4), Ptr(0), 256, 0, 0, 24, brightness: 15 / 16f);
            DrawBg(pixels, Ptr(6), Ptr(8), Ptr(1), 512, 128, 32, 32);
            _visorFrame = new[]
            {
                new Scene.HostHudSprite { Width = 256, Height = 192, Pixels = pixels },
                Crop(pixels, 0, VisorSeamLeft),
                Crop(pixels, VisorSeamLeft, VisorSeamRight),
                Crop(pixels, VisorSeamRight, 256),
                Crop(pixels, VisorSeamLeft - 1, VisorSeamLeft),
                Crop(pixels, VisorSeamRight, VisorSeamRight + 1)
            };
            return _visorFrame;
        }

        // a sprite stretched over screen pixels [left, right), full DS height
        private static void Span(Scene.HostHudSprite sprite, float left, float right)
        {
            float w = _scene.Size.X;
            _scene.HostHudQuad(sprite, left / (w / 2) - 1, right / (w / 2) - 1, 1, -1);
        }

        private static void DrawVisorFrame()
        {
            Scene.HostHudSprite[] frame = VisorFrame();
            float w = _scene.Size.X;
            float leftEnd = VisorSeamLeft * _scale;
            float middleStart = w / 2 - (128 - VisorSeamLeft) * _scale;
            if (middleStart <= leftEnd)
            {
                // 4:3 or narrower: the cart's frame as it is
                Span(frame[0], w / 2 - 128 * _scale, w / 2 + 128 * _scale);
                return;
            }
            float middleEnd = w / 2 + (VisorSeamRight - 128) * _scale;
            float rightStart = w - (256 - VisorSeamRight) * _scale;
            Span(frame[4], leftEnd - 1, middleStart + 1);
            Span(frame[5], middleEnd - 1, rightStart + 1);
            Span(frame[1], 0, leftEnd);
            Span(frame[2], middleStart, middleEnd);
            Span(frame[3], rightStart, w);
        }

        private static void DrawBanner()
        {
            // 0x202db00: typed out 1 character a frame, centred at (128, 84); Survivor has none
            int address = FirstHuntMission.Mode switch
            {
                FhMode.Morphball => FirstHuntMission.Result == FhResult.Complete ? 0x2074ffc : 0x2075054,
                FhMode.Regulator => FirstHuntMission.Result == FhResult.Complete ? 0x2074fd0 : 0x207502c,
                FhMode.Multiplayer => FirstHuntMission.Result == FhResult.Disconnected ? 0x207507c : 0,
                _ => 0
            };
            if (address == 0)
            {
                return;
            }
            if (_bannerCounter < 400 * 2)
            {
                _bannerCounter++;
            }
            float y = FirstHuntMission.Result == FhResult.Disconnected ? 74 : 84;
            Text(SmallFont, TextPalette, 128, y, 2, Arm9String(address), count: _bannerCounter / 2); // todo: FPS stuff
        }
    }
}
