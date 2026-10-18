using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MphRead;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;

// ONLINE PLAY's HUNTER LICENSE view (owner 2026-10-09: pick 10 "tap any name -> their Hunter License,
// everywhere", pick 11 the vanilla card + head to head + actions, mockup V_A "card + side panel"). A name opens it from
// FRIENDS AND RIVALS (every tab), FIND A HUNTER's rows, a game's details (HOST LICENSE and its seats) and the lobby's seat
// cards, and the names on RESULTS after an online match; back returns there (from the lobby the game stays: hosting or waiting
// goes on; from RESULTS its PLAY AGAIN still can). Left: their card as page 60 draws
// it (MultiplayerMenu.EmitCard, from the license their device uploaded; L / R or a tap on it turns its tabs), name#tag and,
// for a friend or rival, where they are. Right: HEAD TO HEAD (counted on this device, by their friend code) and the actions:
// ADD FRIEND / ADD RIVAL (REMOVE when already linked, ACCEPT for their request, SENT while ours waits), BLOCK / UNBLOCK,
// REPORT (their name, kept on the server for the owner's review: server 0.2.0+, an older one says it needs an update), JOIN
// when they're in a game this player may join. Everything goes through the
// connection there is (the hub's, or the hosted / joined game's in the lobby).
// This device's own license goes up as the connection comes in (SetLicenseReq, when it changed): the card's numbers only,
// never its name (the server's word-filtered name is the one shown) or the LAN address it keeps.
// Head to head is counted as RESULTS comes in after an online match (CountHeadToHead: the match's seats matched to the lobby's
// cards as it started, then each human opponent's place against this player's).
// Not yet: ADD FRIEND / ADD RIVAL on RESULTS, a replay's license as it was (V_C, needs O10).
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private static int s_onLicense = -1;

        // our callbacks (2680-2699)
        private const int LicCardCall = 2680, LicFriendCall = 2681, LicRivalCall = 2682, LicJoinCall = 2683, LicBlockCall = 2684,
            LicReportCall = 2685, LicPrevCall = 2686, LicNextCall = 2687;
        private const int LicSeatCall = 2690; // + the details page's seat
        private const int LicResultCall = 2694; // + RESULTS' row (an online match's)
        private const int LicenseFirstCall = LicCardCall, LicenseLastCall = LicResultCall + 3;
        private const int CardTabs = 5;

        // items (-1 = none)
        private int _liPanel = -1, _liSide = -1, _liTitle = -1, _liCard = -1, _liFriend = -1, _liRival = -1, _liJoin = -1, _liBlock = -1,
            _liReport = -1;
        private readonly int[] _gSeat = Unset(4); // the details page's seats (a tap: their license)
        private readonly int[] _resultName = Unset(4); // RESULTS' names (a tap, after an online match: their license)
        // RESULTS' four name rows (MultiplayerMenu.BindResults: the name column at text y 117 / 89 / 61 / 33, x 38-42 to the first
        // value column at 111), as touch areas in menu space
        private static readonly float[] ResultRowY = { 117, 89, 61, 33 };

        // ---- state (menu thread) ----
        private PlayerCard? _licCard; // whose license (the server's latest card once it answers)
        private HunterLicense? _licLicense; // their uploaded license (null = none uploaded, or not known yet)
        private bool _licLoading;
        private string? _licError;
        private int _licReturn = -1, _licTab;
        private static string? s_licenseSent; // the license this device last uploaded (this run)
        private HeadToHeadStore? _headToHead;
        // the online match playing: its seats' friend codes (Play); process-wide like MatchResults.Last (the menus may be made
        // again while the match plays)
        private static Dictionary<int, PlayerCard>? s_matchCards;
        private static MatchResults? s_headToHeadCounted;

        // tools (MphRead.Tools -onlinemenu / -onlinefriendstest)
        internal static int OnlineLicensePage => s_onLicense;
        internal PlayerCard? OnlineLicenseCard => _licCard;
        internal HunterLicense? OnlineLicenseShown => _licLicense;
        internal bool OnlineLicenseLoading => _licLoading;
        internal void OnlineOpenLicense(MenuEngine menu, PlayerCard card) => OpenLicense(menu, card);
        internal void OnlinePreviewHeadToHead(string code, HeadToHead h) => HeadToHeads.Set(code, h);
        internal HeadToHead? OnlineHeadToHead(string code) => HeadToHeads.Get(code);
        internal static string OnlineLicenseJson(HunterLicense l) => LicenseJson(l);

        private void InstallLicense(Func<int, MenuAction[], MenuPage> addFramePage, Func<int, float, float, float, float, int, MenuItem> input,
            Func<int, float, float, MenuItem> panelAt, Action<MenuPage, int> back, MenuItem title)
        {
            MenuItem Px(MenuPage page, float x, float y, float w, float h, int call) => page.AddItem(i => input(i, Mx(x), My(y), Mx(x + w), My(y + h), call));

            // back (B and the arrow) runs LEAVE's call: OnlineCall sends it back where the license was opened from
            MenuPage page = addFramePage(-1, new[] { Call(MenuKeys.R, LicNextCall), Call(MenuKeys.L, LicPrevCall) });
            s_onLicense = page.Index;
            _liPanel = page.AddItem(i => panelAt(i, Mx(70), Mx(1150))).Index;
            _liSide = page.AddItem(i => panelAt(i, Mx(1190), Mx(1850))).Index;
            back(page, -1);
            _liTitle = page.AddItem(i => Clone(title, i, 0, 0, stringId: _strings.Add("HUNTER LICENSE"))).Index;
            (float cx, float cy, float cx1, float cy1) = LicenseCardBox;
            _liCard = Px(page, cx, cy, cx1 - cx, cy1 - cy, LicCardCall).Index;
            _liFriend = Px(page, 1230, LicenseButtonY(0), 580, 66, LicFriendCall).Index;
            _liRival = Px(page, 1230, LicenseButtonY(1), 580, 66, LicRivalCall).Index;
            _liBlock = Px(page, 1230, LicenseButtonY(2), 285, 66, LicBlockCall).Index; // (VOICE right of it: OnlineMenus.Voice.cs)
            _liReport = Px(page, 1525, LicenseButtonY(3), 285, 66, LicReportCall).Index; // (MUTE left of it: OnlineMenus.Chat.cs)
            _liJoin = Px(page, 1230, 800, 580, 90, LicJoinCall).Index;
        }

        // where the card may sit (mockup px: x0, top, x1, bottom): it keeps its own shape inside, centred, at the top
        private static readonly (float X0, float Top, float X1, float Bottom) LicenseCardBox = (100, 225, 1120, 790);

        private static float LicenseButtonY(int row) => 470 + 82 * row;

        // RESULTS (page 47, MultiplayerMenu's): a tap-only area over each name (no look of its own), for online matches
        private void InstallResultNames(Func<int, float, float, float, float, int, MenuItem> touch)
        {
            if (_file.Pages.Count <= MultiplayerMenu.ResultsPage) return;
            MenuPage results = _file.Pages[MultiplayerMenu.ResultsPage];
            for (int r = 0; r < _resultName.Length; r++)
            {
                float y = ResultRowY[r] - 192;
                int row = r;
                _resultName[r] = results.AddItem(i => touch(i, 30, y + 12, 106, y - 4, LicResultCall + row)).Index;
            }
        }

        // RESULTS coming in: its names tappable when it's an online match's and that seat's hunter is known
        private void ShowResultNames(MenuEngine menu)
        {
            MatchResults? r = MatchResults.Last;
            for (int k = 0; k < _resultName.Length; k++)
            {
                bool on = MultiplayerMenu.OnlineResults && r != null && k < r.Rows.Count && s_matchCards?.ContainsKey(r.Rows[k].Seat) == true;
                Show(menu, _resultName[k], on);
            }
        }

        // the details page's seats (as DrawGame places them: four across)
        private static (float X0, float Top, float X1, float Bottom) DetailSeatRect(int seat)
            => (Mx(116 + 430 * seat), My(744), Mx(540 + 430 * seat), My(870));

        private void ApplyLicenseSounds(Action<int, int, int, int> like)
        {
            like(s_onLicense, _liCard, OptionBoxPage, _soundToggle);
            foreach (int b in new[] { _liFriend, _liRival, _liBlock, _liReport }) like(s_onLicense, b, OptionBoxPage, _soundToggle);
            like(s_onLicense, _liJoin, FileSelect.Page, _soundFileStart);
            foreach (int s in _gSeat) like(s_onDetail, s, MainMenuPage, _soundCampaign);
        }

        // the connection there is: the hub's, else the game this device hosts or joined (the license from the lobby), or the one
        // RESULTS kept (the license from RESULTS)
        private OnlineClient? Conn => _client ?? (_hosting ?? _joined ?? Kept)?.Client;

        // ---- opening, fetching ----

        private void OpenLicense(MenuEngine menu, PlayerCard card)
        {
            if (s_onLicense < 0) return;
            int from = menu.Page?.Index ?? s_onHub;
            if (from != s_onLicense) _licReturn = from;
            if (_licCard?.Code != card.Code)
            {
                _licTab = 0;
                _licLicense = ParseLicense(card.License);
            }
            _licCard = card;
            _licError = null;
            menu.GoTo(s_onLicense);
            FetchLicense();
        }

        // their latest card (license, presence, links) and the roster (requests both ways, blocks) from the server
        private void FetchLicense()
        {
            PlayerCard? card = _licCard;
            if (card == null || _previewMe != null) return;
            OnlineClient? client = Conn;
            if (client == null || !client.Connected)
            {
                if (_hosting == null && _joined == null && !_connecting) RefreshOnline(lists: true); // (FetchLicense again once connected)
                return;
            }
            string code = card.Code;
            _licLoading = true;
            Task.Run(async () =>
            {
                try
                {
                    PlayerMsg p = await client.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = code });
                    RosterMsg roster = await client.RequestAsync<RosterMsg>(new RosterReq());
                    Post(() =>
                    {
                        _roster = roster;
                        if (_licCard?.Code != code) return;
                        _licLoading = false;
                        _licError = null;
                        _licCard = p.Player;
                        _licLicense = ParseLicense(p.Player.License);
                        KeepServerRecord(client, p.Player); // (0.7.0: the server's head to head, the same on both sides)
                        Redraw();
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() =>
                    {
                        if (_licCard?.Code != code) return;
                        _licLoading = false;
                        _licError = ex is OnlineException { Code: "not_found" } ? "this hunter can't be found." : Problem(ex);
                        Redraw();
                    });
                }
            });
        }

        // ---- the page ----

        private bool Blocked(PlayerCard c) => _roster?.Blocked.Any(x => x.Code == c.Code) ?? false;

        private bool IncomingFrom(PlayerCard c, string kind) => _roster?.Requests.Any(q => q.Incoming && q.Kind == kind && q.Player.Code == c.Code) ?? false;

        private bool LicenseJoinable(PlayerCard c) => c.Presence == Presence.Lobby && c.GameId != null && _hosting == null && _joined == null
            && Kept == null;

        private void ShowLicense(MenuEngine menu, bool focus)
        {
            PlayerCard? c = _licCard;
            bool actions = c != null && c.Code != MeCard?.Code;
            bool blocked = c != null && Blocked(c);
            bool lost = false; // (a focused button that goes away hands the focus to the card)
            void Set(int item, bool on)
            {
                if (!on && item >= 0 && menu.ItemState(item) == MenuState.Focused) lost = true;
                Show(menu, item, on);
            }
            Set(_liFriend, actions && !blocked);
            Set(_liRival, actions && !blocked && ShowRivals);
            Set(_liBlock, actions);
            Set(_liReport, actions);
            Set(_liMute, actions); // (OnlineMenus.Chat.cs)
            Set(_liVoice, actions && !blocked); // (OnlineMenus.Voice.cs)
            Set(_liJoin, actions && !blocked && LicenseJoinable(c!));
            if (focus || lost) menu.SetState(_liCard, MenuState.Focused);
        }

        private void LicenseCall(MenuEngine menu, int call)
        {
            if (call >= LicResultCall)
            {
                int row = call - LicResultCall;
                if (MultiplayerMenu.OnlineResults && MatchResults.Last is MatchResults res && row < res.Rows.Count
                    && s_matchCards != null && s_matchCards.TryGetValue(res.Rows[row].Seat, out PlayerCard? who))
                {
                    OpenLicense(menu, who);
                }
                return;
            }
            if (call >= LicSeatCall)
            {
                int k = call - LicSeatCall;
                if (OpenGame is GameCard g && k < g.Members.Count) OpenLicense(menu, g.Members[k]);
                return;
            }
            PlayerCard? c = _licCard;
            if (c == null) return;
            string name = OnlineNames.Display(c.Name, c.Tag).ToLowerInvariant();
            switch (call)
            {
            case LicCardCall:
            case LicNextCall:
                _licTab = (_licTab + 1) % CardTabs;
                break;
            case LicPrevCall:
                _licTab = (_licTab + CardTabs - 1) % CardTabs;
                break;
            case LicFriendCall:
                LinkAction(c, LinkKind.Friend);
                break;
            case LicRivalCall:
                LinkAction(c, LinkKind.Rival);
                break;
            case LicJoinCall:
                if (c.GameId is string id && LicenseJoinable(c)) PickThenJoin(menu, id, OnlineNames.Display(c.Name, c.Tag));
                break;
            case LicBlockCall:
                if (Blocked(c))
                {
                    Act(new BlockReq { Code = c.Code, Block = false }, _ => $"{name} is unblocked.");
                    break;
                }
                Import.Popup?.Ask($"block {name}?\n\nthey can't find you or send you requests, and they leave your friends and rivals.", yes =>
                {
                    if (yes) Act(new BlockReq { Code = c.Code, Block = true }, _ => $"{name} is blocked.");
                });
                break;
            case LicReportCall:
                if (Conn?.ServerAtLeast(ChatReq.SinceServer) == true) OpenReport(menu, c); // (what for: OnlineMenus.Chat.cs)
                else Report(c);
                break;
            }
        }

        // REPORT: their name, after a yes (no chat yet: a name is what a player can show others); a server before 0.2.0 doesn't
        // know the request (it would drop the connection), so it isn't sent there
        private void Report(PlayerCard c)
        {
            OnlineClient? client = Conn;
            string name = OnlineNames.Display(c.Name, c.Tag).ToLowerInvariant();
            if (client is not { Connected: true })
            {
                Import.Popup?.Inform("can't reach the server.\n\n" + (_netError ?? "try again in a moment."));
                return;
            }
            if (!client.ServerAtLeast(ReportReason.SinceServer))
            {
                Import.Popup?.Inform("reporting needs a server update.\n\nblock them to stop seeing them online.");
                return;
            }
            Import.Popup?.Ask($"report {name}'s name as offensive?\n\nthe report is kept for review.", yes =>
            {
                if (yes) Act(new ReportReq { Code = c.Code, Reason = ReportReason.Name }, ok => ok.Done ? "thanks.\n\nthe report was sent."
                    : "you reported this hunter today already.");
            });
        }

        // ADD FRIEND / ADD RIVAL, and what it becomes: REMOVE (linked), ACCEPT (their request), taking ours back (sent)
        private void LinkAction(PlayerCard c, string kind)
        {
            string name = OnlineNames.Display(c.Name, c.Tag).ToLowerInvariant();
            if (kind == LinkKind.Rival && Linked(c, LinkKind.Friend))
            {
                // friend OR rival (vanilla): a friend is never made a rival
                Import.Popup?.Inform($"{name} is your friend.\n\na friend can't be a rival too.");
                return;
            }
            if (kind == LinkKind.Friend && Linked(c, LinkKind.Rival) && !IncomingFrom(c, kind) && !Sent(c, kind))
            {
                // vanilla: "you have upgraded your rival to a friend" (once both agree, the rival link goes)
                Import.Popup?.Ask($"ask {name} to be your friend?\n\nonce they say yes, your rival becomes your friend.", yes =>
                {
                    if (yes) AddLink(c, kind);
                });
                return;
            }
            if (Linked(c, kind))
            {
                Import.Popup?.Ask($"remove {name} from your {kind}s?", yes =>
                {
                    if (yes) Act(new RemoveReq { Code = c.Code, Kind = kind }, _ => null);
                });
            }
            else if (IncomingFrom(c, kind))
            {
                Act(new AnswerReq { Code = c.Code, Kind = kind, Accept = true }, _ => $"you and {name} are {kind}s now.");
            }
            else if (Sent(c, kind))
            {
                Import.Popup?.Ask($"take back your {kind} request to {name}?", yes =>
                {
                    if (yes) Act(new RemoveReq { Code = c.Code, Kind = kind }, _ => null);
                });
            }
            else AddLink(c, kind);
        }

        private (string Label, (float R, float G, float B) Colour) LinkLabel(PlayerCard c, string kind)
            => kind == LinkKind.Rival && Linked(c, LinkKind.Friend) ? ("friend (not a rival)", OnGrey)
                : Linked(c, kind) ? ("remove " + kind, Orange) : IncomingFrom(c, kind) ? ("accept " + kind, Green)
                : Sent(c, kind) ? (kind + " request sent", OnGrey) : ("add " + kind, Orange);

        private void DrawLicense(Dictionary<int, List<WidgetTri>> glows)
        {
            PlayerCard? c = _licCard;
            if (c == null) return;
            int tag = _liCard, side = _liTitle;
            // the card: page 60's top screen for their license, fitted into the box with its own shape
            (float px0, float py0, float px1, float py1) = LicenseCardBox;
            float bx0 = Mx(px0), btop = My(py0), bx1 = Mx(px1), bbottom = My(py1);
            var card = new List<WidgetTri>();
            float cardBottom = bbottom;
            if (Multiplayer?.EmitCard(_drawMenu!, _licLicense ?? new HunterLicense(), c.Name, _licTab, card) == true)
            {
                float x0 = card.Min(MinX), x1 = card.Max(MaxX), y0 = card.Min(MinY), y1 = card.Max(MaxY);
                float s = MathF.Min((bx1 - bx0) / MathF.Max(x1 - x0, 1), (btop - bbottom) / MathF.Max(y1 - y0, 1));
                float cx0 = (bx0 + bx1 - (x1 - x0) * s) / 2;
                cardBottom = btop - (y1 - y0) * s;
                UiVertex Move(UiVertex v)
                {
                    v.X = cx0 + (v.X - x0) * s;
                    v.Y = btop - (y1 - v.Y) * s;
                    return v;
                }
                foreach (WidgetTri t in card)
                {
                    WidgetTri m = t;
                    m.A = Move(t.A); m.B = Move(t.B); m.C = Move(t.C);
                    m.Item = tag;
                    _out.Add(m);
                }
                Glow(glows, tag, cx0, btop, cx0 + (x1 - x0) * s, cardBottom, tag);
            }
            // name#tag, where they are (friends and rivals only: the server shows strangers offline), what's missing
            float mid = (bx0 + bx1) / 2;
            TextS(OnlineNames.Display(c.Name, c.Tag).ToLowerInvariant(), mid, cardBottom - Mw(22), 2, White, tag, SizeS);
            string where = "";
            (float, float, float) colour = Pale;
            if (c.Friend || c.Rival)
            {
                (where, colour) = c.Presence switch
                {
                    Presence.Lobby => ("online - in a game lobby", Cyan),
                    Presence.Match => ("online - playing a match", Orange),
                    Presence.Online => ("online - in menus", Green),
                    _ => ("offline", OnGrey),
                };
            }
            string note = _licError != null ? _licError : _licLoading && _licLicense == null ? "loading..."
                : _licLicense == null ? "no license stats yet." : "";
            if (where.Length > 0) TextS(where, mid, cardBottom - Mw(84), 2, colour, tag, SizeM);
            if (note.Length > 0) TextS(Fit(note, 40), mid, cardBottom - Mw(where.Length > 0 ? 130 : 84), 2, _licError != null ? Red : OnGrey, tag, SizeX);

            // HEAD TO HEAD
            Fill(Mx(1194), My(200), Mx(1846), My(238), HeaderBar.R, HeaderBar.G, HeaderBar.B, 1, side);
            TextS("head to head", Mx(1520), My(208), 2, Dark, side, SizeM);
            HeadToHead? h = HeadToHeads.Get(c.Code);
            (string result, (float, float, float) resultColour, string? medal) = h?.Last switch
            {
                HeadToHead.Victory => ("victory", Green, "medalgold"),
                HeadToHead.Defeat => ("defeat", Red, "medallast"),
                HeadToHead.Tie => ("tie", Pale, "medalsilver"),
                _ => ("--", OnGrey, (string?)null),
            };
            float y = 266;
            TextS("last result", Mx(1216), My(y), 0, Pale, side, SizeM);
            TextS(result, Mx(1824), My(y), 1, resultColour, side, SizeM);
            if (medal != null)
            {
                float w = _drawMenu!.Font.Measure(result) * SizeM;
                Model(@"results\" + medal, Mx(1824) - w - Mw(62), My(y - 10), Mw(48), Mw(48), side);
            }
            foreach ((string label, int n) in new[] { ("rounds against", h?.Rounds ?? 0), ("victories against", h?.Victories ?? 0) })
            {
                y += 50;
                TextS(label, Mx(1216), My(y), 0, Pale, side, SizeM);
                TextS(n.ToString(System.Globalization.CultureInfo.InvariantCulture), Mx(1824), My(y), 1, White, side, SizeM);
            }
            if (h == null) TextS("no online matches yet.", Mx(1520), My(y + 58), 2, OnGrey, side, SizeX);

            // the actions (their items hide what doesn't apply: ShowLicense)
            if (c.Code == MeCard?.Code) return;
            (string friend, (float, float, float) friendColour) = LinkLabel(c, LinkKind.Friend);
            LicenseButton(_liFriend, LicenseButtonY(0), friend, friendColour, glows);
            if (ShowRivals)
            {
                (string rival, (float, float, float) rivalColour) = LinkLabel(c, LinkKind.Rival);
                LicenseButton(_liRival, LicenseButtonY(1), rival, rivalColour, glows);
            }
            bool blocked = Blocked(c);
            // BLOCK | VOICE (OnlineMenus.Voice.cs)
            float by = LicenseButtonY(2);
            Bar(Mx(1230), My(by), Mx(1515), My(by + 66), _liBlock, glows);
            TextS(blocked ? "unblock" : "block", Mx(1372), My(by + 19), 2, blocked ? Orange : Red, _liBlock, SizeM);
            if (!blocked) DrawLicenseVoice(c, glows);
            if (_liMute >= 0)
            {
                // MUTE | REPORT (OnlineMenus.Chat.cs)
                DrawLicenseMute(c, glows);
                float ry = LicenseButtonY(3);
                Bar(Mx(1525), My(ry), Mx(1810), My(ry + 66), _liReport, glows);
                TextS("report", Mx(1667), My(ry + 19), 2, Pale, _liReport, SizeM);
            }
            else LicenseButton(_liReport, LicenseButtonY(3), "report", Pale, glows);
            if (!blocked && LicenseJoinable(c))
            {
                string? why = JoinBlock(c.GameId); // (greyed with the reason under it: release 0.2.0 F2)
                Capsule(Mx(1230), My(800), Mw(580), Mw(90), "join", why == null, _liJoin, glows, SizeS);
                if (why != null) TextS("can't join: " + why, Mx(1520), My(906), 2, OnGrey, side, SizeX);
            }
            if (blocked) TextS("you blocked this hunter.", Mx(1520), My(816), 2, OnGrey, side, SizeX);
        }

        private void LicenseButton(int item, float y, string label, (float R, float G, float B) colour, Dictionary<int, List<WidgetTri>> glows)
        {
            Bar(Mx(1230), My(y), Mx(1810), My(y + 66), item, glows);
            TextS(label, Mx(1520), My(y + 19), 2, colour, item, SizeM);
        }

        // ---- the license as it goes to the server and comes back ----

        // the card's numbers (HunterLicense's own property names, so the other side reads it back as a HunterLicense); seconds
        // in whole minutes so the menus' running clock doesn't make every connection upload again
        private static string LicenseJson(HunterLicense l)
        {
            static double Minutes(double s) => double.IsFinite(s) && s > 0 ? Math.Floor(s / 60) * 60 : 0;
            return JsonSerializer.Serialize(new
            {
                V = 1, l.RankPoints, l.RankLevel, l.WifiWins, l.WirelessWins, l.OnlineMatches, l.LocalMatches, l.BipedKills, l.AltFormKills,
                l.HeadshotKills, l.Disconnects, WirelessSeconds = Minutes(l.WirelessSeconds), WifiSeconds = Minutes(l.WifiSeconds),
                TotalSeconds = Minutes(l.TotalSeconds), l.WifiBestWinStreak, l.WifiBestKillStreak, l.WirelessBestWinStreak,
                l.WirelessBestKillStreak, l.FavoriteHunter, l.FavoriteWeapon, l.FavoriteMode, l.LuckyArena, l.BeatGame, l.Completed,
            });
        }

        // ---- a player's round icon in the menus (Seat / the FRIENDS rows): their license's hunter, the one the HUNTER
        //      LICENSE card shows (FavoriteHunter; owner 2026-10-11: "hunter of choice from license") ----

        private static readonly Dictionary<string, MatchPlayer?> s_picks = new();

        // null = no license (the person disc stays)
        private static MatchPlayer? LicensePick(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            if (s_picks.TryGetValue(json, out MatchPlayer? known)) return known;
            MatchPlayer? pick = ParseLicense(json) is { } l ? new MatchPlayer { Hunter = (Hunter)l.FavoriteHunter } : null;
            if (s_picks.Count > 256) s_picks.Clear();
            s_picks[json] = pick;
            return pick;
        }

        // another device's license (anything it sent: clamped to what the card can show), null = none or unreadable
        private static HunterLicense? ParseLicense(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            HunterLicense? l;
            try
            {
                l = JsonSerializer.Deserialize<HunterLicense>(json);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
            if (l == null) return null;
            static int N(int v) => Math.Max(0, v);
            static double S(double v) => double.IsFinite(v) && v > 0 ? v : 0;
            l.RankPoints = Math.Clamp(l.RankPoints, 0, HunterLicense.RankThresholds[4] - 1);
            l.RankLevel = HunterLicense.RankThresholds.Count(t => l.RankPoints >= t);
            (l.WifiWins, l.WirelessWins, l.OnlineMatches, l.LocalMatches) = (N(l.WifiWins), N(l.WirelessWins), N(l.OnlineMatches), N(l.LocalMatches));
            (l.BipedKills, l.AltFormKills, l.HeadshotKills, l.Disconnects) = (N(l.BipedKills), N(l.AltFormKills), N(l.HeadshotKills), N(l.Disconnects));
            (l.WifiBestWinStreak, l.WifiBestKillStreak) = (N(l.WifiBestWinStreak), N(l.WifiBestKillStreak));
            (l.WirelessBestWinStreak, l.WirelessBestKillStreak) = (N(l.WirelessBestWinStreak), N(l.WirelessBestKillStreak));
            (l.WirelessSeconds, l.WifiSeconds, l.TotalSeconds) = (S(l.WirelessSeconds), S(l.WifiSeconds), S(l.TotalSeconds));
            l.FavoriteHunter = Math.Clamp(l.FavoriteHunter, 0, 6);
            l.FavoriteWeapon = Math.Clamp(l.FavoriteWeapon, 0, 8);
            l.FavoriteMode = Math.Clamp(l.FavoriteMode, 0, 6);
            return l;
        }

        // this device's license, when it changed since the last upload (as a connection comes in; a failure only logs)
        private static async Task UploadLicense(OnlineClient client, string? license)
        {
            if (license == null || license == s_licenseSent) return;
            try
            {
                await client.RequestAsync<OkMsg>(new SetLicenseReq { License = license });
                s_licenseSent = license;
            }
            catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException)
            {
                Console.Error.WriteLine("online: license not uploaded: " + ex.Message);
            }
        }

        // ---- head to head ----

        // who sits where in a starting match: seat 0 is the host's (NetMatchSession puts the host's players first), the others
        // the lobby's cards matched by name as the lobby shows them (SeatsNow); bots and this player have none
        private Dictionary<int, PlayerCard> SeatCards(OnlineMatch m)
        {
            var codes = new Dictionary<int, PlayerCard>();
            MatchSettings? s = m.Net.Settings;
            if (s == null) return codes;
            var cards = m.Members.Where(c => c.Code != m.Game.Host.Code).ToList();
            for (int i = 0; i < s.Players.Count; i++)
            {
                MatchPlayer p = s.Players[i];
                if (p.Bot || i < m.Net.Lobby.Count && m.Net.Lobby[i].Bot) continue; // (a joiner's: the LOBBY says which)
                PlayerCard? c = i == 0 ? m.Game.Host : cards.FirstOrDefault(x => string.Equals(x.Name, p.Nickname, StringComparison.OrdinalIgnoreCase));
                if (c == null) continue;
                cards.Remove(c);
                if (i != s.LocalSeat && c.Code != MeCard?.Code) codes[i] = c;
            }
            return codes;
        }

        // RESULTS after an online match (RecompMenus.OnPageEntered): each human opponent's round against this player (a team
        // mode: the other team's), the last result and the victories, kept by their friend code
        private void CountHeadToHead()
        {
            MatchResults? r = MatchResults.Last;
            Dictionary<int, PlayerCard>? cards = s_matchCards;
            if (r == null || cards == null || !MultiplayerMenu.OnlineResults || ReferenceEquals(r, s_headToHeadCounted)) return;
            s_headToHeadCounted = r;
            // a 0.7.0 server counts it once from the host's report (OnlineMenus.Records.cs); the license reads it back
            if ((Kept?.Client ?? Conn)?.ServerAtLeast(MatchResultReq.SinceServer) == true) return;
            if (r.Local is not MatchResults.Player me) return;
            foreach (MatchResults.Player p in r.Rows)
            {
                if (p.Seat == r.LocalSeat || p.Bot || r.Teams && p.Team == me.Team || !cards.TryGetValue(p.Seat, out PlayerCard? card)) continue;
                string code = card.Code;
                HeadToHead was = HeadToHeads.Get(code) ?? new HeadToHead(0, 0, "");
                string last = me.Place < p.Place ? HeadToHead.Victory : me.Place > p.Place ? HeadToHead.Defeat : HeadToHead.Tie;
                HeadToHeads.Set(code, new HeadToHead(was.Rounds + 1, was.Victories + (last == HeadToHead.Victory ? 1 : 0), last));
            }
            Console.WriteLine($"online: head to head counted against {cards.Count} hunter{(cards.Count == 1 ? "" : "s")}");
        }

        private HeadToHeadStore HeadToHeads => _headToHead ??= HeadToHeadStore.Load(ProfileDir);

        // with one online hunter (vanilla's license fields for a friend or rival: the last encounter's result, rounds against,
        // victories against)
        internal sealed record HeadToHead(int Rounds, int Victories, string Last)
        {
            public const string Victory = "victory", Defeat = "defeat", Tie = "tie";
        }

        // kept on this device by friend code (online_head_to_head.json beside the license); no folder (the PC tools): memory only
        internal sealed class HeadToHeadStore
        {
            public const string FileName = "online_head_to_head.json";
            private readonly string? _path;
            private readonly Dictionary<string, HeadToHead> _all;

            private HeadToHeadStore(string? path, Dictionary<string, HeadToHead> all)
            {
                _path = path;
                _all = all;
            }

            public static HeadToHeadStore Load(string? dir)
            {
                string? path = dir == null ? null : Path.Combine(dir, FileName);
                var all = new Dictionary<string, HeadToHead>();
                try
                {
                    if (path != null && File.Exists(path))
                    {
                        all = JsonSerializer.Deserialize<Dictionary<string, HeadToHead>>(File.ReadAllText(path)) ?? all;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
                {
                    Console.Error.WriteLine($"online: {path}: {ex.Message}");
                }
                return new HeadToHeadStore(path, all);
            }

            public HeadToHead? Get(string code) => _all.GetValueOrDefault(code);

            public void Remove(string code)
            {
                if (_all.Remove(code)) Save();
            }

            public void Set(string code, HeadToHead h)
            {
                if (_all.TryGetValue(code, out HeadToHead? was) && was == h) return;
                _all[code] = h;
                Save();
            }

            private void Save()
            {
                if (_path == null) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.WriteAllText(_path, JsonSerializer.Serialize(_all));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"online: head to head not saved: {ex.Message}");
                }
            }
        }
    }
}
