using System;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // -lockjawhovertest: Sylux's Lockjaw laying bombs in the air while rolling one way (owner 2026-11-15: in DS he slowly
    // loses height doing this, in ours he hardly does). Combat Hall's open middle, the ball put up at LOCKJAW_Y (default
    // ground + 6) and pushed forward, the bomb button tapped every LOCKJAW_EVERY steps (default 10 = 6 a second; one step is
    // a 60 Hz frame) for LOCKJAW_STEPS steps (default 360). Prints the height every tick, every bomb jump (the bomb's
    // self-hit setting Speed.Y) and the average drop per second. LOCKJAW_TRACE=1 prints every step.
    internal static class LockjawHoverTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string room = Environment.GetEnvironmentVariable("LOCKJAW_ROOM") ?? "MP3 PROVING GROUND";
            int every = int.TryParse(Environment.GetEnvironmentVariable("LOCKJAW_EVERY"), out int ev) ? ev : 10;
            int steps = int.TryParse(Environment.GetEnvironmentVariable("LOCKJAW_STEPS"), out int st) ? st : 360;
            float up = float.TryParse(Environment.GetEnvironmentVariable("LOCKJAW_Y"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float u) ? u : 6;
            bool trace = Environment.GetEnvironmentVariable("LOCKJAW_TRACE") == "1";
            // LOCKJAW_JITTER=j: each gap between taps is every +- up to j steps (a person mashing), same seed every run
            int jitter = int.TryParse(Environment.GetEnvironmentVariable("LOCKJAW_JITTER"), out int jt) ? jt : 0;
            // LOCKJAW_TURN: radians a step the stick turns (default 0.03, a wide circle; 0 = straight on)
            float turn = float.TryParse(Environment.GetEnvironmentVariable("LOCKJAW_TURN"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float tn) ? tn : 0.03f;
            var rng = new Random(1115);
            var cooldownField = typeof(PlayerEntity).GetField("_bombCooldown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = room };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Sylux, Nickname = "Player1" });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Bot = true });
            using CampaignHost host = CampaignHost.StartMatch(settings);
            PlayerEntity me = PlayerEntity.Players[0];
            void Step(CampaignInput input)
            {
                PlayerEntity bot = PlayerEntity.Players[1];
                bot.Position = bot.PrevPosition = new Vector3(-40, 0.5f, -40);
                bot.Speed = Vector3.Zero;
                host.Step(input);
            }
            for (int f = 0; f < 900; f++) Step(default);
            Step(new CampaignInput { Buttons = CampaignButtons.Morph });
            for (int f = 0; f < 300 && !(me.IsAltForm && !me.IsMorphing); f++) Step(default);
            for (int f = 0; f < 60; f++) Step(default);
            float ground = me.Position.Y;
            Vector3 at = me.Position.AddY(up);
            me.Position = me.PrevPosition = at;
            me.Speed = Vector3.Zero;
            float startY = at.Y;
            int jumps = 0;
            int lays = 0;
            int landed = -1;
            float prevSpeedY = 0;
            int prevCount = 0;
            int nextPress = 0;
            int presses = 0;
            int lockAt = -1; // the first overuse lockout (cooldown past 20 steps): the spam's own drop is measured up to it
            float lockY = 0;
            for (int f = 0; f < steps; f++)
            {
                bool press = f == nextPress;
                if (press)
                {
                    presses++;
                    nextPress = f + Math.Max(2, every + (jitter > 0 ? rng.Next(-jitter, jitter + 1) : 0));
                }
                var move = new System.Numerics.Vector2(MathF.Sin(f * turn), MathF.Cos(f * turn));
                Step(new CampaignInput { Move = move, Buttons = press ? CampaignButtons.AltAttack : 0 });
                // pinned: shift the ball and its bombs back to the start column each step (same relative motion, no walls)
                var slide = new Vector3(me.Position.X - at.X, 0, me.Position.Z - at.Z);
                me.Position -= slide;
                me.PrevPosition -= slide;
                for (int i = 0; i < me.SyluxBombCount; i++)
                {
                    if (me.SyluxBombs[i] is BombEntity bomb) bomb.Position -= slide;
                }
                if (me.SyluxBombCount > prevCount) lays += me.SyluxBombCount - prevCount;
                prevCount = me.SyluxBombCount;
                float sy = me.Speed.Y;
                bool jump = sy > prevSpeedY + 0.05f;
                if (jump) jumps++;
                prevSpeedY = sy;
                if (lockAt < 0 && (ushort)cooldownField.GetValue(me)! > 20)
                {
                    lockAt = f;
                    lockY = me.Position.Y;
                }
                if (landed < 0 && f > 10 && me.Flags1.TestFlag(PlayerFlags1.Standing)) landed = f;
                if (trace || jump || f % 2 == 1)
                {
                    Vector3 p = me.Position;
                    Console.WriteLine($"[lockjaw] f{f} y {p.Y - ground:0.000} sy {sy:0.0000} bombs {me.SyluxBombCount} cd {cooldownField.GetValue(me)}"
                        + $" h {MathF.Sqrt(me.Speed.X * me.Speed.X + me.Speed.Z * me.Speed.Z):0.000}{(jump ? " JUMP" : "")}{(press ? " press" : "")}");
                }
                if (landed >= 0) break;
            }
            int ran = landed >= 0 ? landed + 1 : steps;
            float drop = startY - me.Position.Y;
            Console.WriteLine($"[lockjaw] every {every} jitter {jitter}: {presses} taps, {lays} bombs, {jumps} bomb jumps in {ran} steps, height {up:0.00} -> {me.Position.Y - ground:0.00}"
                + $" ({drop / (ran / 60f):0.000} a second down){(landed >= 0 ? $", landed at step {landed} ({me.Position.X:0.0}, {me.Position.Z:0.0})" : "")}"
                + (lockAt > 0 ? $"; overuse lockout at step {lockAt}, height {startY - ground:0.00} -> {lockY - ground:0.00} before it ({(startY - lockY) / (lockAt / 60f):0.000} a second down)" : ""));
        }
    }
}
