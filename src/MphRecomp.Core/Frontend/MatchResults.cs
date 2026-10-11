using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using MphRecomp.Multiplayer;

// A finished match's results, kept for the vanilla multiplayer menu's RESULTS page (47; MultiplayerMenu). The match screen
// captures them from MphRead's match state when a match the menu started ends (MatchSession), then hands back to the menus,
// which open on RESULTS. Process-wide like the game's own result arrays: the shells run the match and the menus in one
// process (Android: CampaignActivity then GameActivity; Windows: DesktopApp).
//
// What each stat is, read from USA rev 1 overlay 0 (2026-10-07, M2; the RESULTS fill 0x211FE3C, efficiency 0x2136E1C, best
// weapon 0x2136DC8) and checked in BizHawk: the game's per-seat arrays are MphRead's GameState arrays of the same names.
namespace MphRecomp.Frontend
{
    public sealed class MatchResults
    {
        public sealed record Player(int Seat, Hunter Hunter, string Nickname, bool Bot, int Team, int Place,
            int Points, float Time, int Kills, int Deaths, int SelfDestructs, int DamageDealt, int HeadshotKills,
            int AltFormKills, int FriendKills, int Interceptions, int Field260, int Field268, int Efficiency, int BestWeapon,
            string? Character = null); // the picked character's name (D7: TROOPER, DARK SAMUS), null = the hunter

        // best weapon: the beam index (MphRead BeamType order, 0-8), AltForm when the alt-form kills are the most, None
        // when nothing killed
        public const int AltForm = 9, None = 10;

        public GameMode Mode { get; init; }
        public bool Teams { get; init; }
        public bool FriendlyFire { get; init; }
        public int LocalSeat { get; init; }
        // the game's result order (team rank, then player rank: GameState.ResultSlots), one per player
        public IReadOnlyList<Player> Rows { get; init; } = Array.Empty<Player>();

        public static MatchResults? Last { get; set; }

        public Player? Local
        {
            get
            {
                foreach (Player p in Rows)
                {
                    if (p.Seat == LocalSeat) return p;
                }
                return null;
            }
        }

        // MphRead's match state as it stands (call once the match is over)
        public static MatchResults Capture(int localSeat)
        {
            // the order and places from the points as they stand now: a match client takes the host's last points from a
            // snapshot that can come after its frame's own UpdateState (the Samsung read "tied for 1st place" in seat order
            // beside the host's 1-0 points, release 0.2.0 F1); on a host it changes nothing
            GameState.UpdateState(voices: false);
            var seats = new List<int>();
            for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
            {
                if (PlayerEntity.Players[i].LoadFlags.TestFlag(LoadFlags.SlotActive)) seats.Add(i);
            }
            var rows = new List<Player>();
            for (int r = 0; r < seats.Count; r++)
            {
                int s = GameState.ResultSlots[r];
                if (!seats.Contains(s) || rows.Exists(p => p.Seat == s)) continue;
                rows.Add(Read(s));
            }
            foreach (int s in seats)
            {
                if (!rows.Exists(p => p.Seat == s)) rows.Add(Read(s)); // (an incomplete result order: the rest in seat order)
            }
            return new MatchResults
            {
                Mode = GameState.Mode,
                Teams = MatchSettings.IsTeamMode(GameState.Mode),
                FriendlyFire = GameState.FriendlyFire,
                LocalSeat = localSeat,
                Rows = rows,
            };
        }

        private static Player Read(int s)
        {
            PlayerEntity p = PlayerEntity.Players[s];
            // efficiency: beam damage dealt per beam damage fired, in percent (integer division; 0 without shots)
            int max = GameState.BeamDamageMax[s];
            int efficiency = max == 0 ? 0 : (int)(100L * GameState.BeamDamageDealt[s] / max);
            // best weapon: alt-form kills first, then each beam's kills (only a strictly greater count wins, so the
            // alt form wins ties and the lower beam wins ties between beams); none when the best is 0
            int best = GameState.AltDamageCount[s], weapon = AltForm;
            for (int b = 0; b < 9; b++)
            {
                if (GameState.BeamKills[s, b] > best)
                {
                    best = GameState.BeamKills[s, b];
                    weapon = b;
                }
            }
            if (best == 0) weapon = None;
            // field260 / field268: the mode's own counters (octolith scores / drops, node captures / losses, kills as the
            // prime hunter / primes killed), the same two words in the game
            int field260 = GameState.Mode switch
            {
                GameMode.Nodes or GameMode.NodesTeams => GameState.NodesCaptured[s],
                GameMode.PrimeHunter => GameState.KillsAsPrime[s],
                _ => GameState.OctolithScores[s],
            };
            int field268 = GameState.Mode switch
            {
                GameMode.Nodes or GameMode.NodesTeams => GameState.NodesLost[s],
                GameMode.PrimeHunter => GameState.PrimesKilled[s],
                _ => GameState.OctolithDrops[s],
            };
            return new Player(s, p.Hunter, GameState.Nicknames[s] ?? "", p.IsBot, p.TeamIndex, GameState.Standings[s],
                GameState.Points[s], GameState.Time[s], GameState.Kills[s], GameState.Deaths[s], GameState.Suicides[s],
                GameState.DamageCount[s], GameState.HeadshotKills[s], GameState.AltDamageCount[s], GameState.FriendlyKills[s],
                GameState.OctolithStops[s], field260, field268, efficiency, weapon, p.HostCharacterName);
        }
    }
}
