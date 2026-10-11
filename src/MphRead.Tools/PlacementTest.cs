using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using MphRead.Entities;
using MphRead.Formats.Culling;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // The dev pickup placement editor (MphRecomp.Core Arenas/PickupPlacement.cs), headless:
    //   -placetest [arena=Spires] [vanilla=Combat Hall]
    //   -placetest center=<folder of placement files>: each pickup / crate's centring proposal (PlacementCentering)
    //   -placetest baked[=<room name part>]: the shipped placements (PlacedPickups, 0.2.0) in real 2-player matches
    // In an Echoes arena: the owner's picked affinity crate is there on a first visit and up at once, as are the arena's
    // own crates; a Pirate Blaster placed at the player's feet spawns its item; the aim finds a floor; the nearest is
    // the blaster and deleting it takes it (and its item) out; deleting an arena crate and an arena pickup; the file
    // saved and, in a new match, read back (the same placed / deleted, the deleted gone again). In a vanilla arena: a
    // placed affinity pickup spawns, no crate there.
    internal static class PlacementTest
    {
        public static void Run(string[] args)
        {
            string echoes = "Spires", vanilla = "Combat Hall";
            foreach (string arg in args.Skip(1))
            {
                int eq = arg.IndexOf('=');
                string key = eq < 0 ? arg : arg[..eq];
                string value = eq < 0 ? "" : arg[(eq + 1)..];
                switch (key.ToLowerInvariant())
                {
                case "arena": echoes = value; break;
                case "vanilla": vanilla = value; break;
                case "center": Center(value); return;
                case "baked": Baked(value); return;
                case "gravity": Gravity(value); return;
                case "show": Show(value, vanilla); return;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            System.Diagnostics.Trace.Listeners.Clear(); // MphRead's Debug.Asserts would end this Debug process
            if (!ToolPaths.UseDesktopExtraction()) return;
            EchoesArena.Register(Paths.FileSystem);
            string dir = Path.Combine(Path.GetTempPath(), "mph_placetest_" + Environment.ProcessId);
            int fails = 0;
            void Check(bool ok, string what)
            {
                if (!ok) fails++;
                Console.WriteLine($"    {(ok ? "ok  " : "FAIL")} {what}");
            }
            var idle = new CampaignInput { SelectWeapon = BeamType.None };
            CampaignHost Start(string arena)
            {
                MatchSettings settings = MatchSettings.Quick(GameMode.Battle, arena, Hunter.Samus, 0);
                settings.TimeLimitSeconds = 36000;
                settings.PointGoal = 999;
                settings.Solo = true; // alone, as the dev solo run (SoloArena.cs)
                return CampaignHost.StartMatch(settings);
            }
            void Steps(CampaignHost h, int n)
            {
                for (int i = 0; i < n; i++) h.Step(idle);
            }
            List<EchoesCrateEntity> Crates(Scene scene)
            {
                var list = new List<EchoesCrateEntity>();
                foreach (EnemyInstanceEntity e in scene.GetEnemyInstanceEntities()) if (e is EchoesCrateEntity c) list.Add(c);
                return list;
            }
            List<ItemSpawnEntity> Items(Scene scene)
            {
                var list = new List<ItemSpawnEntity>();
                foreach (ItemSpawnEntity e in scene.GetItemSpawnEntities()) list.Add(e);
                return list;
            }
            bool InScene(Scene scene, EntityBase x)
            {
                foreach (EntityBase e in scene.Entities) if (e == x) return true;
                return false;
            }
            int CountCrates(Scene scene) => Crates(scene).Count;
            int CountItems(Scene scene) => Items(scene).Count;

            EchoesArena.Def? def = EchoesArena.Find(echoes);
            if (def != null && Metadata.GetRoomByName(def.Room).Item1 != null)
            {
                Console.WriteLine($"  {def.InGameName} (Echoes):");
                int crates0, items0, deletedId;
                Vector3 deletedCrate;
                using (CampaignHost host = Start(def.Room))
                {
                    Steps(host, 2);
                    Scene scene = host.Scene;
                    crates0 = CountCrates(scene);
                    items0 = CountItems(scene);
                    var placer = new PickupPlacement(scene, host.RoomId, dir);
                    Check(placer.LastError == null, "no error: " + placer.LastError);
                    Check(placer.IsEchoes && placer.CanPlaceCrate, "an Echoes arena: crates can be placed");
                    Check(placer.PlacedCount == 1 && CountCrates(scene) == crates0 + 1, $"first visit: the picked affinity crate ({crates0} arena crates)");
                    Check(File.Exists(placer.Path), "saved on the first visit: " + placer.Path);
                    Steps(host, 3);
                    var all = Crates(scene).ToList();
                    Check(all.All(c => c.Up), "every crate up at once (not after 15 s)");
                    EchoesCrateEntity aff = all.First(c => c.Pool.Count == 1 && c.Pool[0] == ItemType.AffinityWeapon);
                    Console.WriteLine($"    affinity crate at {aff.Position}, floor {aff.DropPoint.AddY(-0.65f)}");
                    PlayerEntity p = host.Player;
                    for (int i = 0; i < 600 && p.Position == Vector3.Zero; i++) host.Step(idle); // (spawned after the intro)
                    Vector3? feet = placer.SpotAtFeet(p);
                    Check(feet != null, $"a floor at the player's feet ({p.Position} -> {feet})");
                    // one at the feet: spawned and picked up at once (Samus gets the blaster)
                    Check(!p.AvailableWeapons[BeamType.PirateBlaster], "Samus starts without the blaster");
                    ItemSpawnEntity atFeet = placer.AddItem(ItemType.PirateBlaster, feet!.Value);
                    Steps(host, 5);
                    Check(p.AvailableWeapons[BeamType.PirateBlaster], "a blaster placed at the feet: picked up (Samus has it)");
                    placer.Delete(atFeet);
                    Vector3? aim = placer.SpotAtAim(p.CameraInfo.Position, p.NetAimVector, out float dist);
                    Console.WriteLine($"    aim from {p.CameraInfo.Position} along {p.NetAimVector}: {aim} ({dist:0.0} away)");
                    Check(aim != null, "the aim finds a floor");
                    // one where the aim lands: its item stays out
                    ItemSpawnEntity blaster = placer.AddItem(ItemType.PirateBlaster, aim ?? feet.Value);
                    Steps(host, 3);
                    Check(blaster.Item?.ItemType == ItemType.PirateBlaster, "the blaster placed where the aim lands spawned its item");
                    var down = placer.SpotAtAim(p.Position.AddY(1f), new Vector3(0.3f, -1f, 0.2f), out _);
                    Check(down != null, $"aiming down at the floor finds it ({down})");
                    PickupPlacement.Target? t = placer.Nearest(blaster.Position.AddY(1f));
                    Check(t?.Entity == blaster, "nearest = the placed blaster: " + t?.Label);
                    ItemInstanceEntity? item = blaster.Item;
                    placer.Delete(blaster);
                    Check(!InScene(scene, blaster) && (item == null || !InScene(scene, item)), "deleted: the spawner and its item gone");
                    Check(placer.PlacedCount == 1, "placed back to 1 (the crate)");
                    // an arena crate and an arena pickup
                    EchoesCrateEntity arenaCrate = all.First(c => c != aff);
                    deletedCrate = arenaCrate.Position;
                    placer.Delete(arenaCrate);
                    ItemSpawnEntity arenaItem = Items(scene).First();
                    deletedId = arenaItem.Id;
                    placer.Delete(arenaItem);
                    Check(CountCrates(scene) == crates0 && CountItems(scene) == items0 - 1, "arena crate + pickup deleted");
                    // a second affinity crate and a blaster to keep
                    placer.AddAffinityCrate(feet.Value + new Vector3(2, 0, 0));
                    placer.AddItem(ItemType.PirateBlaster, feet.Value);
                    Check(placer.Save(), "saved: " + placer.LastError);
                    Steps(host, 30);
                    Console.WriteLine(File.ReadAllText(placer.Path!).Replace("\n", "\n      "));
                }
                using (CampaignHost host = Start(def.Room))
                {
                    Steps(host, 2);
                    Scene scene = host.Scene;
                    var placer = new PickupPlacement(scene, host.RoomId, dir);
                    Check(placer.PlacedCount == 3 && placer.DeletedCount == 2, $"read back: {placer.PlacedCount} placed (3), {placer.DeletedCount} deleted (2)");
                    Check(!scene.TryGetEntity(deletedId, out _), "the deleted arena pickup gone again");
                    Check(!Crates(scene).Any(c => (c.Position - deletedCrate).LengthSquared < 0.01f),
                        "the deleted arena crate gone again");
                    Check(CountCrates(scene) == crates0 - 1 + 2 && CountItems(scene) == items0 - 1 + 1, "crates and pickups as left");
                    Steps(host, 60);
                }
            }
            else
            {
                Console.WriteLine($"  {echoes}: not imported here, skipped");
            }
            Console.WriteLine($"  {vanilla} (vanilla):");
            using (CampaignHost host = Start((MatchArenas.Find(vanilla) ?? throw new ArgumentException("no arena " + vanilla)).Name))
            {
                Steps(host, 2);
                var placer = new PickupPlacement(host.Scene, host.RoomId, dir);
                for (int i = 0; i < 600 && host.Player.Position == Vector3.Zero; i++) host.Step(idle);
                Check(!placer.CanPlaceCrate && placer.PlacedCount == 0, "no crates, nothing placed yet");
                PlayerEntity p = host.Player;
                Vector3 at = placer.SpotAtAim(p.CameraInfo.Position, p.NetAimVector, out _) ?? placer.SpotAtFeet(p)!.Value;
                ItemSpawnEntity aff = placer.AddItem(ItemType.AffinityWeapon, at);
                Steps(host, 3);
                Check(aff.Item?.ItemType == ItemType.AffinityWeapon, "the placed affinity pickup spawned its item");
                Check(aff.NodeRef != NodeRef.None, "in a room node (drawn and heard)");
                Check(placer.Save() && File.Exists(placer.Path), "saved " + Path.GetFileName(placer.Path));
                Steps(host, 60);
            }
            try { Directory.Delete(dir, true); } catch { }
            Console.WriteLine(fails == 0 ? "PASS" : $"FAIL ({fails})");
        }

        static void Center(string dir)
        {
            System.Diagnostics.Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction()) return;
            EchoesArena.Register(Paths.FileSystem);
            var idle = new CampaignInput { SelectWeapon = BeamType.None };
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                var data = System.Text.Json.JsonSerializer.Deserialize<PickupPlacement.FileData>(File.ReadAllText(file))!;
                Console.WriteLine($"  {data.Arena} ({Path.GetFileName(file)}):");
                MatchSettings settings = MatchSettings.Quick(GameMode.Battle, data.Room, Hunter.Samus, 0);
                settings.Solo = true;
                using CampaignHost host = CampaignHost.StartMatch(settings);
                host.Step(idle);
                static Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
                void Show(string what, Vector3 floor, float height)
                {
                    PlacementCentering.Proposal p = PlacementCentering.Propose(host.Scene, floor, height);
                    Console.WriteLine($"    {what} at ({floor.X:0.00}, {floor.Y:0.00}, {floor.Z:0.00}) -> ({p.To.X:0.00}, {p.To.Y:0.00}, {p.To.Z:0.00})"
                        + $"  [{p.Shift:0.00} units]");
                    Console.WriteLine("      " + p.Why);
                }
                foreach (PickupPlacement.PickupSpot s in data.Pickups) Show(s.Item, V(s.Pos), 0.65f);
                foreach (PickupPlacement.CrateSpotData c in data.Crates) Show("crate (" + string.Join(", ", c.Pool) + ")", V(c.Floor), V(c.Pos).Y - V(c.Floor).Y);
            }
        }

        // -placetest baked: each shipped placement file (PlacedPickups) in a real 2-player Battle with EXTRA PICKUPS off
        // (exactly the arena's own spawners and crates) and on (the placed spawners added, the deleted ones gone, every
        // placed item up where the file puts it, a vanilla spawner in a room part; Echoes: its crates added), and on but
        // as an old replay (Settings.PlacedPickups false) = off
        sealed record Spawner(int Id, string Type, Vector3 Position, string? Up, int Interval, int Part);

        static void Baked(string only)
        {
            System.Diagnostics.Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction()) return;
            EchoesArena.Register(Paths.FileSystem);
            var idle = new CampaignInput { SelectWeapon = BeamType.None };
            int fails = 0, arenas = 0;
            void Check(bool ok, string what)
            {
                if (!ok) fails++;
                if (!ok) Console.WriteLine($"    FAIL {what}");
            }
            static Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
            foreach (PickupPlacement.FileData data in PlacedPickups.Files.Values.OrderBy(d => d.Room, StringComparer.Ordinal))
            {
                if (only.Length > 0 && !data.Room.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                if (Metadata.GetRoomByName(data.Room).Item1 == null)
                {
                    Console.WriteLine($"  {data.Arena} ({data.Room}): room not available here, skipped");
                    continue;
                }
                arenas++;
                (List<Spawner> Items, List<Spawner> Crates) Run(bool? blaster, bool? placed)
                {
                    MatchSettings settings = MatchSettings.Quick(GameMode.Battle, data.Room, Hunter.Samus, 1);
                    settings.TimeLimitSeconds = 36000;
                    settings.PointGoal = 999;
                    settings.PirateBlaster = blaster;
                    settings.PlacedPickups = placed;
                    using CampaignHost host = CampaignHost.StartMatch(settings);
                    for (int i = 0; i < 4; i++) host.Step(idle);
                    var items = new List<Spawner>();
                    foreach (ItemSpawnEntity e in host.Scene.GetItemSpawnEntities())
                    {
                        items.Add(new Spawner(e.Id, e.Data.ItemType.ToString(), e.Position, e.Item?.ItemType.ToString(), e.Data.SpawnInterval, e.NodeRef.PartIndex));
                    }
                    var crates = new List<Spawner>();
                    foreach (EnemyInstanceEntity e in host.Scene.GetEnemyInstanceEntities())
                    {
                        if (e is EchoesCrateEntity c) crates.Add(new Spawner(c.Id, "crate", c.Position, null, 0, 0));
                    }
                    return (items, crates);
                }
                var off = Run(false, null);
                var on = Run(null, null);
                var old = Run(null, false);
                int removedItems = data.RemovedItems.Count(r => off.Items.Any(s => s.Id == r.Id));
                Check(old.Items.Count == off.Items.Count && old.Crates.Count == off.Crates.Count, "old replay (format < 29) = vanilla");
                Check(on.Items.Count == off.Items.Count - removedItems + data.Pickups.Count,
                    $"spawners: vanilla {off.Items.Count} - {removedItems} deleted + {data.Pickups.Count} placed = {on.Items.Count}");
                Check(on.Crates.Count >= off.Crates.Count - data.RemovedCrates.Count + data.Crates.Count,
                    $"crates: vanilla {off.Crates.Count} - {data.RemovedCrates.Count} + {data.Crates.Count} placed = {on.Crates.Count}");
                foreach (PickupPlacement.RemovedItem r in data.RemovedItems)
                {
                    Check(!on.Items.Any(s => s.Id == r.Id), $"deleted spawner {r.Id} ({r.Item}) gone");
                }
                int maxOff = off.Items.Select(s => s.Id).Concat(off.Crates.Select(c => c.Id)).DefaultIfEmpty(0).Max();
                bool echoes = EchoesArena.All.Any(d => d.Id == data.RoomId);
                foreach (PickupPlacement.PickupSpot spot in data.Pickups)
                {
                    Spawner? s = on.Items.FirstOrDefault(s => s.Type == spot.Item && (s.Position - V(spot.Pos)).Length < 0.01f);
                    Check(s != null, $"{spot.Item} spawner at ({spot.Pos[0]}, {spot.Pos[1]}, {spot.Pos[2]})");
                    if (s == null) continue;
                    Check(s.Id > maxOff, $"{spot.Item} id {s.Id} past the arena's ({maxOff})");
                    Check(s.Up == spot.Item, $"{spot.Item} item up (got {s.Up ?? "none"})");
                    Check(s.Interval == spot.RespawnSeconds * 30, $"{spot.Item} respawn {spot.RespawnSeconds} s");
                    Check(echoes || s.Part >= 0, $"{spot.Item} in a room part");
                }
                Console.WriteLine($"  {data.Arena} ({data.Room}): spawners {off.Items.Count} -> {on.Items.Count}, crates {off.Crates.Count} -> {on.Crates.Count}"
                    + string.Concat(on.Items.Where(s => s.Id > maxOff).Select(s => $"; {s.Type} id {s.Id} part {s.Part}")));
            }
            Console.WriteLine(fails == 0 ? $"-placetest baked: PASS ({arenas} arenas)" : $"-placetest baked: {fails} FAILED");
        }

        // -placetest show=<dir> vanilla=<arena>: the arena with a pulled placements folder applied, each spawner listed
        // (placed ones from id 20000 up): its item, model, room node and spot, next to the arena's own
        static void Show(string dir, string arena)
        {
            System.Diagnostics.Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction()) return;
            EchoesArena.Register(Paths.FileSystem);
            var idle = new CampaignInput { SelectWeapon = BeamType.None };
            MatchSettings settings = MatchSettings.Quick(GameMode.Battle, (MatchArenas.Find(arena) ?? throw new ArgumentException("no arena " + arena)).Name, Hunter.Samus, 0);
            settings.TimeLimitSeconds = 36000;
            settings.PointGoal = 999;
            settings.Solo = true;
            using CampaignHost host = CampaignHost.StartMatch(settings);
            for (int i = 0; i < 2; i++) host.Step(idle);
            var placer = new PickupPlacement(host.Scene, host.RoomId, dir);
            Console.WriteLine($"  {settings.Arena}: {placer.PlacedCount} placed from {placer.Path} {placer.LastError}");
            for (int i = 0; i < 600 && host.Player.Position == Vector3.Zero; i++) host.Step(idle);
            for (int i = 0; i < 60; i++) host.Step(idle);
            foreach (ItemSpawnEntity s in host.Scene.GetItemSpawnEntities())
            {
                ItemInstanceEntity? it = s.Item;
                Console.WriteLine($"    spawner {s.Id} {s.Data.ItemType} at {s.Position} node {s.NodeRef.PartIndex}/{s.NodeRef.NodeIndex}"
                    + $" item {(it == null ? "none" : $"{it.ItemType} at {it.Position} node {it.NodeRef.PartIndex}/{it.NodeRef.NodeIndex} models {it.GetModels().Count} alpha {it.Alpha}")}");
            }
        }

        // -placetest gravity[=echoesfirst]: Transfer Lock's gravity volumes, solo vs a 2-player match: the player put in
        // each volume's middle, its fall per step against the biped's own gravity
        static void Gravity(string how)
        {
            System.Diagnostics.Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction()) return;
            EchoesArena.Register(Paths.FileSystem);
            var idle = new CampaignInput { SelectWeapon = BeamType.None };
            var field = typeof(AreaVolumeEntity).GetField("_volume", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            static Vector3 Center(CollisionVolume v) => v.Type switch
            {
                VolumeType.Box => v.BoxPosition + (v.BoxVector1 * v.BoxDot1 + v.BoxVector2 * v.BoxDot2 + v.BoxVector3 * v.BoxDot3) / 2,
                VolumeType.Cylinder => v.CylinderPosition + v.CylinderVector * v.CylinderDot / 2,
                _ => v.SpherePosition,
            };
            void Run(string room, bool solo, int bots = 1, Hunter hunter = Hunter.Samus, string? character = null,
                GameMode mode = GameMode.Battle)
            {
                MatchSettings settings = MatchSettings.Quick(mode, room, hunter, solo ? 0 : bots);
                settings.Players[0].Character = character;
                settings.Solo = solo;
                settings.TimeLimitSeconds = solo ? -1 : 36000;
                using CampaignHost host = CampaignHost.StartMatch(settings);
                if (!solo && PlayerEntity.Players.Count > 1) PlayerEntity.Players[1].IsBot = false;
                PlayerEntity p = host.Player;
                for (int i = 0; i < 600 && p.Position == Vector3.Zero; i++) host.Step(idle);
                for (int i = 0; i < 60; i++) host.Step(idle);
                var vols = new List<AreaVolumeEntity>();
                foreach (AreaVolumeEntity a in host.Scene.GetAreaVolumeEntities()) if (a.Data.InsideMessage == Message.Gravity) vols.Add(a);
                Console.WriteLine($"  {room} {mode} {(solo ? "SOLO" : $"{bots + 1} players")} {character ?? hunter.ToString()}: {vols.Count} gravity volumes, biped gravity {Fixed.ToFloat(p.Values.BipedGravity):0.0000}"
                    + $", layer players {PlayerEntity.PlayerCount}, echoes moves {PlayerEntity.HostEchoesMoves}, match {GameState.MatchState}");
                foreach (AreaVolumeEntity a in vols)
                {
                    Vector3 c = Center((CollisionVolume)field.GetValue(a)!);
                    p.Position = c;
                    p.Speed = Vector3.Zero;
                    var dv = new List<string>();
                    float prev = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        host.Step(idle);
                        dv.Add($"{p.Speed.Y - prev:0.0000}");
                        prev = p.Speed.Y;
                    }
                    Console.WriteLine($"    volume {a.Id} active {a.Active} param {Fixed.ToFloat((int)a.Data.InsideMsgParam1):0.0000} prio {a.Data.Priority} at {c}: speed.y per step {string.Join(" ", dv)}");
                }
            }
            if (how == "nodecheck")
            {
                // every vanilla arena's own item spawners: the node PickupPlacement.NodeAt picks for their spot vs the one
                // the game gave them (their entity's node name)
                int same = 0, all = 0, old = 0;
                foreach (RoomMetadata r in MatchArenas.All.Where(a => a.Id >= MatchArenas.FirstId && a.Id <= MatchArenas.LastId))
                {
                    MatchSettings settings = MatchSettings.Quick(GameMode.Battle, r.Name, Hunter.Samus, 0);
                    settings.Solo = true;
                    settings.TimeLimitSeconds = -1;
                    using CampaignHost host = CampaignHost.StartMatch(settings);
                    host.Step(idle);
                    var placer = new PickupPlacement(host.Scene, host.RoomId, null);
                    foreach (ItemSpawnEntity s in host.Scene.GetItemSpawnEntities())
                    {
                        if (s.NodeRef.PartIndex == -1) continue;
                        all++;
                        NodeRef n = placer.NodeAt(s.Position);
                        if (n.PartIndex == s.NodeRef.PartIndex) same++;
                        else Console.WriteLine($"  {r.Name} spawner {s.Id} at {s.Position}: game part {s.NodeRef.PartIndex}, NodeAt {n.PartIndex}");
                        if (host.Scene.GetNodeRefByPosition(s.Position.AddY(0.65f)).PartIndex == s.NodeRef.PartIndex) old++;
                    }
                }
                Console.WriteLine($"  NodeAt agrees with the game on {same}/{all} spawners (the old position lookup: {old}/{all})");
                return;
            }
            if (how == "nodes")
            {
                ModelInstance room = Read.GetRoomModelInstance("AD1 TRANSFER LOCK BT");
                foreach (Node n in room.Model.Nodes)
                {
                    if (n.MeshCount == 0) continue;
                    var mats = new List<string>();
                    for (int m = 0; m < n.MeshCount; m++)
                    {
                        Material mat = room.Model.Materials[room.Model.Meshes[n.MeshId / 2 + m].MaterialId];
                        if (!mats.Contains(mat.Name)) mats.Add(mat.Name + (mat.Alpha < 31 ? $"(a{mat.Alpha})" : ""));
                    }
                    Console.WriteLine($"  node '{n.Name}' bounds x {n.Bounds[0]:0.0}..{n.Bounds[3]:0.0} y {n.Bounds[1]:0.0}..{n.Bounds[4]:0.0} z {n.Bounds[2]:0.0}..{n.Bounds[5]:0.0}  {string.Join(", ", mats)}");
                }
                return;
            }
            if (how == "layers")
            {
                foreach (string file in new[] { @"levels\entities\ad1_Ent.bin", @"levels\entities\ad1_dm1_Ent.bin" })
                {
                    int vols = 0;
                    foreach (Entity e in Read.GetEntities(file, -1, firstHunt: false))
                    {
                        if (e is not Entity<AreaVolumeEntityData> av) continue;
                        vols++;
                        Console.WriteLine($"  {file} area volume {e.EntityId} layers 0x{e.LayerMask:X4} inside {av.Data.InsideMessage} param {av.Data.InsideMsgParam1}");
                    }
                    Console.WriteLine($"  {file}: {vols} area volumes in all layers");
                }
                foreach (Entity e in Read.GetEntities(@"levels\entities\ad1_Ent.bin", 8, firstHunt: false))
                {
                    Vector3 pos = e switch
                    {
                        Entity<AreaVolumeEntityData> x => x.Data.Header.Position.ToFloatVector(),
                        Entity<ForceFieldEntityData> x => x.Data.Header.Position.ToFloatVector(),
                        Entity<ObjectEntityData> x => x.Data.Header.Position.ToFloatVector(),
                        Entity<DoorEntityData> x => x.Data.Header.Position.ToFloatVector(),
                        Entity<PlatformEntityData> x => x.Data.Header.Position.ToFloatVector(),
                        _ => new Vector3(float.NaN),
                    };
                    if (float.IsNaN(pos.X)) continue;
                    string extra = e switch
                    {
                        Entity<AreaVolumeEntityData> x => $"{x.Data.InsideMessage} flags {x.Data.TriggerFlags} exit {x.Data.ExitMessage}",
                        Entity<ForceFieldEntityData> x => $"type {x.Data.Type} w {x.Data.Width.FloatValue:0.0} h {x.Data.Height.FloatValue:0.0} active {x.Data.Active} facing {x.Data.Header.FacingVector.ToFloatVector()}",
                        Entity<ObjectEntityData> x => $"model {x.Data.ModelId}",
                        _ => "",
                    };
                    Console.WriteLine($"    layer 8 {e.Type} {e.EntityId} '{e.NodeName}' at ({pos.X:0.0}, {pos.Y:0.0}, {pos.Z:0.0}) {extra}");
                    if (e is Entity<AreaVolumeEntityData> av)
                    {
                        CollisionVolume v = CollisionVolume.Move(av.Data.Volume, pos);
                        Console.WriteLine($"      volume {v.Type} pos {v.BoxPosition} v1 {v.BoxVector1}*{v.BoxDot1:0.0} v2 {v.BoxVector2}*{v.BoxDot2:0.0} v3 {v.BoxVector3}*{v.BoxDot3:0.0}");
                    }
                }
                foreach (GameMode m in new[] { GameMode.Battle, GameMode.Bounty, GameMode.Capture, GameMode.Defender, GameMode.Survival })
                    Run("AD1 TRANSFER LOCK BT", solo: false, mode: m);
                Run("AD1 TRANSFER LOCK BT", solo: true, mode: GameMode.Bounty); // the solo launcher's choice for it (SoloArena)
                return;
            }
            if (how == "fire")
            {
                foreach (Hunter h in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel })
                    Run("MP8 FIRE CONTROL", solo: true, hunter: h);
                Run("MP8 FIRE CONTROL", solo: true, hunter: Hunter.Weavel, character: "trooper");
                Run("MP8 FIRE CONTROL", solo: true, hunter: Hunter.Samus, character: "darksamus");
                return;
            }
            if (how == "all")
            {
                foreach (RoomMetadata r in MatchArenas.All) Run(r.Name, solo: true);
                foreach (RoomMetadata r in MatchArenas.All) Run(r.Name, solo: false, bots: 3);
                return;
            }
            if (how == "echoesfirst") Run(EchoesArena.All[1].Room, solo: true);
            foreach (RoomMetadata r in MatchArenas.All.Where(a => a.Name.Contains("TRANSFER LOCK")))
            {
                Run(r.Name, solo: false);
                Run(r.Name, solo: false, bots: 2);
                Run(r.Name, solo: false, bots: 3);
                Run(r.Name, solo: true);
            }
        }
    }
}
