using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MphRead;
using MphRead.Entities;
using MphRead.Hud;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;

namespace MphRecomp.App;

// Chat in an online match (docs/ONLINE_PLAY.md section 8, Social O7). The game's chat (the server's ChatEvents, the same log
// the lobby's CHAT page shows: OnlineClient.Chat) appears as MphRead's own HUD messages, bottom left, newest at the bottom,
// a few seconds each; muted players' lines stay hidden (PlayerMutes). The quick-message wheel: hold Select ("View"; the
// touch controls' CHAT button), point the right stick at one of the eight (ChatPresets.Wheel, clockwise from the top), let
// go to send it; the pick stays when the stick goes back to rest, as the weapon ring's. Typed chat (owner 2026-10-09: "build
// the chat in match"): a short TAP of Select (or of the touch CHAT button) with no wheel pick opens a text box: the host's own
// (ChatBoxRequested: Android's dialog with the soft keyboard, CampaignActivity) or, with none, a line typed on the HUD (the
// Windows keyboard: ChatLineChar / ChatLineKey from DesktopInput; Enter or T opens it there too). While either is open the
// match runs on and this player stands still (BuildInput: MatchChatTyping). Nothing here touches the simulation: the HUD
// queue is drawing only. CampaignRenderer.BuildInput calls MatchChatWheel each step (true = the wheel has the right stick,
// so it doesn't aim); MatchTouchOverlay shows CHAT while TouchChat.
internal sealed partial class CampaignRenderer
{
    const int ChatShowLines = 4;            // lines the overlay brings in at once (older ones were missed: none on a rejoin)
    const float ChatLineSeconds = 6f;
    const byte ChatCategory = 4;            // the HUD queue's stacking bit (MphRead's kill feed uses 2): lines push up
    const float ChatLineX = 4, ChatLineY = 168;
    const int ChatLineWidth = 150;
    const long ChatTapMs = 300;             // Select let go sooner than this, no pick: the text box
    const float ChatTypeY = 156;            // the line being typed, above the chat lines

    long _chatSeen = -1;                    // the newest ChatLog line the overlay has shown (-1 = not started)
    OnlineMatch? _chatFor;
    object? _chatForHost;                   // (PLAY AGAIN keeps the game: a new match starts fresh too)
    bool _chatWheelOpen;
    int _chatWheelPick = -1;
    volatile bool _matchChatSending;
    volatile string? _matchChatNote;        // a send's trouble, from the network thread, shown at the next step
    long _chatWheelSince;
    volatile bool _chatBoxWanted;           // a tap asked for the text box (opened at the step, on the renderer's thread)
    string? _chatLine;                      // the HUD line being typed (no host box), null = closed
    bool _chatLineSelect;                   // Select held at the last step while the line was open
    readonly System.Collections.Concurrent.ConcurrentQueue<int> _chatLineKeys = new(); // chars, or -1 Enter, -2 Esc, -3 Backspace

    // the host's own text box (Android: CampaignActivity's dialog), asked for on the renderer's thread; the host calls
    // SendTypedChat with the text (null = cancelled) when it closes. Null: the HUD line (Windows)
    public Action? ChatBoxRequested { get; set; }
    public volatile bool ChatBoxOpen;

    // typing: this player's controls stand still (BuildInput)
    bool MatchChatTyping => _online != null && (ChatBoxOpen || _chatLine != null);

    // the HUD line is open (DesktopInput sends it the keys then)
    public bool ChatLineOpen => _chatLine != null;

    // Windows: Enter / T in an online match
    public void OpenMatchChat()
    {
        if (TouchChat) _chatBoxWanted = true;
    }

    // the HUD line's keys (any thread): a character, or Enter / Esc / Backspace
    public void ChatLineChar(char c) => _chatLineKeys.Enqueue(c);
    public void ChatLineKey(bool enter, bool escape) => _chatLineKeys.Enqueue(enter ? -1 : escape ? -2 : -3);

    // the host's box closed (any thread): its line, or null
    public void SendTypedChat(string? text)
    {
        ChatBoxOpen = false;
        string line = (text ?? "").Trim();
        if (line.Length == 0 || _online is not OnlineMatch m) return;
        SendMatchChat(m, -1, line.Length > ChatReq.MaxChat ? line[..ChatReq.MaxChat] : line);
    }

    // the touch controls' CHAT button: only in an online game
    public bool TouchChat => _online != null && !MatchOver;

    // BuildInput, each step: the new chat lines onto the HUD, and the wheel (true while it has the right stick)
    bool MatchChatWheel(PadState pad)
    {
        OnlineMatch? m = _online;
        PlayerEntity? p = _host?.Player;
        if (m == null || p == null)
        {
            _chatWheelOpen = false;
            return false;
        }
        ShowMatchChat(m, p);
        ShowReconnecting(m, p); // (MatchReconnect.cs)
        if (MatchOver)
        {
            _chatLine = null;
            _chatBoxWanted = false;
        }
        if (_chatBoxWanted)
        {
            _chatBoxWanted = false;
            if (!ChatBoxOpen && _chatLine == null)
            {
                if (ChatBoxRequested is Action open)
                {
                    ChatBoxOpen = true;
                    open();
                }
                else
                {
                    _chatLineKeys.Clear();
                    _chatLine = "";
                    _chatLineSelect = true;
                }
            }
        }
        if (_chatLine != null)
        {
            // (a controller alone can't type: Select again closes it)
            if (pad[PadButton.Select] && !_chatLineSelect) _chatLine = null;
            _chatLineSelect = pad[PadButton.Select];
            TypeChatLine(m, p);
            _chatWheelOpen = false;
            return false;
        }
        if (pad[PadButton.Select] && !MatchOver && !ChatBoxOpen)
        {
            if (!_chatWheelOpen)
            {
                _chatWheelOpen = true;
                _chatWheelPick = -1;
                _chatWheelSince = Environment.TickCount64;
            }
            float x = pad.Rx, y = pad.Ry;
            if (x * x + y * y >= 0.25f)
            {
                // clockwise from the top, in the pad's convention (down = +y)
                int k = (int)MathF.Round(MathF.Atan2(y, x) / (MathF.PI / 4));
                _chatWheelPick = (k + 2 + 8) % 8;
            }
            DrawChatWheel(p);
            return true;
        }
        if (_chatWheelOpen)
        {
            _chatWheelOpen = false;
            if (_chatWheelPick >= 0)
            {
                SendMatchChat(m, ChatPresets.Wheel[_chatWheelPick]);
            }
            else if (Environment.TickCount64 - _chatWheelSince < ChatTapMs && !MatchOver)
            {
                _chatBoxWanted = true; // a tap: type a line (opened at the next step)
            }
        }
        return false;
    }

    void ShowMatchChat(OnlineMatch m, PlayerEntity p)
    {
        ChatLog log = m.Client.Chat;
        if (_chatFor != m || _chatForHost != _host)
        {
            // a new match: what was said before it (the lobby) isn't brought in
            _chatFor = m;
            _chatForHost = _host;
            _chatSeen = log.Latest;
        }
        if (_matchChatNote is string note)
        {
            _matchChatNote = null;
            p.QueueHudMessage(ChatLineX, ChatLineY, Align.Left, ChatLineWidth, 8, new ColorRgba(0x295F), 1, 3f, ChatCategory, note);
        }
        if (log.Latest == _chatSeen)
        {
            return;
        }
        string game = m.Game.Id;
        string? me = m.Client.Me?.Code;
        List<ChatLog.Line> lines = log.Recent(ChatShowLines, PlayerMutes.Current, me, _chatSeen, MphRecomp.Online.Voice.VoiceSettings.Current.Chat);
        _chatSeen = log.Latest;
        foreach (ChatLog.Line line in lines.Where(l => l.GameId == game))
        {
            string name = line.Name;
            int hash = name.IndexOf('#');
            if (hash > 0) name = name[..hash];
            string text = HudText($"{name}: {line.Shown}");
            // your own lines dimmer, the others' white
            var color = new ColorRgba(line.Code == me ? 0x5294u : 0x7FFFu);
            p.QueueHudMessage(ChatLineX, ChatLineY, Align.Left, ChatLineWidth, 8, color, 1, ChatLineSeconds, ChatCategory, text);
        }
    }

    // the eight quick messages around the reticle, the picked one in the HUD's message green (one step each: re-queued
    // every step while Select is held; the queue drops a message at the same height when a new one comes)
    void DrawChatWheel(PlayerEntity p)
    {
        for (int i = 0; i < 8; i++)
        {
            float a = (i - 2) * MathF.PI / 4;
            float x = 128 + MathF.Cos(a) * 62, y = 96 + MathF.Sin(a) * 46 + i * 0.001f; // (a slot's own height)
            bool picked = i == _chatWheelPick;
            var color = new ColorRgba(picked ? 0x3FEFu : 0x4E73u);
            p.QueueHudMessage(x, y, Align.Center, 90, 8, color, 1, 2 / 60f, 0, ChatPresets.Text(ChatPresets.Wheel[i]));
        }
    }

    // the HUD line (no host box): the keys so far, drawn each step above the chat lines; Enter sends, Esc drops it
    void TypeChatLine(OnlineMatch m, PlayerEntity p)
    {
        while (_chatLine != null && _chatLineKeys.TryDequeue(out int key))
        {
            if (key == -1)
            {
                string line = _chatLine.Trim();
                _chatLine = null;
                if (line.Length > 0) SendMatchChat(m, -1, line);
            }
            else if (key == -2)
            {
                _chatLine = null;
            }
            else if (key == -3)
            {
                if (_chatLine.Length > 0) _chatLine = _chatLine[..^1];
            }
            else if (key >= ' ' && key <= '~' && _chatLine.Length < ChatReq.MaxChat)
            {
                _chatLine += (char)key;
            }
        }
        if (_chatLine != null)
        {
            bool caret = Environment.TickCount64 / 400 % 2 == 0;
            p.QueueHudMessage(ChatLineX, ChatTypeY, Align.Left, ChatLineWidth + 90, 8, new ColorRgba(0x3FEFu), 1, 2 / 60f, 0,
                $"say: {_chatLine}{(caret ? "_" : " ")}");
        }
    }

    void SendMatchChat(OnlineMatch m, int preset, string? text = null)
    {
        OnlineClient client = m.Client;
        if (_matchChatSending)
        {
            return;
        }
        if (!MphRecomp.Online.Voice.VoiceSettings.Current.Chat)
        {
            _matchChatNote = "chat is off in online settings.";
            return;
        }
        if (!client.Connected || !client.ServerAtLeast(ChatReq.SinceServer))
        {
            _matchChatNote = "chat isn't available.";
            return;
        }
        _matchChatSending = true;
        Task.Run(async () =>
        {
            try
            {
                await client.RequestAsync<OkMsg>(text != null ? new ChatReq { Text = text } : new ChatReq { Preset = preset });
            }
            catch (Exception ex)
            {
                // (as the lobby: a new profile's lock and a mute say why, in the server's words)
                string code = (ex as OnlineException)?.Code ?? "";
                _matchChatNote = code == "chat_rate" ? "slow down: wait a moment."
                    : code is "chat_locked" or "chat_muted" ? HudText(ex.Message.ToLowerInvariant()) : "not sent.";
            }
            finally
            {
                _matchChatSending = false;
            }
        });
    }

    // the HUD font's characters only (a typed line may hold anything the keyboard gives), within the queue's length
    static string HudText(string s)
    {
        var chars = s.Select(c => c >= ' ' && c <= '~' ? c : '?').Take(200).ToArray();
        return new string(chars);
    }
}
