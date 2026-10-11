using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MphRead;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;

// ONLINE PLAY's FIND GAME (Social track, milestone O6; docs/ONLINE_PLAY.md section 6, the server's queue in
// SecondHunt.Server/HubFind.cs). The hub's big tile opens it when the server has Find Game (0.3.0+; an older server keeps the
// old QUICK MATCH): MODE (any, or one of the seven), REGION (NEARBY: players with a short route through the server first,
// wider the longer the search waits; WORLDWIDE: anyone at once), HUNTER (the last SELECT HUNTER pick; a tap changes it), and
// SEARCH (CANCEL while searching: waited, players in the queue, the route limit now). QUICK MATCH became this page: its
// defaults (any mode, nearby) are the old QUICK MATCH, and a search that fits an open public game goes straight into it, so one
// path does both (the simpler option: no second matchmaker in the client).
// MODES (owner 2026-10-10, release test notes N17): a playlist instead of one mode: a check chip for each of the seven and ALL
// MODES, all on by default, kept in online_settings.json (OnlineSettingsFile.FindModes); the server matches searches on the
// modes they share (FindRules.CommonModes). The arenas each search sends leave out the imported ones (Echoes) with ONLINE
// SETTINGS' NO EXTRA ARENA GAMES or the EXTRA ARENAS rule off, so a group votes Echoes arenas only when everyone has them (N29).
// The PARTY panel: who's in this player's party (the leader first), INVITE FRIENDS (friends and rivals online and free, after a
// yes) and LEAVE PARTY; a party searches together (only the leader's SEARCH), and an invite asks first. Parties live on the
// connection: they end when this player leaves the online pages or a match closes its connection.
// A found match opens VOTE FOR AN ARENA: up to four arena pictures from what every player can play, the countdown and how many
// voted (a tap votes, again changes it); then the server's pick and who hosts (the best connection). The host's device opens
// the game (a normal host_game: the server makes it the match's, private, its players invited) and its lobby, which starts by
// itself once everyone's in (3 s), or after 25 s with whoever made it; the others join it as a JOIN does (their last hunter)
// and wait in the lobby. Nobody can make it in: back to FIND GAME with why.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private static int s_onFind = -1, s_onVote = -1;

        // our callbacks (2720-2739); the MODES chips: the old MODE row's three, then five after the vote's
        private const int FiRegionCall = 2723, FiRegionLeftCall = 2724, FiRegionRightCall = 2725, FiHunterCall = 2726, FiSearchCall = 2727,
            FiInviteCall = 2728, FiLeavePartyCall = 2729;
        private const int FiVoteCall = 2730; // + the arena
        private static readonly int[] FiChipCalls = { 2720, 2721, 2722, 2734, 2735, 2736, 2737, 2738 };
        private const int FindFirstCall = 2720, FindLastCall = 2738;
        private const int VoteCards = 4, PartyRows = 4, FindChips = 8; // (the seven modes, then ALL MODES)
        // the found game's host starts it this long after everyone's in, or with whoever made it this long after it opened
        private const long FindStartDelayMs = 3000, FindStartLateMs = 25000;

        // items (-1 = none)
        private int _fiPanel = -1, _fiSide = -1, _fiTitle = -1, _fiRegion = -1, _fiRegionL = -1, _fiRegionR = -1, _fiHunter = -1, _fiSearch = -1,
            _fiInvite = -1, _fiLeaveParty = -1;
        private readonly int[] _fiChip = Unset(FindChips);
        private int _voPanel = -1, _voTitle = -1, _voTitleString = -1;
        private readonly int[] _voCard = Unset(VoteCards);

        // ---- state (menu thread) ----
        private HashSet<GameMode>? _findModes; // MODES' checked ones (OnlineSettingsFile.FindModes, read once)
        private static bool s_findNearby = true;
        private bool _finding; // a search (this player's or the party leader's) is on, from SEARCH to the game
        private FindEvent? _findEv; // the server's latest word on it
        private long _findAt, _voteEnds;
        private PartyInfo? _party;
        private OnlineClient? _partyConn; // the connection _party belongs to
        private bool _partyPending;
        // the found game this device hosts: how many players the match has, since when, when it starts by itself
        private OnlineMatch? _findHost;
        private int _findExpect;
        private long _findHostSince, _findStartAt;

        // tools (MphRead.Tools -onlinemenu)
        internal static (int Find, int Vote) OnlineFindPages => (s_onFind, s_onVote);
        internal void OnlinePreviewFind(FindEvent? ev, PartyInfo? party)
        {
            _findEv = ev;
            _finding = ev != null;
            _party = party;
            _findAt = Environment.TickCount64;
            _voteEnds = _findAt + (ev?.Seconds ?? 0) * 1000L;
            Redraw();
        }

        private static bool IsFindPage(int page) => page >= 0 && (page == s_onFind || page == s_onVote);

        // the server has Find Game (an older one drops a connection that asks for it)
        private bool FindOn => _previewMe != null || _client?.ServerAtLeast(FindStartReq.SinceServer) == true;

        private bool PartyLeader => _party == null || _party.Leader == MeCard?.Code;

        private void InstallFind(Func<int, MenuAction[], MenuPage> addFramePage, Func<int, float, float, float, float, int, MenuItem> input,
            Func<int, float, float, float, float, int, MenuItem> touch, Func<int, float, float, MenuItem> panelAt, Action<MenuPage, int> back,
            MenuItem title)
        {
            MenuItem Px(MenuPage page, float x, float y, float w, float h, int call) => page.AddItem(i => input(i, Mx(x), My(y), Mx(x + w), My(y + h), call));
            MenuItem Tx(MenuPage page, float x, float y, float w, float h, int call) => page.AddItem(i => touch(i, Mx(x), My(y), Mx(x + w), My(y + h), call));

            // ---- FIND GAME: back (B and the arrow) runs LEAVE's call: a search stops first ----
            MenuPage find = addFramePage(-1, Array.Empty<MenuAction>());
            s_onFind = find.Index;
            _fiPanel = find.AddItem(i => panelAt(i, Mx(70), Mx(1270))).Index;
            _fiSide = find.AddItem(i => panelAt(i, Mx(1310), Mx(1850))).Index;
            back(find, -1);
            _fiTitle = find.AddItem(i => Clone(title, i, 0, 0, stringId: _strings.Add("FIND GAME"))).Index;
            for (int k = 0; k < FindChips; k++)
            {
                (float x, float y) = ChipAt(k);
                _fiChip[k] = Px(find, x, y, ChipW, ChipH, FiChipCalls[k]).Index;
            }
            _fiRegion = Px(find, FindBoxX0, FindRowY(1), FindBoxX1 - FindBoxX0, 64, FiRegionCall).Index;
            _fiRegionL = Tx(find, FindBoxX0 - 90, FindRowY(1) + 4, 86, 56, FiRegionLeftCall).Index;
            _fiRegionR = Tx(find, FindBoxX1 + 4, FindRowY(1) + 4, 86, 56, FiRegionRightCall).Index;
            _fiHunter = Px(find, FindBoxX0, FindRowY(2), FindBoxX1 - FindBoxX0, 64, FiHunterCall).Index;
            _fiSearch = Px(find, 330, 770, 680, 110, FiSearchCall).Index;
            _fiInvite = Px(find, 1350, 720, 460, 66, FiInviteCall).Index;
            _fiLeaveParty = Px(find, 1350, 810, 460, 66, FiLeavePartyCall).Index;

            // ---- VOTE FOR AN ARENA: back = leave the match (after a yes) ----
            MenuPage vote = addFramePage(-1, Array.Empty<MenuAction>());
            s_onVote = vote.Index;
            _voPanel = vote.AddItem(i => panelAt(i, Mx(70), Mx(1850))).Index;
            back(vote, -1);
            _voTitleString = _strings.Add("VOTE FOR AN ARENA");
            _voTitle = vote.AddItem(i => Clone(title, i, 0, 0, stringId: _voTitleString)).Index;
            for (int k = 0; k < VoteCards; k++)
            {
                int card = k;
                float x0 = VoteCardX(k, VoteCards);
                _voCard[k] = vote.AddItem(i => input(i, Mx(x0), My(256), Mx(x0 + 400), My(600), FiVoteCall + card)).Index;
            }
        }

        // FIND GAME's rows (MODES, REGION, HUNTER): the value boxes, the labels left of them
        private const float FindBoxX0 = 560, FindBoxX1 = 1000;
        private static float FindRowY(int row) => row == 0 ? 214 : 446 + 92 * (row - 1);
        // MODES' chips: three a row from x 330, three rows (mockup px)
        private const float ChipW = 290, ChipH = 58;
        private static (float X, float Y) ChipAt(int k) => (330 + 305 * (k % 3), FindRowY(0) + 72 * (k / 3));

        // the vote's arena cards, centred (n of them); the install places four, ShowVote moves none: n shown = the first n
        private static float VoteCardX(int k, int n) => 960 - (n * 400 + (n - 1) * 30) / 2f + k * 430;

        private void ApplyFindSounds(Action<int, int, int, int> like)
        {
            foreach (int b in _fiChip.Append(_fiRegion)) like(s_onFind, b, OptionBoxPage, _soundToggle);
            like(s_onFind, _fiHunter, MainMenuPage, _soundCampaign);
            like(s_onFind, _fiSearch, FileSelect.Page, _soundFileStart);
            like(s_onFind, _fiInvite, MainMenuPage, _soundCampaign);
            like(s_onFind, _fiLeaveParty, OptionBoxPage, _soundToggle);
            foreach (int c in _voCard) like(s_onVote, c, OptionBoxPage, _soundToggle);
        }

        // ---- runtime ----

        // EnterOnline: FIND GAME or the vote coming in
        private void EnterFind(MenuEngine menu, int page)
        {
            if (page == s_onFind)
            {
                ShowFind(menu, focus: _lastPage != MultiplayerMenu.HunterPage);
                if (_lastPage == MultiplayerMenu.HunterPage) menu.SetState(_fiHunter, MenuState.Focused);
                RefreshOnline(lists: false);
            }
            else ShowVote(menu, focus: true);
        }

        private void ShowFind(MenuEngine menu, bool focus)
        {
            bool party = _party != null;
            Show(menu, _fiLeaveParty, party);
            if (focus) menu.SetState(_fiSearch, MenuState.Focused);
        }

        private void ShowVote(MenuEngine menu, bool focus)
        {
            FindEvent? f = _findEv;
            int n = f?.State == FindState.Vote ? Math.Min(VoteCards, f.Arenas.Count) : 0;
            int mine = f == null ? -1 : f.Arenas.IndexOf(f.Arena);
            for (int k = 0; k < VoteCards; k++)
            {
                if (k >= n) menu.SetState(_voCard[k], MenuState.Hidden);
                else if (focus) menu.SetState(_voCard[k], k == Math.Max(0, mine) ? MenuState.Focused : MenuState.Idle);
                else if (menu.ItemState(_voCard[k]) == MenuState.Hidden) menu.SetState(_voCard[k], MenuState.Idle);
            }
            _strings.Set(_voTitleString, f?.State == FindState.Vote || f == null ? "VOTE FOR AN ARENA" : "MATCH FOUND");
        }

        // TickOnline (every menu frame): the found game this device hosts starts by itself; the party belongs to a connection
        private void TickFind()
        {
            if (_client != null && _client != _partyConn)
            {
                _partyConn = _client;
                _party = null; // (a new connection: the server ended the old one's party)
            }
            if (_findHost is not OnlineMatch m) return;
            if (_hosting != m || m.Disposed)
            {
                _findHost = null;
                return;
            }
            long now = Environment.TickCount64;
            int joined = m.Net.Joined;
            bool all = joined >= _findExpect - 1 && m.Net.StillChoosing().Count == 0 && m.Net.NotBack().Count == 0;
            if (!all) _findStartAt = 0;
            else if (_findStartAt == 0) _findStartAt = now + FindStartDelayMs;
            bool late = now - _findHostSince >= FindStartLateMs;
            if (_findStartAt > 0 && now >= _findStartAt || late && joined > 0)
            {
                _findHost = null;
                Console.WriteLine($"online: find: starting with {joined + 1}/{_findExpect}");
                TryStart(m);
            }
            else if (late)
            {
                _findHost = null;
                LeaveHosting();
                _onlineMenu?.GoTo(s_onFind);
                Import.Popup?.Inform("nobody made it into the game.\n\nsearch again?");
            }
        }

        // TickOnline's event loop: the pushes Find Game and parties take (handled next frame: some change the connection)
        private bool FindPush(OnlineMsg ev)
        {
            switch (ev)
            {
            case FindEvent f:
                Post(() => OnFind(f));
                return true;
            case PartyEvent p:
                Post(() =>
                {
                    _party = p.Party;
                    Redraw();
                });
                return true;
            case PartyInviteEvent pi:
                Post(() => PartyInvited(pi));
                return true;
            case PartyGameEvent pg:
                Post(() => PartyGame(pg));
                return true;
            }
            return false;
        }

        private void OnFind(FindEvent f)
        {
            long now = Environment.TickCount64;
            Console.WriteLine($"online: find: {f.State} {f.Mode} {f.Arena} {f.Reason}".TrimEnd());
            switch (f.State)
            {
            case FindState.Searching:
                _finding = true;
                _findEv = f;
                _findAt = now;
                if (_onlineMenu?.Page?.Index == s_onVote) _onlineMenu.GoTo(s_onFind); // (someone left the match: searching again)
                Redraw();
                break;
            case FindState.Vote:
                _finding = true;
                _findEv = f;
                _findAt = now;
                _voteEnds = now + f.Seconds * 1000L;
                ToFindPage(s_onVote);
                break;
            case FindState.Host:
                _findEv = f;
                ToFindPage(s_onVote);
                HostFound(f);
                break;
            case FindState.Join:
                _findEv = f;
                ToFindPage(s_onVote);
                break;
            case FindState.Game:
                _finding = false;
                _findEv = null;
                _joinFrom = s_onFind; // (a join that doesn't get in comes back here)
                Join(f.GameId, f.HostCard is PlayerCard h ? OnlineNames.Display(h.Name, h.Tag) : null);
                break;
            case FindState.Cancelled:
                _finding = false;
                _findEv = null;
                if (_onlineMenu?.Page?.Index == s_onVote) _onlineMenu.GoTo(s_onFind);
                else Redraw();
                if (f.Reason != "you") Import.Popup?.Inform(f.Reason switch
                {
                    "party" => "your party changed.\n\nthe search stopped.",
                    "host" => "the host didn't open the game.\n\nsearch again?",
                    "left" => "a player left.\n\nthe search stopped.",
                    _ => "the search stopped.",
                });
                break;
            }
        }

        // the vote (or who hosts) shows wherever this player is in the online pages
        private void ToFindPage(int page)
        {
            MenuEngine? menu = _onlineMenu;
            if (menu == null) return;
            if (menu.Page?.Index != page)
            {
                MultiplayerMenu.EndOnlineSetup();
                menu.GoTo(page);
            }
            else Redraw();
        }

        // SEARCH: this player (and their party) into the queue
        private void StartSearch()
        {
            if (!Reachable()) return;
            if (!FindOn)
            {
                Import.Popup?.Inform("the server doesn't have find game yet.\n\ntry browse or create.");
                return;
            }
            if (!PartyLeader)
            {
                Import.Popup?.Inform("your party leader starts the search.");
                return;
            }
            OnlineClient client = _client!;
            HashSet<GameMode> modes = FindModes;
            Multiplayer?.RefreshFindArenas(); // (arenas imported since the setup was last open)
            Dictionary<string, List<string>> arenas = MultiplayerMenu.FindArenas();
            if (OnlineSettings.NoExtraArenas)
            {
                foreach (List<string> list in arenas.Values) list.RemoveAll(ExtraArenas.IsExtra);
            }
            var req = new FindStartReq
            {
                Modes = modes.Count == OnModes.Length ? new List<string>() : OnModes.Where(modes.Contains).Select(m => m.ToString()).ToList(),
                Nearby = s_findNearby, Version = VersionKey, Arenas = arenas, PingMs = Math.Max(0, _pingMs),
                NoExtraPickups = OnlineSettings.NoExtraPickups, NoExtraCharacters = OnlineSettings.NoExtraCharacters,
            };
            _finding = true;
            _findEv = null;
            _findAt = Environment.TickCount64;
            Redraw();
            Console.WriteLine($"online: find: search {(req.Modes.Count == 0 ? "any mode" : string.Join("+", req.Modes))}, {(req.Nearby ? "nearby" : "worldwide")}, ping {req.PingMs} ms");
            Task.Run(async () =>
            {
                try
                {
                    await client.RequestAsync<OkMsg>(req);
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() =>
                    {
                        if (ex is OnlineException { Code: "searching" }) return; // (it's on already)
                        _finding = false;
                        Redraw();
                        Import.Popup?.Inform(ex switch
                        {
                            OnlineException { Code: "not_leader" } => "your party leader starts the search.",
                            OnlineException { Code: "party_busy" } => "someone in your party is in a game.",
                            OnlineException { Code: "in_game" } => "you're in a game already.",
                            _ => "couldn't search.\n\n" + Problem(ex),
                        });
                    });
                }
            });
        }

        // MODES: the checked ones (all seven when none are kept)
        private HashSet<GameMode> FindModes
        {
            get
            {
                if (_findModes != null) return _findModes;
                List<string>? kept = OnlineSettings.FindModes;
                HashSet<GameMode> set = kept == null ? OnModes.ToHashSet() : OnModes.Where(m => kept.Contains(m.ToString())).ToHashSet();
                return _findModes = set.Count == 0 ? OnModes.ToHashSet() : set;
            }
        }

        // a MODES chip (k < 7: that mode; 7: ALL MODES); at least one stays checked; kept at once
        private void ToggleFindMode(int k)
        {
            HashSet<GameMode> modes = FindModes;
            if (k >= OnModes.Length)
            {
                if (modes.Count == OnModes.Length) return;
                modes.UnionWith(OnModes);
            }
            else if (!modes.Remove(OnModes[k]))
            {
                modes.Add(OnModes[k]);
            }
            else if (modes.Count == 0)
            {
                modes.Add(OnModes[k]);
                Import.Popup?.Inform("search at least one mode.");
                return;
            }
            OnlineSettings.FindModes = modes.Count == OnModes.Length ? null : OnModes.Where(modes.Contains).Select(m => m.ToString()).ToList();
            OnlineSettings.Save(ProfileDir);
        }

        // the line under the rows: which modes a search asks for
        private string FindModesLine()
        {
            HashSet<GameMode> modes = FindModes;
            List<string> on = OnModes.Where(modes.Contains).Select(ModeName).ToList();
            List<string> off = OnModes.Where(m => !modes.Contains(m)).Select(ModeName).ToList();
            static string List(List<string> names, string and) => names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + $" {and} " + names[^1];
            return off.Count == 0 ? "any of the seven modes." : on.Count == 1 ? $"only {on[0]}." : off.Count <= 2 ? $"any mode but {List(off, "and")}." : List(on, "or") + ".";
        }

        // CANCEL, back, leaving the match: out of the queue (the server tells the party)
        private void StopSearch()
        {
            _finding = false;
            _findEv = null;
            Redraw();
            if (_client is not OnlineClient client) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await client.RequestAsync<OkMsg>(new FindCancelReq());
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Console.Error.WriteLine("online: find cancel: " + ex.Message);
                }
            });
        }

        // the vote is over and this device hosts: the match's game on the server (its players invited) and its lobby
        private void HostFound(FindEvent f)
        {
            if (_creating || _hosting != null || _joining) return;
            if (_client is not { Connected: true } client)
            {
                Import.Popup?.Inform("lost the server.\n\nsearch again?");
                return;
            }
            MatchSettings template;
            try
            {
                template = MultiplayerMenu.BuildFindMatch(f.Mode, f.Arena);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException)
            {
                Import.Popup?.Inform("can't make this game.\n\n" + ex.Message.ToLowerInvariant());
                StopSearch();
                return;
            }
            OnlinePlayer me = Me();
            template.Players.RemoveAll(p => p.Bot || p.Remote);
            if (template.Players.Count == 0) return;
            template.Players[0].Nickname = OnlineNames.Clean(me.Name);
            template.Players[0].RankLevel = me.Rank;
            // what the found players' ONLINE SETTINGS filters ban (the host's own rules are on the template already)
            if (f.NoExtraPickups) template.PirateBlaster = false;
            if (f.NoExtraCharacters) template.ExtraCharacters = false;
            MphRecomp.Multiplayer.Characters.ApplyExtraRules(template);
            int people = Math.Max(2, f.Players.Count);
            string romKey = LanSession.RomKeyFor(Paths.MphKey);
            _creating = true;
            _createCancelled = false;
            _client = null; // the match owns the connection from here
            Task.Run(async () =>
            {
                try
                {
                    OnlineMatch match = await OnlineMatch.HostAsync(client, ServerUri, template, romKey, people, isPublic: false, friends: false, rivals: false);
                    Post(() =>
                    {
                        _creating = false;
                        _finding = false;
                        _findEv = null;
                        _hosting = match;
                        _hostTemplate = template;
                        match.Net.PlayersChanged += LobbyChangedThere;
                        _findHost = match;
                        _findExpect = people;
                        _findHostSince = Environment.TickCount64;
                        _findStartAt = 0;
                        Console.WriteLine($"online: find: hosting {match.Game.Id} ({template.Mode} in {template.Arena} for {people})");
                        _onlineMenu?.GoTo(s_onLobby);
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException or System.IO.IOException)
                {
                    Console.Error.WriteLine("online: find: can't host: " + ex);
                    _ = Task.Run(async () => await client.DisposeAsync());
                    Post(() =>
                    {
                        _creating = false;
                        _finding = false;
                        _findEv = null;
                        if (_onlineMenu?.Page?.Index == s_onVote) _onlineMenu.GoTo(s_onFind);
                        Import.Popup?.Inform("couldn't open the game.\n\n" + Problem(ex));
                        RefreshOnline(lists: false);
                    });
                }
            });
        }

        // the hub's ping goes to the server too (Find Game's REGION and host pick)
        private static async Task SendPing(OnlineClient client, int ms)
        {
            if (ms <= 0 || !client.ServerAtLeast(FindStartReq.SinceServer)) return;
            try
            {
                await client.RequestAsync<OkMsg>(new PingReq { Ms = ms });
            }
            catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException)
            {
                Console.Error.WriteLine("online: ping: " + ex.Message);
            }
        }

        // ---- the party ----

        // INVITE FRIENDS: friends and rivals online, not in a game or this party, after a yes
        private void InviteToParty()
        {
            if (_partyPending || !Reachable()) return;
            if (!PartyLeader)
            {
                Import.Popup?.Inform("only the party leader can invite.");
                return;
            }
            OnlineClient client = _client!;
            _partyPending = true;
            HashSet<string> inParty = _party?.Members.Select(m => m.Code).ToHashSet() ?? new HashSet<string>();
            Task.Run(async () =>
            {
                try
                {
                    RosterMsg roster = await client.RequestAsync<RosterMsg>(new RosterReq());
                    List<PlayerCard> who = roster.Friends.Concat(ShowRivals ? roster.Rivals : new List<PlayerCard>()).GroupBy(c => c.Code).Select(g => g.First())
                        .Where(c => c.Presence == Presence.Online && c.GameId == null && !inParty.Contains(c.Code)).ToList();
                    Post(() =>
                    {
                        _partyPending = false;
                        if (who.Count == 0)
                        {
                            Import.Popup?.Inform("no friends online right now.\n\nadd friends from all friends.");
                            return;
                        }
                        string names = string.Join("\n", who.Take(4).Select(c => OnlineNames.Display(c.Name, c.Tag).ToLowerInvariant()))
                            + (who.Count > 4 ? $"\n+{who.Count - 4} more" : "");
                        Import.Popup?.Ask($"invite them to your party?\n\n{names}", yes =>
                        {
                            if (!yes) return;
                            foreach (PlayerCard c in who) _ = PartyRequest(client, new PartyInviteReq { Code = c.Code });
                        });
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() =>
                    {
                        _partyPending = false;
                        Import.Popup?.Inform("couldn't invite.\n\n" + Problem(ex));
                    });
                }
            });
        }

        private void LeaveParty()
        {
            if (_party == null || _client is not OnlineClient client) return;
            if (_finding) StopSearch();
            _party = null;
            Redraw();
            _ = PartyRequest(client, new PartyLeaveReq());
        }

        // a party invite: join after a yes (not while a search, a join or a game is on)
        private void PartyInvited(PartyInviteEvent invite)
        {
            if (_joining || _creating || _finding || _hosting != null || _joined != null || Import.Popup == null) return;
            OnlineClient? client = _client;
            if (client == null) return;
            string from = OnlineNames.Display(invite.From.Name, invite.From.Tag).ToLowerInvariant();
            Import.Popup.Ask($"{from} invites you to their party.\n\nsearch together?", yes =>
            {
                if (!yes || _client != client) return;
                Task.Run(async () =>
                {
                    try
                    {
                        PartyMsg joined = await client.RequestAsync<PartyMsg>(new PartyJoinReq { PartyId = invite.PartyId });
                        Post(() =>
                        {
                            _party = joined.Party;
                            Redraw();
                        });
                    }
                    catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                        or ObjectDisposedException or InvalidOperationException)
                    {
                        Post(() => Import.Popup?.Inform("couldn't join the party.\n\n" + Problem(ex)));
                    }
                });
            });
        }

        // the party's leader hosted or joined a game (not a found one: Find Game brings the party itself): follow them after a yes
        private void PartyGame(PartyGameEvent ev)
        {
            if (_finding || _joining || _creating || _hosting != null || _joined != null || _onlineMenu == null || Import.Popup == null) return;
            MenuEngine menu = _onlineMenu;
            Import.Popup.Ask("your party leader is in a game.\n\njoin them?", yes =>
            {
                if (yes) PickThenJoin(menu, ev.GameId, null);
            });
        }

        private static async Task PartyRequest(OnlineClient client, OnlineMsg request)
        {
            try
            {
                await client.RequestAsync<OnlineMsg>(request);
            }
            catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException)
            {
                Console.Error.WriteLine("online: party: " + ex.Message);
            }
        }

        // ---- calls ----

        private void FindCall(MenuEngine menu, int call)
        {
            int chip = Array.IndexOf(FiChipCalls, call);
            if (chip >= 0)
            {
                if (!_finding) ToggleFindMode(chip);
                Redraw();
                return;
            }
            switch (call)
            {
            case FiRegionCall:
            case FiRegionLeftCall:
            case FiRegionRightCall:
                if (!_finding) s_findNearby = !s_findNearby;
                break;
            case FiHunterCall:
                if (!_finding && Multiplayer != null) Multiplayer.BeginOnlineSetup(menu, s_onFind, s_onFind, fresh: false);
                break;
            case FiSearchCall:
                if (_finding) StopSearch();
                else StartSearch();
                break;
            case FiInviteCall:
                InviteToParty();
                break;
            case FiLeavePartyCall:
                if (_party != null && Import.Popup != null) Import.Popup.Ask("leave your party?", yes =>
                {
                    if (yes) LeaveParty();
                });
                break;
            case >= FiVoteCall and < FiVoteCall + VoteCards:
            {
                int k = call - FiVoteCall;
                if (_findEv is not { State: FindState.Vote } f || k >= f.Arenas.Count || _client is not OnlineClient client) break;
                string arena = f.Arenas[k];
                f.Arena = arena; // (shown at once; the server's next word agrees)
                Redraw();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await client.RequestAsync<OkMsg>(new FindVoteReq { Arena = arena });
                    }
                    catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                        or ObjectDisposedException or InvalidOperationException)
                    {
                        Console.Error.WriteLine("online: vote: " + ex.Message);
                    }
                });
                break;
            }
            }
            Redraw();
        }

        // B / the back arrow on FIND GAME (a search stops) and the vote (leave the match, after a yes)
        private bool FindBack(MenuEngine menu)
        {
            int page = menu.Page?.Index ?? -1;
            if (page == s_onFind)
            {
                if (_finding) StopSearch();
                menu.GoTo(s_onHub);
                return true;
            }
            if (page != s_onVote) return false;
            if (Import.Popup == null || _findEv == null)
            {
                StopSearch();
                menu.GoTo(s_onFind);
                return true;
            }
            Import.Popup.Ask("leave this match?\n\nthe others search again.", yes =>
            {
                if (!yes || menu.Page?.Index != s_onVote) return;
                StopSearch();
                menu.GoTo(s_onFind);
            });
            return true;
        }

        // ---- drawing ----

        private void DrawFind(Dictionary<int, List<WidgetTri>> glows)
        {
            int tag = _fiTitle;
            if (MeCard is PlayerCard me && me.Code.Length > 0)
                TextS(OnlineNames.Display(me.Name, me.Tag).ToLowerInvariant(), Mx(1850), My(52), 1, Cyan, tag, SizeM);
            float alpha = _finding ? 0.4f : 1;
            TextS("modes", Mx(130), My(FindRowY(0)) - (Mw(ChipH) - 8 * SizeM) / 2, 0, Pale, _fiChip[0], SizeM, alpha);
            for (int k = 0; k < FindChips; k++) FindChip(k, glows, alpha);
            FindValue(1, "region", s_findNearby ? "nearby" : "worldwide", _fiRegion, glows, arrows: true, alpha);
            (Hunter hunter, _, string? character) = MultiplayerMenu.MyLook;
            FindValue(2, "hunter", (character ?? hunter.ToString()).ToLowerInvariant(), _fiHunter, glows, arrows: false, alpha);
            // what the search does, or how it's going
            int st = _fiSearch;
            if (!_finding)
            {
                TextS(s_findNearby ? "players close to you first, wider as you wait." : "anyone, anywhere, at once.", Mx(130), My(630), 0, Pale, st, SizeM);
                TextS(Fit(FindModesLine(), 54), Mx(130), My(674), 0, Pale, st, SizeM);
                string status = _connecting ? "connecting..." : !Live && _netError != null ? "offline: " + _netError
                    : !FindOn && Live ? "the server needs an update for find game." : "";
                TextS(Fit(status, 44), Mx(130), My(720), 0, _connecting ? Cyan : Red, st, SizeX);
            }
            else
            {
                FindEvent? f = _findEv;
                long since = Environment.TickCount64 - _findAt;
                int waited = (f?.Waited ?? 0) + (int)(since / 1000);
                TextS("searching" + new string('.', 1 + (int)(Environment.TickCount64 / 400 % 3)), Mx(130), My(620), 0, Orange, st, SizeS);
                string line = $"{waited / 60}:{waited % 60:00} waited" + (f != null && f.Queued > 0 ? $" - {f.Queued} in the queue" : "");
                TextS(line, Mx(130), My(676), 0, White, st, SizeM);
                string region = f == null ? "" : !f.Nearby || f.LimitMs == 0 ? "region: anyone" : $"region: routes up to {f.LimitMs} ms";
                if (f?.Reason == "match") region = "a player left the match: searching again.";
                if (!PartyLeader) region = "your party leader is searching.";
                TextS(region, Mx(130), My(718), 0, Cyan, st, SizeM);
            }
            Capsule(Mx(330), My(770), Mw(680), Mw(110), _finding ? "cancel" : "search", Live && (FindOn || _finding) && (PartyLeader || _finding),
                _fiSearch, glows, SizeS);
            // PARTY
            int side = _fiInvite;
            Fill(Mx(1314), My(200), Mx(1846), My(238), HeaderBar.R, HeaderBar.G, HeaderBar.B, 1, side);
            TextS(_party == null ? "party" : $"party {_party.Members.Count}/{PartyRows}", Mx(1580), My(208), 2, Dark, side, SizeM);
            if (_party == null)
            {
                TextS(!Live ? "" : "no party.", Mx(1580), My(400), 2, White, side, SizeM);
                TextS(!Live ? "" : ShowRivals ? "invite friends or rivals" : "invite friends", Mx(1580), My(450), 2, Pale, side, SizeX);
                TextS(!Live ? "" : "to search together.", Mx(1580), My(484), 2, Pale, side, SizeX);
            }
            else
            {
                for (int r = 0; r < PartyRows && r < _party.Members.Count; r++)
                {
                    PlayerCard p = _party.Members[r];
                    float y = 262 + 112 * r;
                    Seat(p, Mx(1330), My(y + 6), Mw(90), side);
                    TextS(Fit(OnlineNames.Display(p.Name, p.Tag).ToLowerInvariant(), 16), Mx(1440), My(y + 14), 0, White, side, SizeM);
                    TextS(p.Code == _party.Leader ? "leader" : p.Code == MeCard?.Code ? "you" : "member", Mx(1440), My(y + 58), 0, Orange, side, SizeX);
                }
            }
            Bar(Mx(1350), My(720), Mx(1810), My(786), _fiInvite, glows);
            TextS("invite friends", Mx(1580), My(738), 2, PartyLeader ? Orange : OnGrey, _fiInvite, SizeM);
            if (_party != null)
            {
                Bar(Mx(1350), My(810), Mx(1810), My(876), _fiLeaveParty, glows);
                TextS("leave party", Mx(1580), My(828), 2, Orange, _fiLeaveParty, SizeM);
            }
        }

        // a MODES chip: a check box and the mode's name (ALL MODES checked when all seven are)
        private void FindChip(int k, Dictionary<int, List<WidgetTri>> glows, float alpha)
        {
            int item = _fiChip[k];
            (float px, float py) = ChipAt(k);
            HashSet<GameMode> modes = FindModes;
            bool on = k < OnModes.Length ? modes.Contains(OnModes[k]) : modes.Count == OnModes.Length;
            float x0 = Mx(px), top = My(py), x1 = Mx(px + ChipW), bottom = My(py + ChipH);
            Bar(x0, top, x1, bottom, item, glows);
            float bx = Mx(px + 16), by = My(py + 14), bs = Mw(30);
            Box(bx, by, bx + bs, by - bs, item);
            if (on) Fill(bx + Mw(6), by - Mw(6), bx + bs - Mw(6), by - bs + Mw(6), Orange.R, Orange.G, Orange.B, alpha, item);
            string name = k < OnModes.Length ? ModeName(OnModes[k]) : "all modes";
            float size = name.Length > 10 ? SizeX : SizeM; // (PRIME HUNTER: the smaller size fits the chip)
            TextS(name, Mx(px + 60), top - (top - bottom - 8 * size) / 2, 0, on ? White : OnGrey, item, size, alpha);
        }

        // a label, a value box and (REGION) the option arrows either side
        private void FindValue(int row, string label, string value, int item, Dictionary<int, List<WidgetTri>> glows, bool arrows, float alpha)
        {
            float top = My(FindRowY(row)), bottom = My(FindRowY(row) + 64);
            TextS(label, Mx(130), top - (top - bottom - 8 * SizeM) / 2, 0, Pale, item, SizeM, alpha);
            ValueBox(Mx(FindBoxX0), top, Mx(FindBoxX1), bottom, value, item, glows);
            if (!arrows) return;
            float ay = top - (top - bottom - ArrowH) / 2;
            Arrow(false, Mx(FindBoxX0) - ArrowW - 2.5f, ay, item);
            Arrow(true, Mx(FindBoxX1) + 2.5f, ay, item);
        }

        private void DrawVote(Dictionary<int, List<WidgetTri>> glows)
        {
            FindEvent? f = _findEv;
            int tag = _voTitle;
            if (f == null)
            {
                Text("the match is gone.", 128, -80, 2, White, tag);
                return;
            }
            string mode = Enum.TryParse(f.Mode, out GameMode gm) ? ModeName(gm) : f.Mode.ToLowerInvariant();
            TextS($"{mode} - {f.Players.Count} players", Mx(960), My(212), 2, Orange, tag, SizeM);
            bool voting = f.State == FindState.Vote;
            List<string> arenas = voting ? f.Arenas : f.Arena.Length > 0 ? new List<string> { f.Arena } : new List<string>();
            int n = Math.Min(VoteCards, arenas.Count);
            for (int k = 0; k < n; k++)
            {
                float x0 = VoteCardX(k, voting ? n : 1);
                int item = voting ? _voCard[k] : tag;
                bool mine = voting && arenas[k] == f.Arena;
                Fill(Mx(x0), My(256), Mx(x0 + 400), My(600), 0, 0, 0, 0.35f, item);
                Box(Mx(x0), My(256), Mx(x0 + 400), My(600), item);
                if (voting) Glow(glows, item, Mx(x0), My(256), Mx(x0 + 400), My(600), item);
                ArenaPicture(arenas[k], Mx(x0 + 14), My(270), Mw(372), Mw(234), 1.2f, item);
                TextS(Fit(ArenaLabel(arenas[k]), 20), Mx(x0 + 200), My(520), 2, White, item, SizeM);
                if (mine) TextS("your vote", Mx(x0 + 200), My(562), 2, Cyan, item, SizeX);
                else if (!voting) TextS("picked", Mx(x0 + 200), My(562), 2, Green, item, SizeX);
            }
            // the players, the host marked once it's known
            // (P8, release 0.2.0 F3: up to 8; past 4, two rows of four with smaller pictures, clear of the lines below)
            int np = Math.Min(8, f.Players.Count), perRow = Math.Min(np, 4);
            bool twoRows = np > 4;
            for (int i = 0; i < np; i++)
            {
                PlayerCard p = f.Players[i];
                int row = i / 4, col = i % 4, inRow = row == 0 ? perRow : np - 4;
                float x = 960 - inRow * 420 / 2f + col * 420 + 20, y = twoRows ? 612 + row * 84 : 640, size = twoRows ? 72 : 90;
                Seat(p, Mx(x), My(y), Mw(size), tag);
                TextS(Fit(OnlineNames.Display(p.Name, p.Tag).ToLowerInvariant(), 14), Mx(x + size + 14), My(y + (twoRows ? 6 : 12)), 0, White, tag, SizeM);
                bool host = f.HostCard?.Code == p.Code;
                string what = host ? "host" : p.Code == MeCard?.Code ? "you" : p.Friend ? "friend" : ShowRivals && p.Rival ? "rival" : "";
                TextS(what, Mx(x + size + 14), My(y + (twoRows ? 44 : 56)), 0, host ? Orange : Pale, tag, SizeX);
            }
            string first, second;
            if (voting)
            {
                long left = Math.Max(0, (_voteEnds - Environment.TickCount64 + 999) / 1000);
                first = $"voting ends in {left} s";
                second = $"{f.Votes}/{f.Players.Count} voted - tap an arena";
            }
            else
            {
                string host = f.HostCard is PlayerCard h ? OnlineNames.Display(h.Name, h.Tag).ToLowerInvariant() : "a player";
                first = f.State == FindState.Host ? "you host this match." : $"{host} hosts this match.";
                second = f.State == FindState.Host ? "opening the game..." : "waiting for the game to open...";
            }
            TextS(first, Mx(960), My(790), 2, Orange, tag, SizeS);
            TextS(second, Mx(960), My(850), 2, Pale, tag, SizeM);
        }
    }
}
