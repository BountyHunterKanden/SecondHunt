using System;
using System.Collections.Generic;
using System.Globalization;
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
    // With the script templates (Mp1ScriptTemplates.cs; templates=<MP1 dir>, default brawl_extract/prime/
    // script-templates/MP1):
    //   -mp1scly dump <area id hex> [type=<name|0xNN>] [disc=]: every script object of one room: layer, type name,
    //     id, every named property (position/rotation/scale first) and its connections
    //   -mp1scly check [disc=]: per object type, how many objects the templates read to the exact end
    //   -mp1scly rooms [disc=]: per world pak and room, the enemy types (objects with a PatternedAI block): count,
    //     health, layers, instance names (the owner's pick list, docs/ENEMY_IMPORT.md 11.6)
    //   -mp1scly areas [disc=]: every room id with its pak, object count and enemy count
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
            var rooms = new List<Room>();
            int areas = 0, failed = 0;
            foreach (string path in src.Paths.Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
            {
                Pak pk;
                try { pk = new Pak(src.ReadFile(path), path); } catch (InvalidDataException) { continue; }
                Dictionary<uint, string>? names = null;
                string? world = null;
                foreach (uint rid in pk.Order)
                {
                    if (pk.TypeOf(rid) != "MREA" || !seen.Add(rid)) continue;
                    byte[] raw = pk.Get(rid);
                    if (!Scly1.IsPrime1Area(raw)) continue;
                    areas++;
                    try
                    {
                        names ??= RoomNames(pk);
                        world ??= WorldName(pk);
                        var room = new Room(Path.GetFileName(path), world, rid, names.TryGetValue(rid, out string? nm) ? nm : "", Scly1.ReadScripts(raw));
                        rooms.Add(room);
                        foreach (Scly1.Obj o in room.Objects) objs.Add((o, Scly1.FindPatterned(o)));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
                    { failed++; Console.WriteLine($"{path} {rid:x8}: {ex.Message}"); }
                }
            }
            Console.WriteLine($"{Path.GetFileName(disc)}: {areas} areas, {failed} failed, {objs.Count} objects");
            if (args.Contains("dump") || args.Contains("check") || args.Contains("rooms") || args.Contains("areas"))
            {
                Mp1ScriptTemplates? t = Mp1ScriptTemplates.TryLoad(Arg(args, "templates"));
                if (t == null) Console.WriteLine($"no script templates at {Arg(args, "templates") ?? Mp1ScriptTemplates.DefaultDir}: type ids only");
                else Console.WriteLine($"templates: {t.Scripts.Count} object types from {t.Dir}");
                if (args.Contains("dump")) Dump(args, rooms, t);
                else if (args.Contains("check")) Check(objs.Select(x => x.O).ToList(), t);
                else if (args.Contains("rooms")) Rooms(rooms, t);
                else
                    foreach (Room r in rooms)
                        Console.WriteLine($"{r.Pak} {r.Id:x8} {r.Name} | {r.Objects.Count} objects | {r.Objects.Count(o => Scly1.FindPatterned(o) != null)} enemies");
                return;
            }
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

        sealed record Room(string Pak, string World, uint Id, string Name, List<Scly1.Obj> Objects);

        // The MLVL (version 0x11) holds the world name STRG id right after the magic and version.
        static string WorldName(Pak pk)
        {
            uint mlvl = pk.Order.FirstOrDefault(r => pk.TypeOf(r) == "MLVL");
            if (mlvl == 0 && !pk.Contains(0)) return "";
            byte[] w = pk.Get(mlvl);
            uint strg = w.Length >= 12 ? Be.U32(w, 8) : UInt32.MaxValue;
            return pk.Contains(strg) && pk.TypeOf(strg) == "STRG" && FirstString(pk.Get(strg)) is string s ? s : "";
        }

        // Room names for the pak's MREAs: the world MLVL's area entry holds the name STRG id 76 bytes (the u32 name,
        // a 3x4 transform and a 6-float box) before the MREA id; the STRG's first language holds the text.
        static Dictionary<uint, string> RoomNames(Pak pk)
        {
            var names = new Dictionary<uint, string>();
            uint mlvl = pk.Order.FirstOrDefault(r => pk.TypeOf(r) == "MLVL");
            if (mlvl == 0 && !pk.Contains(0)) return names;
            byte[] w = pk.Get(mlvl);
            Span<byte> needle = stackalloc byte[4];
            foreach (uint rid in pk.Order.Where(r => pk.TypeOf(r) == "MREA"))
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(needle, rid);
                int at = Be.IndexOf(w, needle, 0x18);
                if (at < 76) continue;
                uint strg = Be.U32(w, at - 76);
                if (pk.Contains(strg) && pk.TypeOf(strg) == "STRG" && FirstString(pk.Get(strg)) is string s) names[rid] = s;
            }
            return names;
        }

        // Prime 1 STRG (version 0): magic 87654321, version, language count, string count, (fourcc, offset) per
        // language, then per language a u32 size and u32 string offsets from the table start; UTF-16BE strings.
        static string? FirstString(byte[] s)
        {
            if (s.Length < 0x20 || Be.U32(s, 0) != 0x87654321 || Be.U32(s, 4) != 0 || Be.U32(s, 0xC) == 0) return null;
            int langs = Be.I32(s, 8), table = 0x10 + langs * 8 + Be.I32(s, 0x14) + 4;
            int o = table + Be.I32(s, table), end = o;
            while (end + 1 < s.Length && (s[end] | s[end + 1]) != 0) end += 2;
            return System.Text.Encoding.BigEndianUnicode.GetString(s, o, end - o);
        }

        static string TypeName(Mp1ScriptTemplates? t, byte type) => t?.TypeName(type) ?? $"0x{type:X2}";

        static void Dump(string[] args, List<Room> rooms, Mp1ScriptTemplates? t)
        {
            int at = Array.IndexOf(args, "dump");
            string want = at + 1 < args.Length ? args[at + 1] : "";
            if (want.StartsWith("0x")) want = want[2..];
            if (!UInt32.TryParse(want, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id))
            { Console.WriteLine("usage: -mp1scly dump <area id hex> [type=<name|0xNN>]"); return; }
            Room? room = rooms.FirstOrDefault(r => r.Id == id);
            if (room == null) { Console.WriteLine($"no area {id:x8}"); return; }
            string? type = Arg(args, "type");
            Console.WriteLine($"{room.Pak} {room.Id:x8} {room.Name}: {room.Objects.Count} objects in " +
                $"{room.Objects.Select(o => o.Layer).Distinct().Count()} layers");
            int bad = 0;
            foreach (Scly1.Obj o in room.Objects)
            {
                string tn = TypeName(t, o.Type);
                if (type != null && !String.Equals(type, tn, StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(type, $"0x{o.Type:X2}", StringComparison.OrdinalIgnoreCase)) continue;
                Scly1.Patterned? pat = Scly1.FindPatterned(o);
                Console.WriteLine($"\n[layer {o.Layer}] 0x{o.Type:X2} {tn} id {o.Id:x8} \"{o.Name}\" ({o.PropCount} properties, {o.Props.Length} bytes)" +
                    (pat != null ? $" ENEMY health {pat.Health:0.##}" : ""));
                if (t != null)
                {
                    var (rows, ok, err) = t.Read(o);
                    if (!ok) { bad++; Console.WriteLine($"    !! template mismatch: {err}"); }
                    foreach (var (k, v) in rows.Skip(1)) Console.WriteLine($"    {k} = {v}");
                }
                foreach (var c in o.Connections)
                    Console.WriteLine($"    -> on {t?.State(c.State) ?? $"0x{c.State:X}"} send {t?.Message(c.Message) ?? $"0x{c.Message:X}"} to {c.Target:x8}" +
                        (room.Objects.FirstOrDefault(x => x.Id == c.Target) is Scly1.Obj tg ? $" ({TypeName(t, tg.Type)} \"{tg.Name}\")" : ""));
            }
            if (t != null) Console.WriteLine($"\n{bad} template mismatches");
        }

        static void Check(List<Scly1.Obj> objs, Mp1ScriptTemplates? t)
        {
            if (t == null) return;
            int okAll = 0;
            Console.WriteLine("type | name | objects | read to the end | first error");
            foreach (var g in objs.GroupBy(o => o.Type).OrderBy(g => g.Key))
            {
                var res = g.Select(o => t.Read(o)).ToList();
                int ok = res.Count(r => r.Ok);
                okAll += ok;
                Console.WriteLine($"0x{g.Key:X2} | {t.TypeName(g.Key)} | {g.Count()} | {ok} | {res.FirstOrDefault(r => !r.Ok).Error}");
            }
            Console.WriteLine($"{okAll} of {objs.Count} objects read to the exact end");
        }

        static void Rooms(List<Room> rooms, Mp1ScriptTemplates? t)
        {
            foreach (var world in rooms.GroupBy(r => r.Pak))
            {
                var wpats = world.SelectMany(r => r.Objects).Where(o => Scly1.FindPatterned(o) != null).ToList();
                Console.WriteLine($"\n## {world.First().World} ({world.Key}): {world.Count()} rooms, {wpats.Count} enemy objects, " +
                    $"{wpats.Select(o => o.Type).Distinct().Count()} enemy types");
                foreach (Room r in world)
                {
                    var pats = r.Objects.Select(o => (O: o, P: Scly1.FindPatterned(o))).Where(x => x.P != null).ToList();
                    if (pats.Count == 0) continue;
                    Console.WriteLine($"{r.Id:x8} {r.Name}".TrimEnd() + ": " + String.Join("; ", pats.GroupBy(x => x.O.Type)
                        .OrderByDescending(g => g.Count()).Select(g => $"{TypeName(t, g.Key)} x{g.Count()} " +
                            $"(hp {Spread(g, x => x.P!.Health.ToString("0.##"), 2)}; layers " +
                            String.Join(",", g.Select(x => x.O.Layer).Distinct().OrderBy(l => l)) + "; " +
                            String.Join(", ", g.Select(x => x.O.Name).Distinct().Take(3)) + ")")));
                }
            }
            // Per enemy type over the whole disc: the list the owner picks from.
            var all = rooms.SelectMany(r => r.Objects.Select(o => (R: r, O: o, P: Scly1.FindPatterned(o)))).Where(x => x.P != null).ToList();
            Console.WriteLine("\n## Enemy types\ntype | name | objects | rooms | worlds | health | instance names");
            foreach (var g in all.GroupBy(x => x.O.Type).OrderByDescending(g => g.Count()))
                Console.WriteLine($"0x{g.Key:X2} | {TypeName(t, g.Key)} | {g.Count()} | {g.Select(x => x.R.Id).Distinct().Count()} | " +
                    String.Join(", ", g.Select(x => x.R.World).Distinct()) + $" | {Spread(g, x => x.P!.Health.ToString("0.##"), 3)} | " +
                    String.Join(", ", g.Select(x => x.O.Name).GroupBy(n => n).OrderByDescending(n => n.Count()).Take(3).Select(n => n.Key)));
        }
    }
}
