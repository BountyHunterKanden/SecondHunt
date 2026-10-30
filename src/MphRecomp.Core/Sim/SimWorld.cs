using System;
using System.Collections.Generic;
using System.Numerics;

namespace MphRecomp.Sim
{
    // Action-based input the sim reads each tick, decoupled from the raw gamepad. The presentation
    // layer fills this from the controller; the sim never sees hardware axes/buttons directly.
    public struct InputState
    {
        public Vector2 Move;   // left stick: X = strafe (+right), Y = forward (+fwd)
        public Vector2 Look;   // right stick: X = yaw, Y = pitch
        public InputButtons Buttons;
        public readonly bool Down(InputButtons b) => (Buttons & b) != 0;
    }

    [Flags]
    public enum InputButtons
    {
        None = 0, Jump = 1, Fire = 2, AltForm = 4, Boost = 8,
        WeaponNext = 16, WeaponPrev = 32, Morph = 64, Back = 128
    }

    // A dynamic simulation object. Keeps its current AND previous transform so the renderer can
    // interpolate between ticks. Concrete entities (player, projectile, platform, pickup...) derive
    // this and implement Tick. ModelName is what the presentation layer draws for it.
    public abstract class SimEntity
    {
        public int Id { get; internal set; }
        public bool Alive = true;
        public string ModelName = "";
        public Vector3 Position, Facing = -Vector3.UnitZ, Up = Vector3.UnitY;
        public Vector3 PrevPosition, PrevFacing = -Vector3.UnitZ, PrevUp = Vector3.UnitY;

        // snapshot the transform as "previous" at the start of a tick (for render interpolation)
        internal void SavePrev() { PrevPosition = Position; PrevFacing = Facing; PrevUp = Up; }

        public abstract void Tick(float dt, in InputState input, SimWorld world);
    }

    // A simple entity that integrates a velocity each tick -- base for projectiles, moving
    // platforms, drifting props, etc. Concrete enough to prove the framework end-to-end.
    public class KinematicEntity : SimEntity
    {
        public Vector3 Velocity;
        public float LifeSeconds = float.PositiveInfinity; // despawn after this long (default: never)
        private float _age;

        public override void Tick(float dt, in InputState input, SimWorld world)
        {
            Position += Velocity * dt;
            _age += dt;
            if (_age >= LifeSeconds) Alive = false;
        }
    }

    // One entity's interpolated transform for a single rendered frame. The renderer maps ModelName
    // to a baked mesh and builds a matrix from Position/Facing/Up (as EntityMatrix does today).
    public readonly struct RenderItem
    {
        public readonly string Model;
        public readonly Vector3 Position, Facing, Up;
        public RenderItem(string model, Vector3 pos, Vector3 facing, Vector3 up)
        { Model = model; Position = pos; Facing = facing; Up = up; }
    }

    // The presentation layer consumes this each frame. Reused across frames (cleared + refilled) so
    // the render path stays zero-alloc, per the project's zero-alloc-main-loop rule.
    public sealed class RenderSnapshot
    {
        public readonly List<RenderItem> Items = new();
        public Vector3 CameraPosition, CameraFacing = -Vector3.UnitZ, CameraUp = Vector3.UnitY;
        public void Clear() { Items.Clear(); }
    }

    // The mutable world: a set of dynamic entities advanced by fixed ticks, plus the static room it
    // lives in (RoomName -> the baked geometry the renderer already produces). Spawns are queued and
    // integrated at the next tick boundary so iteration during a tick is safe.
    public sealed class SimWorld
    {
        private readonly List<SimEntity> _entities = new();
        private readonly List<SimEntity> _spawnQueue = new();
        private int _nextId = 1;

        public long TickCount { get; private set; }
        public string? RoomName { get; set; }
        public IReadOnlyList<SimEntity> Entities => _entities;

        // Camera state lives in the sim (the player controller will drive it); the presentation
        // reads it from the snapshot. Defaults let the framework run before a player exists.
        public Vector3 CameraPosition, CameraFacing = -Vector3.UnitZ, CameraUp = Vector3.UnitY;
        public Vector3 PrevCameraPosition, PrevCameraFacing = -Vector3.UnitZ, PrevCameraUp = Vector3.UnitY;

        public T Spawn<T>(T entity) where T : SimEntity
        {
            entity.Id = _nextId++;
            _spawnQueue.Add(entity);
            return entity;
        }

        public void Tick(float dt, in InputState input)
        {
            if (_spawnQueue.Count > 0)
            {
                foreach (SimEntity e in _spawnQueue) { e.SavePrev(); _entities.Add(e); }
                _spawnQueue.Clear();
            }
            PrevCameraPosition = CameraPosition; PrevCameraFacing = CameraFacing; PrevCameraUp = CameraUp;
            for (int i = 0; i < _entities.Count; i++) _entities[i].SavePrev();
            for (int i = 0; i < _entities.Count; i++)
                if (_entities[i].Alive) _entities[i].Tick(dt, input, this);
            _entities.RemoveAll(e => !e.Alive);
            TickCount++;
        }

        // Fill `into` with the interpolated render list for a frame at `alpha` in [0,1] between the
        // last two ticks. Reuses the passed snapshot -> no per-frame allocation.
        public void Snapshot(float alpha, RenderSnapshot into)
        {
            into.Clear();
            for (int i = 0; i < _entities.Count; i++)
            {
                SimEntity e = _entities[i];
                if (string.IsNullOrEmpty(e.ModelName)) continue;
                into.Items.Add(new RenderItem(
                    e.ModelName,
                    Vector3.Lerp(e.PrevPosition, e.Position, alpha),
                    NLerpDir(e.PrevFacing, e.Facing, alpha),
                    NLerpDir(e.PrevUp, e.Up, alpha)));
            }
            into.CameraPosition = Vector3.Lerp(PrevCameraPosition, CameraPosition, alpha);
            into.CameraFacing = NLerpDir(PrevCameraFacing, CameraFacing, alpha);
            into.CameraUp = NLerpDir(PrevCameraUp, CameraUp, alpha);
        }

        // normalized lerp of a direction vector (cheap slerp approximation; fine for small per-tick
        // deltas). Falls back to the target if the interpolant collapses.
        private static Vector3 NLerpDir(Vector3 a, Vector3 b, float t)
        {
            Vector3 v = Vector3.Lerp(a, b, t);
            float len = v.Length();
            return len > 1e-6f ? v / len : (b.LengthSquared() > 1e-12f ? Vector3.Normalize(b) : -Vector3.UnitZ);
        }
    }
}
