using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;

namespace MphRecomp.Campaign
{
    // The enemies Hard Mode can place (docs/ENEMY_IMPORT.md section 5, phase H1): MPH's own story enemies and First Hunt's.
    // Each entry is a template: the first spawner of that type found in the imported game's own entity files, read at
    // run time (nothing from the ROM is in the code). A placed enemy copies its template's per-type settings (patrol
    // volumes, versions, subtypes; all relative to the spawner) and gets its own position, counts and trigger.
    // Section 8.6: which weapons and alt forms hurt each enemy, and which of those the player owns on the normal route.
    public static class EnemyCatalogue
    {
        public sealed class Entry
        {
            public string Id { get; init; } = "";     // "mph:Zoomer", "fh:Metroid"
            public string Label { get; init; } = "";
            public bool FirstHunt { get; init; }
            public EnemyType Type { get; init; }       // MPH only
            public FhEnemyType FhType { get; init; }   // First Hunt only
            public EnemySpawnEntityData Mph { get; init; }
            public FhEnemySpawnEntityData Fh { get; init; }
            public string SourceRoom { get; init; } = "";
        }

        // what H1 can place, in the D-pad's order (bosses and their parts, Slench, Gorea and the spawner/lock helpers
        // are left out: they need their rooms)
        public static readonly EnemyType[] PlaceableMph =
        {
            EnemyType.WarWasp, EnemyType.Zoomer, EnemyType.Temroid, EnemyType.Petrasyl1, EnemyType.Petrasyl2,
            EnemyType.Petrasyl3, EnemyType.Petrasyl4, EnemyType.BarbedWarWasp, EnemyType.Shriekbat, EnemyType.Geemer,
            EnemyType.Blastcap, EnemyType.AlimbicTurret, EnemyType.PsychoBit1, EnemyType.Trocra, EnemyType.Voldrum1,
            EnemyType.Voldrum2, EnemyType.Quadtroid, EnemyType.CrashPillar, EnemyType.FireSpawn, EnemyType.LesserIthrak,
            EnemyType.GreaterIthrak, EnemyType.CarnivorousPlant,
        };

        public static readonly FhEnemyType[] PlaceableFh =
        {
            FhEnemyType.WarWasp, FhEnemyType.Zoomer, FhEnemyType.Metroid, FhEnemyType.Mochtroid1, FhEnemyType.Mochtroid2,
            FhEnemyType.Mochtroid3, FhEnemyType.Mochtroid4,
        };

        static List<Entry>? _entries;
        public static string? LastError { get; private set; }

        public static IReadOnlyList<Entry> Entries => _entries ??= Build();

        public static Entry? Find(string? id) => id == null ? null : Entries.FirstOrDefault(e => e.Id == id);

        // forget the templates (a test after an import change)
        public static void Reset() => _entries = null;

        public static string SplitWords(string s)
        {
            var b = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && (Char.IsUpper(c) && !Char.IsUpper(s[i - 1]) || Char.IsDigit(c) && !Char.IsDigit(s[i - 1])))
                {
                    b.Append(' ');
                }
                b.Append(c);
            }
            return b.ToString();
        }

        static List<Entry> Build()
        {
            var mph = new Dictionary<EnemyType, Entry>();
            var fh = new Dictionary<FhEnemyType, Entry>();
            foreach (RoomMetadata meta in Metadata.RoomList)
            {
                if (meta.Multiplayer || meta.EntityPath == null)
                {
                    continue;
                }
                IReadOnlyList<Entity> entities;
                try
                {
                    entities = Read.GetEntities(meta.EntityPath, layerId: -1, meta.FirstHunt);
                }
                catch (Exception ex)
                {
                    // a game not imported (First Hunt is optional) or a file missing: that room gives no templates
                    LastError = meta.Name + ": " + ex.Message;
                    continue;
                }
                foreach (Entity entity in entities)
                {
                    if (!meta.FirstHunt && entity is Entity<EnemySpawnEntityData> m && Array.IndexOf(PlaceableMph, m.Data.EnemyType) >= 0
                        && !mph.ContainsKey(m.Data.EnemyType))
                    {
                        mph[m.Data.EnemyType] = new Entry
                        {
                            Id = "mph:" + m.Data.EnemyType, Label = SplitWords(m.Data.EnemyType.ToString()),
                            Type = m.Data.EnemyType, Mph = m.Data, SourceRoom = meta.Name,
                        };
                    }
                    else if (meta.FirstHunt && entity is Entity<FhEnemySpawnEntityData> f && Array.IndexOf(PlaceableFh, f.Data.EnemyType) >= 0
                        && !fh.ContainsKey(f.Data.EnemyType))
                    {
                        fh[f.Data.EnemyType] = new Entry
                        {
                            Id = "fh:" + f.Data.EnemyType, Label = "FH " + SplitWords(f.Data.EnemyType.ToString()),
                            FirstHunt = true, FhType = f.Data.EnemyType, Fh = f.Data, SourceRoom = meta.Name,
                        };
                    }
                }
            }
            var list = new List<Entry>();
            foreach (EnemyType t in PlaceableMph)
            {
                if (mph.TryGetValue(t, out Entry? e)) list.Add(e);
            }
            foreach (FhEnemyType t in PlaceableFh)
            {
                if (fh.TryGetValue(t, out Entry? e)) list.Add(e);
            }
            return list;
        }

        // ---- section 8.6: what hurts it ----

        // the beams in BeamType order (Omega is left out of every check: it never counts)
        public static readonly string[] BeamNames = { "POWER", "VOLT", "MISSILE", "BATTLEHAMMER", "IMPERIALIST", "JUDICATOR", "MAGMAUL", "SHOCK COIL" };

        public readonly record struct Vulnerability(bool[] Beams, bool Bomb, bool Contact);

        // from the type's effectiveness table; a live instance's flags (Invincible / NoBombDamage, which also stops the
        // contact alt forms, PlayerCollision) when there is one. First Hunt's enemies have no weaknesses: all hurt.
        public static Vulnerability VulnerabilityOf(Entry entry, EnemyInstanceEntity? live = null)
        {
            var beams = new bool[8];
            if (entry.FirstHunt)
            {
                Array.Fill(beams, true);
                return new Vulnerability(beams, Bomb: true, Contact: true);
            }
            var eff = new Effectiveness[9];
            Metadata.LoadEffectiveness(entry.Type, eff);
            for (int i = 0; i < 8; i++)
            {
                beams[i] = eff[i] != Effectiveness.Zero;
            }
            bool alt = live == null || !live.Flags.TestAny(EnemyFlags.Invincible | EnemyFlags.NoBombDamage);
            return new Vulnerability(beams, alt, alt);
        }

        // the alt form's family: bombs (Samus, Kanden, Sylux, Weavel) or contact (Trace, Noxus, Spire)
        public static bool AltIsContact(Hunter h) => h is Hunter.Trace or Hunter.Noxus or Hunter.Spire;

        public static BeamType Affinity(Hunter h) => h switch
        {
            Hunter.Kanden => BeamType.VoltDriver,
            Hunter.Trace => BeamType.Imperialist,
            Hunter.Sylux => BeamType.ShockCoil,
            Hunter.Noxus => BeamType.Judicator,
            Hunter.Spire => BeamType.Magmaul,
            Hunter.Weavel => BeamType.Battlehammer,
            _ => BeamType.None,
        };

        // the normal route, by area (Metadata.GetAreaInfo: 0/1 Alinos 1/2, 2/3 Celestial Archives 1/2, 4/5 Vesper 1/2,
        // 6/7 Arcterra 1/2, 8 Oubliette). An assumption to check (owner): settable.
        public static int[] RouteAreas { get; set; } = { 2, 0, 3, 1, 4, 6, 5, 7, 8 };

        static Dictionary<int, HashSet<BeamType>>? _areaWeapons;

        static BeamType BeamOf(ItemType t) => t switch
        {
            ItemType.VoltDriver => BeamType.VoltDriver,
            ItemType.Battlehammer => BeamType.Battlehammer,
            ItemType.Imperialist => BeamType.Imperialist,
            ItemType.Judicator => BeamType.Judicator,
            ItemType.Magmaul => BeamType.Magmaul,
            ItemType.ShockCoil => BeamType.ShockCoil,
            _ => BeamType.None,
        };

        // the weapons each area's item spawners give (story rooms only)
        static Dictionary<int, HashSet<BeamType>> AreaWeapons()
        {
            if (_areaWeapons != null) return _areaWeapons;
            var map = new Dictionary<int, HashSet<BeamType>>();
            foreach (RoomMetadata meta in Metadata.RoomList)
            {
                if (meta.Multiplayer || meta.FirstHunt || meta.EntityPath == null || meta.Id < 27)
                {
                    continue;
                }
                IReadOnlyList<Entity> entities;
                try
                {
                    entities = Read.GetEntities(meta.EntityPath, layerId: -1, firstHunt: false);
                }
                catch
                {
                    continue;
                }
                int area = Metadata.GetAreaInfo(meta.Id);
                foreach (Entity entity in entities)
                {
                    if (entity is Entity<ItemSpawnEntityData> item && BeamOf(item.Data.ItemType) is BeamType b && b != BeamType.None)
                    {
                        if (!map.TryGetValue(area, out HashSet<BeamType>? set)) map[area] = set = new HashSet<BeamType>();
                        set.Add(b);
                    }
                }
            }
            return _areaWeapons = map;
        }

        // what the player owns entering a room on the normal route: the Power Beam, missiles, the hunter's affinity weapon,
        // and the weapons of every area before this one on the route (this area's own are not counted: they may lie past
        // the room)
        public static HashSet<BeamType> OwnedAt(int roomId, Hunter hunter)
        {
            var owned = new HashSet<BeamType> { BeamType.PowerBeam, BeamType.Missile };
            if (Affinity(hunter) is BeamType a && a != BeamType.None) owned.Add(a);
            int area = Metadata.GetAreaInfo(roomId);
            Dictionary<int, HashSet<BeamType>> weapons = AreaWeapons();
            foreach (int earlier in RouteAreas)
            {
                if (earlier == area) break;
                if (weapons.TryGetValue(earlier, out HashSet<BeamType>? set)) owned.UnionWith(set);
            }
            return owned;
        }

        // the placer's line: "HURT BY: POWER* MISSILE* MAGMAUL BOMB*" (* = owned on the route), then a warning when
        // nothing owned hurts it, or only the alt form does
        public static (string Line, string? Warning) Describe(Entry entry, int roomId, Hunter hunter, EnemyInstanceEntity? live = null)
        {
            Vulnerability v = VulnerabilityOf(entry, live);
            HashSet<BeamType> owned = OwnedAt(roomId, hunter);
            var parts = new List<string>();
            bool ownedBeam = false;
            for (int i = 0; i < 8; i++)
            {
                if (!v.Beams[i]) continue;
                bool own = owned.Contains((BeamType)i);
                ownedBeam |= own;
                parts.Add(BeamNames[i] + (own ? "*" : ""));
            }
            bool contact = AltIsContact(hunter);
            bool altHurts = contact ? v.Contact : v.Bomb;
            if (v.Bomb) parts.Add("BOMB" + (!contact ? "*" : ""));
            if (v.Contact) parts.Add("CONTACT" + (contact ? "*" : ""));
            string line = "HURT BY: " + (parts.Count > 0 ? String.Join(" ", parts) : "NOTHING");
            string? warning = null;
            if (!ownedBeam && !altHurts) warning = "WARNING: NOTHING " + hunter.ToString().ToUpperInvariant() + " OWNS HERE HURTS IT";
            else if (!ownedBeam) warning = "WARNING: ONLY THE ALT FORM HURTS IT HERE";
            return (line, warning);
        }
    }
}
