using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Find Game on the server (docs/DEDICATED_SERVERS.md D3): when a match's vote is over and this server can run it, the
    // hub opens the game itself instead of naming a player to host it. Its match host plays Find's rules in the voted arena
    // and starts the match once the matched players are in (SecondHunt.MatchHost/ControlMode.cs --find), as the host's
    // device did before. Every player of the match gets FindState.Game and joins it, which clients from 0.3.0 already do
    // for a game a search fits, so this needs no app update. Nobody owns the lobby until that first match starts; then the
    // player the vote would have made the host owns it (PLAY AGAIN, rules), as in a served CREATE game. When the server
    // can't run it (served games off, at the cap, draining, a different version, an arena the server lacks, more players than
    // a game seats), a player hosts it as before.
    public sealed partial class Hub
    {
        // the arenas the match host has (its --arenas at start); null = unknown, so no Find Game match is served
        private HashSet<string>? _servedArenas;

        private void ReadServedArenas()
        {
            try
            {
                var psi = new ProcessStartInfo(Served.Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                Served.Prefix.ForEach(psi.ArgumentList.Add);
                psi.ArgumentList.Add("--arenas");
                AddData(psi);
                using Process p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60_000))
                {
                    p.Kill();
                    throw new IOException("no answer in 60 s");
                }
                var arenas = output.Result.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet();
                if (p.ExitCode != 0 || arenas.Count == 0)
                {
                    throw new IOException($"exit {p.ExitCode}, {arenas.Count} arenas");
                }
                _servedArenas = arenas;
                Console.WriteLine($"served games: Find Game in {arenas.Count} arenas");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"served games: Find Game player-hosted: the match host didn't list its arenas ({ex.Message})");
            }
        }

        // (under _gamesLock: ResolveVote) the vote is over: the server runs the match when it can (true = done, the players
        // were sent to the game)
        private bool TryServeFind(FindMatch m)
        {
            int players = m.Players.Count;
            if (m.Arena.Length == 0 || _servedArenas?.Contains(m.Arena) != true || players > RelayWire.MaxPlayers)
            {
                return false;
            }
            GameInfo info = Clean(new GameInfo
            {
                Mode = m.Mode, Arena = m.Arena, MaxPlayers = players, Friends = false, Rivals = false, Public = false, Version = m.Version,
                Mods = GameInfo.NoMods, ExtraPickups = !m.NoExtraPickups, ExtraCharacters = !m.NoExtraCharacters,
            });
            if (!CanServe(info))
            {
                return false;
            }
            info.Served = true;
            string owner = FindRules.PickHost(m.Players, Ping);
            string id = NewId(_games.ContainsKey);
            Relay.Room room = _relay.OpenRoom(id);
            _relay.Keep(room, true);
            var g = new Game(id, owner, info, room) { FindAuto = true };
            var extra = new List<string> { "--find", "--expect", players.ToString() };
            if (m.NoExtraPickups)
            {
                extra.Add("--no-extra-pickups");
            }
            if (m.NoExtraCharacters)
            {
                extra.Add("--no-extra-characters");
            }
            g.MatchHost = StartMatchHost(g, _relay.AddMember(room, 0), -1, extra);
            if (g.MatchHost == null)
            {
                _relay.CloseRoom(room);
                return false;
            }
            _games[id] = g;
            _matches.Remove(m.Id);
            foreach (string p in m.Players)
            {
                _matchOf.Remove(p);
                g.Invited.Add(p);
            }
            foreach (string p in m.Players)
            {
                PushFind(p, new FindEvent
                {
                    State = FindState.Game, GameId = id, FindId = m.Id, Mode = m.Mode, Arena = m.Arena, HostCard = Card(_store.Get(owner)!, p),
                    Players = Cards(m.Players, p), Votes = m.Votes.Count,
                });
            }
            Console.WriteLine($"served game {id}: Find Game {m.Mode} in {m.Arena} for {players}");
            return true;
        }

        // (under _gamesLock) a Find Game match the server opened has started: the lobby gets its owner now (the vote's pick,
        // or the first player in when that one isn't), so the next match can be started from it
        private void FindOwnerAfterStart(Game g)
        {
            if (!g.FindAuto || g.MatchHost == null || g.Slots.Count == 0)
            {
                return;
            }
            g.FindAuto = false;
            (int slot, string owner) = g.Slots.FirstOrDefault(s => s.Value == g.Host) is { Value: not null } mine ? (mine.Key, mine.Value)
                : (g.Slots.First().Key, g.Slots.First().Value);
            g.Host = owner;
            TellOwner(g.MatchHost, slot);
            Store.Player card = _store.Get(owner)!;
            foreach (string m in g.Slots.Values)
            {
                Push(m, new MemberEvent { GameId = g.Id, Player = Card(card, m), Slot = slot, Joined = true, Owner = owner });
            }
            Console.WriteLine($"served game {g.Id}: the lobby's owner is slot {slot} now");
        }
    }
}
