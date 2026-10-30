using System;
using System.Collections.Generic;
using System.Diagnostics;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // -altpickuptest [arena=<name>]: a beam picked up in alt form is equipped even when fire was held while morphing
    // (owner 2026-10-26, as Trace: the first beam of a match sometimes didn't switch in the ball). The game only updates
    // PlayerFlags2.Shooting on foot, so it stayed set for the whole ball session and the pickup sent the beam to slot 2.
    //   - ball: hold fire, morph, pick up the Volt Driver (first beam) -> equipped; then the Imperialist (Trace's affinity) -> equipped
    //   - foot: hold fire, pick up the Volt Driver -> not equipped (vanilla: no switch under your fingers while firing)
    internal static class AltPickupTest
    {
        public static void Run(string[] args)
        {
            string arenaName = "Combat Hall";
            foreach (string arg in args[1..])
            {
                if (arg.StartsWith("arena=", StringComparison.OrdinalIgnoreCase))
                {
                    arenaName = arg[6..];
                }
                else
                {
                    throw new ArgumentException($"unknown option {arg}");
                }
            }
            Trace.Listeners.Clear(); // MphRead's Debug.Asserts would end this Debug process (as ReplayTest)
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            RoomMetadata arena = MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}");
            var errors = new List<string>();
            Check(arena, ball: true, errors);
            Check(arena, ball: false, errors);
            foreach (string error in errors)
            {
                Console.WriteLine($"FAIL {error}");
            }
            Console.WriteLine(errors.Count == 0 ? "altpickuptest PASS" : $"altpickuptest FAIL ({errors.Count})");
        }

        private static void Check(RoomMetadata arena, bool ball, List<string> errors)
        {
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena.Name, TimeLimitSeconds = 60 };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Trace, Nickname = "Player1" });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Bot = true, BotLevel = 0 });
            CampaignHost host = CampaignHost.StartMatch(settings);
            PlayerEntity p = PlayerEntity.Players[0];
            for (int i = 0; i < 300 && (p.Health == 0 || i < 60); i++)
            {
                host.Step(new CampaignInput { Buttons = i % 2 == 0 ? CampaignButtons.Shoot : 0 }); // spawn
            }
            for (int i = 0; i < 30; i++) host.Step(default);
            string name = ball ? "ball" : "foot";
            for (int i = 0; i < 20; i++) host.Step(new CampaignInput { Buttons = CampaignButtons.Shoot });
            if (ball)
            {
                host.Step(new CampaignInput { Buttons = CampaignButtons.Shoot | CampaignButtons.Morph });
                for (int i = 0; i < 60 && !p.IsAltForm; i++) host.Step(default);
                if (!p.IsAltForm)
                {
                    errors.Add($"{name}: never reached alt form");
                    return;
                }
            }
            bool latched = p.Flags2.TestFlag(PlayerFlags2.Shooting);
            Console.WriteLine($"  {name}: alt {p.IsAltForm} Shooting {latched} weapon {p.CurrentWeapon} gun {p.GunAnimation}");
            if (!latched)
            {
                errors.Add($"{name}: Shooting isn't set, so this doesn't test the case");
                return;
            }
            p.HostPickUpWeapon(ItemType.VoltDriver);
            Console.WriteLine($"  {name}: after the Volt Driver -> {p.CurrentWeapon}");
            if (ball && p.CurrentWeapon != BeamType.VoltDriver)
            {
                errors.Add($"ball: first beam not equipped ({p.CurrentWeapon})");
            }
            if (!ball && p.CurrentWeapon == BeamType.VoltDriver)
            {
                errors.Add("foot: the Volt Driver was equipped while firing (vanilla keeps the current beam)");
            }
            if (ball)
            {
                p.HostPickUpWeapon(ItemType.AffinityWeapon);
                Console.WriteLine($"  {name}: after the Imperialist -> {p.CurrentWeapon}");
                if (p.CurrentWeapon != BeamType.Imperialist)
                {
                    errors.Add($"ball: Trace's Imperialist not equipped ({p.CurrentWeapon})");
                }
            }
        }
    }
}
