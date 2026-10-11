using System;
using System.Collections.Generic;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;
using MphRecomp.Online.Voice;

// ONLINE PLAY's voice controls (Voice track, milestone O8; docs/ONLINE_PLAY.md section 8 and decision 6; the voice itself is
// Core/Online/Voice/VoiceChat.cs). The lobby's seat cards say "talking" while someone is heard (you: while your voice goes out)
// and "voice off" for a player voice is off with (a stranger while strangers are off), beside chat's "muted"; under START a
// TALK bar for touch screens (push to talk: a tap starts, a tap stops, a minute at most; a pad holds R3). The CHAT page's
// bottom has three bars under the panels: VOICE (push to talk -> open mic -> off), STRANGERS (on / off; friends
// stay on), VOLUME (how loud the others are). The license's VOICE (beside BLOCK) turns voice on or off with that one player,
// over the friends / strangers default. MUTE (chat's, the license's) silences a player's voice too (PlayerMutes); REPORT A
// HUNTER has "their voice". Kept in online_voice.json (VoiceSettings), changed here on the menu thread; the voice threads
// read it.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        // our callbacks (2789-2799, from chat's range)
        private const int VcModeCall = 2789, VcStrangersCall = 2790, VcVolumeCall = 2791, LicVoiceCall = 2792, LbTalkCall = 2793;
        private const int VoiceFirstCall = VcModeCall, VoiceLastCall = 2799;
        // the CHAT page's voice bars (mockup px): a row under the panels, level with the lobby's CHAT bar, x 1050..1850 (left of
        // x ~1000 the layout's back corner takes taps)
        private const float VcBarsY = 922, VcBarH = 70, VcBarW = 260, VcBarGap = 10;
        // the lobby's TALK (mockup px): under the GAME panel, level with CHAT (922..992)
        private const float LbTalkX0 = 1450, LbTalkX1 = 1830, LbTalkY = 922, LbTalkH = 70;
        private const long TalkLatchMs = 60_000;
        private static readonly float[] Volumes = { 0.5f, 0.75f, 1, 1.5f, 2 };

        private int _vcMode = -1, _vcStrangers = -1, _vcVolume = -1, _liVoice = -1, _lbTalk = -1;
        private long _talkSince; // the lobby TALK's tap-on time (0 = not latched)
        private OnlineMatch? _voiceLobby; // the lobby this menus instance last gave VoiceChat.Lobby

        // every menu frame: VoiceChat follows our lobby (hosted or joined) before START sets OnlineMatch.Current; only the
        // instance that set it clears it (the in-match pause menus have no lobby of their own)
        private void PublishLobbyVoice()
        {
            OnlineMatch? m = _hosting ?? _joined;
            if (m == _voiceLobby) return;
            if (m != null || VoiceChat.Lobby == _voiceLobby) VoiceChat.Lobby = m;
            _voiceLobby = m;
        }

        private static float VcBarX(int k) => 1050 + k * (VcBarW + VcBarGap);

        // InstallChat, last: the voice bars on the CHAT page, the lobby's TALK, the license's VOICE (right of BLOCK)
        private void InstallVoice(MenuPage chat, Func<int, float, float, float, float, int, MenuItem> input)
        {
            MenuItem Px(MenuPage page, float x, float y, float w, float h, int call) => page.AddItem(i => input(i, Mx(x), My(y), Mx(x + w), My(y + h), call));

            _vcMode = Px(chat, VcBarX(0), VcBarsY, VcBarW, VcBarH, VcModeCall).Index;
            _vcStrangers = Px(chat, VcBarX(1), VcBarsY, VcBarW, VcBarH, VcStrangersCall).Index;
            _vcVolume = Px(chat, VcBarX(2), VcBarsY, VcBarW, VcBarH, VcVolumeCall).Index;
            if (s_onLobby >= 0) _lbTalk = Px(_file.Pages[s_onLobby], LbTalkX0, LbTalkY, LbTalkX1 - LbTalkX0, LbTalkH, LbTalkCall).Index;
            if (s_onLicense >= 0) _liVoice = Px(_file.Pages[s_onLicense], 1525, LicenseButtonY(2), 285, 66, LicVoiceCall).Index;
        }

        private void ApplyVoiceSounds(Action<int, int, int, int> like)
        {
            foreach (int b in new[] { _vcMode, _vcStrangers, _vcVolume }) like(s_onChat, b, OptionBoxPage, _soundToggle);
            like(s_onLobby, _lbTalk, OptionBoxPage, _soundToggle);
            like(s_onLicense, _liVoice, OptionBoxPage, _soundToggle);
        }

        // every menu frame on the lobby: TALK's button follows LobbyTalkShown as its drawing does (ShowLobby only runs on
        // lobby changes, before the voice chat is up or someone joins: TALK was drawn but its button stayed hidden, so taps
        // went nowhere; owner 2026-10-11 "doesnt appear to be real")
        private void SyncLobbyTalk()
        {
            if (_lbTalk < 0 || _onlineMenu is not { } menu || menu.Page?.Index != s_onLobby || menu.PendingPage != -1) return;
            bool shown = LobbyTalkShown();
            if ((menu.ItemState(_lbTalk) != MenuState.Hidden) == shown) return;
            Show(menu, _lbTalk, shown);
            if (!shown && _talkSince != 0)
            {
                VoiceChat.HoldToTalk(false);
                _talkSince = 0;
            }
        }

        // ShowLobby: TALK is there in push to talk with someone else in the game
        private bool LobbyTalkShown()
        {
            if (_lbTalk < 0 || VoiceChat.Current == null || VoiceSettings.Current.Mode != VoiceMode.PushToTalk) return false;
            return _hosting == null ? _joined != null : _hosting.Net.Joined > 0;
        }

        private void VoiceCall(int call)
        {
            VoiceSettings s = VoiceSettings.Current;
            switch (call)
            {
            case VcModeCall:
                s.SetMode(s.Mode switch { VoiceMode.PushToTalk => VoiceMode.OpenMic, VoiceMode.OpenMic => VoiceMode.Off, _ => VoiceMode.PushToTalk });
                break;
            case VcStrangersCall:
                s.SetStrangers(!s.Strangers);
                break;
            case VcVolumeCall:
            {
                int at = Array.FindIndex(Volumes, v => v >= s.Volume - 0.01f);
                s.SetVolume(Volumes[(at + 1) % Volumes.Length]);
                break;
            }
            case LicVoiceCall:
                if (_licCard is PlayerCard c) TogglePlayerVoice(c);
                break;
            case LbTalkCall:
            {
                bool on = VoiceChat.Current?.PushToTalkHeld != true;
                if (VoiceChat.HoldToTalk(on)) _talkSince = on ? Math.Max(1, Environment.TickCount64) : 0;
                break;
            }
            }
        }

        // the license's VOICE: on / off with this player, over the friends / strangers default (back to the default = no choice)
        private void TogglePlayerVoice(PlayerCard c)
        {
            if (PlayerMutes.Current.Voice(c.Code))
            {
                Import.Popup?.Inform("they're muted.\n\nunmute them first: their voice comes back with their chat.");
                return;
            }
            VoiceSettings s = VoiceSettings.Current;
            bool usual = c.Friend ? s.Friends : s.Strangers;
            bool on = !(s.Player(c.Code) ?? usual);
            s.SetPlayer(c.Code, on == usual ? null : on);
        }

        // the lobby TALK's tap-on lets go after a minute (and the match's R3 / TALK takes over at the start)
        private void TickTalkLatch()
        {
            if (_talkSince == 0) return;
            VoiceChat? v = VoiceChat.Current;
            if (v == null || !v.PushToTalkHeld) _talkSince = 0;
            else if (Environment.TickCount64 - _talkSince > TalkLatchMs)
            {
                VoiceChat.HoldToTalk(false);
                _talkSince = 0;
            }
        }

        // ---- drawing ----

        // DrawLobbyChat: "talking" / "voice off" on the seat cards (chat's "muted" takes the same spot: a muted player is
        // silent), and TALK
        private void DrawLobbyVoice(Dictionary<int, List<WidgetTri>> glows, List<LobbySeat> seats)
        {
            TickTalkLatch();
            VoiceChat? voice = VoiceChat.Current;
            if (voice == null || VoiceSettings.Current.Mode == VoiceMode.Off) return;
            PlayerMutes mutes = PlayerMutes.Current;
            for (int k = 0; k < seats.Count; k++)
            {
                LobbySeat seat = seats[k];
                string? word = null;
                (float, float, float) colour = Green;
                if (seat.Kind == SeatKind.You) word = voice.Transmitting ? "talking" : null;
                else if (seat.Kind == SeatKind.Person && seat.Card is PlayerCard c && !mutes.Any(c.Code))
                {
                    if (voice.Speaking(c.Code)) word = "talking";
                    else if (!VoiceChat.AllowedFor(c.Code, c.Friend))
                    {
                        word = "voice off";
                        colour = OnGrey;
                    }
                }
                if (word == null) continue;
                (float lx, float ly) = LobbyCardLabel(k, seats.Count); // (P8: 5-8 seats, OnlineMenus.Seats8.cs)
                TextS(word, lx, ly, 1, colour, LobbySeatItem(k, seats.Count), SizeX);
            }
            if (_lbTalk >= 0 && LobbyTalkShown())
            {
                bool held = voice.PushToTalkHeld;
                Bar(Mx(LbTalkX0), My(LbTalkY), Mx(LbTalkX1), My(LbTalkY + LbTalkH), _lbTalk, glows);
                TextS(held ? "talking: tap to stop" : "tap to talk", Mx((LbTalkX0 + LbTalkX1) / 2), My(LbTalkY + 21), 2,
                    held ? Green : Orange, _lbTalk, SizeX);
            }
        }

        // DrawChat, last: the VOICE, STRANGERS and VOLUME bars
        private void DrawChatVoice(Dictionary<int, List<WidgetTri>> glows)
        {
            if (_vcMode < 0) return;
            TickTalkLatch();
            VoiceSettings s = VoiceSettings.Current;
            string mode = s.Mode switch { VoiceMode.PushToTalk => "push to talk", VoiceMode.OpenMic => "open mic", _ => "voice off" };
            VoiceBar(0, _vcMode, mode, s.Mode == VoiceMode.Off ? OnGrey : Orange, glows);
            VoiceBar(1, _vcStrangers, s.Strangers ? "strangers on" : "strangers off", s.Strangers ? Orange : OnGrey, glows);
            VoiceBar(2, _vcVolume, $"volume {(int)MathF.Round(s.Volume * 100)}%", Orange, glows);
        }

        private void VoiceBar(int k, int item, string label, (float R, float G, float B) colour, Dictionary<int, List<WidgetTri>> glows)
        {
            float x = VcBarX(k);
            Bar(Mx(x), My(VcBarsY), Mx(x + VcBarW), My(VcBarsY + VcBarH), item, glows);
            TextS(FitWidth(label, Mw(VcBarW - 16), SizeX), Mx(x + VcBarW / 2), My(VcBarsY + 21), 2, colour, item, SizeX);
        }

        // the license's VOICE (DrawLicense, right of the half-width BLOCK)
        private void DrawLicenseVoice(PlayerCard c, Dictionary<int, List<WidgetTri>> glows)
        {
            if (_liVoice < 0) return;
            bool muted = PlayerMutes.Current.Voice(c.Code);
            bool on = !muted && (VoiceSettings.Current.Player(c.Code) ?? (c.Friend ? VoiceSettings.Current.Friends : VoiceSettings.Current.Strangers));
            float y = LicenseButtonY(2);
            Bar(Mx(1525), My(y), Mx(1810), My(y + 66), _liVoice, glows);
            TextS(muted ? "voice muted" : on ? "voice on" : "voice off", Mx(1667), My(y + 19), 2, on ? Orange : Pale, _liVoice, SizeM);
        }
    }
}
