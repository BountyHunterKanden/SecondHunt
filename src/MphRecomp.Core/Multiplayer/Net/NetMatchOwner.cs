using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MphRead;

namespace MphRecomp.Multiplayer.Net
{
    // The lobby's owner in a served game (docs/DEDICATED_SERVERS.md section 4). There the match host is a server process with
    // no player of its own, so the player who opened the game is a joiner like everyone else; its device still runs the lobby
    // (MODE / RULES, teams, bots, START) by sending OWNER messages, and the match host takes them from that relay slot only
    // (OwnerSlot, from the server). Owning the lobby gives no say in the match itself.
    //   OWNER (joiner -> host; a new type in protocol 19, which an older host ignores):
    //     op 1 SETTINGS: the lobby as the owner wants it, in seat order (LanSession.WriteSettings): the rules, the bots and
    //          each seat's team; the joiners' own seats stay theirs (hunter, look)
    //     op 2 START: start the match; the ones not back from the results go out with this reason
    // On the owner's device the lobby calls the host uses (SetTeam, AddBot, ReplaceBot, RemoveBot, KickNotBack, SendStart)
    // send these instead, and the host's next LOBBY shows the result.
    public sealed partial class NetMatchSession
    {
        private const byte MsgOwner = 13;
        private const byte OwnerOpSettings = 1, OwnerOpStart = 2;

        // host: the relay slot of the lobby's owner (-1 = none: in a player-hosted game the host owns its lobby)
        public int OwnerSlot { get; set; } = -1;
        // client: this player owns the lobby of a served game (its menus show the host's controls)
        public bool IsOwner { get; set; }
        // this device runs the lobby: the host's or the owner's controls
        public bool RunsLobby => IsHost || IsOwner;

        // host: the owner's START not taken yet (its reason for the ones not back), null = none
        private string? _ownerStart;
        // client: the host's last LOBBY as sent (with the bots' levels): the owner's edits start from it
        private MatchSettings? _hostLobby;
        // client (owner): KickNotBack's reason, sent with the next START
        private string _ownerKickReason = "the game started without you.";

        // host: the owner pressed START (the match host starts the match itself, so it can tell the server); false = not
        public bool TakeOwnerStart(out string reason)
        {
            string? asked = Interlocked.Exchange(ref _ownerStart, null);
            reason = asked ?? "";
            return asked != null;
        }

        // host: the relay slot of the joiner on this seat of the match (-1 = none: a bot, or it left)
        public int SlotOfSeat(int seat)
        {
            lock (_lock)
            {
                return _joiners.FirstOrDefault(j => !j.Left && j.Seat == seat)?.Slot ?? -1;
            }
        }

        // owner: the lobby's rules from this template (the owner's MODE / RULES tabs); bots: its bots too (else the lobby's
        // stay); the joiners' seats and teams stay
        public void OwnerRules(MatchSettings rules, bool bots = false) => OwnerEdit(s =>
        {
            CopyRules(rules, s);
            if (bots)
            {
                s.Players.RemoveAll(p => p.Bot);
                s.Players.AddRange(rules.Players.Where(p => p.Bot).Take(MatchSettings.MaxSeats - s.Players.Count));
            }
        });

        // owner: the lobby as it is now (a copy, with the bots' levels): what the owner's menus show and edit
        public MatchSettings? OwnerLobby()
        {
            lock (_lock)
            {
                return _hostLobby == null ? null : Clone(_hostLobby);
            }
        }

        // client: the server says who owns the lobby now (mine: this player); the menus redraw
        public void OwnerMoved(bool mine)
        {
            if (mine)
            {
                IsOwner = true;
                SetStatus("this player owns the lobby now");
            }
            PlayersChanged?.Invoke();
        }

        // owner: the other people in the lobby as the match host's last LOBBY shows them (the owner's Joined, NotBack and
        // StillChoosing: the host's come from its joiners)
        private List<(string Name, LobbySeat Seat)> OwnerOthers()
        {
            var others = new List<(string, LobbySeat)>();
            MatchSettings? s = Settings;
            IReadOnlyList<LobbySeat> lobby = Lobby;
            if (s == null)
            {
                return others;
            }
            for (int i = 0; i < s.Players.Count && i < lobby.Count; i++)
            {
                if (i != s.LocalSeat && !s.Players[i].Bot && !lobby[i].Bot)
                {
                    others.Add((s.Players[i].Nickname ?? "", lobby[i]));
                }
            }
            return others;
        }

        private void OwnerEdit(Action<MatchSettings> edit)
        {
            MatchSettings? want = OwnerLobby();
            if (want == null || Started)
            {
                return;
            }
            edit(want);
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgOwner);
            w.Write(OwnerOpSettings);
            LanSession.WriteSettings(w, want);
            _net.Send(_hostSlot, ms.ToArray());
        }

        private void OwnerSendStart()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgOwner);
            w.Write(OwnerOpStart);
            w.Write(_ownerKickReason);
            byte[] start = ms.ToArray();
            for (int i = 0; i < 3; i++)
            {
                _net.Send(_hostSlot, start); // (UDP: three times; the host takes the first)
            }
            SetStatus("asked the server to start");
        }

        private void OnOwner(BinaryReader r, int from)
        {
            if (OwnerSlot < 0 || from != OwnerSlot)
            {
                return;
            }
            byte op = r.ReadByte();
            if (op == OwnerOpStart)
            {
                string reason = r.ReadString();
                if (!_hostStarted)
                {
                    Volatile.Write(ref _ownerStart, reason.Length > 64 ? reason[..64] : reason);
                }
                return;
            }
            if (op != OwnerOpSettings)
            {
                return;
            }
            MatchSettings want = LanSession.ReadSettings(r);
            lock (_lock)
            {
                if (_hostStarted || _template == null || !Enum.IsDefined(want.Mode) || MatchArenas.Find(want.Arena) == null
                    || !ExtraArenas.Have(want.Arena)) // (an imported arena the server doesn't have: the lobby stays as it is)
                {
                    return;
                }
                CopyRules(want, _template);
                int humans = _joiners.Count(j => !j.Left);
                _template.Players.RemoveAll(p => p.Bot);
                foreach (MatchPlayer p in want.Players.Where(p => p.Bot).Take(MatchSettings.MaxSeats - humans))
                {
                    _template.Players.Add(new MatchPlayer
                    {
                        Hunter = Enum.IsDefined(p.Hunter) ? p.Hunter : Hunter.Samus, Bot = true,
                        BotLevel = Math.Clamp(p.BotLevel, 0, 2), Team = p.Team is 0 or 1 ? p.Team : -1,
                        Nickname = p.Nickname is { Length: > 0 and <= 16 } n ? n : p.Hunter.ToString().ToUpperInvariant(),
                        RankLevel = -1, Character = p.Character, Skin = p.Skin, ColourPick = p.ColourPick,
                    });
                }
                foreach (Joiner j in _joiners.Where(j => !j.Left && j.Seat < want.Players.Count && !want.Players[j.Seat].Bot))
                {
                    int team = want.Players[j.Seat].Team;
                    j.Player.Team = team is 0 or 1 ? team : -1;
                }
                _seatLimit = Math.Max(_seatLimit, _template.Players.Count + humans);
                Settings = Build();
            }
            BroadcastLobby();
            PlayersChanged?.Invoke();
        }

        // the lobby's rules (what NetMatchSession.Build takes from the template)
        private static void CopyRules(MatchSettings from, MatchSettings to)
        {
            to.Mode = from.Mode;
            to.Arena = from.Arena;
            to.PointGoal = from.PointGoal;
            to.TimeLimitSeconds = from.TimeLimitSeconds;
            to.TimeGoalSeconds = from.TimeGoalSeconds;
            to.DamageLevel = from.DamageLevel;
            to.FriendlyFire = from.FriendlyFire;
            to.OctolithReset = from.OctolithReset;
            to.RadarPlayers = from.RadarPlayers;
            to.AffinityWeapons = from.AffinityWeapons;
            to.Crates = from.Crates;
            to.Breakables = from.Breakables;
            to.PirateBlaster = from.PirateBlaster;
            to.ExtraCharacters = from.ExtraCharacters;
        }

        private static MatchSettings Clone(MatchSettings s)
        {
            var ms = new MemoryStream();
            LanSession.WriteSettings(new BinaryWriter(ms), s);
            ms.Position = 0;
            return LanSession.ReadSettings(new BinaryReader(ms));
        }
    }
}
