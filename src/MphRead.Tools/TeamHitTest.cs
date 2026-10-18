using System;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // -teamhittest: friendly fire off, a teammate's hit is no hit at all (PlayerEntity.HostTeamHitsIgnored, owner
    // 2026-10-11): no health loss, red flash, knockback or invulnerability, so an opponent's hit right after still lands.
    // With MatchSettings.TeamHitsIgnored = false (replays before format 31) it's vanilla's: flash + knockback + invuln.
    internal static class TeamHitTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            bool pass = Check(ignored: true) & Check(ignored: false);
            Console.WriteLine(pass ? "PASS teamhittest" : "FAIL teamhittest");
        }

        private static bool Check(bool ignored)
        {
            var settings = new MatchSettings { Mode = GameMode.BattleTeams, Arena = MatchArenas.Find("Combat Hall")!.Name,
                FriendlyFire = false, TeamHitsIgnored = ignored ? null : false };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Nickname = "Player1", Team = 0 });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Bot = true, Team = 0 });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Spire, Bot = true, Team = 1 });
            using CampaignHost host = CampaignHost.StartMatch(settings);
            PlayerEntity me = PlayerEntity.Players[0];
            PlayerEntity mate = PlayerEntity.Players[1];
            PlayerEntity enemy = PlayerEntity.Players[2];
            for (int f = 0; f < 900; f++)
            {
                host.Step(default); // past the spawn invulnerability
            }
            string label = ignored ? "on " : "off";
            if (me.TeamIndex != mate.TeamIndex || mate.TeamIndex == enemy.TeamIndex || mate.Health == 0)
            {
                Console.WriteLine($"[{label}] FAIL: setup (teams {me.TeamIndex}/{mate.TeamIndex}/{enemy.TeamIndex}, hp {mate.Health})");
                return false;
            }
            int hp = mate.Health;
            Vector3 speed = mate.Speed;
            mate.TakeDamage(20, DamageFlags.None, new Vector3(0.3f, 0.1f, 0), me);
            bool flash = mate.HostDamageFlash;
            bool knocked = mate.Speed != speed;
            int afterTeam = mate.Health;
            mate.TakeDamage(20, DamageFlags.None, null, enemy); // right after: lands unless the team hit gave invulnerability
            bool enemyLanded = mate.Health < afterTeam;
            Console.WriteLine($"[{label}] team hit: hp {hp}->{afterTeam}, flash {flash}, knockback {knocked};"
                + $" opponent's hit right after landed {enemyLanded}");
            bool ok = afterTeam == hp && (ignored ? !flash && !knocked && enemyLanded : flash && knocked && !enemyLanded);
            if (!ok)
            {
                Console.WriteLine($"[{label}] FAIL: expected {(ignored ? "no flash, no knockback, opponent's hit lands"
                    : "vanilla's flash + knockback + invulnerability")}");
            }
            return ok;
        }
    }
}
