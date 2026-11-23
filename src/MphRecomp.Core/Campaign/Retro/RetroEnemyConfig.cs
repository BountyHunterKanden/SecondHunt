using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MphRecomp.Import.Enemies;
using MphRecomp.Import.Retro;

namespace MphRecomp.Campaign.Retro
{
    // An imported Retro enemy's numbers (docs/ENEMY_IMPORT.md E1): <root>/enemies/<Name>/enemy.cfg as written by
    // Import/Enemies/EnemyImportKind.cs. Every leaf property of the disc's reference instance is a "p <id path> <hex>"
    // line (big-endian, as on the disc; ids only), plus type / area / instance / fsm / gib header lines. The state
    // machine (state.fsm2) sits beside it.
    public sealed class RetroEnemyConfig
    {
        public string Dir = "", Type = "";
        public uint Area, Instance, FsmId, Gib;
        public Fsm? Fsm;
        readonly Dictionary<string, byte[]> _props = new(StringComparer.Ordinal);

        public static bool Exists(string dir) => File.Exists(Path.Combine(dir, EnemyImportKind.ConfigFile));

        public static RetroEnemyConfig Load(string dir)
        {
            var c = new RetroEnemyConfig { Dir = dir };
            foreach (string raw in File.ReadAllLines(Path.Combine(dir, EnemyImportKind.ConfigFile)))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 2) continue;
                switch (f[0])
                {
                case "type": c.Type = f[1]; break;
                case "area": c.Area = Hex(f[1]); break;
                case "instance": c.Instance = Hex(f[1]); break;
                case "fsm": c.FsmId = Hex(f[1]); break;
                case "gib": c.Gib = Hex(f[1]); break;
                case "p": c._props[f[1]] = f.Length > 2 ? Convert.FromHexString(f[2]) : Array.Empty<byte>(); break;
                }
            }
            string fsm = Path.Combine(dir, EnemyImportKind.FsmFile);
            if (File.Exists(fsm)) c.Fsm = Fsm.Load(File.ReadAllBytes(fsm));
            return c;
        }

        static uint Hex(string s) => UInt32.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        public bool Has(string path) => _props.ContainsKey(path);

        public float F32(string path, float fallback) =>
            _props.TryGetValue(path, out byte[]? b) && b.Length == 4 ? BinaryPrimitives.ReadSingleBigEndian(b) : fallback;

        public uint U32(string path, uint fallback) =>
            _props.TryGetValue(path, out byte[]? b) && b.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(b) : fallback;

        public int I32(string path, int fallback) => (int)U32(path, (uint)fallback);

        public int Count => _props.Count;
    }
}
