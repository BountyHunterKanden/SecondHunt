using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

// The game-code side of the vanilla file select (page 24, "SELECT A FILE"). The page's data already holds everything a
// file shows -- the empty or filled file icon and its brackets, "create new game", "complete N%", "octoliths N", the
// highlight that starts the file, the "create a new game in this slot?" yes/no -- and what the DS code does is put each
// file in the right state from the save and write the numbers into the placeholder strings. We do the same from the
// campaign's saves (CampaignSaves: files A/B/C = slots 1/2/3).
//
// Pieces are found by what they are (widget, callback, text position), never by index: USA rev 1 inserts two logo items
// before them. Focusing a readable file shows its expansion counts in the top band ("MISSILE EXPANSION -" / "UA
// EXPANSION -" / "ENERGY TANK -", each with its number in the page's "88" / "99" / "77" placeholder). A damaged save
// shows its file as used, with no numbers, and says so in the top band; starting it leaves the campaign's own "could
// not be read" handling to deal with it.
namespace MphRecomp.Frontend
{
    public sealed class FileSelect
    {
        public const int Page = 24;
        // the page the "keys A" start action jumps to (the BEGIN GAME fade) -- like Page above, its index is part
        // of the menu graph's fixed layout, not the renumbered-per-revision callback ids below
        public const int BeginGamePage = 14;

        private sealed class Slot
        {
            public int File, Brackets, NewText, Existing, Empty, CompleteLabel, CompleteValue, OctoLabel, OctoValue;
            // the empty-file picture (fileAempty...): the file's link hides it; shown again when the file is deleted
            public int EmptyFile = -1;
            public int CompleteString = -1, OctoString = -1;
            public string CompleteDefault = "0%", OctoDefault = "0";
            // the game callback ids that mean "start/create this slot" and "this slot's highlight took focus",
            // read from THIS ROM's own data (Bind) rather than assumed literals -- a ROM revision's recompiled
            // code can renumber them (confirmed: USA rev 1 uses different ids here than USA rev 0)
            public int StartCall = -1, FocusCall = -1;
        }

        private readonly Slot[] _slots = new Slot[3];
        private int _newGame = -1, _newGameString = -1;
        private string _newGameDefault = "NEW GAME";
        // top band rows, top to bottom: missile expansions, UA expansions, energy tanks
        private static readonly float[] ExpansionY = { 229, 219, 209 };
        private readonly int[] _expLabel = { -1, -1, -1 }, _expValue = { -1, -1, -1 }, _expString = { -1, -1, -1 };
        private int[] _dialogs = Array.Empty<int>();
        private readonly string? _savesDir;
        private CampaignSlotSummary?[] _summaries = new CampaignSlotSummary?[3];

        // COPY and DELETE (ov0 USA rev 1 0x2132870 / 0x21327b8): each an icon, its label and the small highlight whose A
        // action calls the game (rev 0 76 / 77, rev 1 83 / 84 -- read from the data like the slot calls). The game shows
        // them from its focus handler and after every file change (0x2121884): COPY while the focused file is used and a
        // slot is empty, DELETE while the focused file is used.
        private readonly int[] _copyItems = { -1, -1, -1 }, _deleteItems = { -1, -1, -1 };
        public int CopyCall { get; private set; } = -1;
        public int DeleteCall { get; private set; } = -1;
        // their questions and busy messages, found by text (the ids differ per revision): "copy save file" + " a" + "?",
        // "delete save file" + ... (the letters are ov0's own strings, 0x2147c4c), "saving to the game card...",
        // "now erasing..."
        private string _copyText = "copy save file", _deleteText = "delete save file", _questionEnd = "?";
        private string _savingText = "saving to the game card.\n\ndo not touch the game card or the power button.";
        private string _erasingText = "now erasing.\n\ndo not touch the game card or the power button.";
        private static readonly string[] SlotLetters = { " a", " b", " c" };
        // the busy box's time on screen, menu ticks (BizHawk USA rev 1: COPY 89 frames, DELETE 85, create about the same
        // as COPY -- the emulated card write)
        private const int SavingTicks = 44, ErasingTicks = 42;

        // the front end's message box (null: no frontend2d art -- copy / delete / create then happen at once)
        public MenuPopup? Popup { get; set; }
        public Action<string>? Log { get; set; }

        // the same "saving to the game card" box for another save (the results page's OK after an ending); without the
        // box, done runs at once
        public void ShowSaving(Action done)
        {
            if (Popup == null) done();
            else Popup.Busy(_savingText, SavingTicks, done);
        }

        public bool Bound { get; private set; }

        public FileSelect(string? savesDir) => _savesDir = savesDir;

        // the HD models (<profile>/hd): the hunter box offers the Trooper when one of his skins is there
        public string? HdRoot { get; set; }

        // Find the page's pieces. Returns false (the file select then behaves as the bare data does) if this ROM's page
        // doesn't have them all.
        public bool Bind(MenuFile file, MenuStrings strings)
        {
            if (file.Pages.Count <= Page) return false;
            MenuPage page = file.Pages[Page];
            bool HasWidget(MenuItem it, string name) => it.States.Any(s => s.WidgetIndex >= 0
                && file.Widgets[s.WidgetIndex].ModelPath.EndsWith("\\" + name + "_Model.bin", StringComparison.OrdinalIgnoreCase));
            // the highlight's "keys A" action that leaves the menus (present only once the file exists, or once the
            // player confirms creating one): found by where it goes, not by a callback id that can differ per revision
            MenuAction? BeginAction(MenuItem it) => it.Actions.FirstOrDefault(a => a.TargetPage == BeginGamePage && a.Calls.Count > 0);
            // the highlight's "this slot took focus" auto-action (fires on arrival, no key/page of its own)
            MenuAction? FocusAction(MenuItem it) => it.Actions.FirstOrDefault(a => a.Kind == 0 && a.Calls.Count > 0);
            int Find(Func<MenuItem, bool> match)
            {
                for (int i = 0; i < page.Items.Count; i++)
                {
                    if (match(page.Items[i])) return i;
                }
                return -1;
            }
            int TextAt(float x, float y) => Find(it => it.IsText && MathF.Abs(it.X - x) < 1.5f && MathF.Abs(it.Y - y) < 1.5f);
            string[] fileWidget = { "fileA", "fileB", "fileC" };
            float[] bracketX = { 0, 80, 160 };
            float[] newX = { 48, 128, 209 };
            float[] infoX = { 49, 128, 208 };
            // the big_highlight pair (existing-file / empty-file) sits at a fixed offset from its slot's bracket --
            // stable layout, unlike the callback ids the two used to be told apart by
            float[] highlightX = { -36, 44, 124 };
            for (int s = 0; s < 3; s++)
            {
                int sl = s;
                var slot = new Slot
                {
                    File = Find(it => HasWidget(it, fileWidget[sl])),
                    Brackets = Find(it => HasWidget(it, "fileAbrackets") && MathF.Abs(it.X - bracketX[sl]) < 1),
                    NewText = TextAt(newX[sl], 122),
                    Existing = Find(it => HasWidget(it, "big_highlight") && MathF.Abs(it.X - highlightX[sl]) < 1 && BeginAction(it) != null),
                    Empty = Find(it => HasWidget(it, "big_highlight") && MathF.Abs(it.X - highlightX[sl]) < 1 && BeginAction(it) == null),
                    CompleteLabel = TextAt(infoX[sl], 70),
                    CompleteValue = TextAt(infoX[sl], 61.2f),
                    OctoLabel = TextAt(infoX[sl], 48),
                    OctoValue = TextAt(infoX[sl], 39.2f),
                };
                if (new[] { slot.File, slot.Brackets, slot.NewText, slot.Existing, slot.Empty, slot.CompleteLabel,
                    slot.CompleteValue, slot.OctoLabel, slot.OctoValue }.Any(i => i < 0))
                {
                    return false;
                }
                // the actual "start/create" and "focused" callback ids this ROM uses for this slot (see Slot.StartCall)
                MenuAction? begin = BeginAction(page.Items[slot.Existing]);
                slot.StartCall = begin!.Calls[^1].B; // the slot-specific call; a shared "highlight chosen" call may precede it
                slot.FocusCall = FocusAction(page.Items[slot.Existing])?.Calls[0].B ?? -1;
                slot.CompleteString = page.Items[slot.CompleteValue].States[0].Text!.StringId;
                slot.OctoString = page.Items[slot.OctoValue].States[0].Text!.StringId;
                slot.CompleteDefault = strings[slot.CompleteString];
                slot.OctoDefault = strings[slot.OctoString];
                slot.EmptyFile = Find(it => HasWidget(it, fileWidget[sl] + "empty"));
                _slots[s] = slot;
            }
            _newGame = TextAt(128, 219);
            if (_newGame >= 0)
            {
                _newGameString = page.Items[_newGame].States[0].Text!.StringId;
                _newGameDefault = strings[_newGameString];
            }
            // the top band's expansion lines (labels right-aligned at x 170, their numbers at 175)
            for (int r = 0; r < ExpansionY.Length; r++)
            {
                _expLabel[r] = TextAt(170.1f, ExpansionY[r]);
                _expValue[r] = TextAt(175, ExpansionY[r]);
                _expString[r] = _expValue[r] < 0 ? -1 : page.Items[_expValue[r]].States[0].Text!.StringId;
            }
            _dialogs = Enumerable.Range(0, page.Items.Count).Where(i => HasWidget(page.Items[i], "dialog_yesno")).ToArray();
            // COPY / DELETE: the label on the bottom row, the icon by its model, the highlight whose touch area is centred
            // on the label (USA rev 0: items 71 / 13 / 16 and 72 / 14 / 17)
            MenuAction? CallAction(MenuItem it) => it.Actions.FirstOrDefault(a => (a.Kind & (ushort)MenuKeys.A) != 0 && a.Calls.Count > 0);
            int HighlightOver(int label) => label < 0 ? -1 : Find(it =>
            {
                MenuAction? a = CallAction(it);
                return a != null && HasWidget(it, "small_highlight") && MenuEngine.TryRect(a, it, out float x0, out _, out float x1, out _)
                    && MathF.Abs((x0 + x1) / 2 - page.Items[label].X) < 3;
            });
            int copyLabel = TextAt(94, 0), deleteLabel = TextAt(161.5f, 0);
            int[] copy = { HighlightOver(copyLabel), Find(it => HasWidget(it, "copy")), copyLabel };
            int[] delete = { HighlightOver(deleteLabel), Find(it => HasWidget(it, "delete")), deleteLabel };
            if (copy.All(i => i >= 0) && delete.All(i => i >= 0) && _slots.All(sl => sl.EmptyFile >= 0))
            {
                copy.CopyTo(_copyItems, 0);
                delete.CopyTo(_deleteItems, 0);
                CopyCall = CallAction(page.Items[copy[0]])!.Calls[^1].B;
                DeleteCall = CallAction(page.Items[delete[0]])!.Calls[^1].B;
            }
            BindTransfer(file, strings, page, HasWidget, Find, copy[0]);
            int Text(Func<string, bool> match)
            {
                for (int i = 0; i < strings.RomCount; i++)
                {
                    if (match(strings[i])) return i;
                }
                return -1;
            }
            int copyText = Text(t => t == "copy save file"), deleteText = Text(t => t == "delete save file");
            if (copyText >= 0) _copyText = strings[copyText];
            if (deleteText >= 0) _deleteText = strings[deleteText];
            if (copyText >= 0 && strings[copyText + 1] == "?") _questionEnd = strings[copyText + 1];
            int saving = Text(t => t.StartsWith("saving to the game card.", StringComparison.Ordinal));
            int erasing = Text(t => t.StartsWith("now erasing.", StringComparison.Ordinal));
            if (saving >= 0) _savingText = strings[saving];
            if (erasing >= 0) _erasingText = strings[erasing];
            Bound = true;
            return true;
        }

        // Page 24 came in: every file in its state from the saves (the game's 0x2121c2c: each slot's look, the top band
        // empty).
        public void OnEnter(MenuEngine menu, MenuStrings strings)
        {
            if (!Bound) return;
            Summarize();
            for (int s = 0; s < 3; s++)
            {
                ApplySlot(menu, strings, s, entering: true);
            }
        }

        private void Summarize()
        {
            var saves = _savesDir == null ? null : new CampaignSaves(_savesDir);
            for (int s = 0; s < 3; s++)
            {
                _summaries[s] = saves?.Summarize(s + 1);
            }
        }

        private bool Used(int slot) => slot >= 0 && _summaries[slot] is { Exists: true };
        private bool Readable(int slot) => slot >= 0 && _summaries[slot] is { Exists: true, Readable: true };

        // One file's look from its summary (0x2121af0 / 0x2121788): the used file with its brackets and numbers, or the
        // empty one with "create new game". Entering, the items haven't started yet: a used file's are started in its
        // look, an empty one's keep the page's own entrance. Later only items whose state changes are touched.
        private void ApplySlot(MenuEngine menu, MenuStrings strings, int s, bool entering = false)
        {
            Slot slot = _slots[s];
            CampaignSlotSummary? sum = _summaries[s];
            bool used = Used(s), readable = Readable(s);
            strings.Fill(slot.CompleteString, readable ? $"{sum!.CompletionPercent}%" : slot.CompleteDefault);
            strings.Fill(slot.OctoString, readable ? sum!.Octoliths.ToString() : slot.OctoDefault);
            if (entering)
            {
                if (!used) return; // the page's own start state is the empty file
                menu.SetState(slot.File, MenuState.Idle); // its link hides the empty file
                menu.SetState(slot.Brackets, MenuState.Idle);
                menu.SetState(slot.NewText, MenuState.Hidden);
                menu.SetState(slot.Existing, MenuState.Idle);
                menu.SetState(slot.Empty, MenuState.Hidden);
                MenuState numbers = readable ? MenuState.Idle : MenuState.Hidden;
                menu.SetState(slot.CompleteLabel, numbers);
                menu.SetState(slot.CompleteValue, numbers);
                menu.SetState(slot.OctoLabel, numbers);
                menu.SetState(slot.OctoValue, numbers);
                return;
            }
            Show(menu, slot.File, used); // its link hides the empty file
            if (slot.EmptyFile >= 0 && !used) Show(menu, slot.EmptyFile, true);
            Show(menu, slot.Brackets, used);
            Show(menu, slot.NewText, !used);
            Show(menu, slot.Existing, used);
            Show(menu, slot.Empty, !used);
            Show(menu, slot.CompleteLabel, readable);
            Show(menu, slot.CompleteValue, readable);
            Show(menu, slot.OctoLabel, readable);
            Show(menu, slot.OctoValue, readable);
        }

        // A file's highlight took focus (callbacks 80 / 82 / 84): the top band says what the file is.
        public bool DialogOpen(MenuEngine menu) => _dialogs.Any(d => menu.ItemState(d) != MenuState.Hidden);

        // the file whose highlight last took focus (the create-new-game dialog, COPY and DELETE act on it)
        public int FocusedSlot { get; private set; } = -1;

        // "create a new game in this slot?" YES: write the new file, then (vanilla) SAVING TO THE GAME CARD... while the
        // card is written, then the file shows as a used file at 0%. Ours: once an ending has been reached (any file,
        // CampaignSaves.HuntersUnlocked), a second box first asks which hunter the file plays as; NO there creates nothing.
        // (A DS save goes in through IMPORT / EXPORT, not here.)
        public bool CreateFile(MenuEngine menu, MenuStrings strings)
        {
            if (!Bound || _savesDir == null || FocusedSlot < 0) return false;
            int slot = FocusedSlot;
            var saves = new CampaignSaves(_savesDir);
            CloseDialog(menu);
            void Create(CampaignPick pick)
            {
                saves.CreateNew(slot + 1, pick.Hunter, pick.Character, pick.Skin);
                Log?.Invoke($"file select: file {(char)('A' + slot)} created as {pick.Name}{(pick.Skin == null ? "" : $" ({pick.Skin})")}");
                void ShowFile()
                {
                    OnEnter(menu, strings);
                    OnFocused(menu, strings, slot);
                }
                if (Popup == null) ShowFile();
                else Popup.Busy(_savingText, SavingTicks, ShowFile);
            }
            ChooseHunter(saves, slot, Create);
            return true;
        }

        // the hunter box (or Samus at once while no ending has been reached), then `create`
        private void ChooseHunter(CampaignSaves saves, int slot, Action<CampaignPick> create)
        {
            List<CampaignPick> picks = CampaignPicks();
            if (Popup == null || !saves.HuntersUnlocked())
            {
                create(picks[0]);
                return;
            }
            MenuPopup popup = Popup;
            int pick = 0;
            void Step(int d) => pick = (pick + d + picks.Count) % picks.Count;
            popup.Ask(_huntersText, new MenuPopup.Body
            {
                Width = HunterBoxWidth,
                Height = HunterBoxHeight,
                Draw = (tris, font, bx, by, _) => DrawChoice(tris, font, popup.TextColour, bx, by, picks[pick].Name),
                Touch = (x, _) =>
                {
                    if (x < HunterBoxWidth / 3f) Step(-1);
                    else if (x >= HunterBoxWidth * 2 / 3f) Step(1);
                    else return;
                    popup.Sound?.Invoke((int)SfxId.TOUCH_NEUTRAL, "file select hunter tap");
                },
                Step = Step,
            }, yes =>
            {
                if (yes) create(picks[pick]);
                else Log?.Invoke($"file select: file {(char)('A' + slot)} not created (hunter box NO)");
            });
        }

        // ---- IMPORT / EXPORT (owner 2026-10-28: "another button by movies button ... a pop up comes up with a picker that
        // has import or export as an option and another picker for file a b or c"; it replaces the new-file offer). A button
        // in the bottom bar just left of MOVIES (owner 10-28: by MOVIES, not by the back button), built like MOVIES (its
        // small highlight and label), "SAVE DATA" (owner 10-29; it was "DS SAVE"). Its icon is a clone of MOVIES' too, so it
        // slides in with the bar like the others (owner 10-29), drawing our up and down arrows picture (SaveDataPicture)
        // instead of MOVIES'; SECOND HUNT SETTINGS > SAVES opens the same box.

        public const int TransferCall = 2870;
        // the button's highlight and the item whose sound it borrows (COPY's: it opens a box too), for MenuSounds.ItemLike
        public int TransferItem { get; private set; } = -1;
        public int TransferSoundItem { get; private set; } = -1;
        // the button's icon (the page's item index), drawn with SaveDataPicture
        private int _transferIcon = -1;
        // how far left of MOVIES the button sits (MOVIES' x minus ours), and each widget's entrance: how far right of its
        // place it starts (FileSelect.DrawSaveData)
        private float _transferGap;
        private readonly Dictionary<int, float> _entrance = new();
        // the label's centre: "save data" spans x 145..201, 10 px short of MOVIES' label (x 211.5..247.5); it, its icon and
        // highlight centred past x 166 so the file select's footer-right region pins them to the right corner with MOVIES
        // (MenuLayout.MainFamily; DELETE, x 160.5, stays in the middle)
        private const float TransferX = 172;

        private void BindTransfer(MenuFile file, MenuStrings strings, MenuPage page, Func<MenuItem, string, bool> hasWidget,
            Func<Func<MenuItem, bool>, int> find, int copyHighlight)
        {
            int icon = find(it => hasWidget(it, "movies"));
            int highlight = find(it => hasWidget(it, "small_highlight") && it.Actions.Any(a => a.TargetPage != 0xFF && a.Calls.Count == 0));
            int label = find(it => it.IsText && strings[it.States[0].Text!.StringId] == "movies");
            if (icon < 0 || highlight < 0 || label < 0)
            {
                Log?.Invoke("file select: no IMPORT / EXPORT button (this ROM's MOVIES pieces aren't where expected)");
                return;
            }
            MenuItem moviesIcon = page.Items[icon], moviesHighlight = page.Items[highlight], moviesLabel = page.Items[label];
            float dx = TransferX - moviesLabel.X;
            _transferGap = -dx;
            int newIcon = page.Items.Count;
            page.AddItem(i => RecompMenus.Clone(moviesIcon, i, dx, 0, links: Array.Empty<MenuLink>(), actions: Array.Empty<MenuAction>()));
            _transferIcon = newIcon;
            int newLabel = page.Items.Count;
            int labelString = strings.Add("save data");
            page.AddItem(i => RecompMenus.Clone(moviesLabel, i, dx, 0, stringId: labelString));
            // MOVIES' highlight lights its icon and label: ours lights these
            MenuLink[] links = moviesHighlight.Links.Select(l => l with
            {
                Target = l.Target == icon ? newIcon : l.Target == label ? newLabel : l.Target
            }).ToArray();
            MenuAction movies = moviesHighlight.Actions.First(a => a.TargetPage != 0xFF);
            MenuItem added = page.AddItem(i => RecompMenus.Clone(moviesHighlight, i, dx, 0, links: links, actions: new[]
            {
                new MenuAction
                {
                    Kind = movies.Kind, Field2 = movies.Field2, Flags = movies.Flags, Field4 = movies.Field4, RectX = movies.RectX,
                    RectY = movies.RectY, RectW = movies.RectW, RectH = movies.RectH, Calls = new[] { (0, TransferCall) },
                    Item = (ushort)i, TargetPage = 0xFF, Field17 = movies.Field17
                }
            }));
            TransferItem = added.Index;
            TransferSoundItem = copyHighlight >= 0 ? page.Items[copyHighlight].Index : -1;
        }

        // FrontendSession's draw pass: the SAVE DATA icon (MOVIES' clone) draws our arrows instead of MOVIES' picture, and
        // its icon and highlight come in from where MOVIES' do (owner 10-29: "the save data button is visible before
        // sliding"). The cloned slide starts 54.6 px right of its place, which for a button 56.5 px left of MOVIES is still
        // on screen: MOVIES' starts off it. So ours goes the further distance in the same frames: its offset from its place
        // stretched by (54.6 + 56.5) / 54.6, starting with MOVIES past the screen's right edge and landing with it.
        public void DrawSaveData(MenuEngine menu, List<WidgetTri> tris, int start)
        {
            if (_transferIcon < 0 || menu.Page?.Index != Page || _transferIcon >= menu.Page.Items.Count) return;
            int texture = menu.Widgets.Textures.GetOrAdd(SaveDataPicture.Key, SaveDataPicture.Draw).Id;
            for (int t = start; t < tris.Count; t++)
            {
                if (tris[t].Item != _transferIcon) continue;
                WidgetTri tri = tris[t];
                tri.TextureId = texture;
                tris[t] = tri;
            }
            SlideFromOffScreen(menu, tris, start, _transferIcon);
            if (TransferItem >= 0 && TransferItem < menu.Page.Items.Count) SlideFromOffScreen(menu, tris, start, TransferItem);
        }

        private void SlideFromOffScreen(MenuEngine menu, List<WidgetTri> tris, int start, int item)
        {
            int code = menu.ItemCode(item);
            // the slide is its Hidden -> Idle look (any other look that doesn't move right of its place is left alone)
            if (menu.Page!.Items[item].GetState(code) is not { WidgetIndex: >= 0 } visual) return;
            float left = Single.MaxValue;
            for (int t = start; t < tris.Count; t++)
            {
                if (tris[t].Item == item) left = MathF.Min(left, MathF.Min(tris[t].A.X, MathF.Min(tris[t].B.X, tris[t].C.X)));
            }
            if (left == Single.MaxValue) return;
            // its place: the widget's last frame, and how far right of it the first frame is
            int widget = visual.WidgetIndex;
            var still = new List<WidgetTri>();
            float Left(int frame)
            {
                still.Clear();
                menu.Widgets.Evaluate(widget, frame, loop: false, alpha: 1, still);
                return still.Count == 0 ? 0 : still.Min(w => MathF.Min(w.A.X, MathF.Min(w.B.X, w.C.X)));
            }
            float place = Left(Math.Max(menu.Widgets.FrameCount(widget) - 1, 0)) + menu.Page.Items[item].X;
            if (!_entrance.TryGetValue(widget, out float entrance))
            {
                entrance = Left(0) + menu.Page.Items[item].X - place;
                _entrance[widget] = entrance;
            }
            float offset = left - place;
            if (entrance < 1 || offset < 0.01f) return;
            float shift = offset * _transferGap / entrance;
            for (int t = start; t < tris.Count; t++)
            {
                if (tris[t].Item != item) continue;
                WidgetTri tri = tris[t];
                tri.A.X += shift; tri.B.X += shift; tri.C.X += shift;
                tris[t] = tri;
            }
        }

        // the IMPORT / EXPORT box: IMPORT or EXPORT on the top line, FILE A / B / C on the bottom one (Up / Down pick the
        // line, Left / Right change it); YES does it. From SECOND HUNT SETTINGS too (nothing of the file select to redraw).
        public void Transfer(MenuEngine menu, MenuStrings strings)
        {
            if (Popup == null) return;
            if (_savesDir == null)
            {
                Popup.Inform(FrontendSession.WorkInProgressText);
                return;
            }
            MenuPopup popup = Popup;
            Summarize();
            int row = 0, export = 0, slot = FocusedSlot >= 0 && menu.Page?.Index == Page ? FocusedSlot : 0;
            // the file line names it; a smaller line under it says what's in it (too wide for the arrows together)
            string FileState(int s) => _summaries[s] is { Exists: true } sum
                ? sum.Readable ? $"{sum.CompletionPercent}%" : "DAMAGED"
                : "EMPTY";
            void Step(int d)
            {
                if (row == 0) export ^= 1;
                else slot = (slot + d + 3) % 3;
            }
            popup.Ask(_transferText, new MenuPopup.Body
            {
                Width = HunterBoxWidth,
                Height = HunterBoxHeight,
                Draw = (tris, font, bx, by, _) =>
                {
                    DrawChoiceAt(tris, font, popup.TextColour, bx, by + 15, export == 0 ? "IMPORT" : "EXPORT",
                        arrows: row == 0, alpha: row == 0 ? 1 : 0.5f);
                    DrawChoiceAt(tris, font, popup.TextColour, bx, by + 35, $"FILE {(char)('A' + slot)}",
                        arrows: row == 1, alpha: row == 1 ? 1 : 0.5f);
                    DrawChoiceAt(tris, font, popup.TextColour, bx, by + 46, FileState(slot), arrows: false, alpha: 0.5f);
                },
                // a tap picks the line by its half and steps it by the third it lands in (the middle only picks the line)
                Touch = (x, y) =>
                {
                    row = y < 25 ? 0 : 1;
                    if (x < HunterBoxWidth / 3f) Step(-1);
                    else if (x >= HunterBoxWidth * 2 / 3f) Step(1);
                    popup.Sound?.Invoke((int)SfxId.TOUCH_NEUTRAL, "ds save box tap");
                },
                Step = Step,
                Row = d => row = Math.Clamp(row + d, 0, 1),
            }, yes =>
            {
                if (!yes) return;
                if (export == 0) ImportInto(menu, strings, slot);
                else ExportFrom(slot);
            });
        }

        private const string _transferText = "import or export a ds save.";

        // IMPORT: a used file is asked about first (the import replaces it)
        private void ImportInto(MenuEngine menu, MenuStrings strings, int slot)
        {
            if (_summaries[slot] is { Exists: true })
            {
                Popup!.Ask($"replace file {(char)('a' + slot)} with\na ds save file?", yes =>
                {
                    if (yes) PickDsSave(menu, strings, slot);
                });
                return;
            }
            PickDsSave(menu, strings, slot);
        }

        // the written .sav to the platform shell, which puts it where the player can take it (FrontendRequestKind
        // .ExportDsSave: Android Downloads/Second Hunt, Windows Downloads); null = this build can't hand files out
        public Action<string>? ExportFile { get; set; }

        // EXPORT: the file as a vanilla DS save (Campaign/CartSave.cs: file A of a fresh 256 KB cart save, for an emulator
        // or a flashcart)
        private void ExportFrom(int slot)
        {
            MenuPopup popup = Popup!;
            char letter = (char)('a' + slot);
            if (ExportFile == null)
            {
                popup.Inform(FrontendSession.WorkInProgressText);
                return;
            }
            if (_summaries[slot] is not { Exists: true, Readable: true } sum)
            {
                popup.Inform(_summaries[slot] is { Exists: true } ? $"file {letter} can't be read." : $"file {letter} is empty.");
                return;
            }
            try
            {
                CampaignSaveFile file = new CampaignSaves(_savesDir!).Load(slot + 1)
                    ?? throw new IOException($"file {letter} can't be read");
                string path = Path.Combine(Path.GetTempPath(), $"second-hunt-file-{letter}.sav");
                File.WriteAllBytes(path, CartSave.Export(file));
                Log?.Invoke($"file select: file {letter} ({sum.CompletionPercent}%) exported -> {path}");
                ExportFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log?.Invoke("file select: export: " + ex.Message);
                popup.Inform($"file {letter} can't be exported.");
            }
        }

        // ---- a vanilla DS save into a file (IMPORT / EXPORT's IMPORT; Campaign/CartSave.cs). The platform's file picker
        // answers on any thread: Tick runs its answer.

        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

        // the menu thread's turn (FrontendSession.Tick): a picked save's answer
        public void Tick()
        {
            while (_posted.TryDequeue(out Action? run)) run();
        }

        private void PickDsSave(MenuEngine menu, MenuStrings strings, int slot)
        {
            if (ImportHost.Pick == null)
            {
                Popup?.Inform("this version can't open files.");
                return;
            }
            Log?.Invoke($"file select: picking a ds save for file {(char)('A' + slot)}");
            ImportHost.Pick(new ImportPickRequest(ImportPickKind.DsSave, "mph", "DS SAVE", new[] { ".sav", ".dsv", ".bin" }),
                picked => _posted.Enqueue(() => DsSavePicked(menu, strings, slot, picked)));
        }

        private void DsSavePicked(MenuEngine menu, MenuStrings strings, int slot, ImportPicked? picked)
        {
            if (picked == null || _savesDir == null)
            {
                Log?.Invoke($"file select: no ds save picked; file {(char)('A' + slot)} left as it was");
                return;
            }
            CartSave? cart = null;
            string? error = null;
            try
            {
                using Stream? stream = ImportHost.Open(picked.Token);
                if (stream == null)
                {
                    error = "that file can't be opened.";
                }
                else
                {
                    // a save is 256 KB (a few bytes more as a .dsv); anything far bigger isn't one
                    var bytes = new MemoryStream();
                    stream.CopyTo(bytes);
                    if (bytes.Length > 4 * CartSave.Size) error = "this isn't a metroid prime hunters\nds save.";
                    else cart = CartSave.TryParse(bytes.ToArray(), out error);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = "that file can't be read.";
            }
            CartSaveFile[] used = cart?.Files.Where(f => f.Used).ToArray() ?? Array.Empty<CartSaveFile>();
            if (cart != null && used.Length == 0)
            {
                error = cart.Files.Any(f => f.Damaged) ? "the files in this save are damaged." : "this save has no files in it.";
            }
            Log?.Invoke($"file select: ds save {picked.Name}: {(cart == null ? "refused" : $"{used.Length} file(s)")} {error?.Replace('\n', ' ')}");
            if (error != null || cart == null || Popup == null)
            {
                Popup?.Inform(error ?? "that file can't be read.");
                return;
            }
            void Import(CartSaveFile from)
            {
                var saves = new CampaignSaves(_savesDir);
                saves.Save(slot + 1, cart.ToCampaign(from.Index)); // a beaten file opens the hunter pick (CampaignSaves.Save)
                Log?.Invoke($"file select: file {(char)('A' + slot)} imported from the ds save's file {from.Letter}"
                    + $" ({from.CompletionPercent}%, {from.Octoliths} octoliths{(from.Cleared ? ", ending reached" : "")})");
                void ShowFile()
                {
                    if (menu.Page?.Index != Page) return; // (from SECOND HUNT SETTINGS: the file select draws itself on entry)
                    OnEnter(menu, strings);
                    OnFocused(menu, strings, slot);
                }
                Popup.Busy(_savingText, SavingTicks, ShowFile);
            }
            // one file in the DS save (an EXPORT, or a cart played on one file): nothing to choose
            if (used.Length == 1)
            {
                Import(used[0]);
                return;
            }
            // which of the DS save's own files (A-C) comes in; the file it goes INTO was the IMPORT/EXPORT box's FILE line
            // (owner 10-28: "FILE A/B/C" here read as asking for that file again, so the question names both ends). It starts
            // on the DS save's file of the same letter when that one is used
            MenuPopup popup = Popup;
            int pick = Math.Max(0, Array.FindIndex(used, f => f.Index == slot));
            void Step(int d) => pick = (pick + d + used.Length) % used.Length;
            // (three lines at most beside the box: a fourth runs under YES / NO, Odin screenshot 10-29)
            popup.Ask($"copy which\nds file into\nfile {(char)('a' + slot)}?", new MenuPopup.Body
            {
                Width = HunterBoxWidth,
                Height = HunterBoxHeight,
                // the IMPORT/EXPORT box's look: the name with arrows, what's in it smaller and dimmer under it
                Draw = (tris, font, bx, by, _) =>
                {
                    DrawChoiceAt(tris, font, popup.TextColour, bx, by + 24, $"DS FILE {used[pick].Letter}", arrows: true);
                    DrawChoiceAt(tris, font, popup.TextColour, bx, by + 35, $"{used[pick].CompletionPercent}%", arrows: false,
                        alpha: 0.5f);
                },
                Touch = (x, _) =>
                {
                    if (x < HunterBoxWidth / 3f) Step(-1);
                    else if (x >= HunterBoxWidth * 2 / 3f) Step(1);
                    else return;
                    popup.Sound?.Invoke((int)SfxId.TOUCH_NEUTRAL, "file select ds save file tap");
                },
                Step = Step,
            }, yes =>
            {
                if (yes) Import(used[pick]);
                else Log?.Invoke($"file select: file {(char)('A' + slot)} left as it was (ds save box NO)");
            });
        }

        // the hunters a new file can play as once unlocked (the dev launcher's list: MphRead runs each in the campaign with
        // Features.AlternateHunters1P; Guardian has no single-player kit)
        private static readonly Hunter[] CampaignHunters =
            { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel };
        private const string _huntersText = "choose the hunter for this file.";

        // a hunter box choice: the hunter, or an app character on its rig with the skin it wears
        private readonly record struct CampaignPick(Hunter Hunter, string? Character, string? Skin, string Name);

        // the hunters, then the Trooper (owner 2026-10-28: "i want trooper in the picker", campaign only: Weavel's rig
        // with his Wall Cling and Scout Drone, Weavel's Battlehammer affinity, no Pirate Blaster) once a Trooper body is imported.
        // Which Trooper (POWER, ICE, WAVE, PLASMA) is SECOND HUNT SETTINGS' TROOPER TYPE (owner 2026-10-28: "a picker in the
        // settings menu like the samus skin picker"; the file keeps the first imported one as a fallback)
        private List<CampaignPick> CampaignPicks()
        {
            var picks = CampaignHunters.Select(h => new CampaignPick(h, null, null, h.ToString().ToUpperInvariant())).ToList();
            string? hd = MultiplayerMenu.CharacterHdRoot ?? HdRoot;
            if (hd == null) return picks;
            if (Characters.Skins(Characters.Trooper, hd).FirstOrDefault() is string skin)
                picks.Add(new CampaignPick(Characters.Trooper.Base, Characters.Trooper.Id, skin, Characters.Trooper.Name));
            return picks;
        }
        internal const int HunterBoxWidth = 96, HunterBoxHeight = 59;

        // the hunter box's body (DrawChoice): the name centred between a left and a right arrow, in the message's colour
        internal static void DrawChoice(List<WidgetTri> tris, MenuFont font, (float R, float G, float B) c, int bx, int by, string name,
            bool arrows = true) => DrawChoiceAt(tris, font, c, bx, by + HunterBoxHeight / 2f, name, arrows);

        // one choice line centred on DS pixel row midY (the IMPORT / EXPORT box has two)
        private static void DrawChoiceAt(List<WidgetTri> tris, MenuFont font, (float R, float G, float B) c, int bx, float midY,
            string name, bool arrows = true, float alpha = 1)
        {
            const float z = 3001, lineHeight = 9;
            font.Emit(name, bx + HunterBoxWidth / 2f, 192 - (midY - 4) - lineHeight, align: 2, 0, lineHeight, c.R, c.G, c.B, alpha, z, tris);
            void Arrow(float tipX, float baseX)
            {
                var tip = new UiVertex(tipX, -midY, 0, 0, c.R, c.G, c.B, alpha);
                var top = new UiVertex(baseX, -(midY - 6), 0, 0, c.R, c.G, c.B, alpha);
                var bottom = new UiVertex(baseX, -(midY + 6), 0, 0, c.R, c.G, c.B, alpha);
                tris.Add(new WidgetTri { A = tip, B = top, C = bottom, Z = z, TextureId = -1, Item = -1 });
            }
            if (!arrows) return;
            Arrow(bx + 3, bx + 11);
            Arrow(bx + HunterBoxWidth - 3, bx + HunterBoxWidth - 11);
        }

        public void OnFocused(MenuEngine menu, MenuStrings strings, int slot)
        {
            FocusedSlot = slot;
            if (!Bound) return;
            TopBand(menu, strings, slot);
            RefreshButtons(menu);
        }

        // The top band for one file (0x2121960): a used file at 0% (summary byte 0, the completion) says NEW GAME, one
        // past 0% lists its expansions, an empty slot (or -1) shows nothing. A file we can't read says FILE DAMAGED
        // (ours: the game has its own corrupted-save handling).
        private void TopBand(MenuEngine menu, MenuStrings strings, int slot)
        {
            if (_newGame < 0) return;
            CampaignSlotSummary? sum = slot >= 0 ? _summaries[slot] : null;
            bool used = Used(slot), readable = Readable(slot);
            bool fresh = readable && sum!.CompletionPercent == 0;
            bool counts = readable && !fresh;
            int[] values = counts ? new[] { sum!.MissileExpansions, sum.UaExpansions, sum.EnergyTanks } : new int[3];
            for (int r = 0; r < ExpansionY.Length; r++)
            {
                if (_expLabel[r] < 0 || _expValue[r] < 0) continue;
                if (counts) strings.Fill(_expString[r], values[r].ToString());
                Show(menu, _expLabel[r], counts);
                Show(menu, _expValue[r], counts);
            }
            bool damaged = used && !readable;
            // (ours: a fresh file playing as another hunter says which: NEW GAME - KANDEN)
            string? freshName = !fresh ? null
                : string.Equals(sum!.Character, Characters.Trooper.Id, StringComparison.OrdinalIgnoreCase) ? Characters.Trooper.Name
                : sum.Hunter != Hunter.Samus ? sum.Hunter.ToString().ToUpperInvariant() : null;
            string freshText = freshName != null ? $"{_newGameDefault} - {freshName}" : _newGameDefault;
            if (fresh || damaged) strings.Fill(_newGameString, damaged ? "FILE DAMAGED" : freshText); // (a fading-out band keeps its text)
            Show(menu, _newGame, fresh || damaged);
        }

        // COPY and DELETE shown or hidden for the focused file (0x2121884). A file we can't read can be deleted, not
        // copied.
        private void RefreshButtons(MenuEngine menu)
        {
            if (CopyCall < 0) return;
            bool anyEmpty = !Used(0) || !Used(1) || !Used(2);
            foreach (int item in _copyItems) Show(menu, item, Readable(FocusedSlot) && anyEmpty);
            foreach (int item in _deleteItems) Show(menu, item, Used(FocusedSlot));
        }

        // COPY (0x2132870): the focused file into the first empty slot, after "copy save file a?" YES; SAVING TO THE GAME
        // CARD... while it's written; then the new file shows (and the top band says what it is). NO leaves things as
        // they were.
        public bool Copy(MenuEngine menu, MenuStrings strings)
        {
            int from = FocusedSlot;
            if (!Bound || _savesDir == null || !Readable(from)) return false;
            int to = !Used(0) ? 0 : !Used(1) ? 1 : !Used(2) ? 2 : -1;
            if (to < 0) return false;
            string saves = _savesDir;
            Confirm(_copyText + SlotLetters[from] + _questionEnd, () =>
            {
                try
                {
                    new CampaignSaves(saves).Copy(from + 1, to + 1);
                    Log?.Invoke($"file select: copied file {(char)('A' + from)} to {(char)('A' + to)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log?.Invoke($"file select: copy {(char)('A' + from)} -> {(char)('A' + to)} failed: {ex.Message}");
                }
            }, _savingText, SavingTicks, () => AfterChange(menu, strings, to));
            return true;
        }

        // DELETE (0x21327b8): the focused file, after "delete save file a?" YES; NOW ERASING... while it's erased; then the
        // slot is empty again (and COPY / DELETE go if nothing's left to act on).
        public bool Delete(MenuEngine menu, MenuStrings strings)
        {
            int slot = FocusedSlot;
            if (!Bound || _savesDir == null || !Used(slot)) return false;
            string saves = _savesDir;
            Confirm(_deleteText + SlotLetters[slot] + _questionEnd, () =>
            {
                try
                {
                    new CampaignSaves(saves).Delete(slot + 1);
                    Log?.Invoke($"file select: deleted file {(char)('A' + slot)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log?.Invoke($"file select: delete {(char)('A' + slot)} failed: {ex.Message}");
                }
            }, _erasingText, ErasingTicks, () => AfterChange(menu, strings, slot));
            return true;
        }

        // the question, then on YES the change and the busy box (without the popup art: the change at once)
        private void Confirm(string question, Action change, string busy, int busyTicks, Action done)
        {
            if (Popup == null)
            {
                change();
                done();
                return;
            }
            MenuPopup popup = Popup;
            popup.Ask(question, yes =>
            {
                if (!yes) return; // NO: the button stays focused, as the game leaves it
                change();
                popup.Busy(busy, busyTicks, done);
            });
        }

        // after a copy or delete (0x2121ca4 / 0x2121c58): the slot it changed redrawn, the top band for that slot, COPY /
        // DELETE refreshed for the focused file, and no button left focused
        private void AfterChange(MenuEngine menu, MenuStrings strings, int slot)
        {
            Summarize();
            ApplySlot(menu, strings, slot);
            TopBand(menu, strings, slot);
            foreach (int item in new[] { _copyItems[0], _deleteItems[0] })
            {
                if (item >= 0 && menu.ItemState(item) == MenuState.Focused) menu.SetState(item, MenuState.Idle);
            }
            RefreshButtons(menu);
        }

        private static void Show(MenuEngine menu, int item, bool visible)
        {
            if (item < 0) return;
            MenuState now = menu.ItemState(item);
            // a shown item may be idle or focused: only hidden <-> shown changes it
            if (visible ? now != MenuState.Hidden : now == MenuState.Hidden) return;
            menu.SetState(item, visible ? MenuState.Idle : MenuState.Hidden);
        }

        // B while "create a new game in this slot?" is up (callback 85): close it, as NO does
        public void CloseDialog(MenuEngine menu)
        {
            foreach (int d in _dialogs)
            {
                if (menu.ItemState(d) != MenuState.Hidden) menu.SetState(d, MenuState.Hidden);
            }
        }

        // this ROM's actual callback ids for "start/create slot N" / "slot N's highlight focused" (discovered in
        // Bind -- see Slot.StartCall/FocusCall for why these aren't fixed literals like 79/81/83 or 80/82/84)
        public int SlotOfStart(int call) => Bound ? Array.FindIndex(_slots, sl => sl.StartCall == call) : -1;
        public int SlotOfFocus(int call) => Bound ? Array.FindIndex(_slots, sl => sl.FocusCall == call) : -1;
    }
}
