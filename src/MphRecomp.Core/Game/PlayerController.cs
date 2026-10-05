using System;
using System.Numerics;
using MphRecomp.Sim;

namespace MphRecomp.Game
{
    // Collision the player queries. The renderer implements this against the room's collision mesh
    // (the _tri raycasts already in RenderActivity); headless tests pass null for free movement.
    public interface ICollision
    {
        // Highest walkable surface at or below `pos` (world Y), or float.NegativeInfinity if none.
        float FloorBelow(Vector3 pos);
        // Is there a wall within `dist` along `dir` (unit, horizontal) from `pos`?
        bool Blocked(Vector3 pos, Vector3 dir, float dist);
    }

    // First-person player: reads the action input, turns (yaw/pitch), moves along the ground, and
    // drives the sim camera. Gravity + floor-snap + wall-slide run only when an ICollision is
    // supplied (so this is the same walk logic the renderer does, but abstracted from its geometry
    // source). Not drawn (ModelName stays empty) -- the player IS the camera in first person.
    public sealed class PlayerController : SimEntity
    {
        public float MoveSpeed = 8f;      // world units / second
        public float LookSpeed = 2.2f;    // radians / second at full stick
        public float EyeHeight = 1.2f;
        public float Gravity = 20f;
        public float Yaw, Pitch;
        public ICollision? Collision;
        public Weapon? Weapon; // equipped weapon; fires on the Fire action

        private float _velY;

        public PlayerController(ICollision? collision = null) => Collision = collision;

        public override void Tick(float dt, in InputState input, SimWorld world)
        {
            // --- look ---
            Yaw += input.Look.X * LookSpeed * dt;
            Pitch = Math.Clamp(Pitch + input.Look.Y * LookSpeed * dt, -1.5f, 1.5f);
            float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw), cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
            Facing = new Vector3(sy * cp, sp, -cy * cp); // matches the renderer's look-vector convention
            Up = Vector3.UnitY;

            // --- ground move (yaw only) ---
            Vector3 fwd = new(sy, 0f, -cy);
            Vector3 right = new(cy, 0f, sy);
            Vector3 move = (fwd * input.Move.Y + right * input.Move.X) * MoveSpeed * dt;
            if (Collision != null)
            {
                // per-axis so we slide along walls instead of stopping dead
                float skin = 0.2f;
                if (move.X != 0f && !Collision.Blocked(Position, new Vector3(Math.Sign(move.X), 0, 0), MathF.Abs(move.X) + skin))
                    Position.X += move.X;
                if (move.Z != 0f && !Collision.Blocked(Position, new Vector3(0, 0, Math.Sign(move.Z)), MathF.Abs(move.Z) + skin))
                    Position.Z += move.Z;

                // gravity + floor snap
                _velY -= Gravity * dt;
                Position.Y += _velY * dt;
                float floor = Collision.FloorBelow(Position);
                if (!float.IsNegativeInfinity(floor) && Position.Y < floor + EyeHeight)
                {
                    Position.Y = floor + EyeHeight;
                    _velY = 0f;
                }
            }
            else
            {
                Position += move; // free movement (no collision supplied)
            }

            // --- weapon ---
            if (Weapon != null)
            {
                Weapon.Tick(dt);
                if (input.Down(InputButtons.Fire))
                    Weapon.Fire(world, Position + Facing * 0.5f, Facing, this); // no-op if on cooldown/out of ammo
            }

            // --- drive the camera ---
            world.CameraPosition = Position;
            world.CameraFacing = Facing;
            world.CameraUp = Up;
        }
    }
}
