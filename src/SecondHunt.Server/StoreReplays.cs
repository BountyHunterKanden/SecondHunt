using System;
using System.Collections.Generic;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Shared replays (server 0.9.0, HubReplays.cs): a shared replay's description (replay_mail; Uploaded once its file is in
    // the replays folder), who it waits for (replay_inbox: a row per friend until they download or delete it) and who refuses
    // shared replays (replay_prefs).
    public sealed partial class Store
    {
        public sealed record ReplayMailRow(string Id, string Sender, string Title, string Mode, string Arena, int Seconds, long Bytes,
            long Recorded, long At, bool Uploaded);

        private void CreateReplayTables()
        {
            Exec(@"CREATE TABLE IF NOT EXISTS replay_mail(
                     id TEXT PRIMARY KEY, sender TEXT NOT NULL, title TEXT NOT NULL, mode TEXT NOT NULL, arena TEXT NOT NULL,
                     seconds INTEGER NOT NULL, bytes INTEGER NOT NULL, recorded INTEGER NOT NULL, at INTEGER NOT NULL,
                     uploaded INTEGER NOT NULL DEFAULT 0);
                   CREATE INDEX IF NOT EXISTS replay_mail_sender ON replay_mail(sender, at);
                   CREATE TABLE IF NOT EXISTS replay_inbox(id TEXT NOT NULL, recipient TEXT NOT NULL, PRIMARY KEY(id, recipient));
                   CREATE INDEX IF NOT EXISTS replay_inbox_recipient ON replay_inbox(recipient);
                   CREATE TABLE IF NOT EXISTS replay_prefs(code TEXT PRIMARY KEY, block INTEGER NOT NULL);");
        }

        private const string ReplayColumns = "id, sender, title, mode, arena, seconds, bytes, recorded, at, uploaded";

        private List<ReplayMailRow> ReplayRows(string sql, params (string, object?)[] args)
        {
            var list = new List<ReplayMailRow>();
            using var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new ReplayMailRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5),
                    r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9) != 0));
            }
            return list;
        }

        public void AddReplayMail(ReplayMailRow m, IEnumerable<string> recipients)
        {
            lock (_lock)
            {
                using var tx = _db.BeginTransaction();
                Exec($"INSERT INTO replay_mail({ReplayColumns}) VALUES($i, $s, $t, $m, $a, $sec, $b, $r, $at, 0)",
                    ("$i", m.Id), ("$s", m.Sender), ("$t", m.Title), ("$m", m.Mode), ("$a", m.Arena), ("$sec", m.Seconds), ("$b", m.Bytes),
                    ("$r", m.Recorded), ("$at", m.At));
                foreach (string c in recipients)
                {
                    Exec("INSERT OR IGNORE INTO replay_inbox(id, recipient) VALUES($i, $c)", ("$i", m.Id), ("$c", c));
                }
                tx.Commit();
            }
        }

        public void MarkReplayUploaded(string id, long bytes)
        {
            lock (_lock)
            {
                Exec("UPDATE replay_mail SET uploaded = 1, bytes = $b WHERE id = $i", ("$i", id), ("$b", bytes));
            }
        }

        public ReplayMailRow? ReplayMail(string id)
        {
            lock (_lock)
            {
                List<ReplayMailRow> rows = ReplayRows($"SELECT {ReplayColumns} FROM replay_mail WHERE id = $i", ("$i", id));
                return rows.Count > 0 ? rows[0] : null;
            }
        }

        // the uploaded shared replays waiting for `code`, oldest first
        public List<ReplayMailRow> ReplaysWaiting(string code)
        {
            lock (_lock)
            {
                return ReplayRows($@"SELECT {ReplayColumns.Replace("id,", "m.id,")} FROM replay_mail m JOIN replay_inbox i ON i.id = m.id
                    WHERE i.recipient = $c AND m.uploaded = 1 ORDER BY m.at", ("$c", code));
            }
        }

        public List<string> ReplayRecipients(string id)
        {
            lock (_lock)
            {
                var list = new List<string>();
                using var cmd = Command("SELECT recipient FROM replay_inbox WHERE id = $i", new (string, object?)[] { ("$i", id) });
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add(r.GetString(0));
                return list;
            }
        }

        // shared replays waiting for `code` (uploaded or on the way)
        public int ReplaysQueued(string code)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT COUNT(*) FROM replay_inbox WHERE recipient = $c", new (string, object?)[] { ("$c", code) });
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public bool ReplayWaitsFor(string id, string code)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT 1 FROM replay_inbox WHERE id = $i AND recipient = $c",
                    new (string, object?)[] { ("$i", id), ("$c", code) });
                return cmd.ExecuteScalar() != null;
            }
        }

        // off `code`'s queue; true = nobody else waits for it any more (the caller deletes it)
        public bool DropReplay(string id, string code)
        {
            lock (_lock)
            {
                Exec("DELETE FROM replay_inbox WHERE id = $i AND recipient = $c", ("$i", id), ("$c", code));
                using var cmd = Command("SELECT 1 FROM replay_inbox WHERE id = $i", new (string, object?)[] { ("$i", id) });
                return cmd.ExecuteScalar() == null;
            }
        }

        public void DeleteReplayMail(string id)
        {
            lock (_lock)
            {
                Exec("DELETE FROM replay_inbox WHERE id = $i", ("$i", id));
                Exec("DELETE FROM replay_mail WHERE id = $i", ("$i", id));
            }
        }

        // the ones to delete: older than `before`, never uploaded and older than `uploadBefore`, or waiting for nobody
        public List<string> StaleReplays(long before, long uploadBefore)
        {
            lock (_lock)
            {
                var list = new List<string>();
                using var cmd = Command(@"SELECT id FROM replay_mail m WHERE at < $b OR (uploaded = 0 AND at < $u)
                        OR NOT EXISTS (SELECT 1 FROM replay_inbox i WHERE i.id = m.id)",
                    new (string, object?)[] { ("$b", before), ("$u", uploadBefore) });
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add(r.GetString(0));
                return list;
            }
        }

        public List<string> ReplayIds()
        {
            lock (_lock)
            {
                var list = new List<string>();
                using var cmd = Command("SELECT id FROM replay_mail", Array.Empty<(string, object?)>());
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add(r.GetString(0));
                return list;
            }
        }

        public long ReplayBytes()
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT COALESCE(SUM(bytes), 0) FROM replay_mail", Array.Empty<(string, object?)>());
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        public long ReplayBytesFrom(string sender)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT COALESCE(SUM(bytes), 0) FROM replay_mail WHERE sender = $c",
                    new (string, object?)[] { ("$c", sender) });
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        public int ReplaysSentSince(string code, long since)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT COUNT(*) FROM replay_mail WHERE sender = $c AND at >= $s",
                    new (string, object?)[] { ("$c", code), ("$s", since) });
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public bool BlocksReplays(string code)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT block FROM replay_prefs WHERE code = $c", new (string, object?)[] { ("$c", code) });
                return cmd.ExecuteScalar() is long b && b != 0;
            }
        }

        public void SetBlocksReplays(string code, bool block)
        {
            lock (_lock)
            {
                Exec("INSERT INTO replay_prefs(code, block) VALUES($c, $b) ON CONFLICT(code) DO UPDATE SET block = $b",
                    ("$c", code), ("$b", block ? 1 : 0));
            }
        }

        // a deleted profile: off every queue, its preference gone (what it sent stays for its friends until it expires)
        public void ForgetReplays(string code)
        {
            lock (_lock)
            {
                Exec("DELETE FROM replay_inbox WHERE recipient = $c", ("$c", code));
                Exec("DELETE FROM replay_prefs WHERE code = $c", ("$c", code));
            }
        }
    }
}
