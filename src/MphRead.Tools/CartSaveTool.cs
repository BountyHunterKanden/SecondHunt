using System;
using System.IO;
using System.Linq;
using MphRecomp.Campaign;

namespace MphRead
{
    // Vanilla cart saves (MphRecomp.Campaign.CartSave) on the PC:
    //
    //   -cartsave dump <save.sav>                          list files A/B/C (%, octoliths, play time, ending)
    //   -cartsave import <save.sav> <A|B|C> <out.json>     file -> app campaign<slot>.json
    //   -cartsave export <campaign.json> <out.sav> [base.sav] [A|B|C]   app save -> DS save (fresh, or into a base save)
    //   -cartsave test <save.sav>                          round trip every used file: import -> export -> import
    internal static class CartSaveTool
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string mode = args.Length > 1 ? args[1] : "";
            if (mode == "dump" && args.Length >= 3)
            {
                Dump(Parse(args[2]));
            }
            else if (mode == "import" && args.Length >= 5)
            {
                CampaignSaveFile file = Parse(args[2]).ToCampaign(Letter(args[3]));
                File.WriteAllText(args[4], CampaignSaves.ToJson(file));
                Console.WriteLine($"wrote {args[4]} (planet {file.Planet}, {file.PlaySeconds} s, cleared {file.Cleared},"
                    + $" started {file.Started})");
            }
            else if (mode == "export" && args.Length >= 4)
            {
                CampaignSaveFile file = CampaignSaves.FromJson(File.ReadAllText(args[2]));
                byte[]? baseSave = args.Length >= 5 ? File.ReadAllBytes(args[4]) : null;
                int slot = args.Length >= 6 ? Letter(args[5]) : 0;
                File.WriteAllBytes(args[3], CartSave.Export(file, baseSave, slot));
                Console.WriteLine($"wrote {args[3]} (file {(char)('A' + slot)})");
                Dump(Parse(args[3]));
            }
            else if (mode == "test" && args.Length >= 3)
            {
                Test(args[2]);
            }
            else
            {
                Console.WriteLine("usage: -cartsave dump|import|export|test ... (see CartSaveTool.cs)");
            }
        }

        private static int Letter(string text) => Char.ToUpperInvariant(text[0]) - 'A';

        private static CartSave Parse(string path)
        {
            CartSave? save = CartSave.TryParse(File.ReadAllBytes(path), out string? error);
            return save ?? throw new InvalidDataException(error?.Replace('\n', ' '));
        }

        private static void Dump(CartSave save)
        {
            foreach (CartSaveFile f in save.Files)
            {
                string text = f.Used
                    ? $"{f.CompletionPercent}%, octoliths {f.Octoliths}, {f.PlaySeconds / 3600}:{f.PlaySeconds / 60 % 60:00}:"
                        + $"{f.PlaySeconds % 60:00}{(f.Cleared ? ", ending reached" : "")}"
                    : f.Damaged ? "DAMAGED (no block passes its checksum)" : "empty";
                Console.WriteLine($"  file {f.Letter}: {text}");
            }
        }

        private static int _pass;
        private static int _fail;

        private static void Check(string name, bool ok)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        private static void Test(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            CartSave save = Parse(path);
            Dump(save);
            Check("a padded save (.dsv footer) reads the same",
                CartSave.TryParse(bytes.Concat(new byte[122]).ToArray(), out _)?.Files.Count(f => f.Used)
                    == save.Files.Count(f => f.Used));
            Check("a short file is refused", CartSave.TryParse(bytes[..0x1000], out _) == null);
            foreach (CartSaveFile f in save.Files.Where(f => f.Used))
            {
                CampaignSaveFile app = save.ToCampaign(f.Index);
                string json = CampaignSaves.ToJson(app);
                CampaignSaveFile reloaded = CampaignSaves.FromJson(json);
                foreach (int slot in new[] { 0, f.Index })
                {
                    byte[] fresh = CartSave.Export(reloaded);
                    byte[] merged = CartSave.Export(reloaded, bytes, slot);
                    foreach ((string kind, byte[] data, int at) in new[] { ("fresh", fresh, 0), ("into the source", merged, slot) })
                    {
                        CartSave? back = CartSave.TryParse(data, out string? error);
                        Check($"file {f.Letter} -> {kind} save file {(char)('A' + at)}: reads back ({error})", back?.Files[at].Used == true);
                        if (back?.Files[at].Used != true) continue;
                        CartSaveFile g = back.Files[at];
                        Check($"  same %/octoliths/time/ending ({g.CompletionPercent}% {g.Octoliths} {g.PlaySeconds} {g.Cleared})",
                            g.CompletionPercent == f.CompletionPercent && g.Octoliths == f.Octoliths
                            && g.PlaySeconds == f.PlaySeconds && g.Cleared == f.Cleared);
                        string again = CampaignSaves.ToJson(Stamp(back.ToCampaign(at), app));
                        Check("  same app save after a second import", again == json);
                    }
                }
                // a file written back into its own slot of its own save = that block's story bytes unchanged
                if (f.Index >= 0)
                {
                    byte[] merged = CartSave.Export(reloaded, bytes, f.Index);
                    int diffs = 0;
                    var orig = OwnStoryBytes(bytes, f.Index);
                    var mine = OwnStoryBytes(merged, f.Index);
                    for (int i = 0; i < orig.Length; i++)
                    {
                        if (orig[i] != mine[i])
                        {
                            if (diffs++ < 12) Console.WriteLine($"    +0x{i:X4}: {orig[i]:X2} -> {mine[i]:X2}");
                        }
                    }
                    Check($"file {f.Letter} written back: {diffs} story bytes differ from the vanilla block", true);
                }
            }
            Console.WriteLine($"cartsave test: {_pass} pass, {_fail} fail");
        }

        // SavedAtUtc is the import time; take it from the first import so the JSON compares
        private static CampaignSaveFile Stamp(CampaignSaveFile file, CampaignSaveFile like)
        {
            file.SavedAtUtc = like.SavedAtUtc;
            return file;
        }

        private static byte[] OwnStoryBytes(byte[] data, int file)
        {
            int best = -1;
            uint bestIndex = 0;
            for (int b = 0; b < 13; b++)
            {
                int o = 0x100 + (file * 13 + b) * 0x1100;
                uint checksum = BitConverter.ToUInt32(data, o);
                uint index = BitConverter.ToUInt32(data, o + 4);
                if (checksum != 0 && (best < 0 || index > bestIndex))
                {
                    best = o;
                    bestIndex = index;
                }
            }
            return data.AsSpan(best + 8, 0x10B4).ToArray();
        }
    }
}
