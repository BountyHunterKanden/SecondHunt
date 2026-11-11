using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Mining Plaza's seven SAND CRATEs, the shootable plants (thread 3d373d stage 3, owner 2026-10-31: "imported exactly:
    // their own animated model, the hit flash and flinch, their own debris burst, effects and sound. The plants regrow 30 s
    // after being destroyed"). Each is Echoes' own actor: its animation set's ready loop and flinch baked as placed passes
    // (MiningPlaza.Crates, at crate 0's transform; each crate drawn through its own Delta), its DCLN as entity collision
    // (players stand on it, beams hit it: BeamCollideWith), its generator's DebrisExtended pieces flown by the app
    // (CampaignRenderer.SandCrateFx.cs) with the pillar look's effects and sounds. Health and damage are EchoesBreakables'
    // (6 points; a power shot breaks it). Its drops follow Echoes' two pickup rules (RULE 1bb549fb health/missiles,
    // ec6568a0 the beams' ammo, here our UA) on the attacker's needs. It regrows 30 s after it broke, held while a player
    // stands in its spot, either growing up from the ground or fading in (FadeIn, switchable live). LAN/online guests
    // mirror the host's (Net/NetWorld.cs); the bots shoot it like the Echoes arenas' crates (PlayerEntity.HostCrateList).
    public static class MiningPlazaCrates
    {
        public sealed class CrateSpot
        {
            public uint Id { get; set; }
            // the passes of the ready loop's and the flinch's frames, at Fps
            public int[] Ready { get; set; } = Array.Empty<int>();
            public int[] Flinch { get; set; } = Array.Empty<int>();
            public float Fps { get; set; } = 12;
            // the baked frames' place (crate 0's) to this crate's (MPH, row vectors: 16 floats)
            public float[] Delta { get; set; } = Array.Empty<float>();
            // MPH: the actor's origin (its foot), its model's middle, the spot's radius and top (for the regrow hold)
            public float[] Origin { get; set; } = Array.Empty<float>();
            public float[] Center { get; set; } = Array.Empty<float>();
            public float Radius { get; set; }
            public float Top { get; set; }
            public float[] Tris { get; set; } = Array.Empty<float>();
            public float[]? Ambient { get; set; }
            public List<float[]>? Lights { get; set; }
            // its generator's pieces (EchoesTurret.GrateDebris), their placed passes, how many it makes of how many slots
            public List<TurretFx> Debris { get; set; } = new();
            public Dictionary<uint, int> DebrisPasses { get; set; } = new();
            public int DebrisCount { get; set; }
            public int DebrisSlots { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public float[]? DebrisAxes { get; set; }
        }

        public const int Health = 6 * EchoesBreakables.Unit;
        public const int RegrowFrames = 30 * 60, GrowFrames = 60, FlashFrames = 10;

        // the regrow's look (owner: "build both a grow-up-from-the-ground look and a fade-in look, switchable live")
        public static bool FadeIn { get; set; }

        static readonly Dictionary<int, List<CrateSpot>> _rooms = new();

        internal static void SetRoom(int roomId, EchoesArena.Info? info)
        {
            lock (_rooms)
            {
                if (info?.SandCrates is { Count: > 0 } p) _rooms[roomId] = p;
                else _rooms.Remove(roomId);
            }
        }

        public static Matrix4 M(float[] a) => a.Length < 16 ? Matrix4.Identity
            : new Matrix4(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);

        public static float[] F(Matrix4 m) => new[]
        {
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
        };

        public static Vector3 V(float[] a) => a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.Zero;

        // the drops' pickups: Echoes health 10 / 30 / 50 / 100, missiles 5 / 10, beam ammo small / normal+large
        static readonly ItemType[] Drops =
        {
            ItemType.HealthSmall, ItemType.HealthMedium, ItemType.HealthBig, ItemType.MissileSmall, ItemType.MissileBig, ItemType.UASmall, ItemType.UABig,
        };

        // SceneSetup.HostExtraEntities: a crate entity each after every other entity (any multiplayer match in the room);
        // the bots' crate list gets them after the Echoes crates' (EchoesCrates.Extra runs inside this one)
        public static IReadOnlyList<EntityBase> Extra(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            List<CrateSpot>? spots;
            lock (_rooms)
            {
                if (!GameState.Multiplayer || !_rooms.TryGetValue(roomId, out spots)) return entities;
            }
            var list = new List<EntityBase>(entities);
            int id = entities.Count == 0 ? 0 : entities.Max(e => e.Id) + 1;
            var mine = new List<EnemyInstanceEntity>();
            foreach (CrateSpot s in spots)
            {
                var e = new MiningPlazaCrateEntity(id++, s, MiningPlazaPillars.Light(s.Ambient, s.Lights), scene);
                list.Add(e);
                mine.Add(e);
            }
            PlayerEntity.HostCrateList = (PlayerEntity.HostCrateList ?? Array.Empty<EnemyInstanceEntity>()).Concat(mine).ToArray();
            foreach (ItemType t in Drops) scene.LoadModel(Metadata.Items[(int)t]);
            return list;
        }

        // ---- Echoes' drop rules on a player's needs ----

        // RULE 1bb549fb (health and missiles): the first condition that holds rolls its chance; RULE ec6568a0 (the beams'
        // ammo, our UA) by the UA share: each line rolls its own chance and count
        internal static List<ItemType> PickDrops(PlayerEntity? p)
        {
            var got = new List<ItemType>();
            bool Roll(int pct) => Rng.GetRandomInt2(100) < (uint)pct;
            int Count(int lo, int hi) => lo + (int)Rng.GetRandomInt2((uint)(hi - lo + 1));
            if (p == null) return got;
            float hp = p.HealthMax > 0 ? 100f * p.Health / p.HealthMax : 100;
            int cap = Math.Max(1, (int)p.Values.MpAmmoCap);
            int missiles = p.HostAmmo(1), ua = p.HostAmmo(0);
            bool needMsl = missiles < cap, needHlt = p.Health < p.HealthMax;
            if (hp < 20) { if (Roll(75)) got.Add(ItemType.HealthBig); }
            else if (needMsl && missiles == 0) { if (Roll(80)) got.Add(ItemType.MissileBig); }
            else if (hp < 50) { if (Roll(50)) got.Add(ItemType.HealthMedium); }
            else if (hp < 75) { if (Roll(30)) got.Add(ItemType.HealthBig); }
            else if (needMsl) { if (Roll(30)) got.Add(ItemType.MissileSmall); }
            else if (needHlt) { if (Roll(60)) got.Add(ItemType.HealthSmall); }
            float uaPct = 100f * ua / cap;
            void Add(ItemType t, int pct, int lo, int hi)
            {
                if (!Roll(pct)) return;
                for (int n = Count(lo, hi); n > 0; n--) got.Add(t);
            }
            if (uaPct < 25)
            {
                Add(ItemType.UABig, 50, 1, 2);
                Add(ItemType.UABig, 50, 1, 1);
            }
            else if (uaPct < 50)
            {
                Add(ItemType.UABig, 50, 1, 1);
                Add(ItemType.UASmall, 80, 1, 2);
            }
            else Add(ItemType.UASmall, 25, 1, 1);
            // (11-01) one crate, one pickup: the first thing its rolls came up with (health or missiles, else beam ammo). Every roll
            // above still happens, so the random stream is the old one; on with format 78 so older replays keep their bundles
            if (PlayerEntity.HostBotLedgeLand && got.Count > 1) got.RemoveRange(1, got.Count - 1);
            return got;
        }
    }

    public sealed class MiningPlazaCrateEntity : EnemyInstanceEntity
    {
        public MiningPlazaCrates.CrateSpot Spot { get; }
        public CrateLight? Light { get; }
        public Vector3 Origin { get; }
        readonly Vector3 _center; // its middle; Position is zeroed by Transform = Identity in EnemyInitialize
        // (11-01) aim, bombs and drops at its middle: on with format 78 like PlayerEntity.HostBotLedgeLand, so older replays keep
        // the zeroed Position they were recorded with
        Vector3 Mid => PlayerEntity.HostBotLedgeLand ? _center : Position;
        readonly Matrix4 _delta;
        int _hp = MiningPlazaCrates.Health;
        // frames: until it regrows (broken), since it regrew (the grow / fade), since the last hit, the clips' time
        int _timer, _grow = MiningPlazaCrates.GrowFrames, _sinceHit = 1000, _anim, _flinch = -1;
        readonly List<BombEntity> _bombs = new(), _gone = new();

        public bool Up { get; private set; } = true;
        public int Breaks { get; private set; }
        public bool Flash => Up && _sinceHit < MiningPlazaCrates.FlashFrames;
        public bool Hit => _sinceHit == 0;

        public MiningPlazaCrateEntity(int id, MiningPlazaCrates.CrateSpot spot, CrateLight? light, Scene scene)
            : base(new EnemyInstanceEntityData(EnemyType.HitZone, null!), MphRead.Formats.Culling.NodeRef.None, scene)
        {
            Id = id;
            Spot = spot;
            Light = light;
            _delta = MiningPlazaCrates.M(spot.Delta);
            Origin = MiningPlazaCrates.V(spot.Origin);
            // its middle: the bots aim at it (PlayerAiCrates: GetPosition)
            Position = _center = spot.Center.Length == 3 ? MiningPlazaCrates.V(spot.Center) : Origin;
        }

        protected override void EnemyInitialize()
        {
            NodeRef = _scene.GetNodeRefByName(ArenaModelWriter.RoomNode);
            Flags = EnemyFlags.NoMaxDistance | EnemyFlags.Static | EnemyFlags.Invincible;
            _hurtVolumeInit = _hurtVolume = new CollisionVolume(Position.AddY(-10000), 0);
            Transform = Matrix4.Identity;
            SetCollision(MiningPlazaPillars.Shape(Spot.Tris, $"plaza_crate{Spot.Id:x8}", Terrain.Sand), 0);
            UpdateCollisionTransform(0, Matrix4.Identity);
            UpdateLinkedInverse(0);
        }

        public override bool GetTargetable() => Up;
        public override void GetPosition(out Vector3 position) => position = Mid;

        protected override bool EnemyGetDrawInfo() => true;

        public override bool Process()
        {
            _anim++;
            if (_sinceHit < 1000) _sinceHit++;
            if (_flinch >= 0 && ++_flinch >= Spot.Flinch.Length * 60 / Math.Max(1, Spot.Fps)) _flinch = -1;
            if (_grow < MiningPlazaCrates.GrowFrames) _grow++;
            if (EchoesBreakables.Mirror)
            {
                _bombs.Clear();
                return base.Process();
            }
            if (Up)
            {
                foreach (BombEntity bomb in _scene.GetBombEntities())
                {
                    if (!bomb.Flags.TestFlag(BombFlags.Exploded) || _bombs.Contains(bomb)) continue;
                    _bombs.Add(bomb);
                    if (Vector3.Distance(bomb.Position, Mid) <= bomb.Radius + Spot.Radius) Damage(EchoesBreakables.BombDamage, bomb.Owner as PlayerEntity);
                }
            }
            _gone.Clear();
            foreach (BombEntity b in _bombs)
            {
                if (!b.Flags.TestFlag(BombFlags.Exploded)) _gone.Add(b);
            }
            foreach (BombEntity b in _gone) _bombs.Remove(b);
            if (!Up)
            {
                if (_timer > 0) _timer--;
                if (Occupied()) _timer = Math.Max(_timer, 1);
                if (_timer == 0) Restore();
            }
            return base.Process();
        }

        // a living player in the spot (its cylinder: radius, foot to top)
        bool Occupied()
        {
            float bottom = Origin.Y - 0.5f, top = Spot.Top;
            foreach (PlayerEntity p in _scene.GetPlayerEntities())
            {
                if (p.Health == 0) continue;
                Vector3 c = p.Volume.SpherePosition;
                float r = p.Volume.SphereRadius;
                float dx = c.X - Origin.X, dz = c.Z - Origin.Z;
                float reach = Spot.Radius + r;
                if (dx * dx + dz * dz <= reach * reach && c.Y - r <= top && c.Y + 1.6f >= bottom) return true;
            }
            return false;
        }

        void Damage(int damage, PlayerEntity? by)
        {
            if (!Up) return;
            _sinceHit = 0;
            _flinch = 0; // every shot restarts the flinch, as in Echoes: it answers each hit, not only the first
            _hp -= damage;
            if (_hp <= 0) Break(by);
        }

        void Break(PlayerEntity? by)
        {
            Up = false;
            Breaks++;
            _timer = MiningPlazaCrates.RegrowFrames;
            if (EntityCollision[0]?.Collision is { } c) c.Active = false;
            if (EchoesBreakables.Mirror) return;
            // the drops: on the attacker's needs, else the nearest living player's
            PlayerEntity? need = by;
            if (need == null || need.Health == 0)
            {
                float best = float.MaxValue;
                foreach (PlayerEntity p in _scene.GetPlayerEntities())
                {
                    if (p.Health == 0) continue;
                    float d = Vector3.DistanceSquared(p.Position, Mid);
                    if (d < best)
                    {
                        best = d;
                        need = p;
                    }
                }
            }
            List<ItemType> drops = MiningPlazaCrates.PickDrops(need);
            for (int i = 0; i < drops.Count; i++)
            {
                // more than one: around the middle, a little apart
                Vector3 at = Mid;
                if (drops.Count > 1)
                {
                    float a = MathF.Tau * i / drops.Count;
                    at += new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * 0.6f;
                }
                ItemSpawnEntity.SpawnItem(drops[i], at, NodeRef, EchoesCrateEntity.DropFrames, _scene);
            }
        }

        void Restore()
        {
            Up = true;
            _hp = MiningPlazaCrates.Health;
            _grow = 0;
            _flinch = -1;
            _sinceHit = 1000;
            if (EntityCollision[0]?.Collision is { } c) c.Active = true;
        }

        public override void HandleMessage(MessageInfo info)
        {
            if (info.Message == Message.BeamCollideWith && Up && !EchoesBreakables.Mirror && info.Sender is BeamProjectileEntity beam
                && beam.Owner?.Type != EntityType.EnemyInstance)
            {
                Damage(EchoesBreakables.Damage(beam.Beam, beam.Damage, beam.Flags.TestFlag(BeamFlags.Charged)), beam.Owner as PlayerEntity);
            }
        }

        // a network client's crate: the host's state (Net/NetWorld.cs); down in the first snapshot: already down there
        int _netBreaks = -1;
        public void NetApply(bool up, bool hit, int breaks)
        {
            if (_netBreaks >= 0 && breaks != _netBreaks && Up) Break(null);
            _netBreaks = breaks;
            Breaks = breaks;
            if (up && !Up) Restore();
            else if (!up && Up)
            {
                Break(null);
                Breaks = breaks;
            }
            // (the host sends its flash, several frames long: a new hit is its rising edge)
            if (hit && !_netHit && Up)
            {
                _sinceHit = 0;
                _flinch = 0;
            }
            _netHit = hit;
        }
        bool _netHit;

        // the regrow's share (1: whole)
        public float Grown => Math.Clamp(_grow / (float)MiningPlazaCrates.GrowFrames, 0, 1);

        // what to draw now: the frame's model (from the baked place), its pass, light and fade (negative: blended), added to
        // the caller's list (drawn twice a frame: no iterator garbage, frame dips 2026-11-01)
        public void AddPieces(List<(Matrix4 Model, int Pass, CrateLight? Light, float Fade)> into)
        {
            if (!Up) return;
            int[] clip = _flinch >= 0 && Spot.Flinch.Length > 0 ? Spot.Flinch : Spot.Ready;
            if (clip.Length == 0) return;
            int t = _flinch >= 0 ? _flinch : _anim;
            int frame = (int)(t * Spot.Fps / 60f);
            int pass = clip[_flinch >= 0 ? Math.Min(frame, clip.Length - 1) : frame % clip.Length];
            Matrix4 m = _delta;
            float fade = 1, g = Grown;
            if (g < 1)
            {
                // eased: quick at first, settling
                float k = 1 - (1 - g) * (1 - g);
                if (MiningPlazaCrates.FadeIn) fade = k >= 0.998f ? 1 : -Math.Max(0.01f, k);
                else
                {
                    float s = 0.05f + 0.95f * k;
                    m = m * Matrix4.CreateTranslation(-Origin) * Matrix4.CreateScale(s) * Matrix4.CreateTranslation(Origin);
                }
            }
            into.Add((m, pass, Light, fade));
        }
    }
}
