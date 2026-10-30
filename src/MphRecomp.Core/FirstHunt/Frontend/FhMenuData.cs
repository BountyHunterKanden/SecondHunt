using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using MphRead;

namespace MphRecomp.FirstHunt
{
    // First Hunt's front end data (docs/FIRST_HUNT_PORT.md P4; fh_research p4_frontend.md), read from the user's import:
    //   - menu_state.bin (menusTD, unpacked at import): 349 named states, each a list of {model node, frame start, end,
    //     loop} the player steps at 30 fps (0x2049f8c-0x204a134)
    //   - the screen table in arm9 (0x207c7f0, 13 x 0xa4): each page's model, label, top-screen backdrop, item list,
    //     handler and open sound; items (0x74 bytes) are the touch buttons and the text labels
    public sealed class FhMenuStateNode
    {
        public string Node { get; init; } = "";
        public short Start { get; init; }
        public short End { get; init; }
        public short Loop { get; init; }
    }

    public sealed class FhMenuState
    {
        public string Name { get; init; } = "";
        public int Length { get; init; } // frames at 30 fps
        public IReadOnlyList<FhMenuStateNode> Nodes { get; init; } = Array.Empty<FhMenuStateNode>();
    }

    public sealed class FhMenuItem
    {
        public int X0, Y0, X1, Y1; // as stored: a button's touch box with y negated (bottom screen), a text's anchor and extent
        public string Label = ""; // a button's target screen label or action keyword, or a text's literal text
        public string StateBase = ""; // a button's menu_state name base ("" = a text item)
        public string Node = ""; // the model node it drives or anchors to
        public uint Flags; // a button's flags (2 = hidden), a text's style word (0xc000 centred, 0x8000 left, 0xa000 right...)
        public uint TextParam; // a text's glyph height (8, or 0 when the letters are in the model)
        public uint Extra;

        public bool IsButton => StateBase.Length > 0;

        // a button's touch box in bottom-screen pixels (y down from the screen's top); flag 1 makes it a circle, centre
        // x0, -y0 and radius squared x1 (the hex keys of Edit Name, the title's hidden Movie hex)
        public bool Circle => IsButton && (Flags & 1) != 0;

        public bool Contains(float x, float y)
        {
            if (Circle)
            {
                float dx = x - X0, dy = y + Y0;
                return dx * dx + dy * dy <= X1;
            }
            return x >= Math.Min(X0, X1) && x <= Math.Max(X0, X1) && y >= Math.Min(-Y0, -Y1) && y <= Math.Max(-Y0, -Y1);
        }

        // the top of its box (pad order: top to bottom, then left to right)
        public float Top => Circle ? -Y0 - MathF.Sqrt(X1) : Math.Min(-Y0, -Y1);
        public float Left => Circle ? X0 - MathF.Sqrt(X1) : Math.Min(X0, X1);
    }

    public sealed class FhMenuScreen
    {
        public int Index { get; init; }
        public string Name { get; init; } = ""; // the model and state prefix (title, main_menu, training...)
        public string Label { get; init; } = ""; // what buttons name to go here (Title, Main Menu...)
        public string Backdrop { get; init; } = ""; // <backdrop>_bg_Model.bin on the top screen
        public int Handler { get; init; }
        public int OpenSound { get; init; } // MENUSFX index, -1 none
        public IReadOnlyList<FhMenuItem> Items { get; init; } = Array.Empty<FhMenuItem>();

        public override string ToString() => $"{Index} {Name} '{Label}' bg {Backdrop} items {Items.Count} sfx {OpenSound}";
    }

    public static class FhMenuData
    {
        public const int ScreenTable = 0x207c7f0;
        public const int ScreenCount = 13;
        public const int ScreenSize = 0xa4;
        public const int ItemSize = 0x74;

        // the screens' indices (the table's order)
        public const int Title = 0, MainMenu = 1, Training = 2, Multiplayer = 3, Options = 4, EditName = 5, TrainingHiScore = 6,
            MultiHiScore = 7, HostStart = 8, Configuration = 9, ClientStart = 10, Records = 11, Popup = 12;

        public static Dictionary<string, FhMenuState> ReadStates(byte[] file)
        {
            var states = new Dictionary<string, FhMenuState>(StringComparer.Ordinal);
            int at = 0;
            for (int guard = 0; guard < 4096 && at >= 0 && at + 0x30 <= file.Length; guard++)
            {
                string name = CString(file, at, 0x20);
                int count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 0x24));
                int length = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(at + 0x28));
                int next = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 0x2c));
                var nodes = new List<FhMenuStateNode>(count);
                for (int i = 0; i < count; i++)
                {
                    int n = at + 0x30 + i * 0x24;
                    nodes.Add(new FhMenuStateNode
                    {
                        Node = CString(file, n, 0x18),
                        Start = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(n + 0x1e)),
                        End = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(n + 0x20)),
                        Loop = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(n + 0x22))
                    });
                }
                states[name] = new FhMenuState { Name = name, Length = length, Nodes = nodes };
                if (next == 0) break;
                at = next;
            }
            return states;
        }

        // the screen table, from the imported arm9 (FirstHuntRules holds it)
        public static List<FhMenuScreen> ReadScreens()
        {
            var screens = new List<FhMenuScreen>();
            for (int i = 0; i < ScreenCount; i++)
            {
                int s = ScreenTable + i * ScreenSize;
                int items = Int(s + 0x48);
                int count = Int(s + 0x4c);
                var list = new List<FhMenuItem>();
                for (int k = 0; k < count && items != 0; k++)
                {
                    int it = items + k * ItemSize;
                    int xy0 = Int(it), xy1 = Int(it + 4);
                    list.Add(new FhMenuItem
                    {
                        X0 = (short)xy0,
                        Y0 = (short)(xy0 >> 16),
                        X1 = (short)xy1,
                        Y1 = (short)(xy1 >> 16),
                        Label = Str(it + 8, 0x20),
                        StateBase = Str(it + 0x28, 0x20),
                        Node = Str(it + 0x48, 0x20),
                        Flags = (uint)Int(it + 0x68),
                        TextParam = (uint)Int(it + 0x6c),
                        Extra = (uint)Int(it + 0x70)
                    });
                }
                screens.Add(new FhMenuScreen
                {
                    Index = i,
                    Name = Str(s, 0x18),
                    Label = Str(s + 0x18, 0x18),
                    Backdrop = Str(s + 0x30, 0x18),
                    Handler = Int(s + 0x50),
                    OpenSound = Int(s + 0x54),
                    Items = list
                });
            }
            return screens;
        }

        // a string at a cart address (the arm9 image)
        public static string Str(int address, int max)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < max; i += 4)
            {
                int word = Int(address + i);
                for (int b = 0; b < 4 && i + b < max; b++)
                {
                    char c = (char)(word >> (8 * b) & 0xff);
                    if (c == 0) return sb.ToString();
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static int Int(int address) => FirstHuntRules.ReadArm9Int(address) ?? 0;

        private static string CString(byte[] b, int at, int max)
        {
            int end = at;
            while (end < at + max && end < b.Length && b[end] != 0) end++;
            return Encoding.ASCII.GetString(b, at, end - at);
        }
    }
}
