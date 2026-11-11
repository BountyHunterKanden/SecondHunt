using System;
using System.Collections.Generic;
using System.Reflection;
using MphRead.Entities;

namespace MphRead
{
    // mph-recomp: gameplay mods' value filters (docs/MOD_API.md §4.2-4.3). Where GameEvents only reports, a filter may
    // change a value the engine is about to use: the damage of a hit, a kill's score, a respawn's wait, and so on. Each
    // site is written `if (GameFilters.Damage) { var f = new Dictionary...; GameFilters.Run("damage", f); ... }`, so with no
    // gameplay mod hooking that filter it costs one static bool read: nothing is allocated and the sim is unchanged.
    //
    // Only gameplay mods (mod.json "Kind": "gameplay") can filter. They run offline only, and a replay of a match they
    // changed names them in its header and plays them again (MphRecomp.Replay ReplayFile format 75), so a replay of a
    // filtered match stays exact.
    public static class GameFilters
    {
        // one flag per filter: set by the app's mod host when some gameplay mod hooks it
        public static bool Damage;       // "damage": a hit on a player (PlayerEntity.TakeDamage)
        public static bool Kill;         // "kill": a kill's scoring
        public static bool RespawnTime;  // "respawn_time": how long a dead player waits
        public static bool SpawnPoint;   // "spawn_point": which spawn point a player (re)spawns at
        public static bool Pickup;       // "pickup": what a pickup gives
        public static bool PlayerValues; // "player_values": a hunter's movement/health values at spawn
        public static bool EnemyDamage;  // "enemy_damage": a hit on an enemy
        public static bool EnemySpawn;   // "enemy_spawn": an enemy's health as it spawns

        // (filter name, fields) -> whether any filter ran; changed values come back in the fields
        public static Func<string, Dictionary<string, object?>, bool>? Apply;

        public static bool Run(string name, Dictionary<string, object?> fields)
        {
            try { return Apply?.Invoke(name, fields) ?? false; }
            catch { return false; } // a filter must never be able to break a frame
        }

        public static void Clear()
        {
            Damage = Kill = RespawnTime = SpawnPoint = Pickup = PlayerValues = EnemyDamage = EnemySpawn = false;
            Apply = null;
        }

        public static void Set(ICollection<string> names, Func<string, Dictionary<string, object?>, bool>? apply)
        {
            Damage = names.Contains("damage");
            Kill = names.Contains("kill");
            RespawnTime = names.Contains("respawn_time");
            SpawnPoint = names.Contains("spawn_point");
            Pickup = names.Contains("pickup");
            PlayerValues = names.Contains("player_values");
            EnemyDamage = names.Contains("enemy_damage");
            EnemySpawn = names.Contains("enemy_spawn");
            Apply = apply;
        }

        // "damage": target/attacker slots, damage after the damage-level scaling, beam name, headshot -> the damage dealt
        public static uint PlayerDamage(int target, int attacker, uint damage, string beam, bool headshot)
        {
            var f = new Dictionary<string, object?>
            {
                ["target"] = target, ["attacker"] = attacker, ["damage"] = (int)damage, ["beam"] = beam, ["headshot"] = headshot,
            };
            if (Run("damage", f) && f["damage"] is int d)
            {
                return (uint)Math.Max(d, 0);
            }
            return damage;
        }

        // "kill": runs after vanilla scored the kill (vanilla_points is what it gave the scorer); the filter's "points"
        // (default 0) are added on top to "scorer" (default the attacker), so a mod can add, take away or move points
        public static void KillPoints(int victim, int attacker, string beam, bool headshot, int vanillaPoints)
        {
            var f = new Dictionary<string, object?>
            {
                ["victim"] = victim, ["attacker"] = attacker, ["suicide"] = victim == attacker, ["beam"] = beam,
                ["headshot"] = headshot, ["vanilla_points"] = vanillaPoints, ["points"] = 0, ["scorer"] = attacker,
            };
            if (Run("kill", f) && f["points"] is int p && p != 0 && f["scorer"] is int s
                && s >= 0 && s < GameState.Points.Length)
            {
                GameState.Points[s] += p;
            }
        }

        // "respawn_time": frames a dead player waits (vanilla 180 at 60 Hz) -> the wait, 1..65535
        public static ushort RespawnFrames(int player, int attacker, ushort frames)
        {
            var f = new Dictionary<string, object?> { ["player"] = player, ["attacker"] = attacker, ["frames"] = (int)frames };
            return Run("respawn_time", f) && f["frames"] is int n ? (ushort)Math.Clamp(n, 1, ushort.MaxValue) : frames;
        }

        // "spawn_point": index of vanilla's pick among the room's spawn points (-1 = none) -> the index to use
        public static int SpawnIndex(int player, int index, int count)
        {
            var f = new Dictionary<string, object?> { ["player"] = player, ["index"] = index, ["count"] = count };
            return Run("spawn_point", f) && f["index"] is int i ? i : index;
        }

        // "pickup": a player touching an item -> false when a mod set "allow" to false (the item stays)
        public static bool AllowPickup(int player, string item)
        {
            var f = new Dictionary<string, object?> { ["player"] = player, ["item"] = item, ["allow"] = true };
            return !Run("pickup", f) || f["allow"] is not false;
        }

        // "enemy_damage": a hit on an enemy (attacker = player slot or -1) -> the damage dealt
        public static uint EnemyHit(string enemy, int attacker, uint damage)
        {
            var f = new Dictionary<string, object?> { ["enemy"] = enemy, ["attacker"] = attacker, ["damage"] = (int)damage };
            return Run("enemy_damage", f) && f["damage"] is int d ? (uint)Math.Max(d, 0) : damage;
        }

        // "enemy_spawn": an enemy just set up (room = scene room id) -> its health, 1..65535 (max health follows it)
        public static ushort EnemySpawnHealth(string enemy, int room, ushort health)
        {
            var f = new Dictionary<string, object?> { ["enemy"] = enemy, ["room"] = room, ["health"] = (int)health };
            return Run("enemy_spawn", f) && f["health"] is int h ? (ushort)Math.Clamp(h, 1, ushort.MaxValue) : health;
        }

        // "player_values": every int/ushort field of the hunter's PlayerValues by name (e.g. JumpSpeed, WalkSpeedCap; fixed
        // point 4096 = 1.0) -> a patched copy; the shared table in Metadata is never touched
        public static PlayerValues PatchValues(int player, string hunter, PlayerValues values)
        {
            FieldInfo[] fields = typeof(PlayerValues).GetFields(BindingFlags.Public | BindingFlags.Instance);
            var f = new Dictionary<string, object?> { ["player"] = player, ["hunter"] = hunter };
            foreach (FieldInfo field in fields)
            {
                if (field.FieldType == typeof(int) || field.FieldType == typeof(ushort))
                {
                    f[field.Name] = Convert.ToInt32(field.GetValue(values));
                }
            }
            if (!Run("player_values", f))
            {
                return values;
            }
            object boxed = values;
            foreach (FieldInfo field in fields)
            {
                if (f.TryGetValue(field.Name, out object? v) && v is int n)
                {
                    if (field.FieldType == typeof(int))
                    {
                        field.SetValue(boxed, n);
                    }
                    else if (field.FieldType == typeof(ushort))
                    {
                        field.SetValue(boxed, (ushort)Math.Clamp(n, 0, ushort.MaxValue));
                    }
                }
            }
            return (PlayerValues)boxed;
        }

        // the filter names the engine knows (mod.filter refuses others)
        public static readonly string[] Names =
            { "damage", "kill", "respawn_time", "spawn_point", "pickup", "player_values", "enemy_damage", "enemy_spawn" };
    }
}
