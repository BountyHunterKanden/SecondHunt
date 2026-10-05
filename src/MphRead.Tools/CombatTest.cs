using System;
using System.Linq;
using System.Numerics;
using MphRecomp.Game;
using MphRecomp.Sim;

namespace MphRead
{
    // Headless tests for the combat core (MphRecomp.Game: WeaponDef/Weapon/ProjectileEntity/
    // HealthEntity): fire mechanics (cooldown, ammo), projectile flight + hit/damage/death, misses,
    // owner immunity, and firing through the PlayerController. Run: MphRead.Tools.dll -combattest.
    internal static class CombatTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }

        public static void Run(string[] args)
        {
            // --- weapon mechanics: cooldown ---
            var w = new SimWorld();
            var noAmmo = new Weapon(new WeaponDef { Cooldown = 0.25f, AmmoCost = 0 });
            Check("weapon starts ready", noAmmo.CanFire);
            var shot = noAmmo.Fire(w, Vector3.Zero, -Vector3.UnitZ, null);
            Check("fire returns a projectile", shot != null);
            Check("on cooldown right after firing", !noAmmo.CanFire);
            noAmmo.Tick(0.25f);
            Check("ready again after cooldown elapses", noAmmo.CanFire);

            // --- weapon mechanics: ammo depletion ---
            var ammoW = new Weapon(new WeaponDef { Cooldown = 0f, AmmoCost = 1 }, ammo: 2);
            ammoW.Fire(w, Vector3.Zero, -Vector3.UnitZ, null);
            Check("ammo decremented on fire (2 -> 1)", ammoW.Ammo == 1);
            ammoW.Fire(w, Vector3.Zero, -Vector3.UnitZ, null);
            Check("ammo now 0", ammoW.Ammo == 0);
            Check("cannot fire with no ammo", !ammoW.CanFire && ammoW.Fire(w, Vector3.Zero, -Vector3.UnitZ, null) == null);

            // --- projectile hits a target and applies damage ---
            var w2 = new SimWorld();
            var target = w2.Spawn(new HealthEntity { Health = 25, MaxHealth = 25, HitRadius = 1.5f, Position = new Vector3(0, 0, -5) });
            bool died = false; target.OnDeath = (h, s) => died = true;
            var proj = w2.Spawn(new ProjectileEntity { Position = Vector3.Zero, Velocity = new Vector3(0, 0, -40), Damage = 10, Owner = null });
            for (int i = 0; i < 12; i++) w2.Tick(1f / 60f, default);
            Check("projectile damaged the target (25 -> 15)", Math.Abs(target.Health - 15f) < 1e-4f);
            Check("projectile despawned on hit", !proj.Alive && !w2.Entities.Contains(proj));
            Check("target not dead yet", !died && !target.IsDead);

            // --- lethal hit -> death + despawn ---
            var w3 = new SimWorld();
            var frail = w3.Spawn(new HealthEntity { Health = 8, HitRadius = 1.5f, Position = new Vector3(0, 0, -5) });
            bool frailDied = false; frail.OnDeath = (h, s) => frailDied = true;
            w3.Spawn(new ProjectileEntity { Position = Vector3.Zero, Velocity = new Vector3(0, 0, -40), Damage = 10 });
            for (int i = 0; i < 12; i++) w3.Tick(1f / 60f, default);
            Check("lethal hit kills the target", frail.IsDead && frailDied);
            Check("dead target removed from world", !w3.Entities.Contains(frail));

            // --- a miss deals no damage and the projectile fizzles after its lifetime ---
            var w4 = new SimWorld();
            var offside = w4.Spawn(new HealthEntity { Health = 30, HitRadius = 1.5f, Position = new Vector3(10, 0, -5) });
            var misser = w4.Spawn(new ProjectileEntity { Position = Vector3.Zero, Velocity = new Vector3(0, 0, -40), Damage = 10, Life = 0.2f });
            for (int i = 0; i < 20; i++) w4.Tick(1f / 60f, default);
            Check("miss deals no damage", Math.Abs(offside.Health - 30f) < 1e-4f);
            Check("projectile fizzles after its lifetime", !w4.Entities.Contains(misser));

            // --- a projectile never damages its own owner ---
            var w5 = new SimWorld();
            var owner = w5.Spawn(new HealthEntity { Health = 50, HitRadius = 2f, Position = new Vector3(0, 0, -1) });
            w5.Spawn(new ProjectileEntity { Position = Vector3.Zero, Velocity = new Vector3(0, 0, -40), Damage = 10, Owner = owner });
            for (int i = 0; i < 12; i++) w5.Tick(1f / 60f, default);
            Check("owner immune to its own projectile", Math.Abs(owner.Health - 50f) < 1e-4f);

            // --- firing through the player controller ---
            var w6 = new SimWorld();
            var player = w6.Spawn(new PlayerController { Position = Vector3.Zero, Weapon = new Weapon(new WeaponDef { Cooldown = 0.1f, Damage = 5 }) });
            var enemy = w6.Spawn(new HealthEntity { Health = 100, HitRadius = 1.5f, Position = new Vector3(0, 0, -6) });
            var fire = new InputState { Buttons = InputButtons.Fire };
            for (int i = 0; i < 60; i++) w6.Tick(1f / 60f, fire); // hold fire for ~1s
            Check("player fired projectiles into the world", w6.Entities.OfType<ProjectileEntity>().Any() || enemy.Health < 100f);
            Check("player's shots damaged the enemy ahead", enemy.Health < 100f);

            Console.WriteLine($"\nCOMBAT TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }
    }
}
