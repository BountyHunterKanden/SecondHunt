using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Retro;

namespace MphRead
{
    // Retro AI state machines (Prime 1 AFSM, Echoes FSM2; docs/ENEMY_IMPORT.md 11.2 item 1):
    //   -fsmdump extract out=<dir> [disc=<.iso/.rvz>]: every AFSM/FSM2 resource's bytes, one file each
    //   -fsmdump dump [disc=] [id=<hex>]: parse every state machine (Core Retro/Fsm.cs; fails on any unread byte) and
    //     print its states, transitions and conditions
    //   -fsmdump refs [disc=]: which room script objects (by type and name) name each state machine
    internal static class FsmDumpTool
    {
        static readonly string Prime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "brawl_extract", "prime"));

        static string? Arg(string[] args, string key) => args.FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..];

        // (pak path, id, type, name) of every state machine on a disc, first copy of each id
        static IEnumerable<(string Pak, uint Id, string Type, Pak P)> All(IFileSource src)
        {
            var seen = new HashSet<uint>();
            foreach (string path in src.Paths.Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
            {
                Pak pk;
                try { pk = new Pak(src.ReadFile(path), path); } catch (InvalidDataException) { continue; }
                foreach (uint id in pk.Order)
                {
                    string t = pk.TypeOf(id);
                    if ((t == "AFSM" || t == "FSM2") && seen.Add(id)) yield return (path, id, t, pk);
                }
            }
        }

        public static void Run(string[] args)
        {
            string verb = args.Length > 1 ? args[1] : "dump";
            string[] discs = Arg(args, "disc") is string d ? new[] { d }
                : new[] { Path.Combine(Prime, "mp1.iso"), Path.Combine(Prime, "echoes_retail.rvz") };
            if (verb == "extract" && Arg(args, "out") is string outDir)
            {
                foreach (string disc in discs)
                {
                    using IFileSource src = DiscImage.Open(disc);
                    string sub = Path.Combine(outDir, Path.GetFileNameWithoutExtension(disc));
                    Directory.CreateDirectory(sub);
                    int n = 0;
                    foreach ((string pak, uint id, string type, Pak p) in All(src))
                    {
                        string nm = p.Names.TryGetValue(id, out var nn) ? nn.Name : "";
                        File.WriteAllBytes(Path.Combine(sub, $"{id:x8}.{type.ToLowerInvariant()}"), p.Get(id));
                        Console.WriteLine($"{Path.GetFileName(disc)} {pak} {type} {id:x8} {nm}");
                        n++;
                    }
                    Console.WriteLine($"{disc}: {n}");
                }
                return;
            }
            if (verb == "dump") { Dump(discs, Arg(args, "id")); return; }
            if (verb == "refs") { Refs(discs); return; }
            Console.WriteLine("-fsmdump extract out=<dir> [disc=] | dump [disc=] [id=] | refs [disc=]");
        }

        static void Dump(string[] discs, string? only)
        {
            foreach (string disc in discs)
            {
                using IFileSource src = DiscImage.Open(disc);
                int n = 0, bad = 0;
                var conds = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach ((string pak, uint id, string type, Pak p) in All(src))
                {
                    if (only != null && !id.ToString("x8").Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                    n++;
                    Fsm f;
                    try { f = Fsm.Load(p.Get(id)); }
                    catch (Exception e) { bad++; Console.WriteLine($"{type} {id:x8} {pak}: FAILED {e.Message}"); continue; }
                    foreach (string c in f.ConditionNames()) conds[c] = conds.GetValueOrDefault(c) + 1;
                    Console.WriteLine($"{type} {id:x8} {pak}: {f.States.Count} states" + (f.IsFsm2
                        ? $", {f.Transitions.Count} transitions, {f.Actions.Count} actions, {f.Groups.Count} groups" : ""));
                    if (only == null && discs.Length > 1) continue;
                    string L(Fsm.Link l) => (l.Label.Length > 0 ? l.Label + ":" : "") + f.NameOf(l);
                    for (int i = 0; i < f.States.Count; i++)
                    {
                        Fsm.State s = f.States[i];
                        Console.WriteLine($"  [{i}] {s.Name}" + (s.Links.Count > 0 ? " -> " + String.Join(", ", s.Links.Select(L)) : ""));
                        foreach (Fsm.Transition t in s.Transitions)
                            Console.WriteLine($"      {String.Join(" & ", t.Conditions.Select(c => (c.Not ? "!" : "") + c.Name
                                + (c.Arg is > 0.001f and < 1e6f ? $"({c.Arg:0.###})" : "")))} => {f.States[t.Target].Name}");
                    }
                    for (int i = 0; i < f.Transitions.Count; i++)
                    {
                        Fsm.Node t = f.Transitions[i];
                        Console.WriteLine($"  t{i} {(t.Extra != 0 ? "!" : "")}{t.Name}" + (t.Arg is > 0.001f and < 1e6f ? $"({t.Arg:0.###})" : "")
                            + " -> " + String.Join(", ", t.Links.Select(L)));
                    }
                    foreach (Fsm.Node a in f.Actions) Console.WriteLine($"  action {a.Name} -> {String.Join(", ", a.Links.Select(L))}");
                    foreach (Fsm.Node g in f.Groups) Console.WriteLine($"  group {g.Name} -> {String.Join(", ", g.Links.Select(L))}");
                }
                Console.WriteLine($"{Path.GetFileName(disc)}: {n} state machines, {bad} failed, {conds.Count} distinct conditions");
                if (only == null) Console.WriteLine("  " + String.Join(" ", conds.Select(kv => $"{kv.Key}x{kv.Value}")));
            }
        }

        // the room script objects whose properties hold a state machine id: Echoes through Core Mrea (named, typed
        // objects); Prime 1 through a light SCLY walk (u8 type, u32 size, u32 id, connections, then untyped
        // properties that start with the object's name)
        static void Refs(string[] discs)
        {
            foreach (string disc in discs)
            {
                using IFileSource src = DiscImage.Open(disc);
                var fsms = All(src).ToDictionary(x => x.Id, x => x.Type);
                var hits = new SortedDictionary<uint, SortedSet<string>>();
                void Hit(uint id, string what) { if (!hits.TryGetValue(id, out var set)) hits[id] = set = new(StringComparer.Ordinal); set.Add(what); }
                IEnumerable<uint> Ids(byte[] b, int o, int end)
                {
                    for (int i = o; i + 4 <= end; i++) { uint v = Be.U32(b, i); if (fsms.ContainsKey(v)) yield return v; }
                }
                foreach (string path in src.Paths.Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
                {
                    Pak pk;
                    try { pk = new Pak(src.ReadFile(path), path); } catch (InvalidDataException) { continue; }
                    foreach (uint rid in pk.Order)
                    {
                        string t = pk.TypeOf(rid);
                        if (t == "AFSM" || t == "FSM2") continue;
                        byte[] raw = pk.Get(rid);
                        if (t != "MREA") { foreach (uint v in Ids(raw, 0, raw.Length)) Hit(v, $"{t} {rid:x8}"); continue; }
                        if (Be.U32(raw, 4) == Mrea.EchoesVersion)
                        {
                            foreach (Mrea.ScriptObject o in Mrea.Read(raw).ReadScripts(true))
                                foreach (uint v in Ids(o.Root.Data, 0, o.Root.Data.Length)) Hit(v, $"{o.Type} \"{o.Name}\"");
                            continue;
                        }
                        int sc = Be.IndexOf(raw, "SCLY"u8, 0);
                        if (sc < 0) continue;
                        int layers = (int)Be.U32(raw, sc + 8), lo = sc + 12 + 4 * layers;
                        for (int l = 0; l < layers; l++)
                        {
                            int lsize = (int)Be.U32(raw, sc + 12 + 4 * l), q = lo + 1, count = (int)Be.U32(raw, lo + 1);
                            q += 4;
                            for (int k = 0; k < count && q + 5 <= lo + lsize; k++)
                            {
                                byte type = raw[q];
                                int size = (int)Be.U32(raw, q + 1), body = q + 5, conns = (int)Be.U32(raw, body + 4);
                                int props = body + 8 + 12 * conns + 4;
                                string name = props < body + size ? Be.CString(raw, props, out _) : "";
                                foreach (uint v in Ids(raw, body + 4, body + size)) Hit(v, $"0x{type:x2} \"{name}\"");
                                q = body + size;
                            }
                            lo += lsize;
                        }
                    }
                }
                foreach ((uint id, string type) in fsms.OrderBy(kv => kv.Key))
                    Console.WriteLine($"{type} {id:x8}: " + (hits.TryGetValue(id, out var h) ? String.Join("; ", h.Take(8)) + (h.Count > 8 ? $" (+{h.Count - 8})" : "") : "(no reference)"));
            }
        }
    }
}
