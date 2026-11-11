using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Retro;
using MphRecomp.Import.Retro3;

namespace MphRead
{
    // Corruption room scripts (docs/ENEMY_IMPORT.md 11.2 item 3), read by Core Retro3/Mrea3.cs:
    //   -mp3scly types [disc=]: every area's script objects; per type the count, the areas and top names
    //   -mp3scly dump <TYPE> [n=1]: the property tree of the first n objects of a type, names from PropertyMap.xml
    //   -mp3scly census: per type with a PatternedAI struct: count, health, contact damage, top names
    //   -mp3scly hunters: per hunter type, health per difficulty, attacks (named DamageInfo), assets by type, CHAR clips
    // Property names come from PrimeWorldEditor's MP2 PropertyMap.xml (research reference, nothing ships); ids are
    // name+type hashes, so the names Corruption shares with Echoes resolve and the rest print as hex
    internal static class Mp3SclyTool
    {
        static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        static readonly string Prime = Path.Combine(Root, "brawl_extract", "prime");

        static string? Arg(string[] args, string key) => args.FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..];

        static Dictionary<uint, (string Name, string Type)> LoadNames()
        {
            var res = new Dictionary<uint, (string, string)>();
            string path = Path.Combine(Root, "extract_out", "multiplayer", "mp2", "mp2", "PropertyMap.xml");
            if (!File.Exists(path)) return res;
            string xml = File.ReadAllText(path);
            foreach (Match m in Regex.Matches(xml, "<Key ID=\"0x([0-9A-Fa-f]+)\" Type=\"([^\"]*)\"/>\\s*<Value Name=\"([^\"]*)\"/>"))
            {
                res.TryAdd(Convert.ToUInt32(m.Groups[1].Value, 16), (m.Groups[3].Value, m.Groups[2].Value));
            }
            return res;
        }

        static Dictionary<uint, (string Name, string Type)> _names = new();
        static string NameOf(uint id) => _names.TryGetValue(id, out var n) ? $"{n.Name}:{n.Type}" : $"{id:X8}";

        static Mrea.Property? FindByName(Mrea.Property p, string name, int depth = 0)
        {
            if (p.Children == null || depth > 4) return null;
            foreach (Mrea.Property c in p.Children)
            {
                if (_names.TryGetValue(c.Id, out var n) && n.Name == name) return c;
            }
            foreach (Mrea.Property c in p.Children)
            {
                Mrea.Property? f = FindByName(c, name, depth + 1);
                if (f != null) return f;
            }
            return null;
        }

        static Mrea.Property? FindByType(Mrea.Property p, string type, int depth = 0)
        {
            if (p.Children == null || depth > 4) return null;
            foreach (Mrea.Property c in p.Children)
            {
                if (_names.TryGetValue(c.Id, out var n) && n.Type == type) return c;
                Mrea.Property? f = FindByType(c, type, depth + 1);
                if (f != null) return f;
            }
            return null;
        }

        static void Dump(Mrea.Property p, string indent)
        {
            foreach (Mrea.Property c in p.Children ?? new())
            {
                string val = c.Children != null ? $"struct x{c.Children.Count}" :
                    c.Data.Length == 4 ? $"{Be.F32(c.Data, 0):0.###} / {Be.I32(c.Data, 0)} / {Be.U32(c.Data, 0):x8}" :
                    c.Data.Length == 1 ? $"{c.Data[0]}" :
                    c.Data.Length == 8 ? $"{Be.U32(c.Data, 0):x8}{Be.U32(c.Data, 4):x8}" :
                    c.Data.Length <= 48 && c.Data.Length > 0 && c.Data.Take(c.Data.Length - 1).All(b => b >= 32 && b < 127) ? $"\"{c.Str}\"" :
                    $"{c.Data.Length} bytes";
                Console.WriteLine($"{indent}{NameOf(c.Id)} = {val}");
                if (c.Children != null) Dump(c, indent + "  ");
            }
        }

        static string Spread<T>(IEnumerable<T> xs, Func<T, string> f, int top = 4)
        {
            var g = xs.GroupBy(f).OrderByDescending(x => x.Count()).ToList();
            return g.Count == 1 ? g[0].Key : String.Join(", ", g.Take(top).Select(x => $"{x.Key} x{x.Count()}")) + (g.Count > top ? $" (+{g.Count - top})" : "");
        }

        static string TypeName(uint id) => _names.TryGetValue(id, out var n) ? n.Type : "";
        static string PropName(uint id) => _names.TryGetValue(id, out var n) ? n.Name : $"{id:X8}";

        // hunter census (11.2 item 8): per hunter type, health and contact damage per difficulty block, every named
        // DamageInfo (the attacks), the referenced assets by type (WPSC = projectiles), and the CHAR's animation count
        // what Hunters needs from a pak: has/type of an asset id, a line describing a character asset, and cleanup
        sealed record PakView(Func<ulong, bool> Has, Func<ulong, string> Type, Func<ulong, uint, string> Character, IDisposable Owner);

        static PakView View3(IFileSource src, string path)
        {
            var pk = new Pak3(src.OpenRead(path), path, leaveOpen: false);
            return new PakView(pk.Contains, pk.TypeOf, (cid, _) =>
            {
                byte[] cb = pk.Get(cid);
                var anims = Char3.Animations(cb, id => pk.Contains(id) && pk.TypeOf(id) == "ANIM");
                return $"CHAR {cid:x16} \"{Char3.CharIds(cb, id => pk.Contains(id) ? pk.TypeOf(id) : null).Name}\": {anims.Count} animations"
                    + $" ({anims.SelectMany(a => Char3.Prims(a.Anim)).Select(m => m.AnimId).Distinct().Count()} distinct clips)";
            }, pk);
        }

        static PakView View2(IFileSource src, string path)
        {
            var pk = new Pak(src.ReadFile(path), path);
            return new PakView(id => id <= uint.MaxValue && pk.Contains((uint)id), id => pk.TypeOf((uint)id), (cid, index) =>
            {
                Ancs a = Ancs.Parse(pk.Get((uint)cid));
                string ch = index < a.Characters.Count ? a.Characters[(int)index].Name : $"character {index}";
                var anims = a.Set?.Anims ?? new();
                return $"ANCS {cid:x8} \"{ch}\" ({a.Characters.Count} characters): {anims.Count} animations"
                    + $" ({anims.SelectMany(x => Ancs.Prims(x.Anim)).Select(m => m.AnimId).Distinct().Count()} distinct clips)";
            }, new Noop());
        }

        sealed class Noop : IDisposable { public void Dispose() { } }

        // hunter census (11.2 item 8): per hunter type, health and contact damage per difficulty block, every named
        // DamageInfo (the attacks), the referenced assets by type (WPSC = projectiles), and the character's animation count
        static void Hunters(List<(Mrea.ScriptObject O, string Pak, ulong Area)> objs, Func<string, PakView> open, string[] types)
        {
            static float? F(Mrea.Property? p) => p != null && p.Data.Length == 4 ? Be.F32(p.Data, 0) : null;
            foreach (string t in types)
            {
                var all = objs.Where(x => x.O.Type == t).ToList();
                Console.WriteLine($"== {t}: {all.Count} objects in {all.Select(x => x.Area).Distinct().Count()} areas; names {Spread(all, x => x.O.Name)}");
                if (all.Count == 0) continue;
                var x0 = all[0];
                PakView pk = open(x0.Pak);
                using IDisposable owner = pk.Owner;
                var top = x0.O.Root.Children ?? new();
                var chars = new HashSet<(ulong, uint)>();
                foreach (Mrea.Property blk in top.Where(c => TypeName(c.Id) == "PatternedAITypedef"))
                {
                    var hs = all.Select(x => x.O.Root.Find(blk.Id)).Where(p => p != null).Select(p => p!).ToList();
                    string health = Spread(hs, p => F(FindByName(FindByName(p, "Health") ?? p, "Health"))?.ToString("0.##") ?? "?");
                    string contact = Spread(hs, p => F(FindByName(FindByName(p, "ContactDamage") ?? p, "DI_Damage"))?.ToString("0.##") ?? "?");
                    Console.WriteLine($"  {PropName(blk.Id)}: health {health}, contact {contact}");
                    foreach (Mrea.Property h in hs)
                    {
                        // Corruption: CharacterAnimationSet (CHAR id u64 + anim); Echoes: AnimationSet (ANCS u32, character u32, anim)
                        Mrea.Property? ca = FindByType(h, "AnimationSet") ?? FindByName(h, "CharacterAnimationInformation");
                        if (ca == null) continue;
                        if (ca.Data.Length == 12 && TypeName(ca.Id) != "AnimationSet") chars.Add((Pak3.U64(ca.Data, 0), 0));
                        else if (ca.Data.Length >= 8) chars.Add((Be.U32(ca.Data, 0), Be.U32(ca.Data, 4)));
                    }
                }
                // attacks: DamageInfo structs outside the PatternedAI blocks
                var attacks = new List<string>();
                var assets = new Dictionary<string, int>();
                void Walk(Mrea.Property p, string path)
                {
                    foreach (Mrea.Property c in p.Children ?? new())
                    {
                        string tn = TypeName(c.Id), nm = PropName(c.Id);
                        if (tn == "PatternedAITypedef" || tn == "ActorParameters" || tn == "EditorProperties") continue;
                        if (tn == "DamageInfo")
                        {
                            string s = $"{(nm == "DamageInfo" ? path + ".DamageInfo" : nm)} {F(FindByName(c, "DI_Damage"))?.ToString("0.##") ?? "?"}";
                            if (!attacks.Contains(s)) attacks.Add(s);
                            continue;
                        }
                        if (c.Children != null) { Walk(c, nm); continue; }
                        if (c.Data.Length == 8 || c.Data.Length == 4 && tn == "asset")
                        {
                            ulong id = c.Data.Length == 8 ? Pak3.U64(c.Data, 0) : Be.U32(c.Data, 0);
                            if (id != 0 && id != ulong.MaxValue && id != uint.MaxValue && pk.Has(id))
                            {
                                string ty = pk.Type(id);
                                assets[ty] = assets.GetValueOrDefault(ty) + 1;
                            }
                        }
                    }
                }
                Walk(x0.O.Root, t);
                Console.WriteLine($"  attacks ({attacks.Count}): {String.Join("; ", attacks)}");
                Console.WriteLine($"  assets: {String.Join(", ", assets.OrderByDescending(a => a.Value).Select(a => $"{a.Key} {a.Value}"))}");
                foreach ((ulong cid, uint index) in chars)
                {
                    if (!pk.Has(cid)) { Console.WriteLine($"  character {cid:x}: not in {x0.Pak}"); continue; }
                    try { Console.WriteLine("  " + pk.Character(cid, index)); }
                    catch (InvalidDataException ex) { Console.WriteLine($"  character {cid:x}: {ex.Message}"); }
                }
            }
        }

        public static void Run(string[] args)
        {
            _names = LoadNames();
            string disc = Arg(args, "disc") ?? Path.Combine(Prime, "corruption.rvz");
            using IFileSource src = DiscImage.Open(disc);
            int fi = Array.IndexOf(args, "find");
            if (fi >= 0 && fi + 1 < args.Length)
            {
                // which non-pak files (main.dol, the RSO modules) hold an ASCII string
                byte[] needle = System.Text.Encoding.ASCII.GetBytes(args[fi + 1]);
                foreach (string path in src.Paths.Where(p => !p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                    && (p.EndsWith(".dol", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".rso", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".rel", StringComparison.OrdinalIgnoreCase))))
                {
                    byte[] b = src.ReadFile(path);
                    var hits = new List<int>();
                    for (int i = b.AsSpan().IndexOf(needle); i >= 0 && hits.Count < 8; )
                    {
                        hits.Add(i);
                        int next = b.AsSpan(i + 1).IndexOf(needle);
                        i = next < 0 ? -1 : i + 1 + next;
                    }
                    if (hits.Count > 0)
                        Console.WriteLine($"{path} ({b.Length} bytes): " + String.Join("; ", hits.Select(h =>
                        {
                            int s = h; while (s > 0 && b[s - 1] >= 32 && b[s - 1] < 127) s--;
                            int e = h; while (e < b.Length && b[e] >= 32 && b[e] < 127) e++;
                            return $"0x{h:x} \"{System.Text.Encoding.ASCII.GetString(b, s, Math.Min(e - s, 80))}\"";
                        })));
                }
                return;
            }
            if (args.Contains("hunters") && disc.Contains("echoes", StringComparison.OrdinalIgnoreCase))
            {
                // Echoes (MREA 0x19, 32-bit ids): the same census for Dark Samus and the Echoes Guardians
                var eobjs = new List<(Mrea.ScriptObject O, string Pak, ulong Area)>();
                var eseen = new HashSet<uint>();
                foreach (string path in src.Paths.Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
                {
                    Pak pk;
                    try { pk = new Pak(src.ReadFile(path), path); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { continue; }
                    foreach (var kv in pk.Resources)
                    {
                        if (kv.Value.Type != "MREA" || !eseen.Add(kv.Key)) continue;
                        try { foreach (Mrea.ScriptObject o in Mrea.Read(pk.Get(kv.Key)).ReadScripts()) eobjs.Add((o, path, kv.Key)); }
                        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or InvalidDataException) { }
                    }
                }
                Console.WriteLine($"{Path.GetFileName(disc)}: {eseen.Count} areas, {eobjs.Count} objects");
                string[] et = Arg(args, "types")?.Split(',') ?? new[] { "DRKS" };
                Hunters(eobjs, p => View2(src, p), et);
                return;
            }
            var seen = new HashSet<ulong>();
            var objs = new List<(Mrea.ScriptObject O, string Pak, ulong Area)>();
            int areas = 0, failed = 0;
            foreach (string path in src.Paths.Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
            {
                Pak3 pk;
                try { pk = new Pak3(src.OpenRead(path), path, leaveOpen: false); } catch (InvalidDataException) { continue; }
                using (pk)
                {
                    foreach (ulong id in pk.Order)
                    {
                        if (pk.TypeOf(id) != "MREA" || !seen.Add(id)) continue;
                        byte[] raw = pk.Get(id);
                        if (!Mrea3.IsCorruptionArea(raw)) continue;
                        areas++;
                        try
                        {
                            Mrea3 a = Mrea3.Read(raw);
                            if (args.Contains("probe") && areas <= 2)
                            {
                                Console.WriteLine($"{path} {id:x16}: {a.LayerCount} layers, {a.Sections.Length} sections, numbers "
                                    + String.Join(" ", a.SectionNumbers.Select(kv => $"{kv.Key}={kv.Value}")));
                                int s0 = a.Section("SOBJ");
                                if (s0 >= 0) Console.WriteLine("  SOBJ head: " + String.Join(" ", a.Sections[s0].Take(24).Select(b => b.ToString("x2"))));
                            }
                            foreach (Mrea.ScriptObject o in a.ReadScripts()) objs.Add((o, path, id));
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or InvalidDataException)
                        { failed++; if (failed <= 10) Console.WriteLine($"{path} {id:x16}: {ex.GetType().Name} {ex.Message}"); }
                    }
                }
            }
            Console.WriteLine($"{Path.GetFileName(disc)}: {areas} areas, {failed} failed, {objs.Count} objects, {_names.Count} property names");
            if (args.Contains("types"))
            {
                foreach (var g in objs.GroupBy(x => x.O.Type).OrderBy(g => g.Key))
                    Console.WriteLine($"{g.Key} | {g.Count()} | {g.Select(x => x.Area).Distinct().Count()} areas | "
                        + (g.Any(x => FindByType(x.O.Root, "PatternedAITypedef") != null || FindByName(x.O.Root, "Patterned") != null) ? "patterned | " : "")
                        + String.Join(", ", g.Select(x => x.O.Name).GroupBy(n => n).OrderByDescending(n => n.Count()).Take(3).Select(n => n.Key)));
                return;
            }
            if (args.Contains("hunters"))
            {
                Hunters(objs, p => View3(src, p), new[] { "RUND", "GHOR", "GHRU", "GAND", "DRKS", "DKSE" });
                return;
            }
            int di = Array.IndexOf(args, "dump");
            if (di >= 0 && di + 1 < args.Length)
            {
                int n = int.TryParse(Arg(args, "n"), out int nn) ? nn : 1;
                foreach (var x in objs.Where(x => x.O.Type == args[di + 1]).Take(n))
                {
                    Console.WriteLine($"{x.O.Type} {x.O.Id:x8} \"{x.O.Name}\" ({x.Pak} {x.Area:x16})");
                    Dump(x.O.Root, "  ");
                }
                return;
            }
            // census: PatternedInfo's HealthInfo.Health, ContactDamage.DI_Damage, and the vulnerability multipliers that
            // are not 100 (most common per weapon)
            static float? F(Mrea.Property? p) => p != null && p.Data.Length == 4 ? Be.F32(p.Data, 0) : null;
            Console.WriteLine("type | objects | areas | health | contact damage | vulnerabilities (multiplier % where not 100) | names");
            foreach (var g in objs.GroupBy(x => x.O.Type).OrderBy(g => g.Key))
            {
                var pats = g.Select(x => FindByType(x.O.Root, "PatternedAITypedef")).Where(p => p != null).Select(p => p!).ToList();
                if (pats.Count == 0) continue;
                string health = Spread(pats, p => F(FindByName(FindByName(p, "Health") ?? p, "Health"))?.ToString("0.##") ?? "?");
                string contact = Spread(pats, p => F(FindByName(FindByName(p, "ContactDamage") ?? p, "DI_Damage"))?.ToString("0.##") ?? "?");
                var vulnStructs = pats.Select(p => FindByName(p, "Vulnerability")).Where(v => v?.Children != null).ToList();
                var weapons = new List<string>();
                if (vulnStructs.Count > 0)
                {
                    foreach (Mrea.Property w in vulnStructs[0]!.Children!)
                    {
                        string wn = _names.TryGetValue(w.Id, out var nm) ? nm.Name : $"{w.Id:X8}";
                        string mult = Spread(vulnStructs, v => F(FindByName(v!.Find(w.Id) ?? new Mrea.Property(), "DamageMultiplier"))?.ToString("0.##") ?? "?", 2);
                        if (mult != "100") weapons.Add($"{wn} {mult}");
                    }
                }
                string names = String.Join(", ", g.Select(x => x.O.Name).GroupBy(n => n).OrderByDescending(n => n.Count()).Take(3).Select(n => n.Key));
                Console.WriteLine($"{g.Key} | {g.Count()} | {g.Select(x => x.Area).Distinct().Count()} | {health} | {contact} | "
                    + (weapons.Count == 0 ? "all 100" : String.Join("; ", weapons)) + $" | {names}");
            }
        }
    }
}
