using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRead.Formats.Culling;
using MphRead.Utility;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Echoes arenas' breakable terrain and Dark Water (docs/MP2_MULTIPLAYER_IMPORT.md section 14; owner 2026-10-08, MPH
    // rules first). Shooting Gallery's 8 'Grating' actors over its middle Dark Water pool break and come back 10 s later
    // (Echoes' RESET GRILLE timer); Crossfire Chaos's 4 'wall' platforms break and stay broken. Each takes Echoes'
    // multiplayer hit counts (TweakPlayerGunMuli: Power Beam 1, charged 15, bomb 15, missile 20; grate health 5, wall 20)
    // in MPH health: a grate 30, a wall 120; a hit is Damage()'s. Each is an MPH enemy that MphRead's own damage never
    // reaches (Invincible, no hurt volume in play): players stand on and bump into its box through the entity
    // collision MphRead's platforms use (CollisionDetection.HostEntityCollision), beams that hit the box message it
    // (BeamCollideWith), and it looks for exploding bombs itself. Dark Water is MPH's acid floor: 1 energy on entering
    // and every 4 MPH ticks (8 steps) while standing in it, invulnerability ignored (PlayerEntity's OnAcid rule), from
    // one more hidden entity (EchoesFluidEntity). Off unless a match turns them on: old replays (format < 5: Settings.
    // Breakables false) play without them. LAN and online (0.2.0): the host's; clients mirror them (Net/NetWorld.cs) and
    // take Dark Water's damage from the host as any other.
    public static class EchoesBreakables
    {
        sealed record Spot(Vector3 Min, Vector3 Max, Vector3 Half, float Yaw, int Health, bool AnyBomb, int Respawn, int Pass, CrateLight? Light);
        sealed record Pool(Vector3 Min, Vector3 Max, float Surface);
        sealed record Room(Spot[] Spots, Pool[] Pools);

        static readonly Dictionary<int, Room> _rooms = new();

        // this match has them (set by MatchStart before the arena loads)
        public static bool Enabled { get; set; }

        // on unless the settings turn them off (LAN + online too: the host's state reaches clients, Net/NetWorld.cs)
        public static bool For(MatchSettings settings) => settings.Breakables ?? true;

        // a network client: crates and breakables follow the host's snapshots (Net/NetWorld.cs) instead of their own hits,
        // timers and drops (set by MatchStart; off again when this device becomes the host, O5b)
        public static bool Mirror { get; set; }

        // Echoes' multiplayer hit counts in MPH health: one uncharged Power Beam shot (MPH multiplayer: 6) per Echoes point
        public const int Unit = 6;
        public const int GrateHealth = 5 * Unit, WallHealth = 20 * Unit;
        // Echoes' 'RESET GRILLE' timer (10 s) in MphRead's 60 Hz steps; walls stay broken (0)
        public const int GrateRespawn = 10 * 60;

        // an arena registered (info) or gone (null): its breakables and pools, as arena.json gives them
        internal static void SetRoom(int roomId, EchoesArena.Info? info)
        {
            lock (_rooms)
            {
                // (only the Dark Water hurts: Pipeline's water is EchoesMovement's)
                List<EchoesArena.FluidSpot> dark = info?.Fluids?.Where(f => f.Kind == "darkwater").ToList() ?? new();
                if (info == null || ((info.Breakables?.Count ?? 0) == 0 && dark.Count == 0))
                {
                    _rooms.Remove(roomId);
                    return;
                }
                static Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
                static CrateLight? Light(EchoesArena.BreakSpot b)
                {
                    if (b.Ambient is not { Length: 3 } a || b.Lights == null) return null;
                    float[][] ls = b.Lights.Where(l => l.Length == 6).Take(4).ToArray();
                    return new CrateLight(V(a), ls.Select(l => new Vector3(l[0], l[1], l[2])).ToArray(), ls.Select(l => new Vector3(l[3], l[4], l[5])).ToArray());
                }
                // a turned one's box: its own size and turn; else the axis-aligned Min/Max
                static Vector3 Half(EchoesArena.BreakSpot b) => b.Size is { Length: 3 } z ? V(z) / 2 : (V(b.Max) - V(b.Min)) / 2;
                Spot[] spots = (info.Breakables ?? new()).Select(b => new Spot(V(b.Min), V(b.Max), Half(b), b.Size is { Length: 3 } ? b.Yaw ?? 0 : 0, b.Health, b.Kind == "grate", b.RespawnFrames, b.Pass, Light(b))).ToArray();
                Pool[] pools = dark.Select(f => new Pool(V(f.Min), V(f.Max), f.Surface)).ToArray();
                _rooms[roomId] = new Room(spots, pools);
            }
        }

        // SceneSetup.HostExtraEntities (after the crates): an Echoes arena's breakables, then one entity for its pools
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
            for (int i = 0; i < room.Spots.Length; i++)
            {
                Spot s = room.Spots[i];
                list.Add(new EchoesBreakableEntity(id++, i, s.Min, s.Max, s.Half, s.Yaw, s.Health, s.AnyBomb, s.Respawn, s.Pass, s.Light, scene));
            }
            if (room.Pools.Length > 0)
            {
                list.Add(new EchoesFluidEntity(id++, room.Pools.Select(p => (p.Min, p.Max, p.Surface)).ToArray(), scene));
            }
            if (room.Spots.Length > 0) scene.LoadEffect(EchoesCrateEntity.BreakEffect, persistent: false);
            return list;
        }

        // a beam's hit in breakable health: Echoes' multiplayer weights on MPH's Power Beam and Missile (an uncharged
        // shot 1 point = 6, a full charge 15 points = 90, partial charges in between on MPH's own charge curve, any
        // missile 20 points = 120); every other MPH weapon its own MPH damage
        public static int Damage(BeamType beam, float damage, bool charged)
        {
            if (beam == BeamType.Missile) return 20 * Unit;
            if (beam == BeamType.PowerBeam && charged)
            {
                // MPH multiplayer Power Beam: 6 at the least charge, 36 full (Weapons.WeaponsMP)
                float t = Math.Clamp((damage - 6) / 30f, 0, 1);
                return (int)MathF.Round(Unit + t * (15 * Unit - Unit));
            }
            return Math.Max(1, (int)MathF.Round(damage));
        }

        // a bomb's hit: 15 points (90); a grate breaks to any bomb
        public const int BombDamage = 15 * Unit;

        // a box as MPH entity collision about its centre: 6 faces, outward, wound counter-clockwise about their normals
        internal static CollisionInstance Box(Vector3 half, string name)
        {
            var editors = new List<CollisionDataEditor>();
            void Face(Vector3 n, Vector3 u, Vector3 v)
            {
                // corners centre + n*h +- u +- v, counter-clockwise seen from outside (u x v = n)
                Vector3 c = n;
                var ed = new CollisionDataEditor { Plane = new Vector4(n.Normalized(), Vector3.Dot(n.Normalized(), c)), LayerMask = (ushort)(4 | Axis(n)) };
                ed.Points.Add(c - u - v); ed.Points.Add(c + u - v); ed.Points.Add(c + u + v); ed.Points.Add(c - u + v);
                ed.Terrain = Terrain.Metal;
                editors.Add(ed);
            }
            Vector3 x = Vector3.UnitX * half.X, y = Vector3.UnitY * half.Y, z = Vector3.UnitZ * half.Z;
            Face(y, z, x); Face(-y, x, z);
            Face(x, y, z); Face(-x, z, y);
            Face(z, x, y); Face(-z, y, x);
            byte[] bytes = RepackCollision.PackMphCollision(editors);
            CollisionHeader header = Read.ReadStruct<CollisionHeader>(bytes);
            return new CollisionInstance(name, Collision.ReadMphCollision(header, bytes, roomLayerMask: -1), isEntity: true);
        }

        static int Axis(Vector3 n)
        {
            float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
            if (ay > ax && ay >= az) return 1;
            if (az > ax && az > ay) return 2;
            return 0;
        }
    }

    public sealed class EchoesBreakableEntity : EnemyInstanceEntity
    {
        readonly Vector3 _half;
        // its own axes (turned about MPH Y by its yaw): right, up = Y, facing
        readonly Vector3 _right, _facing;
        readonly int _full, _respawn;
        readonly bool _anyBomb;
        readonly HashSet<BombEntity> _bombs = new();
        readonly List<BombEntity> _gone = new();
        int _hp, _timer, _sinceHit = 510;

        // which one of the arena's (arena.json order) and its draw pass (arena.bin: its model, placed)
        public int Index { get; }
        public int Pass { get; }
        // the axis-aligned bounds around its box (its box itself when unturned)
        public Vector3 Min { get; }
        public Vector3 Max { get; }
        public bool Up { get; private set; } = true;
        public int Hp => _hp;
        public int FullHp => _full;
        public int Breaks { get; private set; }
        // red while just hit, as the crates (MphRead's enemies flash red for 10 steps)
        public bool Flash => Up && _sinceHit < 5 * 2;
        // Echoes' lighting there, for the draw (null: lit white)
        public CrateLight? Light { get; }

        public EchoesBreakableEntity(int id, int index, Vector3 min, Vector3 max, Vector3 half, float yaw, int health, bool anyBomb,
            int respawn, int pass, CrateLight? light, Scene scene) : base(new EnemyInstanceEntityData(EnemyType.HitZone, null!), NodeRef.None, scene)
        {
            Id = id;
            Index = index;
            Min = min;
            Max = max;
            Position = (min + max) / 2;
            _half = half;
            float turn = MathHelper.DegreesToRadians(yaw);
            _right = new Vector3(MathF.Cos(turn), 0, -MathF.Sin(turn));
            _facing = new Vector3(MathF.Sin(turn), 0, MathF.Cos(turn));
            _full = _hp = health;
            _anyBomb = anyBomb;
            _respawn = respawn;
            Pass = pass;
            Light = light;
        }

        protected override void EnemyInitialize()
        {
            NodeRef = _scene.GetNodeRefByName(ArenaModelWriter.RoomNode);
            // nothing of MphRead's enemy damage reaches it: no hurt volume in play, invincible to beams, bombs and alt
            // attacks (its own box takes the hits)
            Flags = EnemyFlags.NoMaxDistance | EnemyFlags.Static | EnemyFlags.Invincible;
            _hurtVolumeInit = _hurtVolume = new CollisionVolume(Position.AddY(-10000), 0);
            // turned as its model is: the entity collision follows the transform (Transform.ClearScale)
            var transform = Matrix4.Identity;
            transform.Row0.Xyz = _right;
            transform.Row2.Xyz = _facing;
            transform.Row3.Xyz = Position;
            Transform = transform;
            SetCollision(EchoesBreakables.Box(_half, $"echoes_break{Index}"));
        }

        public override bool GetTargetable()
        {
            return false;
        }

        // a network client's breakable: the host's state (Net/NetWorld.cs); a break since the last one plays here
        int _netBreaks = -1;
        public void NetApply(bool up, bool hit, int breaks)
        {
            if (_netBreaks >= 0 && breaks != _netBreaks && Up) Break();
            _netBreaks = breaks;
            if (up && !Up) Restore();
            else if (!up && Up) Break();
            if (hit && Up) _sinceHit = 0;
        }

        public override bool Process()
        {
            if (_sinceHit < 510) _sinceHit++;
            if (EchoesBreakables.Mirror)
            {
                _bombs.Clear();
                return base.Process();
            }
            if (!Up)
            {
                // back when its time is up and nobody is inside its box (else it would close around them)
                if (_respawn > 0 && --_timer <= 0)
                {
                    if (Occupied()) _timer = 1;
                    else Restore();
                }
                _bombs.Clear();
                return base.Process();
            }
            // bombs exploding this step whose blast reaches the box, each counted once
            foreach (BombEntity bomb in _scene.GetBombEntities())
            {
                if (!bomb.Flags.TestFlag(BombFlags.Exploded) || _bombs.Contains(bomb)) continue;
                _bombs.Add(bomb);
                Vector3 local = Local(bomb.Position);
                if ((Vector3.Clamp(local, -_half, _half) - local).LengthSquared <= bomb.Radius * bomb.Radius)
                {
                    Hit(_anyBomb ? _hp : EchoesBreakables.BombDamage);
                    if (!Up) break;
                }
            }
            if (_bombs.Count > 0)
            {
                _gone.Clear();
                foreach (BombEntity b in _bombs) if (!b.Flags.TestFlag(BombFlags.Exploded)) _gone.Add(b);
                foreach (BombEntity b in _gone) _bombs.Remove(b);
            }
            return base.Process();
        }

        public override void HandleMessage(MessageInfo info)
        {
            if (info.Message == Message.BeamCollideWith && Up && info.Sender is BeamProjectileEntity beam
                && beam.Owner?.Type != EntityType.EnemyInstance)
            {
                Hit(EchoesBreakables.Damage(beam.Beam, beam.Damage, beam.Flags.TestFlag(BeamFlags.Charged)));
            }
        }

        // damage in breakable health (EchoesBreakables.Damage / BombDamage); breaks at 0
        public void Hit(int damage)
        {
            if (!Up || damage <= 0 || EchoesBreakables.Mirror) return;
            _sinceHit = 0;
            _hp -= damage;
            if (_hp <= 0) Break();
        }

        void Break()
        {
            Up = false;
            _hp = 0;
            Breaks++;
            EntityCollision[0]!.Collision.Active = false;
            _timer = _respawn;
            // MPH's mechanical death (the crates' break) across the box, and its sound
            Vector3 size = _half * 2;
            int nx = Math.Clamp((int)MathF.Round(size.X / 3), 1, 3), nz = Math.Clamp((int)MathF.Round(size.Z / 3), 1, 3);
            int ny = Math.Clamp((int)MathF.Round(size.Y / 3), 1, 3);
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    for (int k = 0; k < nz; k++)
                    {
                        Vector3 p = World(new Vector3(size.X * ((i + 0.5f) / nx - 0.5f), size.Y * ((j + 0.5f) / ny - 0.5f), size.Z * ((k + 0.5f) / nz - 0.5f)));
                        _scene.SpawnEffect(EchoesCrateEntity.BreakEffect, GetTransformMatrix(Vector3.UnitZ, Vector3.UnitY, p));
                    }
                }
            }
            int sfx = Metadata.EnemyDeathSfx[(int)EnemyType.ForceFieldLock];
            if (sfx != -1)
            {
                _soundSource.PlaySfx(sfx & ~0xA0000, noUpdate: true, recency: (sfx & 0x20000) != 0 ? Single.MaxValue : (sfx & 0x80000) != 0 ? 0 : -1,
                    sourceOnly: (sfx & 0x20000) != 0);
            }
        }

        bool Occupied()
        {
            foreach (PlayerEntity player in _scene.GetPlayerEntities())
            {
                if (player.Health == 0) continue;
                Vector3 c = Local(player.Volume.SpherePosition);
                float r = player.Volume.SphereRadius;
                Vector3 nearest = Vector3.Clamp(c, -_half, _half);
                // the biped's two spheres reach 1.6 up from its feet; check the whole height
                if ((nearest.Xz - c.Xz).LengthSquared <= r * r && c.Y - r <= _half.Y && c.Y + 1.6f >= -_half.Y) return true;
            }
            return false;
        }

        // a point in its box's own axes (centre 0) and back
        Vector3 Local(Vector3 p)
        {
            Vector3 d = p - Position;
            return new Vector3(Vector3.Dot(d, _right), d.Y, Vector3.Dot(d, _facing));
        }

        Vector3 World(Vector3 l) => Position + _right * l.X + Vector3.UnitY * l.Y + _facing * l.Z;

        void Restore()
        {
            Up = true;
            _hp = _full;
            _sinceHit = 510;
            EntityCollision[0]!.Collision.Active = true;
        }

        // MphRead draws nothing for it; the device draws its Echoes model (CampaignArena.cs)
        protected override bool EnemyGetDrawInfo()
        {
            return true;
        }
    }

    // Dark Water as MPH acid: 1 energy when a standing player's feet first go under a pool's surface, then every 4 MPH
    // ticks (8 steps) while they stay, invulnerability ignored (PlayerProcess's OnAcid rule)
    public sealed class EchoesFluidEntity : EnemyInstanceEntity
    {
        readonly (Vector3 Min, Vector3 Max, float Surface)[] _pools;
        readonly bool[] _in = new bool[8]; // (8 players, P8)

        public int Hurts { get; private set; }

        public EchoesFluidEntity(int id, (Vector3 Min, Vector3 Max, float Surface)[] pools, Scene scene)
            : base(new EnemyInstanceEntityData(EnemyType.HitZone, null!), NodeRef.None, scene)
        {
            Id = id;
            _pools = pools;
        }

        protected override void EnemyInitialize()
        {
            NodeRef = _scene.GetNodeRefByName(ArenaModelWriter.RoomNode);
            Flags = EnemyFlags.NoMaxDistance | EnemyFlags.Static | EnemyFlags.Invincible;
            _hurtVolumeInit = _hurtVolume = new CollisionVolume(Position.AddY(-10000), 0);
        }

        public override bool GetTargetable()
        {
            return false;
        }

        // the player's feet (its volume's lowest point) under a pool's surface, inside its box
        public bool InPool(PlayerEntity player)
        {
            Vector3 c = player.Volume.SpherePosition;
            float feet = c.Y - player.Volume.SphereRadius;
            foreach ((Vector3 min, Vector3 max, float surface) in _pools)
            {
                if (c.X >= min.X && c.X <= max.X && c.Z >= min.Z && c.Z <= max.Z && feet < surface && feet > min.Y - 1) return true;
            }
            return false;
        }

        public override bool Process()
        {
            foreach (PlayerEntity player in _scene.GetPlayerEntities())
            {
                int slot = player.SlotIndex;
                if (slot < 0 || slot >= _in.Length) continue;
                bool standing = player.Health > 0 && player.Flags1.TestFlag(PlayerFlags1.Standing) && InPool(player);
                if (standing && (!_in[slot] || _scene.FrameCount % (4 * 2) == 0))
                {
                    player.TakeDamage(1, DamageFlags.IgnoreInvuln, direction: null, source: null);
                    Hurts++;
                }
                _in[slot] = standing;
            }
            return base.Process();
        }

        protected override bool EnemyGetDrawInfo()
        {
            return true;
        }
    }
}
