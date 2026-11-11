using System;
using System.Numerics;
using MphRecomp.Campaign;

namespace MphRecomp.Input
{
    // STICK CURVE (CONTROLS > AIM > MOVE, owner 2026-11-08: "subtle movements are subtle and quick movements are quick"). The
    // DS d-pad is all or nothing, and our move stick past CampaignInput.MoveDeadZone (0.35 on each axis) was a whole press:
    // the lightest tilt walked and rolled at the whole traction and top speed. MODERN turns the tilt into a push
    // (CampaignInput.MovePush, PlayerStickPush.cs) on a ramp: a light tilt creeps, half a tilt is about half the speed
    // (owner 11-08 took the ramp over the first, squared curve, whose middle was sluggish), and from Edge out it is the
    // whole press, so full tilt moves, turns and tops out exactly as vanilla. MODERN also steers in any direction (owner
    // 11-08, "360 movement when you are not using dpad"): CampaignInput.MoveDir holds the stick's angle and each axis
    // takes its part of the press (Axes), which lands on the d-pad's own presses at its eight directions, so keys and
    // the d-pad move exactly as before. CLASSIC leaves the input alone.
    public static class StickCurve
    {
        public const string Modern = "modern", Classic = "classic";
        public static readonly string[] Options = { Modern, Classic };

        public const float DeadZone = 0.15f; // a radial dead zone: below it the stick is at rest
        public const float Edge = 0.92f; // from here out the whole push (a worn stick's rim, a square gate's sides)
        public const float Floor = 0.15f; // the push just past the dead zone
        public const float Power = 1; // the ramp between: Floor + (1 - Floor) * t^Power (the first curve was 2)

        // the push for a tilt (0..1+), 0 at rest, 1 = all the way
        public static float Push(float tilt)
        {
            if (tilt <= DeadZone) return 0;
            float t = Math.Clamp((tilt - DeadZone) / (Edge - DeadZone), 0, 1);
            return Floor + (1 - Floor) * MathF.Pow(t, Power);
        }

        // the push in CampaignInput's 16ths: 0 = the whole push, 1..15 = part of it
        public static byte Level(float push)
        {
            int n = (int)MathF.Round(push * 16);
            return n >= 16 ? (byte)0 : (byte)Math.Max(n, 1);
        }

        // MoveDir: 0 = no stick direction (the d-pad's bits steer, as vanilla), 1..DirSteps = the stick's angle in steps of
        // 360 / DirSteps degrees counterclockwise from right (+Y = forward). 120 steps: the eight d-pad directions land on
        // steps exactly, and it fits the online input's spare 7 bits (NetInput)
        public const int DirSteps = 120;

        // each axis's part of the press for a direction, right (+) / forward (+), at most 1: the stick's own direction
        // stretched to the d-pad's square, so a 45-degree tilt presses both axes whole like the d-pad's diagonal and a
        // straight one only its own. Rounded to 4096ths, so every device gets the very same numbers (replays, online)
        private static readonly float[] _right = new float[DirSteps], _forward = new float[DirSteps];

        static StickCurve()
        {
            for (int i = 0; i < DirSteps; i++)
            {
                double a = i * 2 * Math.PI / DirSteps;
                double c = Math.Cos(a), sn = Math.Sin(a), big = Math.Max(Math.Abs(c), Math.Abs(sn));
                _right[i] = (float)(Math.Round(c / big * 4096) / 4096);
                _forward[i] = (float)(Math.Round(sn / big * 4096) / 4096);
            }
        }

        public static bool Axes(byte dir, out float right, out float forward)
        {
            if (dir == 0 || dir > DirSteps)
            {
                right = forward = 0;
                return false;
            }
            right = _right[dir - 1];
            forward = _forward[dir - 1];
            return true;
        }

        // the stick's push and direction onto a player for its next step (the local seat, a remote one's input)
        public static void ToPlayer(MphRead.Entities.PlayerEntity p, byte push, byte dir)
        {
            p.HostMovePush = CampaignInput.PushScale(push); // a part push walks and rolls gentler (PlayerStickPush.cs)
            p.HostMoveAnalog = Axes(dir, out float right, out float forward); // and steers any way (PlayerStickPush.cs)
            p.HostMoveRight = right;
            p.HostMoveForward = forward;
        }

        // MODERN: Move becomes the d-pad direction it points nearest (+-1 an axis, nothing at rest: the d-pad's bits, which
        // still pick the animations), MoveDir the stick's own direction and MovePush how far it leans. Keys and the d-pad
        // come in as whole presses on the eight directions, so they move as they always did
        public static void Apply(ref CampaignInput input)
        {
            Vector2 m = input.Move;
            float tilt = m.Length();
            if (tilt <= DeadZone)
            {
                input.Move = Vector2.Zero;
                input.MovePush = 0;
                input.MoveDir = 0;
                return;
            }
            float angle = MathF.Atan2(m.Y, m.X); // 0 = right, counterclockwise; +Y = forward
            // the nearest of the eight directions, by 45-degree sector
            int sector = (int)MathF.Round(angle / (MathF.PI / 4));
            sector = ((sector % 8) + 8) % 8;
            int x = sector is 7 or 0 or 1 ? 1 : sector is 3 or 4 or 5 ? -1 : 0;
            int y = sector is 1 or 2 or 3 ? 1 : sector is 5 or 6 or 7 ? -1 : 0;
            input.Move = new Vector2(x, y);
            input.MovePush = Level(Push(tilt));
            int step = (int)MathF.Round(angle / (2 * MathF.PI) * DirSteps);
            input.MoveDir = (byte)(((step % DirSteps) + DirSteps) % DirSteps + 1);
        }
    }
}
