using System;

// The game's ending in the menus (owner queue #36; ROM notes in docs/ENDINGS.md). After an ending movie the campaign
// hands over to the front end, which plays the credits (pages 6-12, the same pages as Options > CREDITS, but they can't be
// skipped: ov0 0x2128afc acts only for the Options run), then the results page -- 49 after the good ending
// ("CONGRATULATIONS", "mission complete!"), 50 after the bad one ("mission complete...?", gorea battle time "incomplete")
// -- with the numbers ov0 0x211e538 writes into its placeholders. OK (call 206, 0x212ef00) shows "saving to the game
// card" and goes to page 2, the boot logos, then the intro movie and the title.

namespace MphRecomp.Frontend
{
    // what the results page shows (the campaign fills it from the story save when the ending movie is over)
    public sealed record EndingResults(bool Good, int CompletePercent, long TotalSeconds, long GoreaSeconds, uint Deaths, uint Defeated);

    public sealed class GameEnding
    {
        public const int CreditsPage = 6, GoodPage = 49, BadPage = 50, AfterPage = 2;
        public const int CreditsSkippedCall = 11, CreditsOverCall = 12, OkCall = 206;

        // set by a campaign just before it leaves for the menus; the next FrontendSession takes it (FrontendSession.ReturnPage
        // then opens the menus at the credits)
        public static EndingResults? Pending { get; set; }

        // the ending this session is showing, null when none (the credits are then Options' own)
        public EndingResults? Current { get; set; }

        // what an ending movie that ended the session in roomId leaves for the results page: Gorea_b2 (92) is the good
        // ending, Gorea_b1 (91) the bad one; playSeconds = the file's play time (ours stands in for the game's four timers)
        public static EndingResults From(MphRead.StorySave story, int roomId, long playSeconds)
        {
            return new EndingResults(
                Good: roomId == MphRecomp.Campaign.ShipNavigation.GoreaSecondRoomId,
                CompletePercent: Math.Clamp(story.GetCompletionPercentage(), 0, 100),
                TotalSeconds: playSeconds,
                GoreaSeconds: (long)(story.Stats.Gorea1Time + story.Stats.Gorea2Time),
                Deaths: story.Stats.Deaths,
                Defeated: story.Stats.HunterKills);
        }

        // the placeholder strings, from the items' positions on page 49 (page 50 shares the same strings)
        private int _total = -1, _gorea = -1, _percent = -1, _deaths = -1, _defeated = -1;

        public bool Bound => _total >= 0 && _percent >= 0 && _deaths >= 0 && _defeated >= 0;

        public bool Bind(MenuFile file)
        {
            if (file.Pages.Count <= BadPage) return false;
            MenuPage page = file.Pages[GoodPage];
            int TextAt(float x, float y)
            {
                foreach (MenuItem it in page.Items)
                {
                    if (it.IsText && MathF.Abs(it.X - x) < 1.5f && MathF.Abs(it.Y - y) < 1.5f) return it.States[0].Text!.StringId;
                }
                return -1;
            }
            _total = TextAt(38, 136);
            _gorea = TextAt(38, 82);
            _percent = TextAt(128, 25);
            _deaths = TextAt(218, 136);
            _defeated = TextAt(218, 82);
            return Bound;
        }

        // the centre of the page's OK button (the item whose action makes call 206), in the engine's touch space
        public static bool TryOkPoint(MenuPage page, out float x, out float y)
        {
            foreach (MenuItem it in page.Items)
            {
                foreach (MenuAction a in it.Actions)
                {
                    if (a.Calls.Count > 0 && a.Calls[^1].B == OkCall && MenuEngine.TryRect(a, it, out float x0, out float y0, out float x1, out float y1))
                    {
                        x = (x0 + x1) / 2;
                        y = (y0 + y1) / 2;
                        return true;
                    }
                }
            }
            x = y = 0;
            return false;
        }

        // the numbers into the placeholders, in the formats of 0x211e538: total "%dh %02dm", gorea "%dm %02ds" (the hours
        // are dropped, as there), complete "%d%s" with the game's "%", deaths and defeated "%d"
        public void Fill(MenuStrings strings)
        {
            if (Current is not EndingResults r || !Bound) return;
            long total = Math.Max(0, r.TotalSeconds), gorea = Math.Max(0, r.GoreaSeconds);
            strings.Fill(_total, $"{total / 3600}h {total % 3600 / 60:00}m");
            if (_gorea >= 0) strings.Fill(_gorea, $"{gorea % 3600 / 60}m {gorea % 60:00}s");
            strings.Fill(_percent, $"{Math.Clamp(r.CompletePercent, 0, 100)}%");
            strings.Fill(_deaths, r.Deaths.ToString());
            strings.Fill(_defeated, r.Defeated.ToString());
        }
    }
}
