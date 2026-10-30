using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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

        public IReadOnlyList<CartSaveFile> Files { get; }

        private CartSave(IReadOnlyList<CartSaveFile> files)
        {
            Files = files;
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
            return new CartSave(files);
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
        // file goes into that save's slot instead and everything else in it is kept.
        public static byte[] Export(CampaignSaveFile file, byte[]? baseSave = null, int slot = 0)
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
            return data;
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
