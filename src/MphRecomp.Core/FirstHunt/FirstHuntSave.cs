using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using MphRead;

namespace MphRecomp.FirstHunt
{
    // First Hunt's save (docs/FIRST_HUNT_PORT.md P4; fh_research p4_frontend.md section 5), kept in the cart's own
    // format: the file is the cart's 512-byte EEPROM image, two copies of the 0xdc-byte block at 0x000 and 0x0e0, each
    // write going to the other copy with a generation count and a checksum (0x2031920 / 0x2031c84), so a save from the
    // cart works here and the other way round. The block:
    //   +0x00 name (10 chars)          +0x0c Morph Ball top 5 {score, DS frames, 0}
    //   +0x48 Regulator top 5 {score, DS frames, 0}   +0x84 Survivor top 5 {score, kills, 0}
    //   +0xc0 multiplayer totals (12)  +0xcc flags: bits 0-2 control type, 0x10 / 0x20 / 0x40 placed in Regulator /
    //   Survivor / Morph Ball (all three = the teaser movie unlocked)   +0xd0 generation, +0xd2 size, +0xd4 checksum, +0xd8 ~checksum
    // A new or unreadable save gets the cart's defaults (0x2045528): "Samus Aran" and its tables (read from the import).
    public sealed class FirstHuntSave
    {
        public const int BlockSize = 0xdc;
        public const int SlotStride = 0xe0;
        public const int FileSize = 0x200;
        public const int Rows = 5;

        // the tables' order in the block, and the cart's mode index for the record insert (0x2032558)
        public enum Table
        {
            Morphball = 0,
            Regulator = 1,
            Survivor = 2
        }

        public const ushort RegulatorPlaced = 0x10, SurvivorPlaced = 0x20, MorphballPlaced = 0x40, MovieUnlocked = 0x70;

        private readonly byte[] _block = new byte[BlockSize];
        private int _slot = 0; // the copy last read or written
        private readonly string? _path;

        public string Name
        {
            get => Encoding.ASCII.GetString(_block, 0, 10).TrimEnd('\0');
            set
            {
                Array.Clear(_block, 0, 12);
                byte[] bytes = Encoding.ASCII.GetBytes(value.Length > 10 ? value[..10] : value);
                bytes.CopyTo(_block, 0);
            }
        }

        public ushort Flags
        {
            get => BinaryPrimitives.ReadUInt16LittleEndian(_block.AsSpan(0xcc));
            set => BinaryPrimitives.WriteUInt16LittleEndian(_block.AsSpan(0xcc), value);
        }

        // 0-4: the cart's control types S, DR, TR, DL, TL
        public int ControlType
        {
            get => Flags & 7;
            set => Flags = (ushort)(Flags & ~7 | value & 7);
        }

        public bool MovieAvailable => (Flags & MovieUnlocked) == MovieUnlocked;

        public ushort Generation => BinaryPrimitives.ReadUInt16LittleEndian(_block.AsSpan(0xd0));

        private static int TableOffset(Table table) => 0x0c + (int)table * Rows * 12;

        public (uint Score, uint Second) Row(Table table, int rank)
        {
            int at = TableOffset(table) + rank * 12;
            return (BinaryPrimitives.ReadUInt32LittleEndian(_block.AsSpan(at)), BinaryPrimitives.ReadUInt32LittleEndian(_block.AsSpan(at + 4)));
        }

        private FirstHuntSave(string? path)
        {
            _path = path;
        }

        // a save at path (the cart's EEPROM image), or the defaults when there's none or neither copy is valid
        public static FirstHuntSave Load(string? path)
        {
            var save = new FirstHuntSave(path);
            if (path != null && File.Exists(path))
            {
                byte[] file = File.ReadAllBytes(path);
                int best = -1;
                int bestGen = -1;
                bool wrap = false;
                for (int slot = 0; slot < 2; slot++)
                {
                    if (Valid(file, slot) && Gen(file, slot) == 0xffff)
                    {
                        wrap = true;
                    }
                }
                for (int slot = 0; slot < 2; slot++)
                {
                    if (!Valid(file, slot))
                    {
                        continue;
                    }
                    int gen = Gen(file, slot);
                    if (wrap && gen < 0x8000)
                    {
                        gen += 0x10000;
                    }
                    if (gen > bestGen)
                    {
                        bestGen = gen;
                        best = slot;
                    }
                }
                if (best >= 0)
                {
                    Array.Copy(file, best * SlotStride, save._block, 0, BlockSize);
                    save._slot = best;
                    return save;
                }
            }
            save.Reset();
            return save;
        }

        // read the file again (the game screen wrote to it while the menus were away)
        public void Reload()
        {
            FirstHuntSave fresh = Load(_path);
            Array.Copy(fresh._block, _block, BlockSize);
            _slot = fresh._slot;
        }

        private static int Gen(byte[] file, int slot) => BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(slot * SlotStride + 0xd0));

        private static bool Valid(byte[] file, int slot)
        {
            int at = slot * SlotStride;
            if (file.Length < at + BlockSize || BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 0xd2)) != BlockSize)
            {
                return false;
            }
            uint sum = Checksum(file.AsSpan(at, BlockSize));
            return BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 0xd4)) == sum
                && BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 0xd8)) == ~sum;
        }

        // 0x2031c84: each byte xor its index, shifted into the byte lane index & 3; the checksum words count as 0
        public static uint Checksum(ReadOnlySpan<byte> block)
        {
            uint sum = 0;
            for (int i = 0; i < BlockSize; i++)
            {
                uint b = i >= 0xd4 && i < 0xdc ? 0u : block[i];
                sum += ((b ^ (uint)i) & 0xff) << (8 * (i & 3));
            }
            return sum;
        }

        // the cart's defaults (0x2045528 + 0x2032840): the name, the three tables from its code, everything else 0
        public void Reset()
        {
            ushort gen = Generation;
            Array.Clear(_block);
            BinaryPrimitives.WriteUInt16LittleEndian(_block.AsSpan(0xd0), gen);
            Name = "Samus Aran";
            (Table table, int address)[] defaults = { (Table.Morphball, 0x20752c0), (Table.Regulator, 0x2075284), (Table.Survivor, 0x2075248) };
            foreach ((Table table, int address) in defaults)
            {
                for (int i = 0; i < Rows * 3; i++)
                {
                    int value = FirstHuntRules.ReadArm9Int(address + i * 4) ?? 0;
                    BinaryPrimitives.WriteInt32LittleEndian(_block.AsSpan(TableOffset(table) + i * 4), value);
                }
            }
        }

        // 0x2032558: the first row the score is at least; -1 = not placed. Unless queryOnly, the rows below move down
        // and it takes that row; Survivor and Morph Ball then order equal scores by the lower second value.
        public int Insert(Table table, uint score, uint second, bool queryOnly)
        {
            int rank = 0;
            while (rank < Rows && score < Row(table, rank).Score)
            {
                rank++;
            }
            if (rank >= Rows || queryOnly)
            {
                return rank >= Rows ? -1 : rank;
            }
            int baseAt = TableOffset(table);
            for (int i = Rows - 1; i > rank; i--)
            {
                Array.Copy(_block, baseAt + (i - 1) * 12, _block, baseAt + i * 12, 12);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(_block.AsSpan(baseAt + rank * 12), score);
            BinaryPrimitives.WriteUInt32LittleEndian(_block.AsSpan(baseAt + rank * 12 + 4), second);
            if (table != Table.Regulator)
            {
                for (int i = rank + 1; i < Rows; i++)
                {
                    (uint s0, uint t0) = Row(table, i - 1);
                    (uint s1, uint t1) = Row(table, i);
                    if (s0 == s1 && t0 > t1)
                    {
                        Span<byte> a = _block.AsSpan(baseAt + (i - 1) * 12, 12);
                        Span<byte> b = _block.AsSpan(baseAt + i * 12, 12);
                        Span<byte> tmp = stackalloc byte[12];
                        a.CopyTo(tmp);
                        b.CopyTo(a);
                        tmp.CopyTo(b);
                        rank = i;
                    }
                }
            }
            return rank;
        }

        // the results fill (0x203f898): a training game's run against its table. Failed runs are ranked but not
        // entered; a first placing sets that mode's flag. Returns the rank (-1 = none) and whether the movie unlocked
        // with this run; Save() writes it if anything changed.
        public (int Rank, bool MovieNowUnlocked, bool Changed) RecordTraining(FhMode mode, bool failed, int score, int dsFrames, int kills)
        {
            (Table table, ushort flag, uint second) = mode switch
            {
                FhMode.Morphball => (Table.Morphball, MorphballPlaced, (uint)dsFrames),
                FhMode.Regulator => (Table.Regulator, RegulatorPlaced, (uint)dsFrames),
                _ => (Table.Survivor, SurvivorPlaced, (uint)kills)
            };
            int rank = Insert(table, (uint)Math.Max(score, 0), second, queryOnly: failed);
            bool changed = rank >= 0 && !failed;
            bool unlocked = false;
            if (rank >= 0 && (Flags & flag) == 0)
            {
                bool before = MovieAvailable;
                Flags |= flag;
                unlocked = !before && MovieAvailable;
                changed = true;
            }
            return (rank, unlocked, changed);
        }

        // +0xc0 the multiplayer totals (fh_research p5_multiplayer.md 3, 0x20431f8): all deaths (falls and self-kills too),
        // kills of other players, games won (first place, ties too)
        public uint TotalDeaths => BinaryPrimitives.ReadUInt32LittleEndian(_block.AsSpan(0xc0));
        public uint TotalKills => BinaryPrimitives.ReadUInt32LittleEndian(_block.AsSpan(0xc4));
        public ushort GamesWon => BinaryPrimitives.ReadUInt16LittleEndian(_block.AsSpan(0xc8));

        // a deathmatch's end for this player (the results fill): Save() writes it
        public void RecordMultiplayer(int rank, int kills, int deaths)
        {
            if (rank == 0) BinaryPrimitives.WriteUInt16LittleEndian(_block.AsSpan(0xc8), (ushort)Math.Min(GamesWon + 1, UInt16.MaxValue));
            BinaryPrimitives.WriteUInt32LittleEndian(_block.AsSpan(0xc4), TotalKills + (uint)Math.Max(kills, 0));
            BinaryPrimitives.WriteUInt32LittleEndian(_block.AsSpan(0xc0), TotalDeaths + (uint)Math.Max(deaths, 0));
        }

        // 0x2031920: the generation counts up and the block goes to the other copy
        public void Save()
        {
            if (_path == null)
            {
                return;
            }
            byte[] file = File.Exists(_path) ? File.ReadAllBytes(_path) : Array.Empty<byte>();
            if (file.Length < FileSize)
            {
                Array.Resize(ref file, FileSize);
            }
            BinaryPrimitives.WriteUInt16LittleEndian(_block.AsSpan(0xd0), (ushort)(Generation + 1));
            BinaryPrimitives.WriteUInt16LittleEndian(_block.AsSpan(0xd2), BlockSize);
            uint sum = Checksum(_block);
            BinaryPrimitives.WriteUInt32LittleEndian(_block.AsSpan(0xd4), sum);
            BinaryPrimitives.WriteUInt32LittleEndian(_block.AsSpan(0xd8), ~sum);
            _slot ^= 1;
            Array.Copy(_block, 0, file, _slot * SlotStride, BlockSize);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string temp = _path + ".tmp";
            File.WriteAllBytes(temp, file);
            File.Move(temp, _path, overwrite: true);
        }
    }
}
