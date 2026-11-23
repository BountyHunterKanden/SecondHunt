using System;
using System.Collections.Generic;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRecomp.Import;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign.Retro
{
    // The imported Retro enemies Hard Mode can place (docs/ENEMY_IMPORT.md E1): <imports>/enemies/<Name>/, written by the
    // "enemies" import kind from the user's own disc. Root is set by the campaign launch; a name is placeable once its
    // import is usable (ImportedContent). Catalogue ids are "mp2:<Name without MP2>", e.g. "mp2:Splinter".
    public static class RetroEnemies
    {
        static string? _root;

        public static string? Root
        {
            get => _root;
            set
            {
                if (_root == value) return;
                _root = value;
                Reset();
                EnemyCatalogue.ResetRetro();
            }
        }

        // the pilot's enemies (one so far)
        public static readonly string[] Known = { "MP2Splinter" };

        static readonly Dictionary<string, RetroEnemyConfig?> _configs = new(StringComparer.OrdinalIgnoreCase);

        public static string IdOf(string name) => "mp2:" + (name.StartsWith("MP2", StringComparison.Ordinal) ? name[3..] : name);

        public static string? NameOf(string id)
        {
            foreach (string n in Known)
            {
                if (IdOf(n) == id) return n;
            }
            return null;
        }

        public static string Dir(string name) => Path.Combine(Root ?? "", name);

        public static bool Usable(string name) =>
            !String.IsNullOrEmpty(Root) && RetroEnemyConfig.Exists(Dir(name)) && ImportedContent.Usable(Root, name);

        public static RetroEnemyConfig? Config(string name)
        {
            lock (_configs)
            {
                if (_configs.TryGetValue(name, out RetroEnemyConfig? c)) return c;
                try
                {
                    c = Usable(name) ? RetroEnemyConfig.Load(Dir(name)) : null;
                }
                catch (Exception)
                {
                    c = null;
                }
                _configs[name] = c;
                return c;
            }
        }

        // forget the loaded numbers (a re-import, or a test)
        public static void Reset()
        {
            lock (_configs) _configs.Clear();
        }
    }

    // A placed Retro enemy's spawner (EnemyPlacement's Retro branch): our own, shaped like a placed MPH spawner (counts as
    // PlacedEnemy: Count at once up to Limit alive, Total overall, 0 = no end; trigger near / entry / wave). It draws
    // nothing (EntityType.Model like the editor's stand-ins) and makes EchoesSplinter instances owned by it.
    public sealed class PlacedRetroSpawner : EntityBase
    {
        public readonly string Name;
        public readonly RetroEnemyConfig Config;
        public readonly int Total, Limit, Count;
        public readonly float Radius, Yaw;
        public readonly bool Near;
        public bool Held { get; set; }
        public int SpawnedCount { get; private set; }
        public int AliveCount { get; private set; }
        public bool Finished => Total > 0 && SpawnedCount >= Total && AliveCount == 0;

        // the pause before the next one after a kill (ours: MPH spawners use their own cooldown field)
        const float Cooldown = 2f;
        float _wait;
        bool _triggered;

        public PlacedRetroSpawner(int id, string name, RetroEnemyConfig config, Vector3 pos, float facingDegrees,
            int total, int limit, int count, string trigger, float radius, Scene scene) : base(EntityType.Model, scene)
        {
            Id = id;
            Name = name;
            Config = config;
            Position = pos;
            Yaw = MathHelper.DegreesToRadians(facingDegrees);
            Total = Math.Max(total, 0);
            Limit = Math.Max(limit, 1);
            Count = Math.Max(count, 1);
            Near = trigger == "near";
            Radius = radius;
            Held = trigger == "wave";
        }

        public override bool GetTargetable() => false;

        public override bool Process()
        {
            AliveCount = 0;
            foreach (EnemyInstanceEntity e in _scene.GetEnemyInstanceEntities())
            {
                if (e.Owner == this) AliveCount++;
            }
            if (Held || Total > 0 && SpawnedCount >= Total)
            {
                return true;
            }
            if (!_triggered)
            {
                if (Near && Vector3.DistanceSquared(PlayerEntity.Main.Position, Position) > Radius * Radius)
                {
                    return true;
                }
                _triggered = true;
            }
            if (_wait > 0)
            {
                _wait -= EchoesSplinter.Step;
                return true;
            }
            for (int i = 0; i < Count && AliveCount < Limit && (Total == 0 || SpawnedCount < Total); i++)
            {
                Spawn();
            }
            return true;
        }

        void Spawn()
        {
            int id = 31000;
            while (_scene.TryGetEntity(id, out _)) id++;
            // a little spread when several are out at once
            float a = SpawnedCount * 2.4f;
            Vector3 at = Position + (AliveCount == 0 ? Vector3.Zero : new Vector3(MathF.Sin(a), 0, MathF.Cos(a)) * 0.6f);
            var e = new EchoesSplinter(id, Config, at, Yaw, _scene, seed: Id * 31 + SpawnedCount + 1, owner: this);
            _scene.AddEntity(e);
            SpawnedCount++;
            AliveCount++;
            _wait = Cooldown;
        }
    }
}
