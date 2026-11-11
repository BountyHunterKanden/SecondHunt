using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MphRead;

namespace MphRecomp.Campaign
{
    // One adventure file (A/B/C) of a vanilla cart save, as the import box lists it.
    public sealed class CartSaveFile
    {
        public int Index { get; init; }
        public char Letter => (char)('A' + Index);
        // the section tag says the file is in use and a block with a good checksum was found
        public bool Used { get; init; }
        // the tag says in use but no block passed its checksum
        public bool Damaged { get; init; }
        public int CompletionPercent { get; init; }
        public int Octoliths { get; init; }
        public long PlaySeconds { get; init; }
        public bool Cleared { get; init; }
        internal byte[]? Story { get; init; }
    }

    // What a vanilla cart keeps for the whole cart, outside the three files: the options save's multiplayer unlocks
    // (section 4 +0x04 arenas by room id - 93, +0x08 hunters by Hunter, +0x10 bit 5 an ending / bit 6 the 100% ending)
    // and the Hunter License's marks (section 5 +0x18 bit 28 beaten, bit 29 at 100%). Null parts = that section is empty
    // or damaged on this cart.
    public sealed class CartUnlocks
    {
        public int? Arenas { get; init; }
        public int? Hunters { get; init; }
        public bool Ending { get; init; }
        public bool Ending100 { get; init; }
        public bool LicenseBeaten { get; init; }
        public bool LicenseCompleted { get; init; }
        // the whole Hunter License (section 5), null = none on this cart
        public CartLicense? License { get; init; }
        // why the license doesn't look like one the game wrote (CartSave.CheckLicense), null = it does
        public string? LicenseProblem { get; init; }
    }

    // A vanilla cart's Hunter License (section 5, 0x1E8 bytes; docs/MULTIPLAYER_MENU.md M6), as the card's own fields:
    // the name (ASCII, NUL-ended, +0x00..0x17), +0x18 the packed marks (favourites, rank level, beaten, 100%), +0x1C rank
    // points, the counters +0x20..0x58, +0x74 matches by hunter, +0xAC kills by beam, +0xD0 / +0x144 wins / matches by arena
    // (room id - 93), +0x1B8 matches by mode, the current streaks +0x1D4..0x1E0. Times are whole seconds.
    public sealed class CartLicense
    {
        public string Name { get; init; } = "";
        public uint Marks { get; init; }
        public int RankPoints { get; init; }
        public int WifiWins { get; init; }
        public int OnlineMatches { get; init; }
        public int WirelessWins { get; init; }
        public int LocalMatches { get; init; }
        public int BipedKills { get; init; }
        public int AltFormKills { get; init; }
        public int HeadshotKills { get; init; }
        public int Disconnects { get; init; }
        public int WirelessSeconds { get; init; }
        public int WifiSeconds { get; init; }
        public int TotalSeconds { get; init; }
        public int WifiBestWinStreak { get; init; }
        public int WifiBestKillStreak { get; init; }
        public int WirelessBestWinStreak { get; init; }
        public int WirelessBestKillStreak { get; init; }
        public int WifiWinStreak { get; init; }
        public int WifiKillStreak { get; init; }
        public int WirelessWinStreak { get; init; }
        public int WirelessKillStreak { get; init; }
        public int[] HunterMatches { get; init; } = new int[7];
        public int[] WeaponKills { get; init; } = new int[9];
        public int[] ArenaWins { get; init; } = new int[29];
        public int[] ArenaMatches { get; init; } = new int[29];
        public int[] ModeMatches { get; init; } = new int[7];
        public int FavoriteHunter => (int)(Marks >> 3) & 0xF;
        public int FavoriteWeapon => (int)(Marks >> 7) & 0x1F;
        public int FavoriteMode => (int)(Marks >> 12) & 0x7;
        public int LuckyArena => (int)(Marks >> 15) & 0x1F; // room id - 93
        public int RankLevel => (int)(Marks >> 24) & 0x7;
        public bool Beaten => (Marks & (1u << 28)) != 0;
        public bool Completed => (Marks & (1u << 29)) != 0;

        internal static CartLicense Read(byte[] b)
        {
            int I(int o) => (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)), (uint)Int32.MaxValue - 1);
            int[] A(int o, int n) => Enumerable.Range(0, n).Select(i => I(o + 4 * i)).ToArray();
            int end = Array.IndexOf(b, (byte)0, 0, 0x18);
            return new CartLicense
            {
                Name = Encoding.ASCII.GetString(b, 0, end < 0 ? 0x18 : end),
                Marks = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x18)),
                RankPoints = I(0x1C),
                WifiWins = I(0x20), OnlineMatches = I(0x24), WirelessWins = I(0x28), LocalMatches = I(0x2C),
                BipedKills = I(0x30), AltFormKills = I(0x34), HeadshotKills = I(0x38), Disconnects = I(0x3C),
                WirelessSeconds = I(0x40), WifiSeconds = I(0x44), TotalSeconds = I(0x48),
                WifiBestWinStreak = I(0x4C), WifiBestKillStreak = I(0x50), WirelessBestWinStreak = I(0x54), WirelessBestKillStreak = I(0x58),
                HunterMatches = A(0x74, 7), WeaponKills = A(0xAC, 9), ArenaWins = A(0xD0, 29), ArenaMatches = A(0x144, 29),
                ModeMatches = A(0x1B8, 7),
                WifiWinStreak = I(0x1D4), WifiKillStreak = I(0x1D8), WirelessWinStreak = I(0x1DC), WirelessKillStreak = I(0x1E0)
            };
        }
    }

    // Vanilla Metroid Prime Hunters cartridge saves (US rev 1, AMHE1): reading the three adventure files into app saves
    // and writing an app save back out as a DS save. Layout:
    // 256 KB: "METROID_HUNTERS_SAVE_DATA", u32 section tags at 0x20 (0x234A609B in use,
    // 0x71C4128F empty), files A/B/C = 13 blocks of 0x1100 from 0x100 (the highest block index wins). Block = u32
    // checksum, u32 index, StorySaveData (0x10B4, MphRead MemoryClasses). Checksum = sum of the u16 words over
    // block[4..0x10BC] + 0x10B5.
    public sealed class CartSave
    {
        public const int Size = 0x40000;
        private const int BlockSize = 0x1100;
        private const int StoryLength = 0x10B4;
        private const int ChecksumEnd = 0x10BC;
        private const int BlocksPerFile = 13;
        private const int FirstBlock = 0x100;
        private const int TagOffset = 0x20;
        private const int TagCount = 7;
        private const uint TagUsed = 0x234A609B;
        private const uint TagEmpty = 0x71C4128F;
        private static readonly byte[] _header = Encoding.ASCII.GetBytes("METROID_HUNTERS_SAVE_DATA");

        // The empty-block starts of the sections after the story files (a zero checksum word each, 0xFF elsewhere), as
        // a formatted cart has them (taken from the owner's rev 1 melonDS save; 0x29A00 / 0x2A300 = the options save,
        // empty here so the game sets its defaults).
        private static readonly int[] _otherEmptyBlocks = BuildOtherEmptyBlocks();

        // Sections 4 (options) and 5 (Hunter License): blocks of roundup(length + 8, 0x100) from their start, the highest
        // index wins, checksum = 1 + length + the u16 words over block[4..8 + length] (the story's rule)
        private const int OptionsStart = 0x29A00, OptionsBlocks = 10, OptionsLength = 0xA4;
        private const int LicenseStart = 0x2A400, LicenseBlocks = 9, LicenseLength = 0x1E8;
        // the retail arenas (rooms 93-119) and hunters (Samus..Guardian)
        private const int ArenaMask = 0x7FFFFFF, HunterMask = 0x7F;

        public IReadOnlyList<CartSaveFile> Files { get; }
        public CartUnlocks Unlocks { get; }

        private CartSave(IReadOnlyList<CartSaveFile> files, CartUnlocks unlocks)
        {
            Files = files;
            Unlocks = unlocks;
        }

        // Accepts raw .sav (melonDS, flashcarts, BizHawk, cart dumpers), DeSmuME .dsv (raw + footer) and padded raws:
        // the first 256 KB, detected by the header rather than the extension.
        public static CartSave? TryParse(byte[] data, out string? error)
        {
            error = null;
            if (data.Length >= 31 && Encoding.ASCII.GetString(data, 0, 31) == "NocashGbaBackupMediaSavDataFile")
            {
                error = "no$gba saves aren't supported yet.\nexport a raw .sav from the emulator.";
                return null;
            }
            if (data.Length < Size || !data.AsSpan(0, _header.Length).SequenceEqual(_header))
            {
                error = "this isn't a metroid prime hunters\nds save.";
                return null;
            }
            var files = new List<CartSaveFile>();
            for (int f = 0; f < 3; f++)
            {
                uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(TagOffset + 4 * f));
                byte[]? story = tag == TagUsed ? NewestBlock(data, f) : null;
                if (story == null)
                {
                    files.Add(new CartSaveFile { Index = f, Damaged = tag == TagUsed });
                    continue;
                }
                StorySave save = IsStarted(story) ? ReadStory(story) : new StorySave();
                files.Add(new CartSaveFile
                {
                    Index = f,
                    Used = true,
                    Story = story,
                    CompletionPercent = Math.Clamp(save.GetCompletionPercentage(), 0, 100),
                    Octoliths = save.CountFoundOctoliths(),
                    PlaySeconds = ReadPlaySeconds(story),
                    Cleared = (story[0x1084] & 2) != 0
                });
            }
            return new CartSave(files, ReadUnlocks(data));
        }

        private static CartUnlocks ReadUnlocks(byte[] data)
        {
            byte[]? options = NewestSectionBlock(data, OptionsStart, OptionsBlocks, OptionsLength);
            byte[]? license = TagIsUsed(data, 5) ? NewestSectionBlock(data, LicenseStart, LicenseBlocks, LicenseLength) : null;
            uint marks = license == null ? 0 : U32(license, 0x18);
            return new CartUnlocks
            {
                Arenas = options == null ? null : (int)(U32(options, 0x04) & ArenaMask),
                Hunters = options == null ? null : (int)(U32(options, 0x08) & HunterMask),
                Ending = options != null && (options[0x10] & 0x20) != 0,
                Ending100 = options != null && (options[0x10] & 0x40) != 0,
                LicenseBeaten = (marks & (1u << 28)) != 0,
                LicenseCompleted = (marks & (1u << 29)) != 0,
                License = license == null ? null : CartLicense.Read(license),
                LicenseProblem = license == null ? null : CheckLicense(data)
            };
        }

        // Is the Hunter License one the game wrote? (owner 11-07: refuse a hand-edited one.) Nothing on a DS save is signed,
        // but the cart keeps its last 9 license saves in a ring (block = save index % 9, the highest wins) and the game
        // writes one after every match and again on the way out of RESULTS (a real Wi-Fi cart, 11-07: saves 162-170, one
        // match per two saves). So every save has to add up on its own (matches by hunter / mode / arena = the matches,
        // arena wins = the wins, ...) and be a step on from the one before: counters only go up, at most one match per
        // save, points by what one match can give, the clock moving. An editor that changes the newest block, or writes
        // one block (our own -cartsave license), fails here.
        private static string? CheckLicense(byte[] data)
        {
            int size = (LicenseLength + 8 + 0xFF) & ~0xFF;
            var saves = new List<(uint Index, CartLicense License)>();
            for (int b = 0; b < LicenseBlocks; b++)
            {
                var block = data.AsSpan(LicenseStart + b * size, size);
                uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(block);
                if (checksum == 0 || checksum != SectionChecksum(block, LicenseLength))
                {
                    continue;
                }
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
                if (index % LicenseBlocks != b)
                {
                    return $"save {index} in block {b}";
                }
                saves.Add((index, CartLicense.Read(block.Slice(8, LicenseLength).ToArray())));
            }
            saves.Sort((x, y) => x.Index.CompareTo(y.Index));
            uint newest = saves[^1].Index;
            // a save cut off by a power loss leaves one bad block
            if (saves.Count < Math.Min(LicenseBlocks, newest + 1) - 1)
            {
                return $"{saves.Count} saves up to save {newest}";
            }
            for (int s = 0; s < saves.Count; s++)
            {
                string? problem = LicenseAddsUp(saves[s].License)
                    ?? (s == 0 ? null : LicenseStep(saves[s - 1].License, saves[s].License, saves[s].Index - saves[s - 1].Index));
                if (problem != null)
                {
                    return $"save {saves[s].Index}: {problem}";
                }
            }
            CartLicense last = saves[^1].License;
            if (last.OnlineMatches + (long)last.LocalMatches > newest + 1)
            {
                return $"{last.OnlineMatches + last.LocalMatches} matches in {newest + 1} saves";
            }
            return null;
        }

        private static string? LicenseAddsUp(CartLicense l)
        {
            // 50M is over a year and a half of play; keeps the sums below from overflowing
            int[] all = new[] { l.RankPoints, l.WifiWins, l.OnlineMatches, l.WirelessWins, l.LocalMatches, l.BipedKills,
                l.AltFormKills, l.HeadshotKills, l.Disconnects, l.WirelessSeconds, l.WifiSeconds, l.TotalSeconds }
                .Concat(l.HunterMatches).Concat(l.WeaponKills).Concat(l.ArenaWins).Concat(l.ArenaMatches).Concat(l.ModeMatches).ToArray();
            if (all.Any(v => v > 50_000_000)) return "a count too big";
            int matches = l.OnlineMatches + l.LocalMatches, wins = l.WifiWins + l.WirelessWins, kills = l.BipedKills + l.AltFormKills;
            if (l.HunterMatches.Sum() != matches) return "matches by hunter";
            if (l.ModeMatches.Sum() != matches) return "matches by mode";
            if (l.ArenaMatches.Sum() != matches) return "matches by arena";
            if (l.ArenaWins.Sum() != wins) return "wins by arena";
            if (l.ArenaWins.Where((w, i) => w > l.ArenaMatches[i]).Any()) return "an arena's wins";
            if (l.WifiWins > l.OnlineMatches || l.WirelessWins > l.LocalMatches) return "wins";
            if (l.WeaponKills.Sum() > kills || l.HeadshotKills > kills) return "kills";
            if ((long)l.WifiSeconds + l.WirelessSeconds > l.TotalSeconds) return "times";
            if (l.WifiBestWinStreak > l.WifiWins || l.WirelessBestWinStreak > l.WirelessWins
                || l.WifiWinStreak > l.WifiBestWinStreak || l.WirelessWinStreak > l.WirelessBestWinStreak) return "win streaks";
            if (l.WifiBestKillStreak > kills || l.WirelessBestKillStreak > kills
                || l.WifiKillStreak > l.WifiBestKillStreak || l.WirelessKillStreak > l.WirelessBestKillStreak) return "kill streaks";
            int[] thresholds = MphRecomp.Frontend.HunterLicense.RankThresholds;
            if (l.RankPoints >= thresholds[4] || l.RankLevel != thresholds.Count(t => l.RankPoints >= t)) return "rank";
            return null;
        }

        // the next save from the one before (steps > 1 = a block lost to a cut-off save in between)
        private static string? LicenseStep(CartLicense a, CartLicense b, uint steps)
        {
            static bool Up(int[] x, int[] y) => x.Zip(y).All(p => p.Second >= p.First);
            if (b.OnlineMatches < a.OnlineMatches || b.LocalMatches < a.LocalMatches || b.WifiWins < a.WifiWins
                || b.WirelessWins < a.WirelessWins || b.BipedKills < a.BipedKills || b.AltFormKills < a.AltFormKills
                || b.HeadshotKills < a.HeadshotKills || b.Disconnects < a.Disconnects || b.WirelessSeconds < a.WirelessSeconds
                || b.WifiSeconds < a.WifiSeconds || b.WifiBestWinStreak < a.WifiBestWinStreak
                || b.WifiBestKillStreak < a.WifiBestKillStreak || b.WirelessBestWinStreak < a.WirelessBestWinStreak
                || b.WirelessBestKillStreak < a.WirelessBestKillStreak || !Up(a.HunterMatches, b.HunterMatches)
                || !Up(a.WeaponKills, b.WeaponKills) || !Up(a.ArenaWins, b.ArenaWins) || !Up(a.ArenaMatches, b.ArenaMatches)
                || !Up(a.ModeMatches, b.ModeMatches))
            {
                return "a count went down";
            }
            if (b.TotalSeconds <= a.TotalSeconds) return "the clock didn't move";
            int matches = b.OnlineMatches + b.LocalMatches - a.OnlineMatches - a.LocalMatches;
            if (matches > steps) return $"{matches} matches in {steps} save(s)";
            if (b.WifiWins - a.WifiWins > b.OnlineMatches - a.OnlineMatches
                || b.WirelessWins - a.WirelessWins > b.LocalMatches - a.LocalMatches) return "more wins than matches";
            if ((long)b.WifiSeconds - a.WifiSeconds + b.WirelessSeconds - a.WirelessSeconds > (long)b.TotalSeconds - a.TotalSeconds)
            {
                return "match time past the clock";
            }
            // 3 opponents at 15 points each is the most one 4-player match moves
            if (Math.Abs(b.RankPoints - a.RankPoints) > 45 * Math.Max(matches, 1)) return "points jumped";
            if (matches == 0 && (b.BipedKills != a.BipedKills || b.AltFormKills != a.AltFormKills || !a.WeaponKills.SequenceEqual(b.WeaponKills)))
            {
                return "kills without a match";
            }
            return null;
        }

        private static byte[]? NewestSectionBlock(byte[] data, int start, int blocks, int length)
        {
            int size = (length + 8 + 0xFF) & ~0xFF;
            byte[]? best = null;
            uint bestIndex = 0;
            for (int b = 0; b < blocks; b++)
            {
                var block = data.AsSpan(start + b * size, size);
                uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(block);
                if (checksum == 0 || checksum != SectionChecksum(block, length))
                {
                    continue;
                }
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
                if (best == null || index > bestIndex)
                {
                    best = block.Slice(8, length).ToArray();
                    bestIndex = index;
                }
            }
            return best;
        }

        private static uint SectionChecksum(ReadOnlySpan<byte> block, int length)
        {
            uint sum = 1 + (uint)length;
            for (int i = 4; i < 8 + length; i += 2)
            {
                sum += BinaryPrimitives.ReadUInt16LittleEndian(block[i..]);
            }
            return sum;
        }

        private static byte[]? NewestBlock(byte[] data, int file)
        {
            byte[]? best = null;
            uint bestIndex = 0;
            for (int b = 0; b < BlocksPerFile; b++)
            {
                var block = data.AsSpan(FirstBlock + (file * BlocksPerFile + b) * BlockSize, BlockSize);
                uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(block);
                if (checksum == 0 || checksum != Checksum(block))
                {
                    continue;
                }
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
                if (best == null || index > bestIndex)
                {
                    best = block.Slice(8, StoryLength).ToArray();
                    bestIndex = index;
                }
            }
            return best;
        }

        private static uint Checksum(ReadOnlySpan<byte> block)
        {
            uint sum = 0x10B5;
            for (int i = 4; i < ChecksumEnd; i += 2)
            {
                sum += BinaryPrimitives.ReadUInt16LittleEndian(block[i..]);
            }
            return sum;
        }

        // false for a file made on the vanilla file select and never played: the game fills the scan totals on its first
        // save
        private static bool IsStarted(byte[] s) => U32(s, 0x10AC) != 0 || ReadPlaySeconds(s) != 0;

        // play time + the time spent in rooms 89-92 after an ending (vanilla keeps that apart at +0x10A0)
        private static long ReadPlaySeconds(byte[] s) => U32(s, 0x109C) + (long)U32(s, 0x10A0);

        private static ushort U16(byte[] s, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(offset));
        private static uint U32(byte[] s, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(offset));

        private static StorySave ReadStory(byte[] s)
        {
            var save = new StorySave
            {
                Weapons = U16(s, 0x00),
                Health = U16(s, 0x0E),
                HealthMax = U16(s, 0x10),
                Areas = U16(s, 0x12),
                CheckpointRoomId = -1,
                CheckpointEntityId = -1,
                BossFlags = (BossFlags)U32(s, 0x18),
                Artifacts = U32(s, 0x1C),
                LostOctoliths = U32(s, 0x20),
                CurrentOctoliths = s[0x24],
                FoundOctoliths = s[0x26],
                DefeatedHunters = s[0x1087],
                ScanCount = (int)U32(s, 0x10A4),
                EquipmentCount = (int)U32(s, 0x10A8)
            };
            for (int i = 0; i < 3; i++)
            {
                save.WeaponSlots[i] = (sbyte)s[0x02 + i];
            }
            for (int i = 0; i < 2; i++)
            {
                save.Ammo[i] = U16(s, 0x06 + 2 * i);
                save.AmmoMax[i] = U16(s, 0x0A + 2 * i);
            }
            s.AsSpan(0x27, 9).CopyTo(save.VisitedRooms);
            for (int r = 0; r < 66; r++)
            {
                s.AsSpan(0x54 + 60 * r, 60).CopyTo(save.RoomState[r]);
            }
            // one row per area (9 in vanilla); MphRead keeps the first 8
            for (int r = 0; r < save.EnemyEncounters.Length; r++)
            {
                s.AsSpan(0xFCC + 8 * r, 8).CopyTo(save.EnemyEncounters[r]);
            }
            s.AsSpan(0x1014, 4).CopyTo(save.TriggerState);
            s.AsSpan(0x101C, 68).CopyTo(save.Logbook);
            s.AsSpan(0x1080, 4).CopyTo(save.AreaHunters);
            save.Stats.HunterKills = U32(s, 0x1088);
            save.Stats.EnemyHunterDeaths = U32(s, 0x108C);
            save.Stats.Deaths = U32(s, 0x1090);
            save.Stats.Gorea1Time = U32(s, 0x1094);
            save.Stats.Gorea2Time = U32(s, 0x1098);
            return save;
        }

        // File A/B/C as an app save (Samus, parked where the vanilla save was made: vanilla saves on the gunship).
        public CampaignSaveFile ToCampaign(int fileIndex)
        {
            CartSaveFile file = Files[fileIndex];
            byte[] s = file.Story ?? throw new InvalidOperationException($"file {file.Letter} is empty");
            bool started = IsStarted(s);
            // a never-played file = the app's own new file (vanilla's new-file counts at +0x10A4 aren't final yet)
            StorySave story = started ? ReadStory(s) : new StorySave();
            var connectors = new uint[8];
            for (int i = 0; i < connectors.Length; i++)
            {
                connectors[i] = U32(s, 0x30 + 4 * i);
            }
            return new CampaignSaveFile
            {
                Hunter = Hunter.Samus,
                Planet = ShipNavigation.ByLandingRoom(s[0x1085])?.Id ?? Planet.CelestialArchives,
                SavedAtUtc = DateTime.UtcNow,
                PlaySeconds = ReadPlaySeconds(s),
                Cleared = file.Cleared,
                Started = started,
                KnownAreas = started ? U16(s, 0x14) : 0,
                MapConnectors = connectors,
                Story = story
            };
        }

        // A fresh 256 KB DS save holding the app file as file A (B and C empty, options unset). With a base save the
        // file goes into that save's slot instead and everything else in it is kept. With unlocks (the app's Hunter
        // License) the options + license sections get them OR-ed in, written as their next block.
        public static byte[] Export(CampaignSaveFile file, byte[]? baseSave = null, int slot = 0, CartUnlocks? unlocks = null)
        {
            byte[] data;
            if (baseSave != null)
            {
                if (TryParse(baseSave, out string? error) == null)
                {
                    throw new ArgumentException(error);
                }
                data = baseSave.AsSpan(0, Size).ToArray();
            }
            else
            {
                data = NewCart();
            }
            int first = FirstBlock + slot * BlocksPerFile * BlockSize;
            for (int b = 0; b < BlocksPerFile; b++)
            {
                var block = data.AsSpan(first + b * BlockSize, BlockSize);
                block.Fill(0xFF);
                BinaryPrimitives.WriteUInt32LittleEndian(block, 0);
            }
            var written = data.AsSpan(first, BlockSize);
            written.Clear();
            WriteStory(file).CopyTo(written[8..]);
            BinaryPrimitives.WriteUInt32LittleEndian(written[4..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(written, Checksum(written));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(TagOffset + 4 * slot), TagUsed);
            if (unlocks != null)
            {
                WriteUnlocks(data, unlocks);
            }
            return data;
        }

        // A formatted cart has no options/license blocks: the game sets these in RAM at boot (arm9 0x0205C29C) and a
        // section whose tag is empty keeps them. Options: volumes 9/9, the starting arenas + hunters, +0x10 0x680,
        // +0x14 0x1333; license: all zero but the name, which the game refills from the firmware nickname after a
        // load while options +0x10 bit 8 is clear (ov0 0x02124B08), so an empty name is fine.
        private static readonly byte[] _optionsDefault = BuildOptionsDefault();

        private static byte[] BuildOptionsDefault()
        {
            var d = new byte[OptionsLength];
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x00), 0x242400);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x04), 0x161295);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x08), 0x23);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x10), 0x680);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x14), 0x1333);
            return d;
        }

        private static void WriteUnlocks(byte[] data, CartUnlocks unlocks)
        {
            byte[] options = TagIsUsed(data, 4) ? NewestSectionBlock(data, OptionsStart, OptionsBlocks, OptionsLength)
                ?? (byte[])_optionsDefault.Clone() : (byte[])_optionsDefault.Clone();
            uint arenas = U32(options, 0x04) | (uint)((unlocks.Arenas ?? 0) & ArenaMask);
            uint hunters = U32(options, 0x08) | (uint)((unlocks.Hunters ?? 0) & HunterMask);
            BinaryPrimitives.WriteUInt32LittleEndian(options.AsSpan(0x04), arenas);
            BinaryPrimitives.WriteUInt32LittleEndian(options.AsSpan(0x08), hunters);
            if (unlocks.Ending || unlocks.LicenseBeaten) options[0x10] |= 0x20;
            if (unlocks.Ending100 || unlocks.LicenseCompleted) options[0x10] |= 0x40;
            WriteSection(data, 4, OptionsStart, OptionsBlocks, options);

            byte[] license = (TagIsUsed(data, 5) ? NewestSectionBlock(data, LicenseStart, LicenseBlocks, LicenseLength) : null)
                ?? new byte[LicenseLength];
            uint marks = U32(license, 0x18);
            if (unlocks.LicenseBeaten || unlocks.Ending) marks |= 1u << 28;
            if (unlocks.LicenseCompleted || unlocks.Ending100) marks |= 1u << 29;
            BinaryPrimitives.WriteUInt32LittleEndian(license.AsSpan(0x18), marks);
            WriteSection(data, 5, LicenseStart, LicenseBlocks, license);
        }

        // a license block (section 5, 0x1E8 bytes as CartLicense.Read reads them) written into a save as its newest: the
        // -cartsave test's made-up license (the owner's test saves have none)
        public static void WriteLicense(byte[] data, byte[] license)
        {
            var block = new byte[LicenseLength];
            license.AsSpan(0, Math.Min(license.Length, LicenseLength)).CopyTo(block);
            WriteSection(data, 5, LicenseStart, LicenseBlocks, block);
        }

        private static bool TagIsUsed(byte[] data, int section) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(TagOffset + 4 * section)) != TagEmpty;

        // the section's next block after its newest valid one (vanilla's ring), index + 1, then the tag set
        private static void WriteSection(byte[] data, int section, int start, int blocks, byte[] content)
        {
            int length = content.Length;
            int size = (length + 8 + 0xFF) & ~0xFF;
            int newest = -1;
            uint newestIndex = 0;
            for (int b = 0; b < blocks; b++)
            {
                var block = data.AsSpan(start + b * size, size);
                uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(block);
                if (checksum == 0 || checksum != SectionChecksum(block, length)) continue;
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
                if (newest < 0 || index > newestIndex)
                {
                    newest = b;
                    newestIndex = index;
                }
            }
            int at = newest < 0 ? 0 : (newest + 1) % blocks;
            uint next = newest < 0 ? 0 : newestIndex + 1;
            var written = data.AsSpan(start + at * size, size);
            written.Fill(0xFF);
            written[..(8 + length)].Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(written[4..], next);
            content.CopyTo(written[8..]);
            BinaryPrimitives.WriteUInt32LittleEndian(written, SectionChecksum(written, length));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(TagOffset + 4 * section), TagUsed);
        }

        private static byte[] NewCart()
        {
            var data = new byte[Size];
            data.AsSpan().Fill(0xFF);
            data.AsSpan(0, FirstBlock).Fill(0);
            _header.CopyTo(data, 0);
            for (int i = 0; i < TagCount; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(TagOffset + 4 * i), TagEmpty);
            }
            data.AsSpan(TagOffset + 4 * TagCount, FirstBlock - TagOffset - 4 * TagCount).Fill(0xFF);
            for (int b = 0; b < 3 * BlocksPerFile; b++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(FirstBlock + b * BlockSize), 0);
            }
            foreach (int offset in _otherEmptyBlocks)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), 0);
            }
            return data;
        }

        private static int[] BuildOtherEmptyBlocks()
        {
            var list = new List<int> { 0x29800, 0x29900, 0x29A00, 0x2A300 };
            for (int offset = 0x2A400; offset <= 0x2B600; offset += 0x200)
            {
                list.Add(offset);
            }
            for (int offset = 0x2D700; offset <= 0x3DF00; offset += 0x2100)
            {
                list.Add(offset);
            }
            return list.ToArray();
        }

        private static byte[] WriteStory(CampaignSaveFile file)
        {
            StorySave save = file.Story;
            var s = new byte[StoryLength];
            void W16(int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(offset), (ushort)value);
            void W32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(offset), value);
            W16(0x00, save.Weapons);
            for (int i = 0; i < 3; i++)
            {
                s[0x02 + i] = (byte)save.WeaponSlots[i];
            }
            for (int i = 0; i < 2; i++)
            {
                W16(0x06 + 2 * i, save.Ammo[i]);
                W16(0x0A + 2 * i, save.AmmoMax[i]);
            }
            W16(0x0E, save.Health);
            W16(0x10, save.HealthMax);
            W16(0x12, save.Areas);
            W16(0x14, file.Started ? file.KnownAreas ?? save.Areas : 0);
            // vanilla saves on the gunship, with no checkpoint
            W16(0x16, 0xFFFF);
            W32(0x18, (uint)save.BossFlags);
            W32(0x1C, save.Artifacts);
            W32(0x20, save.LostOctoliths);
            s[0x24] = (byte)save.CurrentOctoliths;
            s[0x25] = (byte)save.CurrentOctoliths;
            s[0x26] = (byte)save.FoundOctoliths;
            save.VisitedRooms.AsSpan(0, 9).CopyTo(s.AsSpan(0x27));
            if (file.MapConnectors is uint[] connectors)
            {
                for (int i = 0; i < Math.Min(8, connectors.Length); i++)
                {
                    W32(0x30 + 4 * i, connectors[i]);
                }
            }
            for (int r = 0; r < 66; r++)
            {
                save.RoomState[r].AsSpan(0, 60).CopyTo(s.AsSpan(0x54 + 60 * r));
            }
            for (int r = 0; r < Math.Min(9, save.EnemyEncounters.Length); r++)
            {
                save.EnemyEncounters[r].AsSpan(0, 8).CopyTo(s.AsSpan(0xFCC + 8 * r));
            }
            save.TriggerState.AsSpan(0, 4).CopyTo(s.AsSpan(0x1014));
            save.Logbook.AsSpan(0, 68).CopyTo(s.AsSpan(0x101C));
            save.AreaHunters.AsSpan(0, 4).CopyTo(s.AsSpan(0x1080));
            s[0x1084] = (byte)(file.Cleared ? 2 : 0);
            s[0x1085] = (byte)ShipNavigation.Get(file.Planet).LandingRoomId;
            s[0x1087] = save.DefeatedHunters;
            W32(0x1088, save.Stats.HunterKills);
            W32(0x108C, save.Stats.EnemyHunterDeaths);
            W32(0x1090, save.Stats.Deaths);
            W32(0x1094, (uint)Math.Max(0, save.Stats.Gorea1Time));
            W32(0x1098, (uint)Math.Max(0, save.Stats.Gorea2Time));
            W32(0x109C, (uint)Math.Clamp(file.PlaySeconds, 0, UInt32.MaxValue));
            W32(0x10A4, (uint)save.ScanCount);
            W32(0x10A8, (uint)save.EquipmentCount);
            if (file.Started)
            {
                // the scan totals (rev 1: 215 scans, 27 equipment), which vanilla fills on a file's first save
                W32(0x10AC, (uint)save.GetMaxScanCount());
                W32(0x10B0, 27);
            }
            return s;
        }
    }
}
