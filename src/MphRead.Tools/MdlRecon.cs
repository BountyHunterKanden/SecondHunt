using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MphRead
{
    // PHASE 0 recon for the HD TEV-evaluator project (Brawl MDL0 -> faithful GX render). Maps the
    // MDL0 section layout (resource groups + their entry names) for each user-supplied .brres, so we
    // know exactly where Materials / Shaders / Objects live per model -- the navigation that tripped us
    // up on the Samus-family layout. Read-only over the user's own extracted files; ships nothing.
    // Run: MphRead.Tools.dll -mdlrecon <brres-or-folder>
    internal static class MdlRecon
    {
        static uint U32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        static string Str(byte[] b, int o)
        { var sb = new StringBuilder(); for (int i = o; i < b.Length && b[i] >= 32 && b[i] < 127 && sb.Length < 40; i++) sb.Append((char)b[i]); return sb.ToString(); }
        static int Find(byte[] b, string s)
        { var p = Encoding.ASCII.GetBytes(s); for (int i = 0; i <= b.Length - p.Length; i++) { bool ok = true; for (int j = 0; j < p.Length; j++) if (b[i + j] != p[j]) { ok = false; break; } if (ok) return i; } return -1; }

        // Section offset table lives at mdl0+0x10; section i is U32(mdl0+0x10+i*4) relative to mdl0.
        // v9 order: 0 Defs, 1 Bones, 2 Verts, 3 Norms, 4 Colors, 5 UVs, 6 Materials, 7 Shaders, 8 Objects,
        // 9 Textures. Returns the group offset for a section, or -1.
        static int Section(byte[] b, int mdl0, int idx)
        {
            uint rel = U32(b, mdl0 + 0x10 + idx * 4);
            return rel == 0 ? -1 : mdl0 + (int)rel;
        }

        static readonly string[] CC = { "CPREV","APREV","C0","A0","C1","A1","C2","A2","TEXC","TEXA","RASC","RASA","ONE","HALF","KONST","ZERO" };
        static readonly string[] AC = { "APREV","A0","A1","A2","TEXA","RASA","KONST","ZERO" };
        static readonly string[] DST = { "PREV","C0","C1","C2" };
        static readonly string[] OP = { "+","-" };
        static readonly string[] BIAS = { "","+0.5","-0.5","?" };
        static readonly string[] SCL = { "*1","*2","*4","/2" };

        // PHASE 0: parse each material's TEV shader program from the Shaders section (idx 7) and print the
        // real color/alpha combine stages + texture-order per stage. This is the ground truth the GX
        // evaluator must run. Aggregates which GX features actually appear (to bound the evaluator).
        static readonly System.Collections.Generic.SortedSet<string> _feat = new();
        public static void DumpTev(string[] args)
        {
            string path = args.Length > 1 ? args[1]
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "brawl_extract", "toyfig", "DATA", "files", "toy", "fig"));
            var files = Directory.Exists(path) ? Directory.GetFiles(path, "*.brres").OrderBy(f => f).ToArray() : new[] { path };
            foreach (var f in files)
            {
                byte[] b = File.ReadAllBytes(f);
                int mdl0 = Find(b, "MDL0"); if (mdl0 < 0) continue;
                int shg = Section(b, mdl0, 7); if (shg < 0) { Console.WriteLine($"{Path.GetFileName(f)}: no shaders"); continue; }
                uint count = U32(b, shg + 4);
                Console.WriteLine($"\n##### {Path.GetFileName(f)}  ({count} shaders) #####");
                for (int i = 1; i <= count; i++)
                {
                    int e = shg + 8 + i * 0x10;
                    string name = Str(b, shg + (int)U32(b, e + 8));
                    int st = shg + (int)U32(b, e + 0xC);
                    int hi = st + 0x200;
                    Console.WriteLine($"  -- {name} --");
                    for (int k = st; k + 5 <= hi && k + 5 <= b.Length; k++)
                    {
                        if (b[k] != 0x61) continue;
                        int reg = b[k + 1];
                        uint d = (uint)((b[k + 2] << 16) | (b[k + 3] << 8) | b[k + 4]);
                        if (reg >= 0xC0 && reg <= 0xDF && (reg & 1) == 0)   // TEV color env
                        {
                            int id = (int)(d & 0xF), ic = (int)((d >> 4) & 0xF), ib = (int)((d >> 8) & 0xF), ia = (int)((d >> 12) & 0xF);
                            int bias = (int)((d >> 16) & 3), op = (int)((d >> 18) & 1), clamp = (int)((d >> 19) & 1), scale = (int)((d >> 20) & 3), dest = (int)((d >> 22) & 3);
                            Console.WriteLine($"     C{(reg - 0xC0) / 2}: {DST[dest]}=({CC[ia]},{CC[ib]},{CC[ic]},{CC[id]}) {OP[op]}{BIAS[bias]}{SCL[scale]}{(clamp == 1 ? " clamp" : "")}");
                            _feat.Add($"Cin:{CC[ia]}"); _feat.Add($"Cin:{CC[ib]}"); _feat.Add($"Cin:{CC[ic]}"); _feat.Add($"Cin:{CC[id]}"); _feat.Add($"Cscale:{SCL[scale]}"); _feat.Add($"Cbias:{bias}"); _feat.Add($"Cdst:{DST[dest]}");
                        }
                        else if (reg >= 0xC1 && reg <= 0xDF && (reg & 1) == 1) // TEV alpha env
                        {
                            int id = (int)((d >> 4) & 7), ic = (int)((d >> 7) & 7), ib = (int)((d >> 10) & 7), ia = (int)((d >> 13) & 7);
                            int bias = (int)((d >> 16) & 3), op = (int)((d >> 18) & 1), scale = (int)((d >> 20) & 3), dest = (int)((d >> 22) & 3);
                            Console.WriteLine($"     A{(reg - 0xC1) / 2}: {DST[dest]}a=({AC[ia]},{AC[ib]},{AC[ic]},{AC[id]}) {OP[op]}{BIAS[bias]}{SCL[scale]}");
                            _feat.Add($"Ain:{AC[ia]}"); _feat.Add($"Ain:{AC[ib]}"); _feat.Add($"Ain:{AC[ic]}"); _feat.Add($"Ain:{AC[id]}");
                        }
                        else if (reg >= 0x28 && reg <= 0x2F)                 // TREF (2 stages)
                        {
                            int s = (reg - 0x28) * 2;
                            int m0 = (int)(d & 7), c0 = (int)((d >> 3) & 7), en0 = (int)((d >> 6) & 1), r0 = (int)((d >> 7) & 7);
                            int m1 = (int)((d >> 12) & 7), c1 = (int)((d >> 15) & 7), en1 = (int)((d >> 18) & 1), r1 = (int)((d >> 19) & 7);
                            Console.WriteLine($"     ord s{s}: tex={(en0 == 1 ? m0.ToString() : "-")} coord={c0} ras={r0};  s{s + 1}: tex={(en1 == 1 ? m1.ToString() : "-")} coord={c1} ras={r1}");
                        }
                        else if (reg >= 0xF6 && reg <= 0xF9) { _feat.Add("KSEL:used"); }
                    }
                }
            }
            Console.WriteLine("\n\n=====================  GX FEATURE CATALOG (across all models)  =====================");
            foreach (var s in _feat) Console.WriteLine("  " + s);
        }

        public static void Run(string[] args)
        {
            string path = args.Length > 1 ? args[1]
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "brawl_extract", "toyfig", "DATA", "files", "toy", "fig"));
            var files = Directory.Exists(path)
                ? Directory.GetFiles(path, "*.brres").OrderBy(f => f).ToArray()
                : new[] { path };
            foreach (var f in files)
            {
                byte[] b = File.ReadAllBytes(f);
                int mdl0 = Find(b, "MDL0");
                if (mdl0 < 0) { Console.WriteLine($"\n{Path.GetFileName(f)}: no MDL0"); continue; }
                int ver = (int)U32(b, mdl0 + 8);
                int size = (int)U32(b, mdl0 + 4);
                int end = Math.Min(b.Length, mdl0 + size);
                Console.WriteLine($"\n##### {Path.GetFileName(f),-16} MDL0 v{ver} @0x{mdl0:X} size=0x{size:X} #####");
                // Scan the MDL0 header region for section-offset-table entries that point to resource
                // groups. A group = u32 groupSize, u32 count, then (count+1) 0x10-byte entries whose
                // name/data offsets are RELATIVE TO THE GROUP START. Print the section-table slot, the
                // group offset, its count, and the first few entry names (which identify the section).
                for (int p = mdl0 + 0x0C; p < mdl0 + 0x60 && p + 4 <= end; p += 4)
                {
                    uint rel = U32(b, p);
                    if (rel == 0 || rel >= (uint)size) continue;
                    int g = mdl0 + (int)rel;
                    if (g + 8 >= end) continue;
                    uint count = U32(b, g + 4);
                    if (count < 1 || count > 400) continue;
                    if (g + 8 + (int)(count + 1) * 0x10 > end) continue;
                    int e1 = g + 8 + 0x10;                       // first real entry
                    uint noff = U32(b, e1 + 8);
                    // Name/data offsets are RELATIVE TO THE GROUP START. Names point into the BRRES-level
                    // string pool that lives AFTER the MDL0 block, so bound them by the whole file, not
                    // the MDL0 size; data (structs) live inside the MDL0.
                    if (noff == 0 || g + (int)noff >= b.Length) continue;
                    string n1 = Str(b, g + (int)noff);
                    if (n1.Length < 2) continue;                 // entry name must be readable ASCII
                    uint doff = U32(b, e1 + 0xC);
                    int st = g + (int)doff;
                    uint dsz = st > mdl0 && st + 4 < end ? U32(b, st) : 0;   // first entry's struct size (if any)
                    var names = new StringBuilder();
                    for (int i = 1; i <= Math.Min(5, count); i++)
                    {
                        int e = g + 8 + i * 0x10;
                        names.Append(Str(b, g + (int)U32(b, e + 8))).Append(" | ");
                    }
                    Console.WriteLine($"  tbl+0x{p - mdl0:X3} -> grp@0x{g - mdl0:X5} n={count,-3} data@0x{st - mdl0:X5} dsz=0x{dsz:X4}  [{names}]");
                }
            }
        }
    }
}
