using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Config;

// OPTIONS > AUDIO ("ADJUST AUDIO OPTIONS", page 22): the game code behind the page's callbacks, which the menu data only
// names (ov0's page-22 switch, callback - 0x31; USA rev 0 and rev 1 use the same numbers; research in
// handoffs/beta-release-planning_2026-10-05_0300-files/audio_research.md): the speaker type, the SFX and music volumes
// (steps 0-9, heard at once) and the sound test (SFXSELECTLIST.DAT / BGMSELECTLIST.DAT). The recomp's changes: the MIC row (the DS microphone's level) is gone, and the music quality
// (RecompSettings.Music, GameAudio.cs MusicMode) sits in its place, so a music test song can be heard in every mode, live;
// and every change is saved at once (owner 2026-10-05: no SAVE; vanilla kept them until SAVE, B forgot them).
// The page's items are found by content (text, widget, callback), as RecompMenus and FileSelect find theirs.
namespace MphRecomp.Frontend
{
    // one sound test entry (a 16-byte record): Kind -- music: 0 sequence, 1 MusicId, 2 stream; sfx: 0 sample, 1 DGN,
    // 2 script, 3 stream (the announcer's voices, STRM 0-9) -- and Param (+8: a script's full DS rate when 1, a DGN's curve
    // input 0-0xFFFF)
    public readonly record struct SoundTestEntry(int Id, int Kind, int Bits, int Param, string Name);

    public sealed class AudioOptions
    {
        public const int Page = 22;

        private const int CallSave = 49, CallStereoIcon = 50, CallSurroundIcon = 51, CallHeadphonesIcon = 52, CallSpeakers = 53;
        private const int CallSfxNext = 54, CallSfxPrev = 55, CallSfxPlay = 56, CallSfxStop = 57;
        private const int CallMusicNext = 58, CallMusicPrev = 59, CallMusicPlay = 60, CallMusicStop = 67;
        private const int CallSfxUp = 61, CallSfxDown = 62, CallMusicUp = 63, CallMusicDown = 64, CallMicUp = 65, CallMicDown = 66;
        // ours (checked before RecompMenus, which takes every unknown call from 1000 up)
        public const int CallQualityNext = 2000, CallQualityPrev = 2001;

        public IReadOnlyList<SoundTestEntry> MusicList { get; }
        public IReadOnlyList<SoundTestEntry> SfxList { get; }

        private readonly RecompSettings _settings;
        private readonly Action _save;
        private readonly Items _it;

        // the page's own copy while it's open (vanilla's pending config): heard and saved at once (Commit)
        private bool _open;
        private int _sfxVolume, _musicVolume, _speaker;
        private string _quality = "original";
        private int _sfxIndex, _musicIndex;

        // the music test's song while it owns the music (from PLAY until STOP or the page is left); MusicTestStopped:
        // stopped on this page, so the page plays nothing until it's left
        public SoundTestEntry? MusicTest { get; private set; }
        public bool MusicTestStopped { get; private set; }
        // counts PLAYs, so PLAY on the playing song starts it over
        public int MusicTestSerial { get; private set; }
        // SFX test requests for the host: an entry to play, or null to stop the one playing. SfxTestPlaying: the stop
        // button is up; the host calls OnSfxTestEnded when the sound has played out (vanilla flips back to play)
        public Action<SoundTestEntry?>? SfxTest { get; set; }
        public bool SfxTestPlaying { get; private set; }
        private MenuEngine? _menu;
        public Action<string>? Log { get; set; }

        // SURROUND and HEADPHONES are picked and saved, but sound like STEREO until the speaker modes are in: the session
        // shows its WORK IN PROGRESS box
        public Action? WorkInProgress { get; set; }

        // what to hear right now: the page's values while it's open, else the saved ones
        public int SfxVolume => _open ? _sfxVolume : Math.Clamp(_settings.SfxVolume, 0, 9);
        public int MusicVolume => _open ? _musicVolume : Math.Clamp(_settings.MusicVolume, 0, 9);
        public string Speakers => RecompSettings.SpeakerTypes[_open ? _speaker : SpeakerIndex(_settings.Speakers)];
        public string Quality => _open ? _quality : _settings.Music;

        private AudioOptions(RecompSettings settings, Action save, Items items, IReadOnlyList<SoundTestEntry> music,
            IReadOnlyList<SoundTestEntry> sfx)
        {
            _settings = settings;
            _save = save;
            _it = items;
            MusicList = music;
            SfxList = sfx;
        }

        private static int SpeakerIndex(string? s) => Math.Max(0, Array.IndexOf(RecompSettings.SpeakerTypes, s));

        // ---- set-up: the lists, the page's items, and the quality row in the mic row's place ----

        private sealed class Items
        {
            public int[] Icon = new int[3], Label = new int[3];
            public int Hint, QualityHint;
            public int SfxLocked, MusicLocked;
            public int[] SfxTest = Array.Empty<int>(), MusicTest = Array.Empty<int>(); // shown only when unlocked
            public int SfxIndexString, MusicIndexString;
            public int SfxValueString, MusicValueString;
            public int SfxPlay, SfxStop, MusicPlay, MusicStop;
            public int[] Mic = Array.Empty<int>();
            public int QualityValueString;
        }

        // null when this ROM's page 22 isn't the layout we know (the page then runs as plain data, as before)
        public static AudioOptions? Install(MenuFile file, MenuStrings strings, string fileSystemRoot, RecompSettings settings, Action save)
        {
            if (file.Pages.Count <= Page) return null;
            MenuPage page = file.Pages[Page];
            var it = new Items();
            string Text(MenuItem m) => m.States.FirstOrDefault(s => s.Text != null)?.Text is MenuTextStyle t ? strings[t.StringId] : "";
            int StringOf(MenuItem m) => m.States.First(s => s.Text != null).Text!.StringId;
            bool Widget(MenuItem m, string name) => m.States.Any(s => s.WidgetIndex >= 0
                && file.Widgets[s.WidgetIndex].ModelPath.Contains(name, StringComparison.OrdinalIgnoreCase));
            int Find(string what, Func<MenuItem, bool> match)
            {
                MenuItem? m = page.Items.FirstOrDefault(match);
                if (m == null) throw new InvalidOperationException($"audio page: no {what}");
                return m.Index;
            }
            MenuItem Item(int i) => page.Items[i];
            try
            {
                string[] modes = RecompSettings.SpeakerTypes;
                for (int k = 0; k < 3; k++)
                {
                    it.Icon[k] = Find(modes[k] + " icon", m => !m.IsText && Widget(m, modes[k] + "_"));
                    it.Label[k] = Find(modes[k] + " label", m => m.IsText && Text(m) == modes[k]);
                }
                it.Hint = Find("description", m => m.IsText && m.Y > 192 && m.States.Any(s => s.Text?.WrapWidth == 230));
                it.SfxLocked = Find("sfx ? ? ? ?", m => m.IsText && Text(m) == "? ? ? ?" && m.X < 128);
                it.MusicLocked = Find("music ? ? ? ?", m => m.IsText && Text(m) == "? ? ? ?" && m.X >= 128);
                int sfxLabel = Find("sfx test", m => m.IsText && Text(m) == "sfx test");
                int musicLabel = Find("music test", m => m.IsText && Text(m) == "music test");
                int sfxIndex = Find("sfx index", m => m.IsText && Text(m) == "00s");
                int musicIndex = Find("music index", m => m.IsText && Text(m) == "00m");
                it.SfxIndexString = StringOf(Item(sfxIndex));
                it.MusicIndexString = StringOf(Item(musicIndex));
                int Calling(string what, int call, Func<MenuItem, bool>? also = null)
                    => Find(what, m => RecompMenus.HasCall(m, call) && (also == null || also(m)));
                int sfxBox = Calling("sfx test box", CallSfxNext, m => Widget(m, "box_arrows"));
                int musicBox = Calling("music test box", CallMusicNext, m => Widget(m, "box_arrows"));
                it.SfxPlay = Calling("sfx play", CallSfxPlay, m => Widget(m, "playmask"));
                it.SfxStop = Calling("sfx stop", CallSfxStop);
                it.MusicPlay = Calling("music play", CallMusicPlay, m => Widget(m, "playmask"));
                it.MusicStop = Calling("music stop", CallMusicStop);
                // the decorative arrow pairs: the arrows_option item nearest each box
                int ArrowsNear(int box) => page.Items.Where(m => !m.IsText && Widget(m, "arrows_option"))
                    .OrderBy(m => MathF.Abs(m.X - Item(box).X) + MathF.Abs(m.Y - Item(box).Y)).First().Index;
                it.SfxTest = new[]
                {
                    sfxLabel, sfxIndex, sfxBox, ArrowsNear(sfxBox), it.SfxPlay, it.SfxStop,
                    Calling("sfx right arrow", CallSfxNext, m => Widget(m, "highlight_arrowright")),
                    Calling("sfx left arrow", CallSfxPrev, m => Widget(m, "highlight_arrowleft")),
                };
                it.MusicTest = new[]
                {
                    musicLabel, musicIndex, musicBox, ArrowsNear(musicBox), it.MusicPlay, it.MusicStop,
                    Calling("music right arrow", CallMusicNext, m => Widget(m, "highlight_arrowright")),
                    Calling("music left arrow", CallMusicPrev, m => Widget(m, "highlight_arrowleft")),
                };
                // the volume rows: the "5" value texts by height (sfx, music, mic from the top), the labels, the boxes
                int[] values = page.Items.Where(m => m.IsText && Text(m) == "5" && m.X > 128).OrderByDescending(m => m.Y)
                    .Select(m => m.Index).ToArray();
                if (values.Length != 3) throw new InvalidOperationException("audio page: not three volume values");
                it.SfxValueString = StringOf(Item(values[0]));
                it.MusicValueString = StringOf(Item(values[1]));
                int micLabel = Find("mic label", m => m.IsText && Text(m) == "mic");
                int micBox = Calling("mic box", CallMicUp, m => Widget(m, "box_arrows"));
                int micRight = Calling("mic right arrow", CallMicUp, m => Widget(m, "highlight_arrowright"));
                int micLeft = Calling("mic left arrow", CallMicDown, m => Widget(m, "highlight_arrowleft"));
                int micArrows = ArrowsNear(micBox);
                it.Mic = new[] { micLabel, values[2], micBox, micArrows, micRight, micLeft };

                // the quality row: the mic row's pieces, the value box 24 wide (as RECOMP SETTINGS' rows, for HQ+TONE),
                // with its own description in the hint band while it has focus
                const float grow = 24;
                int labelString = strings.Add("quality");
                it.QualityValueString = strings.Add("");
                // 3 lines at the band's wrap (230; measured with -fecanvas FE_MEASURE)
                int hintString = strings.Add("orig: the ds. orig+fix: no clicks or clipping. hq+fix: smoother. "
                    + "hq+tone: hq + fixes + the ds's treble. changes live.");
                MenuItem hint = Item(it.Hint);
                it.QualityHint = page.AddItem(i => RecompMenus.Clone(hint, i, 0, 0, stringId: hintString, init: MenuState.Hidden)).Index;
                page.AddItem(i => RecompMenus.Clone(Item(micArrows), i, 0, 0, stretchX: grow));
                int focusIn = MenuStateCode.Transition(MenuState.Any, MenuState.Focused);
                int focusOut = MenuStateCode.Transition(MenuState.Focused, MenuState.Any);
                page.AddItem(i => RecompMenus.Clone(Item(micBox), i, 0, 0, stretchX: grow,
                    links: new[]
                    {
                        new MenuLink(focusIn, MenuState.Idle, it.QualityHint), new MenuLink(focusIn, MenuState.Hidden, it.Hint),
                        new MenuLink(focusOut, MenuState.Hidden, it.QualityHint), new MenuLink(focusOut, MenuState.Idle, it.Hint),
                    },
                    actions: new[] { RecompMenus.Call(MenuKeys.A, CallQualityNext), RecompMenus.Call(MenuKeys.Y, CallQualityPrev) }));
                page.AddItem(i => RecompMenus.Clone(Item(micLabel), i, -grow / 2, 0, stringId: labelString));
                page.AddItem(i => RecompMenus.Clone(Item(values[2]), i, 0, 0, stringId: it.QualityValueString, wrapGrow: (int)grow));
                page.AddItem(i => RecompMenus.Clone(Item(micRight), i, grow / 2, 0,
                    actions: Item(micRight).Actions.Select(a => RecompMenus.WithCall(a, CallQualityNext)).ToList()));
                page.AddItem(i => RecompMenus.Clone(Item(micLeft), i, -grow / 2, 0,
                    actions: Item(micLeft).Actions.Select(a => RecompMenus.WithCall(a, CallQualityPrev)).ToList()));
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message + " -- OPTIONS > AUDIO left as plain data");
                return null;
            }
            string dir = Path.Combine(fileSystemRoot, "data", "sound");
            return new AudioOptions(settings, save, it, ReadList(Path.Combine(dir, "BGMSELECTLIST.DAT")),
                ReadList(Path.Combine(dir, "SFXSELECTLIST.DAT")));
        }

        // u32 count, then 16-byte records: +0 u16 id, +2 u16 kind, +4 u16 bits, +8 u16 param, +12 u32 name offset
        private static IReadOnlyList<SoundTestEntry> ReadList(string path)
        {
            var list = new List<SoundTestEntry>();
            if (!File.Exists(path)) return list;
            byte[] d = File.ReadAllBytes(path);
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(d);
            for (int i = 0; i < count && 4 + 16 * i + 16 <= d.Length; i++)
            {
                ReadOnlySpan<byte> r = d.AsSpan(4 + 16 * i, 16);
                int nameAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[12..]);
                int end = nameAt < d.Length ? Array.IndexOf(d, (byte)0, nameAt) : -1;
                string name = end > nameAt ? System.Text.Encoding.Latin1.GetString(d, nameAt, end - nameAt) : "";
                list.Add(new SoundTestEntry(BinaryPrimitives.ReadUInt16LittleEndian(r), BinaryPrimitives.ReadUInt16LittleEndian(r[2..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(r[4..]), BinaryPrimitives.ReadUInt16LittleEndian(r[8..]), name));
            }
            return list;
        }

        // ---- the page ----

        // the sound test is open to everyone in the recomp (vanilla: see Unlocked)
        public Func<bool> Unlocked { get; set; } = () => true;

        public void OnPageEntered(MenuEngine menu, int page)
        {
            if (page != Page)
            {
                if (_open) Close();
                return;
            }
            _open = true;
            _menu = menu;
            SfxTestPlaying = false;
            _sfxVolume = Math.Clamp(_settings.SfxVolume, 0, 9);
            _musicVolume = Math.Clamp(_settings.MusicVolume, 0, 9);
            _speaker = SpeakerIndex(_settings.Speakers);
            _quality = Array.IndexOf(RecompSettings.MusicSources, _settings.Music) >= 0 ? _settings.Music : "original";
            MusicTest = null;
            MusicTestStopped = false;
            // the current speaker type's icon and label (the others stay hidden; the icons' links swap them)
            menu.SetState(_it.Icon[_speaker], MenuState.Idle);
            menu.SetState(_it.Label[_speaker], MenuState.Idle);
            foreach (int i in _it.Mic) menu.SetState(i, MenuState.Hidden);
            bool unlocked = Unlocked();
            menu.SetState(_it.SfxLocked, unlocked ? MenuState.Hidden : MenuState.Idle);
            menu.SetState(_it.MusicLocked, unlocked ? MenuState.Hidden : MenuState.Idle);
            if (!unlocked)
            {
                foreach (int i in _it.SfxTest.Concat(_it.MusicTest)) menu.SetState(i, MenuState.Hidden);
            }
            else
            {
                ShowPlaying(menu, _it.SfxPlay, _it.SfxStop, playing: false);
                ShowPlaying(menu, _it.MusicPlay, _it.MusicStop, playing: false);
            }
            RefreshTexts(menu);
        }

        // left: the tests stop (the values were saved as they changed)
        private void Close()
        {
            _open = false;
            _menu = null;
            SfxTestPlaying = false;
            MusicTest = null;
            MusicTestStopped = false;
            SfxTest?.Invoke(null);
        }

        // play or stop button (they share a spot)
        private static void ShowPlaying(MenuEngine menu, int play, int stop, bool playing)
        {
            menu.SetState(play, playing ? MenuState.Hidden : MenuState.Idle);
            menu.SetState(stop, playing ? MenuState.Idle : MenuState.Hidden);
        }

        private void RefreshTexts(MenuEngine menu)
        {
            menu.Strings.Fill(_it.SfxValueString, _sfxVolume.ToString());
            menu.Strings.Fill(_it.MusicValueString, _musicVolume.ToString());
            menu.Strings.Fill(_it.SfxIndexString, (_sfxIndex + 1).ToString("000"));
            menu.Strings.Fill(_it.MusicIndexString, (_musicIndex + 1).ToString("00"));
            menu.Strings.Set(_it.QualityValueString, QualityLabel(_quality));
        }

        public static string QualityLabel(string music) => music switch
        {
            "plain" => "ORIG",
            "hq" => "HQ+FIX",
            "hqtone" => "HQ+TONE",
            _ => "ORIG+FIX",
        };

        public bool OnCall(MenuEngine menu, int call)
        {
            if (!_open) return false;
            switch (call)
            {
            case CallSave: // hidden (RecompMenus.HideSaveButtons); if a ROM still shows it, it just goes back
                menu.GoTo(FrontendSession.OptionsPage);
                return true;
            case CallStereoIcon: // a tap on the shown icon: the next type (its own action hides it; the links show the next)
            case CallSurroundIcon:
            case CallHeadphonesIcon:
                _speaker = (call - CallStereoIcon + 1) % 3;
                Commit();
                if (_speaker != 0) WorkInProgress?.Invoke();
                return true;
            case CallSpeakers: // A on SPEAKERS: the same, by code
                menu.SetState(_it.Icon[_speaker], MenuState.Hidden);
                _speaker = (_speaker + 1) % 3;
                Commit();
                if (_speaker != 0) WorkInProgress?.Invoke();
                return true;
            case CallSfxNext:
            case CallSfxPrev:
                if (SfxList.Count == 0) return true;
                _sfxIndex = (_sfxIndex + (call == CallSfxNext ? 1 : SfxList.Count - 1)) % SfxList.Count;
                StopSfx(menu);
                break;
            case CallMusicNext:
            case CallMusicPrev:
                if (MusicList.Count == 0) return true;
                _musicIndex = (_musicIndex + (call == CallMusicNext ? 1 : MusicList.Count - 1)) % MusicList.Count;
                if (MusicTest != null) StopMusic(menu);
                break;
            case CallSfxPlay:
                if (SfxList.Count == 0) return true;
                SfxTest?.Invoke(SfxList[_sfxIndex]);
                SfxTestPlaying = true;
                ShowPlaying(menu, _it.SfxPlay, _it.SfxStop, playing: true);
                Log?.Invoke($"sfx test {_sfxIndex + 1}: {SfxList[_sfxIndex]}");
                return true;
            case CallSfxStop:
                StopSfx(menu);
                return true;
            case CallMusicPlay:
                if (MusicList.Count == 0) return true;
                MusicTest = MusicList[_musicIndex];
                MusicTestStopped = false;
                MusicTestSerial++;
                ShowPlaying(menu, _it.MusicPlay, _it.MusicStop, playing: true);
                Log?.Invoke($"music test {_musicIndex + 1}: {MusicTest}");
                return true;
            case CallMusicStop:
                StopMusic(menu);
                return true;
            case CallSfxUp:
            case CallSfxDown:
                _sfxVolume = Step(_sfxVolume, call == CallSfxUp ? 1 : -1);
                Commit();
                break;
            case CallMusicUp:
            case CallMusicDown:
                _musicVolume = Step(_musicVolume, call == CallMusicUp ? 1 : -1);
                Commit();
                break;
            case CallMicUp:
            case CallMicDown:
                return true; // the mic row is hidden
            case CallQualityNext:
            case CallQualityPrev:
            {
                string[] all = RecompSettings.MusicSources;
                int i = Math.Max(0, Array.IndexOf(all, _quality));
                _quality = all[(i + (call == CallQualityNext ? 1 : all.Length - 1)) % all.Length];
                Commit();
                break;
            }
            default:
                return false;
            }
            RefreshTexts(menu);
            return true;
        }

        // the page's values into the settings file, at once
        private void Commit()
        {
            _settings.SfxVolume = _sfxVolume;
            _settings.MusicVolume = _musicVolume;
            _settings.Speakers = RecompSettings.SpeakerTypes[_speaker];
            _settings.Music = _quality;
            _save();
            Log?.Invoke($"audio options saved: sfx {_sfxVolume}, music {_musicVolume}, {_settings.Speakers}, {_quality}");
        }

        // 0..9, wrapping round both ways
        private static int Step(int v, int d) => ((v + d) % 10 + 10) % 10;

        private void StopSfx(MenuEngine menu)
        {
            SfxTest?.Invoke(null);
            SfxTestPlaying = false;
            ShowPlaying(menu, _it.SfxPlay, _it.SfxStop, playing: false);
        }

        public void OnSfxTestEnded()
        {
            if (!SfxTestPlaying || _menu == null) return;
            SfxTestPlaying = false;
            ShowPlaying(_menu, _it.SfxPlay, _it.SfxStop, playing: false);
        }

        private void StopMusic(MenuEngine menu)
        {
            MusicTest = null;
            MusicTestStopped = true;
            ShowPlaying(menu, _it.MusicPlay, _it.MusicStop, playing: false);
        }
    }
}
