using System.IO;
using System.Linq;
using System.Text;

// A readable listing of the whole menu graph (every page, item, state, link, action and timer), for comparing ROM
// revisions and checking the recomp's own additions. Used by the PC tool (-fedump) and by the app on a device
// (GameActivity --es fedump 1 writes it beside the user's files), so a device's own extraction can be inspected.
namespace MphRecomp.Frontend
{
    public static class MenuDump
    {
        public static string Describe(MenuFile file, MenuStrings strings, MenuWidgets? widgets = null, string? title = null)
        {
            string Short(int w)
            {
                if (w < 0) return "none";
                MenuWidget mw = file.Widgets[w];
                string anim = Path.GetFileName((mw.AnimPath ?? mw.ModelPath).Replace('\\', '/'));
                string name = mw.ModelPath.Split('\\')[0] + "/" + anim.Replace("_Anim.bin", "");
                return widgets == null ? name : $"{name} ({widgets.FrameCount(w)}f)";
            }
            string Esc(string s) => s.Replace("\n", "\\n");
            var sb = new StringBuilder();
            sb.AppendLine($"# MPH front-end menu graph{(title == null ? "" : " -- " + title)}");
            sb.AppendLine();
            sb.AppendLine("Coordinates: text/touch = DS pixels, Y up from the bottom of the touch screen (0..191 touch, 192..383 top).");
            sb.AppendLine("States: 1 Hidden, 2 Idle, 3 Selected, 4 Focused, 7 any; transitions = to*8+from. Action kind = the game's menu key");
            sb.AppendLine("bits (1 A, 2 B, 4 X, 8 Y, 16 L, 32 R, 64 Start, 128 Select, 256 Up, 512 Down, 1024 Left, 2048 Right, 4095 any);");
            sb.AppendLine("kind 0 = runs when the item arrives in state Field2,");
            sb.AppendLine("or with a rectangle a touch-only button. Rect: f4 bit 0 = centre + half size, else two corners.");
            sb.AppendLine($"{file.Pages.Count} pages, {file.Widgets.Count} widget refs, {strings.Strings.Count} strings ({strings.RomCount} from the ROM)");
            sb.AppendLine();
            foreach (MenuPage page in file.Pages)
            {
                sb.AppendLine($"## Page {page.Index}  (f0 0x{page.Field0:X}, f14 0x{page.Field14:X}, f16 0x{page.Field16:X}, " +
                    $"f18 0x{page.Field18:X}, items {page.ItemCount}/{page.Items.Count}, group {page.Field1D}, f1E {page.Field1E}, flags 0x{page.Flags:X})");
                foreach (MenuAction a in page.Actions) sb.AppendLine("- page action: " + Action(a));
                foreach (MenuTimer t in page.Timers) sb.AppendLine($"- timer at frame {t.Delay}: {Action(t.Action)}");
                foreach (MenuItem it in page.Items)
                {
                    sb.Append($"- [{it.Index}] at ({it.X}, {it.Y}) depth {it.Depth} delay {it.Delay} init {(MenuState)it.Field34}/{(MenuState)it.Field35}");
                    if (it.Field18 != 0) sb.Append($" f18 {it.Field18}");
                    if (it.Flags != 0) sb.Append($" flags 0x{it.Flags:X}");
                    if (it.Field2C != null) sb.Append($" extra [{string.Join(",", it.Field2C)}]");
                    sb.AppendLine();
                    foreach (MenuItemState s in it.States)
                    {
                        string what = s.Text != null
                            ? $"text #{s.Text.StringId} \"{Esc(strings[s.Text.StringId])}\" colour {s.Text.StartColor:X8}->{s.Text.EndColor:X8} " +
                              $"over {s.Text.Duration} align {s.Text.Align} wrap {s.Text.WrapWidth} size {s.Text.Size} fmt {s.Text.Format1:X2}{s.Text.Format2:X2}"
                            : Short(s.WidgetIndex) + (s.WidgetFlags != 0 ? $" flags 0x{s.WidgetFlags:X}" : "");
                        sb.AppendLine($"    - {MenuStateCode.Name(s.Code)}: {what}");
                    }
                    foreach (MenuLink l in it.Links)
                        sb.AppendLine($"    - link: on {MenuStateCode.Name(l.Event)} -> item {l.Target} to {l.State}");
                    foreach (MenuAction a in it.Actions) sb.AppendLine("    - action: " + Action(a));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string Action(MenuAction a)
        {
            string s = a.Kind == 0 ? $"on arrive {(MenuState)a.Field2}" : $"keys {(MenuKeys)a.Kind}";
            if (a.RectW != 0 || a.RectH != 0 || a.RectX != 0 || a.RectY != 0) s += $" rect ({a.RectX},{a.RectY},{a.RectW},{a.RectH})";
            if (a.Calls.Count > 0) s += " calls " + string.Join(" ", a.Calls.Select(c => $"({c.A},{c.B})"));
            if (a.TargetPage != 0xFF) s += $" -> page {a.TargetPage}";
            if (a.Item != 0xFFFF) s += $" item {a.Item}";
            if (a.Field17 != 0) s += $" f17 {a.Field17}";
            if (a.Flags != 0) s += $" flags 0x{a.Flags:X}";
            if (a.Field4 != 0) s += $" f4 0x{a.Field4:X}";
            return s;
        }
    }
}
