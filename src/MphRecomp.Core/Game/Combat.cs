using System;
using System.Numerics;
using MphRecomp.Sim;

// mph-recomp combat core: data-driven weapons, projectiles, and damageable health -- the gameplay
// layer for the arena firefight. Pure sim logic (no rendering) -> headlessly testable. Projectiles
// and targets are ordinary SimEntities, so they flow through the world/snapshot like everything else.
namespace MphRecomp.Game
{
    // A weapon's tuning. Data-driven so beams can be defined in data and retuned by mods.
    public sealed class WeaponDef
    {
        public string Name { get; set; } = "";
        public float Damage { get; set; } = 10f;
        public float Cooldown { get; set; } = 0.25f;   // seconds between shots
        public int AmmoCost { get; set; } = 0;          // 0 = no ammo (e.g. Power Beam)
        public float ProjectileSpeed { get; set; } = 40f;
        public float ProjectileLife { get; set; } = 2f; // seconds before it fizzles
        public float ProjectileRadius { get; set; } = 0.3f;
        public string ProjectileModel { get; set; } = ""; // drawn if set; logic works either way
    }

    public interface IDamageable
    {
        bool IsDead { get; }
        void Damage(float amount, SimEntity? source);
    }

    // Anything with health: hunters, enemies, destructibles. Fires death/damage callbacks the
    // gameplay + mods can hook.
    public class HealthEntity : SimEntity, IDamageable
    {
        public float MaxHealth = 100f;
        public float Health = 100f;
        public float HitRadius = 1.5f;
        public bool IsDead => Health <= 0f;
        public Action<HealthEntity, float, SimEntity?>? OnDamaged;
        public Action<HealthEntity, SimEntity?>? OnDeath;

        public void Damage(float amount, SimEntity? source)
        {
            if (IsDead || amount <= 0f) return;
            Health -= amount;
            OnDamaged?.Invoke(this, amount, source);
            if (Health <= 0f)
            {
                Health = 0f;
                Alive = false;
                OnDeath?.Invoke(this, source);
            }
        }

        public override void Tick(float dt, in InputState input, SimWorld world) { }
    }

    // A fired shot: flies straight, hits the first live damageable it overlaps (skipping its owner),
    // applies damage, and despawns on hit or after its lifetime.
    public sealed class ProjectileEntity : SimEntity
    {
        public Vector3 Velocity;
        public float Damage;
        public float Radius = 0.3f;
        public float Life = 2f;
        public SimEntity? Owner;
        private float _age;

        public override void Tick(float dt, in InputState input, SimWorld world)
        {
            Position += Velocity * dt;
            Facing = Velocity.LengthSquared() > 1e-8f ? Vector3.Normalize(Velocity) : Facing;
            _age += dt;
            if (_age >= Life) { Alive = false; return; }

            var ents = world.Entities;
            for (int i = 0; i < ents.Count; i++)
            {
                if (ents[i] is HealthEntity h && !h.IsDead && !ReferenceEquals(h, Owner))
                {
                    float r = Radius + h.HitRadius;
                    if (Vector3.DistanceSquared(Position, h.Position) <= r * r)
                    {
                        h.Damage(Damage, Owner);
                        Alive = false;
                        return;
                    }
                }
            }
        }
    }

    // Equipped-weapon state: ammo + cooldown, and firing projectiles into the world.
    public sealed class Weapon
    {
        public WeaponDef Def;
        public int Ammo;
        private float _cooldown;

        public Weapon(WeaponDef def, int ammo = 0) { Def = def; Ammo = ammo; }

        public bool CanFire => _cooldown <= 0f && (Def.AmmoCost == 0 || Ammo >= Def.AmmoCost);

        public void Tick(float dt) { if (_cooldown > 0f) _cooldown -= dt; }

        // Fire a shot from `origin` along `dir`. Returns the projectile, or null if on cooldown /
        // out of ammo. Consumes ammo and starts the cooldown when it fires.
        public ProjectileEntity? Fire(SimWorld world, Vector3 origin, Vector3 dir, SimEntity? owner)
        {
            if (!CanFire) return null;
            if (Def.AmmoCost > 0) Ammo -= Def.AmmoCost;
            _cooldown = Def.Cooldown;
            Vector3 n = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : new Vector3(0, 0, -1);
            return world.Spawn(new ProjectileEntity
            {
                Owner = owner,
                Damage = Def.Damage,
                Radius = Def.ProjectileRadius,
                Life = Def.ProjectileLife,
                ModelName = Def.ProjectileModel,
                Position = origin,
                Velocity = n * Def.ProjectileSpeed
            });
        }
    }
}
