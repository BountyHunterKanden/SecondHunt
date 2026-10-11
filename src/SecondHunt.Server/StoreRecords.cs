using System;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // T3 (server 0.7.0): a pair of players is friends OR rivals, never both (vanilla); head to head per pair.
    public sealed partial class Store
    {
        // the one-time move to "friend OR rival" (and a safety net every start): rival links and rival requests between two
        // friends go (the friendship is kept: it's the closer link). Returns how many rows went.
        public int SeparateFriendsAndRivals()
        {
            lock (_lock)
            {
                using var cmd = Command(@"DELETE FROM links WHERE kind IN ($r, $q) AND EXISTS
                        (SELECT 1 FROM links f WHERE f.kind = $f AND ((f.a = links.a AND f.b = links.b) OR (f.a = links.b AND f.b = links.a)))",
                    new (string, object?)[] { ("$r", LinkKind.Rival), ("$q", "req:" + LinkKind.Rival), ("$f", LinkKind.Friend) });
                return cmd.ExecuteNonQuery();
            }
        }

        // a finished match between a and b (places: 0 = first, equal = a tie): both sides' records move on together
        public void AddHeadToHead(string a, string b, int placeA, int placeB)
        {
            lock (_lock)
            {
                long now = Now;
                void One(string x, string y, int px, int py)
                {
                    string last = px < py ? HeadToHeadInfo.Victory : px > py ? HeadToHeadInfo.Defeat : HeadToHeadInfo.Tie;
                    int won = px < py ? 1 : 0;
                    Exec(@"INSERT INTO records(a, b, rounds, victories, last, at) VALUES($a, $b, 1, $w, $l, $n)
                           ON CONFLICT(a, b) DO UPDATE SET rounds = rounds + 1, victories = victories + $w, last = $l, at = $n",
                        ("$a", x), ("$b", y), ("$w", won), ("$l", last), ("$n", now));
                }
                One(a, b, placeA, placeB);
                One(b, a, placeB, placeA);
            }
        }

        // the viewer's record against other (null = they never finished a match together)
        public HeadToHeadInfo? HeadToHead(string viewer, string other)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT rounds, victories, last FROM records WHERE a = $a AND b = $b",
                    new (string, object?)[] { ("$a", viewer), ("$b", other) });
                using var r = cmd.ExecuteReader();
                return r.Read() ? new HeadToHeadInfo { Rounds = r.GetInt32(0), Victories = r.GetInt32(1), Last = r.GetString(2) } : null;
            }
        }
    }
}
