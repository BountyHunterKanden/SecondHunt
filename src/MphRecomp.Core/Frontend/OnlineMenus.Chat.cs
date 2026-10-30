using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;
using MphRecomp.Online.Voice;
using MphRecomp.Config;

// ONLINE PLAY's chat (Social track, milestone O7; docs/ONLINE_PLAY.md section 8, the server's side in
// SecondHunt.Server/HubChat.cs). The lobby shows its game's last two chat lines under the seat cards and a CHAT bar that opens
// the CHAT page: the game's recent lines on the left (a line from someone else has MUTE and REPORT), TYPE A MESSAGE under them
// (the name keyboard), and the QUICK MESSAGES on the right (ChatPresets.All: no typing, no filter). Chat goes to the server on
// the game's own connection (ChatReq); the server filters typed text, keeps the new-identity limit (typed chat to strangers
// after a few online matches: quick messages and friends work at once) and sends every line back to everyone in the game who
// may see it, the sender too (ChatEvent -> OnlineClient.Chat, the one log both the lobby and the match overlay read).
// MUTE is this device's (PlayerMutes: chat and voice together, by friend code; the license's MUTE switches it back). REPORT
// asks, then sends a "chat" report: the server attaches what they typed (it keeps typed chat nowhere else). REPORT A HUNTER
// (from the license) picks what to report: their name, what they typed, their voice, cheating.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private static int s_onChat = -1, s_onReport = -1;

        // our callbacks (2740-2799)
        private const int ChOpenCall = 2740, ChTypeCall = 2741, LicMuteCall = 2742;
        private const int ChStartCall = 2743; // Start on CHAT / REPORT: in a match's pause, resume (as the pause page does)
        private const int ChPresetCall = 2750; // + the quick message (18 of them, to 2767)
        private const int ChMuteCall = 2770, ChReportCall = 2776; // + the log row
        private const int RpReasonCall = 2785; // + the reason
        private const int ChatFirstCall = ChOpenCall, ChatLastCall = VoiceLastCall; // (2789-2799: voice, OnlineMenus.Voice.cs)
        private const int ChatRows = 5, LobbyChatLines = 2, PresetRows = 9;
        // the keyboard's longest message (the server takes ChatReq.MaxChat; the name box shows about this many)
        private const int ChatTypeMax = 60;

        private static readonly (string Reason, string Label, string Line)[] ReportReasons =
        {
            (ReportReason.Chat, "what they typed", "the server attaches their last chat lines for the review."),
            (ReportReason.Name, "their name", "an offensive name or tag."),
            (ReportReason.Voice, "their voice", "voice isn't recorded: the review goes on your word."),
            (ReportReason.Cheating, "cheating", "a hacked game, or playing unfairly on purpose."),
        };

        // items (-1 = none)
        private int _chPanel = -1, _chSide = -1, _chTitle = -1, _chType = -1, _lbChat = -1, _liMute = -1;
        private readonly int[] _chPreset = Unset(ChatPresets.All.Length), _chMute = Unset(ChatRows), _chReport = Unset(ChatRows);
        private int _rpPanel = -1, _rpTitle = -1;
        private readonly int[] _rpRow = Unset(ReportReasons.Length);

        // ---- state (menu thread) ----
        private bool _chatTyping; // the name keyboard is up for a chat line (the lobby's game stays)
        private bool _chatSending;
        private string? _chatNote; // a short word under TYPE A MESSAGE (too fast, not sent), until _chatNoteUntil
        private long _chatNoteUntil;
        private List<ChatLog.Line> _chatShown = new(); // the CHAT page's rows as drawn (MUTE / REPORT act on these)
        private PlayerCard? _reportCard; // REPORT A HUNTER's player
        private int _reportReturn = -1;

        // tools (MphRead.Tools -chattest)
        internal static (int Chat, int Report) OnlineChatPages => (s_onChat, s_onReport);
        internal IReadOnlyList<ChatLog.Line> OnlineChatShown => _chatShown;

        private static bool IsChatPage(int page) => page >= 0 && (page == s_onChat || page == s_onReport);

        // the lobby's game (hosted or joined) and the connection its chat goes on; in a match's pause menu, the match's
        // (PauseChat: the pause's CHAT row opens this page, its back goes to the pause again: EnterOnline)
        private OnlineMatch? ChatMatch => _hosting ?? _joined ?? PauseChat;

        // the in-game pause of an online match (PauseOverlay sets it as it opens): its CHAT row and page; null = none
        public OnlineMatch? PauseChat { get; set; }

        // pages that keep the lobby's game while they're up (EnterOnline, TickLobby)
        private bool KeepsLobby(int page) => page == s_onChat || page == s_onReport || _chatTyping && page == MultiplayerMenu.NamePage;

        private void InstallChat(Func<int, MenuAction[], MenuPage> addFramePage, Func<int, float, float, float, float, int, MenuItem> input,
            Func<int, float, float, MenuItem> panelAt, Action<MenuPage, int> back, MenuItem title)
        {
            MenuItem Px(MenuPage page, float x, float y, float w, float h, int call) => page.AddItem(i => input(i, Mx(x), My(y), Mx(x + w), My(y + h), call));

            // ---- CHAT: back to the lobby ----
            MenuPage chat = addFramePage(s_onLobby, new[] { Call(MenuKeys.Start, ChStartCall) });
            s_onChat = chat.Index;
            _chPanel = chat.AddItem(i => panelAt(i, Mx(70), Mx(1150))).Index;
            _chSide = chat.AddItem(i => panelAt(i, Mx(1190), Mx(1850))).Index;
            back(chat, s_onLobby);
            _chTitle = chat.AddItem(i => Clone(title, i, 0, 0, stringId: _strings.Add("CHAT"))).Index;
            for (int r = 0; r < ChatRows; r++)
            {
                float y = ChatRowY(r);
                _chMute[r] = Px(chat, 850, y + 18, 126, 60, ChMuteCall + r).Index;
                _chReport[r] = Px(chat, 986, y + 18, 136, 60, ChReportCall + r).Index;
            }
            _chType = Px(chat, 100, 764, 1020, 76, ChTypeCall).Index; // (its centre above y 815: below, the layout's back corner takes it)
            for (int k = 0; k < ChatPresets.All.Length; k++)
            {
                (float x, float y) = PresetAt(k);
                _chPreset[k] = Px(chat, x, y, 305, 62, ChPresetCall + k).Index;
            }
            InstallVoice(chat, input); // (VOICE + STRANGERS under the quick messages: OnlineMenus.Voice.cs)

            // ---- REPORT A HUNTER: back (B and the arrow) runs LEAVE's call: back where it was opened from ----
            MenuPage report = addFramePage(-1, new[] { Call(MenuKeys.Start, ChStartCall) });
            s_onReport = report.Index;
            _rpPanel = report.AddItem(i => panelAt(i, Mx(70), Mx(1850))).Index;
            back(report, -1);
            _rpTitle = report.AddItem(i => Clone(title, i, 0, 0, stringId: _strings.Add("REPORT A HUNTER"))).Index;
            for (int r = 0; r < ReportReasons.Length; r++)
            {
                (float x0, float top, float x1, float bottom) = SettingsRowRect(r + 1);
                int row = r;
                _rpRow[r] = report.AddItem(i => input(i, x0, top, x1, bottom, RpReasonCall + row)).Index;
            }

            // ---- the lobby's CHAT bar (under the seat cards), the license's MUTE (left of REPORT) ----
            if (s_onLobby >= 0) _lbChat = Px(_file.Pages[s_onLobby], 1050, 922, 320, 70, ChOpenCall).Index;
            if (s_onLicense >= 0) _liMute = Px(_file.Pages[s_onLicense], 1230, LicenseButtonY(3), 285, 66, LicMuteCall).Index;
        }

        // the CHAT page's log rows (tops, mockup px), its quick messages (two columns)
        private static float ChatRowY(int row) => 228 + 106 * row;

        private static (float X, float Y) PresetAt(int k) => (1215 + k / PresetRows * 315, 262 + k % PresetRows * 70);

        private void ApplyChatSounds(Action<int, int, int, int> like)
        {
            like(s_onLobby, _lbChat, MultiplayerMenu.CreateJoinPage, _soundListRow);
            like(s_onLicense, _liMute, OptionBoxPage, _soundToggle);
            like(s_onChat, _chType, MultiplayerMenu.CreateJoinPage, _soundListRow);
            foreach (int p in _chPreset) like(s_onChat, p, MultiplayerMenu.CreateJoinPage, _soundListRow);
            foreach (int b in _chMute.Concat(_chReport)) like(s_onChat, b, OptionBoxPage, _soundToggle);
            foreach (int r in _rpRow) like(s_onReport, r, OptionBoxPage, _soundToggle);
            ApplyVoiceSounds(like); // (OnlineMenus.Voice.cs)
        }

        // ---- runtime ----

        // EnterOnline: CHAT or REPORT A HUNTER coming in
        private void EnterChat(MenuEngine menu, int page)
        {
            PlayerMutes.Use(ProfileDir);
            if (page == s_onChat)
            {
                bool typed = _chatTyping;
                _chatTyping = false;
                if (ChatMatch == null)
                {
                    menu.GoTo(s_onHub); // (the game is gone)
                    return;
                }
                ShowChat(menu, focus: !typed);
                if (typed) menu.SetState(_chType, MenuState.Focused);
            }
            else ShowReport(menu, focus: true);
        }

        // the game's lines now (muted players' hidden), newest last
        private List<ChatLog.Line> ChatLines(int max)
        {
            OnlineMatch? m = ChatMatch;
            if (m == null) return new List<ChatLog.Line>();
            string game = m.Game.Id;
            return m.Client.Chat.Recent(ChatLog.Keep, PlayerMutes.Current, m.Client.Me?.Code, others: VoiceSettings.Current.Chat).Where(l => l.GameId == game).TakeLast(max).ToList();
        }

        private void ShowChat(MenuEngine menu, bool focus)
        {
            _chatShown = ChatLines(ChatRows);
            string? me = ChatMatch?.Client.Me?.Code;
            bool lost = false;
            for (int r = 0; r < ChatRows; r++)
            {
                bool other = r < _chatShown.Count && _chatShown[r].Code != me;
                foreach (int item in new[] { _chMute[r], _chReport[r] })
                {
                    if (!other && menu.ItemState(item) == MenuState.Focused) lost = true;
                    Show(menu, item, other);
                }
            }
            if (focus) menu.SetState(_chPreset[0], MenuState.Focused);
            else if (lost) menu.SetState(_chType, MenuState.Focused);
        }

        private void ShowReport(MenuEngine menu, bool focus)
        {
            if (focus) menu.SetState(_rpRow[0], MenuState.Focused);
        }

        // TickOnline: new chat lines redraw the lobby / CHAT (the license's MUTE too)
        private long _chatSeen;

        private void TickChat()
        {
            OnlineMatch? m = ChatMatch;
            if (m == null || m.Disposed) return;
            long latest = m.Client.Chat.Latest;
            if (latest == _chatSeen) return;
            _chatSeen = latest;
            int page = _onlineMenu?.Page?.Index ?? -1;
            if (page == s_onChat && _onlineMenu != null) ShowChat(_onlineMenu, focus: false);
        }

        private void ChatCall(MenuEngine menu, int call)
        {
            switch (call)
            {
            case ChOpenCall:
                if (ChatMatch != null) menu.GoTo(s_onChat);
                break;
            case ChTypeCall:
                TypeChat(menu);
                break;
            case ChStartCall:
                if (PauseChat != null) Requested?.Invoke(FrontendRequestKind.Resume); // (the lobby: Start does nothing here)
                break;
            case LicMuteCall:
                if (_licCard is PlayerCard lc) AskMute(lc.Code, StreamerPrivacy.Display(lc.Name, lc.Tag));
                break;
            case >= ChPresetCall and < ChPresetCall + 18:
                SendChat(call - ChPresetCall, null);
                break;
            case >= ChMuteCall and < ChMuteCall + ChatRows:
            {
                int r = call - ChMuteCall;
                if (r < _chatShown.Count) AskMute(_chatShown[r].Code, _chatShown[r].Name);
                break;
            }
            case >= ChReportCall and < ChReportCall + ChatRows:
            {
                int r = call - ChReportCall;
                if (r < _chatShown.Count) AskReportChat(_chatShown[r].Code, _chatShown[r].Name);
                break;
            }
            case >= RpReasonCall and < RpReasonCall + 4:
                if (_reportCard is PlayerCard rc) AskReport(rc.Code, StreamerPrivacy.Display(rc.Name, rc.Tag), ReportReasons[call - RpReasonCall].Reason);
                break;
            default:
                VoiceCall(call); // (OnlineMenus.Voice.cs)
                break;
            }
        }

        // REPORT A HUNTER's and CHAT's back: where it was opened from (true = it was one of ours)
        private bool ChatBack(MenuEngine menu)
        {
            int page = menu.Page?.Index ?? -1;
            if (page != s_onReport) return false;
            menu.GoTo(_reportReturn >= 0 ? _reportReturn : s_onHub);
            return true;
        }

        // the license's REPORT: what for
        private void OpenReport(MenuEngine menu, PlayerCard c)
        {
            _reportCard = c;
            _reportReturn = menu.Page?.Index ?? s_onHub;
            menu.GoTo(s_onReport);
        }

        // TYPE A MESSAGE: the name keyboard, then the line goes out
        private void TypeChat(MenuEngine menu)
        {
            if (EditText == null || ChatMatch == null)
            {
                Import.Popup?.Inform(FrontendSession.WorkInProgressText);
                return;
            }
            if (ChatMutedText() is string muted)
            {
                Import.Popup?.Inform(muted);
                return;
            }
            _chatTyping = true;
            EditText(menu, "", ChatTypeMax, s_onChat, text =>
            {
                if (!string.IsNullOrWhiteSpace(text)) SendChat(-1, text);
            });
        }

        // the server muted this player's typed chat (an owner's mute): what to say, or null
        private string? ChatMutedText()
        {
            long until = ChatMatch?.Client.ChatMutedUntil ?? 0;
            if (until <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            return until == long.MaxValue ? "your typed chat is muted.\n\nquick messages still work."
                : $"your typed chat is muted until {DateTimeOffset.FromUnixTimeSeconds(until).ToLocalTime():MMM d, HH:mm}.\n\nquick messages still work.".ToLowerInvariant();
        }

        // a quick message (preset >= 0) or a typed line, on the game's connection; the server's echo is what shows it
        private void SendChat(int preset, string? text)
        {
            OnlineMatch? m = ChatMatch;
            if (m == null || _chatSending) return;
            if (!VoiceSettings.Current.Chat)
            {
                Import.Popup?.Inform("chat is off.\n\nturn chat on in online settings.");
                return;
            }
            OnlineClient client = m.Client;
            if (!client.Connected)
            {
                ChatNote("not connected.");
                return;
            }
            if (!client.ServerAtLeast(ChatReq.SinceServer))
            {
                Import.Popup?.Inform("chat needs a server update.");
                return;
            }
            _chatSending = true;
            Task.Run(async () =>
            {
                try
                {
                    await client.RequestAsync<OkMsg>(new ChatReq { Preset = preset, Text = preset >= 0 ? null : text });
                    Post(() => _chatSending = false);
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() =>
                    {
                        _chatSending = false;
                        string code = (ex as OnlineException)?.Code ?? "";
                        if (code == "chat_rate") ChatNote("slow down: wait a moment.");
                        else if (code is "chat_locked" or "chat_muted") Import.Popup?.Inform(ex.Message.ToLowerInvariant());
                        else ChatNote("not sent: " + Problem(ex));
                    });
                }
            });
        }

        private void ChatNote(string text)
        {
            _chatNote = text;
            _chatNoteUntil = Environment.TickCount64 + 4000;
        }

        // MUTE (a chat line's, the license's): this device stops showing their chat and playing their voice; again = unmute
        private void AskMute(string code, string display)
        {
            PlayerMutes mutes = PlayerMutes.Use(ProfileDir);
            string name = display.ToLowerInvariant();
            if (mutes.Any(code))
            {
                mutes.Set(code, false, false);
                Import.Popup?.Inform($"{name} is unmuted.");
                AfterMute();
                return;
            }
            Import.Popup?.Ask($"mute {name}?\n\nyou won't see their chat or hear their voice. unmute them on their hunter license.", yes =>
            {
                if (!yes) return;
                mutes.Set(code, true, true);
                AfterMute();
            });
        }

        private void AfterMute()
        {
            if (_onlineMenu?.Page?.Index == s_onChat) ShowChat(_onlineMenu, focus: false);
            Redraw();
        }

        // a chat line's REPORT: their typed chat, after a yes
        private void AskReportChat(string code, string display)
        {
            string name = display.ToLowerInvariant();
            Import.Popup?.Ask($"report what {name} typed?\n\ntheir last chat lines go with the report, for review.", yes =>
            {
                if (yes) SendReport(code, name, ReportReason.Chat);
            });
        }

        private void AskReport(string code, string display, string reason)
        {
            string name = display.ToLowerInvariant();
            string what = ReportReasons.First(r => r.Reason == reason).Label;
            Import.Popup?.Ask($"report {name} for {what}?\n\nthe report is kept for review.", yes =>
            {
                if (yes) SendReport(code, name, reason);
            });
        }

        private void SendReport(string code, string name, string reason)
        {
            OnlineClient? client = Conn;
            if (client is not { Connected: true })
            {
                Import.Popup?.Inform("can't reach the server.\n\n" + (_netError ?? "try again in a moment."));
                return;
            }
            // (a server before 0.5.0 drops a connection that sends it "chat" or "voice")
            bool needsNew = reason is ReportReason.Chat or ReportReason.Voice;
            if (!client.ServerAtLeast(needsNew ? ChatReq.SinceServer : ReportReason.SinceServer))
            {
                Import.Popup?.Inform("reporting this needs a server update.\n\nmute or block them meanwhile.");
                return;
            }
            Act(new ReportReq { Code = code, Reason = reason }, ok => ok.Done ? "thanks.\n\nthe report was sent."
                : "you reported this already today.");
        }

        // ---- drawing ----

        // DrawLobby, last: the game's last chat lines and the CHAT bar under the seat cards; "muted" on a muted player's card
        private void DrawLobbyChat(Dictionary<int, List<WidgetTri>> glows, List<LobbySeat> seats)
        {
            if (_lbChat < 0) return;
            int tag = _lbChat; // (one item from the lines to the bar: its centre is right of the layout's back corner)
            List<ChatLog.Line> lines = ChatLines(LobbyChatLines);
            if (lines.Count == 0) TextS("say hello: quick messages are in chat.", Mx(420), My(942), 0, OnGrey, tag, SizeX);
            for (int i = 0; i < lines.Count; i++)
            {
                ChatLine(lines[i], Mx(420), My(922 + 38 * i + (lines.Count == 1 ? 20 : 0)), Mw(600), tag);
            }
            Bar(Mx(1050), My(922), Mx(1370), My(992), _lbChat, glows);
            TextS("chat", Mx(1210), My(943), 2, Orange, _lbChat, SizeM);
            PlayerMutes mutes = PlayerMutes.Current;
            for (int k = 0; k < seats.Count; k++)
            {
                if (seats[k].Card is PlayerCard c && seats[k].Kind == SeatKind.Person && mutes.Any(c.Code))
                {
                    (float lx, float ly) = LobbyCardLabel(k, seats.Count); // (P8: 5-8 seats, OnlineMenus.Seats8.cs)
                    TextS("muted", lx, ly, 1, Red, LobbySeatItem(k, seats.Count), SizeX);
                }
            }
            DrawLobbyVoice(glows, seats); // ("talking" / "voice off", TALK: OnlineMenus.Voice.cs)
        }

        // one chat line on a row: name#tag (cyan: you) then the words (quick messages pale), cut to the width
        private void ChatLine(ChatLog.Line l, float x, float top, float width, int tag)
        {
            bool mine = l.Code == ChatMatch?.Client.Me?.Code;
            string name = StreamerPrivacy.Name(l.Name, mine).ToLowerInvariant() + ": ";
            float nw = _drawMenu!.Font.Measure(name) * SizeX;
            TextS(name, x, top, 0, mine ? Cyan : Orange, tag, SizeX);
            TextS(FitWidth(l.Shown, width - nw, SizeX), x + nw, top, 0, l.IsPreset ? Pale : White, tag, SizeX);
        }

        // the words wrapped into lines of `width` menu units at `size` (at most `max` lines, the last cut)
        private List<string> Wrap(string text, float width, float size, int max)
        {
            var lines = new List<string>();
            string line = "";
            foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (_drawMenu!.Font.Measure(next) * size <= width || line.Length == 0) line = next;
                else
                {
                    lines.Add(line);
                    line = word;
                }
            }
            if (line.Length > 0) lines.Add(line);
            if (lines.Count > max)
            {
                lines = lines.Take(max).ToList();
                lines[max - 1] = FitWidth(lines[max - 1] + "...", width, size);
            }
            for (int i = 0; i < lines.Count; i++) lines[i] = FitWidth(lines[i], width, size);
            return lines;
        }

        private void DrawChat(Dictionary<int, List<WidgetTri>> glows)
        {
            OnlineMatch? m = ChatMatch;
            if (m == null) return;
            int tag = _chPanel; // (the title stays the title's own)
            string? me = m.Client.Me?.Code;
            // the log: the newest at the bottom
            if (_chatShown.Count == 0) TextS("no chat yet. send a quick message, or type one.", Mx(610), My(450), 2, OnGrey, tag, SizeM);
            for (int r = 0; r < _chatShown.Count; r++)
            {
                ChatLog.Line l = _chatShown[r];
                float y = ChatRowY(r);
                Fill(Mx(100), My(y), Mx(1120), My(y + 96), 0, 0, 0, 0.35f, tag);
                Box(Mx(100), My(y), Mx(1120), My(y + 96), tag);
                bool mine = l.Code == me;
                TextS(StreamerPrivacy.Name(l.Name, mine).ToLowerInvariant() + (mine ? "  (you)" : ""), Mx(124), My(y + 10), 0, mine ? Cyan : Orange, tag, SizeX);
                List<string> words = Wrap(l.Shown, Mw(mine ? 970 : 700), SizeX, 2);
                for (int i = 0; i < words.Count; i++) TextS(words[i], Mx(124), My(y + 42 + 28 * i), 0, l.IsPreset ? Pale : White, tag, SizeX);
                if (mine) continue;
                Bar(Mx(850), My(y + 18), Mx(976), My(y + 78), _chMute[r], glows);
                TextS("mute", Mx(913), My(y + 36), 2, Orange, _chMute[r], SizeX);
                Bar(Mx(986), My(y + 18), Mx(1112), My(y + 78), _chReport[r], glows);
                TextS("report", Mx(1049), My(y + 36), 2, Red, _chReport[r], SizeX);
            }
            // TYPE A MESSAGE, and what holds typed chat back
            Bar(Mx(100), My(764), Mx(1120), My(840), _chType, glows);
            TextS("type a message", Mx(610), My(782), 2, Orange, _chType, SizeM);
            string? note = _chatNote != null && Environment.TickCount64 < _chatNoteUntil ? _chatNote : null;
            int left = m.Client.ChatLeft;
            if (note == null && ChatMutedText() != null) note = "your typed chat is muted: quick messages still work.";
            if (note == null && left > 0)
            {
                note = $"typed chat reaches friends only for now: {left} more online match{(left == 1 ? "" : "es")} to open it.";
            }
            if (note != null) TextS(FitWidth(note, Mw(1020), SizeX), Mx(610), My(858), 2, Pale, tag, SizeX);
            // QUICK MESSAGES
            int side = _chSide;
            Fill(Mx(1194), My(200), Mx(1846), My(238), HeaderBar.R, HeaderBar.G, HeaderBar.B, 1, side);
            TextS("quick messages", Mx(1520), My(208), 2, Dark, side, SizeM);
            for (int k = 0; k < ChatPresets.All.Length; k++)
            {
                (float x, float y) = PresetAt(k);
                Bar(Mx(x), My(y), Mx(x + 305), My(y + 62), _chPreset[k], glows);
                TextS(FitWidth(ChatPresets.All[k].ToLowerInvariant(), Mw(285), SizeX), Mx(x + 152), My(y + 19), 2, White, _chPreset[k], SizeX);
            }
            DrawChatVoice(glows); // (OnlineMenus.Voice.cs)
        }

        private void DrawReport(Dictionary<int, List<WidgetTri>> glows)
        {
            PlayerCard? c = _reportCard;
            if (c == null) return;
            int tag = _rpTitle;
            TextS("report " + StreamerPrivacy.Display(c.Name, c.Tag).ToLowerInvariant() + " for:", Mx(130), My(250), 0, White, tag, SizeS);
            for (int r = 0; r < ReportReasons.Length; r++)
            {
                (float x0, float top, float x1, float bottom) = SettingsRowRect(r + 1);
                float by = 222 + StPitch * (r + 1);
                Bar(x0, top, x1, bottom, _rpRow[r], glows);
                TextS(ReportReasons[r].Label, Mx(130), My(by + 12), 0, Orange, _rpRow[r], SizeS);
                TextS(ReportReasons[r].Line, Mx(130), My(by + 60), 0, White, _rpRow[r], SizeM);
            }
            TextS("reports go to the game's owner. mute or block them to stop seeing them at once.", Mx(960), My(830), 2, Pale, tag, SizeX);
        }

        // the license's MUTE (DrawLicense, beside its half-width REPORT)
        private void DrawLicenseMute(PlayerCard c, Dictionary<int, List<WidgetTri>> glows)
        {
            if (_liMute < 0) return;
            bool muted = PlayerMutes.Use(ProfileDir).Any(c.Code);
            float y = LicenseButtonY(3);
            Bar(Mx(1230), My(y), Mx(1515), My(y + 66), _liMute, glows);
            TextS(muted ? "unmute" : "mute", Mx(1372), My(y + 19), 2, muted ? Orange : Pale, _liMute, SizeM);
        }
    }
}
