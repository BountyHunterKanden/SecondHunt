using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;

namespace MphRecomp.Multiplayer.Net
{
    // Online snapshots only (O5b host migration, docs/ONLINE_PLAY.md section 9b item 3): the item state only the host
    // runs, so a client that becomes the host carries on with it instead of starting over. Per item spawner (by entity id,
    // the same arena on every device): on or off, its cooldown and how many it spawned; per item lying in the arena (by
    // ItemSync id): its despawn timer (kill drops vanish after 15 s; spawner items never). A client only keeps the newest;
    // nothing is applied until it's promoted (CampaignHost.PromoteToHost). SnapshotDelta sends it XOR'd like the extras.
    public sealed class HostHandoff
    {
        public struct Spawner
        {
            public short Id;
            public bool Active;
            public ushort Cooldown, Count;
        }

        public Spawner[] Spawners = Array.Empty<Spawner>();
        // (ItemSync id, despawn timer: -1 = none)
        public (ushort Id, short Timer)[] Items = Array.Empty<(ushort, short)>();

        private const int SpawnerSize = 7, ItemSize = 4;

        // host: this frame's spawners, and the timers of the items the snapshot lists
        public static HostHandoff Capture(Scene scene, ItemSync sync)
        {
            var spawners = new List<Spawner>();
            foreach (EntityBase entity in scene.Entities)
            {
                if (entity is ItemSpawnEntity s)
                {
                    (ushort cooldown, ushort count) = s.HostSpawnState;
                    spawners.Add(new Spawner { Id = (short)s.Id, Active = s.Active, Cooldown = cooldown, Count = count });
                }
            }
            spawners.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new HostHandoff { Spawners = spawners.ToArray(), Items = sync.HostTimers() };
        }

        // the new host: the old host's spawners and drop timers on this scene (the items themselves are already here, as
        // ItemSync made them)
        public void Apply(Scene scene, ItemSync sync)
        {
            var byId = new Dictionary<int, Spawner>();
            foreach (Spawner s in Spawners)
            {
                byId[s.Id] = s;
            }
            foreach (EntityBase entity in scene.Entities)
            {
                if (entity is ItemSpawnEntity spawner && byId.TryGetValue(spawner.Id, out Spawner s))
                {
                    spawner.Active = s.Active;
                    spawner.HostSpawnState = (s.Cooldown, s.Count);
                }
            }
            foreach ((ushort id, short timer) in Items)
            {
                if (sync.LocalItem(id) is ItemInstanceEntity item && item.DespawnTimer != 0)
                {
                    item.DespawnTimer = timer;
                }
            }
        }

        public byte[] ToBytes()
        {
            byte[] b = new byte[2 + Spawners.Length * SpawnerSize + 2 + Items.Length * ItemSize];
            int o = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)Spawners.Length);
            o += 2;
            foreach (Spawner s in Spawners)
            {
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o), s.Id);
                b[o + 2] = (byte)(s.Active ? 1 : 0);
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 3), s.Cooldown);
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 5), s.Count);
                o += SpawnerSize;
            }
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)Items.Length);
            o += 2;
            foreach ((ushort id, short timer) in Items)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), id);
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 2), timer);
                o += ItemSize;
            }
            return b;
        }

        // null for an empty block (none sent)
        public static HostHandoff? FromBytes(ReadOnlySpan<byte> b)
        {
            if (b.Length < 4)
            {
                return null;
            }
            int o = 0;
            int spawners = BinaryPrimitives.ReadUInt16LittleEndian(b);
            o += 2;
            var h = new HostHandoff { Spawners = new Spawner[spawners] };
            for (int i = 0; i < spawners; i++)
            {
                h.Spawners[i] = new Spawner
                {
                    Id = BinaryPrimitives.ReadInt16LittleEndian(b[o..]),
                    Active = b[o + 2] != 0,
                    Cooldown = BinaryPrimitives.ReadUInt16LittleEndian(b[(o + 3)..]),
                    Count = BinaryPrimitives.ReadUInt16LittleEndian(b[(o + 5)..])
                };
                o += SpawnerSize;
            }
            int items = BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
            o += 2;
            h.Items = new (ushort, short)[items];
            for (int i = 0; i < items; i++)
            {
                h.Items[i] = (BinaryPrimitives.ReadUInt16LittleEndian(b[o..]), BinaryPrimitives.ReadInt16LittleEndian(b[(o + 2)..]));
                o += ItemSize;
            }
            return h;
        }
    }
}
