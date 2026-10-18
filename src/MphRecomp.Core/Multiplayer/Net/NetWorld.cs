using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using MphRecomp.Arenas;

namespace MphRecomp.Multiplayer.Net
{
    // The Echoes arenas' map elements as the host has them (owner 2026-10-11: "all the map elements in 0.2.0"): every
    // weapon crate and breakable (grate, wall) in entity id order, one byte each: bit 0 up, bit 1 just hit (its red
    // flash), bits 2-7 how often it broke (mod 64; a change = it broke since the last snapshot: the client plays the
    // break). Clients run them as mirrors (EchoesBreakables.Mirror): no hits, breaks, timers or drops of their own; the
    // weapon a crate leaves is an ordinary item the item sync already brings. LAN protocol 16 / online 15. The last
    // block of Snapshot.Write's bytes (online's SnapshotDelta carries it inside the mode block, which runs to the end).
    // Then Shooting Gallery's turret (a presence byte + Arenas/EchoesTurret.cs NetTurret; LAN protocol 21 / online 19):
    // clients mirror it (EchoesTurret.Mirror).
    public sealed class NetWorld
    {
        public const byte Up = 1, Hit = 2;
        public byte[] States = Array.Empty<byte>();
        public NetTurret? Turret;

        public int Size => 1 + States.Length + 1 + (Turret != null ? NetTurret.Size : 0);

        // the scene's crates + breakables in id order (the same ids on every device: SceneSetup.HostExtraEntities)
        public static List<EnemyInstanceEntity> Elements(Scene scene)
        {
            var list = new List<EnemyInstanceEntity>();
            foreach (EntityBase e in scene.Entities)
            {
                if (e is EchoesCrateEntity or EchoesBreakableEntity) list.Add((EnemyInstanceEntity)e);
            }
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        // the scene's turret (null: none)
        public static EchoesTurretEntity? TurretOf(Scene scene)
        {
            foreach (EntityBase e in scene.Entities)
            {
                if (e is EchoesTurretEntity t) return t;
            }
            return null;
        }

        // host: null when the arena has none
        public static NetWorld? Capture(Scene scene)
        {
            List<EnemyInstanceEntity> list = Elements(scene);
            EchoesTurretEntity? turret = TurretOf(scene);
            if (list.Count == 0 && turret == null) return null;
            var w = new NetWorld { States = new byte[Math.Min(list.Count, 255)], Turret = turret?.NetCapture() };
            for (int i = 0; i < w.States.Length; i++)
            {
                (bool up, bool hit, int breaks) = list[i] switch
                {
                    EchoesCrateEntity c => (c.Up, c.Flash, c.Breaks),
                    EchoesBreakableEntity b => (b.Up, b.Flash, b.Breaks),
                    _ => (false, false, 0)
                };
                w.States[i] = (byte)((up ? Up : 0) | (hit ? Hit : 0) | ((breaks & 63) << 2));
            }
            return w;
        }

        // client: each element takes the host's state
        public void Apply(Scene scene)
        {
            List<EnemyInstanceEntity> list = Elements(scene);
            for (int i = 0; i < list.Count && i < States.Length; i++)
            {
                byte s = States[i];
                bool up = (s & Up) != 0, hit = (s & Hit) != 0;
                int breaks = s >> 2;
                if (list[i] is EchoesCrateEntity c) c.NetApply(up, hit, breaks);
                else if (list[i] is EchoesBreakableEntity b) b.NetApply(up, hit, breaks);
            }
            if (Turret != null) TurretOf(scene)?.NetApply(Turret);
        }

        public int Write(Span<byte> dest)
        {
            dest[0] = (byte)States.Length;
            States.CopyTo(dest[1..]);
            int o = 1 + States.Length;
            dest[o++] = (byte)(Turret != null ? 1 : 0);
            Turret?.Write(dest[o..]);
            return Size;
        }

        public static NetWorld? Read(ReadOnlySpan<byte> src, out int size)
        {
            size = 0;
            if (src.Length < 1 || src.Length < 1 + src[0]) return null;
            size = 1 + src[0];
            var w = new NetWorld { States = src.Slice(1, src[0]).ToArray() };
            if (src.Length > size && src[size++] == 1 && src.Length >= size + NetTurret.Size)
            {
                w.Turret = NetTurret.Read(src[size..]);
                size += NetTurret.Size;
            }
            return w;
        }
    }
}
