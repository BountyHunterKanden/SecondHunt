using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;

namespace MphRecomp.Mods
{
    // The entity table's view of the running scene (docs/MOD_API.md §4.4). Handles are handed out in the scene's own enemy
    // order the first time a mod sees an enemy, counting up from 1, so the same run gives the same handles live and in a
    // replay. The scene comes from LiveMods' room hook (SetScene) as each room loads.
    public sealed class LiveEntities : IModEntities
    {
        public static readonly LiveEntities Instance = new();

        // mod-spawned spawners' ids start here (room.on_load uses 30000 up, EnemyPlacement 20000 up)
        const int FirstSpawnId = 40000;

        WeakReference<Scene>? _scene;
        readonly Dictionary<EnemyInstanceEntity, int> _handles = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<int, EnemyInstanceEntity> _byHandle = new();
        int _next = 1;

        public Scene? Scene => _scene != null && _scene.TryGetTarget(out Scene? s) ? s : null;

        // a new room (or a new game): handles start again
        public void SetScene(Scene? scene)
        {
            _scene = scene == null ? null : new WeakReference<Scene>(scene);
            Reset();
        }

        public void Reset()
        {
            _handles.Clear();
            _byHandle.Clear();
            _next = 1;
        }

        public IReadOnlyList<int> Enemies()
        {
            var list = new List<int>();
            Scene? scene = Scene;
            if (scene == null) return list;
            var alive = new HashSet<EnemyInstanceEntity>(ReferenceEqualityComparer.Instance);
            foreach (EnemyInstanceEntity e in scene.GetEnemyInstanceEntities())
            {
                alive.Add(e);
                if (!_handles.TryGetValue(e, out int h))
                {
                    h = _next++;
                    _handles[e] = h;
                    _byHandle[h] = e;
                }
                list.Add(h);
            }
            // forget the gone ones so a dead enemy's handle reads nil
            if (_handles.Count > alive.Count)
            {
                var gone = new List<EnemyInstanceEntity>();
                foreach (EnemyInstanceEntity e in _handles.Keys) if (!alive.Contains(e)) gone.Add(e);
                foreach (EnemyInstanceEntity e in gone)
                {
                    _byHandle.Remove(_handles[e]);
                    _handles.Remove(e);
                }
            }
            return list;
        }

        EnemyInstanceEntity? Find(int handle)
        {
            Scene? scene = Scene;
            if (scene == null || !_byHandle.TryGetValue(handle, out EnemyInstanceEntity? e)) return null;
            foreach (EnemyInstanceEntity a in scene.GetEnemyInstanceEntities()) if (a == e) return e;
            _byHandle.Remove(handle);
            _handles.Remove(e);
            return null;
        }

        public ModEntityInfo? Get(int handle)
        {
            EnemyInstanceEntity? e = Find(handle);
            if (e == null) return null;
            OpenTK.Mathematics.Vector3 p = e.Position;
            return new ModEntityInfo("enemy", e.EnemyType.ToString(), p.X, p.Y, p.Z, e.Health, e.HealthMax);
        }

        // enemy types that are parts of a bigger whole (bosses, their eyes and arms, hunters, locks) or that spawn others:
        // removing one alone would leave the rest broken, so entity.remove refuses them
        static bool Removable(EnemyType t)
        {
            int n = (int)t;
            return !((n >= 19 && n <= 29) || (n >= 31 && n <= 33) || (n >= 40 && n <= 45) || n == 48 || n == 49 || n == 50);
        }

        // removes an enemy at the end of a frame as if it died out of sight: its spawner counts it gone (no item drop, no
        // kill sound). Returns why not, or null when done
        public string? Remove(Scene scene, int handle)
        {
            EnemyInstanceEntity? e = Find(handle);
            if (e == null) return "no such enemy (gone already?)";
            if (!Removable(e.EnemyType)) return $"{e.EnemyType} is part of a bigger enemy and can't be removed alone";
            if (e.Owner is not (EnemySpawnEntity or FhEnemySpawnEntity)) return $"{e.EnemyType} has no spawner and can't be removed";
            scene.SendMessage(Message.Destroyed, e, e.Owner, 0, 0);
            scene.SendMessage(Message.Destroyed, e, null, 0, 0, delay: 1);
            e.Destroy();
            scene.RemoveEntity(e);
            _byHandle.Remove(handle);
            _handles.Remove(e);
            return null;
        }

        // a free entity id for a mod-spawned spawner
        public int FreeId(Scene scene)
        {
            int id = FirstSpawnId;
            while (scene.TryGetEntity(id, out _)) id++;
            return id;
        }
    }
}
