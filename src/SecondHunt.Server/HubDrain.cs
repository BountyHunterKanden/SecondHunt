using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // The restart drain before a deploy (server 0.10.3; docs/RELEASE.md "Server deploy", IDEAS_SURVEY B "server drain on
    // deploy"). `secondhunt-admin drain restart [cap] [exit]` (or deploy.ps1) writes "restart <unix> <cap> [exit]" to the
    // drain file; the running server then:
    //  - tells everyone online (ServerNoticeEvent to clients that understand it; WelcomeMsg.Restart for new logins),
    //  - ends every Find Game search and refuses new lobbies, searches and joins (error "draining"; a rejoin to your own
    //    started match is still let in), and no new served games (Draining),
    //  - lets the started matches play on until none is left or the cap passes, then reports " drained" on /v1/health
    //    (and with "exit" stops by itself: systemd's Restart=always starts the new binary).
    // Removing the file calls it off. A restart file older than this process is a leftover and is deleted. On SIGTERM
    // every session hears why it is closing (KickedEvent, which every app version understands). All in memory.
    public sealed partial class Hub
    {
        public const string DrainingCode = "draining";

        private sealed class RestartDrain
        {
            public long Requested; // unix seconds (the file's; 0 = started in code)
            public DateTime Deadline;
            public int Cap;
            public bool Exit;
            public bool FromFile;
            public bool Drained;
        }

        private readonly object _drainLock = new();
        private readonly long _processStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private RestartDrain? _restart;
        private Timer? _drainTimer;
        private int _shutDown;

        // Program.cs: stops the app (drain restart ... exit)
        public Action? StopApp { get; set; }

        public bool Restarting => _restart != null;
        public bool Drained => _restart?.Drained == true;

        // seconds until the cap (0 = not restarting, or drained)
        public int RestartSecondsLeft
        {
            get
            {
                RestartDrain? r = _restart;
                return r == null || r.Drained ? 0 : Math.Max(1, (int)Math.Ceiling((r.Deadline - DateTime.UtcNow).TotalSeconds));
            }
        }

        // the /v1/health tail: " restart 412s 2 matches" while draining, " drained" once every match has ended
        public string DrainHealth => _restart is not RestartDrain r ? "" : r.Drained ? " drained"
            : FormattableString.Invariant($" restart {RestartSecondsLeft}s {StartedCount()} matches");

        private void StartDrainTimer() => _drainTimer = new Timer(_ => DrainTick(), null, 1_000, 1_000);

        // the selftest (and anything in process): a restart drain without the file
        public void BeginRestart(int capSeconds, bool exit = false)
        {
            lock (_drainLock)
            {
                Cancel("replaced");
                Begin(new RestartDrain { Cap = Math.Max(0, capSeconds), Exit = exit });
            }
            DrainTick();
        }

        public void CancelRestart()
        {
            lock (_drainLock)
            {
                Cancel("called off");
            }
        }

        // "restart <unix> <cap> [exit]" (Admin.Drain); null = not a restart line ("drain on" writes only a time)
        public static (long At, int Cap, bool Exit)? ParseRestart(string? text)
        {
            string[] w = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (w.Length < 3 || w[0] != "restart" || !long.TryParse(w[1], NumberStyles.None, CultureInfo.InvariantCulture, out long at)
                || !int.TryParse(w[2], NumberStyles.None, CultureInfo.InvariantCulture, out int cap))
            {
                return null;
            }
            return (at, cap, w.Length > 3 && w[3] == "exit");
        }

        public static string RestartLine(long at, int cap, bool exit) =>
            FormattableString.Invariant($"restart {at} {cap}") + (exit ? " exit" : "") + "\n";

        private void DrainTick()
        {
            try
            {
                (long At, int Cap, bool Exit)? want = null;
                if (DrainFile.Length > 0 && File.Exists(DrainFile))
                {
                    want = ParseRestart(File.ReadAllText(DrainFile));
                    if (want is { } old && old.At < _processStart - 5)
                    {
                        // (a deploy that didn't remove its file before the restart: this process is the new binary already)
                        Console.WriteLine($"drain: a restart file from before this server started ({old.At}): removed");
                        File.Delete(DrainFile);
                        want = null;
                    }
                }
                bool drainedNow = false;
                bool exit = false;
                lock (_drainLock)
                {
                    if (want is { } w && (_restart == null || _restart.FromFile && _restart.Requested != w.At))
                    {
                        Cancel("replaced"); // (a new request: its cap counts from now)
                        Begin(new RestartDrain { Requested = w.At, Cap = w.Cap, Exit = w.Exit, FromFile = true });
                    }
                    else if (want == null && _restart is { FromFile: true })
                    {
                        Cancel("called off (the drain file was removed)");
                    }
                    if (_restart is { Drained: false } r && (StartedCount() == 0 || DateTime.UtcNow >= r.Deadline))
                    {
                        r.Drained = true;
                        drainedNow = true;
                        exit = r.Exit;
                        int left = StartedCount();
                        Console.WriteLine(left == 0 ? "drain: every match has ended: drained (safe to restart)"
                            : $"drain: the cap ({r.Cap} s) passed with {left} match(es) still running: drained (safe to restart)");
                    }
                }
                if (drainedNow)
                {
                    Notify(new ServerNoticeEvent { Kind = NoticeKind.Drained, Text = RestartText(0) });
                    if (exit && StopApp is Action stop)
                    {
                        Console.WriteLine("drain: exit: stopping (systemd starts the new binary)");
                        Task.Delay(1_000).ContinueWith(_ => stop());
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine("drain: can't read the drain file: " + ex.Message);
            }
        }

        // (under _drainLock)
        private void Begin(RestartDrain r)
        {
            r.Deadline = DateTime.UtcNow.AddSeconds(r.Cap);
            _restart = r;
            Console.WriteLine($"drain: restart requested (cap {r.Cap} s{(r.Exit ? ", exit when drained" : "")}): {StartedCount()} "
                + $"match(es) running, {OnlineCount} online; no new lobbies, searches, joins or served games");
            // every search ends (a vote first: leaving one can put the others back in the queue)
            lock (_gamesLock)
            {
                foreach (string p in _matchOf.Keys.ToList())
                {
                    FindCancel(p, "restart");
                }
                foreach (string p in _ticketOf.Keys.ToList())
                {
                    FindCancel(p, "restart");
                }
            }
            Notify(new ServerNoticeEvent { Kind = NoticeKind.Restart, Seconds = r.Cap, Text = RestartText(r.Cap) });
        }

        // (under _drainLock)
        private void Cancel(string why)
        {
            if (_restart == null)
            {
                return;
            }
            _restart = null;
            Console.WriteLine("drain: restart " + why + ": back to normal");
            Notify(new ServerNoticeEvent { Kind = NoticeKind.Cancel, Text = "the server restart was called off." });
        }

        private static string RestartText(int seconds) => seconds <= 0 ? "the server is restarting for an update now. back in a moment."
            : $"the server restarts for an update in about {Duration(seconds)} (sooner once the running matches end).";

        private static string Duration(int seconds) => seconds < 90 ? $"{seconds} s" : $"{(seconds + 30) / 60} min";

        private int StartedCount()
        {
            lock (_gamesLock)
            {
                return _games.Values.Count(g => g.Started);
            }
        }

        // only to sessions whose app understands ev_notice (HelloMsg.Notices): an older one would drop on an unknown "t"
        private void Notify(ServerNoticeEvent ev)
        {
            foreach (Session s in _online.Values.Where(s => s.Notices).ToList())
            {
                _ = s.Push(new ServerNoticeEvent { Kind = ev.Kind, Seconds = ev.Seconds, Text = ev.Text });
            }
        }

        // the restart notice a login gets in its welcome (0 = none)
        private int WelcomeRestart => _restart == null ? 0 : Math.Max(1, RestartSecondsLeft); // (drained: any moment now)

        // Handle(): nothing new starts while the server drains (a rejoin to your own started match still does)
        private void RefuseWhileRestarting(string me, OnlineMsg msg)
        {
            if (_restart == null)
            {
                return;
            }
            bool refused = msg switch
            {
                HostGameReq or FindStartReq => true,
                JoinGameReq j => !IsRejoin(me, j.GameId),
                _ => false,
            };
            if (refused)
            {
                int left = RestartSecondsLeft;
                throw new OnlineException(DrainingCode, left > 0
                    ? $"the server restarts for an update in about {Duration(left)}. try again in a minute or two."
                    : "the server is restarting for an update. try again in a minute or two.");
            }
        }

        private bool IsRejoin(string me, string? id)
        {
            lock (_gamesLock)
            {
                return string.IsNullOrEmpty(id) ? _gameOf.TryGetValue(me, out Game? mine) && mine.Started && mine.Has(me)
                    : _games.TryGetValue(id, out Game? g) && g.Started && g.Has(me);
            }
        }

        // Program.cs, ApplicationStopping (systemctl restart / stop): every session hears why before the sockets go
        public void ShuttingDown()
        {
            if (Interlocked.Exchange(ref _shutDown, 1) != 0)
            {
                return;
            }
            _drainTimer?.Dispose();
            List<Session> all = _online.Values.ToList();
            Console.WriteLine($"stopping: closing {all.Count} session(s)");
            string reason = _restart != null ? "the server is restarting for an update. back in a moment."
                : "the server is restarting. back in a moment.";
            Task.WhenAll(all.Select(s => s.Kick(reason))).Wait(TimeSpan.FromSeconds(3));
        }
    }
}
