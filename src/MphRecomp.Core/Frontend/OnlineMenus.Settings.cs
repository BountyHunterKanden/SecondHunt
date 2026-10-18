using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MphRecomp.Online;
using MphRecomp.Online.Voice;

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
        private const int StRows = 12, StFindable = 0, StVoice = 1, StPtt = 2, StChat = 3, StNoPickups = 4, StNoCharacters = 5,
            StNoArenas = 6, StCode = 7, StBackup = 8, StRestore = 9, StAbout = 10, StDelete = 11;
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

        private void InstallSettings(Func<int, MenuAction[], MenuPage> addFramePage, Func<int, float, float, float, float, int, MenuItem> input,
            Func<int, float, float, MenuItem> panelAt, Action<MenuPage, int> back, MenuItem title, MenuPage hub)
        {
            MenuPage page = addFramePage(hub.Index, Array.Empty<MenuAction>());
            s_onSettings = page.Index;
            _stPanel = page.AddItem(i => panelAt(i, Mx(70), Mx(1850))).Index;
            back(page, hub.Index);
            _stTitle = page.AddItem(i => Clone(title, i, 0, 0, stringId: _strings.Add("ONLINE SETTINGS"))).Index;
            for (int r = 0; r < StRows; r++)
            {
                if (r == StCode) continue; // (shown, nothing to do)
                (float x0, float top, float x1, float bottom) = SettingsRowRect(r);
                int row = r;
                _stRow[r] = page.AddItem(i => input(i, x0, top, x1, bottom, StRowCall + row)).Index;
            }
            _hSettings = hub.AddItem(i => input(i, StButtonX0, StButtonTop + 1, StButtonX1, StButtonBottom - 1, StOpenCall)).Index;
        }

        // mockup px from one row's top to the next, the first row's top and a row's height (12 rows in the panel: release
        // 0.2.0 F2; was 77 / 212 / 71 for 9, 118 / 222 / 104 for 6, 86 / 214 / 78 for 8). StPitch stays for REPORT A HUNTER.
        private const float StPitch = 77, StRowPitch = 59, StFirstTop = 206, StRowHeight = 54;

        private static (float X0, float Top, float X1, float Bottom) SettingsRowRect(int row)
            => (Mx(100), My(StFirstTop + StRowPitch * row), Mx(1820), My(StFirstTop + StRowHeight + StRowPitch * row));

        private void ApplySettingsSounds(Action<int, int, int, int> like)
        {
            foreach (int r in _stRow) like(s_onSettings, r, OptionBoxPage, _soundToggle);
            like(s_onHub, _hSettings, MainMenuPage, _soundCampaign);
        }

        private void ShowSettings(MenuEngine menu, bool focus)
        {
            if (focus)
            {
                _stShowBackup = false;
                _stBackupText = null;
                _stCodeText = OnlineIdentity.Load(ProfileDir)?.FriendCode is string c ? FriendCodes.Format(c) : null;
                menu.SetState(_stRow[StFindable], MenuState.Focused);
            }
        }

        private void SettingsCall(MenuEngine menu, int call)
        {
            switch (call - StRowCall)
            {
            case StOpenCall - StRowCall:
                menu.GoTo(s_onSettings);
                return;
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
                _stShowBackup = !_stShowBackup;
                _stBackupText = _stShowBackup ? OnlineIdentity.LoadOrCreate(ProfileDir).BackupCode.ToLowerInvariant() : null;
                break;
            case StRestore:
                if (EditText == null)
                {
                    Import.Popup?.Inform(FrontendSession.WorkInProgressText);
                    break;
                }
                EditText(menu, "", BackupCodeMax, s_onSettings, text =>
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
                Import.Popup?.Ask("are you sure?\n\nthis can't be undone.", sure =>
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
            Import.Popup?.Ask($"become hunter {FriendCodes.Format(id.FriendCode)} here?\n\nthis device's profile is lost unless you kept its backup code.", yes =>
            {
                if (!yes) return;
                id.Save(dir);
                CloseOnline();
                _roster = null;
                _recent = new List<PlayerCard>();
                _friendsOn = new List<PlayerCard>();
                s_licenseSent = null;
                RefreshOnline(lists: true);
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
            string backup = _stBackupText ?? "";
            VoiceSettings v = VoiceSettings.Current;
            (string Label, string Line, string? Value)[] rows =
            {
                ("findable by name", "others can find you when they search your name.", st.Findable ? "on" : "off"),
                ("voice chat", "talk and listen in online lobbies and matches.", v.Mode != VoiceMode.Off ? "on" : "off"),
                ("push to talk", (v.OnMode == VoiceMode.PushToTalk ? "hold r3 or tap talk to speak." : "off: open mic, your voice goes out when you speak.")
                    + (v.Mode == VoiceMode.Off ? " (voice chat is off)" : ""), v.OnMode == VoiceMode.PushToTalk ? "on" : "off"),
                ("chat", "see and send chat messages in online lobbies and matches.", v.Chat ? "on" : "off"),
                ("no extra pickup games", "don't list or join games with extra pickups (the pirate blaster).", st.NoExtraPickups ? "on" : "off"),
                ("no extra character games", "don't list or join games with the trooper or dark samus.", st.NoExtraCharacters ? "on" : "off"),
                ("no extra arena games", "don't list, join or search games in imported arenas (echoes).", st.NoExtraArenas ? "on" : "off"),
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
                else if (r is StFindable or StVoice or StPtt or StChat or StNoPickups or StNoCharacters or StNoArenas)
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
