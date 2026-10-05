using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // Headless tests for the adventure's ship loop (ShipNavigation + CampaignSaves): the save format, boarding the
    // gunship, the star map's unlocks, landing on every planet from a save, game over -> quit, and the ending.
    //
    //   -campaignship         run everything (save files go to extract_out/campaign/ship_test/)
    internal static class CampaignShipTest
    {
        private static int _pass;
        private static int _fail;

        private static void Check(string name, bool ok)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string outDir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "extract_out", "campaign", "ship_test"));
            Directory.CreateDirectory(outDir);
            var saves = new CampaignSaves(outDir);
            Console.WriteLine($"-- save format (files in {outDir})");
            SaveFormat(saves);
            Console.WriteLine("-- file-select counts from real pickups");
            Pickups(saves);
            Console.WriteLine("-- boarding the gunship at Celestial Gateway");
            StorySave? boarded = BoardShip(saves);
            Console.WriteLine("-- star map unlocks");
            StarMap();
            Console.WriteLine("-- landing on every planet from a save");
            Landings(boarded);
            Console.WriteLine("-- game over -> quit");
            GameOverQuit();
            Console.WriteLine("-- ending");
            Ending();
            Console.WriteLine($"  {_pass} passed, {_fail} failed");
        }

        // ---------------------------------------------------------------- save format

        private static JsonSerializerOptions MphReadJson() => new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            WriteIndented = true,
            Converters = { new GameState.ByteArrayConverter() }
        };

        private static void SaveFormat(CampaignSaves saves)
        {
            var rng = new Random(1234);
            var story = new StorySave();
            List<string> unhandled = Randomize(story, rng);
            Check($"every StorySave property has a test value (unhandled: {String.Join(", ", unhandled.DefaultIfEmpty("none"))})", unhandled.Count == 0);
            // every public property must be written (a field added to StorySave later would otherwise be dropped)
            string storyJson = CampaignSaves.StoryToJson(story);
            using (JsonDocument doc = JsonDocument.Parse(storyJson))
            {
                var missing = typeof(StorySave).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => p.Name).Where(n => !doc.RootElement.TryGetProperty(n, out _)).ToList();
                Check($"our writer covers every StorySave property (missing: {String.Join(", ", missing.DefaultIfEmpty("none"))})", missing.Count == 0);
            }
            var file = new CampaignSaveFile
            {
                Hunter = Hunter.Kanden,
                Planet = Planet.Arcterra,
                SavedAtUtc = new DateTime(2026, 9, 29, 12, 34, 56, DateTimeKind.Utc),
                PlaySeconds = 12345,
                Cleared = true,
                Story = story
            };
            saves.Save(9, file);
            CampaignSaveFile back = saves.Load(9)!;
            JsonSerializerOptions opt = MphReadJson();
            string before = JsonSerializer.Serialize(story, opt);
            string after = JsonSerializer.Serialize(back.Story, opt);
            Check("story survives save -> load (compared through MphRead's own serializer)", before == after);
            Check("header survives save -> load", back.Hunter == Hunter.Kanden && back.Planet == Planet.Arcterra
                && back.SavedAtUtc == file.SavedAtUtc && back.PlaySeconds == 12345 && back.Cleared);
            // desktop MphRead can read our Story object, and we can read its save
            StorySave? viaMphRead = JsonSerializer.Deserialize<StorySave>(storyJson, opt);
            Check("MphRead's own deserializer reads our Story object", viaMphRead != null && JsonSerializer.Serialize(viaMphRead, opt) == before);
            var fromMphRead = CampaignSaves.FromJson(CampaignSaves.ToJson(file).Replace(storyJson, before));
            Check("we read a Story object written by MphRead", JsonSerializer.Serialize(fromMphRead.Story, opt) == before);
            StorySave clone = CampaignSaves.Clone(story);
            Check("Clone is a deep copy", !ReferenceEquals(clone.RoomState, story.RoomState) && JsonSerializer.Serialize(clone, opt) == before);
            saves.Save(9, file);
            Check("second save keeps the previous file as .bak", File.Exists(saves.PathFor(9) + ".bak"));
            File.WriteAllText(saves.PathFor(8), "{ \"Version\": 1, \"Hunter\": \"Samus\" ");
            bool loaded = saves.TryLoad(8, out _, out string? error);
            Check($"a damaged file is reported, not thrown ({error})", !loaded && error != null);
            Check("an empty slot loads as null", saves.Load(7) == null);
            // the file-select summary (front end files A/B/C)
            CampaignSlotSummary sum = saves.Summarize(9);
            Check($"summary of a save: {sum.Hunter}, {sum.Planet}, {sum.PlaySeconds} s, cleared {sum.Cleared}, "
                + $"{sum.CompletionPercent}%, octoliths {sum.Octoliths}/{sum.OctolithTotal}, {sum.Planets.Count} planets",
                sum.Exists && sum.Readable && sum.Hunter == Hunter.Kanden && sum.Planet == Planet.Arcterra && sum.PlaySeconds == 12345
                && sum.Cleared && sum.SavedAtUtc == file.SavedAtUtc && sum.CompletionPercent == Math.Clamp(story.GetCompletionPercentage(), 0, 100)
                && sum.Octoliths == story.CountFoundOctoliths() && sum.Planets.Count == 5
                && sum.Planets[0].Unlocked == ShipNavigation.IsUnlocked(story, Planet.Alinos));
            CampaignSlotSummary empty = saves.Summarize(7);
            CampaignSlotSummary damaged = saves.Summarize(8);
            Check($"summary: empty slot (exists {empty.Exists}), damaged file (exists {damaged.Exists}, readable {damaged.Readable}, error set {damaged.Error != null})",
                !empty.Exists && !empty.Readable && damaged.Exists && !damaged.Readable && damaged.Error != null);
            IReadOnlyList<CampaignSlotSummary> all = saves.SummarizeSlots(7, 3);
            Check($"SummarizeSlots(7, 3) -> slots {String.Join(",", all.Select(s => s.Slot))}",
                all.Count == 3 && all[0].Slot == 7 && all[2].Slot == 9 && all[2].Readable);
            saves.Delete(8);
            saves.Delete(9);
            File.Delete(saves.PathFor(9) + ".bak");
        }

        // Fills every public property with non-default values; returns the names of properties of a type it
        // doesn't know how to fill.
        private static List<string> Randomize(StorySave s, Random rng)
        {
            var unhandled = new List<string>();
            foreach (PropertyInfo p in typeof(StorySave).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object? value = p.GetValue(s);
                switch (value)
                {
                case byte[][] jagged:
                    foreach (byte[] row in jagged) rng.NextBytes(row);
                    break;
                case byte[] bytes:
                    rng.NextBytes(bytes);
                    break;
                case int[] ints:
                    for (int i = 0; i < ints.Length; i++) ints[i] = rng.Next(-1000, 100000);
                    break;
                case int:
                    p.SetValue(s, rng.Next(-5, 100000));
                    break;
                case ushort:
                    p.SetValue(s, (ushort)rng.Next(1, 65536));
                    break;
                case uint:
                    p.SetValue(s, (uint)rng.NextInt64(1, UInt32.MaxValue));
                    break;
                case byte:
                    p.SetValue(s, (byte)rng.Next(1, 256));
                    break;
                case BossFlags:
                    p.SetValue(s, (BossFlags)rng.Next(1, 0x20000));
                    break;
                case StorySave.SaveStats stats:
                    stats.HunterKills = (uint)rng.Next(1, 1000);
                    stats.Deaths = (uint)rng.Next(1, 1000);
                    stats.EnemyHunterDeaths = (uint)rng.Next(1, 1000);
                    stats.EnemyKills = (uint)rng.Next(1, 1000);
                    break;
                default:
                    unhandled.Add($"{p.Name} ({p.PropertyType.Name})");
                    break;
                }
            }
            return unhandled;
        }

        // ---------------------------------------------------------------- pickups

        // picks up one of each expansion in the sim, saves, and reads the file-select counts back
        private static void Pickups(CampaignSaves saves)
        {
            var fresh = new StorySave();
            saves.Save(5, new CampaignSaveFile { Story = fresh });
            CampaignSlotSummary none = saves.Summarize(5);
            Check($"new game counts: missile {none.MissileExpansions}, UA {none.UaExpansions}, energy tanks {none.EnergyTanks}",
                none.MissileExpansions == 0 && none.UaExpansions == 0 && none.EnergyTanks == 0);
            StorySave story = CampaignSaves.Clone(fresh);
            foreach ((string room, ItemType type) in new[] { ("UNIT1_RM2", ItemType.MissileExpansion), ("UNIT1_RM5", ItemType.UAExpansion),
                ("UNIT1_C0", ItemType.EnergyTank) })
            {
                using CampaignHost host = CampaignHost.Start(room, save: CampaignSaves.Clone(story));
                var idle = new CampaignInput { SelectWeapon = BeamType.None };
                for (int i = 0; i < 300; i++)
                {
                    idle.DialogButton = host.DialogPaused && i % 10 == 0 ? HostDialogButton.Advance : HostDialogButton.None;
                    host.Step(idle);
                }
                ItemInstanceEntity? item = null;
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (e is ItemInstanceEntity it && it.ItemType == type)
                    {
                        item = it;
                    }
                }
                if (item == null)
                {
                    Check($"{type} in {room}: no item instance spawned", false);
                    continue;
                }
                host.PlacePlayer(item.Position, -Vector3.UnitZ);
                StorySave live = GameState.StorySave;
                int before = type == ItemType.EnergyTank ? live.HealthMax : live.AmmoMax[type == ItemType.MissileExpansion ? 1 : 0];
                for (int i = 0; i < 300; i++)
                {
                    idle.DialogButton = host.DialogPaused && i % 10 == 0 ? HostDialogButton.Advance : HostDialogButton.None;
                    host.Step(idle);
                }
                int after = type == ItemType.EnergyTank ? live.HealthMax : live.AmmoMax[type == ItemType.MissileExpansion ? 1 : 0];
                Check($"{type} picked up in {room} ({before} -> {after})", after > before);
                story = CampaignSaves.Clone(live);
            }
            saves.Save(5, new CampaignSaveFile { Story = story });
            CampaignSlotSummary got = saves.Summarize(5);
            Check($"after one of each: missile {got.MissileExpansions}, UA {got.UaExpansions}, energy tanks {got.EnergyTanks}",
                got.MissileExpansions == 1 && got.UaExpansions == 1 && got.EnergyTanks == 1);
            saves.Delete(5);
        }

        // ---------------------------------------------------------------- boarding

        // the entity that sends the ship-hatch message, and a point inside its volume
        private static (EntityBase Entity, Vector3 Point)? FindHatch(Scene scene)
        {
            foreach (EntityBase entity in scene.Entities)
            {
                if (entity is TriggerVolumeEntity trigger && (trigger.Data.ParentMessage == Message.ShipHatch
                    || trigger.Data.ChildMessage == Message.ShipHatch))
                {
                    return (trigger, trigger.Volume.GetCenter());
                }
                if (entity is AreaVolumeEntity area && area.Data.InsideMessage == Message.ShipHatch)
                {
                    var volume = (CollisionVolume)typeof(AreaVolumeEntity)
                        .GetField("_volume", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(area)!;
                    return (area, volume.GetCenter());
                }
            }
            return null;
        }

        // stands the player in the hatch volume and answers YES; returns frames until the session ended, or -1
        private static int EnterShip(CampaignHost host, bool verbose)
        {
            (EntityBase Entity, Vector3 Point)? hatch = FindHatch(host.Scene);
            if (hatch == null)
            {
                Console.WriteLine("    no ship hatch entity in this room");
                return -1;
            }
            if (verbose)
            {
                Console.WriteLine($"    hatch: {hatch.Value.Entity.Type} #{hatch.Value.Entity.Id} at {Fmt(hatch.Value.Point)}");
            }
            host.PlacePlayer(hatch.Value.Point, -Vector3.UnitZ);
            int f = 0;
            for (; f < 300 && !(host.DialogPaused && host.Dialog == DialogType.YesNo); f++)
            {
                host.Step(default);
            }
            if (!host.DialogPaused)
            {
                Console.WriteLine($"    no prompt after {f} frames (player at {Fmt(host.Player.Position)})");
                return -1;
            }
            for (int i = 0; i < 1200 && !host.Ended; i++, f++)
            {
                host.Step(new CampaignInput { DialogButton = i % 10 == 0 ? HostDialogButton.Yes : HostDialogButton.None });
            }
            return host.Ended ? f : -1;
        }

        private static void RunLanding(CampaignHost host, int maxFrames = 2000)
        {
            // the landing camera sequence holds input; wait for the player to be controllable
            for (int i = 0; i < maxFrames; i++)
            {
                host.Step(default);
            }
        }

        private static StorySave? BoardShip(CampaignSaves saves)
        {
            StorySave? saved = null;
            using (CampaignHost host = CampaignHost.Start("UNIT2_LAND"))
            {
                RunLanding(host, 1000);
                host.Player.TakeDamage(40, DamageFlags.None, null, null);
                for (int i = 0; i < 30; i++) host.Step(default);
                int hurt = host.Player.Health;
                int frames = EnterShip(host, verbose: true);
                Check($"YES at the hatch ends the session (after {frames} frames)", frames >= 0);
                CampaignEnd end = ShipNavigation.Classify(host);
                Check($"classified as boarding the ship ({end})", end == CampaignEnd.EnteredShip);
                Check("classification consumes Menu.NeededSave", Menu.NeededSave == SaveWhen.Never);
                StorySave live = GameState.StorySave;
                Check($"boarding refills energy ({hurt} -> {live.Health}/{live.HealthMax})", live.Health == live.HealthMax && hurt < live.HealthMax);
                // (the live save's checkpoint is cleared every frame by PlayerEntity.SaveStatus; the landing room sets
                // it again when it loads -- the Landings test below spawns at the ship from this save)
                Check($"boarding refills ammo ({live.Ammo[0]}/{live.AmmoMax[0]}, {live.Ammo[1]}/{live.AmmoMax[1]})",
                    live.Ammo[0] == live.AmmoMax[0] && live.Ammo[1] == live.AmmoMax[1]);
                StorySave copy = CampaignSaves.Clone(live);
                ShipNavigation.PrepareForSave(copy);
                saves.Save(1, new CampaignSaveFile { Planet = Planet.CelestialArchives, SavedAtUtc = DateTime.UtcNow, Story = copy });
                saved = saves.Load(1)?.Story;
                Check("saved to slot 1 and loaded back", saved != null && saved.CheckVisitedRoom(45));
            }
            return saved;
        }

        // ---------------------------------------------------------------- star map

        private static void StarMap()
        {
            var fresh = new StorySave();
            string Unlocked(StorySave s) => String.Join(", ", ShipNavigation.StarMap(s).Where(p => p.Unlocked).Select(p => p.Planet.Name));
            Check($"new game: only Celestial Archives ({Unlocked(fresh)})", Unlocked(fresh) == "CELESTIAL ARCHIVES");
            var afterCretaphid = new StorySave();
            afterCretaphid.Areas |= 3; // what EnemySpawnEntity does when Cretaphid spawns its death
            Check($"after Cretaphid: + Alinos ({Unlocked(afterCretaphid)})", Unlocked(afterCretaphid) == "ALINOS, CELESTIAL ARCHIVES");
            var afterSlench = new StorySave();
            afterSlench.Areas |= 3 | 0xF0;
            Check($"after Slench: + VDO, Arcterra ({Unlocked(afterSlench)})",
                Unlocked(afterSlench) == "ALINOS, CELESTIAL ARCHIVES, VESPER DEFENSE OUTPOST, ARCTERRA");
            var all = new StorySave();
            all.Areas = 0x1FF;
            all.FoundOctoliths = 0xFF;
            all.Artifacts = 0xFFFFFF;
            all.BossFlags = BossFlags.All;
            PlanetStatus alinos = ShipNavigation.Status(all, Planet.Alinos);
            PlanetStatus oubliette = ShipNavigation.Status(all, Planet.Oubliette);
            Check($"Oubliette listed once unlocked ({Unlocked(all)})", oubliette.Unlocked);
            Check($"per-planet progress: Alinos {alinos.Octoliths}/{alinos.OctolithTotal} octoliths, "
                + $"{alinos.Artifacts}/{alinos.ArtifactTotal} artifacts, areas {String.Join("/", alinos.Areas)}",
                alinos.Octoliths == 2 && alinos.Artifacts == 6 && alinos.Areas.All(a => a == AreaState.Escape));
            Check($"Oubliette has no octoliths/artifacts ({oubliette.OctolithTotal}/{oubliette.ArtifactTotal})",
                oubliette.OctolithTotal == 0 && oubliette.ArtifactTotal == 0);
            Check("landing room lookups", ShipNavigation.ByLandingRoom(77)?.Id == Planet.Arcterra
                && ShipNavigation.ByArea(3)?.Id == Planet.CelestialArchives && ShipNavigation.ByRoomName("gorea_land")?.Id == Planet.Oubliette
                && ShipNavigation.ByLandingRoom(46) == null);
        }

        // ---------------------------------------------------------------- landings

        private static void Landings(StorySave? boarded)
        {
            if (boarded == null)
            {
                Check("landings need the boarded save", false);
                return;
            }
            foreach (PlanetInfo planet in ShipNavigation.Planets)
            {
                StorySave save = CampaignSaves.Clone(boarded);
                save.Areas = 0x1FF; // every planet on the map
                try
                {
                    using CampaignHost host = CampaignHost.Start(planet.LandingRoom, save: save);
                    RunLanding(host);
                    PlayerEntity p = host.Player;
                    bool alive = p.Health > 0 && !Single.IsNaN(p.Position.X);
                    bool visited = GameState.StorySave.CheckVisitedRoom(planet.LandingRoomId);
                    (EntityBase Entity, Vector3 Point)? hatch = FindHatch(host.Scene);
                    float toHatch = hatch == null ? -1 : (hatch.Value.Point - p.Position).Length;
                    Check($"{planet.Name}: lands in {planet.LandingRoom} (room {host.RoomId}), energy {p.Health}, at {Fmt(p.Position)}, "
                        + $"{toHatch:0.0} from the hatch, visited {visited}",
                        host.RoomId == planet.LandingRoomId && alive && visited && !host.Ended);
                    int frames = EnterShip(host, verbose: false);
                    CampaignEnd end = ShipNavigation.Classify(host);
                    Check($"{planet.Name}: back aboard ({end}, {frames} frames)", end == CampaignEnd.EnteredShip);
                }
                catch (Exception ex)
                {
                    Check($"{planet.Name}: {ex.GetType().Name}: {ex.Message}", false);
                    Console.WriteLine(ex.StackTrace);
                }
            }
        }

        // ---------------------------------------------------------------- game over / ending

        private static void GameOverQuit()
        {
            using CampaignHost host = CampaignHost.Start("UNIT2_LAND");
            RunLanding(host, 1000);
            host.Player.TakeDamage(9999, DamageFlags.IgnoreInvuln, null, null);
            int f = 0;
            for (; f < 900 && !(host.DialogPaused && host.Dialog == DialogType.YesNo); f++)
            {
                host.Step(default);
            }
            Check($"death shows the continue prompt (frame {f})", host.DialogPaused && host.Dialog == DialogType.YesNo);
            for (int i = 0; i < 600 && !host.Ended; i++)
            {
                host.Step(new CampaignInput { DialogButton = i % 10 == 0 ? HostDialogButton.No : HostDialogButton.None });
            }
            CampaignEnd end = ShipNavigation.Classify(host);
            Check($"NO ends the session as a quit ({end})", host.Ended && end == CampaignEnd.Quit);
        }

        private static void Ending()
        {
            // Gorea 2's death runs exactly this (31_Gorea2.cs): the good ending movie, then AfterMovie.EndGame
            using CampaignHost host = CampaignHost.Start("Gorea_b2", setupSave: s => s.BossFlags = BossFlags.All);
            for (int i = 0; i < 60; i++) host.Step(default);
            host.Scene.StartMovie(Movie.GoodEnding, FadeType.FadeOutInWhite, 60 / 30f, FadeType.FadeOutBlack, 0,
                afterMovieAction: AfterMovie.EndGame);
            int f = 0;
            for (; f < 600 && !host.Ended; f++)
            {
                host.Step(default);
            }
            CampaignEnd end = ShipNavigation.Classify(host);
            Check($"the ending movie ends the session as the ending ({end}, {f} frames)", end == CampaignEnd.Ending);
        }

        private static string Fmt(Vector3 v) => $"({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})";
    }
}
