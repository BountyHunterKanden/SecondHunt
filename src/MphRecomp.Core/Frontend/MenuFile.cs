using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

// The game's front-end menu graph, read from the user's own ROM (frontend/metroidhunters.bin, magic "MARM"). Every
// start/options/multiplayer screen is data: a list of pages (menus), each a list of items. An item is either a widget
// (an MPH model + one animation per state, positioned by its own node) or a line of text (a string id from
// frontend/metroidhunters_text_<lang>.bin with a colour fade per state). Items carry touch actions (a rectangle plus
// the page it opens) and links that change other items' states when this one changes state (focusing an icon shows
// its description text, etc.), and pages can carry timed actions (the credits advance by themselves).
//
// Field meanings were worked out from the data itself (2026-09-29): the layout of every structure matches MphRead's
// partial notes in Formats/Frontend.cs; names below marked "(?)" are inferred from patterns across all 67 pages, and
// raw values are kept so nothing is lost while the rest is confirmed.
//
// Coordinates: text positions and touch rectangles use DS pixels with Y UP, on the two screens stacked with the touch
// screen at the bottom: y 0..191 = touch (bottom) screen, 192..383 = top screen. Widget models place themselves with
// their own node positions in pixels from the top-left of their screen (Y down is negative).
namespace MphRecomp.Frontend
{
    // Steady item states. A state code in the file is either a steady state (1..7) or a transition written as
    // (to << 3) | from -- e.g. 17 = Hidden -> Idle, 20 = Focused -> Idle, 10 = Idle -> Hidden. 7 stands for "any" in
    // link events (39 = any -> Focused, 60 = Focused -> any).
    public enum MenuState : byte
    {
        None = 0,
        Hidden = 1,
        Idle = 2,
        Selected = 3,
        Focused = 4,
        State5 = 5, // disabled: a greyed look, no touch (vanilla's rumble row without a Rumble Pak; EngTouch skips it)
        State6 = 6, // (?) not seen
        Any = 7
    }

    public static class MenuStateCode
    {
        public static bool IsTransition(int code) => code >= 8;
        public static MenuState From(int code) => (MenuState)(code & 7);
        public static MenuState To(int code) => code >= 8 ? (MenuState)(code >> 3) : (MenuState)code;
        public static int Transition(MenuState from, MenuState to) => ((int)to << 3) | (int)from;

        public static string Name(int code) => code < 8
            ? ((MenuState)code).ToString()
            : $"{(MenuState)(code & 7)}->{(MenuState)(code >> 3)}";
    }

    // A widget model + animation file pair (MenuStruct2). Paths are relative to the file system root with the
    // game's backslashes, e.g. @"main menu\options_Model.bin" + @"main menu\options_Selected_Anim.bin".
    public sealed class MenuWidget
    {
        public int Index { get; init; }
        public uint Field0 { get; init; } // 0x20000 almost always, 0x10000 for 16 (?)
        public string ModelPath { get; init; } = "";
        public string? AnimPath { get; init; }
    }

    // Text appearance for one state (MenuStruct1A3 + MenuStruct1A4).
    public sealed class MenuTextStyle
    {
        public int StringId { get; init; }
        public ushort StringField { get; init; } // high half of the string word (?)
        // colours are 5-bit channels (0..31), R G B A in byte order; the text fades from Start to End over Duration
        public uint StartColor { get; init; }
        public uint EndColor { get; init; }
        public ushort WrapWidth { get; init; } // (?) max line width in pixels, 0 = none (0xF0/0xE6/0x41 seen)
        public float Duration { get; init; } // frames
        public byte Size { get; init; } // (?) low byte of the format word, 8 almost everywhere
        public byte Format1 { get; init; } // (?) 0x00 / 0x01 / 0xFF
        public byte Format2 { get; init; } // (?) 0x00 / 0xFF
        public byte Align { get; init; } // 0 left, 1 right, 2 centre (x is the anchor)
        public byte Flags { get; init; }
        public byte Field15 { get; init; }
        public ushort Field16 { get; init; }

        public static (int R, int G, int B, int A) Channels(uint color)
            => ((int)(color & 0x1F), (int)((color >> 8) & 0x1F), (int)((color >> 16) & 0x1F), (int)((color >> 24) & 0x1F));
    }

    // One state of an item (MenuStruct1A2): a widget animation, a text style, or nothing.
    public sealed class MenuItemState
    {
        public int Code { get; init; } // steady state or transition (MenuStateCode)
        public byte Kind { get; init; } // 0 widget, 1 text
        public ushort Field2 { get; init; }
        public int WidgetIndex { get; init; } = -1; // into MenuFile.Widgets, -1 = none
        public ushort WidgetFlags { get; init; }
        public MenuTextStyle? Text { get; init; }
    }

    // "When this item's state changes by Event, put item Target into state State" (MenuStruct1A5).
    public readonly record struct MenuLink(int Event, MenuState State, int Target);

    // An input/timer action (MenuStruct1A1; also embedded in MenuStruct1B for timed page actions).
    public sealed class MenuAction
    {
        public ushort Kind { get; init; } // 1 = touch/press item (?), 2 = back (?), 3 = any input (?) ... raw
        public byte Field2 { get; init; }
        public byte Flags { get; init; }
        public uint Field4 { get; init; }
        // touch rectangle: centre (x, y up) and half extents (?) in DS pixels, relative to the item's offset
        public short RectX { get; init; }
        public short RectY { get; init; }
        public short RectW { get; init; }
        public short RectH { get; init; }
        public IReadOnlyList<(int A, int B)> Calls { get; init; } = Array.Empty<(int, int)>(); // game callbacks (?)
        public ushort Item { get; init; } // the item this action belongs to (0xFFFF = page)
        public byte TargetPage { get; init; } // page opened, 0xFF = none
        public byte Field17 { get; init; }
    }

    // A timed page action (MenuStruct1B): after Delay frames, run Action.
    public sealed class MenuTimer
    {
        public float Delay { get; init; }
        public MenuAction Action { get; init; } = new();
        public byte Flags { get; init; }
        public byte Field1D { get; init; }
        public ushort Field1E { get; init; }
    }

    public sealed class MenuItem
    {
        public int Index { get; init; }
        public float Delay { get; init; } // frames before the item starts appearing (credits, labels)
        public float Field18 { get; init; }
        public uint Field1C { get; init; }
        public float X { get; init; } // offset added to the widget / text anchor
        public float Y { get; init; }
        // Ours, not in the file (RecompMenus): resize a widget without thickening its border -- every vertex left of the
        // widget's centre moves StretchX / 2 left and every one right of it StretchX / 2 right; every vertex below the
        // centre moves StretchDown down. The ROM's boxes are drawn as end caps + a middle, so only the middle grows.
        public float StretchX { get; init; }
        public float StretchDown { get; init; }
        public float Depth { get; init; } // (?) draw order / z
        public int[]? Field2C { get; init; } // MenuStruct1A6, only one item in the file has it
        public uint Field30 { get; init; } // pointer to an int list shared by most items (?)
        public byte Field34 { get; init; }
        public byte Field35 { get; init; }
        public byte Flags { get; init; }
        public byte Field39 { get; init; }
        public ushort Field3A { get; init; }
        public IReadOnlyList<MenuItemState> States { get; init; } = Array.Empty<MenuItemState>();
        public IReadOnlyList<MenuLink> Links { get; init; } = Array.Empty<MenuLink>();
        public IReadOnlyList<MenuAction> Actions { get; init; } = Array.Empty<MenuAction>();

        public MenuItemState? GetState(int code)
        {
            foreach (MenuItemState s in States)
            {
                if (s.Code == code) return s;
            }
            return null;
        }

        public bool IsText => States.Count > 0 && States[0].Kind == 1;
    }

    public sealed class MenuPage
    {
        private List<MenuItem> _items = new();

        public int Index { get; init; }
        public uint Field0 { get; init; }
        public ushort Field14 { get; init; } // (?) sound ids: 0xFFFF / 0xB1A / 0xB1B / 0xB65 seen
        public ushort Field16 { get; init; }
        public ushort Field18 { get; init; }
        public ushort ItemCount { get; init; }
        public byte Field1D { get; init; } // (?) page group
        public byte Field1E { get; init; }
        public byte Flags { get; init; }
        public IReadOnlyList<MenuItem> Items { get => _items; init => _items = new List<MenuItem>(value); }
        public IReadOnlyList<MenuAction> Actions { get; init; } = Array.Empty<MenuAction>();
        public IReadOnlyList<MenuTimer> Timers { get; init; } = Array.Empty<MenuTimer>();

        // The recomp's own additions (MODS / RECOMP entries, new pages) go on the end, so every ROM item keeps its
        // index. `make` gets the new item's index.
        public MenuItem AddItem(Func<int, MenuItem> make)
        {
            MenuItem item = make(_items.Count);
            _items.Add(item);
            return item;
        }
    }

    public sealed class MenuFile
    {
        private List<MenuPage> _pages = new();
        private List<MenuWidget> _widgets = new();

        public ushort Field4 { get; private init; }
        public byte Field6 { get; private init; }
        public byte Field7 { get; private init; }
        public IReadOnlyList<MenuPage> Pages { get => _pages; private init => _pages = new List<MenuPage>(value); }
        public IReadOnlyList<MenuWidget> Widgets { get => _widgets; private init => _widgets = new List<MenuWidget>(value); }

        // The ROM's pages keep their indices; the recomp's own pages are appended. `make` gets the new page's index.
        public MenuPage AddPage(Func<int, MenuPage> make)
        {
            MenuPage page = make(_pages.Count);
            _pages.Add(page);
            return page;
        }

        // the widget (model + animation pair) with these paths, added if the file has no such pair yet
        public int WidgetIndex(string modelPath, string? animPath)
        {
            for (int i = 0; i < _widgets.Count; i++)
            {
                if (_widgets[i].ModelPath == modelPath && _widgets[i].AnimPath == animPath) return i;
            }
            _widgets.Add(new MenuWidget { Index = _widgets.Count, Field0 = 0x20000, ModelPath = modelPath, AnimPath = animPath });
            return _widgets.Count - 1;
        }

        public static MenuFile Load(string path) => Parse(System.IO.File.ReadAllBytes(path));

        public static MenuFile Parse(byte[] data)
        {
            var r = new Reader(data);
            if (Encoding.ASCII.GetString(data, 0, 4) != "MARM")
            {
                throw new InvalidOperationException("not a MARM menu file");
            }
            var widgets = new List<MenuWidget>();
            foreach (uint o in r.NullTerminatedList(r.U32(12)))
            {
                string? model = null, anim = null;
                foreach (uint f in r.NullTerminatedList(r.U32(o + 8)))
                {
                    uint nameOffset = r.U32(f + 8);
                    string? name = nameOffset == 0 ? null : r.CString(nameOffset);
                    if (model == null) model = name;
                    else anim = name;
                }
                widgets.Add(new MenuWidget { Index = widgets.Count, Field0 = r.U32(o), ModelPath = model ?? "", AnimPath = anim });
            }
            var pages = new List<MenuPage>();
            foreach (uint o in r.NullTerminatedList(r.U32(8)))
            {
                var items = new List<MenuItem>();
                foreach (uint io in r.NullTerminatedList(r.U32(o + 8)))
                {
                    items.Add(ReadItem(r, io));
                }
                var actions = new List<MenuAction>();
                foreach (uint ao in r.NullTerminatedList(r.U32(o + 12)))
                {
                    actions.Add(ReadAction(r, ao));
                }
                var timers = new List<MenuTimer>();
                foreach (uint to in r.NullTerminatedList(r.U32(o + 16)))
                {
                    timers.Add(new MenuTimer
                    {
                        Delay = r.Fx(to),
                        Action = ReadAction(r, to + 4),
                        Flags = r.U8(to + 28),
                        Field1D = r.U8(to + 29),
                        Field1E = r.U16(to + 30)
                    });
                }
                uint f18 = r.U32(o + 24);
                pages.Add(new MenuPage
                {
                    Index = pages.Count,
                    Field0 = r.U32(o),
                    Field14 = r.U16(o + 20),
                    Field16 = r.U16(o + 22),
                    Field18 = (ushort)(f18 & 0xFFFF),
                    ItemCount = (ushort)(f18 >> 16),
                    Field1D = r.U8(o + 29),
                    Field1E = r.U8(o + 30),
                    Flags = r.U8(o + 31),
                    Items = items,
                    Actions = actions,
                    Timers = timers
                });
            }
            return new MenuFile
            {
                Field4 = r.U16(4),
                Field6 = r.U8(6),
                Field7 = r.U8(7),
                Pages = pages,
                Widgets = widgets
            };
        }

        private static MenuItem ReadItem(Reader r, uint o)
        {
            var states = new List<MenuItemState>();
            foreach (uint so in r.NullTerminatedList(r.U32(o + 12)))
            {
                byte kind = r.U8(so);
                int code = r.U8(so + 1);
                ushort f2 = r.U16(so + 2);
                uint value = r.U32(so + 4);
                if (kind == 1)
                {
                    uint word = r.U32(value);
                    uint style = r.U32(value + 4);
                    uint format = r.U32(style + 16);
                    states.Add(new MenuItemState
                    {
                        Code = code,
                        Kind = kind,
                        Field2 = f2,
                        Text = new MenuTextStyle
                        {
                            StringId = (int)(word & 0xFFFF),
                            StringField = (ushort)(word >> 16),
                            StartColor = r.U32(style),
                            EndColor = r.U32(style + 4),
                            WrapWidth = r.U16(style + 8),
                            Duration = r.Fx(style + 12),
                            Size = (byte)format,
                            Format1 = (byte)(format >> 8),
                            Format2 = (byte)(format >> 16),
                            Align = (byte)(format >> 24),
                            Flags = r.U8(style + 20),
                            Field15 = r.U8(style + 21),
                            Field16 = r.U16(style + 22)
                        }
                    });
                }
                else
                {
                    int widget = (int)(value & 0xFFFF);
                    states.Add(new MenuItemState
                    {
                        Code = code,
                        Kind = kind,
                        Field2 = f2,
                        WidgetIndex = widget == 0xFFFF ? -1 : widget,
                        WidgetFlags = (ushort)(value >> 16)
                    });
                }
            }
            var links = new List<MenuLink>();
            foreach (uint lo in r.NullTerminatedList(r.U32(o + 8)))
            {
                links.Add(new MenuLink(r.U8(lo), (MenuState)r.U8(lo + 1), r.U16(lo + 2)));
            }
            var actions = new List<MenuAction>();
            foreach (uint ao in r.NullTerminatedList(r.U32(o + 4)))
            {
                actions.Add(ReadAction(r, ao));
            }
            int[]? extra = null;
            uint extraOffset = r.U32(o + 44);
            if (extraOffset != 0)
            {
                extra = new int[11];
                for (int i = 0; i < 11; i++) extra[i] = r.I32(extraOffset + (uint)i * 4);
            }
            return new MenuItem
            {
                Index = r.U16(o + 54),
                Delay = r.Fx(o + 20),
                Field18 = r.Fx(o + 24),
                Field1C = r.U32(o + 28),
                X = r.Fx(o + 32),
                Y = r.Fx(o + 36),
                Depth = r.Fx(o + 40),
                Field2C = extra,
                Field30 = r.U32(o + 48),
                Field34 = r.U8(o + 52),
                Field35 = r.U8(o + 53),
                Flags = r.U8(o + 56),
                Field39 = r.U8(o + 57),
                Field3A = r.U16(o + 58),
                States = states,
                Links = links,
                Actions = actions
            };
        }

        private static MenuAction ReadAction(Reader r, uint o)
        {
            var calls = new List<(int, int)>();
            foreach (uint p in r.NullTerminatedList(r.U32(o + 16)))
            {
                calls.Add((r.I32(p), r.I32(p + 4)));
            }
            return new MenuAction
            {
                Kind = r.U16(o),
                Field2 = r.U8(o + 2),
                Flags = r.U8(o + 3),
                Field4 = r.U32(o + 4),
                RectX = (short)r.U16(o + 8),
                RectY = (short)r.U16(o + 10),
                RectW = (short)r.U16(o + 12),
                RectH = (short)r.U16(o + 14),
                Calls = calls,
                Item = r.U16(o + 20),
                TargetPage = r.U8(o + 22),
                Field17 = r.U8(o + 23)
            };
        }

        private readonly struct Reader
        {
            private readonly byte[] _data;
            public Reader(byte[] data) => _data = data;
            public byte U8(uint o) => _data[o];
            public ushort U16(uint o) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan((int)o));
            public uint U32(uint o) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)o));
            public int I32(uint o) => BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan((int)o));
            public float Fx(uint o) => I32(o) / 4096f; // 20.12 fixed point

            public List<uint> NullTerminatedList(uint o)
            {
                var list = new List<uint>();
                if (o == 0) return list;
                for (; ; o += 4)
                {
                    uint v = U32(o);
                    if (v == 0) return list;
                    list.Add(v);
                }
            }

            public string CString(uint o)
            {
                int end = Array.IndexOf(_data, (byte)0, (int)o);
                return Encoding.Latin1.GetString(_data, (int)o, end - (int)o);
            }
        }
    }
}
