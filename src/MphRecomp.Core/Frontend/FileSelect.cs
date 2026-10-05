using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Campaign;

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

        public bool Bound { get; private set; }

        public FileSelect(string? savesDir) => _savesDir = savesDir;

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
        // card is written, then the file shows as a used file at 0%
        public bool CreateFile(MenuEngine menu, MenuStrings strings)
        {
            if (!Bound || _savesDir == null || FocusedSlot < 0) return false;
            int slot = FocusedSlot;
            new CampaignSaves(_savesDir).CreateNew(slot + 1);
            CloseDialog(menu);
            void ShowFile()
            {
                OnEnter(menu, strings);
                OnFocused(menu, strings, slot);
            }
            if (Popup == null) ShowFile();
            else Popup.Busy(_savingText, SavingTicks, ShowFile);
            return true;
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
            if (fresh || damaged) strings.Fill(_newGameString, damaged ? "FILE DAMAGED" : _newGameDefault); // (a fading-out band keeps its text)
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
