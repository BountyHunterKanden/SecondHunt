using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using MphRead.Entities;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -enemyplacetest [ROOM] (docs/ENEMY_IMPORT.md H1, Hard Mode): the enemy catalogue and its hurt-by lines (8.6), the
    // spawner data builders, then a real campaign room with a placement file in a temp folder: placed MPH + First Hunt
    // enemies spawn, a wave waits for its group, the gate locks the doors while the group lives and opens them when it's
    // dead, a room's own spawner is removed and another buffed; then the editor's calls (place, change, delete) and the
    // saved file read back. ROOM defaults to UNIT1_RM1; root=<import root> adds a First Hunt import (else its checks fail).
    internal static class EnemyPlaceTest
    {
        static int _fails;

        static void Check(bool ok, string what)
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) _fails++;
        }

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string roomName = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('=')) ?? "UNIT1_RM1";
            // root=<import root>: a First Hunt import (FhGameTool's), for the First Hunt enemies
            if (args.FirstOrDefault(a => a.StartsWith("root=")) is string rootArg)
            {
                string root = rootArg["root=".Length..];
                MphRecomp.Arenas.EchoesArena.ImportRoot = root;
                Console.WriteLine($"First Hunt import {root}: {(MphRecomp.FirstHunt.FirstHuntGame.Register(root) ? "registered" : "NOT FOUND")}");
                EnemyCatalogue.Reset();
            }

            Console.WriteLine("== data sizes");
            Check(Marshal.SizeOf<EnemySpawnEntityData>() == 512, $"EnemySpawnEntityData {Marshal.SizeOf<EnemySpawnEntityData>()} bytes (512)");
            Check(Marshal.SizeOf<FhEnemySpawnEntityData>() == 268, $"FhEnemySpawnEntityData {Marshal.SizeOf<FhEnemySpawnEntityData>()} bytes (268)");

            Console.WriteLine("== catalogue");
            IReadOnlyList<EnemyCatalogue.Entry> all = EnemyCatalogue.Entries;
            foreach (EnemyCatalogue.Entry e in all)
            {
                (string hurt, string? warn) = EnemyCatalogue.Describe(e, 27, Hunter.Samus);
                Console.WriteLine($"  {e.Id,-22} from {e.SourceRoom,-14} {hurt}{(warn != null ? "  " + warn : "")}");
            }
            Check(all.Count(e => !e.FirstHunt) >= 15, $"{all.Count(e => !e.FirstHunt)} MPH entries of {EnemyCatalogue.PlaceableMph.Length}");
            Check(all.Any(e => e.FirstHunt), $"{all.Count(e => e.FirstHunt)} First Hunt entries of {EnemyCatalogue.PlaceableFh.Length}"
                + (all.Any(e => e.FirstHunt) ? "" : " (First Hunt imported? " + EnemyCatalogue.LastError + ")"));

            Console.WriteLine("== route weapons (Samus; Trace's alt form is contact)");
            foreach (int area in EnemyCatalogue.RouteAreas)
            {
                int room = Metadata.RoomList.First(m => !m.Multiplayer && !m.FirstHunt && m.Id >= 27 && Metadata.GetAreaInfo(m.Id) == area).Id;
                Console.WriteLine($"  area {area} (room {room}): {String.Join(" ", EnemyCatalogue.OwnedAt(room, Hunter.Samus).OrderBy(b => b))}");
            }
            if (EnemyCatalogue.Find("mph:Voldrum2") is EnemyCatalogue.Entry vol)
            {
                Console.WriteLine($"  Voldrum2 as Trace at 27: {EnemyCatalogue.Describe(vol, 27, Hunter.Trace)}");
            }

            Console.WriteLine("== data builders");
            EnemyCatalogue.Entry? zoomer = all.FirstOrDefault(e => !e.FirstHunt);
            if (zoomer != null)
            {
                var p = new EnemyPlacement.PlacedEnemy { Id = zoomer.Id, Pos = new[] { 1.5f, 2f, -3f }, Facing = 90, Count = 2, Limit = 3, Total = 4, Trigger = "wave" };
                EnemySpawnEntityData d = EnemyPlacement.PlacedData(zoomer.Mph, p, 20001);
                Vector3 pos = d.Header.Position.ToFloatVector(), face = d.Header.FacingVector.ToFloatVector();
                Check(d.Header.EntityId == 20001 && (pos - new Vector3(1.5f, 2f, -3f)).Length < 0.01f, $"placed header id {d.Header.EntityId} pos {pos}");
                Check((face - Vector3.UnitX).Length < 0.01f, $"facing 90 -> {face}");
                Check(d.EnemyType == zoomer.Type && d.SpawnCount == 2 && d.SpawnLimit == 3 && d.SpawnTotal == 4 && d.Active == 0,
                    $"{d.EnemyType} count {d.SpawnCount} limit {d.SpawnLimit} total {d.SpawnTotal} active {d.Active} (wave: inactive)");
                Check(d.LinkedEntityId == -1, "no links");
                EnemyCatalogue.Entry other = all.First(e => !e.FirstHunt && e.Type != zoomer.Type);
                EnemySpawnEntityData r = EnemyPlacement.ReplacedData(zoomer.Mph, other.Mph);
                Check(r.EnemyType == other.Type && r.Header.EntityId == zoomer.Mph.Header.EntityId && r.SpawnTotal == zoomer.Mph.SpawnTotal,
                    $"replaced {zoomer.Type} -> {r.EnemyType}, keeps id {r.Header.EntityId} and its counts");
            }
            if (all.FirstOrDefault(e => e.FirstHunt) is EnemyCatalogue.Entry fhe)
            {
                FhEnemySpawnEntityData f = EnemyPlacement.PlacedFhData(fhe.Fh, 20002, new Vector3(4, 0, 4), Vector3.UnitZ, 3, 2, 1);
                Check(f.Header.EntityId == 20002 && f.EnemyType == fhe.FhType && f.SpawnTotal == 3 && f.SpawnLimit == 2 && f.SpawnCount == 1,
                    $"FH {f.EnemyType} id {f.Header.EntityId} total {f.SpawnTotal} limit {f.SpawnLimit} count {f.SpawnCount}");
            }

            Live(roomName, all);
            Console.WriteLine(_fails == 0 ? "== ALL OK" : $"== {_fails} FAILED");
        }

        static void Steps(CampaignHost host, int frames)
        {
            var input = new CampaignInput();
            for (int i = 0; i < frames; i++)
            {
                host.Step(input);
            }
        }

        static void Live(string roomName, IReadOnlyList<EnemyCatalogue.Entry> all)
        {
            Console.WriteLine($"== live room {roomName}");
            string dir = Path.Combine(Path.GetTempPath(), "mph_enemyplacetest");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            EnemyPlacement.Install(dir);

            // first visit with no file: where the player stands, the room's own spawners and doors
            Vector3 start;
            var own = new List<(int Id, EnemyType Type)>();
            int doors, roomId;
            using (CampaignHost host = CampaignHost.Start(roomName))
            {
                Steps(host, 10);
                start = host.Player.Position;
                roomId = host.RoomId;
                foreach (EnemySpawnEntity s in host.Scene.GetEnemySpawnEntities()) own.Add((s.Id, s.Data.EnemyType));
                doors = 0;
                foreach (DoorEntity d in host.Scene.GetDoorEntities()) doors++;
            }
            Console.WriteLine($"  room {roomId}, player at {start}, {own.Count} own spawners ({String.Join(", ", own.Select(o => $"{o.Id} {o.Type}"))}), {doors} doors");

            EnemyCatalogue.Entry mph = all.First(e => !e.FirstHunt && e.Type is EnemyType.Zoomer or EnemyType.WarWasp or EnemyType.Geemer);
            EnemyCatalogue.Entry? fh = all.FirstOrDefault(e => e.FirstHunt);
            EnemyCatalogue.Entry wave2 = fh ?? mph;
            float[] At(float dx, float dz) => new[] { start.X + dx, start.Y, start.Z + dz };
            var file = new EnemyPlacement.FileData { Room = roomName, RoomId = roomId };
            file.Enemies.Add(new EnemyPlacement.PlacedEnemy { Id = mph.Id, Pos = At(3, 3), Trigger = "entry", Group = 0, HealthMul = 2f });
            file.Enemies.Add(new EnemyPlacement.PlacedEnemy { Id = wave2.Id, Pos = At(-3, 3), Trigger = "wave", Wave = 1, Group = 0 });
            file.Gates.Add(new EnemyPlacement.Gate { Group = 0 });
            if (own.Count > 0) file.RoomEdits.Add(new EnemyPlacement.RoomEdit { Id = own[0].Id, Type = own[0].Type.ToString(), Remove = true });
            if (own.Count > 1) file.RoomEdits.Add(new EnemyPlacement.RoomEdit { Id = own[1].Id, Type = own[1].Type.ToString(), HealthMul = 1.5f });
            string path = Path.Combine(dir, MphRecomp.Arenas.PickupPlacement.FileName(Metadata.GetRoomById(roomId)!.Name));
            File.WriteAllText(path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));

            using (CampaignHost host = CampaignHost.Start(roomName))
            {
                Scene scene = host.Scene;
                Check(own.Count == 0 || !scene.TryGetEntity(own[0].Id, out _), $"room's own spawner {(own.Count > 0 ? own[0].Id : -1)} removed");
                Check(scene.TryGetEntity(20000, out EntityBase? s0) && s0 is EnemySpawnEntity, "placed MPH spawner 20000 in the scene");
                Check(scene.TryGetEntity(20001, out EntityBase? s1) && (fh == null ? s1 is EnemySpawnEntity : s1 is FhEnemySpawnEntity),
                    "placed wave spawner 20001 in the scene" + (fh == null ? " (MPH: no First Hunt import)" : " (First Hunt)"));
                Steps(host, 90);
                var st = EnemyPlacement.DebugState();
                int placedAlive = 0;
                ushort health = 0, healthMax = 0;
                foreach (EnemyInstanceEntity e in scene.GetEnemyInstanceEntities())
                {
                    if (e.Owner?.Id == 20000) { placedAlive++; health = e.Health; healthMax = e.HealthMax; }
                }
                Check(placedAlive > 0, $"placed {mph.Label} spawned ({placedAlive} alive, health {health} of base {healthMax}: buff x2)");
                Check(health >= healthMax * 2 - 1 && healthMax > 0, "health buff applied");
                Check(st.Held == 1, $"wave 1 held while wave 0 lives (held {st.Held})");
                Check(st.GatePhases.Length == 1 && st.GatePhases[0] == 1, $"gate locked (phase {String.Join(",", st.GatePhases)})");
                int locked = 0;
                foreach (DoorEntity d in scene.GetDoorEntities()) if (d.Flags.TestFlag(DoorFlags.Locked)) locked++;
                Check(doors == 0 || locked > 0, $"{locked} of {doors} doors locked");

                // kill group 0 one wave at a time
                for (int round = 0; round < 6; round++)
                {
                    var targets = new List<EnemyInstanceEntity>();
                    foreach (EnemyInstanceEntity e in scene.GetEnemyInstanceEntities())
                    {
                        if (e.Owner?.Id is 20000 or 20001) targets.Add(e);
                    }
                    foreach (EnemyInstanceEntity e in targets) e.TakeDamage(5000, host.Player);
                    Steps(host, 90);
                    st = EnemyPlacement.DebugState();
                    Console.WriteLine($"  round {round}: killed {targets.Count}, held {st.Held}, gate phase {String.Join(",", st.GatePhases)}");
                    if (st.GatePhases[0] == 2) break;
                }
                Check(st.Held == 0, "wave 1 released after wave 0 died");
                Check(st.GatePhases[0] == 2, "gate opened after the group died");
                int stillLocked = 0;
                foreach (DoorEntity d in scene.GetDoorEntities()) if (d.Flags.TestFlag(DoorFlags.Locked)) stillLocked++;
                Console.WriteLine($"  doors locked now: {stillLocked} (the room's own locks stay)");

                // the editor's calls
                int before = EnemyPlacement.Current!.Enemies.Count;
                EntityBase? placed = EnemyPlacement.Place(mph, start + new Vector3(0, 0, 5), 180);
                Check(placed != null && EnemyPlacement.Current.Enemies.Count == before + 1, $"Place -> spawner {placed?.Id}");
                if (placed != null)
                {
                    int f = Array.IndexOf(EnemyPlacement.PlacedFields, "COUNT");
                    EntityBase now = EnemyPlacement.Adjust(placed, f, 1);
                    Check(now != placed && EnemyPlacement.FieldValue(now, f) == "2", $"Adjust COUNT -> {EnemyPlacement.FieldValue(now, f)} (spawner rebuilt {now.Id})");
                    int h = Array.IndexOf(EnemyPlacement.PlacedFields, "HEALTH");
                    now = EnemyPlacement.Adjust(now, h, 1);
                    Check(EnemyPlacement.FieldValue(now, h) == "x1.25", $"Adjust HEALTH -> {EnemyPlacement.FieldValue(now, h)}");
                    Check(EnemyPlacement.Nearest(now.Position, 1f)?.Spawner == now, "Nearest finds it");
                    Check(EnemyPlacement.Delete(now) && EnemyPlacement.Current.Enemies.Count == before, "Delete");
                }
                if (own.Count > 2 && scene.TryGetEntity(own[2].Id, out EntityBase? third) && third is EnemySpawnEntity)
                {
                    EntityBase r = EnemyPlacement.Adjust(third, Array.IndexOf(EnemyPlacement.RoomFields, "REPLACE"), 1);
                    Check(r != third && EnemyPlacement.EntryOf(r).Id == all[0].Id, $"room's own {own[2].Type} replaced with {EnemyPlacement.EntryOf(r).Label}");
                }
                Check(EnemyPlacement.LastError == null, "no errors: " + (EnemyPlacement.LastError ?? "none"));
            }

            EnemyPlacement.FileData back = JsonSerializer.Deserialize<EnemyPlacement.FileData>(File.ReadAllText(path))!;
            Check(back.Enemies.Count == 2 && back.Gates.Count == 1 && back.Enemies[0].HealthMul == 2f, $"saved file reads back: {back.Enemies.Count} enemies, {back.Gates.Count} gate, {back.RoomEdits.Count} room edits");
            Console.WriteLine($"  file: {path}");
            EnemyPlacement.Install(null);
        }
    }
}
