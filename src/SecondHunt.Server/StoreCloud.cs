using System;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // CLOUD SAVE (server 0.11.0, HubCloud.cs): one sealed profile per player (profiles: the blob, its rev, when it was put,
    // and the put counters behind the rate limit). A delete keeps the row (blob NULL) so the rev only goes up and the put
    // limits still count; delete_profile and the year-unseen purge remove it.
    public sealed partial class Store
    {
        public sealed record CloudRow(long Rev, long At, byte[]? Blob, long LastPut, long PutDay, int PutsToday);

        private void CreateCloudTables()
        {
            Exec(@"CREATE TABLE IF NOT EXISTS profiles(
                     code TEXT PRIMARY KEY, rev INTEGER NOT NULL, blob BLOB, at INTEGER NOT NULL,
                     last_put INTEGER NOT NULL, put_day INTEGER NOT NULL, puts_today INTEGER NOT NULL);");
        }

        public CloudRow? Cloud(string code)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT rev, at, blob, last_put, put_day, puts_today FROM profiles WHERE code = $c",
                    new (string, object?)[] { ("$c", code) });
                using var r = cmd.ExecuteReader();
                return r.Read() ? new CloudRow(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : (byte[])r[2], r.GetInt64(3),
                    r.GetInt64(4), r.GetInt32(5)) : null;
            }
        }

        public enum CloudPut { Stored, Conflict, TooSoon, TooMany }

        // compare-and-swap on rev, with the rate limit checked and counted in the same lock; rev = the stored or current one
        public CloudPut PutCloud(string code, long baseRev, byte[] blob, out long rev)
        {
            lock (_lock)
            {
                long now = Now, day = now / 86400;
                CloudRow? row = Cloud(code);
                rev = row?.Rev ?? 0;
                if (row != null && now - row.LastPut < CloudSaveRules.PutGapSeconds) return CloudPut.TooSoon;
                int today = row != null && row.PutDay == day ? row.PutsToday : 0;
                if (today >= CloudSaveRules.PutsPerDay) return CloudPut.TooMany;
                if (baseRev != rev) return CloudPut.Conflict;
                rev++;
                Exec(@"INSERT INTO profiles(code, rev, blob, at, last_put, put_day, puts_today) VALUES($c, $r, $b, $n, $n, $d, $t)
                       ON CONFLICT(code) DO UPDATE SET rev = $r, blob = $b, at = $n, last_put = $n, put_day = $d, puts_today = $t",
                    ("$c", code), ("$r", rev), ("$b", blob), ("$n", now), ("$d", day), ("$t", today + 1));
                return CloudPut.Stored;
            }
        }

        public bool DropCloud(string code)
        {
            lock (_lock)
            {
                using var cmd = Command("UPDATE profiles SET blob = NULL WHERE code = $c AND blob IS NOT NULL",
                    new (string, object?)[] { ("$c", code) });
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        // copies of players gone, or not seen for KeepDays; how many went
        public int PurgeCloud()
        {
            lock (_lock)
            {
                using var cmd = Command(@"DELETE FROM profiles WHERE code NOT IN (SELECT code FROM players)
                                            OR code IN (SELECT code FROM players WHERE seen < $t)",
                    new (string, object?)[] { ("$t", Now - CloudSaveRules.KeepDays * 86400L) });
                return cmd.ExecuteNonQuery();
            }
        }

        // whether a ban holds on this profile now (a banned player is kicked within 15 s, SafetyTick: this closes the gap)
        public bool Banned(string code)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT until FROM bans WHERE code = $c", new (string, object?)[] { ("$c", code) });
                return cmd.ExecuteScalar() is object o && (Convert.ToInt64(o) == 0 || Convert.ToInt64(o) > Now);
            }
        }

        // selftest only: move the last put and the player's last login back in time
        internal void AgeCloud(string code, long putSeconds, long seenSeconds = 0)
        {
            lock (_lock)
            {
                Exec("UPDATE profiles SET last_put = last_put - $s WHERE code = $c", ("$c", code), ("$s", putSeconds));
                Exec("UPDATE players SET seen = seen - $s WHERE code = $c", ("$c", code), ("$s", seenSeconds));
            }
        }
    }
}
