using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MphRecomp.Import;
using MphRecomp.Import.Retro;

namespace MphRead
{
    // Names and types Prime 1 script properties (Scly1.Obj) by reading the MIT-licensed
    // retro-script-object-templates XML (PrimeDecomp, (c) Aruki / AxioDL; the PrimeWorldEditor templates) at run
    // time from a folder outside the repo (brawl_extract/prime/script-templates/MP1). Dev-time reference only:
    // nothing from the templates is copied into the repo or ships. Layout rules used (from their format):
    //   - an object's own properties start at its name and run for Obj.PropCount elements;
    //   - a nested struct starts with a u32 property count unless its template says <Atomic>true</Atomic>;
    //   - a struct written with fewer properties than its template lists stops early (later ones are newer).
    internal sealed class Mp1ScriptTemplates
    {
        internal sealed class Prop
        {
            public string Type = "", Name = "";
            public string? Archetype;
            public Node? Inline;                       // struct with its own SubProperties
            public Prop? Item;                         // array item
            public Dictionary<uint, string>? Values;   // inline choice values
        }

        internal sealed class Node
        {
            public string Name = "";
            public bool Atomic;
            public readonly List<Prop> Props = new();
            public Dictionary<uint, string>? Values;   // choice archetype
        }

        public readonly string Dir;
        public readonly Dictionary<byte, Node> Scripts = new();
        readonly Dictionary<string, Node> archetypes = new();
        public readonly Dictionary<uint, string> States = new(), Messages = new();

        public static string DefaultDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "brawl_extract", "prime", "script-templates", "MP1"));

        public static Mp1ScriptTemplates? TryLoad(string? dir)
        {
            dir ??= DefaultDir;
            return File.Exists(Path.Combine(dir, "Game.xml")) ? new Mp1ScriptTemplates(dir) : null;
        }

        static uint Hex(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? UInt32.Parse(s[2..], NumberStyles.HexNumber) : UInt32.Parse(s, CultureInfo.InvariantCulture);

        static IEnumerable<XElement> Entries(XElement game, string section) =>
            game.Element(section)?.Elements("Element") ?? Enumerable.Empty<XElement>();

        Mp1ScriptTemplates(string dir)
        {
            Dir = dir;
            XElement game = XDocument.Load(Path.Combine(dir, "Game.xml")).Root!;
            foreach (XElement e in Entries(game, "PropertyArchetypes"))
            {
                string path = Path.Combine(dir, e.Element("Value")!.Attribute("Path")!.Value);
                if (!File.Exists(path)) continue;
                XElement? a = XDocument.Load(path).Root!.Element("PropertyArchetype");
                if (a != null) archetypes[e.Element("Key")!.Value] = ReadNode(a);
            }
            foreach (XElement e in Entries(game, "ScriptObjects"))
            {
                string path = Path.Combine(dir, e.Element("Value")!.Attribute("Path")!.Value);
                if (!File.Exists(path)) continue;
                XElement? p = XDocument.Load(path).Root!.Element("Properties");
                if (p != null) Scripts[(byte)Hex(e.Element("Key")!.Value)] = ReadNode(p);
            }
            foreach (XElement e in Entries(game, "States")) States[Hex(e.Element("Key")!.Value)] = e.Element("Value")!.Value;
            foreach (XElement e in Entries(game, "Messages")) Messages[Hex(e.Element("Key")!.Value)] = e.Element("Value")!.Value;
        }

        static Dictionary<uint, string>? ReadValues(XElement x) =>
            x.Element("Values")?.Elements("Element").ToDictionary(e => Hex(e.Attribute("ID")!.Value), e => e.Attribute("Name")!.Value);

        static Node ReadNode(XElement x)
        {
            var n = new Node
            {
                Name = x.Element("Name")?.Value ?? "",
                Atomic = String.Equals(x.Element("Atomic")?.Value, "true", StringComparison.OrdinalIgnoreCase),
                Values = ReadValues(x),
            };
            foreach (XElement e in x.Element("SubProperties")?.Elements("Element") ?? Enumerable.Empty<XElement>())
                n.Props.Add(ReadProp(e));
            return n;
        }

        static Prop ReadProp(XElement e)
        {
            var p = new Prop
            {
                Type = e.Attribute("Type")!.Value,
                Name = e.Element("Name")?.Value ?? "",
                Archetype = e.Attribute("Archetype")?.Value,
                Values = ReadValues(e),
            };
            if (e.Element("SubProperties") != null) p.Inline = ReadNode(e);
            if (e.Element("ItemArchetype") is XElement item) p.Item = ReadProp(item);
            if (p.Name.Length == 0) p.Name = p.Archetype ?? p.Type;
            return p;
        }

        public string TypeName(byte type) => Scripts.TryGetValue(type, out Node? n) ? n.Name : $"0x{type:X2}";

        public string State(uint s) => States.TryGetValue(s, out string? n) ? n : $"0x{s:X}";
        public string Message(uint m) => Messages.TryGetValue(m, out string? n) ? n : $"0x{m:X}";

        public bool HasArchetype(byte type, string archetype) =>
            Scripts.TryGetValue(type, out Node? n) && n.Props.Any(p => p.Archetype == archetype);

        // Reads every property of o into (dotted name, value) rows. Ok is false when the walk did not end exactly at
        // the end of the object (a template mismatch: rows past that point are not trustworthy).
        public (List<(string Key, string Value)> Rows, bool Ok, string? Error) Read(Scly1.Obj o)
        {
            var rows = new List<(string, string)>();
            if (!Scripts.TryGetValue(o.Type, out Node? node)) return (rows, false, "no template");
            int pos = 0;
            try
            {
                ReadStruct(o.Props, ref pos, node, o.PropCount, "", rows);
                return (rows, pos == o.Props.Length, pos == o.Props.Length ? null : $"ended at {pos} of {o.Props.Length} bytes");
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or ArgumentException or IndexOutOfRangeException)
            {
                return (rows, false, $"stopped at byte {pos}: {ex.Message}");
            }
        }

        void ReadStruct(byte[] b, ref int pos, Node node, int count, string prefix, List<(string, string)> rows)
        {
            if (count > node.Props.Count) throw new ArgumentException($"{prefix}{node.Name} has {count} properties, template {node.Props.Count}");
            for (int i = 0; i < count; i++) ReadOne(b, ref pos, node.Props[i], prefix + node.Props[i].Name, rows);
        }

        static string F(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        void ReadOne(byte[] b, ref int pos, Prop p, string key, List<(string, string)> rows)
        {
            Node? arch = p.Archetype != null && archetypes.TryGetValue(p.Archetype, out Node? a) ? a : null;
            switch (p.Type)
            {
                case "String": rows.Add((key, "\"" + Be.CString(b, pos, out int end) + "\"")); pos = end + 1; break;   // end = the terminator
                case "Vector": rows.Add((key, $"({F(Be.F32(b, pos))}, {F(Be.F32(b, pos + 4))}, {F(Be.F32(b, pos + 8))})")); pos += 12; break;
                case "Color": rows.Add((key, $"rgba({F(Be.F32(b, pos))}, {F(Be.F32(b, pos + 4))}, {F(Be.F32(b, pos + 8))}, {F(Be.F32(b, pos + 12))})")); pos += 16; break;
                case "Float": rows.Add((key, F(Be.F32(b, pos)))); pos += 4; break;
                case "Int": rows.Add((key, Be.I32(b, pos).ToString(CultureInfo.InvariantCulture))); pos += 4; break;
                case "Sound": rows.Add((key, $"sound {Be.I32(b, pos)}")); pos += 4; break;
                case "Bool": rows.Add((key, b[pos] != 0 ? "true" : "false")); pos += 1; break;
                case "Short": rows.Add((key, Be.I16(b, pos).ToString(CultureInfo.InvariantCulture))); pos += 2; break;
                case "Flags": rows.Add((key, $"0x{Be.U32(b, pos):X}")); pos += 4; break;
                case "Asset":
                {
                    uint id = Be.U32(b, pos); pos += 4;
                    rows.Add((key, id == UInt32.MaxValue ? "none" : $"{id:x8}"));
                    break;
                }
                case "Choice":
                {
                    uint v = Be.U32(b, pos); pos += 4;
                    string? nm = null;
                    if (p.Values?.TryGetValue(v, out nm) != true) arch?.Values?.TryGetValue(v, out nm);
                    rows.Add((key, nm != null ? $"{nm} ({v})" : v.ToString(CultureInfo.InvariantCulture)));
                    break;
                }
                case "AnimationSet":
                    rows.Add((key, $"ANCS {Be.U32(b, pos):x8} char {Be.I32(b, pos + 4)} anim {Be.I32(b, pos + 8)}")); pos += 12; break;
                case "Array":
                {
                    int n = Be.I32(b, pos); pos += 4;
                    if (n < 0 || n > 4096 || p.Item == null) throw new ArgumentException($"{key}: bad array");
                    rows.Add((key, $"[{n}]"));
                    for (int i = 0; i < n; i++) ReadOne(b, ref pos, p.Item, $"{key}[{i}]", rows);
                    break;
                }
                case "Struct":
                {
                    Node node = p.Inline ?? arch ?? throw new ArgumentException($"{key}: no archetype {p.Archetype}");
                    int count;
                    if (node.Atomic) count = node.Props.Count;
                    else { count = Be.I32(b, pos); pos += 4; }
                    if (count < 0) throw new ArgumentException($"{key}: bad property count");
                    ReadStruct(b, ref pos, node, count, key + ".", rows);
                    break;
                }
                default: throw new ArgumentException($"{key}: unknown property type {p.Type}");
            }
        }
    }
}
