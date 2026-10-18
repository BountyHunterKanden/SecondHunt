using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Arenas;
using MphRecomp.Import;

namespace MphRecomp.FirstHunt
{
    // The whole First Hunt game (docs/FIRST_HUNT_PORT.md, step 2): its files, taken from the user's own cart dump, in
    // <import root>/firsthunt/AMFE/, which MphRead then reads as its First Hunt file system (Paths.FhFileSystem):
    //   data/...       the cart's data folder as it is, except each *_Collision.bin rewritten as MPH collision ("wc01", what
    //                  MphRead's physics runs on; Collision.GetCollision reads it by its header), the cart's own kept beside
    //                  it as *_Collision.bin.fh; and beside each level's entity file, its pads and morph cameras as MPH
    //                  entities (*_Ent.mph.bin, FirstHuntRoom)
    //   arm9.bin, overlay0.bin   the cart's code, for the values tables the port reads (player, weapons, levels)
    // Nothing from the cart ships with the app.
    public static class FirstHuntGame
    {
        public const string ImportKind = "firsthunt";
        public const string ItemName = "AMFE";
        public const string FhSuffix = ".fh";

        public static string Dir(string importRoot) => Path.Combine(importRoot, ImportKind, ItemName);
        public static string DataDir(string importRoot) => Path.Combine(Dir(importRoot), "data");

        public static bool IsFirstHunt(NdsRom rom) => rom.GameCode == "AMFE";

        // write the game's folder from the cart; returns how many files were written
        public static int Import(NdsRom rom, string importRoot, Action<string>? log = null)
        {
            if (!IsFirstHunt(rom)) throw new InvalidDataException($"not First Hunt (game code {rom.GameCode}, AMFE expected)");
            string dir = Dir(importRoot);
            Directory.CreateDirectory(dir);
            int count = 0;
            foreach (string path in rom.Files.Where(p => p.StartsWith("data/", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal))
            {
                string to = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                byte[] bytes = rom.Get(path);
                if (path.EndsWith("_Collision.bin", StringComparison.OrdinalIgnoreCase) && !IsMphCollision(bytes))
                {
                    var notes = new List<string>();
                    byte[] mph;
                    try
                    {
                        mph = FirstHuntArena.ConvertCollision(bytes, notes);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
                    {
                        log?.Invoke($"{path}: kept as First Hunt collision ({ex.Message})");
                        File.WriteAllBytes(to, bytes);
                        count++;
                        continue;
                    }
                    File.WriteAllBytes(to + FhSuffix, bytes);
                    File.WriteAllBytes(to, mph);
                    count += 2;
                    foreach (string n in notes) log?.Invoke($"{path}: {n}");
                    continue;
                }
                File.WriteAllBytes(to, IsPackedFolder(path) && Unpack(bytes) is { } unpacked ? unpacked : bytes);
                count++;
                // the level's pads and morph cameras as MphRead's MPH entities (FirstHuntRoom)
                if (path.StartsWith("data/levels/entities/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("_Ent.bin", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Paths.AllPaths; // (MphRead's path table; the file is read where it is)
                    File.WriteAllBytes(FirstHuntRoom.MphEntityPath(to), FirstHuntRoom.ConvertEntities(to));
                    count++;
                }
            }
            File.WriteAllBytes(Path.Combine(dir, "arm9.bin"), rom.Arm9());
            count++;
            if (rom.Arm9Overlay(0) is { } ov0)
            {
                File.WriteAllBytes(Path.Combine(dir, "overlay0.bin"), ov0.Code);
                count++;
            }
            File.WriteAllText(Path.Combine(dir, "data", "menusTD", UnpackedMarker), "");
            log?.Invoke($"First Hunt: {count} files in {dir}");
            return count;
        }

        // the front end's files the cart keeps packed (LZ10: some menusTD models, menu_state.bin, the logo's animation; RLE:
        // the other menusTD page models): stored unpacked, as MphRead reads them
        static bool IsPackedFolder(string path) =>
            path.StartsWith("data/menusTD/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("data/logo/", StringComparison.OrdinalIgnoreCase);

        // a packed file's contents (the DS BIOS LZ10 or RLE formats), or null if it isn't one: the whole input used (up
        // to the 4-byte padding) and the size its header gives
        public static byte[]? Unpack(byte[] bytes)
        {
            if (bytes.Length < 8) return null;
            int size = bytes[1] | bytes[2] << 8 | bytes[3] << 16;
            if (size == 0 || size > 0x400000) return null;
            if (bytes[0] == LZ10.MagicByte)
            {
                try
                {
                    using var input = new MemoryStream(bytes);
                    using var output = new MemoryStream();
                    LZ10.Decompress(input, bytes.Length, output);
                    return output.Length == size && bytes.Length - input.Position < 4 ? output.ToArray() : null;
                }
                catch (Exception ex) when (ex is InvalidDataException or ProgramException or EndOfStreamException or ArgumentException)
                {
                    return null;
                }
            }
            if (bytes[0] == 0x30)
            {
                byte[] output = new byte[size];
                int at = 4, written = 0;
                while (written < size)
                {
                    if (at >= bytes.Length) return null;
                    int flag = bytes[at++];
                    if ((flag & 0x80) != 0)
                    {
                        int run = (flag & 0x7f) + 3;
                        if (at >= bytes.Length || written + run > size) return null;
                        byte value = bytes[at++];
                        for (int i = 0; i < run; i++) output[written++] = value;
                    }
                    else
                    {
                        int run = (flag & 0x7f) + 1;
                        if (at + run > bytes.Length || written + run > size) return null;
                        Array.Copy(bytes, at, output, written, run);
                        at += run;
                        written += run;
                    }
                }
                return bytes.Length - at < 4 ? output : null;
            }
            return null;
        }

        const string UnpackedMarker = ".unpacked2";

        // an import from before the front end (P4) holds the packed files as the cart has them: unpacked once
        static void UnpackFrontEnd(string importRoot)
        {
            string data = DataDir(importRoot);
            string marker = Path.Combine(data, "menusTD", UnpackedMarker);
            if (File.Exists(marker) || !Directory.Exists(Path.Combine(data, "menusTD"))) return;
            foreach (string folder in new[] { "menusTD", "logo" })
            {
                string dir = Path.Combine(data, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.bin"))
                {
                    if (Unpack(File.ReadAllBytes(file)) is { } unpacked) File.WriteAllBytes(file, unpacked);
                }
            }
            File.WriteAllText(marker, "");
        }

        static bool IsMphCollision(byte[] b) => b.Length >= 4 && b[0] == 'w' && b[1] == 'c' && b[2] == '0' && b[3] == '1';

        public static bool Available(string? importRoot) =>
            importRoot != null && Directory.Exists(DataDir(importRoot)) && ImportedContent.Usable(importRoot, ImportKind, ItemName);

        // point MphRead's First Hunt file system at the imported game; false (and nothing changed) if there isn't one
        public static bool Register(string? importRoot)
        {
            if (!Available(importRoot)) return false;
            UnpackFrontEnd(importRoot!);
            Paths.SetPath(Ver.AMFE0, DataDir(importRoot!));
            Paths.FhKey = Ver.AMFE0;
            // the player and weapon tables (P1) from the cart's code
            string arm9 = Path.Combine(Dir(importRoot!), "arm9.bin");
            if (File.Exists(arm9))
            {
                byte[] code = File.ReadAllBytes(arm9);
                FirstHuntRules.Load(code);
                // the HUD's sprites and fonts (P3): overlay 0 is its graphics pack
                string ov0 = Path.Combine(Dir(importRoot!), "overlay0.bin");
                if (File.Exists(ov0)) FirstHuntHud.Load(code, File.ReadAllBytes(ov0));
            }
            return true;
        }
    }
}
