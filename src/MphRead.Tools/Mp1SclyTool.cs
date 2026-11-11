using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Retro;

namespace MphRead
{
    // Prime 1 room scripts (docs/ENEMY_IMPORT.md 11.2 item 2):
    //   -mp1scly census [disc=<mp1 .iso/.rvz>]: read every MREA's SCLY (Core Retro/Scly1.cs; fails loudly on a bad
    //     layer) and, per object type with a PatternedAI block, print instance count, health, contact damage and the
    //     Power..Phazon vulnerabilities (most common value, with the spread)
    //   -mp1scly types [disc=]: every object type with its count, patterned count and top names
    internal static class Mp1SclyTool
    {
        static readonly string Prime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "brawl_extract", "prime"));

        static string? Arg(string[] args, string key) => args.FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..];

        static string Spread<T>(IEnumerable<T> xs, Func<T, string> f, int top = 4)
        {
            var g = xs.GroupBy(f).OrderByDescending(x => x.Count()).ToList();
            return g.Count == 1 ? g[0].Key : String.Join(", ", g.Take(top).Select(x => $"{x.Key} x{x.Count()}")) + (g.Count > top ? $" (+{g.Count - top})" : "");
        }

        public static void Run(string[] args)
        {
            string disc = Arg(args, "disc") ?? Path.Combine(Prime, "mp1.iso");
            using IFileSource src = DiscImage.Open(disc);
            var seen = new HashSet<uint>();
            var objs = new List<(Scly1.Obj O, Scly1.Patterned? P)>();
            int areas = 0, failed = 0;
            foreach (string path in src.Paths.Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
            {
                Pak pk;
                try { pk = new Pak(src.ReadFile(path), path); } catch (InvalidDataException) { continue; }
                foreach (uint rid in pk.Order)
                {
                    if (pk.TypeOf(rid) != "MREA" || !seen.Add(rid)) continue;
                    byte[] raw = pk.Get(rid);
                    if (!Scly1.IsPrime1Area(raw)) continue;
                    areas++;
                    try { foreach (Scly1.Obj o in Scly1.ReadScripts(raw)) objs.Add((o, Scly1.FindPatterned(o))); }
                    catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
                    { failed++; Console.WriteLine($"{path} {rid:x8}: {ex.Message}"); }
                }
            }
            Console.WriteLine($"{Path.GetFileName(disc)}: {areas} areas, {failed} failed, {objs.Count} objects");
            if (args.Contains("types"))
            {
                foreach (var g in objs.GroupBy(x => x.O.Type).OrderBy(g => g.Key))
                    Console.WriteLine($"0x{g.Key:X2} | {g.Count()} | {g.Count(x => x.P != null)} | " + String.Join(", ",
                        g.Select(x => x.O.Name).GroupBy(n => n).OrderByDescending(n => n.Count()).Take(3).Select(n => n.Key)));
                return;
            }
            Console.WriteLine("type | objects | patterned | health | contact damage | " + String.Join(" / ", Scly1.VulnWeapons) + " | names");
            foreach (var g in objs.GroupBy(x => x.O.Type).OrderBy(g => g.Key))
            {
                var pats = g.Where(x => x.P != null).Select(x => x.P!).ToList();
                if (pats.Count == 0) continue;
                string vul = String.Join(" / ", Enumerable.Range(0, Scly1.VulnWeapons.Length).Select(i => Spread(pats, p => p.Vulns[i].ToString(), 1)));
                string names = String.Join(", ", g.Select(x => x.O.Name).GroupBy(n => n).OrderByDescending(n => n.Count()).Take(3).Select(n => n.Key));
                Console.WriteLine($"0x{g.Key:X2} | {g.Count()} | {pats.Count} | {Spread(pats, p => p.Health.ToString("0.##"))} | " +
                    $"{Spread(pats, p => p.ContactDamage.ToString("0.##"))} | {vul} | {names}");
            }
        }
    }
}
