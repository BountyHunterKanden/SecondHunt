using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Chat and safety (docs/ONLINE_PLAY.md section 8, Social O7; server 0.5.0). Chat goes to the people in the sender's game
    // (its lobby and its match share the game). Quick messages are always allowed; typed text goes through the word filter
    // (masked, never dropped), a rate limit, the owner's chat mutes and the new-identity limit: until a player has played
    // ChatUnlockMatches matches with other people, their typed chat reaches only their friends and rivals in the game. Blocked
    // pairs never see each other's chat. The last lines each player said are kept in MEMORY only (never in the database, gone
    // at a restart or after three hours) so a report can carry what was said; typed chat is stored nowhere else.
    public sealed partial class Hub
    {
        // typed chat reaches strangers after this many matches with other people (--chat-unlock; 0 = from the start)
        public int ChatUnlockMatches { get; set; } = 3;

        private readonly ChatRecorder _chat = new();
        private readonly Dictionary<string, Queue<long>> _chatTimes = new();
        private readonly Dictionary<string, (string Text, long At)> _chatLast = new();
        private readonly object _chatLock = new();

        private const int TypedPer10Seconds = 4, PresetsPer10Seconds = 6, MinTypedGapMs = 700;

        private OkMsg Chat(string me, ChatReq r)
        {
            bool typed = r.Preset < 0;
            string text = "";
            if (typed)
            {
                text = CleanChat(r.Text);
                if (text.Length == 0)
                {
                    throw new OnlineException("bad_text", "nothing to say");
                }
            }
            else if (!ChatPresets.Valid(r.Preset))
            {
                throw new OnlineException("bad_text", "not a quick message");
            }
            Store.Player player = _store.Get(me) ?? throw new OnlineException("not_found", "no such player");
            string gameId;
            List<string> members;
            lock (_gamesLock)
            {
                if (!_gameOf.TryGetValue(me, out Game? g))
                {
                    throw new OnlineException("no_game", "chat is for the people in your lobby or match");
                }
                gameId = g.Id;
                members = g.Slots.Values.ToList();
            }
            long nowMs = Environment.TickCount64;
            if (typed)
            {
                long until = _store.ChatUntil(me);
                if (until > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    throw new OnlineException("chat_muted", "typed chat is switched off for you right now: quick messages still work");
                }
            }
            CheckRate(me, typed, text, nowMs);
            int matches = _store.Matches(me);
            bool unlocked = matches >= ChatUnlockMatches;
            var to = new List<string>();
            foreach (string m in members)
            {
                if (m == me)
                {
                    to.Add(m);
                }
                else if (!_store.Blocked(me, m) && (!typed || unlocked || _store.HasLink(me, m, LinkKind.Friend))) // (vanilla: messages are for friends, not rivals: T3 0.7.0)
                {
                    to.Add(m);
                }
            }
            if (typed && !unlocked && members.Count > 1 && to.Count == 1 && members.Any(m => m != me && !_store.Blocked(me, m)))
            {
                int left = ChatUnlockMatches - matches;
                throw new OnlineException("chat_locked", $"typed chat opens after {ChatUnlockMatches} online matches ({left} to go): "
                    + "quick messages work now, and friends see your typing");
            }
            string shown = typed ? WordFilter.Mask(text) : "";
            string name = OnlineNames.Display(player.Name, player.Tag);
            _chat.Add(new ChatRecorder.Line(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), gameId, me, name,
                typed ? text : ChatPresets.Text(r.Preset), r.Preset));
            var ev = new ChatEvent { GameId = gameId, Code = me, Name = name, Preset = r.Preset, Text = shown };
            foreach (string m in to)
            {
                Push(m, ev);
            }
            return new OkMsg { Done = true };
        }

        // printable ASCII-and-above only, spaces squeezed, at most MaxChat characters
        private static string CleanChat(string? text)
        {
            var sb = new StringBuilder();
            bool space = true;
            foreach (char c in text ?? "")
            {
                if (Char.IsWhiteSpace(c))
                {
                    if (!space)
                    {
                        sb.Append(' ');
                    }
                    space = true;
                }
                else if (!Char.IsControl(c) && c != '​' && c != '‎' && c != '‏' && c != '‮' && c != '⁦' && c != '⁧' && c != '⁩')
                {
                    sb.Append(c);
                    space = false;
                }
            }
            string s = sb.ToString().TrimEnd();
            return s.Length > ChatReq.MaxChat ? s[..ChatReq.MaxChat].TrimEnd() : s;
        }

        private void CheckRate(string me, bool typed, string text, long nowMs)
        {
            lock (_chatLock)
            {
                if (!_chatTimes.TryGetValue(me, out Queue<long>? times))
                {
                    _chatTimes[me] = times = new Queue<long>();
                }
                while (times.Count > 0 && nowMs - times.Peek() > 10_000)
                {
                    times.Dequeue();
                }
                int limit = typed ? TypedPer10Seconds : PresetsPer10Seconds;
                bool tooFast = times.Count >= limit || (typed && times.Count > 0 && nowMs - times.Last() < MinTypedGapMs);
                if (typed && _chatLast.TryGetValue(me, out var last) && last.Text == text && nowMs - last.At < 10_000)
                {
                    tooFast = true; // the same line again
                }
                if (tooFast)
                {
                    throw new OnlineException("chat_rate", "slow down a little");
                }
                times.Enqueue(nowMs);
                if (typed)
                {
                    _chatLast[me] = (text, nowMs);
                }
            }
        }

        // the report evidence for a chat report: what this player said lately and the game's chat around it, or null
        private string? ChatEvidence(string target) => _chat.Evidence(target);

        private string BanText(Store.BanRow ban, bool device = false)
            => (device ? "this device is banned from online play" : "this online profile is banned")
                + (ban.Until == 0 ? "" : " until " + DateTimeOffset.FromUnixTimeSeconds(ban.Until).ToString("yyyy-MM-dd"))
                + (ban.Reason.Length > 0 ? ": " + ban.Reason : "");

        // HelloMsg.Device is a SHA-256 in hex (DeviceTag); anything else counts as no device
        private static string CleanDevice(string? device)
            => device is { Length: 64 } && device.All(Uri.IsHexDigit) ? device.ToLowerInvariant() : "";

        // every 15 s: a ban the owner just added kicks the player; once a day old reports go
        private Timer? _safetyTimer;
        private int _safetyTicks;

        private void StartSafetyTimer()
        {
            _safetyTimer = new Timer(_ => SafetyTick(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            try
            {
                _store.PurgeReports();
                _store.ReplaceBadNames();
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or OnlineException)
            {
                Console.Error.WriteLine($"safety: start-up sweep failed: {ex.Message}");
            }
        }

        internal void SafetyTick()
        {
            try
            {
                foreach (Store.BanRow ban in _store.Bans())
                {
                    if (_online.TryGetValue(ban.Code, out Session? s))
                    {
                        _store.AddBanDevice(ban.Code, s.Device);
                        _ = s.Kick(BanText(ban));
                    }
                }
                if (++_safetyTicks % (4 * 60 * 24) == 0)
                {
                    _store.PurgeReports();
                }
                _chat.Prune();
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
            {
                Console.Error.WriteLine($"safety tick: {ex.Message}"); // (tried again in 15 s)
            }
        }
    }

    // The chat kept in memory for reports (see Hub chat above): per player the last lines they sent, per game the last lines
    // anyone sent, all dropped after three hours. Nothing here is ever written to disk.
    public sealed class ChatRecorder
    {
        public sealed record Line(long At, string GameId, string Code, string Name, string Text, int Preset);

        private const int PerPlayer = 30, PerGame = 60, KeepSeconds = 3 * 3600, ReportOwn = 10, ReportContext = 20;
        private readonly object _lock = new();
        private readonly Dictionary<string, List<Line>> _byPlayer = new();
        private readonly Dictionary<string, List<Line>> _byGame = new();

        public void Add(Line line)
        {
            lock (_lock)
            {
                Push(_byPlayer, line.Code, line, PerPlayer);
                Push(_byGame, line.GameId, line, PerGame);
            }
        }

        private static void Push(Dictionary<string, List<Line>> map, string key, Line line, int max)
        {
            if (!map.TryGetValue(key, out List<Line>? list))
            {
                map[key] = list = new List<Line>();
            }
            list.Add(line);
            if (list.Count > max)
            {
                list.RemoveAt(0);
            }
        }

        public void Prune()
        {
            long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - KeepSeconds;
            lock (_lock)
            {
                foreach (var map in new[] { _byPlayer, _byGame })
                {
                    foreach (string key in map.Keys.ToList())
                    {
                        map[key].RemoveAll(l => l.At < cutoff);
                        if (map[key].Count == 0)
                        {
                            map.Remove(key);
                        }
                    }
                }
            }
        }

        // what the player said (newest 10) and the game's lines around it, for the owner to read; null = nothing in memory
        public string? Evidence(string code)
        {
            lock (_lock)
            {
                Prune();
                if (!_byPlayer.TryGetValue(code, out List<Line>? own) || own.Count == 0)
                {
                    return null;
                }
                var sb = new StringBuilder();
                var mine = own.TakeLast(ReportOwn).ToList();
                sb.AppendLine("Said by the reported player (newest last, typed text as sent, before masking):");
                foreach (Line l in mine)
                {
                    sb.AppendLine(Format(l));
                }
                string game = mine[^1].GameId;
                if (_byGame.TryGetValue(game, out List<Line>? around))
                {
                    sb.AppendLine("The game's chat around it (everyone, newest last):");
                    foreach (Line l in around.TakeLast(ReportContext))
                    {
                        sb.AppendLine(Format(l));
                    }
                }
                return sb.ToString().TrimEnd();
            }
        }

        private static string Format(Line l)
            => $"[{DateTimeOffset.FromUnixTimeSeconds(l.At):HH:mm:ss}] {l.Name}: {(l.Preset >= 0 ? "(quick) " : "")}{l.Text}";

        public void DropGame(string gameId)
        {
            lock (_lock)
            {
                _byGame.Remove(gameId);
            }
        }
    }
}
