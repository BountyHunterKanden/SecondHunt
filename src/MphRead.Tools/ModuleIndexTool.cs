using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MphRecomp.Import;
using MphRecomp.Import.Disc;

namespace MphRead
{
    // Module symbol index (docs/ENEMY_IMPORT.md 11.2 item 4):
    //   -moduleindex [out=<file.md>] [disc=<image>]...: every RSO (Trilogy, Corruption Wii) and REL (Echoes GC) module.
    // RSOs export only _prolog/_epilog, so their own code is unnamed; what they do name is (a) the engine functions they
    // import from the DOL, which give the base classes (a class whose AcceptScriptMsg/Think the module imports), and (b)
    // the strings in their data sections: AI state and trigger function names (the FSM's vocabulary), locators, effects.
    // RELs import by address (no names), so for Echoes GC only (b) is listed
    internal static class ModuleIndexTool
    {
        static readonly string Prime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "brawl_extract", "prime"));
        // 5+ characters, or a capitalised 4-letter word (Dead, Bite): shorter runs are mostly code bytes that look like text
        static readonly Regex Ident = new("^([A-Za-z_][A-Za-z0-9_]{4,}|[A-Z][a-z]{3})$");
        static readonly HashSet<string> Boiler = new() { "RSO_Setup", "Address", "_prolog", "_epilog", "_unresolved" };

        static string CStr(byte[] b, int o)
        {
            int e = o;
            while (e < b.Length && b[e] != 0) e++;
            return Encoding.Latin1.GetString(b, o, e - o);
        }

        // the class after a method name in a CodeWarrior mangled symbol: "AcceptScriptMsg__10CPatternedFR..." -> CPatterned
        static string? ClassOf(string sym, string method)
        {
            string head = method + "__";
            if (!sym.StartsWith(head)) return null;
            int i = head.Length, n = 0;
            while (i < sym.Length && char.IsDigit(sym[i])) n = n * 10 + (sym[i++] - '0');
            return n > 0 && i + n <= sym.Length ? sym.Substring(i, n) : null;
        }

        public record Module(string Disc, string Path, int Imports, List<string> Bases, List<string> Strings);

        public static Module? Read(string disc, string path, byte[] b)
        {
            bool rso = path.EndsWith(".rso", StringComparison.OrdinalIgnoreCase);
            if (b.Length < 0x60) return null;
            int nSec = (int)Be.U32(b, rso ? 0x08 : 0x0C), secInfo = (int)Be.U32(b, rso ? 0x0C : 0x10);
            if (nSec <= 0 || nSec > 64 || secInfo + 8 * nSec > b.Length) return null;
            var imports = new List<string>();
            int symStart = b.Length;
            if (rso)
            {
                int expO = (int)Be.U32(b, 0x40), impO = (int)Be.U32(b, 0x4C), impS = (int)Be.U32(b, 0x50), impN = (int)Be.U32(b, 0x54);
                for (int i = 0; i < impS / 12; i++) imports.Add(CStr(b, impN + (int)Be.U32(b, impO + 12 * i)));
                symStart = Math.Min(expO, impO);
            }
            var strings = new List<string>();
            var seen = new HashSet<string>();
            for (int s = 0; s < nSec; s++)
            {
                uint off = Be.U32(b, secInfo + 8 * s);
                int size = (int)Be.U32(b, secInfo + 8 * s + 4);
                if ((off & 1) != 0 || off == 0 || size == 0 || off + size > b.Length) continue;
                int end = Math.Min((int)off + size, symStart);
                for (int i = (int)off; i < end; )
                {
                    if (b[i] < 0x20 || b[i] >= 0x7F) { i++; continue; }
                    int j = i;
                    while (j < end && b[j] >= 0x20 && b[j] < 0x7F) j++;
                    // a C string: printable run ending in a NUL
                    if (j < b.Length && b[j] == 0 && j - i >= 3)
                    {
                        string str = Encoding.ASCII.GetString(b, i, j - i);
                        if (Ident.IsMatch(str) && !Boiler.Contains(str) && seen.Add(str)) strings.Add(str);
                    }
                    i = j + 1;
                }
            }
            var bases = imports.Select(x => ClassOf(x, "AcceptScriptMsg") ?? ClassOf(x, "Think")).Where(c => c != null)
                .Select(c => c!).Distinct().ToList();
            return new Module(disc, path, imports.Count, bases, strings);
        }

        public static void Run(string[] args)
        {
            string? outPath = args.FirstOrDefault(a => a.StartsWith("out="))?[4..];
            var discs = args.Where(a => a.StartsWith("disc=")).Select(a => a[5..]).ToList();
            if (discs.Count == 0)
            {
                discs.Add(Path.Combine(Prime, "trilogy.rvz"));
                discs.Add(Path.Combine(Prime, "corruption.rvz"));
                discs.Add(Path.Combine(Prime, "echoes_retail.rvz"));
            }
            var mods = new List<Module>();
            foreach (string disc in discs)
            {
                using IFileSource src = DiscImage.Open(disc);
                foreach (string p in src.Paths.Where(p => p.EndsWith(".rso", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(".rel", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal))
                {
                    Module? m = Read(Path.GetFileName(disc), p, src.ReadFile(p));
                    if (m == null) Console.WriteLine($"{disc}: {p}: not a module");
                    else mods.Add(m);
                }
            }
            // a Wii Corruption module whose name and strings match its Trilogy copy is listed once
            var sb = new StringBuilder();
            sb.AppendLine("# Module symbol index (generated by `-moduleindex`, docs/ENEMY_IMPORT.md 11.2 item 4)");
            sb.AppendLine();
            sb.AppendLine("Per module: the disc, imported DOL symbols (RSO only), base classes (classes whose AcceptScriptMsg or Think the");
            sb.AppendLine("module imports), and the identifier strings in its data sections (AI state/trigger names, locators, effects,");
            sb.AppendLine("in file order). Generated; do not edit by hand, rerun the tool.");
            var trilogyKeys = new HashSet<string>();
            foreach (var g in mods.GroupBy(m => m.Disc))
            {
                sb.AppendLine();
                sb.AppendLine($"## {g.Key}");
                foreach (Module m in g)
                {
                    string name = Path.GetFileNameWithoutExtension(m.Path);
                    string key = name + "|" + String.Join(",", m.Strings);
                    if (m.Disc.StartsWith("trilogy")) trilogyKeys.Add(key);
                    else if (trilogyKeys.Contains(key)) { sb.AppendLine($"- **{name}**: same as the Trilogy copy"); continue; }
                    sb.AppendLine();
                    sb.AppendLine($"### {name} (`{m.Path}`)");
                    if (m.Path.EndsWith(".rso", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine($"- {m.Imports} imports; base: {(m.Bases.Count == 0 ? "none" : String.Join(", ", m.Bases))}");
                    sb.AppendLine($"- strings ({m.Strings.Count}): {(m.Strings.Count == 0 ? "none" : String.Join(", ", m.Strings.Take(200)))}"
                        + (m.Strings.Count > 200 ? $" (+{m.Strings.Count - 200})" : ""));
                }
            }
            Console.WriteLine($"{mods.Count} modules from {discs.Count} discs");
            if (outPath != null) File.WriteAllText(outPath, sb.ToString().Replace("\r\n", "\n"));
            else Console.Write(sb.ToString());
        }
    }
}
