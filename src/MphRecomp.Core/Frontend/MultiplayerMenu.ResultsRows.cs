using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Multiplayer;

namespace MphRecomp.Frontend
{
    // P8 (8-player matches; owner 2026-10-10 "instead of scrolling ... a compact view that scales for number of players"):
    // RESULTS with 5-8 players shows every row at once, two rows in each of the table's 4 cells (half vanilla's pitch, the
    // text at CompactScale). Rows 5-8 are copies of the ROM's rows 1-4 (the three name items and the three values, strings
    // of their own), added once per menu file. Team play: one team in the top two cells, the other in the bottom two, so
    // vanilla's divider between cells 2 and 3 splits them. 1-4 players: vanilla's page, nothing here moves.
    public sealed partial class MultiplayerMenu
    {
        private const int ResultsRowsShown = 4, ResultsRowsMax = 8;
        private const float CompactScale = 0.8f, CompactHalf = 7; // a row's offset from its cell's middle (DS px)

        private sealed class ResultsCopies
        {
            public readonly int[][] Name = new int[4][]; // rows 5-8: the three name items
            public readonly int[] NameString = { -1, -1, -1, -1 };
            public readonly int[][] Value = new int[4][];
            public readonly int[][] ValueString = new int[4][];
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuFile, ResultsCopies> s_results8 = new();
        private ResultsCopies _results8 = new();

        // (BindResults, once the ROM's rows are found)
        private void BindResults8(MenuFile file, MenuStrings strings, MenuPage p)
        {
            if (s_results8.TryGetValue(file, out ResultsCopies? bound))
            {
                _results8 = bound;
                return;
            }
            _results8 = new ResultsCopies();
            for (int r = 0; r < 4; r++)
            {
                int Copy(int item, int stringId)
                {
                    MenuItem it = p.Items[item];
                    int at = p.Items.Count;
                    p.AddItem(n => SoundsLike(RecompMenus.Clone(it, n, 0, 0, stringId: stringId, links: Array.Empty<MenuLink>(),
                        actions: Array.Empty<MenuAction>()), it.Index));
                    return at;
                }
                int name = strings.Add("");
                _results8.NameString[r] = name;
                _results8.Name[r] = _resultName[r].Select(item => Copy(item, name)).ToArray();
                _results8.ValueString[r] = _resultValue[r].Select(_ => strings.Add("")).ToArray();
                _results8.Value[r] = _resultValue[r].Select((item, c) => Copy(item, _results8.ValueString[r][c])).ToArray();
            }
            s_results8.AddOrUpdate(file, _results8);
        }

        // a table row's items: rows 0-3 the ROM's, 4-7 the copies
        private int[] ResultNameItems(int row) => row < 4 ? _resultName[row] : _results8.Name[row - 4];

        private int ResultNameString(int row) => row < 4 ? _resultNameString[row] : _results8.NameString[row - 4];

        private int ResultValueItem(int row, int c) => row < 4 ? _resultValue[row][c] : _results8.Value[row - 4][c];

        private int ResultValueString(MenuPage page, int row, int c) => row < 4 ? StringOf(page, _resultValue[row][c]) : _results8.ValueString[row - 4][c];

        // the ROM's strings are filled, ours set (each ignores the other kind)
        private static void PutString(MenuStrings s, int id, string text)
        {
            s.Fill(id, text);
            s.Set(id, text);
        }

        // the players in table order: vanilla's (team play with the first two on different teams: each team its own half,
        // 0x213720C); 5-8 players: in order, or with teams one team's rows 0-3 and the other's 4-7
        internal static MatchResults.Player?[] ResultsShown(MatchResults r)
        {
            var shown = new MatchResults.Player?[ResultsRowsMax];
            if (r.Rows.Count <= ResultsRowsShown)
            {
                // team play: the first row's team in the top half (rows 0-1), the other in the bottom half (2-3). The game's
                // result order can alternate teams (its sort also swaps players across teams by their own points, and a team
                // tie does it outright): vanilla's split (rows 0 + 1 on different teams -> the second row down to row 2)
                // then pushed the 4th player off the table (release 0.2.0 F1, a bot missing from RESULTS)
                if (r.Teams && r.Rows.Count(p => p.Team == r.Rows[0].Team) <= 2 && r.Rows.Count(p => p.Team != r.Rows[0].Team) <= 2)
                {
                    int topRow = 0, bottomRow = 2;
                    foreach (MatchResults.Player p in r.Rows)
                    {
                        shown[p.Team == r.Rows[0].Team ? topRow++ : bottomRow++] = p;
                    }
                    return shown;
                }
                for (int i = 0; i < r.Rows.Count; i++) shown[i] = r.Rows[i]; // (1 vs 3: in order, nobody missing)
                return shown;
            }
            if (!r.Teams)
            {
                for (int i = 0; i < r.Rows.Count && i < ResultsRowsMax; i++) shown[i] = r.Rows[i];
                return shown;
            }
            int first = r.Rows[0].Team, top = 0, bottom = 4;
            foreach (MatchResults.Player p in r.Rows)
            {
                if (p.Team == first && top < 4) shown[top++] = p;
                else if (bottom < ResultsRowsMax) shown[bottom++] = p;
                else if (top < 4) shown[top++] = p; // (a team past 4: not a legal match, but nobody goes missing)
            }
            return shown;
        }

        // the rows' places: vanilla's 4 rows, or 8 at half the pitch in the 4 cells (names' and values' item y, DS up)
        private void LayoutResults8(MenuEngine menu, bool compact)
        {
            float[] cellY = { 117, 89, 61, 33 };
            for (int row = 0; row < ResultsRowsMax; row++)
            {
                int source = row % 4;
                float py = cellY[source] - 189; // the row's text middle, menu y
                float ty = cellY[row / 2] - 189 + (row % 2 == 0 ? CompactHalf : -CompactHalf);
                foreach (int item in ResultNameItems(row).Concat(Enumerable.Range(0, 3).Select(c => ResultValueItem(row, c))))
                {
                    if (item < 0) continue;
                    if (compact) menu.SetItemTransform(item, CompactScale, menu.Page!.Items[item].X, py, 0, ty - py);
                    else menu.ClearItemTransform(item);
                }
            }
        }

        // the top screen's place with 5-8 players: 1st-3rd and last as vanilla's (the last place medal is the skull), 4th-7th
        // as words of their own with no medal
        private (string Text, int Medal, bool Lightning) ResultsPlaceWide(MatchResults r, MatchResults.Player local, bool tied)
        {
            int place = Math.Max(0, local.Place);
            bool last = place > 0 && r.Rows.All(q => q.Place <= place);
            if (last) return (Word(PlaceWords[3]), 3, false);
            if (place < 3) return (Word(tied ? TiedWords[place] : PlaceWords[place]), place, true);
            string nth = $"{place + 1}th place";
            return (tied ? $"tied for {nth}" : nth, -1, true);
        }
    }
}
