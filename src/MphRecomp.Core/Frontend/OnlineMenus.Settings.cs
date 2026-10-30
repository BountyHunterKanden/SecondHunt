using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MphRecomp.Online;
using MphRecomp.Online.Voice;
using MphRecomp.Config;

// ONLINE PLAY's ONLINE SETTINGS (owner 2026-10-09: "finish everything you can"; pick 9 findable by
// name ON by default with a switch; decision 3's backup / transfer code). The hub's SETTINGS (its bottom-right corner, where
// REPLAYS keeps "record replays") opens one page of rows in the WHO CAN JOIN look: FINDABLE BY NAME on / off (the server's
// search: its value comes with the login, a switch sends SetFindableReq), YOUR FRIEND CODE, BACKUP CODE (shown on a tap:
// it moves this online profile to another device), RESTORE FROM A BACKUP CODE (the name keyboard, then this device becomes
// that hunter: OnlineIdentity saved over this one after a yes), ABOUT ONLINE PLAY (the first-visit notice again), DELETE ONLINE
// PROFILE (two yeses, server 0.2.0: the server forgets this player; this device's identity and head to head go too, and the
// menus leave ONLINE PLAY so a new profile isn't made at once).
// No popup on the first visit (owner 10-09: no popups; the hub has a dim age line and OPTIONS' bottom-left line opens the
// privacy / code of conduct page); NoticeSeen is still kept in online_settings.json beside the license. DRAFT text: the
// owner words it.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private static int s_onSettings = -1;

        // our callbacks (2700-2719)
        private const int StRowCall = 2700; // + the row
        private const int StOpenCall = 2715; // (was 2710: the 12 rows reach 2711)
        private const int SettingsFirstCall = StRowCall, SettingsLastCall = StOpenCall;
        // (StNoPickups / StNoCharacters: the Extra pickups + Extra characters filters, owner 2026-10-10; StNoArenas: the Extra
        // arenas one, N28)
        // (StVoice / StPtt / StChat: VOICE CHAT on / off, PUSH TO TALK on / off = open mic, CHAT on / off; release 0.2.0 F2,
        // VoiceSettings: the one setting the lobby and the match both use)
        // (StDuck: DUCK MUSIC, the music fades down while others talk; Thread C, C3, 2026-10-23: 13 rows, the pitch 54)
        private const int StRows = 13, StFindable = 0, StVoice = 1, StPtt = 2, StChat = 3, StDuck = 4, StNoPickups = 5, StNoCharacters = 6,
            StNoArenas = 7, StCode = 8, StBackup = 9, StRestore = 10, StAbout = 11, StDelete = 12;
        // the hub's SETTINGS, in MenuLayout.Replays' bottom-right corner region (x 330..462, y -200..-182)
        private const float StButtonX0 = 398, StButtonX1 = 458, StButtonTop = -185, StButtonBottom = -198;

        private int _stPanel = -1, _stTitle = -1, _hSettings = -1;
        private readonly int[] _stRow = Unset(StRows);
        private bool _stShowBackup;
        private string? _stBackupText, _stCodeText; // (read from the identity file as they're shown, not every frame)
        private OnlineSettingsFile? _onlineSettings;
        private bool _findableSending;

        // tools: no first-visit notice in the PC tests' sessions (it would take their key presses)
        internal static bool OnlineNoticeSkipped { get; set; }
        internal static int OnlineSettingsPage => s_onSettings;
        internal void OnlineShowNotice() => ShowNotice();

        private OnlineSettingsFile OnlineSettings => _onlineSettings ??= OnlineSettingsFile.Load(ProfileDir);

        // (thread D 10-24, M8b: the page is a SECOND HUNT SETTINGS section page now, its rows under headers as the other
        // tabs': RecompMenus.SettingsPages.cs' OnlineEntries, installed before this)
        private void InstallSettings(Func<int, MenuAction[], MenuPage> addFramePage, Func<int, float, float, float, float, int, MenuItem> input,
            Func<int, float, float, MenuItem> panelAt, Action<MenuPage, int> back, MenuItem title, MenuPage hub)
        {
            s_onSettings = _onlineHubPage?.Page ?? -1;
            _hSettings = hub.AddItem(i => input(i, StButtonX0, StButtonTop + 1, StButtonX1, StButtonBottom - 1, StOpenCall)).Index;
        }

        // mockup px from one row's top to the next, the first row's top and a row's height (12 rows in the panel: release
        // 0.2.0 F2; was 77 / 212 / 71 for 9, 118 / 222 / 104 for 6, 86 / 214 / 78 for 8). StPitch stays for REPORT A HUNTER.
        private const float StPitch = 77, StRowPitch = 54, StFirstTop = 206, StRowHeight = 50;

        private static (float X0, float Top, float X1, float Bottom) SettingsRowRect(int row)
            => (Mx(100), My(StFirstTop + StRowPitch * row), Mx(1820), My(StFirstTop + StRowHeight + StRowPitch * row));

        private void ApplySettingsSounds(Action<int, int, int, int> like)
        {
            foreach (int r in _stRow) if (r >= 0) like(s_onSettings, r, OptionBoxPage, _soundToggle);
            like(s_onHub, _hSettings, MultiplayerMenu.CreateJoinPage, _soundListRow);
        }

        private void ShowSettings(MenuEngine menu, bool focus)
        {
            if (focus)
            {
                _stShowBackup = false;
                _stBackupText = null;
                _stCodeText = OnlineIdentity.Load(ProfileDir)?.FriendCode is string c ? FriendCodes.Format(c) : null;
                StreamerPrivacy.SetOwnCode(_stCodeText?.Replace("-", "")); // (STREAMING: this device's code; masked where drawn)
            }
        }

        private void SettingsCall(MenuEngine menu, int call)
        {
            if (call == StOpenCall) menu.GoTo(s_onSettings);
            else OnlineRow(menu, call - StRowCall, s_onSettings);
        }

        // one ONLINE SETTINGS row run (ONLINE PLAY's page and SECOND HUNT SETTINGS' ONLINE tab, page: the one it's on); the
        // server's two (findable, delete) connect first wherever they are (owner 2026-10-30: "find profile by name and delete
        // profile should not be locked by needing to go to a differnet menu changing those settings should connect to the
        // serer"; SECOND HUNT SETTINGS' rows used to send the player to online play)
        private void OnlineRow(MenuEngine menu, int row, int page)
        {
            if ((row is StFindable or StDelete) && _client is not { Connected: true })
            {
                ConnectThen(() => OnlineRow(menu, row, page));
                return;
            }
            switch (row)
            {
            case StFindable:
                if (!Reachable() || _findableSending) break;
                SendFindable(_client!, !OnlineSettings.Findable);
                break;
            case StVoice:
                VoiceSettings.Current.SetOn(VoiceSettings.Current.Mode == VoiceMode.Off);
                break;
            case StPtt:
                VoiceSettings.Current.SetPushToTalk(VoiceSettings.Current.OnMode != VoiceMode.PushToTalk);
                break;
            case StChat:
                VoiceSettings.Current.SetChat(!VoiceSettings.Current.Chat);
                break;
            case StDuck:
                VoiceSettings.Current.SetDuckMusic(!VoiceSettings.Current.DuckMusic);
                break;
            case StNoPickups:
                OnlineSettings.NoExtraPickups = !OnlineSettings.NoExtraPickups;
                OnlineSettings.Save(ProfileDir);
                break;
            case StNoCharacters:
                OnlineSettings.NoExtraCharacters = !OnlineSettings.NoExtraCharacters;
                OnlineSettings.Save(ProfileDir);
                break;
            case StNoArenas:
                OnlineSettings.NoExtraArenas = !OnlineSettings.NoExtraArenas;
                OnlineSettings.Save(ProfileDir);
                break;
            case StBackup:
                if (StreamerPrivacy.HideCode && !_stShowBackup)
                {
                    // STREAMING (owner 10-28): the backup code stays hidden with the friend code
                    Import.Popup?.Inform(StreamerPrivacy.CodeLocked);
                    break;
                }
                _stShowBackup = !_stShowBackup;
                _stBackupText = _stShowBackup ? OnlineIdentity.LoadOrCreate(ProfileDir).BackupCode.ToLowerInvariant() : null;
                break;
            case StRestore:
                if (StreamerPrivacy.HideCode)
                {
                    Import.Popup?.Inform(StreamerPrivacy.CodeLocked); // (STREAMING: typing a backup code shows it)
                    break;
                }
                if (EditText == null)
                {
                    Import.Popup?.Inform(FrontendSession.WorkInProgressText);
                    break;
                }
                EditText(menu, "", BackupCodeMax, page, text =>
                {
                    if (text != null) Restore(text);
                });
                break;
            case StAbout:
                ShowNotice();
                break;
            case StDelete:
                AskDelete(menu);
                break;
            }
        }

        private Action? _whenConnected; // a server row (findable, delete) waiting on the connection (ConnectThen)

        // a server row with no connection: "connecting..." (NO stops waiting), then the row runs once RefreshOnline is in
        // (RunWhenConnected), or the box says why not. Leaving the page closes the connection again (OnlineMenus' page change)
        private void ConnectThen(Action then)
        {
            if (Import.Popup == null) return;
            _whenConnected = then;
            Import.Popup.BusyCancel("connecting to the server...", () =>
            {
                _whenConnected = null;
                Import.Popup?.Close();
            });
            if (!_connecting) RefreshOnline(lists: false);
            if (!_connecting && _client is not { Connected: true })
            {
                // (RefreshOnline didn't start one: a join or a hosted game has the menus)
                _whenConnected = null;
                Import.Popup.Inform("can't reach the server.\n\ntry again in a moment.");
            }
        }

        // the connection is up (RefreshOnline): the row that waited on it, if one did
        private bool RunWhenConnected()
        {
            if (_whenConnected is not Action then) return false;
            _whenConnected = null;
            Import.Popup?.Close();
            then();
            return true;
        }

        // it couldn't connect: the row that waited says why
        private void FailWhenConnected(string? why)
        {
            if (_whenConnected == null) return;
            _whenConnected = null;
            Import.Popup?.Inform("can't reach the server.\n\n" + (why ?? "try again in a moment."));
        }

        // DELETE ONLINE PROFILE: two yeses, then the server forgets this player (DeleteProfileReq)
        private void AskDelete(MenuEngine menu)
        {
            if (!Reachable() || Import.Popup == null) return;
            OnlineClient client = _client!;
            if (!client.ServerAtLeast(DeleteProfileReq.SinceServer))
            {
                Import.Popup.Inform("deleting a profile needs a server update.");
                return;
            }
            Import.Popup.Ask("delete your online profile?\n\nyour name, friend code, friends, rivals and license leave the server.", yes =>
            {
                if (!yes) return;
                // (owner 2026-10-30: a second box "stating this cant be undone and account cannot be recovered")
                Import.Popup?.Ask("are you sure?\n\nthis can't be undone. your profile can't be recovered, not even with your backup code.", sure =>
                {
                    if (sure) DeleteProfile(menu, client);
                });
            });
        }

        private void DeleteProfile(MenuEngine menu, OnlineClient client)
        {
            Import.Popup?.BusyCancel("deleting your online profile...", () => { });
            Task.Run(async () =>
            {
                try
                {
                    await client.RequestAsync<OkMsg>(new DeleteProfileReq());
                    Post(() =>
                    {
                        // this device forgets it too: the next visit makes a new hunter
                        string? dir = ProfileDir;
                        foreach (string file in new[] { OnlineIdentity.FileName, HeadToHeadStore.FileName })
                        {
                            try
                            {
                                if (dir != null) File.Delete(Path.Combine(dir, file));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                Console.Error.WriteLine($"online: {file} not deleted: {ex.Message}");
                            }
                        }
                        _headToHead = null;
                        CloseOnline();
                        _roster = null;
                        _recent = new List<PlayerCard>();
                        _friendsOn = new List<PlayerCard>();
                        _found = null;
                        s_licenseSent = null;
                        Import.Popup?.Close();
                        menu.GoTo(MultiplayerMenu.TypePage); // (out of ONLINE PLAY: no new profile until the next visit)
                        Import.Popup?.Inform("your online profile is deleted.\n\nnext time you go online, you start as a new hunter.");
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() => Import.Popup?.Inform("couldn't delete it.\n\n" + Problem(ex)));
                }
            });
        }

        // a backup code as typed (with or without its dashes)
        private const int BackupCodeMax = 34;

        // RESTORE: the typed code's hunter becomes this device's online profile, after a yes (the old one is gone unless its
        // own backup code was kept)
        private void Restore(string text)
        {
            OnlineIdentity? id = OnlineIdentity.FromBackupCode(text);
            if (id == null)
            {
                Import.Popup?.Inform("that isn't a backup code.\n\ncheck it and try again.");
                return;
            }
            string? dir = ProfileDir;
            if (OnlineIdentity.Load(dir)?.FriendCode == id.FriendCode)
            {
                Import.Popup?.Inform("that's this device's own code.");
                return;
            }
            Import.Popup?.Ask($"become hunter {(StreamerPrivacy.HideCode ? StreamerPrivacy.CodeMask : FriendCodes.Format(id.FriendCode))} here?\n\nthis device's profile is lost unless you kept its backup code.", yes =>
            {
                if (!yes) return;
                id.Save(dir);
                CloseOnline();
                _roster = null;
                _recent = new List<PlayerCard>();
                _friendsOn = new List<PlayerCard>();
                s_licenseSent = null;
                _stCodeText = FriendCodes.Format(id.FriendCode);
                StreamerPrivacy.SetOwnCode(id.FriendCode);
                if (_stepMenu?.Page is { } now && IsOnline(now.Index)) RefreshOnline(lists: true); // (not from the ONLINE tab)
                RefreshSettingsPages();
                Import.Popup?.Inform("done.\n\nthis device plays as that hunter now.");
            });
        }

        // ABOUT ONLINE PLAY. DRAFT wording for the owner (who sees what, blocking, the age line,
        // what the server keeps); each box holds about seven short lines
        private static readonly string[] NoticeText =
        {
            "welcome to online play.\n\nother hunters see your name and your hunter license.",
            "you can block anyone from their license.\n\nonline play is for ages 13 and up.",
            "the server keeps your name, friend code, license, friends, blocks and recent players.",
            "privacy and the code of conduct:\n\noptions, bottom left.",
        };

        private void ShowNotice(int part = 0)
        {
            if (Import.Popup == null) return;
            if (part < NoticeText.Length)
            {
                Import.Popup.Inform(NoticeText[part], () => ShowNotice(part + 1));
            }
            else if (part == NoticeText.Length && Me().Name == MultiplayerMenu.DefaultNickname)
            {
                // still vanilla's default name: everyone would meet a crowd of HUNTER#tags
                Import.Popup.Inform("you're playing as hunter.\n\nset your own name on the hunter license: edit name.");
            }
        }

        // the hub's first time: marked seen, no popup (owner 10-09)
        private void FirstVisit()
        {
            if (OnlineNoticeSkipped || OnlineSettings.NoticeSeen) return;
            OnlineSettings.NoticeSeen = true;
            OnlineSettings.Save(ProfileDir);
        }

        // FINDABLE BY NAME switched: the server's search, then the page (and the file, for when there's no connection)
        private void SendFindable(OnlineClient client, bool findable)
        {
            _findableSending = true;
            Task.Run(async () =>
            {
                try
                {
                    await client.RequestAsync<OkMsg>(new SetFindableReq { Findable = findable });
                    Post(() =>
                    {
                        _findableSending = false;
                        KnowFindable(findable);
                        Redraw();
                        RefreshSettingsPages();
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() =>
                    {
                        _findableSending = false;
                        Import.Popup?.Inform("couldn't change it.\n\n" + Problem(ex));
                    });
                }
            });
        }

        // the server's FINDABLE BY NAME (its login says it; a switch sets it), kept in the file
        private void KnowFindable(bool findable)
        {
            if (OnlineSettings.Findable == findable) return;
            OnlineSettings.Findable = findable;
            OnlineSettings.Save(ProfileDir);
        }

        private void DrawSettings(Dictionary<int, List<WidgetTri>> glows)
        {
            OnlineSettingsFile st = OnlineSettings;
            PlayerCard? me = MeCard;
            string code = me != null && me.Code.Length > 0 ? FriendCodes.Format(me.Code) : _stCodeText ?? "--";
            if (StreamerPrivacy.HideCode && code != "--") code = StreamerPrivacy.CodeMask; // (STREAMING: always this device's)
            string backup = _stBackupText ?? "";
            VoiceSettings v = VoiceSettings.Current;
            static (string, string, string?) Toggle(string label, string own, bool on) =>
                (label, SettingHints.Online(label, on ? "on" : "off", own), on ? "on" : "off");
            (string Label, string Line, string? Value)[] rows =
            {
                // (F2: each switch's line describes the value it's on: SettingHints' "online.<label>.<on|off>")
                Toggle("findable by name", "others can find you when they search your name.", st.Findable),
                Toggle("voice chat", "talk and listen in online lobbies and matches.", v.Mode != VoiceMode.Off),
                ("push to talk", SettingHints.Online("push to talk", v.OnMode == VoiceMode.PushToTalk ? "on" : "off",
                        v.OnMode == VoiceMode.PushToTalk ? "hold r3 or tap talk to speak." : "off: open mic, your voice goes out when you speak.")
                    + (v.Mode == VoiceMode.Off ? " (voice chat is off)" : ""), v.OnMode == VoiceMode.PushToTalk ? "on" : "off"),
                Toggle("chat", "see and send chat messages in online lobbies and matches.", v.Chat),
                Toggle("duck music", "the music fades down while others talk, and comes back after.", v.DuckMusic),
                Toggle("no extra pickup games", "don't list or join games with extra pickups (the pirate blaster).", st.NoExtraPickups),
                Toggle("no extra character games", "don't list or join games with the trooper or dark samus.", st.NoExtraCharacters),
                Toggle("no extra arena games", "don't list, join or search games in imported arenas (echoes).", st.NoExtraArenas),
                ("your friend code", "give it to a friend: enter each other's to be friends.", null),
                ("backup code", _stShowBackup ? backup : "moves your online profile to another device. keep it secret.", _stShowBackup ? "hide" : "show"),
                ("restore from a backup code", "play as your hunter from another device.", "enter"),
                ("about online play", "who sees what, and what the server keeps.", "read"),
                ("delete online profile", "the server forgets your name, friend code, friends and license.", "delete"),
            };
            for (int r = 0; r < StRows; r++)
            {
                int item = _stRow[r] >= 0 ? _stRow[r] : _stTitle;
                (float x0, float top, float x1, float bottom) = SettingsRowRect(r);
                float by = StFirstTop + StRowPitch * r; // the row's top in mockup pixels
                if (_stRow[r] >= 0) Bar(x0, top, x1, bottom, item, glows);
                else
                {
                    Fill(x0, top, x1, bottom, 0, 0, 0, 0.35f, item);
                    Box(x0, top, x1, bottom, item);
                }
                TextS(rows[r].Label, Mx(130), My(by + 2), 0, r == StDelete ? Red : Orange, item, SizeM);
                bool code1 = r == StBackup && _stShowBackup;
                TextS(Fit(rows[r].Line, code1 ? 48 : 80), Mx(130), My(by + 30), 0, code1 ? Cyan : White, item, code1 ? SizeM : SizeX);
                if (r == StCode) TextS(code, Mx(1790), My(by + 14), 1, Cyan, item, SizeS);
                else if (r is StFindable or StVoice or StPtt or StChat or StDuck or StNoPickups or StNoCharacters or StNoArenas)
                {
                    ValueBox(Mx(1600), My(by + 6), Mx(1780), My(by + 48), rows[r].Value!, item, glows);
                }
                else TextS(rows[r].Value!, Mx(1780), My(by + 16), 1, r == StDelete ? Red : Pale, item, SizeM);
            }
        }

        // the hub's SETTINGS in its corner (as REPLAYS' "record replays")
        private void DrawSettingsButton(Dictionary<int, List<WidgetTri>> glows)
        {
            if (_hSettings < 0) return;
            Fill(StButtonX0, StButtonTop, StButtonX1, StButtonBottom, 0, 0, 0, 0.43f, _hSettings);
            Box(StButtonX0, StButtonTop, StButtonX1, StButtonBottom, _hSettings);
            Glow(glows, _hSettings, StButtonX0, StButtonTop, StButtonX1, StButtonBottom, _hSettings);
            Text("settings", (StButtonX0 + StButtonX1) / 2, StButtonTop - 2.5f, 2, Orange, _hSettings);
        }

        // ONLINE SETTINGS' file (beside the license): findable by name (pick 9: on by default), the first-visit notice seen
        internal sealed class OnlineSettingsFile
        {
            public const string FileName = "online_settings.json";
            public bool Findable { get; set; } = true;
            public bool NoticeSeen { get; set; }
            // the Extra pickups + Extra characters filters (owner 2026-10-10): don't list or quick-join games that allow them
            public bool NoExtraPickups { get; set; }
            public bool NoExtraCharacters { get; set; }
            // ... and the Extra arenas one (N28): no games in imported arenas (a device without them never sees those anyway)
            public bool NoExtraArenas { get; set; }
            // FIND GAME's playlist (owner 2026-10-10, N17): the modes (FindMode names) a search asks for; null = all seven
            public List<string>? FindModes { get; set; }

            public static OnlineSettingsFile Load(string? dir)
            {
                string? path = dir == null ? null : Path.Combine(dir, FileName);
                try
                {
                    if (path != null && File.Exists(path)) return JsonSerializer.Deserialize<OnlineSettingsFile>(File.ReadAllText(path)) ?? new();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
                {
                    Console.Error.WriteLine($"online: {path}: {ex.Message}");
                }
                return new OnlineSettingsFile();
            }

            public void Save(string? dir)
            {
                if (dir == null) return;
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(this));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"online: settings not saved: {ex.Message}");
                }
            }
        }
    }
}
