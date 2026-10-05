using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer.Net
{
    // One item lying in the arena on the host: spawner items (Spawner = the ItemSpawn entity id) and kill drops (-1).
    public struct NetItem
    {
        public ushort Id; // the host's own number for it, unique while it exists
        public byte Type; // ItemType
        public short Spawner;
        public Vector3 Position;

        public const int Size = 17;

        public readonly void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, Id);
            d[2] = Type;
            BinaryPrimitives.WriteInt16LittleEndian(d[3..], Spawner);
            BinaryPrimitives.WriteSingleLittleEndian(d[5..], Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(d[9..], Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(d[13..], Position.Z);
        }

        public static NetItem Read(ReadOnlySpan<byte> d)
        {
            return new NetItem
            {
                Id = BinaryPrimitives.ReadUInt16LittleEndian(d),
                Type = d[2],
                Spawner = BinaryPrimitives.ReadInt16LittleEndian(d[3..]),
                Position = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d[5..]),
                    BinaryPrimitives.ReadSingleLittleEndian(d[9..]), BinaryPrimitives.ReadSingleLittleEndian(d[13..]))
            };
        }
    }

    // The host owns the items. It numbers every item in its arena and sends the list; a client (which never spawns
    // or drops items, and whose puppets never pick up -- PlayerEntity.NetClient) creates and removes items to match.
    // The client's own player still picks up at once for feel; the host's health/ammo confirm it, and an item this
    // device just took isn't put back while the host catches up.
    public sealed class ItemSync
    {
        // host
        private readonly Dictionary<ItemInstanceEntity, ushort> _ids = new(ReferenceEqualityComparer.Instance);
        private ushort _next = 1;
        private int _lastSignature;
        // client
        private readonly Dictionary<ushort, ItemInstanceEntity> _local = new();
        private readonly Dictionary<ushort, long> _takenHere = new();
        private const long TakenHereFrames = 90; // 1.5 s: the host has long seen the pickup by then

        public NetItem[] Capture(Scene scene, out bool changed)
        {
            var list = new List<NetItem>();
            var seen = new HashSet<ItemInstanceEntity>(ReferenceEqualityComparer.Instance);
            int signature = 17;
            foreach (ItemInstanceEntity item in scene.GetItemInstanceEntities())
            {
                if (item.DespawnTimer == 0)
                {
                    continue; // picked up or expiring this frame
                }
                if (!_ids.TryGetValue(item, out ushort id))
                {
                    id = _next++;
                    if (_next == 0)
                    {
                        _next = 1;
                    }
                    _ids[item] = id;
                }
                seen.Add(item);
                signature = signature * 31 + id;
                list.Add(new NetItem
                {
                    Id = id,
                    Type = (byte)item.ItemType,
                    Spawner = (short)(item.Owner?.Id ?? -1),
                    Position = item.Position
                });
            }
            if (_ids.Count != seen.Count)
            {
                var gone = new List<ItemInstanceEntity>();
                foreach (ItemInstanceEntity item in _ids.Keys)
                {
                    if (!seen.Contains(item))
                    {
                        gone.Add(item);
                    }
                }
                foreach (ItemInstanceEntity item in gone)
                {
                    _ids.Remove(item);
                }
            }
            changed = signature != _lastSignature;
            _lastSignature = signature;
            return list.ToArray();
        }

        public void Apply(Scene scene, NetItem[] items, long frame)
        {
            var present = new HashSet<ItemInstanceEntity>(ReferenceEqualityComparer.Instance);
            foreach (ItemInstanceEntity item in scene.GetItemInstanceEntities())
            {
                if (item.DespawnTimer != 0)
                {
                    present.Add(item);
                }
            }
            var hostIds = new HashSet<ushort>();
            foreach (NetItem n in items)
            {
                hostIds.Add(n.Id);
            }
            var drop = new List<ushort>();
            foreach ((ushort id, ItemInstanceEntity item) in _local)
            {
                if (!present.Contains(item))
                {
                    drop.Add(id);
                    _takenHere[id] = frame; // this device's player picked it up
                }
                else if (!hostIds.Contains(id))
                {
                    item.DespawnTimer = 0; // gone on the host: someone took it, or it expired
                    drop.Add(id);
                }
            }
            foreach (ushort id in drop)
            {
                _local.Remove(id);
            }
            foreach (NetItem n in items)
            {
                if (_local.ContainsKey(n.Id))
                {
                    continue;
                }
                if (_takenHere.TryGetValue(n.Id, out long taken) && frame - taken < TakenHereFrames)
                {
                    continue;
                }
                ItemInstanceEntity? item = ItemSpawnEntity.SpawnItem((ItemType)n.Type, n.Position,
                    scene.GetNodeRefByPosition(n.Position), despawnTime: 0, scene);
                if (item == null)
                {
                    continue;
                }
                if (n.Spawner >= 0 && scene.TryGetEntity(n.Spawner, out EntityBase? owner) && owner is ItemSpawnEntity spawner)
                {
                    item.Owner = spawner;
                    item.ParentId = spawner.Data.ParentId;
                    spawner.Item = item;
                }
                _local[n.Id] = item;
            }
            if (_takenHere.Count > 0)
            {
                var stale = new List<ushort>();
                foreach ((ushort id, long taken) in _takenHere)
                {
                    if (!hostIds.Contains(id) || frame - taken >= TakenHereFrames)
                    {
                        stale.Add(id);
                    }
                }
                foreach (ushort id in stale)
                {
                    _takenHere.Remove(id);
                }
            }
        }

        // for tests: the items this client shows, keyed like the host's
        public int LocalCount => _local.Count;
    }
}
