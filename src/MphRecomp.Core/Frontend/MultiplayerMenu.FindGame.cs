using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRecomp.Multiplayer;

// ONLINE PLAY's FIND GAME (Social track O6, OnlineMenus.Find.cs) from the setup's side: which arenas this device can play in each
// mode (the search's arena lists, the server's VOTE FOR AN ARENA picks from what every player has), and the match the chosen
// host opens: the mode's default rules (as a new CREATE GAME's, PickMode), the voted arena, solo (capture is teams), no bots,
// seat 0 = this player's last SELECT HUNTER pick. The setup itself is left as it was.
namespace MphRecomp.Frontend
{
    public sealed partial class MultiplayerMenu
    {
        // per mode (FindMode names = the solo GameMode's), the open arenas' room names (no RANDOM; imported arenas once
        // RefreshArenas has them)
        public static Dictionary<string, List<string>> FindArenas()
        {
            var arenas = new Dictionary<string, List<string>>();
            foreach (ModeInfo m in Modes)
            {
                var names = new List<string>();
                for (int i = 0; i < ArenaRooms.Length; i++)
                {
                    if (ArenaRooms[i] == RandomArena || ArenaModes[i][m.Id - 2] != '1' || !(_unlocks?.ArenaOpen(ArenaRooms[i]) ?? true)) continue;
                    // (only what a match accepts)
                    if (Metadata.GetRoomById(ArenaRooms[i], noThrow: true) is RoomMetadata room && MatchArenas.IsArena(room.Name)) names.Add(room.Name);
                }
                arenas[m.Solo.ToString()] = names;
            }
            return arenas;
        }

        // FIND GAME's SEARCH: the arena list as the setup has it now (arenas imported since it was last open, the EXTRA ARENAS rule)
        public void RefreshFindArenas() => RefreshArenas();

        // the found match's settings for its host (arena "" or unknown here: any of the mode's open arenas)
        public static MatchSettings BuildFindMatch(string mode, string arena)
        {
            ModeInfo m = Modes.FirstOrDefault(x => x.Solo.ToString() == mode) ?? throw new ArgumentException("no mode " + mode);
            Setup saved = _setup;
            bool savedDefaults = _defaultsSet;
            try
            {
                _setup = new Setup
                {
                    MyHunter = saved.MyHunter, MySkin = saved.MySkin, MyCharacter = saved.MyCharacter, Nickname = saved.Nickname, Rank = saved.Rank,
                };
                _defaultsSet = false;
                PickMode(m.Id);
                int at = MatchArenas.IsArena(arena) ? Array.FindIndex(ArenaRooms, r => r != RandomArena && Metadata.GetRoomById(r, noThrow: true)?.Name == arena) : -1;
                // (N32: the voted arena must take the mode, e.g. no Capture/Bounty/Nodes/Defender in an Echoes arena)
                if (at >= 0 && !ArenaAllowed(at)) at = -1;
                if (at < 0)
                {
                    // (no vote: a retail arena, which every player has; imported ones only come from a vote over everyone's lists)
                    int[] open = Enumerable.Range(0, ArenaRooms.Length).Where(i => ArenaRooms[i] != RandomArena && ArenaAllowed(i)
                        && Metadata.GetRoomById(ArenaRooms[i], noThrow: true) is RoomMetadata r && MatchArenas.IsArena(r.Name)
                        && !ExtraArenas.IsExtra(r.Name)).ToArray();
                    if (open.Length == 0) throw new InvalidOperationException("no open arena for " + m.Name);
                    at = open[_random.Next(open.Length)];
                }
                _setup.Arena = at;
                Seat me = _setup.Seats[0];
                me.Hunter = saved.MyHunter;
                me.Skin = saved.MySkin;
                me.Character = saved.MyCharacter;
                return BuildMatch();
            }
            finally
            {
                _setup = saved;
                _defaultsSet = savedDefaults;
            }
        }
    }
}
