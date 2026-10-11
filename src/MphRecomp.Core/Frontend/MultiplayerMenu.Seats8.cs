using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;

namespace MphRecomp.Frontend
{
    // P8 (8-player matches, owner pick 2026-10-10: "B only when there are more than four"): the lobby (38, and the
    // joiner's 39) seats 8. Seats 5-8 are copies of the ROM's seat 2-4 pieces (grid, pictures, name, arrows, check,
    // remove, stars, team), added once per menu file; their calls are the ROM seat's, moved to Seat8CallBase + 1000 per
    // copy. With a 5th seat used, every seat is drawn at Seats8Scale in two rows of four (MenuEngine.SetItemTransform),
    // and the top screen's PLAYERS box lists 8 names in two columns. With 4 or fewer seats used nothing here moves:
    // vanilla's lobby. Seats past 4 move down into free seats 2-4 once 4 or fewer are used again (CompactSeats).
    public sealed partial class MultiplayerMenu
    {
        private const int Seat8CallBase = 5000, Seat8CallStride = 1000;
        private const float Seats8Scale = 0.56f;
        // each display column's grid centre (DS px), the seat block's centre (DS y, names to star bars) and the rows'
        private static readonly float[] Seat8ColumnX = { 42, 99, 156, 212 };
        private const float SeatBlockY = 102, Seats8Row1Y = 81, Seats8Row2Y = 129.5f;
        private const float TopBarLift = 22; // (item y, before the 0.56: the top row's team bars over its checks)
        // the PLAYERS box: its names (x 194) in two columns at 0.8
        private const float Panel8Scale = 0.8f, Panel8LeftX = 167, Panel8RightX = 221;

        private sealed class Seats8Bound
        {
            public readonly Dictionary<int, SeatItems[]> Seats = new(); // lobby page -> seats 5-8's copies
            public readonly Dictionary<int, (int[] Rom, int[] Copies)> Panel = new(); // setup page -> PLAYERS names
            public readonly int[] Strings = { -1, -1, -1, -1 }; // seats 5-8's names (the lobby's and the box's)
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuFile, Seats8Bound> s_seats8 = new();
        private Seats8Bound _seats8 = new();

        // a 5th seat is used: two rows
        private static bool Eight => _setup.Seats.Skip(PlayerEntity.VanillaMaxPlayers).Any(s => s.Used);

        // the copied seat's calls: (seat 4..7, the ROM seat's call)
        private static int Seat8Call(int seat, int call) => call is >= 140 and < 200 ? Seat8CallBase + (seat - 4) * Seat8CallStride + call : call;

        private static bool FromSeat8Call(ref int call, out int seat)
        {
            seat = -1;
            if (call < Seat8CallBase) return false;
            seat = 4 + (call - Seat8CallBase) / Seat8CallStride;
            call = (call - Seat8CallBase) % Seat8CallStride;
            return true;
        }

        // seat 4..7 copies the ROM's seat 1, 3, 1, 3 (seat 0, this player's, has no bot pieces; seat 2 is the one with
        // its check above the grid, which the bottom row has no room for)
        private static int Seat8Source(int seat) => 1 + 2 * ((seat - 4) % 2);

        // (Bind, after BindStarBars: the copies take the star bars as they are fixed there)
        private void BindSeats8(MenuFile file, MenuStrings strings)
        {
            if (s_seats8.TryGetValue(file, out Seats8Bound? bound))
            {
                _seats8 = bound;
                return;
            }
            _seats8 = new Seats8Bound();
            for (int k = 0; k < 4; k++) _seats8.Strings[k] = strings.Add("");
            foreach (int index in new[] { LobbyPage, JoinLobbyPage })
            {
                if (file.Pages.Count <= index) continue;
                MenuPage page = file.Pages[index];
                var copies = new SeatItems[4];
                for (int seat = 4; seat < 8; seat++)
                {
                    SeatItems src = FindSeat(page, Seat8Source(seat));
                    var si = new SeatItems();
                    int s = seat;
                    int Copy(int item, int stringId = -1)
                    {
                        if (item < 0) return -1;
                        MenuItem it = page.Items[item];
                        int at = page.Items.Count;
                        page.AddItem(n => SoundsLike(RecompMenus.Clone(it, n, 0, 0, stringId: stringId, init: MenuState.Hidden,
                            links: Array.Empty<MenuLink>(), actions: it.Actions.Select(a => CopyAction(a,
                                calls: a.Calls.Select(c => (c.A, Seat8Call(s, c.B))).ToList(), item: n)).ToList()), it.Index));
                        return at;
                    }
                    si.Grid = Copy(src.Grid);
                    foreach ((Hunter h, int item) in src.Portrait) si.Portrait[h] = Copy(item);
                    si.Spot = Copy(src.Spot);
                    si.ArrowFrame = Copy(src.ArrowFrame);
                    si.Left = Copy(src.Left);
                    si.Right = Copy(src.Right);
                    si.TeamA = Copy(src.TeamA);
                    si.TeamB = Copy(src.TeamB);
                    si.Check = Copy(src.Check);
                    si.Remove = Copy(src.Remove);
                    for (int k = 0; k < 3; k++) si.Stars[k] = Copy(src.Stars[k]);
                    si.Name = Copy(src.Name, _seats8.Strings[seat - 4]);
                    copies[seat - 4] = si;
                }
                _seats8.Seats[index] = copies;
            }
            // the PLAYERS box on every setup page that has it
            float[] playerY = { 226, 216, 206, 196 };
            for (int index = CreateJoinPage; index < file.Pages.Count && index <= JoinLobbyPage; index++)
            {
                MenuPage page = file.Pages[index];
                if (!IsSetupPage(page)) continue;
                int[] rom = playerY.Select(y => TextAt(page, 194, y)).ToArray();
                if (rom.Any(i => i < 0)) continue;
                int[] copies = new int[4];
                for (int r = 0; r < 4; r++)
                {
                    MenuItem it = page.Items[rom[r]];
                    int stringId = _seats8.Strings[r];
                    copies[r] = page.Items.Count;
                    page.AddItem(n => SoundsLike(RecompMenus.Clone(it, n, 0, 0, stringId: stringId, init: MenuState.Hidden,
                        links: Array.Empty<MenuLink>(), actions: Array.Empty<MenuAction>()), it.Index));
                }
                _seats8.Panel[index] = (rom, copies);
            }
            s_seats8.AddOrUpdate(file, _seats8);
        }

        // the pieces the ROM puts on the other side of the grid for seat 2 (check, arrows and stars above, name below)
        // than for seats 1 and 3 (name above, the rest below)
        private static IEnumerable<int> MirroredPieces(SeatItems si)
        {
            foreach (int item in new[] { si.Name, si.ArrowFrame, si.Left, si.Right, si.Check, si.TeamA, si.TeamB }) yield return item;
            foreach (int item in si.Stars) yield return item;
        }

        // two rows (owner report "check overlap", 2026-10-10): the top row takes seat 2's sides (check above, name
        // below) and the bottom row seat 1's (name above, check below), so nothing of one row sits between the rows.
        // A piece's move (item y, up) from its ROM seat's side to its row's
        private Dictionary<int, float> SideMoves(MenuPage page)
        {
            var moves = new Dictionary<int, float>();
            if (_seats == null) return moves;
            int[] above = MirroredPieces(_seats[2]).ToArray(), below = MirroredPieces(_seats[1]).ToArray();
            for (int s = 1; s < 8; s++)
            {
                int source = s < 4 ? s : Seat8Source(s);
                bool top = s < 4, sourceTop = source == 2;
                if (top == sourceTop) continue;
                int[] mine = MirroredPieces(_seats[s]).ToArray(), from = sourceTop ? above : below, to = top ? above : below;
                for (int k = 0; k < mine.Length; k++)
                {
                    if (mine[k] < 0 || from[k] < 0 || to[k] < 0) continue;
                    moves[mine[k]] = page.Items[to[k]].Y - page.Items[from[k]].Y;
                }
            }
            // TEAM PLAY's bars: the bottom row's sit between the rows (above their names), so the top row's go above its
            // checks, TopBarLift over where seat 1's bar is (each seat's own bar, seat 0's included)
            foreach (Func<SeatItems, int> bar in new Func<SeatItems, int>[] { si => si.TeamA, si => si.TeamB })
            {
                int above1 = bar(_seats[1]);
                if (above1 < 0) continue;
                for (int s = 0; s < 4; s++)
                {
                    int mine = bar(_seats[s]);
                    if (mine >= 0) moves[mine] = page.Items[above1].Y + TopBarLift - page.Items[mine].Y;
                }
            }
            return moves;
        }

        // every piece of a seat (what moves with it)
        private static IEnumerable<int> SeatPieces(SeatItems si)
        {
            foreach (int item in si.Portrait.Values) yield return item;
            foreach (int item in new[] { si.Grid, si.Spot, si.Name, si.ArrowFrame, si.Left, si.Right, si.Check, si.Remove, si.TeamA, si.TeamB })
            {
                yield return item;
            }
            foreach (int item in si.Stars) yield return item;
        }

        // the lobby's seats and PLAYERS box as many seats as are used need: vanilla's, or two rows (Lobby, JoinLobby)
        private void LayoutSeats8(MenuEngine menu, Action<int, bool> show)
        {
            if (_seats == null || _seats.Length < 8) return;
            bool eight = Eight;
            Dictionary<int, float> sides = eight ? SideMoves(menu.Page!) : new();
            for (int s = 0; s < 8; s++)
            {
                SeatItems si = _seats[s];
                int source = s < 4 ? s : Seat8Source(s);
                float px = Seat8ColumnX[source], py = -SeatBlockY;
                float tx = Seat8ColumnX[s % 4], ty = -(s < 4 ? Seats8Row1Y : Seats8Row2Y);
                foreach (int item in SeatPieces(si))
                {
                    if (item < 0) continue;
                    float side = sides.TryGetValue(item, out float dy) ? dy * Seats8Scale : 0;
                    if (eight) menu.SetItemTransform(item, Seats8Scale, px, py, tx - px, ty - py + side);
                    else menu.ClearItemTransform(item);
                }
                if (s >= 4) show(si.Grid, eight);
            }
            Panel8(menu, show);
        }

        // the PLAYERS box: 8 names in two columns when a 5th seat is used (any setup page; OnPageEntered, the lobbies)
        private void Panel8(MenuEngine menu, Action<int, bool>? show = null)
        {
            if (menu.Page == null || !_seats8.Panel.TryGetValue(menu.Page.Index, out var panel)) return;
            show ??= (item, visible) => Show(menu, item, visible);
            bool eight = Eight;
            for (int r = 0; r < 4; r++)
            {
                foreach ((int item, float x) in new[] { (panel.Rom[r], Panel8LeftX), (panel.Copies[r], Panel8RightX) })
                {
                    MenuItem it = menu.Page.Items[item];
                    if (eight) menu.SetItemTransform(item, Panel8Scale, 194, it.Y - 192, x - 194, 0);
                    else menu.ClearItemTransform(item);
                }
                show(panel.Copies[r], eight);
            }
        }

        // a bot's skin badge (CharacterPicker): as small as its seat
        private static float SeatScale => Eight ? Seats8Scale : 1;

        // 4 or fewer seats used again: the ones past 4 move into the free seats 2-4 (vanilla's lobby again)
        private static void CompactSeats()
        {
            if (_setup.Seats.Count(s => s.Used) > PlayerEntity.VanillaMaxPlayers) return;
            for (int s = PlayerEntity.VanillaMaxPlayers; s < _setup.Seats.Length; s++)
            {
                if (!_setup.Seats[s].Used) continue;
                int free = Array.FindIndex(_setup.Seats, 1, x => !x.Used);
                if (free < 0 || free >= PlayerEntity.VanillaMaxPlayers) return;
                _setup.Seats[free] = _setup.Seats[s];
                _setup.Seats[s] = new Seat();
            }
        }

        // ADD BOT's seat: vanilla's (the last free of 4, 3, 2), then 5-8 in order
        private static int AddBotSeat()
        {
            for (int s = PlayerEntity.VanillaMaxPlayers - 1; s >= 1; s--)
            {
                if (!_setup.Seats[s].Used) return s;
            }
            for (int s = PlayerEntity.VanillaMaxPlayers; s < _setup.Seats.Length; s++)
            {
                if (!_setup.Seats[s].Used) return s;
            }
            return -1;
        }
    }
}
