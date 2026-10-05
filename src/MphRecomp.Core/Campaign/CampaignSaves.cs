using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MphRead;

namespace MphRecomp.Campaign
{
    // One adventure save: MphRead's own StorySave (everything the game itself keeps) plus what the host needs to
    // resume -- who is playing, where the gunship is parked, play time.
    public sealed class CampaignSaveFile
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        public Hunter Hunter { get; set; } = Hunter.Samus;
        // the landing site the ship was boarded from (the star map's starting cursor)
        public Planet Planet { get; set; } = Planet.CelestialArchives;
        public DateTime SavedAtUtc { get; set; }
        public long PlaySeconds { get; set; }
        // an ending has been reached with this file
        public bool Cleared { get; set; }
        // false for a file just created on the file select (vanilla writes it there, at 0%): starting it runs the new-game
        // intro (briefing + story movie) before the first planet select. Saves without the field count as started.
        public bool Started { get; set; } = true;
        // the planets the star map has already shown (StorySave.Areas bits as of the last planet select; vanilla keeps it
        // at e8c50+0x14): a planet found since gets the "scanning ALIMBIC CLUSTER" box. null (saves without the field) =
        // all found planets already shown
        public int? KnownAreas { get; set; }
        // the connectors walked through, for the Prime-style pause map (AreaMapIndex: vanilla's save +0x30 layout, a word
        // per area half). null (saves without the field) = seeded from the visited rooms on first use
        public uint[]? MapConnectors { get; set; }
        public StorySave Story { get; set; } = null!;
    }

    // What a file-select screen shows for one save slot (CampaignSaves.Summarize).
    public sealed class CampaignSlotSummary
    {
        public int Slot { get; init; }
        // a save file is there (it may still fail to read: see Readable / Error)
        public bool Exists { get; init; }
        public bool Readable { get; init; }
        public string? Error { get; init; }
        public Hunter Hunter { get; init; }
        // where the gunship is parked
        public Planet Planet { get; init; }
        public long PlaySeconds { get; init; }
        public DateTime SavedAtUtc { get; init; }
        // an ending was reached with this file
        public bool Cleared { get; init; }
        public bool Started { get; init; } = true;
        // MphRead's StorySave.GetCompletionPercentage (scans, weapons, artifacts, octoliths, expansions), 0-100
        public int CompletionPercent { get; init; }
        public int Octoliths { get; init; }
        public int OctolithTotal => 8;
        // the DS file select's "MISSILE EXPANSION -", "UA EXPANSION -" and "ENERGY TANK -" lines (collected counts; the ROM
        // text shows no total). Derived from the capacities the way MphRead's completion formula does: each pickup adds a
        // fixed amount (PlayerProcess: energy tank +EnergyTank from 99, missile +100 from 50, UA +300 from 400).
        // The ROM places 9 missile expansions, 12 UA expansions and 8 energy tanks (two of them in Gorea's rooms).
        public int MissileExpansions { get; init; }
        public int UaExpansions { get; init; }
        public int EnergyTanks { get; init; }
        // the star map: every planet with unlocked / visited / octoliths / artifacts / area states
        public IReadOnlyList<PlanetStatus> Planets { get; init; } = Array.Empty<PlanetStatus>();
    }

    // Adventure saves as JSON files, campaign<slot>.json in one folder. Written with Utf8JsonWriter and read with
    // JsonDocument, field by field: no reflection, so it works in a trimmed Android build. The "Story" object uses
    // MphRead's own property names and its byte-arrays-as-numbers layout, so it matches the saves desktop MphRead
    // writes (GameState.CommitSave). Writes go to a temp file first, and the previous save is kept as .bak.
    public sealed class CampaignSaves
    {
        public string Directory { get; }

        public CampaignSaves(string directory)
        {
            Directory = directory;
        }

        public string PathFor(int slot) => Path.Combine(Directory, $"campaign{slot}.json");

        public bool Exists(int slot) => File.Exists(PathFor(slot));

        // null when there is no save in the slot; throws if the file is there but unreadable
        public CampaignSaveFile? Load(int slot)
        {
            string path = PathFor(slot);
            if (!File.Exists(path))
            {
                return null;
            }
            return FromJson(File.ReadAllText(path));
        }

        public bool TryLoad(int slot, out CampaignSaveFile? file, out string? error)
        {
            file = null;
            error = null;
            try
            {
                file = Load(slot);
                return file != null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException
                or InvalidOperationException or IOException or FormatException or OverflowException or ArgumentException)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        public void Save(int slot, CampaignSaveFile file)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string path = PathFor(slot);
            string temp = path + ".tmp";
            File.WriteAllText(temp, ToJson(file));
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            File.Move(temp, path, overwrite: true);
        }

        // the file select's "create a new game in this slot?" YES: a fresh file at 0% (not started)
        public CampaignSaveFile CreateNew(int slot)
        {
            var story = new StorySave();
            RepairLogbook(story);
            var file = new CampaignSaveFile
            {
                Hunter = Hunter.Samus,
                Planet = Planet.CelestialArchives,
                SavedAtUtc = DateTime.UtcNow,
                PlaySeconds = 0,
                Started = false,
                KnownAreas = 0,
                Story = story
            };
            Save(slot, file);
            return file;
        }

        // the file select's DELETE: the slot is empty afterwards; the file itself is kept as .bak, like a save's previous
        // version (vanilla erases the block outright)
        public void Delete(int slot)
        {
            string path = PathFor(slot);
            if (File.Exists(path))
            {
                File.Move(path, path + ".bak", overwrite: true);
            }
        }

        // the file select's COPY: the save as it is, into another slot (vanilla copies the save block)
        public void Copy(int from, int to)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string path = PathFor(to);
            string temp = path + ".tmp";
            File.Copy(PathFor(from), temp, overwrite: true);
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            File.Move(temp, path, overwrite: true);
        }

        // One slot for a file-select screen; never throws (a damaged file comes back Exists, !Readable, Error).
        public CampaignSlotSummary Summarize(int slot)
        {
            if (!Exists(slot))
            {
                return new CampaignSlotSummary { Slot = slot };
            }
            if (!TryLoad(slot, out CampaignSaveFile? file, out string? error))
            {
                return new CampaignSlotSummary { Slot = slot, Exists = true, Error = error ?? "the file disappeared" };
            }
            StorySave story = file!.Story;
            return new CampaignSlotSummary
            {
                Slot = slot,
                Exists = true,
                Readable = true,
                Hunter = file.Hunter,
                Planet = file.Planet,
                PlaySeconds = file.PlaySeconds,
                SavedAtUtc = file.SavedAtUtc,
                Cleared = file.Cleared,
                Started = file.Started,
                CompletionPercent = Math.Clamp(story.GetCompletionPercentage(), 0, 100),
                Octoliths = story.CountFoundOctoliths(),
                MissileExpansions = Math.Max(0, (story.AmmoMax[1] - 50) / 100),
                UaExpansions = Math.Max(0, (story.AmmoMax[0] - 400) / 300),
                EnergyTanks = Math.Max(0, story.HealthMax / Metadata.PlayerValues[0].EnergyTank),
                Planets = ShipNavigation.StarMap(story)
            };
        }

        // Slots first .. first+count-1 (the front end's files A/B/C are slots 1-3).
        public IReadOnlyList<CampaignSlotSummary> SummarizeSlots(int first = 1, int count = 3)
        {
            var list = new List<CampaignSlotSummary>(count);
            for (int slot = first; slot < first + count; slot++)
            {
                list.Add(Summarize(slot));
            }
            return list;
        }

        // A detached copy of a story save (the live one belongs to the running scene).
        public static StorySave Clone(StorySave save)
        {
            var copy = new StorySave();
            using JsonDocument doc = JsonDocument.Parse(StoryToJson(save));
            ReadStory(doc.RootElement, copy);
            return copy;
        }

        // ---------------------------------------------------------------- JSON

        private static readonly JsonWriterOptions _writerOptions = new JsonWriterOptions { Indented = true };

        public static string ToJson(CampaignSaveFile file)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, _writerOptions))
            {
                w.WriteStartObject();
                w.WriteNumber("Version", file.Version);
                w.WriteString("Hunter", file.Hunter.ToString());
                w.WriteString("Planet", file.Planet.ToString());
                w.WriteString("SavedAtUtc", file.SavedAtUtc);
                w.WriteNumber("PlaySeconds", file.PlaySeconds);
                w.WriteBoolean("Cleared", file.Cleared);
                w.WriteBoolean("Started", file.Started);
                if (file.KnownAreas is int known) w.WriteNumber("KnownAreas", known);
                if (file.MapConnectors is uint[] connectors)
                {
                    w.WriteStartArray("MapConnectors");
                    foreach (uint word in connectors) w.WriteNumberValue(word);
                    w.WriteEndArray();
                }
                w.WritePropertyName("Story");
                WriteStory(w, file.Story);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public static CampaignSaveFile FromJson(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            int version = root.GetProperty("Version").GetInt32();
            if (version < 1 || version > CampaignSaveFile.CurrentVersion)
            {
                throw new InvalidDataException($"unsupported campaign save version {version}");
            }
            var story = new StorySave();
            ReadStory(root.GetProperty("Story"), story);
            RepairLogbook(story);
            return new CampaignSaveFile
            {
                Version = version,
                Hunter = Enum.Parse<Hunter>(root.GetProperty("Hunter").GetString()!),
                Planet = Enum.Parse<Planet>(root.GetProperty("Planet").GetString()!),
                SavedAtUtc = root.GetProperty("SavedAtUtc").GetDateTime(),
                PlaySeconds = root.GetProperty("PlaySeconds").GetInt64(),
                Cleared = root.GetProperty("Cleared").GetBoolean(),
                Started = !root.TryGetProperty("Started", out JsonElement started) || started.GetBoolean(),
                KnownAreas = root.TryGetProperty("KnownAreas", out JsonElement known) ? known.GetInt32() : null,
                MapConnectors = root.TryGetProperty("MapConnectors", out JsonElement cons) && cons.ValueKind == JsonValueKind.Array
                    ? cons.EnumerateArray().Select(e => e.GetUInt32()).ToArray() : null,
                Story = story
            };
        }

        // Saves made with MphRead's old new-game setup (fixed 2026-10-01) carry the starting equipment by table position
        // -- bits 0-6, 26, 28 -- where scanning marks scan ids = record numbers. That exact pattern (bit 0 is never a
        // scan: there's no record L000) is moved to SCAN VISOR..MORPH BALL BOMB (1-7), JUMP BOOTS (27), CHARGE SHOT (29);
        // 26 / 28 (PORTAL / OMEGA CANNON, never meant) are cleared, and the scan / equipment counts are recounted.
        public static void RepairLogbook(StorySave story)
        {
            foreach (int id in new[] { 0, 1, 2, 3, 4, 5, 6, 26, 28 })
            {
                if (!story.CheckLogbook(id)) return;
            }
            void Clear(int id) => story.Logbook[id / 8] &= (byte)~(1 << (id % 8));
            void Set(int id) => story.Logbook[id / 8] |= (byte)(1 << (id % 8));
            Clear(0);
            Clear(26);
            Clear(28);
            foreach (int id in new[] { 1, 2, 3, 4, 5, 6, 7, 27, 29 })
            {
                Set(id);
            }
            int scans = 0, equipment = 0;
            foreach (var entry in MphRead.Text.Strings.ReadStringTable(MphRead.Text.StringTables.ScanLog))
            {
                if (!story.CheckLogbook(entry)) continue;
                int category = MphRead.Text.Strings.GetScanEntryCategory(Int32.Parse(entry.Id[1..]));
                if (category < 3) scans++;
                else if (category == 3) equipment++;
            }
            story.ScanCount = scans;
            story.EquipmentCount = equipment;
        }

        public static string StoryToJson(StorySave save)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, _writerOptions))
            {
                WriteStory(w, save);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteStory(Utf8JsonWriter w, StorySave s)
        {
            w.WriteStartObject();
            w.WritePropertyName("RoomState");
            w.WriteStartArray();
            foreach (byte[] room in s.RoomState)
            {
                WriteBytes(w, null, room);
            }
            w.WriteEndArray();
            WriteBytes(w, "VisitedRooms", s.VisitedRooms);
            WriteBytes(w, "TriggerState", s.TriggerState);
            WriteBytes(w, "Logbook", s.Logbook);
            w.WritePropertyName("EnemyEncounters");
            w.WriteStartArray();
            foreach (byte[] area in s.EnemyEncounters)
            {
                WriteBytes(w, null, area);
            }
            w.WriteEndArray();
            w.WriteNumber("ScanCount", s.ScanCount);
            w.WriteNumber("EquipmentCount", s.EquipmentCount);
            w.WriteNumber("CheckpointEntityId", s.CheckpointEntityId);
            w.WriteNumber("CheckpointRoomId", s.CheckpointRoomId);
            w.WriteNumber("Health", s.Health);
            w.WriteNumber("HealthMax", s.HealthMax);
            WriteInts(w, "Ammo", s.Ammo);
            WriteInts(w, "AmmoMax", s.AmmoMax);
            WriteInts(w, "WeaponSlots", s.WeaponSlots);
            w.WriteNumber("Weapons", s.Weapons);
            w.WriteNumber("Artifacts", s.Artifacts);
            w.WriteNumber("FoundOctoliths", s.FoundOctoliths);
            w.WriteNumber("CurrentOctoliths", s.CurrentOctoliths);
            w.WriteNumber("LostOctoliths", s.LostOctoliths);
            w.WriteNumber("Areas", s.Areas);
            w.WriteNumber("BossFlags", (int)s.BossFlags);
            WriteBytes(w, "AreaHunters", s.AreaHunters);
            w.WriteNumber("DefeatedHunters", s.DefeatedHunters);
            w.WritePropertyName("Stats");
            w.WriteStartObject();
            w.WriteNumber("HunterKills", s.Stats.HunterKills);
            w.WriteNumber("Deaths", s.Stats.Deaths);
            w.WriteNumber("EnemyHunterDeaths", s.Stats.EnemyHunterDeaths);
            w.WriteNumber("EnemyKills", s.Stats.EnemyKills);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        private static void WriteBytes(Utf8JsonWriter w, string? name, byte[] values)
        {
            if (name != null)
            {
                w.WritePropertyName(name);
            }
            w.WriteStartArray();
            foreach (byte value in values)
            {
                w.WriteNumberValue(value);
            }
            w.WriteEndArray();
        }

        private static void WriteInts(Utf8JsonWriter w, string name, int[] values)
        {
            w.WritePropertyName(name);
            w.WriteStartArray();
            foreach (int value in values)
            {
                w.WriteNumberValue(value);
            }
            w.WriteEndArray();
        }

        // Fills a fresh StorySave (its constructor sizes every array). Arrays are copied element by element and
        // must have the constructor's length.
        private static void ReadStory(JsonElement e, StorySave s)
        {
            JsonElement rooms = e.GetProperty("RoomState");
            CheckLength("RoomState", rooms, s.RoomState.Length);
            int i = 0;
            foreach (JsonElement room in rooms.EnumerateArray())
            {
                ReadBytes($"RoomState[{i}]", room, s.RoomState[i]);
                i++;
            }
            ReadBytes("VisitedRooms", e.GetProperty("VisitedRooms"), s.VisitedRooms);
            ReadBytes("TriggerState", e.GetProperty("TriggerState"), s.TriggerState);
            ReadBytes("Logbook", e.GetProperty("Logbook"), s.Logbook);
            JsonElement encounters = e.GetProperty("EnemyEncounters");
            CheckLength("EnemyEncounters", encounters, s.EnemyEncounters.Length);
            i = 0;
            foreach (JsonElement area in encounters.EnumerateArray())
            {
                ReadBytes($"EnemyEncounters[{i}]", area, s.EnemyEncounters[i]);
                i++;
            }
            s.ScanCount = e.GetProperty("ScanCount").GetInt32();
            s.EquipmentCount = e.GetProperty("EquipmentCount").GetInt32();
            s.CheckpointEntityId = e.GetProperty("CheckpointEntityId").GetInt32();
            s.CheckpointRoomId = e.GetProperty("CheckpointRoomId").GetInt32();
            s.Health = e.GetProperty("Health").GetInt32();
            s.HealthMax = e.GetProperty("HealthMax").GetInt32();
            ReadInts("Ammo", e.GetProperty("Ammo"), s.Ammo);
            ReadInts("AmmoMax", e.GetProperty("AmmoMax"), s.AmmoMax);
            ReadInts("WeaponSlots", e.GetProperty("WeaponSlots"), s.WeaponSlots);
            s.Weapons = e.GetProperty("Weapons").GetUInt16();
            s.Artifacts = e.GetProperty("Artifacts").GetUInt32();
            s.FoundOctoliths = e.GetProperty("FoundOctoliths").GetUInt16();
            s.CurrentOctoliths = e.GetProperty("CurrentOctoliths").GetUInt16();
            s.LostOctoliths = e.GetProperty("LostOctoliths").GetUInt32();
            s.Areas = e.GetProperty("Areas").GetUInt16();
            s.BossFlags = (BossFlags)e.GetProperty("BossFlags").GetInt32();
            ReadBytes("AreaHunters", e.GetProperty("AreaHunters"), s.AreaHunters);
            s.DefeatedHunters = e.GetProperty("DefeatedHunters").GetByte();
            JsonElement stats = e.GetProperty("Stats");
            s.Stats.HunterKills = stats.GetProperty("HunterKills").GetUInt32();
            s.Stats.Deaths = stats.GetProperty("Deaths").GetUInt32();
            s.Stats.EnemyHunterDeaths = stats.GetProperty("EnemyHunterDeaths").GetUInt32();
            s.Stats.EnemyKills = stats.GetProperty("EnemyKills").GetUInt32();
        }

        private static void CheckLength(string name, JsonElement array, int length)
        {
            if (array.GetArrayLength() != length)
            {
                throw new InvalidDataException($"{name}: expected {length} entries, found {array.GetArrayLength()}");
            }
        }

        private static void ReadBytes(string name, JsonElement array, byte[] target)
        {
            CheckLength(name, array, target.Length);
            int i = 0;
            foreach (JsonElement value in array.EnumerateArray())
            {
                // MphRead writes bytes through a short[] converter
                target[i++] = checked((byte)value.GetInt16());
            }
        }

        private static void ReadInts(string name, JsonElement array, int[] target)
        {
            CheckLength(name, array, target.Length);
            int i = 0;
            foreach (JsonElement value in array.EnumerateArray())
            {
                target[i++] = value.GetInt32();
            }
        }
    }
}
