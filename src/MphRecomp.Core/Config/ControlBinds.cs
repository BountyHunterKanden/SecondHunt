using System;
using System.Collections.Generic;
using System.Linq;

// The control customizer's model (OPTIONS > CONTROLS > CONTROLLER / KEYBOARD & MOUSE, also opened from the gunship's
// OPTIONS): the game functions a player can move to other buttons, their default buttons -- the layouts the app has
// always had, the Prime-style pad of CampaignRenderer.BuildInput and MphRead's own PC controls (PlayerControls.GetDefault)
// -- and the player's choices, kept in RecompSettings.PadBinds / KeyBinds (a function that isn't listed has its
// defaults). Buttons are names: a controller's are PadButton's (MphRecomp.App), a PC's are OpenTK Keys names plus
// "Mouse.Left" / "Mouse.Right" / "Mouse.Middle" / "Mouse.Button4" / "Mouse.Button5" and "Wheel.Up" / "Wheel.Down".
// A function has one or two buttons. Giving a button to a function takes it from any other function on the same device,
// and a function left with none gets the first one's old buttons (a swap), so nothing ends up out of reach.
// Fixed, never offered: the sticks, Start (pause) and Select (map), L3 (the status text), the menus' and dialogs' own
// buttons; on a PC Esc / Tab (pause), M (map), the arrow keys (the controller's D-pad), F1-F12 and the 1-9 weapon keys.
namespace MphRecomp.Config
{
    public static class ControlBinds
    {
        public sealed record Function(string Id, string Label, string Description, string[] Defaults);

        // function ids (CampaignRenderer.BuildInput reads the controller's, KeyboardMouseInput the keyboard's)
        public const string Fire = "fire", Jump = "jump", Morph = "morph", WeaponMenu = "weaponmenu", ScanZoom = "scanzoom",
            Zoom = "zoom", ScanVisor = "scanvisor", Missiles = "missiles", PrevWeapon = "prevweapon", NextWeapon = "nextweapon",
            MissileToggle = "missiletoggle", Forward = "forward", Back = "back", Left = "left", Right = "right",
            ZoomScan = "zoomscan", BombScan = "bombscan";

        // labels: up to two short lines (a row's label); descriptions: the description band (wrap 230)
        public static readonly Function[] PadFunctions =
        {
            new(Fire, "fire", "fires your weapon. in the morph ball, lays bombs.", new[] { "R2", "B" }),
            new(Jump, "jump", "jumps. in the morph ball, boosts.", new[] { "A", "L1" }),
            new(Morph, "morph\nball", "rolls into the morph ball and back out.", new[] { "X" }),
            new(WeaponMenu, "weapon\nmenu", "hold for the weapon menu, then point the right stick at a weapon.", new[] { "Y" }),
            new(ScanZoom, "scan /\nzoom", "scans with the scan visor on, zooms the rest of the time.", new[] { "L2" }),
            new(Zoom, "zoom", "zooms in.", new[] { "ThumbR" }),
            new(ScanVisor, "scan\nvisor", "the scan visor on or off. in a match, where there's no visor: the power beam.", new[] { "DpadUp" }),
            new(Missiles, "missiles", "picks missiles.", new[] { "DpadDown" }),
            new(PrevWeapon, "previous\nweapon", "the previous weapon you have, with ammo.", new[] { "DpadLeft" }),
            new(NextWeapon, "next\nweapon", "the next weapon you have, with ammo.", new[] { "DpadRight" }),
            new(MissileToggle, "missiles /\npower", "switches between missiles and the power beam.", new[] { "R1" }),
        };

        public static readonly Function[] KeyFunctions =
        {
            new(Forward, "move\nforward", "walks forward. in the morph ball, rolls.", new[] { "W" }),
            new(Back, "move\nback", "walks back. in the morph ball, rolls.", new[] { "S" }),
            new(Left, "move\nleft", "steps left. in the morph ball, rolls.", new[] { "A" }),
            new(Right, "move\nright", "steps right. in the morph ball, rolls.", new[] { "D" }),
            new(Fire, "fire", "fires your weapon. in the morph ball, lays bombs.", new[] { "Mouse.Left" }),
            new(ZoomScan, "zoom /\nscan", "zooms in. scans with the scan visor on.", new[] { "Mouse.Right" }),
            new(Jump, "jump", "jumps. in the morph ball, boosts.", new[] { "Space" }),
            new(Morph, "morph\nball", "rolls into the morph ball and back out.", new[] { "C" }),
            new(BombScan, "bombs /\nscan", "lays bombs in the morph ball. scans with the scan visor on.", new[] { "Q" }),
            new(ScanVisor, "scan\nvisor", "the scan visor on or off.", new[] { "E" }),
            new(WeaponMenu, "weapon\nmenu", "hold for the weapon menu, then move the mouse toward a weapon.", new[] { "Mouse.Middle" }),
            new(NextWeapon, "next\nweapon", "the next weapon you have, with ammo.", new[] { "Wheel.Down" }),
            new(PrevWeapon, "previous\nweapon", "the previous weapon you have, with ammo.", new[] { "Wheel.Up" }),
        };

        public static Function[] Functions(bool keys) => keys ? KeyFunctions : PadFunctions;

        private static Dictionary<string, string[]> Map(RecompSettings s, bool keys) => keys ? s.KeyBinds : s.PadBinds;

        private static Function? Find(bool keys, string id) => Functions(keys).FirstOrDefault(f => f.Id == id);

        // the buttons a function has now
        public static string[] Get(RecompSettings s, bool keys, string id)
            => Map(s, keys).TryGetValue(id, out string[]? b) && b != null ? b : Find(keys, id)?.Defaults ?? Array.Empty<string>();

        // `button` does `id` now: in place of what it had, or (add) as its second button
        public static void Assign(RecompSettings s, bool keys, string id, string button, bool add)
        {
            string[] old = Get(s, keys, id);
            string[] mine = add && old.Length > 0 && old[0] != button ? new[] { old[0], button } : new[] { button };
            var changes = new Dictionary<string, string[]> { [id] = mine };
            foreach (Function f in Functions(keys))
            {
                if (f.Id == id) continue;
                string[] theirs = Get(s, keys, f.Id);
                if (!theirs.Contains(button)) continue;
                string[] left = theirs.Where(b => b != button).ToArray();
                if (left.Length == 0) left = old.Where(b => !mine.Contains(b)).ToArray(); // a swap
                if (left.Length > 0) changes[f.Id] = left;
            }
            foreach ((string fid, string[] buttons) in changes) Store(s, keys, fid, buttons);
        }

        // only what differs from the defaults is kept, so a later change of a default reaches untouched functions
        private static void Store(RecompSettings s, bool keys, string id, string[] buttons)
        {
            if (Find(keys, id) is Function f && f.Defaults.SequenceEqual(buttons)) Map(s, keys).Remove(id);
            else Map(s, keys)[id] = buttons;
        }

        public static void Reset(RecompSettings s, bool keys) => Map(s, keys).Clear();

        public static bool IsDefault(RecompSettings s, bool keys) => Map(s, keys).Count == 0;

        private static readonly HashSet<string> PadNames = new(StringComparer.Ordinal)
        {
            "A", "B", "X", "Y", "L1", "R1", "L2", "R2", "ThumbR", "DpadUp", "DpadDown", "DpadLeft", "DpadRight",
        };

        private static readonly HashSet<string> ReservedKeys = new(StringComparer.Ordinal)
        {
            "Unknown", "Escape", "Tab", "M", "Up", "Down", "Left", "Right", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9",
        };

        // can a player give this button to a function (see the header: the fixed ones can't)
        public static bool Allowed(bool keys, string button)
        {
            if (!keys) return PadNames.Contains(button);
            if (ReservedKeys.Contains(button)) return false;
            return !(button.Length > 1 && button[0] == 'F' && int.TryParse(button.AsSpan(1), out _)); // F1-F25
        }

        // a button's name as a value box shows it (about seven letters fit)
        public static string Short(string button) => button switch
        {
            "ThumbL" => "L3", "ThumbR" => "R3", "DpadUp" => "D-UP", "DpadDown" => "D-DOWN", "DpadLeft" => "D-LEFT",
            "DpadRight" => "D-RIGHT",
            "Mouse.Left" => "LCLICK", "Mouse.Right" => "RCLICK", "Mouse.Middle" => "MCLICK", "Mouse.Button4" => "MOUSE4",
            "Mouse.Button5" => "MOUSE5", "Wheel.Up" => "WHL UP", "Wheel.Down" => "WHL DN",
            "LeftShift" => "LSHIFT", "RightShift" => "RSHIFT", "LeftControl" => "LCTRL", "RightControl" => "RCTRL",
            "LeftAlt" => "LALT", "RightAlt" => "RALT", "Backspace" => "BKSP", "CapsLock" => "CAPS", "KeyPadEnter" => "NUMENTR",
            _ when button.StartsWith("KeyPad", StringComparison.Ordinal) => "NUM" + Cut(button[6..].ToUpperInvariant(), 4),
            _ => Cut(button.ToUpperInvariant(), 7),
        };

        private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

        // a function's buttons for its value box, on one line (the customizer's boxes fit about 96 px: "LCLICK / MCLICK"
        // is 91); a longer pair drops the spaces
        public static string Value(string[] buttons)
        {
            string spaced = string.Join(" / ", buttons.Select(Short));
            return spaced.Length <= 15 ? spaced : string.Join("/", buttons.Select(Short));
        }
    }
}
