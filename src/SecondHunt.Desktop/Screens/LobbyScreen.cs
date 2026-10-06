using System.Drawing;
using System.Net;
using System.Text.Json;
using MphRead;
using MphRecomp.App;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using SecondHunt.Desktop.Ui;
using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace SecondHunt.Desktop.Screens;

// The player-facing multiplayer screen (as the Android app's MatchLobbyActivity, the same choices and rules: the shared
// MatchLobby): a match against bots on this PC, or a LAN match with one other player on the same network, Android or
// Windows (host + one joiner, Battle only). Arrow keys / D-pad move, left / right change a row, Enter / A presses, Esc /
// B goes back; the mouse clicks rows, their arrows and the buttons. The last choices are kept in lobby.json.
internal sealed class LobbyScreen : PageScreen
{
    enum Mode { Choices, Hosting, Joining }

    readonly MatchLobby _lobby;
    readonly IReadOnlyList<MatchLobby.Row> _rows;
    readonly Action<Dictionary<string, string>> _startMatch;
    readonly Action _back;
    readonly string _prefsPath;
    Mode _mode;
    int _focus; // rows, then the buttons
    string? _note; // a message under the title (Android's toasts)
    LanSession? _session;
    volatile bool _joined, _joining;
    string _status = "";
    string _address = "";
    List<LanHostInfo> _hosts = new();
    long _hostsAt;

    // painted rectangles, for clicks
    readonly List<(RectangleF Rect, Action Act, int Focus)> _hits = new();

    static readonly string[] ChoiceButtons = { "Start match\n(vs bots)", "Host LAN match", "Join LAN match", "Back" };

    public LobbyScreen(string filesDir, string prefsPath, Action<Dictionary<string, string>> startMatch, Action back)
    {
        _lobby = new MatchLobby(filesDir);
        _rows = _lobby.Rows();
        _prefsPath = prefsPath;
        _startMatch = startMatch;
        _back = back;
        Load();
    }

    // ---- remembered choices ----

    void Load()
    {
        try
        {
            if (!File.Exists(_prefsPath)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_prefsPath)) ?? new();
            _lobby.Load(k => d.TryGetValue(k, out string? v) ? v : null,
                (k, fallback) => d.TryGetValue(k, out string? v) && int.TryParse(v, out int n) ? n : fallback);
        }
        catch (Exception ex)
        {
            MphRecomp.App.Platform.Log.Warn("MPHLobby", "load choices: " + ex.Message);
        }
    }

    void Save()
    {
        try
        {
            var d = new Dictionary<string, string>();
            _lobby.Save((k, v) => { if (v != null) d[k] = v; }, (k, v) => d[k] = v.ToString());
            File.WriteAllText(_prefsPath, JsonSerializer.Serialize(d));
        }
        catch (Exception ex)
        {
            MphRecomp.App.Platform.Log.Warn("MPHLobby", "save choices: " + ex.Message);
        }
    }

    // ---- actions ----

    void Change(int row, int delta)
    {
        _rows[row].Step(delta);
        Save();
        Invalidate();
    }

    void StartBotMatch()
    {
        Save();
        _startMatch(_lobby.BotMatchArgs());
    }

    void HostLan()
    {
        MatchSettings? template = _lobby.LanTemplate(out bool modeChanged);
        _note = modeChanged ? "LAN matches are Battle only for now: mode set to Battle" : null;
        Save();
        if (template == null)
        {
            _note = "Can't read the game's files, so LAN play can't start: restart the app and try again";
            Invalidate();
            return;
        }
        LanSession.Current?.Dispose();
        try
        {
            _session = LanSession.Host(template, MatchLobby.LanRomKey(Paths.MphKey), "Windows PC");
        }
        catch (Exception ex)
        {
            _note = "Can't host: " + ex.Message;
            Invalidate();
            return;
        }
        LanSession.Current = _session;
        _status = _session.Status;
        _session.StatusChanged += s => { _status = s; Invalidate(); };
        _session.Joined += () => _joined = true;
        _mode = Mode.Hosting;
        _focus = 0;
        Invalidate();
    }

    void JoinLan()
    {
        if (!_lobby.EnsurePaths()) // the ROM revision the handshake compares
        {
            _note = "Can't read the game's files, so LAN play can't start: restart the app and try again";
            Invalidate();
            return;
        }
        LanSession.Current?.Dispose();
        _session = LanSession.Browse(MatchLobby.LanRomKey(Paths.MphKey));
        LanSession.Current = _session;
        _status = _session.Status;
        string mine = LanSession.LocalAddress();
        int dot = mine.LastIndexOf('.');
        _address = dot > 0 ? mine[..(dot + 1)] : "";
        _hosts = new();
        _mode = Mode.Joining;
        _focus = 0;
        Invalidate();
    }

    void JoinTarget(IPEndPoint target)
    {
        if (_joining || _session == null) return;
        _joining = true;
        _status = $"joining {target.Address}...";
        Invalidate();
        LanSession session = _session;
        Task.Run(() =>
        {
            bool ok = session.Join(target, Enum.Parse<Hunter>(MatchLobby.Hunters[_lobby.Hunter]), "Windows PC");
            _joining = false;
            if (ok) _joined = true;
            else _status = session.Status;
            Invalidate();
        });
    }

    void JoinTyped()
    {
        string text = _address.Trim();
        string host = text;
        int port = LanSession.GamePort;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out int p))
        {
            host = text[..colon];
            port = p;
        }
        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            JoinTarget(new IPEndPoint(ip, port));
        }
        else
        {
            _status = $"\"{text}\" isn't an address like 10.0.0.23";
            Invalidate();
        }
    }

    void CancelLan()
    {
        _session?.Dispose();
        if (LanSession.Current == _session) LanSession.Current = null;
        _session = null;
        Mode was = _mode;
        _mode = Mode.Choices;
        _focus = _rows.Count + (was == Mode.Hosting ? 1 : 2); // back on the button that opened it
        _joined = false;
        Invalidate();
    }

    // the LAN session the match screen plays (it closes it when the match ends)
    protected override void Update()
    {
        if (_joined && _session != null)
        {
            _joined = false;
            bool host = _session.IsHost;
            _session = null; // handed over: LanSession.Current
            _startMatch(_lobby.NetMatchArgs(host));
            return;
        }
        if (_mode == Mode.Joining && _session != null && Environment.TickCount64 - _hostsAt > 1000)
        {
            _hostsAt = Environment.TickCount64;
            var hosts = _session.Hosts.ToList();
            if (hosts.Count != _hosts.Count || hosts.Where((h, i) => h.Label != _hosts[i].Label).Any())
            {
                _hosts = hosts;
                Invalidate();
            }
            if (!_joining)
            {
                string status = _hosts.Count == 0 ? _session.Status : "click a match to join:";
                if (status != _status)
                {
                    _status = status;
                    Invalidate();
                }
            }
        }
    }

    // the screen is going away (Esc out of the lobby, or the window closing): a LAN session not handed to a match ends
    public void Close()
    {
        if (_session != null) CancelLan();
    }

    // ---- input ----

    // focusables in the current mode: Choices = rows then 4 buttons; Hosting = Cancel; Joining = hosts, the address
    // field, Join this address, Cancel
    int FocusCount => _mode switch
    {
        Mode.Choices => _rows.Count + ChoiceButtons.Length,
        Mode.Hosting => 1,
        _ => _hosts.Count + 3,
    };

    public override void Move(int dx, int dy)
    {
        if (_mode == Mode.Choices && _focus < _rows.Count && dx != 0)
        {
            Change(_focus, dx);
            return;
        }
        if (_mode == Mode.Choices && _focus >= _rows.Count && dx != 0)
        {
            _focus = Math.Clamp(_focus + dx, _rows.Count, _rows.Count + ChoiceButtons.Length - 1);
        }
        else if (dy != 0)
        {
            if (_mode == Mode.Choices && _focus >= _rows.Count)
            {
                _focus = dy < 0 ? _rows.Count - 1 : _focus;
            }
            else
            {
                _focus = Math.Clamp(_focus + dy, 0, FocusCount - 1);
            }
        }
        Invalidate();
    }

    public override void Press()
    {
        switch (_mode)
        {
        case Mode.Choices:
            if (_focus < _rows.Count) Change(_focus, 1);
            else ChoiceAction(_focus - _rows.Count)();
            break;
        case Mode.Hosting:
            CancelLan();
            break;
        case Mode.Joining:
            if (_focus < _hosts.Count) JoinTarget(_hosts[_focus].Address);
            else if (_focus == _hosts.Count + 1) JoinTyped();
            else if (_focus == _hosts.Count + 2) CancelLan();
            break;
        }
    }

    Action ChoiceAction(int button) => button switch
    {
        0 => StartBotMatch,
        1 => HostLan,
        2 => JoinLan,
        _ => () => { Save(); _back(); },
    };

    public override void Back()
    {
        if (_mode != Mode.Choices)
        {
            CancelLan();
        }
        else
        {
            Save();
            _back();
        }
    }

    public override void Click(float x, float y)
    {
        foreach ((RectangleF rect, Action act, int focus) in _hits.ToArray())
        {
            if (rect.Contains(x, y))
            {
                if (focus >= 0) _focus = focus;
                act();
                Invalidate();
                return;
            }
        }
    }

    public override void Hover(float x, float y)
    {
        foreach ((RectangleF rect, _, int focus) in _hits.ToArray())
        {
            if (focus >= 0 && rect.Contains(x, y) && focus != _focus)
            {
                _focus = focus;
                Invalidate();
                return;
            }
        }
    }

    // the address field takes typing while it has the focus
    bool AddressFocused => _mode == Mode.Joining && _focus == _hosts.Count;

    public override void Text(char c)
    {
        if (AddressFocused && (char.IsDigit(c) || c is '.' or ':') && _address.Length < 32)
        {
            _address += c;
            Invalidate();
        }
    }

    public override void Key(Keys key)
    {
        if (AddressFocused && key == Keys.Backspace && _address.Length > 0)
        {
            _address = _address[..^1];
            Invalidate();
        }
    }

    // Backspace in the address field edits instead of going back
    public bool WantsBackspace => AddressFocused;

    // ---- painting ----

    protected override void Paint(Graphics g)
    {
        _hits.Clear();
        float s = S, x = 32 * s, y = 14 * s, w = Width - 64 * s;
        using (Font title = Font(28, bold: true))
        {
            DrawText(g, "MULTIPLAYER", title, Color.White, new RectangleF(x, y, w, 44 * s));
        }
        y += 44 * s;
        using (Font sub = Font(14))
        {
            string text = _note ?? "LAN play is experimental: two players on the same network, Battle mode only.";
            DrawText(g, text, sub, Warning, new RectangleF(x, y, w, 24 * s));
        }
        y += 32 * s;
        if (_mode == Mode.Choices) PaintChoices(g, x, y, w);
        else if (_mode == Mode.Hosting) PaintHosting(g, x, y, w);
        else PaintJoining(g, x, y, w);
    }

    void PaintChoices(Graphics g, float x, float y, float w)
    {
        float s = S;
        using Font label = Font(19), value = Font(19, bold: true), arrow = Font(26, bold: true), button = Font(17), hint = Font(13);
        for (int i = 0; i < _rows.Count; i++)
        {
            int row = i;
            var r = new RectangleF(x, y, w, 46 * s);
            Box(g, r, _focus == i);
            DrawText(g, _rows[i].Label, label, Label, new RectangleF(r.X + 16 * s, r.Y, r.Width / 2, r.Height), v: StringAlignment.Center, wrap: false);
            var right = new RectangleF(r.Right - 56 * s, r.Y, 52 * s, r.Height);
            var box = new RectangleF(right.X - 260 * s, r.Y, 260 * s, r.Height);
            var left = new RectangleF(box.X - 52 * s, r.Y, 52 * s, r.Height);
            DrawText(g, "‹", arrow, Accent, left, StringAlignment.Center, StringAlignment.Center);
            DrawText(g, _rows[i].Value(), value, Color.White, box, StringAlignment.Center, StringAlignment.Center, wrap: false);
            DrawText(g, "›", arrow, Accent, right, StringAlignment.Center, StringAlignment.Center);
            _hits.Add((left, () => Change(row, -1), row));
            _hits.Add((right, () => Change(row, 1), row));
            _hits.Add((r, () => Change(row, 1), row));
            y += 51 * s;
        }
        y += 8 * s;
        float bw = (w - 6 * s * (ChoiceButtons.Length - 1)) / ChoiceButtons.Length;
        for (int i = 0; i < ChoiceButtons.Length; i++)
        {
            int focus = _rows.Count + i;
            var r = new RectangleF(x + i * (bw + 6 * s), y, bw, 62 * s);
            Box(g, r, _focus == focus);
            DrawText(g, ChoiceButtons[i], button, Color.White, r, StringAlignment.Center, StringAlignment.Center);
            _hits.Add((r, ChoiceAction(i), focus));
        }
        y += 70 * s;
        DrawText(g, "Arrows / D-pad: move    left / right or Enter: change    Enter / A: press    Esc / B: back    or click",
            hint, Muted, new RectangleF(x, y, w, 22 * s));
    }

    void PaintHosting(Graphics g, float x, float y, float w)
    {
        float s = S;
        using Font head = Font(22, bold: true), body = Font(17), button = Font(17);
        DrawText(g, "Hosting a LAN match", head, Color.White, new RectangleF(x, y, w, 34 * s));
        y += 44 * s;
        float h = MeasureHeight(g, _status, body, w) + 8 * s;
        DrawText(g, _status, body, Label, new RectangleF(x, y, w, h));
        y += h + 16 * s;
        var r = new RectangleF(x, y, 220 * s, 56 * s);
        Box(g, r, _focus == 0);
        DrawText(g, "Cancel", button, Color.White, r, StringAlignment.Center, StringAlignment.Center);
        _hits.Add((r, CancelLan, 0));
    }

    void PaintJoining(Graphics g, float x, float y, float w)
    {
        float s = S;
        using Font head = Font(22, bold: true), body = Font(17), button = Font(17), mono = Font(18, mono: true);
        DrawText(g, "Join a LAN match", head, Color.White, new RectangleF(x, y, w, 34 * s));
        y += 44 * s;
        DrawText(g, _status, body, Label, new RectangleF(x, y, w, 28 * s));
        y += 36 * s;
        for (int i = 0; i < _hosts.Count; i++)
        {
            IPEndPoint target = _hosts[i].Address;
            var r = new RectangleF(x, y, w, 46 * s);
            Box(g, r, _focus == i);
            DrawText(g, _hosts[i].Label, body, Color.White, new RectangleF(r.X + 16 * s, r.Y, r.Width - 32 * s, r.Height), v: StringAlignment.Center, wrap: false);
            _hits.Add((r, () => JoinTarget(target), i));
            y += 51 * s;
        }
        y += 8 * s;
        int field = _hosts.Count;
        var f = new RectangleF(x, y, 420 * s, 46 * s);
        Box(g, f, _focus == field);
        string shown = _address.Length > 0 ? _address + (_focus == field ? "_" : "") : "or type the host's address, e.g. 10.0.0.23";
        DrawText(g, shown, _address.Length > 0 ? mono : body, _address.Length > 0 ? Color.White : Muted,
            new RectangleF(f.X + 14 * s, f.Y, f.Width - 28 * s, f.Height), v: StringAlignment.Center, wrap: false);
        _hits.Add((f, () => { }, field));
        var join = new RectangleF(f.Right + 8 * s, y, 220 * s, 46 * s);
        Box(g, join, _focus == field + 1);
        DrawText(g, "Join this address", button, Color.White, join, StringAlignment.Center, StringAlignment.Center);
        _hits.Add((join, JoinTyped, field + 1));
        y += 58 * s;
        var cancel = new RectangleF(x, y, 220 * s, 56 * s);
        Box(g, cancel, _focus == field + 2);
        DrawText(g, "Cancel", button, Color.White, cancel, StringAlignment.Center, StringAlignment.Center);
        _hits.Add((cancel, CancelLan, field + 2));
    }
}
