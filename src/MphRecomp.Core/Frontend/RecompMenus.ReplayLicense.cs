using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MphRead;
using MphRecomp.Multiplayer;
using MphRecomp.Online;
using MphRecomp.Replay;
using MphRecomp.Config;

// A replay's HUNTER LICENSE (O10, Replays track; owner 2026-10-09: "tapping one opens the HUNTER LICENSE view (design V_C ...)
// with ADD FRIEND / ADD RIVAL"; mockup extract_out/online_menus/mockups/V_C_license_from_replay.png). A player on a replay's
// details page who carries a friend code opens it: left, their card as page 60 draws it from the license snapshot the replay
// keeps (MultiplayerMenu.EmitCard; L / R or a tap turns its tabs), name#tag in its name slot, and under it "as it was <date>
// (replay)"; right, HEAD TO HEAD (counted on this device by their friend code, as ONLINE PLAY's license view), SEE CURRENT
// (ONLINE PLAY's live license view, OnlineMenus.License.cs: back comes here) and ADD FRIEND / ADD RIVAL side by side (what they
// become when already linked or asked: OnlineMenus.License.cs's LinkAction / LinkLabel). Not for this device's own seat's
// actions. The page is REPLAYS' (the CREATE OR JOIN frame, MenuLayout.Replays); the actions need the server: the connection
// is made a frame after the page comes in (ONLINE PLAY closes its connection as any other page comes in) and closed as the
// page goes, as ONLINE PLAY's pages do.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private static int s_replayLicensePage = -1;

        // our callbacks (ReplaySeatCall 2150-2153 and ReplaySeat8Call 2174-2177 open it from the details page)
        private const int RlCardCall = 2154, RlPrevCall = 2155, RlNextCall = 2156, RlCurrentCall = 2157, RlFriendCall = 2158, RlRivalCall = 2159;

        // a tap on the card's k-th tab (owner 2026-10-24: as the main menu's card); in the gap ReplayShare.cs leaves (ReplayCall)
        private const int RlTabCall = 2163, RlTabLastCall = RlTabCall + CardTabs - 1;
        private readonly int[] _rlTabItem = Unset(CardTabs);
        private readonly MenuAction[][] _rlTabRect = new MenuAction[CardTabs][]; // (rects set as the card is drawn: it is fitted into its box)

        // items (-1 = none)
        private int _rlPanel = -1, _rlSide = -1, _rlBack = -1, _rlTitle = -1, _rlCard = -1, _rlCurrent = -1, _rlFriend = -1, _rlRival = -1;

        // the seat shown (of the replay open on the details page), the card's tab, the menu to connect from next frame
        private int _rlSeat = -1, _rlTab;
        private string? _rlFor; // the replay + seat the tab belongs to
        private MenuEngine? _rlConnect;

        // where the card may sit (mockup px, as ONLINE PLAY's view but ending higher: the "as it was" line goes under it)
        private static readonly (float X0, float Top, float X1, float Bottom) ReplayCardBox = (100, 225, 1120, 745);

        // tools (MphRead.Tools -replaymock licensecheck)
        internal static int ReplayLicensePage => s_replayLicensePage;
        internal static int ReplaysDetailPage => s_detailPage;
        internal int ReplayLicenseSeat => _rlSeat;
        internal int ReplayLicenseTab => _rlTab;

        private void InstallReplayLicense(Func<int, MenuAction[], MenuPage> addFramePage, Func<int, float, float, float, float, int, MenuItem> input,
            MenuItem panel, MenuItem backIcon, MenuItem backButton, MenuItem title, int detailPage)
        {
            MenuItem Px(MenuPage page, float x, float y, float w, float h, int call) => page.AddItem(i => input(i, Mx(x), My(y), Mx(x + w), My(y + h), call));
            MenuItem Panel(int i, float x0, float x1) => Clone(panel, i, (x0 + x1) / 2 - PanelMid, 0, stretchX: x1 - x0 - PanelWidth,
                stretchDown: PanelStretchDown);

            MenuPage page = addFramePage(detailPage, new[] { Call(MenuKeys.R, RlNextCall), Call(MenuKeys.L, RlPrevCall) });
            s_replayLicensePage = page.Index;
            _rlPanel = page.AddItem(i => Panel(i, Mx(70), Mx(1150))).Index;
            _rlSide = page.AddItem(i => Panel(i, Mx(1190), Mx(1850))).Index;
            page.AddItem(i => Clone(backIcon, i, 0, 0));
            _rlBack = page.AddItem(i => Clone(backButton, i, 0, 0, actions: backButton.Actions.Select(a => WithTarget(a, detailPage)).ToList())).Index;
            _rlTitle = page.AddItem(i => Clone(title, i, 0, 0, stringId: _strings.Add("HUNTER LICENSE"))).Index;
            (float cx, float cy, float cx1, float cy1) = ReplayCardBox;
            _rlCard = Px(page, cx, cy, cx1 - cx, cy1 - cy, RlCardCall).Index;
            for (int t = 0; t < CardTabs; t++)
            {
                MenuAction[] rect = { TouchRect(0, 0, 0, 0, RlTabCall + t) };
                _rlTabRect[t] = rect;
                _rlTabItem[t] = page.AddItem(i => new MenuItem
                {
                    Index = i, Field34 = (byte)MenuState.Idle, Field35 = (byte)MenuState.Idle, States = Array.Empty<MenuItemState>(),
                    Links = Array.Empty<MenuLink>(), Actions = rect
                }).Index;
            }
            _rlCurrent = Px(page, 1230, 500, 580, 90, RlCurrentCall).Index;
            _rlFriend = Px(page, 1220, 640, 295, 66, RlFriendCall).Index;
            _rlRival = Px(page, 1535, 640, 295, 66, RlRivalCall).Index;
        }

        private void ApplyReplayLicenseSounds(Action<int, int, int, int> like)
        {
            like(s_replayLicensePage, _rlBack, MultiplayerMenu.CreateJoinPage, _soundBack);
            like(s_replayLicensePage, _rlCard, OptionBoxPage, _soundToggle);
            foreach (int t in _rlTabItem) like(s_replayLicensePage, t, OptionBoxPage, _soundToggle);
            like(s_replayLicensePage, _rlCurrent, MultiplayerMenu.CreateJoinPage, _soundListRow);
            like(s_replayLicensePage, _rlFriend, OptionBoxPage, _soundToggle);
            like(s_replayLicensePage, _rlRival, OptionBoxPage, _soundToggle);
        }

        // the seat shown, when the replay open still carries who they are
        private ReplaySeatIdentity? ShownIdentity => OpenInfo is ReplayInfo r && _rlSeat >= 0 ? LicenseOf(r, _rlSeat) : null;

        private static PlayerCard CardOf(ReplaySeatIdentity id) => new() { Code = id.FriendCode!, Name = id.Name, Tag = id.Tag };

        // not this device's own seat (the one it recorded or was sent as), nor its own friend code
        private bool Others(ReplayInfo r, ReplaySeatIdentity id) => _rlSeat != r.LocalSeat && id.FriendCode != MeCard?.Code;

        // RecompMenus.OnPageEntered
        private void EnterReplayLicense(MenuEngine menu)
        {
            ListReplays();
            if (OpenInfo is not ReplayInfo r || ShownIdentity is not ReplaySeatIdentity id)
            {
                menu.GoTo(s_detailPage); // (the replay went, or it carries nobody there)
                return;
            }
            bool actions = Others(r, id);
            Show(menu, _rlFriend, actions);
            Show(menu, _rlRival, actions && ShowRivals);
            if (_lastPage != OnlineLicensePage) menu.SetState(_rlCard, MenuState.Focused);
            if (actions) _rlConnect = menu; // (TickReplayLicense: next frame)
        }

        // every menu frame (TickReplays): the connection for the actions' labels and requests
        private void TickReplayLicense()
        {
            MenuEngine? menu = _rlConnect;
            _rlConnect = null;
            if (menu?.Page?.Index != s_replayLicensePage) return;
            _rlRecordMenu = menu;
            RefreshOnline(lists: true);
            FetchReplayRecord(); // (not connected yet: again from the connection's Post, OnlineMenus.RefreshOnline)
        }

        // HEAD TO HEAD is the server's (0.7.0): this device only holds what a license last read back, and a host that never
        // opened the other player's live license held nothing ("no online matches yet" while the other side showed the
        // match; owner 2026-10-11, PC host vs Samsung). So the replay's license reads the record from the server too.
        private MenuEngine? _rlRecordMenu;
        private void FetchReplayRecord()
        {
            if (_rlRecordMenu?.Page?.Index != s_replayLicensePage || OpenInfo is not ReplayInfo r
                || ShownIdentity is not ReplaySeatIdentity id || !Others(r, id) || Conn is not { Connected: true } client) return;
            string code = id.FriendCode!;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    PlayerMsg p = await client.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = code });
                    Post(() =>
                    {
                        KeepServerRecord(client, p.Player);
                        Redraw();
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Console.Error.WriteLine("online: replay license record: " + ex.Message);
                }
            });
        }

        private void ReplayLicenseCall(MenuEngine menu, int call)
        {
            if (CallSeat(call) is int seat and >= 0)
            {
                if (OpenInfo is not ReplayInfo r || LicenseOf(r, seat) == null) return;
                string key = r.Path + "#" + seat;
                if (_rlFor != key) _rlTab = 0;
                _rlFor = key;
                _rlSeat = seat;
                menu.GoTo(s_replayLicensePage);
                return;
            }
            if (ShownIdentity is not ReplaySeatIdentity id) return;
            if (call is >= RlTabCall and <= RlTabLastCall)
            {
                _rlTab = call - RlTabCall;
                return;
            }
            switch (call)
            {
            case RlCardCall:
            case RlNextCall:
                _rlTab = (_rlTab + 1) % CardTabs;
                if (call == RlNextCall) KeyClick(menu, _rlTabItem[0]);
                break;
            case RlPrevCall:
                _rlTab = (_rlTab + CardTabs - 1) % CardTabs;
                KeyClick(menu, _rlTabItem[0]);
                break;
            case RlCurrentCall:
                OpenLicense(menu, CardOf(id)); // (the server's card as it is now; back comes here)
                break;
            case RlFriendCall:
                LinkAction(CardOf(id), LinkKind.Friend);
                break;
            case RlRivalCall:
                LinkAction(CardOf(id), LinkKind.Rival);
                break;
            }
        }

        private void DrawReplayLicense(Dictionary<int, List<WidgetTri>> glows)
        {
            if (OpenInfo is not ReplayInfo r || ShownIdentity is not ReplaySeatIdentity id) return;
            int tag = _rlCard, side = _rlTitle;
            string name = id.Tag > 0 ? StreamerPrivacy.Display(id.Name, id.Tag) : StreamerPrivacy.Name(id.Name, r.IsOwnSeat(id.Seat)); // (the card writes names as they are)

            // the card as it was: page 60's top screen for the license the replay kept, fitted into the box with its own shape
            (float px0, float py0, float px1, float py1) = ReplayCardBox;
            float bx0 = Mx(px0), btop = My(py0), bx1 = Mx(px1), bbottom = My(py1);
            HunterLicense? license = ParseLicense(id.LicenseJson);
            var card = new List<WidgetTri>();
            float cardX0 = bx0, cardX1 = bx1, cardBottom = bbottom;
            if (Multiplayer?.EmitCard(_drawMenu!, license ?? new HunterLicense(), name, _rlTab, card) == true)
            {
                float x0 = card.Min(MinX), x1 = card.Max(MaxX), y0 = card.Min(MinY), y1 = card.Max(MaxY);
                float s = MathF.Min((bx1 - bx0) / MathF.Max(x1 - x0, 1), (btop - bbottom) / MathF.Max(y1 - y0, 1));
                cardX0 = (bx0 + bx1 - (x1 - x0) * s) / 2;
                cardX1 = cardX0 + (x1 - x0) * s;
                cardBottom = btop - (y1 - y0) * s;
                UiVertex Move(UiVertex v)
                {
                    v.X = cardX0 + (v.X - x0) * s;
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
                // the tabs' touch areas: the strip under the card, fitted the way the card was (touch space: the menu's y + 192)
                float TabX(float v) => cardX0 + (v - x0) * s;
                float TabY(float v) => btop - (y1 - v) * s;
                for (int k = 0; k < CardTabs; k++)
                {
                    _rlTabRect[k][0] = TouchRect(TabX(MultiplayerMenu.CardTabX0 + MultiplayerMenu.CardTabWidth * k),
                        TabY(MultiplayerMenu.CardTabY0) + 192, TabX(MultiplayerMenu.CardTabX0 + MultiplayerMenu.CardTabWidth * (k + 1) - 1),
                        TabY(MultiplayerMenu.CardTabY1) + 192, RlTabCall + k);
                }
            }
            // "as it was <date> (replay)": a bar under the card
            string when = r.Recorded is DateTime t0 ? t0.ToString("MMM d HH:mm", CultureInfo.InvariantCulture).ToLowerInvariant() + " " : "";
            float stampTop = cardBottom - Mw(28), stampBottom = stampTop - Mw(64);
            Fill(cardX0, stampTop, cardX1, stampBottom, 0, 0, 0, 0.35f, side);
            Box(cardX0, stampTop, cardX1, stampBottom, side);
            string stamp = $"as it was {when}(replay)";
            float stampSize = MathF.Min(SizeM, (cardX1 - cardX0 - Mw(60)) / MathF.Max(_drawMenu!.Font.Measure(stamp), 1));
            TextS(stamp, (cardX0 + cardX1) / 2, stampTop - (stampTop - stampBottom - 8 * stampSize) / 2, 2, Pale, side, stampSize);
            if (license == null) TextS("no license stats in this replay.", (cardX0 + cardX1) / 2, stampBottom - Mw(30), 2, OnGrey, side, SizeX);

            // HEAD TO HEAD (as ONLINE PLAY's license view; not with this device's own seat)
            bool others = Others(r, id);
            if (others) DrawHeadToHead(id.FriendCode!, side);

            // SEE CURRENT, then ADD FRIEND / ADD RIVAL (not on this device's own seat)
            Capsule(Mx(1230), My(500), Mw(580), Mw(90), "see current", true, _rlCurrent, glows, SizeS);
            if (!others) return;
            PlayerCard c = CardOf(id);
            foreach ((int item, float x, string kind) in new[] { (_rlFriend, 1220f, LinkKind.Friend), (_rlRival, 1535f, LinkKind.Rival) })
            {
                if (kind == LinkKind.Rival && !ShowRivals) continue; // (rivals hidden: OnlineMenus.ShowRivals)
                (string label, (float, float, float) colour) = LinkLabel(c, kind);
                Bar(Mx(x), My(640), Mx(x + 295), My(706), item, glows);
                TextS(label, Mx(x + 147.5f), My(659), 2, colour, item, label.Length > 12 ? SizeX : SizeM);
            }
            if (Conn is not { Connected: true }) TextS(_connecting ? "connecting to the server..." : Fit(_netError ?? "", 44), Mx(1525), My(740), 2,
                _connecting ? OnGrey : Red, side, SizeX);
        }

        private void DrawHeadToHead(string code, int side)
        {
            Fill(Mx(1194), My(200), Mx(1846), My(238), HeaderBar.R, HeaderBar.G, HeaderBar.B, 1, side);
            TextS("head to head", Mx(1520), My(208), 2, Dark, side, SizeM);
            HeadToHead? h = HeadToHeads.Get(code);
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
                TextS(n.ToString(CultureInfo.InvariantCulture), Mx(1824), My(y), 1, White, side, SizeM);
            }
            if (h == null) TextS("no online matches yet.", Mx(1520), My(y + 58), 2, OnGrey, side, SizeX);
        }
    }
}
