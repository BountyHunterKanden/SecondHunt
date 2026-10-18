using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRead.Formats.Culling;
using MphRecomp.Import.Retro;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Echoes arenas' weapon crates (docs/MP2_MULTIPLAYER_IMPORT.md section 13; owner 2026-10-08: real crates, a random
    // weapon from a fixed pool per arena, equal chance). Echoes places them as '[OUT] Weapon Crate' actors that a 15 s
    // timer turns on; each takes damage, breaks into a pickup, and comes back 30 s later. Here each is an MPH enemy
    // (EnemyInstanceEntity, the reused HitZone type: beams, bombs and alt attacks hurt it, players bump into it) that a
    // match adds to the arena's own entities (SceneSetup.HostExtraEntities). Its weapon is MphRead's own pickup
    // (ItemSpawnEntity.SpawnItem), picked with the gameplay RNG, so replays and the checksums see it like any other
    // drop. The device draws Echoes' crate model at each crate that's up (CampaignArena.cs, arena.bin's prop pass), lit
    // as Echoes lights it there (Lighting, worked out at import into arena.json).
    // Off unless a match turns them on: old replays (format < 4: Settings.Crates false) play without them. LAN and online
    // (0.2.0, owner 2026-10-11): the host's crates; clients mirror them (EchoesBreakables.Mirror, Net/NetWorld.cs).
    public static class EchoesCrates
    {
        sealed record Room(Vector3[] Pos, Vector3[] Floor, CrateLight?[] Light, ItemType[] Pool, float Scale);

        static readonly Dictionary<int, Room> _rooms = new();

        // this match has crates (set by CampaignHost.StartMatch before the arena loads)
        public static bool Enabled { get; set; }

        // on unless the settings turn them off (LAN + online too: the host's state reaches clients, Net/NetWorld.cs)
        public static bool For(MatchSettings settings) => settings.Crates ?? true;

        // the arena's crates can also drop the Pirate Blaster (owner 2026-10-15: every Echoes crate pool but the placed
        // affinity crates', PlacedPickups): with EXTRA PICKUPS, set by MatchStart (old replays: off, MatchSettings.CrateBlaster)
        public static bool Blaster { get; set; }

        // an arena registered (info) or gone (null): its crates, as arena.json gives them
        internal static void SetRoom(int roomId, EchoesArena.Info? info)
        {
            lock (_rooms)
            {
                var pool = new List<ItemType>();
                foreach (string name in info?.CrateWeapons ?? new List<string>())
                {
                    if (Enum.TryParse(name, out ItemType t) && t != ItemType.None) pool.Add(t);
                }
                if (info == null || info.Crates.Count == 0 || pool.Count == 0)
                {
                    _rooms.Remove(roomId);
                    return;
                }
                static Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
                static CrateLight? Light(EchoesArena.CrateSpot c)
                {
                    if (c.Ambient is not { Length: 3 } a || c.Lights == null) return null;
                    float[][] ls = c.Lights.Where(l => l.Length == 6).Take(4).ToArray();
                    return new CrateLight(V(a), ls.Select(l => new Vector3(l[0], l[1], l[2])).ToArray(), ls.Select(l => new Vector3(l[3], l[4], l[5])).ToArray());
                }
                _rooms[roomId] = new Room(info.Crates.Select(c => V(c.Pos)).ToArray(), info.Crates.Select(c => V(c.Floor)).ToArray(),
                    info.Crates.Select(Light).ToArray(), pool.ToArray(), info.Scale);
            }
        }

        // SceneSetup.HostExtraEntities: an Echoes arena's crates after its own entities, ids past theirs; the pool's
        // pickup models and the crate's break effect loaded with the room
        public static IReadOnlyList<EntityBase> Extra(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            if (!Enabled || !GameState.Multiplayer) return entities;
            Room? room;
            lock (_rooms)
            {
                if (!_rooms.TryGetValue(roomId, out room)) return entities;
            }
            var list = new List<EntityBase>(entities);
            int id = entities.Count == 0 ? 0 : entities.Max(e => e.Id) + 1;
            ItemType[] pool = Blaster && !room.Pool.Contains(ItemType.PirateBlaster) ? room.Pool.Append(ItemType.PirateBlaster).ToArray() : room.Pool;
            for (int i = 0; i < room.Pos.Length; i++)
            {
                list.Add(new EchoesCrateEntity(id++, room.Pos[i], room.Floor[i], room.Light[i], pool, room.Scale, scene));
            }
            foreach (ItemType t in pool) scene.LoadModel(Metadata.Items[(int)t]);
            scene.LoadEffect(EchoesCrateEntity.BreakEffect, persistent: false);
            return list;
        }

        // Echoes' lighting of a crate at p (area space, metres), as its actor's LightParameters ask (world lighting, 4
        // area lights, casts shadows) and Retro's actor lights pick them: the area's local ambient lights summed (colour
        // x brightness, each clamped), then the 4 lights that reach the crate's centre brightest. Each at the GX distance
        // attenuation Retro builds from its falloff and brightness q: 1 / (2/q), 1 / ((250/q) d), 1 / ((25000/q) d^2);
        // a spot's cone COS2-style about half its cutoff; a shadow-casting light the collision hides from the centre left
        // out. Out: the ambient, and per light [the way to it (MPH axes, unit), its colour at the crate]. The device
        // lights the crate's lit channel with them as directional lights (the crate is a 1 m cube).
        internal static (float[] Ambient, List<float[]> Lights) Lighting(IReadOnlyList<Mrea.Light> lights, Vector3 p,
            Func<float, float, float, Vector3> map, ArenaRaycaster ray, int max = 4)
        {
            Vector3 amb = Vector3.Zero;
            Vector3 at = map(p.X, p.Y, p.Z);
            var cand = new List<(float Strength, Vector3 To, Vector3 Col)>();
            foreach (Mrea.Light l in lights)
            {
                var col = new Vector3(l.Color[0], l.Color[1], l.Color[2]);
                float q = MathF.Max(l.Multiplier, 1.1920929e-7f);
                if (l.Type == Mrea.LightType.LocalAmbient)
                {
                    amb += Vector3.ComponentMin(col * q, Vector3.One);
                    continue;
                }
                var lp = new Vector3(l.Position[0], l.Position[1], l.Position[2]);
                var ld = new Vector3(l.Direction[0], l.Direction[1], l.Direction[2]);
                Vector3 to;
                float k = 1;
                if (l.Type == Mrea.LightType.Directional)
                {
                    if (ld.LengthSquared < 1e-8f) continue;
                    to = map(-ld.X, -ld.Y, -ld.Z).Normalized();
                }
                else
                {
                    float d = (lp - p).Length;
                    if (d < 1e-4f) continue;
                    float den = l.Falloff switch { 0 => 2 / q, 1 => 250 / q * d, _ => 25000 / q * d * d };
                    k = 1 / MathF.Max(den, 1e-6f);
                    if (l.Type == Mrea.LightType.Spot && ld.LengthSquared > 1e-8f)
                    {
                        float cr = MathF.Cos(MathHelper.DegreesToRadians(l.SpotCutoff / 2));
                        float c = Vector3.Dot((p - lp) / d, ld.Normalized());
                        k *= cr < 1 ? MathF.Max(c * (c - cr) / (1 - cr), 0) : 0;
                    }
                    Vector3 lpm = map(lp.X, lp.Y, lp.Z);
                    float len = (lpm - at).Length;
                    to = (lpm - at) / len;
                    if (MathF.Max(col.X, MathF.Max(col.Y, col.Z)) * k < 1 / 255f) continue;
                    if (l.CastShadows && ray.Cast(at, lpm) is { } hit && hit.T * len < len - 0.6f) continue;
                }
                float s = MathF.Max(col.X, MathF.Max(col.Y, col.Z)) * k;
                if (s >= 1 / 255f) cand.Add((s, to, col * k));
            }
            static float R(float v) => MathF.Round(v, 4);
            var res = cand.OrderByDescending(c => c.Strength).Take(max)
                .Select(c => new[] { R(c.To.X), R(c.To.Y), R(c.To.Z), R(c.Col.X), R(c.Col.Y), R(c.Col.Z) }).ToList();
            return (new[] { R(amb.X), R(amb.Y), R(amb.Z) }, res);
        }
    }

    // a crate's lighting on the device: ambient, and up to 4 directional lights (the way to each, MPH axes; its colour)
    public sealed record CrateLight(Vector3 Ambient, Vector3[] Dirs, Vector3[] Colors);

    public sealed class EchoesCrateEntity : EnemyInstanceEntity
    {
        // Echoes' timers (TIMR 'Delay to Spawn Crates' 15 s, 'Crate Respawn' 30 s) in MphRead's 60 Hz steps; the weapon
        // left behind lasts as long as MPH's own enemy drops (ItemSpawnEntity.SpawnItemDrop)
        public const int AppearFrames = 15 * 60, RespawnFrames = 30 * 60, DropFrames = 450 * 2;
        // AROT 'Spin Crates': one turn about the vertical in 5 s
        public const int SpinFrames = 5 * 60;
        // five uncharged Power Beam shots (MPH multiplayer: 6 damage each, Weapons.WeaponsMP "Power Beam MP")
        public const ushort FullHealth = 5 * 6;
        // what breaks: MPH's mechanical death (the force field lock's), and its sound
        public const int BreakEffect = 77; // deathMech1

        readonly Vector3 _floor;
        readonly ItemType[] _pool;
        readonly float _scale;
        int _timer = AppearFrames, _frame;

        public bool Up { get; private set; }
        public int Breaks { get; private set; }
        public ItemType LastDrop { get; private set; } = ItemType.None;
        // where its weapon appears: over the floor under it, as MPH's item spawners hold theirs (ItemSpawnEntity)
        public Vector3 DropPoint => _floor.AddY(0.65f);
        // about the vertical, for the draw
        public float Spin => MathHelper.TwoPi * (_frame % SpinFrames) / SpinFrames;
        // red while it was just hit (MphRead's enemies flash red for 10 steps: EnemyInstanceEntity.DrawGeneric)
        public bool Flash => Up && _timeSinceDamage < 5 * 2;
        // Echoes' lighting there, for the draw (null: lit white)
        public CrateLight? Light { get; }
        // the dev pickup placement editor (PickupPlacement.cs): its size, its weapons, and up on the next step instead of
        // after the 15 s delay
        public float Scale => _scale;
        public IReadOnlyList<ItemType> Pool => _pool;
        public void UpNow()
        {
            if (!Up) _timer = 1;
        }

        public EchoesCrateEntity(int id, Vector3 position, Vector3 floor, CrateLight? light, ItemType[] pool, float scale, Scene scene)
            : base(new EnemyInstanceEntityData(EnemyType.HitZone, null!), NodeRef.None, scene)
        {
            Id = id;
            Position = position;
            Light = light;
            _floor = floor;
            _pool = pool;
            _scale = scale;
        }

        protected override void EnemyInitialize()
        {
            NodeRef = _scene.GetNodeRefByName(ArenaModelWriter.RoomNode);
            // every beam does its full damage (Echoes: each vulnerability 100 %)
            Array.Fill(BeamEffectiveness, Effectiveness.Normal);
            _health = _healthMax = FullHealth;
            // the crate as drawn: Echoes' 1 m cube model at the actor's scale (1.5), centred on the actor (0.89 units
            // across here: arena.json's note), as an upright cylinder reaching most of the way to the spinning cube's
            // corners. (Echoes' own box, 1 x 1 x 1.5 m 0.25 m low, reaches past the model's bottom: not what's seen.)
            float half = 0.5f * _scale * EchoesArena.CrateActorScale;
            float radius = 1.2f * half;
            _hurtVolumeInit = new CollisionVolume(Vector3.UnitY, Position.AddY(-half), radius, 2 * half);
            _hurtVolume = _hurtVolumeInit;
            _boundingRadius = radius;
            Hide();
        }

        void Hide()
        {
            Up = false;
            Flags = EnemyFlags.NoMaxDistance | EnemyFlags.Static | EnemyFlags.Invincible;
        }

        void Appear()
        {
            Up = true;
            _health = _healthMax;
            _timeSinceDamage = 510;
            Flags = EnemyFlags.Visible | EnemyFlags.CollideBeam | EnemyFlags.CollidePlayer | EnemyFlags.NoMaxDistance | EnemyFlags.Static;
        }

        public override bool GetTargetable()
        {
            return Up && _health != 0;
        }

        // a network client's crate: the host's state (Net/NetWorld.cs); a break since the last one plays here
        int _netBreaks = -1;
        public void NetApply(bool up, bool hit, int breaks)
        {
            if (_netBreaks >= 0 && breaks != _netBreaks)
            {
                Breaks++;
                BreakFx();
                Hide();
            }
            _netBreaks = breaks;
            if (up && !Up) Appear();
            else if (!up && Up) Hide();
            if (hit && Up) _timeSinceDamage = 0;
        }

        public override bool Process()
        {
            _frame++;
            if (EchoesBreakables.Mirror)
            {
                // a client: no timers, breaks or drops of its own (the host's weapon comes as an item)
                if (!Up) return true;
                _health = _healthMax;
                return base.Process();
            }
            if (!Up)
            {
                // hidden: nothing to touch, hit or target until it's back
                if (--_timer <= 0) Appear();
                return true;
            }
            if (_health == 0)
            {
                Break();
                return true;
            }
            // health stays above 0 here, so MphRead's enemy step never removes it
            return base.Process();
        }

        void Break()
        {
            ItemType item = _pool[Rng.GetRandomInt2((uint)_pool.Length)];
            ItemSpawnEntity.SpawnItem(item, DropPoint, NodeRef, DropFrames, _scene);
            LastDrop = item;
            Breaks++;
            BreakFx();
            Hide();
            _health = _healthMax;
            _timer = RespawnFrames;
        }

        void BreakFx()
        {
            _scene.SpawnEffect(BreakEffect, GetTransformMatrix(Vector3.UnitZ, Vector3.UnitY, _hurtVolume.GetCenter()));
            int sfx = Metadata.EnemyDeathSfx[(int)EnemyType.ForceFieldLock];
            if (sfx != -1)
            {
                _soundSource.PlaySfx(sfx & ~0xA0000, noUpdate: true, recency: (sfx & 0x20000) != 0 ? Single.MaxValue : (sfx & 0x80000) != 0 ? 0 : -1,
                    sourceOnly: (sfx & 0x20000) != 0);
            }
        }

        // MphRead draws nothing for it (no DS model); the device draws Echoes' crate
        protected override bool EnemyGetDrawInfo()
        {
            return true;
        }
    }
}
