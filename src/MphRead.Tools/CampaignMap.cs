using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Text;

namespace MphRead
{
    // Dumps the whole Adventure campaign as data, straight from the ROM's own entity files: every story room
    // (ids 27-92) on every entity layer, the door/connector/teleporter graph between rooms, what locks each
    // exit (beam colour, bomb, message-only), every pickup, boss, artifact, octolith, force field, and the
    // message wiring between entities (who unlocks / activates what). Nothing here is hand-authored -- it is
    // the same data MphRead's DoorEntity / RoomEntity / TeleporterEntity read at runtime, resolved the same way.
    //   MphRead.Tools.dll -campaignmap [outDir]      (default <repo>/extract_out/campaign, gitignored)
    // Writes campaign_rooms.json (full per-room entity data), campaign_graph.json (resolved exits) and
    // campaign_map.md (readable summary per area).
    internal static class CampaignMap
    {
        private static readonly string[] _areaNames =
        {
            "Alinos 1", "Alinos 2", "Celestial Archives 1", "Celestial Archives 2",
            "Vesper Defense Outpost 1", "Vesper Defense Outpost 2", "Arcterra 1", "Arcterra 2", "Oubliette"
        };

        private static readonly string[] _layerNames = { "FirstVisit", "Escape", "Cleared", "Layer3" };

        private sealed class RoomDump
        {
            public RoomMetadata Meta = null!;
            public int Area;
            public IReadOnlyList<Entity> Entities = Array.Empty<Entity>();
        }

        private sealed class Exit
        {
            public int FromRoom;
            public string Kind = ""; // door / teleporter
            public short EntityId;
            public ushort LayerMask;
            public string Lock = "";
            public bool StartsLocked;
            public int ToRoom = -1;
            public int TargetLayer = -1; // door TargetLayerId (255 -> -1 = from boss flags)
            public short ArrivalEntityId = -1;
            public string ArrivalKind = "";
            public bool Reciprocal;
            public int ArtifactGate = -1; // teleporter ArtifactId < 8: needs 3 artifacts of that planet model
            public List<string> UnlockedBy = new List<string>();
        }

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string outDir = args.Length >= 2 ? args[1]
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign"));
            Directory.CreateDirectory(outDir);

            var rooms = new Dictionary<int, RoomDump>();
            for (int id = 27; id <= 92; id++)
            {
                RoomMetadata? meta = Metadata.GetRoomById(id);
                if (meta?.EntityPath == null)
                {
                    Console.WriteLine($"  room {id}: no metadata/entity path");
                    continue;
                }
                rooms[id] = new RoomDump
                {
                    Meta = meta,
                    Area = Metadata.GetAreaInfo(id),
                    Entities = Read.GetEntities(meta.EntityPath, layerId: -1, meta.FirstHunt)
                };
            }

            List<Exit> exits = ResolveExits(rooms);
            var incoming = BuildIncomingMessages(rooms);
            foreach (Exit exit in exits)
            {
                if (incoming.TryGetValue((exit.FromRoom, exit.EntityId), out List<string>? senders))
                {
                    exit.UnlockedBy.AddRange(senders.Where(s => s.Contains("Unlock")));
                }
            }

            File.WriteAllText(Path.Combine(outDir, "campaign_messages.md"), MessagesMarkdown(rooms));
            var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(Path.Combine(outDir, "campaign_rooms.json"), RoomsJson(rooms).ToJsonString(jsonOpts));
            File.WriteAllText(Path.Combine(outDir, "campaign_graph.json"), GraphJson(rooms, exits).ToJsonString(jsonOpts));
            File.WriteAllText(Path.Combine(outDir, "campaign_map.md"), Markdown(rooms, exits, incoming));

            int doors = exits.Count(e => e.Kind == "door");
            int teles = exits.Count(e => e.Kind == "teleporter");
            int unresolved = exits.Count(e => e.ToRoom < 0 || e.ArrivalEntityId < 0);
            int oneWay = exits.Count(e => e.ToRoom >= 0 && !e.Reciprocal);
            Console.WriteLine($"  rooms: {rooms.Count}, entities: {rooms.Values.Sum(r => r.Entities.Count)}");
            Console.WriteLine($"  exits: {doors} room-to-room doors, {teles} cross-room teleporters");
            Console.WriteLine($"  unresolved target/arrival: {unresolved}, no return path: {oneWay}");
            Console.WriteLine($"  wrote {outDir}");
        }

        // ---------------------------------------------------------------- graph

        private static List<Exit> ResolveExits(Dictionary<int, RoomDump> rooms)
        {
            var exits = new List<Exit>();
            foreach (RoomDump room in rooms.Values)
            {
                foreach (Entity entity in room.Entities)
                {
                    if (entity is Entity<DoorEntityData> door && door.Data.ConnectorId != 255)
                    {
                        // a connector-owning door: the corridor + the far "loader" door belong to this room,
                        // and the loader carries OutLoaderId to the arrival door (OutConnectorId) next door
                        DoorEntityData d = door.Data;
                        var exit = new Exit
                        {
                            FromRoom = room.Meta.Id, Kind = "door", EntityId = door.EntityId, LayerMask = door.LayerMask,
                            Lock = DoorLock(d.PaletteId, d.Locked != 0), StartsLocked = d.Locked != 0,
                            ToRoom = FindRoom(d.EntityFilename, 16),
                            TargetLayer = d.TargetLayerId == 255 ? -1 : d.TargetLayerId
                        };
                        if (exit.ToRoom >= 0 && rooms.TryGetValue(exit.ToRoom, out RoomDump? target))
                        {
                            foreach (Entity other in target.Entities)
                            {
                                if (other is Entity<DoorEntityData> arrival && arrival.Data.OutConnectorId == d.OutLoaderId)
                                {
                                    exit.ArrivalEntityId = arrival.EntityId;
                                    exit.ArrivalKind = "door";
                                    exit.Reciprocal = FindRoom(arrival.Data.EntityFilename, 16) == room.Meta.Id;
                                    break;
                                }
                            }
                        }
                        exits.Add(exit);
                    }
                    else if (entity is Entity<TeleporterEntityData> tele && !IsBlank(tele.Data.EntityFilename))
                    {
                        TeleporterEntityData t = tele.Data;
                        var exit = new Exit
                        {
                            FromRoom = room.Meta.Id, Kind = "teleporter", EntityId = tele.EntityId, LayerMask = tele.LayerMask,
                            Lock = t.ArtifactId < 8 ? $"3 artifacts (set {t.ArtifactId})" : "none",
                            StartsLocked = t.Active == 0,
                            ToRoom = FindRoom(t.EntityFilename, 15),
                            ArtifactGate = t.ArtifactId < 8 ? t.ArtifactId : -1
                        };
                        if (exit.ToRoom >= 0 && rooms.TryGetValue(exit.ToRoom, out RoomDump? target))
                        {
                            // PlayerProcess spawn rule: teleporter with LoadIndex == TargetIndex, else door OutConnectorId
                            Entity? arrival = target.Entities.FirstOrDefault(e =>
                                e is Entity<TeleporterEntityData> a && a.Data.LoadIndex == t.TargetIndex)
                                ?? target.Entities.FirstOrDefault(e =>
                                e is Entity<DoorEntityData> a && a.Data.OutConnectorId == t.TargetIndex);
                            if (arrival != null)
                            {
                                exit.ArrivalEntityId = arrival.EntityId;
                                exit.ArrivalKind = arrival.Type == EntityType.Door ? "door" : "teleporter";
                                exit.Reciprocal = arrival is Entity<TeleporterEntityData> back
                                    && FindRoom(back.Data.EntityFilename, 15) == room.Meta.Id;
                            }
                        }
                        exits.Add(exit);
                    }
                }
            }
            // a door pair is reciprocal if either side points back (the arrival door need not own a connector)
            foreach (Exit exit in exits.Where(e => e.Kind == "door" && !e.Reciprocal && e.ToRoom >= 0))
            {
                exit.Reciprocal = exits.Any(o => o.Kind == "door" && o.FromRoom == exit.ToRoom && o.ToRoom == exit.FromRoom);
            }
            return exits;
        }

        // same rule as DoorEntity/TeleporterEntity: first 15 chars of the entity filename prefix-match a room
        private static int FindRoom(char[] filename, int length)
        {
            if (filename.Length == 0 || filename[0] == '\0')
            {
                return -1;
            }
            ReadOnlySpan<char> key = filename.AsSpan(0, Math.Min(15, length));
            int nul = key.IndexOf('\0');
            if (nul >= 0)
            {
                key = key[..nul];
            }
            foreach (RoomMetadata room in Metadata.RoomList)
            {
                if (room.EntityFilename != null
                    && room.EntityFilename.AsSpan().StartsWith(key, StringComparison.InvariantCultureIgnoreCase))
                {
                    return room.Id;
                }
            }
            return -1;
        }

        // BeamProjectileEntity/BombEntity rules: an unlocked door opens to any shot. A locked door opens to the
        // matching beam (palette 0-7), a bomb/touch (8), or -- palette 9, the plain "any beam" door, which gets
        // the ShowLock flag -- only to an Unlock message from another entity.
        // the game only checks [0] != ' '; a space-padded name then matches no room -> in-room teleport
        private static bool IsBlank(char[] filename) => filename.MarshalString().Trim().Length == 0;

        private static string DoorLock(uint paletteId, bool locked) => !locked ? "open (any beam)" : paletteId switch
        {
            < 8 => ((BeamType)paletteId).ToString(),
            8 => "Bomb",
            9 => "Locked until Unlock message",
            _ => $"palette{paletteId}"
        };

        private static string ForceFieldLock(uint type) => type < 8 ? ((BeamType)type).ToString() : type == 9 ? "MessageOnly" : $"type{type}";

        // ---------------------------------------------------------------- messages

        private readonly struct MsgEdge
        {
            public readonly string When;
            public readonly Message Message;
            public readonly int Target;
            public readonly int Param1;

            public MsgEdge(string when, Message message, int target, int param1 = 0)
            {
                When = when;
                Message = message;
                Target = target;
                Param1 = param1;
            }
        }

        private static IEnumerable<MsgEdge> Messages(Entity entity)
        {
            switch (entity)
            {
            case Entity<TriggerVolumeEntityData> t:
                yield return new MsgEdge("trigger->parent", t.Data.ParentMessage, t.Data.ParentId, t.Data.ParentMsgParam1);
                yield return new MsgEdge("trigger->child", t.Data.ChildMessage, t.Data.ChildId, t.Data.ChildMsgParam1);
                break;
            case Entity<AreaVolumeEntityData> a:
                yield return new MsgEdge("inside", a.Data.InsideMessage, a.Data.ParentId, a.Data.InsideMsgParam1);
                yield return new MsgEdge("exit", a.Data.ExitMessage, a.Data.ChildId, a.Data.ExitMsgParam1);
                break;
            case Entity<EnemySpawnEntityData> s:
                yield return new MsgEdge("spawner-done", s.Data.Message1, s.Data.EntityId1);
                yield return new MsgEdge("spawner-done", s.Data.Message2, s.Data.EntityId2);
                yield return new MsgEdge("spawner-done", s.Data.Message3, s.Data.EntityId3);
                break;
            case Entity<ItemSpawnEntityData> i:
                yield return new MsgEdge("collected", i.Data.CollectedMessage, i.Data.NotifyEntityId, i.Data.CollectedMsgParam1);
                break;
            case Entity<ArtifactEntityData> a:
                yield return new MsgEdge("picked-up", a.Data.Message1, a.Data.Message1Target);
                yield return new MsgEdge("picked-up", a.Data.Message2, a.Data.Message2Target);
                yield return new MsgEdge("picked-up", a.Data.Message3, a.Data.Message3Target);
                break;
            case Entity<ObjectEntityData> o:
                yield return new MsgEdge("scanned", o.Data.ScanMessage, o.Data.ScanMsgTarget);
                break;
            case Entity<CameraSequenceEntityData> c:
                yield return new MsgEdge("camseq-end", c.Data.EndMessage, c.Data.EndMessageTargetId, c.Data.EndMessageParam);
                break;
            case Entity<PlatformEntityData> p:
                yield return new MsgEdge("scanned", p.Data.ScanMessage, p.Data.ScanMsgTarget);
                yield return new MsgEdge("beam-hit", p.Data.BeamHitMessage, p.Data.BeamHitMsgTarget, p.Data.BeamHitMsgParam1);
                yield return new MsgEdge("player-touch", p.Data.PlayerColMessage, p.Data.PlayerColMsgTarget, p.Data.PlayerColMsgParam1);
                yield return new MsgEdge("destroyed", p.Data.DeadMessage, p.Data.DeadMsgTarget, p.Data.DeadMsgParam1);
                yield return new MsgEdge("lifetime", p.Data.LifetimeMessage1, p.Data.LifetimeMsg1Target, p.Data.LifetimeMsg1Param1);
                yield return new MsgEdge("lifetime", p.Data.LifetimeMessage2, p.Data.LifetimeMsg2Target, p.Data.LifetimeMsg2Param1);
                yield return new MsgEdge("lifetime", p.Data.LifetimeMessage3, p.Data.LifetimeMsg3Target, p.Data.LifetimeMsg3Param1);
                yield return new MsgEdge("lifetime", p.Data.LifetimeMessage4, p.Data.LifetimeMsg4Target, p.Data.LifetimeMsg4Param1);
                break;
            }
        }

        private static string Label(RoomDump room, int entityId)
        {
            Entity? e = room.Entities.FirstOrDefault(x => x.EntityId == entityId);
            return e == null ? (entityId < 0 ? "global" : $"#{entityId}?") : $"{e.Type}#{entityId}";
        }

        // (room, target entity id) -> "Message from Source#id (when)"
        private static Dictionary<(int, short), List<string>> BuildIncomingMessages(Dictionary<int, RoomDump> rooms)
        {
            var incoming = new Dictionary<(int, short), List<string>>();
            foreach (RoomDump room in rooms.Values)
            {
                foreach (Entity entity in room.Entities)
                {
                    foreach (MsgEdge edge in Messages(entity))
                    {
                        if (edge.Message == Message.None || edge.Target < 0)
                        {
                            continue;
                        }
                        var key = (room.Meta.Id, (short)edge.Target);
                        if (!incoming.TryGetValue(key, out List<string>? list))
                        {
                            incoming[key] = list = new List<string>();
                        }
                        list.Add($"{edge.Message} from {entity.Type}#{entity.EntityId} ({edge.When})");
                    }
                }
            }
            return incoming;
        }

        // ---------------------------------------------------------------- output

        private static string Layers(ushort mask)
        {
            if (mask == 0xFFFF)
            {
                return "All";
            }
            var names = new List<string>();
            for (int i = 0; i < 16; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    names.Add(i < _layerNames.Length ? _layerNames[i] : $"L{i}");
                }
            }
            return names.Count == 0 ? "none" : String.Join(",", names);
        }

        private static string RoomLabel(Dictionary<int, RoomDump> rooms, int id)
        {
            if (id < 0)
            {
                return "?";
            }
            if (rooms.TryGetValue(id, out RoomDump? r))
            {
                return $"{r.Meta.InGameName} [{id} {r.Meta.Name}]";
            }
            RoomMetadata? meta = Metadata.GetRoomById(id);
            return meta == null ? $"room {id}" : $"{meta.InGameName} [{id} {meta.Name}]";
        }

        private static JsonObject EntityJson(RoomDump room, Entity entity)
        {
            var o = new JsonObject
            {
                ["id"] = entity.EntityId,
                ["type"] = entity.Type.ToString(),
                ["layers"] = Layers(entity.LayerMask),
                ["layerMask"] = entity.LayerMask,
                ["node"] = entity.NodeName,
                ["pos"] = new JsonArray(Round(entity.Position.X), Round(entity.Position.Y), Round(entity.Position.Z))
            };
            switch (entity)
            {
            case Entity<DoorEntityData> d:
                o["doorType"] = d.Data.DoorType.ToString();
                o["lock"] = DoorLock(d.Data.PaletteId, d.Data.Locked != 0);
                o["palette"] = d.Data.PaletteId;
                o["locked"] = d.Data.Locked != 0;
                o["connectorId"] = d.Data.ConnectorId == 255 ? null : (int)d.Data.ConnectorId;
                o["targetRoom"] = FindRoom(d.Data.EntityFilename, 16);
                o["targetFile"] = d.Data.EntityFilename.MarshalString();
                o["targetLayer"] = d.Data.TargetLayerId == 255 ? null : d.Data.TargetLayerId;
                o["outConnectorId"] = d.Data.OutConnectorId;
                o["outLoaderId"] = d.Data.OutLoaderId;
                break;
            case Entity<TeleporterEntityData> t:
                o["loadIndex"] = t.Data.LoadIndex;
                o["targetIndex"] = t.Data.TargetIndex;
                o["artifactId"] = t.Data.ArtifactId;
                o["active"] = t.Data.Active != 0;
                o["invisible"] = t.Data.Invisible != 0;
                o["targetRoom"] = FindRoom(t.Data.EntityFilename, 15);
                o["targetFile"] = t.Data.EntityFilename.MarshalString();
                break;
            case Entity<ItemSpawnEntityData> i:
                o["item"] = i.Data.ItemType.ToString();
                o["enabled"] = i.Data.Enabled != 0;
                o["alwaysActive"] = i.Data.AlwaysActive != 0;
                o["maxSpawnCount"] = i.Data.MaxSpawnCount;
                break;
            case Entity<ArtifactEntityData> a:
                o["kind"] = a.Data.ModelId < 8 ? "artifact" : "octolith";
                o["modelId"] = a.Data.ModelId;
                o["artifactId"] = a.Data.ArtifactId;
                o["active"] = a.Data.Active != 0;
                break;
            case Entity<ForceFieldEntityData> f:
                o["lock"] = ForceFieldLock(f.Data.Type);
                o["active"] = f.Data.Active != 0;
                break;
            case Entity<EnemySpawnEntityData> s:
                o["enemy"] = s.Data.EnemyType.ToString();
                o["active"] = s.Data.Active != 0;
                o["alwaysActive"] = s.Data.AlwaysActive != 0;
                o["spawnTotal"] = s.Data.SpawnTotal;
                o["dropItem"] = s.Data.ItemType.ToString();
                if (s.Data.EnemyType == EnemyType.Hunter)
                {
                    EnemySpawnFields09 h = s.Data.Fields.S09;
                    o["hunterId"] = h.HunterId;
                    o["encounterType"] = h.EncounterType;
                    o["hunterChance"] = h.HunterChance;
                }
                break;
            case Entity<TriggerVolumeEntityData> t:
                o["subtype"] = t.Data.Subtype.ToString();
                o["active"] = t.Data.Active != 0;
                o["alwaysActive"] = t.Data.AlwaysActive != 0;
                o["oneShot"] = t.Data.DeactivateAfterUse != 0;
                o["triggerFlags"] = t.Data.TriggerFlags.ToString();
                if (t.Data.Subtype == TriggerType.StateBits)
                {
                    o["requiredStateBit"] = t.Data.RequiredStateBit;
                }
                if (t.Data.Subtype == TriggerType.Threshold)
                {
                    o["threshold"] = t.Data.TriggerThreshold;
                }
                break;
            case Entity<AreaVolumeEntityData> a:
                o["active"] = a.Data.Active != 0;
                o["alwaysActive"] = a.Data.AlwaysActive != 0;
                break;
            case Entity<ObjectEntityData> ob:
                o["modelId"] = ob.Data.ModelId;
                o["scanId"] = ob.Data.ScanId;
                break;
            case Entity<PlatformEntityData> p:
                o["modelId"] = p.Data.ModelId;
                o["active"] = p.Data.Active != 0;
                o["flags"] = p.Data.Flags.ToString();
                break;
            case Entity<CameraSequenceEntityData> c:
                o["sequenceId"] = c.Data.SequenceId;
                o["blockInput"] = c.Data.BlockInput != 0;
                break;
            }
            var msgs = new JsonArray();
            foreach (MsgEdge edge in Messages(entity))
            {
                if (edge.Message != Message.None)
                {
                    msgs.Add(new JsonObject
                    {
                        ["when"] = edge.When,
                        ["message"] = edge.Message.ToString(),
                        ["target"] = edge.Target < 0 ? "global" : Label(room, edge.Target),
                        ["param1"] = edge.Param1
                    });
                }
            }
            if (msgs.Count > 0)
            {
                o["sends"] = msgs;
            }
            return o;
        }

        private static float Round(float v) => MathF.Round(v, 2);

        private static JsonObject RoomsJson(Dictionary<int, RoomDump> rooms)
        {
            var root = new JsonObject();
            foreach (RoomDump room in rooms.Values.OrderBy(r => r.Meta.Id))
            {
                var entities = new JsonArray();
                foreach (Entity e in room.Entities)
                {
                    entities.Add(EntityJson(room, e));
                }
                root[room.Meta.Id.ToString()] = new JsonObject
                {
                    ["name"] = room.Meta.Name,
                    ["inGameName"] = room.Meta.InGameName,
                    ["area"] = room.Area,
                    ["areaName"] = _areaNames[room.Area],
                    ["entityFile"] = room.Meta.EntityFilename,
                    ["entities"] = entities
                };
            }
            return root;
        }

        private static JsonObject GraphJson(Dictionary<int, RoomDump> rooms, List<Exit> exits)
        {
            var nodes = new JsonArray();
            foreach (RoomDump room in rooms.Values.OrderBy(r => r.Meta.Id))
            {
                nodes.Add(new JsonObject
                {
                    ["id"] = room.Meta.Id,
                    ["name"] = room.Meta.Name,
                    ["inGameName"] = room.Meta.InGameName,
                    ["area"] = room.Area,
                    ["areaName"] = _areaNames[room.Area]
                });
            }
            var edges = new JsonArray();
            foreach (Exit e in exits)
            {
                edges.Add(new JsonObject
                {
                    ["from"] = e.FromRoom,
                    ["to"] = e.ToRoom,
                    ["kind"] = e.Kind,
                    ["entity"] = e.EntityId,
                    ["layers"] = Layers(e.LayerMask),
                    ["lock"] = e.Lock,
                    ["startsLocked"] = e.StartsLocked,
                    ["targetLayer"] = e.TargetLayer < 0 ? null : e.TargetLayer,
                    ["arrival"] = e.ArrivalEntityId < 0 ? null : $"{e.ArrivalKind}#{e.ArrivalEntityId}",
                    ["reciprocal"] = e.Reciprocal,
                    ["unlockedBy"] = new JsonArray(e.UnlockedBy.Select(s => (JsonNode)s).ToArray())
                });
            }
            return new JsonObject { ["rooms"] = nodes, ["exits"] = edges };
        }

        private static string Markdown(Dictionary<int, RoomDump> rooms, List<Exit> exits,
            Dictionary<(int, short), List<string>> incoming)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Campaign map (generated by `-campaignmap` from the ROM's entity files)");
            sb.AppendLine();
            sb.AppendLine("Layers: FirstVisit = area not yet cleared, Escape = boss dead/escape running, Cleared = after re-entering the ship.");
            sb.AppendLine("Lock: open = any shot opens it; beam name = locked, shoot with that weapon; Bomb = bomb/touch; 'Locked until Unlock message' = another entity (trigger, spawner, scan, pickup) must send Unlock. Lock state can also change at runtime (Lock/Unlock messages, hunter encounters).");
            foreach (IGrouping<int, RoomDump> area in rooms.Values.OrderBy(r => r.Meta.Id).GroupBy(r => r.Area))
            {
                sb.AppendLine();
                sb.AppendLine($"## {_areaNames[area.Key]}");
                foreach (RoomDump room in area)
                {
                    sb.AppendLine();
                    sb.AppendLine($"### {room.Meta.Id} {room.Meta.InGameName} (`{room.Meta.Name}`)");
                    foreach (Exit e in exits.Where(x => x.FromRoom == room.Meta.Id))
                    {
                        string gate = e.Lock + (e.StartsLocked ? ", starts locked" : "");
                        string unlock = e.UnlockedBy.Count > 0 ? $" -- unlocked by {String.Join("; ", e.UnlockedBy)}" : "";
                        string arrive = e.ArrivalEntityId >= 0 ? $" at {e.ArrivalKind}#{e.ArrivalEntityId}" : " (NO ARRIVAL FOUND)";
                        string back = e.Reciprocal ? "" : " (one-way)";
                        string layer = e.TargetLayer >= 0 ? $", forces layer {e.TargetLayer}" : "";
                        sb.AppendLine($"- {e.Kind}#{e.EntityId} [{Layers(e.LayerMask)}] -> {RoomLabel(rooms, e.ToRoom)}{arrive}{back}; lock: {gate}{layer}{unlock}");
                    }
                    foreach (Entity ent in room.Entities)
                    {
                        string? line = ent switch
                        {
                            Entity<ItemSpawnEntityData> i when IsProgressionItem(i.Data.ItemType) => $"item {i.Data.ItemType}",
                            Entity<ArtifactEntityData> a => a.Data.ModelId < 8 ? $"artifact (model {a.Data.ModelId}, #{a.Data.ArtifactId})" : $"octolith (area bit {a.Data.ArtifactId})",
                            Entity<ForceFieldEntityData> f => $"force field, lock {ForceFieldLock(f.Data.Type)}{(f.Data.Active != 0 ? "" : ", starts off")}",
                            Entity<EnemySpawnEntityData> s when IsBossOrHunter(s.Data.EnemyType) => $"spawner {s.Data.EnemyType}"
                                + (s.Data.EnemyType == EnemyType.Hunter ? $" (hunter {s.Data.Fields.S09.HunterId}, encounter {s.Data.Fields.S09.EncounterType})" : ""),
                            Entity<PlatformEntityData> p when (p.Data.Flags & PlatformFlags.SamusShip) != 0 => "gunship",
                            Entity<TeleporterEntityData> t when IsBlank(t.Data.EntityFilename) => $"in-room teleporter (load {t.Data.LoadIndex} -> {t.Data.TargetIndex}){(t.Data.Active != 0 ? "" : ", starts off")}",
                            Entity<DoorEntityData> d when d.Data.ConnectorId == 255 => $"in-room door ({d.Data.DoorType}), lock: {DoorLock(d.Data.PaletteId, d.Data.Locked != 0)}",
                            _ => null
                        };
                        if (line == null)
                        {
                            continue;
                        }
                        string unlock = incoming.TryGetValue((room.Meta.Id, ent.EntityId), out List<string>? from)
                            ? $" -- receives {String.Join("; ", from)}" : "";
                        sb.AppendLine($"- {ent.Type}#{ent.EntityId} [{Layers(ent.LayerMask)}]: {line}{unlock}");
                    }
                    foreach (Entity ent in room.Entities)
                    {
                        foreach (MsgEdge edge in Messages(ent))
                        {
                            if (edge.Target < 0 && IsGlobalStoryMessage(edge.Message))
                            {
                                sb.AppendLine($"- {ent.Type}#{ent.EntityId} [{Layers(ent.LayerMask)}] {edge.When}: {edge.Message} (param {edge.Param1})");
                            }
                        }
                    }
                }
            }
            return sb.ToString();
        }

        // every prompt / warning / overlay an entity can put on screen, with its text -- the tutorial and gating
        // flow as the player sees it (ShowPrompt param2: 0 = OK dialog, 1 = YES/NO)
        private static string MessagesMarkdown(Dictionary<int, RoomDump> rooms)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# On-screen messages sent by room entities (generated by `-campaignmap`)");
            foreach (RoomDump room in rooms.Values.OrderBy(r => r.Meta.Id))
            {
                bool header = false;
                foreach (Entity ent in room.Entities)
                {
                    foreach (MsgEdge edge in Messages(ent))
                    {
                        if (edge.Message is not (Message.ShowPrompt or Message.ShowOverlay or Message.ShowWarning))
                        {
                            continue;
                        }
                        if (!header)
                        {
                            sb.AppendLine();
                            sb.AppendLine($"## {room.Meta.Id} {room.Meta.InGameName ?? room.Meta.Name}");
                            header = true;
                        }
                        StringTableEntry? entry = Strings.GetEntry('M', edge.Param1, StringTables.GameMessages);
                        string text = entry == null ? "(no text)" : $"{entry.String1} {entry.String2}".Replace('\n', ' ').Trim();
                        sb.AppendLine($"- {ent.Type}#{ent.EntityId} [{Layers(ent.LayerMask)}] {edge.When}: {edge.Message} {edge.Param1} -- \"{text}\"");
                    }
                }
            }
            return sb.ToString();
        }

        private static bool IsProgressionItem(ItemType type) => type switch
        {
            ItemType.EnergyTank or ItemType.MissileExpansion or ItemType.UAExpansion or ItemType.ArtifactKey
                or ItemType.VoltDriver or ItemType.Battlehammer or ItemType.Imperialist or ItemType.Judicator
                or ItemType.Magmaul or ItemType.ShockCoil or ItemType.OmegaCannon => true,
            _ => false
        };

        private static bool IsBossOrHunter(EnemyType type) => type is EnemyType.Cretaphid or EnemyType.Slench
            or EnemyType.Gorea1A or EnemyType.Gorea2 or EnemyType.Hunter;

        private static bool IsGlobalStoryMessage(Message message) => message is Message.ShipHatch or Message.Checkpoint
            or Message.UnlockOubliette or Message.LoadOubliette or Message.EscapeUpdate1 or Message.EscapeUpdate2
            or Message.SetTriggerState or Message.ClearTriggerState or Message.Complete;
    }
}
