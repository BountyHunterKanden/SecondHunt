using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // The owner's admin tool (docs/ONLINE_PLAY.md section 8, Social O7): the server binary itself, run on the VPS by the same
    // user as the service (src/SecondHunt.Server/deploy/admin.ps1 does that over ssh from the owner's PC):
    //   SecondHuntServer admin [--db secondhunt.db] [--words words.txt] <command> ...
    // It works on the database file, so nothing is exposed on the network. A new ban kicks the player within 15 s (the
    // running server checks every 15 s); a new chat mute or a word-list edit is used at once / within 20 s.
    public static class Admin
    {
        public static int Run(string[] args, TextWriter output)
        {
            string db = "secondhunt.db";
            string? words = null;
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--db" && i + 1 < args.Length)
                {
                    db = args[++i];
                }
                else if (args[i] == "--words" && i + 1 < args.Length)
                {
                    words = args[++i];
                }
                else
                {
                    rest.Add(args[i]);
                }
            }
            if (rest.Count == 0 || rest[0] is "help" or "--help")
            {
                output.WriteLine(Help);
                return 0;
            }
            if (rest[0] == "drain")
            {
                return Drain(db, rest, output);
            }
            WordFilter.Load(words ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db)) ?? ".", "words.txt"));
            using var store = new Store(db);
            try
            {
                return Command(store, rest, output);
            }
            catch (OnlineException ex)
            {
                output.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        public const string Help = @"Second Hunt admin (run it as the service's user, on the server):
  reports [all]               the report queue, newest first (all = handled ones too)
  show <id>                   one report with what the player said, and the reports against the same player
  dismiss <id> [note]         nothing to act on: the report's chat text is erased now
  resolve <id> [note]         acted on it (kept 30 days with its text, then erased)
  player <who>                a player: name, code, matches played, reports, ban, chat mute
  ban <who> <days|perm> <reason...>   ban the player's key and the devices it used (they are kicked within 15 s); open reports against them are resolved
  unban <who>                 lift a ban
  bans                        the bans that hold now
  mute <who> <hours|perm>     switch typed chat off for a player (quick messages still work)
  unmute <who>                chat back on
  words <text>                what the word filter does to this text
  stats                       players, open reports, bans
  drain [on|off]              before a server update: while on, no new served games (new games are player-hosted, the
                              running ones play on); restart once the health line says served 0/..
<who> is a friend code (12 digits) or NAME#0421.";

        // the drain file: next to the database, so the running server sees it (Hub.Draining) without a network command
        public static string DrainPath(string db) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db)) ?? ".", "drain");

        private static int Drain(string db, List<string> a, TextWriter o)
        {
            string file = DrainPath(db);
            if (a.Count > 1 && a[1] == "on")
            {
                File.WriteAllText(file, DateTime.UtcNow.ToString("u") + "\n");
            }
            else if (a.Count > 1 && a[1] == "off")
            {
                File.Delete(file);
            }
            else if (a.Count > 1)
            {
                o.WriteLine("usage: drain [on|off]");
                return 1;
            }
            o.WriteLine(File.Exists(file) ? "drain on: no new served games (the running ones play on; see served N/M on /v1/health)"
                : "drain off: new games may be served");
            return 0;
        }

        private static Store.Player Who(Store store, string? who)
        {
            if (who == null)
            {
                throw new OnlineException("usage", "which player? (a friend code or NAME#0421)");
            }
            return store.Find(who) ?? throw new OnlineException("not_found", $"no single player matches '{who}' (use the friend code or NAME#0421)");
        }

        private static string When(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToString("yyyy-MM-dd HH:mm") + " UTC";

        // device bans: the devices a banned profile was seen on (recorded at its kick or its next login try)
        private static string Devices(int n) => n == 0 ? "no device seen yet" : n == 1 ? "1 device" : $"{n} devices";

        private static string Ago(long unix)
        {
            TimeSpan t = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix);
            return t.TotalDays >= 2 ? $"{(int)t.TotalDays} d ago" : t.TotalHours >= 2 ? $"{(int)t.TotalHours} h ago" : $"{Math.Max(0, (int)t.TotalMinutes)} min ago";
        }

        private static string Join(IEnumerable<string> words) => string.Join(" ", words);

        private static int Command(Store store, List<string> a, TextWriter o)
        {
            switch (a[0])
            {
            case "reports":
            {
                var rows = store.Reports(includeHandled: a.Count > 1 && a[1] == "all");
                if (rows.Count == 0)
                {
                    o.WriteLine("no open reports");
                }
                foreach (Store.ReportRow r in rows)
                {
                    int total = store.ReportsAgainst(r.Target).Count;
                    Store.Player? reporter = store.Get(r.Reporter);
                    o.WriteLine($"#{r.Id,-5} {Ago(r.At),-11} {r.Reason,-8} against {r.TargetName} ({r.Target}, {total} report{(total == 1 ? "" : "s")} in all)"
                        + $"  by {(reporter == null ? "(deleted)" : OnlineNames.Display(reporter.Name, reporter.Tag))}"
                        + (r.Evidence != null ? "  [has chat text]" : "") + (r.Handled ? "  (handled" + (r.Note != null ? ": " + r.Note : "") + ")" : ""));
                }
                return 0;
            }
            case "show":
            {
                Store.ReportRow r = (a.Count > 1 && Int64.TryParse(a[1], out long id) ? store.Report(id) : null)
                    ?? throw new OnlineException("not_found", "no such report (show <id>)");
                Store.Player? target = store.Get(r.Target);
                o.WriteLine($"report #{r.Id}: {r.Reason} against {r.TargetName} ({r.Target}), {When(r.At)}");
                o.WriteLine(target == null ? "  the player has deleted their profile" : $"  now called {OnlineNames.Display(target.Name, target.Tag)}, {store.Matches(r.Target)} matches played");
                o.WriteLine(r.Handled ? $"  handled{(r.Note != null ? ": " + r.Note : "")}" : "  open");
                o.WriteLine(r.Evidence ?? "  (no chat text with this report)");
                var others = store.ReportsAgainst(r.Target).Where(x => x.Id != r.Id).ToList();
                if (others.Count > 0)
                {
                    o.WriteLine("other reports against the same player: " + Join(others.Select(x => $"#{x.Id} {x.Reason} {Ago(x.At)}")));
                }
                return 0;
            }
            case "dismiss":
            case "resolve":
            {
                if (a.Count < 2 || !Int64.TryParse(a[1], out long id) || store.Report(id) == null)
                {
                    throw new OnlineException("not_found", $"{a[0]} <id> [note]");
                }
                store.HandleReport(id, a.Count > 2 ? Join(a.Skip(2)) : null, keepEvidence: a[0] == "resolve");
                o.WriteLine(a[0] == "dismiss" ? $"report #{id} dismissed, its chat text erased" : $"report #{id} resolved");
                return 0;
            }
            case "player":
            {
                Store.Player p = Who(store, a.Count > 1 ? a[1] : null);
                o.WriteLine($"{OnlineNames.Display(p.Name, p.Tag)}  code {p.Code}  findable {p.Findable}");
                o.WriteLine($"  matches with others: {store.Matches(p.Code)}");
                long mute = store.ChatUntil(p.Code);
                o.WriteLine(mute > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? "  typed chat OFF until " + (mute == long.MaxValue ? "further notice" : When(mute)) : "  typed chat on");
                Store.BanRow? ban = store.ActiveBan(p.Key);
                o.WriteLine(ban == null ? "  not banned" : $"  BANNED {(ban.Until == 0 ? "for good" : "until " + When(ban.Until))}: {ban.Reason}"
                    + $" ({Devices(store.BanDeviceCount(p.Code))})");
                var reports = store.ReportsAgainst(p.Code);
                o.WriteLine($"  reports against: {reports.Count}" + (reports.Count > 0 ? " (" + Join(reports.Take(8).Select(x => $"#{x.Id} {x.Reason}{(x.Handled ? " handled" : "")}")) + ")" : ""));
                return 0;
            }
            case "ban":
            {
                Store.Player p = Who(store, a.Count > 1 ? a[1] : null);
                if (a.Count < 4)
                {
                    throw new OnlineException("usage", "ban <who> <days|perm> <reason...>");
                }
                long until = 0;
                if (a[2] != "perm")
                {
                    if (!Double.TryParse(a[2], out double days) || days <= 0)
                    {
                        throw new OnlineException("usage", "days must be a number, or perm");
                    }
                    until = DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds();
                }
                string reason = Join(a.Skip(3));
                store.Ban(p, reason.Length > 120 ? reason[..120] : reason, until);
                int closed = 0;
                foreach (Store.ReportRow r in store.ReportsAgainst(p.Code).Where(x => !x.Handled))
                {
                    store.HandleReport(r.Id, "banned: " + reason, keepEvidence: true);
                    closed++;
                }
                o.WriteLine($"banned {OnlineNames.Display(p.Name, p.Tag)} ({p.Code}) {(until == 0 ? "for good" : "until " + When(until))}; {closed} open report(s) resolved; they are kicked within 15 s");
                return 0;
            }
            case "unban":
            {
                Store.Player p = Who(store, a.Count > 1 ? a[1] : null);
                o.WriteLine(store.Unban(p.Code) ? $"unbanned {OnlineNames.Display(p.Name, p.Tag)}" : "they weren't banned");
                return 0;
            }
            case "bans":
            {
                var bans = store.Bans();
                if (bans.Count == 0)
                {
                    o.WriteLine("no bans");
                }
                foreach (Store.BanRow b in bans)
                {
                    o.WriteLine($"{b.Name} ({b.Code}) {(b.Until == 0 ? "for good" : "until " + When(b.Until))}, set {Ago(b.At)}, {Devices(store.BanDeviceCount(b.Code))}: {b.Reason}");
                }
                return 0;
            }
            case "mute":
            {
                Store.Player p = Who(store, a.Count > 1 ? a[1] : null);
                if (a.Count < 3)
                {
                    throw new OnlineException("usage", "mute <who> <hours|perm>");
                }
                long until = long.MaxValue;
                if (a[2] != "perm")
                {
                    if (!Double.TryParse(a[2], out double hours) || hours <= 0)
                    {
                        throw new OnlineException("usage", "hours must be a number, or perm");
                    }
                    until = DateTimeOffset.UtcNow.AddHours(hours).ToUnixTimeSeconds();
                }
                store.SetChatUntil(p.Code, until);
                o.WriteLine($"typed chat is off for {OnlineNames.Display(p.Name, p.Tag)} {(until == long.MaxValue ? "until you unmute" : "until " + When(until))}");
                return 0;
            }
            case "unmute":
            {
                Store.Player p = Who(store, a.Count > 1 ? a[1] : null);
                store.SetChatUntil(p.Code, 0);
                o.WriteLine($"typed chat is on again for {OnlineNames.Display(p.Name, p.Tag)}");
                return 0;
            }
            case "words":
            {
                string text = Join(a.Skip(1));
                string masked = WordFilter.Mask(text, out bool changed);
                o.WriteLine($"{WordFilter.Count} entries loaded; {(changed ? "masked: " + masked : "nothing masked")}; as a name: {(WordFilter.Allowed(text) ? "allowed" : "refused")}");
                return 0;
            }
            case "stats":
                o.WriteLine($"{store.PlayerCount()} players, {store.Reports(false, 1000).Count} open reports, {store.Bans().Count} bans, {WordFilter.Count} filter entries");
                return 0;
            default:
                o.WriteLine($"unknown command '{a[0]}'.\n{Help}");
                return 2;
            }
        }
    }
}
