using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Served games (0.8.0, docs/DEDICATED_SERVERS.md D3): the match runs here, not on a player's device. A client asks for
    // it (HostGameReq with GameInfo.Served); when this server can, it starts a SecondHuntMatchHost process for the game on
    // relay slot 0 (over loopback) and the asking player joins on slot 1 as the lobby's OWNER (Game.Host): it still runs the
    // lobby (its START and settings go to the match host over the relay, NetMatchOwner.cs) but it is a joiner like the rest.
    // When it can't (served games off, at the cap, draining for an update, another version, an imported arena or mods), the
    // reply says Served = false and the player hosts as before.
    //   - One process per game (MphRead's game state is static): a crash ends only that game. The process talks to this hub
    //     over its stdin/stdout (SecondHunt.MatchHost/ControlMode.cs) and dies with it (a closed stdin = stop).
    //   - The owner leaving passes the lobby to the next player; the game closes when nobody is left. No host migration.
    //   - Results come from the match host (!result: trusted); players' own reports of a served match are ignored.
    // SecondHuntServer --matchhost <SecondHuntMatchHost> --matchhost-data <its game files' folder (paths.txt)> [--max-matches 4]
    public sealed class ServedConfig
    {
        public string Exe { get; set; } = "";
        public string Data { get; set; } = "";
        public int MaxMatches { get; set; } = 4;
        // arguments before the match host's own (the selftest runs this server's binary as a stand-in: --fake-matchhost)
        public List<string> Prefix { get; set; } = new();
        // arguments after the server's own for every served game's match host (--matchhost-args "--time 20": tests)
        public List<string> Extra { get; set; } = new();

        public static ServedConfig FromArgs(string[] args, Func<string[], string, string?> arg) => new()
        {
            Exe = arg(args, "--matchhost") ?? "",
            Data = arg(args, "--matchhost-data") ?? "",
            MaxMatches = Int32.TryParse(arg(args, "--max-matches"), out int max) ? Math.Max(0, max) : 4,
            Extra = (arg(args, "--matchhost-args") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
        };
    }

    public sealed partial class Hub
    {
        public ServedConfig Served { get; set; } = new();
        // no new served games (before an update: the running ones play on); new games are player-hosted meanwhile. On while
        // the drain file next to the database is there (SecondHuntServer admin drain on), or set here
        public bool Draining { get => _draining || DrainFile.Length > 0 && File.Exists(DrainFile); set => _draining = value; }
        private bool _draining;
        public string DrainFile { get; set; } = "";
        // (the health line's tail: served games now / the cap, and draining)
        public string ServedHealth => _servedVersion == null ? "" : $" served {ServedCount}/{Served.MaxMatches}" + (Draining ? " draining" : "");
        // the game version the match host plays (its --version), null = served games off
        private string? _servedVersion;

        private sealed class MatchHostProcess
        {
            public MatchHostProcess(string gameId, Process process)
            {
                GameId = gameId;
                Process = process;
            }

            public string GameId { get; }
            public Process Process { get; }
            public volatile bool Stopping;
        }

        // at start: which game version the match host plays (it reads its game files to know); served games stay off if it
        // doesn't answer
        public void StartServed()
        {
            if (Served.Exe.Length == 0)
            {
                Console.WriteLine("served games: off (no --matchhost)");
                return;
            }
            try
            {
                var psi = new ProcessStartInfo(Served.Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                Served.Prefix.ForEach(psi.ArgumentList.Add);
                psi.ArgumentList.Add("--version");
                AddData(psi);
                using Process p = Process.Start(psi)!;
                Task<string> output = p.StandardOutput.ReadToEndAsync();
                Task<string> errors = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60_000))
                {
                    p.Kill();
                    throw new IOException("no answer in 60 s");
                }
                string version = output.Result.Trim();
                if (p.ExitCode != 0 || version.Length == 0 || version.Contains('\n'))
                {
                    throw new IOException($"exit {p.ExitCode}: {(output.Result + errors.Result).Trim()}");
                }
                _servedVersion = version;
                Console.WriteLine($"served games: on, game version {version}, up to {Served.MaxMatches} at once");
                ReadServedArenas(); // (Find Game: HubServedFind.cs)
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"served games: off: the match host didn't start ({ex.Message})");
            }
        }

        public int ServedCount
        {
            get
            {
                lock (_gamesLock)
                {
                    return _games.Values.Count(g => g.MatchHost != null);
                }
            }
        }

        // (under _gamesLock) may a new game with this info be served here
        private bool CanServe(GameInfo info) => _servedVersion != null && !Draining && info.Version == _servedVersion
            && (!info.ExtraArena || _servedArenas?.Contains(info.Arena) == true) && info.Mods == GameInfo.NoMods && _games.Values.Count(g => g.MatchHost != null) < Served.MaxMatches;

        // (under _gamesLock) the match host for this game on relay slot 0 (null = it didn't start: the game isn't served)
        private MatchHostProcess? StartMatchHost(Game g, Relay.Member slot0, int ownerSlot, IEnumerable<string>? extra = null)
        {
            var psi = new ProcessStartInfo(Served.Exe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            Served.Prefix.ForEach(psi.ArgumentList.Add);
            foreach (string a in new[]
            {
                "--control", "--relay-host", "127.0.0.1", "--relay-port", _relay.Port.ToString(), "--token", RelayWire.TokenText(slot0.Token),
                "--owner", ownerSlot.ToString(), "--seats", g.Info.MaxPlayers.ToString(), "--mode", g.Info.Mode, "--arena", g.Info.Arena,
                "--stats", "60",
            })
            {
                psi.ArgumentList.Add(a);
            }
            foreach (string a in (extra ?? []).Concat(Served.Extra))
            {
                psi.ArgumentList.Add(a);
            }
            AddData(psi);
            try
            {
                Process p = Process.Start(psi)!;
                var mh = new MatchHostProcess(g.Id, p);
                p.StandardInput.AutoFlush = true;
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        FromMatchHost(mh, e.Data);
                    }
                };
                p.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        Console.WriteLine($"[match {mh.GameId}] {e.Data}");
                    }
                };
                p.Exited += (_, _) => MatchHostExited(mh);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                Console.WriteLine($"served game {g.Id}: match host pid {p.Id} ({_games.Values.Count(x => x.MatchHost != null) + 1}/{Served.MaxMatches})");
                return mh;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                Console.WriteLine($"served game {g.Id}: the match host didn't start ({ex.Message}): player-hosted");
                return null;
            }
        }

        // selftest: a line from this game's match host as if it said it; the game's match host process (null = none)
        internal void MatchHostSays(string gameId, string line)
        {
            MatchHostProcess? mh;
            lock (_gamesLock)
            {
                mh = _games.TryGetValue(gameId, out Game? g) ? g.MatchHost : null;
            }
            if (mh != null)
            {
                FromMatchHost(mh, line);
            }
        }

        internal Process? MatchHostOf(string gameId)
        {
            lock (_gamesLock)
            {
                return _games.TryGetValue(gameId, out Game? g) ? g.MatchHost?.Process : null;
            }
        }

        private void AddData(ProcessStartInfo psi)
        {
            if (Served.Data.Length > 0)
            {
                psi.ArgumentList.Add("--data");
                psi.ArgumentList.Add(Served.Data);
            }
        }

        private void FromMatchHost(MatchHostProcess mh, string line)
        {
            if (!line.StartsWith('!'))
            {
                Console.WriteLine($"[match {mh.GameId}] {line}");
                return;
            }
            string[] parts = line[1..].Split(' ', 2);
            string arg = parts.Length > 1 ? parts[1] : "";
            lock (_gamesLock)
            {
                if (!_games.TryGetValue(mh.GameId, out Game? g) || g.MatchHost != mh)
                {
                    return;
                }
                switch (parts[0])
                {
                case "ready":
                    Console.WriteLine($"served game {g.Id}: match host ready ({arg})");
                    break;
                case "started":
                    SetStarted(g, true);
                    FindOwnerAfterStart(g); // (a Find Game match: HubServedFind.cs)
                    break;
                case "lobby":
                    SetStarted(g, false);
                    break;
                case "result":
                    ServedResult(g, arg);
                    break;
                case "bye":
                    Console.WriteLine($"served game {g.Id}: match host leaving ({arg})");
                    break;
                }
            }
        }

        private void MatchHostExited(MatchHostProcess mh)
        {
            lock (_gamesLock)
            {
                if (_games.TryGetValue(mh.GameId, out Game? g) && g.MatchHost == mh)
                {
                    Console.WriteLine($"served game {g.Id}: match host exited ({SafeExitCode(mh.Process)})");
                    CloseGame(g, "the server's match ended");
                }
            }
        }

        private static string SafeExitCode(Process p)
        {
            try
            {
                return $"code {p.ExitCode}";
            }
            catch (InvalidOperationException)
            {
                return "code ?";
            }
        }

        // (under _gamesLock: CloseGame) the game is closed: the match host leaves (killed if it hasn't in 5 s)
        private static void StopMatchHost(MatchHostProcess mh)
        {
            mh.Stopping = true;
            try
            {
                mh.Process.StandardInput.WriteLine("stop");
                mh.Process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
            _ = Task.Delay(5000).ContinueWith(_ =>
            {
                try
                {
                    if (!mh.Process.HasExited)
                    {
                        mh.Process.Kill();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            });
        }

        // (under _gamesLock) the lobby's owner is now this relay slot
        private static void TellOwner(MatchHostProcess mh, int slot)
        {
            try
            {
                mh.Process.StandardInput.WriteLine($"owner {slot}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        // shutting down: every match host leaves (they would anyway: their stdin closes with this process)
        private void StopMatchHosts()
        {
            lock (_gamesLock)
            {
                foreach (Game g in _games.Values.Where(g => g.MatchHost != null))
                {
                    StopMatchHost(g.MatchHost!);
                }
            }
        }

        // (under _gamesLock) !result {"teams":false,"rows":[{"slot":1,"place":0,"team":-1},...]}: the match host's word on a
        // finished match (only human seats; places as RESULTS shows them, a team mode's = its team's best)
        private void ServedResult(Game g, string json)
        {
            ServedResultLine? line;
            try
            {
                line = JsonSerializer.Deserialize<ServedResultLine>(json, OnlineProtocol.Json);
            }
            catch (JsonException)
            {
                line = null;
            }
            if (line?.Rows == null || !_rounds.TryGetValue(g.Id, out Round? round) || round.Reported)
            {
                return;
            }
            var rows = new List<ResultRow>();
            foreach (ServedResultRow x in line.Rows)
            {
                if (g.Slots.TryGetValue(x.Slot, out string? code) && round.Members.Contains(code) && rows.All(r => r.Code != code))
                {
                    rows.Add(new ResultRow
                    {
                        Code = code, Place = Math.Clamp(x.Place, 0, MatchResultReq.MaxRows - 1), Team = line.Teams && x.Team is 0 or 1 ? x.Team : -1,
                    });
                }
            }
            round.Reported = true;
            int pairs = rows.Count >= 2 ? CountHeadToHead(rows, line.Teams) : 0;
            Console.WriteLine($"served game {g.Id}: result for {rows.Count} players ({pairs} pairs)");
        }

        private sealed class ServedResultLine
        {
            public bool Teams { get; set; }
            public List<ServedResultRow>? Rows { get; set; }
        }

        private sealed class ServedResultRow
        {
            public int Slot { get; set; }
            public int Place { get; set; }
            public int Team { get; set; } = -1;
        }
    }
}
