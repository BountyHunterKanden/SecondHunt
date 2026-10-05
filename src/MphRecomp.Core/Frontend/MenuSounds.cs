using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// The front end's button sounds, picked the way the game picks them. Nothing in frontend/metroidhunters.bin names a
// sound: the game keeps them in a parallel file, data/sound/METROIDHUNTERS_IDS.DAT, loaded by overlay 0 at start-up
// (USA rev 0 0x21022a0 / 0x2102220; USA rev 1 +0xAC0) next to three fixed sounds (0x2102434(table, 14, 4, 0x401C)).
// Read from the ROM and confirmed by logging every sound call in BizHawk on USA rev 1 (2026-10-01).
//
// File layout: u32 count (= the menu file's page count, same order: entry n is page n -- the page's own index byte at
// page+0x1C); then count x 12 bytes:
//   +0 u16 music id (0 = keep), +2 u8 fade-out frames, +3 u8 music flags  -- the page's menu music (PageMusic)
//   +4 u16 item count, +6 u16 page sound, +8 u32 file offset of the item list
// and each item list is item-count x { s16 sound, u16 flags }, indexed by the item's own index (item+0x36). A sound id
// is a plain sample id, or 0x4000 | script id; 0 = none.
//
// The item lists are per ROM revision (USA rev 1 has two extra items at the start of most pages, and its lists are
// shifted to match), which is why this is read from the user's ROM and never indexed by a hard-coded page/item number.
// One rev 1 list doesn't match its page, the file select's: see RealignFileSelect.
namespace MphRecomp.Frontend
{
    [Flags]
    public enum MenuSoundFlags : ushort
    {
        None = 0,
        NoTouchFocusSound = 1,   // a touch that focuses this item makes no TOUCH_NEUTRAL (0x21026a0)
        OnlyWithPageChange = 2,  // the item's sound plays only for an action that changes page (0x210280c)
        NoNavFocusSound = 4      // D-pad focus landing on this item makes no TOUCH_NEUTRAL (0x2102758)
    }

    public sealed class MenuSounds
    {
        // the fixed sounds (ov0 0x2102434's arguments; the same values sit in RAM on USA rev 1)
        public const int Negative = 14;    // TOUCH_NEGATIVE: an action that needs its item focused, on an unfocusable item
        public const int Neutral = 4;      // TOUCH_NEUTRAL: focus moved, or a direction action that had no sound of its own
        public const int Back = 0x401C;    // TOUCH_BACK_SCR: a B action that had no sound of its own

        private readonly int[] _pageSound;
        private readonly (short Sound, MenuSoundFlags Flags)[][] _items;
        private readonly (int Id, int Fade, int Flags)[] _pageMusic;

        private MenuSounds(int[] pageSound, (short, MenuSoundFlags)[][] items, (int, int, int)[] pageMusic)
        {
            _pageSound = pageSound;
            _items = items;
            _pageMusic = pageMusic;
        }

        public static MenuSounds Empty { get; } = new(Array.Empty<int>(), Array.Empty<(short, MenuSoundFlags)[]>(),
            Array.Empty<(int, int, int)>());

        public static MenuSounds Load(string fileSystemRoot)
        {
            string path = Path.Combine(fileSystemRoot, "data", "sound", "METROIDHUNTERS_IDS.DAT");
            return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : Empty;
        }

        public static MenuSounds Parse(byte[] data)
        {
            ReadOnlySpan<byte> d = data;
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(d);
            var pageSound = new int[count];
            var items = new (short, MenuSoundFlags)[count][];
            var pageMusic = new (int, int, int)[count];
            for (int p = 0; p < count; p++)
            {
                int o = 4 + 12 * p;
                pageMusic[p] = (BinaryPrimitives.ReadUInt16LittleEndian(d[o..]), d[o + 2], d[o + 3]);
                int itemCount = BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 4)..]);
                pageSound[p] = BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 6)..]);
                int list = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[(o + 8)..]);
                if (list == 0) itemCount = 0;
                items[p] = new (short, MenuSoundFlags)[itemCount];
                for (int i = 0; i < itemCount; i++)
                {
                    items[p][i] = (BinaryPrimitives.ReadInt16LittleEndian(d[(list + 4 * i)..]),
                        (MenuSoundFlags)BinaryPrimitives.ReadUInt16LittleEndian(d[(list + 4 * i + 2)..]));
                }
            }
            return new MenuSounds(pageSound, items, pageMusic);
        }

        // the page's music (ov0 MenuPageMusic, rev 1 0x2102FE4; started by MusicMgrFrame 0x2102E00 once the previous
        // music has faded out over Fade frames): Id 0 = keep the current music; Flags & 2 = Id is a STRM, else a SEQ
        public (int Id, int Fade, int Flags) PageMusic(int page) =>
            page >= 0 && page < _pageMusic.Length ? _pageMusic[page] : (0, 0, 0);

        // the sound an A-key page action makes when nothing else did (only the title has one: TOUCH_TO_START_SCR)
        public int PageSound(int page) => page >= 0 && page < _pageSound.Length ? _pageSound[page] : 0;

        // an item's own sound and flags (pages/items the file doesn't cover -- the recomp's own -- have none)
        public (int Sound, MenuSoundFlags Flags) Item(int page, int item)
        {
            if (page >= 0 && _entryOf.TryGetValue(page, out int[]? entryOf))
            {
                item = item >= 0 && item < entryOf.Length ? entryOf[item] : -1;
            }
            return Entry(page, item);
        }

        private (int Sound, MenuSoundFlags Flags) Entry(int page, int entry)
        {
            if (page < 0 || page >= _items.Length || entry < 0 || entry >= _items[page].Length) return (0, MenuSoundFlags.None);
            (short sound, MenuSoundFlags flags) = _items[page][entry];
            return (sound & 0xFFFF, flags);
        }

        // item index -> list entry, for a page whose list isn't in its items' order (RealignFileSelect)
        private readonly Dictionary<int, int[]> _entryOf = new();

        // USA rev 1's file select list is out of step with its own page, so rev 1 plays the wrong sounds there (owner
        // queue #24, 2026-10-03; both USA dumps No-Intro verified). Rev 1 gave most pages two new logo items (the "TM" text
        // and toplogoR) and each of those pages' lists two leading entries -- right where the new items come first,
        // harmless where every item ahead of them is silent. But on page 24 rev 1 also moved the copy / delete / movies
        // block (the three icons, then their three highlights) from ahead of the files to after them, and the list kept
        // rev 0's order. As shipped, the "start this file" highlights read 0 (rev 0: the start sound, script 0x18, played
        // with the page change), the empty-file highlights 0 (rev 0: 0xF), delete reads the start sound (inert: no page
        // change; rev 0: 0xF), movies 0xF (rev 0: 0x4017) -- exactly what vanilla rev 1 does (BizHawk 2026-10-01: a file
        // start makes only StopMusic(120)). The owner's call: every revision gets rev 0's sounds. So this reads the list
        // in the order it was written for: the two new items, what precedes them, the copy block, the files, the rest.
        // Found by content, and applied only when the list shows the fault (every file-start highlight silent as shipped
        // and sounded once realigned), so rev 0 and any ROM whose list matches its page are left alone.
        public bool RealignFileSelect(MenuFile file)
        {
            int page = FileSelect.Page;
            if (file.Pages.Count <= page || page >= _items.Length) return false;
            IReadOnlyList<MenuItem> items = file.Pages[page].Items;
            bool Is(int i, string name) => i >= 0 && i < items.Count && items[i].States.Any(s => s.WidgetIndex >= 0
                && file.Widgets[s.WidgetIndex].ModelPath.EndsWith("\\" + name + "_Model.bin", StringComparison.OrdinalIgnoreCase));
            int Find(string name)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (Is(i, name)) return i;
                }
                return -1;
            }
            int logo = Find("toplogoR"); // the second new item; the "TM" text is the first, right before it
            int copy = Find("copy");     // copy, delete, movies, then their three highlights
            const int CopyBlock = 6;
            if (logo < 1 || !items[logo - 1].IsText || copy <= logo + 1 || !Is(copy + 1, "delete") || !Is(copy + 2, "movies")
                || !Enumerable.Range(copy + 3, 3).All(i => Is(i, "small_highlight")))
            {
                return false;
            }
            var order = new List<int> { logo - 1, logo };
            order.AddRange(Enumerable.Range(0, logo - 1));
            order.AddRange(Enumerable.Range(copy, CopyBlock));
            order.AddRange(Enumerable.Range(logo + 1, copy - logo - 1));
            order.AddRange(Enumerable.Range(copy + CopyBlock, items.Count - copy - CopyBlock));
            var entryOf = new int[items.Max(it => it.Index) + 1];
            Array.Fill(entryOf, -1);
            for (int e = 0; e < order.Count; e++) entryOf[items[order[e]].Index] = e;
            // the file-start highlights: their A action leaves for the BEGIN GAME page (as FileSelect finds them)
            int[] starts = Enumerable.Range(0, items.Count).Where(i => items[i].Actions.Any(a =>
                a.TargetPage == FileSelect.BeginGamePage && a.Calls.Count > 0)).Select(i => items[i].Index).ToArray();
            if (starts.Length == 0 || starts.Any(i => Entry(page, i).Sound != 0) || starts.Any(i => Entry(page, entryOf[i]).Sound == 0))
            {
                return false;
            }
            _entryOf[page] = entryOf;
            return true;
        }
    }
}
