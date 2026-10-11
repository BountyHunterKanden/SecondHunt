using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // The server's storage: one SQLite file. Players (key, friend code, name#tag, findable, license) and links between
    // them: "friend" / "rival" rows exist in both directions (a pair is friends OR rivals, never both: server 0.7.0),
    // "req:friend" / "req:rival" from the asker to the asked, "block" from the blocker; head-to-head records per pair
    // (StoreRecords.cs). One connection behind a lock: every call is a few small queries.
    public sealed partial class Store : IDisposable
    {
        private readonly SqliteConnection _db;
        private readonly object _lock = new();

        public sealed record Player(string Code, byte[] Key, string Name, int Tag, bool Findable, string? License);

        public Store(string path)
        {
            _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            _db.Open();
            Exec("PRAGMA journal_mode=WAL;");
            Exec(@"CREATE TABLE IF NOT EXISTS players(
                     code TEXT PRIMARY KEY, pubkey BLOB NOT NULL UNIQUE, name TEXT NOT NULL, name_key TEXT NOT NULL,
                     tag INTEGER NOT NULL, findable INTEGER NOT NULL DEFAULT 1, license TEXT,
                     created INTEGER NOT NULL, seen INTEGER NOT NULL);
                   CREATE UNIQUE INDEX IF NOT EXISTS players_name ON players(name_key, tag);
                   CREATE TABLE IF NOT EXISTS links(
                     a TEXT NOT NULL, b TEXT NOT NULL, kind TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(a, b, kind));
                   CREATE INDEX IF NOT EXISTS links_b ON links(b, kind);
                   CREATE TABLE IF NOT EXISTS recent(
                     a TEXT NOT NULL, b TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(a, b));
                   CREATE TABLE IF NOT EXISTS reports(
                     reporter TEXT NOT NULL, target TEXT NOT NULL, reason TEXT NOT NULL, target_name TEXT NOT NULL,
                     at INTEGER NOT NULL);
                   CREATE INDEX IF NOT EXISTS reports_target ON reports(target, at);
                   CREATE TABLE IF NOT EXISTS turn_usage(month TEXT PRIMARY KEY, bytes INTEGER NOT NULL);
                   CREATE TABLE IF NOT EXISTS safety(
                     code TEXT PRIMARY KEY, matches INTEGER NOT NULL DEFAULT 0, chat_until INTEGER NOT NULL DEFAULT 0);
                   CREATE TABLE IF NOT EXISTS bans(
                     pubkey BLOB PRIMARY KEY, code TEXT NOT NULL, name TEXT NOT NULL, reason TEXT NOT NULL,
                     at INTEGER NOT NULL, until INTEGER NOT NULL);
                   CREATE TABLE IF NOT EXISTS ban_devices(
                     code TEXT NOT NULL, device TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(code, device));
                   CREATE INDEX IF NOT EXISTS ban_devices_device ON ban_devices(device);
                   CREATE TABLE IF NOT EXISTS records(
                     a TEXT NOT NULL, b TEXT NOT NULL, rounds INTEGER NOT NULL, victories INTEGER NOT NULL, last TEXT NOT NULL,
                     at INTEGER NOT NULL, PRIMARY KEY(a, b));");
            // reports grew in 0.5.0: what the reported player said (typed chat is kept only here), and the owner's handling
            AddColumn("reports", "evidence", "TEXT");
            AddColumn("reports", "handled", "INTEGER NOT NULL DEFAULT 0");
            AddColumn("reports", "note", "TEXT");
            AddColumn("reports", "handled_at", "INTEGER NOT NULL DEFAULT 0");
            int merged = SeparateFriendsAndRivals();
            if (merged > 0)
            {
                Console.WriteLine($"store: {merged} rival link(s) and request(s) between friends removed (friend OR rival, 0.7.0)");
            }
        }

        private void AddColumn(string table, string column, string type)
        {
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = '{column}'";
                if (cmd.ExecuteScalar() != null)
                {
                    return;
                }
            }
            Exec($"ALTER TABLE {table} ADD COLUMN {column} {type}");
        }

        public const int MaxRecent = 50;

        public void Dispose() => _db.Dispose();

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private void Exec(string sql, params (string, object?)[] args)
        {
            using var cmd = Command(sql, args);
            cmd.ExecuteNonQuery();
        }

        private SqliteCommand Command(string sql, (string, object?)[] args)
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach ((string name, object? value) in args)
            {
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
            return cmd;
        }

        private List<Player> Players(string sql, params (string, object?)[] args)
        {
            var list = new List<Player>();
            using var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Player(r.GetString(0), (byte[])r[1], r.GetString(2), r.GetInt32(3), r.GetInt64(4) != 0,
                    r.IsDBNull(5) ? null : r.GetString(5)));
            }
            return list;
        }

        private const string PlayerColumns = "code, pubkey, name, tag, findable, license";

        public Player? Get(string code)
        {
            lock (_lock)
            {
                var list = Players($"SELECT {PlayerColumns} FROM players WHERE code = $c", ("$c", code));
                return list.Count > 0 ? list[0] : null;
            }
        }

        // log in: the player for this key, made now on the first connect (the friend code comes from the key)
        public Player Login(byte[] key, string name)
        {
            lock (_lock)
            {
                var found = Players($"SELECT {PlayerColumns} FROM players WHERE pubkey = $k", ("$k", key));
                if (found.Count > 0)
                {
                    Exec("UPDATE players SET seen = $n WHERE code = $c", ("$n", Now), ("$c", found[0].Code));
                    return name == found[0].Name ? found[0] : SetNameLocked(found[0], name);
                }
                string code = FriendCodes.FromKey(key);
                if (Players($"SELECT {PlayerColumns} FROM players WHERE code = $c", ("$c", code)).Count > 0)
                {
                    // a different key with the same 11 digits (about 1 in 10^11 per pair): the game makes a new key
                    throw new OnlineException("code_taken", "this friend code belongs to another player");
                }
                string clean = CleanName(name);
                int tag = PickTag(OnlineNames.Key(clean), prefer: null);
                Exec(@"INSERT INTO players(code, pubkey, name, name_key, tag, findable, created, seen)
                       VALUES($c, $k, $name, $key, $tag, 1, $n, $n)",
                    ("$c", code), ("$k", key), ("$name", clean), ("$key", OnlineNames.Key(clean)), ("$tag", tag), ("$n", Now));
                return new Player(code, key, clean, tag, true, null);
            }
        }

        public Player SetName(string code, string name)
        {
            lock (_lock)
            {
                var me = Players($"SELECT {PlayerColumns} FROM players WHERE code = $c", ("$c", code))[0];
                return SetNameLocked(me, name);
            }
        }

        private Player SetNameLocked(Player me, string name)
        {
            string clean = CleanName(name);
            string key = OnlineNames.Key(clean);
            // the same name in other letter case keeps its tag; a new name keeps the old tag if it's free there
            int tag = key == OnlineNames.Key(me.Name) ? me.Tag : PickTag(key, prefer: me.Tag);
            Exec("UPDATE players SET name = $name, name_key = $key, tag = $tag WHERE code = $c",
                ("$name", clean), ("$key", key), ("$tag", tag), ("$c", me.Code));
            return me with { Name = clean, Tag = tag };
        }

        private static string CleanName(string name)
        {
            string clean = OnlineNames.Clean(name);
            if (clean.Length == 0 || !WordFilter.Allowed(clean))
            {
                clean = OnlineNames.Default;
            }
            return clean;
        }

        private int PickTag(string nameKey, int? prefer)
        {
            bool Free(int tag)
            {
                using var cmd = Command("SELECT 1 FROM players WHERE name_key = $k AND tag = $t",
                    new (string, object?)[] { ("$k", nameKey), ("$t", tag) });
                return cmd.ExecuteScalar() == null;
            }
            if (prefer is int p && Free(p))
            {
                return p;
            }
            for (int i = 0; i < 50; i++)
            {
                int tag = RandomNumberGenerator.GetInt32(1, OnlineNames.MaxTag + 1);
                if (Free(tag))
                {
                    return tag;
                }
            }
            for (int tag = 1; tag <= OnlineNames.MaxTag; tag++)
            {
                if (Free(tag))
                {
                    return tag;
                }
            }
            throw new OnlineException("name_full", "too many players have this name: pick another");
        }

        public void SetFindable(string code, bool findable)
        {
            lock (_lock)
            {
                Exec("UPDATE players SET findable = $f WHERE code = $c", ("$f", findable ? 1 : 0), ("$c", code));
            }
        }

        public void SetLicense(string code, string license)
        {
            if (license.Length > OnlineProtocol.MaxLicense)
            {
                throw new OnlineException("too_big", "license too big");
            }
            try
            {
                using var doc = JsonDocument.Parse(license);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new OnlineException("bad_license", "license must be a JSON object");
                }
            }
            catch (JsonException)
            {
                throw new OnlineException("bad_license", "license must be JSON");
            }
            lock (_lock)
            {
                Exec("UPDATE players SET license = $l WHERE code = $c", ("$l", license), ("$c", code));
            }
        }

        // search: a friend code finds that player always (like vanilla); a name finds findable players only
        public List<Player> Search(string query, int limit = 20)
        {
            string? code = FriendCodes.Parse(query);
            lock (_lock)
            {
                if (code != null)
                {
                    return Players($"SELECT {PlayerColumns} FROM players WHERE code = $c", ("$c", code));
                }
                (string name, int? tag) = OnlineNames.ParseQuery(query);
                string key = OnlineNames.Key(name);
                if (key.Length == 0)
                {
                    return new List<Player>();
                }
                return tag is int t
                    ? Players($"SELECT {PlayerColumns} FROM players WHERE name_key = $k AND tag = $t AND findable = 1",
                        ("$k", key), ("$t", t))
                    : Players($"SELECT {PlayerColumns} FROM players WHERE name_key = $k AND findable = 1 ORDER BY seen DESC LIMIT $l",
                        ("$k", key), ("$l", limit));
            }
        }

        public bool HasLink(string a, string b, string kind)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT 1 FROM links WHERE a = $a AND b = $b AND kind = $k",
                    new (string, object?)[] { ("$a", a), ("$b", b), ("$k", kind) });
                return cmd.ExecuteScalar() != null;
            }
        }

        public bool Blocked(string a, string b) => HasLink(a, b, LinkKind.Block) || HasLink(b, a, LinkKind.Block);

        public void AddLink(string a, string b, string kind)
        {
            lock (_lock)
            {
                Exec("INSERT OR IGNORE INTO links(a, b, kind, at) VALUES($a, $b, $k, $n)",
                    ("$a", a), ("$b", b), ("$k", kind), ("$n", Now));
            }
        }

        // true if there was such a link
        public bool RemoveLink(string a, string b, string kind)
        {
            lock (_lock)
            {
                using var cmd = Command("DELETE FROM links WHERE a = $a AND b = $b AND kind = $k",
                    new (string, object?)[] { ("$a", a), ("$b", b), ("$k", kind) });
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public int CountLinks(string a, string kind)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT COUNT(*) FROM links WHERE a = $a AND kind = $k",
                    new (string, object?)[] { ("$a", a), ("$k", kind) });
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        // a match started with these players: each one's recent list gets the others (newest MaxRecent kept)
        public void AddRecent(IReadOnlyList<string> codes)
        {
            lock (_lock)
            {
                long now = Now;
                foreach (string a in codes)
                {
                    foreach (string b in codes)
                    {
                        if (a != b)
                        {
                            Exec(@"INSERT INTO recent(a, b, at) VALUES($a, $b, $n)
                                   ON CONFLICT(a, b) DO UPDATE SET at = excluded.at",
                                ("$a", a), ("$b", b), ("$n", now));
                        }
                    }
                    Exec(@"DELETE FROM recent WHERE a = $a AND b NOT IN
                             (SELECT b FROM recent WHERE a = $a ORDER BY at DESC, rowid DESC LIMIT $l)",
                        ("$a", a), ("$l", MaxRecent));
                }
            }
        }

        // a report for the owner's review (sqlite3 secondhunt.db "SELECT * FROM reports ORDER BY at DESC"): who, why and the
        // name they had then; the same reporter, player and reason again within a day isn't kept twice. False = a repeat.
        public const int MaxReportsPerDay = 10;

        public bool AddReport(string reporter, Player target, string reason, string? evidence = null)
        {
            lock (_lock)
            {
                long now = Now;
                using (var count = Command("SELECT COUNT(*) FROM reports WHERE reporter = $r AND at > $since",
                    new (string, object?)[] { ("$r", reporter), ("$since", now - 86400) }))
                {
                    if (Convert.ToInt64(count.ExecuteScalar()) >= MaxReportsPerDay)
                    {
                        throw new OnlineException("too_many_reports", "you've sent a lot of reports today: the team will read them");
                    }
                }
                using var check = Command("SELECT COUNT(*) FROM reports WHERE reporter = $r AND target = $t AND reason = $w AND at > $since",
                    new (string, object?)[] { ("$r", reporter), ("$t", target.Code), ("$w", reason), ("$since", now - 86400) });
                if (Convert.ToInt64(check.ExecuteScalar()) > 0)
                {
                    return false;
                }
                Exec("INSERT INTO reports(reporter, target, reason, target_name, at, evidence) VALUES($r, $t, $w, $n, $a, $e)",
                    ("$r", reporter), ("$t", target.Code), ("$w", reason), ("$n", OnlineNames.Display(target.Name, target.Tag)), ("$a", now),
                    ("$e", evidence));
                return true;
            }
        }

        public sealed record ReportRow(long Id, string Reporter, string Target, string TargetName, string Reason, long At,
            string? Evidence, bool Handled, string? Note);

        private List<ReportRow> ReportRows(string where, params (string, object?)[] args)
        {
            var list = new List<ReportRow>();
            using var cmd = Command("SELECT rowid, reporter, target, target_name, reason, at, evidence, handled, note FROM reports "
                + where, args);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new ReportRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5),
                    r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7) != 0, r.IsDBNull(8) ? null : r.GetString(8)));
            }
            return list;
        }

        // the owner's queue: unhandled reports (or all), newest first
        public List<ReportRow> Reports(bool includeHandled, int limit = 50)
        {
            lock (_lock)
            {
                return ReportRows((includeHandled ? "" : "WHERE handled = 0 ") + "ORDER BY at DESC LIMIT $l", ("$l", limit));
            }
        }

        public ReportRow? Report(long id)
        {
            lock (_lock)
            {
                var rows = ReportRows("WHERE rowid = $i", ("$i", id));
                return rows.Count > 0 ? rows[0] : null;
            }
        }

        public List<ReportRow> ReportsAgainst(string target)
        {
            lock (_lock)
            {
                return ReportRows("WHERE target = $t ORDER BY at DESC", ("$t", target));
            }
        }

        // the owner dealt with a report; its chat evidence goes at once unless it was kept for a ban (keepEvidence)
        public bool HandleReport(long id, string? note, bool keepEvidence)
        {
            lock (_lock)
            {
                using var cmd = Command("UPDATE reports SET handled = 1, note = $n, handled_at = $a" + (keepEvidence ? "" : ", evidence = NULL")
                    + " WHERE rowid = $i", new (string, object?)[] { ("$n", note), ("$a", Now), ("$i", id) });
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        // privacy: handled reports are kept 30 days (a ban's reason), unhandled reports 90 days, then the whole row goes.
        // Returns how many rows went.
        public const int HandledKeepDays = 30, OpenKeepDays = 90;

        public int PurgeReports()
        {
            lock (_lock)
            {
                long now = Now;
                // a ban that ended takes its device IDs with it (privacy: kept only while the ban holds)
                Exec("DELETE FROM ban_devices WHERE code NOT IN (SELECT code FROM bans WHERE until = 0 OR until > $n)", ("$n", now));
                using var cmd = Command("DELETE FROM reports WHERE (handled = 1 AND handled_at < $h) OR (handled = 0 AND at < $o)",
                    new (string, object?)[] { ("$h", now - HandledKeepDays * 86400L), ("$o", now - OpenKeepDays * 86400L) });
                return cmd.ExecuteNonQuery();
            }
        }

        // ---- safety: matches played (typed chat unlocks after a few), owner chat mutes, bans by key ----

        public int Matches(string code)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT matches FROM safety WHERE code = $c", new (string, object?)[] { ("$c", code) });
                return cmd.ExecuteScalar() is object o ? Convert.ToInt32(o) : 0;
            }
        }

        public void AddMatches(IEnumerable<string> codes)
        {
            lock (_lock)
            {
                foreach (string c in codes)
                {
                    Exec("INSERT INTO safety(code, matches) VALUES($c, 1) ON CONFLICT(code) DO UPDATE SET matches = matches + 1", ("$c", c));
                }
            }
        }

        public void SetMatches(string code, int matches)
        {
            lock (_lock)
            {
                Exec("INSERT INTO safety(code, matches) VALUES($c, $m) ON CONFLICT(code) DO UPDATE SET matches = $m", ("$c", code), ("$m", matches));
            }
        }

        // unix seconds the owner's chat mute ends (0 = none)
        public long ChatUntil(string code)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT chat_until FROM safety WHERE code = $c", new (string, object?)[] { ("$c", code) });
                return cmd.ExecuteScalar() is object o ? Convert.ToInt64(o) : 0;
            }
        }

        public void SetChatUntil(string code, long until)
        {
            lock (_lock)
            {
                Exec("INSERT INTO safety(code, chat_until) VALUES($c, $u) ON CONFLICT(code) DO UPDATE SET chat_until = $u", ("$c", code), ("$u", until));
            }
        }

        public sealed record BanRow(byte[] Key, string Code, string Name, string Reason, long At, long Until)
        {
            public bool Active(long now) => Until == 0 || Until > now;
        }

        public void Ban(Player p, string reason, long until)
        {
            lock (_lock)
            {
                Exec(@"INSERT INTO bans(pubkey, code, name, reason, at, until) VALUES($k, $c, $n, $r, $a, $u)
                       ON CONFLICT(pubkey) DO UPDATE SET reason = $r, at = $a, until = $u, name = $n",
                    ("$k", p.Key), ("$c", p.Code), ("$n", OnlineNames.Display(p.Name, p.Tag)), ("$r", reason), ("$a", Now), ("$u", until));
            }
        }

        public bool Unban(string code)
        {
            lock (_lock)
            {
                Exec("DELETE FROM ban_devices WHERE code = $c", ("$c", code));
                using var cmd = Command("DELETE FROM bans WHERE code = $c", new (string, object?)[] { ("$c", code) });
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        // device bans: the scrambled device IDs (HelloMsg.Device) a banned profile used, recorded when it logs in or is
        // kicked; a new key from one of them is refused while that ban holds. Kept only for banned profiles.
        public void AddBanDevice(string code, string device)
        {
            if (device.Length == 0)
            {
                return;
            }
            lock (_lock)
            {
                Exec("INSERT OR IGNORE INTO ban_devices(code, device, at) VALUES($c, $d, $a)", ("$c", code), ("$d", device), ("$a", Now));
            }
        }

        // the ban that still holds on this device, or null
        public BanRow? ActiveDeviceBan(string device)
        {
            if (device.Length == 0)
            {
                return null;
            }
            lock (_lock)
            {
                long now = Now;
                return BanRows("WHERE code IN (SELECT code FROM ban_devices WHERE device = $d) ORDER BY until = 0 DESC, until DESC",
                    ("$d", device)).FirstOrDefault(b => b.Active(now));
            }
        }

        public int BanDeviceCount(string code)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT COUNT(*) FROM ban_devices WHERE code = $c", new (string, object?)[] { ("$c", code) });
                return (int)Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        // the ban on this key that still holds, or null
        public BanRow? ActiveBan(byte[] key)
        {
            lock (_lock)
            {
                var rows = BanRows("WHERE pubkey = $k", ("$k", key));
                return rows.Count > 0 && rows[0].Active(Now) ? rows[0] : null;
            }
        }

        public List<BanRow> Bans(bool onlyActive = true)
        {
            lock (_lock)
            {
                long now = Now;
                return BanRows("ORDER BY at DESC").Where(b => !onlyActive || b.Active(now)).ToList();
            }
        }

        private List<BanRow> BanRows(string where, params (string, object?)[] args)
        {
            var list = new List<BanRow>();
            using var cmd = Command("SELECT pubkey, code, name, reason, at, until FROM bans " + where, args);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new BanRow((byte[])r[0], r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5)));
            }
            return list;
        }

        // admin lookups: a friend code, or NAME#0421 (findable or not); a bare name that several players have is null
        public Player? Find(string who)
        {
            string? code = FriendCodes.Parse(who);
            lock (_lock)
            {
                if (code != null)
                {
                    return Get(code);
                }
                (string name, int? tag) = OnlineNames.ParseQuery(who);
                string key = OnlineNames.Key(name);
                if (key.Length == 0)
                {
                    return null;
                }
                var found = tag is int t
                    ? Players($"SELECT {PlayerColumns} FROM players WHERE name_key = $k AND tag = $t", ("$k", key), ("$t", t))
                    : Players($"SELECT {PlayerColumns} FROM players WHERE name_key = $k ORDER BY seen DESC LIMIT 2", ("$k", key));
                return found.Count == 1 ? found[0] : null;
            }
        }

        // a longer word list (or an owner addition) can make a name already in the database a match: those players get the
        // default name (their tag follows the usual rules). Returns how many.
        public int ReplaceBadNames()
        {
            lock (_lock)
            {
                var bad = Players($"SELECT {PlayerColumns} FROM players").Where(p => !WordFilter.Allowed(p.Name)).ToList();
                foreach (Player p in bad)
                {
                    SetNameLocked(p, OnlineNames.Default);
                }
                return bad.Count;
            }
        }

        public long PlayerCount()
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT COUNT(*) FROM players", Array.Empty<(string, object?)>());
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        // Cloudflare TURN bytes counted this UTC month ("2026-10"), kept across restarts for the monthly cap
        public long TurnBytes(string month)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT bytes FROM turn_usage WHERE month = $m", new (string, object?)[] { ("$m", month) });
                return cmd.ExecuteScalar() is object o ? Convert.ToInt64(o) : 0;
            }
        }

        public long AddTurnBytes(string month, long bytes)
        {
            lock (_lock)
            {
                Exec("INSERT INTO turn_usage(month, bytes) VALUES($m, $b) ON CONFLICT(month) DO UPDATE SET bytes = bytes + $b",
                    ("$m", month), ("$b", bytes));
                return TurnBytes(month);
            }
        }

        // the player and everything that links to it (delete_profile); reports stay
        public void DeletePlayer(string code)
        {
            lock (_lock)
            {
                Exec("DELETE FROM links WHERE a = $c OR b = $c", ("$c", code));
                Exec("DELETE FROM recent WHERE a = $c OR b = $c", ("$c", code));
                Exec("DELETE FROM records WHERE a = $c OR b = $c", ("$c", code));
                Exec("DELETE FROM players WHERE code = $c", ("$c", code));
            }
        }

        public int ReportCount(string target)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT COUNT(*) FROM reports WHERE target = $t", new (string, object?)[] { ("$t", target) });
                return (int)Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        // the players this one played with, newest first
        public List<string> Recent(string code)
        {
            lock (_lock)
            {
                var list = new List<string>();
                using var cmd = Command("SELECT b FROM recent WHERE a = $c ORDER BY at DESC, rowid DESC",
                    new (string, object?)[] { ("$c", code) });
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(r.GetString(0));
                }
                return list;
            }
        }

        // codes this player links to (outgoing rows of a kind), or that link to them (incoming)
        public List<string> Linked(string code, string kind, bool incoming = false)
        {
            lock (_lock)
            {
                var list = new List<string>();
                using var cmd = Command(incoming ? "SELECT a FROM links WHERE b = $c AND kind = $k ORDER BY at"
                    : "SELECT b FROM links WHERE a = $c AND kind = $k ORDER BY at",
                    new (string, object?)[] { ("$c", code), ("$k", kind) });
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(r.GetString(0));
                }
                return list;
            }
        }
    }
}
