using System;
using System.Collections.Generic;
using System.Linq;

// The control customizer's model (OPTIONS > CONTROLS, also opened from the gunship's OPTIONS; RecompMenus.Controls.cs): the
// game functions a player can move to other buttons, their default buttons -- the layouts the app has always had, the
// Prime-style pad of CampaignRenderer.BuildInput and MphRead's own PC controls (PlayerControls.GetDefault) -- and the
// player's choices, kept in RecompSettings.PadBinds / KeyBinds (a function that isn't listed has its defaults). Buttons are
// names: a controller's are PadButton's (MphRecomp.App), a PC's are OpenTK Keys names plus "Mouse.Left" / "Mouse.Right" /
// "Mouse.Middle" / "Mouse.Button4" / "Mouse.Button5" and "Wheel.Up" / "Wheel.Down".
// A combo (owner 2026-10-11) is two or three of them held together, joined by '+' ("Select+DpadUp"): it does its function
// when its last button goes down, and while it's held its buttons don't also do their own (PadBindings,
// KeyboardMouseInput).
// One function a row (owner 2026-10-17): each function has two slots, PRIMARY and SECONDARY (a button
// or combo each, "" = blank). Setting a slot never refuses and never takes the button from another function: one button
// can do several (L2 is the weapon menu, scan and zoom by default; CampaignRenderer.BuildInput sorts out which one a press
// is), and the customizer says what else it does (SharedWith). CLEAR blanks both slots.
// Groups (owner 2026-10-11): the hunter's functions, the Trooper's Scout Drone's (read instead of the hunter's while the
// drone is out, so the two lists can share buttons) and the common ones (both: push to talk, the map, the scoreboard, chat;
// on a PC the movement keys). The Trooper deploys and recalls the drone with the hunter's MORPH (2026-10-19: no deploy
// function of its own, it clashed with morph), so MORPH is read with the drone's too and can't share a button with them; an
// old "dronedeploy" entry in the saved lists is dropped by Tidy like any id no longer listed.
// Fixed, never offered: the sticks, Start (pause), L3 (the status text; L3 + R3 the gun-adjust pause), the menus' and
// dialogs' own buttons; on a PC Esc (pause), M (map), the arrow keys (the controller's D-pad) and F1-F12. (R3 = push to
// talk and Select = the map and the live scoreboard are functions since owner 2026-10-11; Tab is a PC's scoreboard, owner
// 2026-10-11, no longer its pause.)
// Weapons (owner 2026-10-15: "bindable keys to each weapon"): on a PC's keyboard a function per weapon, pressed = pick it
// (as the PC's 1-9 keys always did, now their defaults), the WEAPONS section. A controller has only missiles' D-down (owner
// 2026-10-17, C9: the per-weapon buttons off the controller, as before 10-15).
namespace MphRecomp.Config
{
    public static class ControlBinds
    {
        // Touch: the touch controls' buttons that count for it besides its single-button defaults (PadBindings: the touch
        // SCAN, PadButton Y, still zooms outside the scan visor)
        public sealed record Function(string Id, string Label, string Description, string[] Defaults, string Group = Hunter,
            string[]? Touch = null);

        // (ZoomStep: zoom in / zoom out, read only while zoomed with ZOOM STEP on, when they take their buttons from the
        // hunter's functions; so, like the drone's, they can share a button with those)
        public const string Hunter = "hunter", Drone = "drone", Common = "common", ZoomStep = "zoomstep";
        // a function's slots (PRIMARY, SECONDARY); a combo's most buttons
        public const int MaxButtons = 2, MaxCombo = 3;
        public const int Version = 2; // RecompSettings.BindsVersion (Tidy)

        // function ids (CampaignRenderer.BuildInput reads the controller's, KeyboardMouseInput the keyboard's)
        public const string Fire = "fire", Jump = "jump", Morph = "morph", WeaponMenu = "weaponmenu", Scan = "scan",
            Zoom = "zoom", ScanVisor = "scanvisor", Missiles = "missiles", PrevWeapon = "prevweapon", NextWeapon = "nextweapon",
            MissileToggle = "missiletoggle", Forward = "forward", Back = "back", Left = "left", Right = "right",
            Bombs = "bombs", Talk = "talk", Chat = "chat", Map = "map", Scoreboard = "scoreboard",
            DroneFire = "dronefire", DroneLaser = "dronelaser", DroneRise = "dronerise",
            PowerBeam = "powerbeam", VoltDriver = "voltdriver", Battlehammer = "battlehammer", Imperialist = "imperialist",
            Judicator = "judicator", Magmaul = "magmaul", ShockCoil = "shockcoil", OmegaCannon = "omegacannon",
            PirateBlaster = "pirateblaster", ZoomIn = "zoomin", ZoomOut = "zoomout";

        // the weapon functions (the keyboard's WEAPONS section) and the weapon each picks (MphRead BeamType's number)
        public static readonly (string Id, int Beam)[] WeaponPicks =
        {
            (PowerBeam, 0), (Missiles, 2), (VoltDriver, 1), (Battlehammer, 3), (Imperialist, 4), (Judicator, 5), (Magmaul, 6),
            (ShockCoil, 7), (OmegaCannon, 8), (PirateBlaster, 11),
        };

        public static bool IsWeapon(string id) => Array.Exists(WeaponPicks, w => w.Id == id);

        // labels: up to two short lines (the customizer shows them on one); descriptions: the description band
        public static readonly Function[] PadFunctions =
        {
            new(Fire, "fire", "fires your weapon. in the morph ball, lays bombs.", new[] { "R2", "B" }),
            new(Jump, "jump", "jumps. in the morph ball, boosts.", new[] { "A", "L1" }),
            new(Morph, "morph", "enters and exits alt form.", new[] { "X" }),
            new(WeaponMenu, "weapon\nmenu", "hold for the weapon ring, point the right stick at a weapon, let go to equip it.", new[] { "L2" }),
            // (owner 2026-10-17, C10: scan and zoom their own rows; L2 is the weapon menu, scan and zoom at once, sorted by
            // what's going on: CampaignRenderer.BuildInput)
            new(Scan, "scan", "with the scan visor on hold to scan.", new[] { "Y", "L2" }),
            new(Zoom, "imp\nzoom", "zooms in with the imperialist. if this button is also assigned to weapon ring, the weapon ring will "
                + "require a short hold to appear when imp is equipped.", new[] { "L2" }, Touch: new[] { "Y" }),
            // (owner 2026-10-17: the D-pad by default; only while zoomed, and then they do nothing else)
            new(ZoomIn, "zoom\nin", "with zoom step on, this button will perform the same auto zoom vanilla does when a target is in "
                + "sight, but only when you press it while the reticle is lit.", new[] { "DpadUp" }, ZoomStep),
            new(ZoomOut, "zoom\nout", "with zoom step on, this button will step down from second and first zoom level back to "
                + "regular aiming.", new[] { "DpadDown" }, ZoomStep),
            new(ScanVisor, "scan\nvisor", "turn the scan visor on or off in campaign.", new[] { "DpadUp" }),
            new(Missiles, "missiles", "switches to missiles.", new[] { "DpadDown" }),
            new(PrevWeapon, "previous\nweapon", "the previous weapon you have with ammo.", new[] { "DpadLeft" }),
            new(NextWeapon, "next\nweapon", "the next weapon you have with ammo.", new[] { "DpadRight" }),
            new(MissileToggle, "missiles /\npower", "switches between missiles and the power beam.", new[] { "R1" }),
            // (no default button: owner 2026-10-11, Select is the live scoreboard; chat is also in the pause menu; its "chat
            // mode" row comes after it, RecompMenus.Controls.cs)
            new(Chat, "chat", "online quick messages: point the right stick at one, or tap to type.", Array.Empty<string>(), Common),
            // (owner 2026-10-11: R3 and Select rebindable; the touch controls' TALK counts for talk, their CHAT never for the
            // map or the scoreboard)
            new(Talk, "push to\ntalk", "online voice chat: hold to talk.", new[] { "ThumbR" }, Common),
            new(Map, "map", "in the campaign, opens the map.", new[] { "Select" }, Common),
            new(Scoreboard, "scoreboard", "in a match, hold for the live scoreboard.", new[] { "Select" }, Common),
            // the Trooper's Scout Drone (2026-10-11: its own buttons, read while the drone is out; deploy / recall is MORPH,
            // 2026-10-19)
            new(DroneFire, "drone\nblasters", "hold to unfold the drone's blasters and fire.", new[] { "R2", "B" }, Drone),
            new(DroneLaser, "cutting\nlaser", "fire the drone's sweeping laser.", new[] { "L2" }, Drone),
            new(DroneRise, "rise", "hold to fly the drone up, let go to sink.", new[] { "A", "L1" }, Drone),
        };

        public static readonly Function[] KeyFunctions =
        {
            new(Forward, "move\nforward", "walks forward. in the morph ball, rolls.", new[] { "W" }, Common),
            new(Back, "move\nback", "walks back. in the morph ball, rolls.", new[] { "S" }, Common),
            new(Left, "move\nleft", "steps left. in the morph ball, rolls.", new[] { "A" }, Common),
            new(Right, "move\nright", "steps right. in the morph ball, rolls.", new[] { "D" }, Common),
            new(Fire, "fire", "fires your weapon. in the morph ball, lays bombs.", new[] { "Mouse.Left" }),
            // (owner 2026-10-17, C10: zoom / scan and bombs / scan split; right click still zooms and scans, Q bombs and scans)
            new(Zoom, "imp\nzoom", "zooms in with the imperialist.", new[] { "Mouse.Right" }),
            new(Scan, "scan", "with the scan visor on hold to scan.", new[] { "Mouse.Right", "Q" }),
            new(ZoomIn, "zoom\nin", "with zoom step on, this key will perform the same auto zoom vanilla does when a target is in "
                + "sight, but only when you press it while the reticle is lit.", new[] { "R" }, ZoomStep),
            new(ZoomOut, "zoom\nout", "with zoom step on, this key will step down from second and first zoom level back to "
                + "regular aiming.", new[] { "F" }, ZoomStep),
            new(Jump, "jump", "jumps. in the morph ball, boosts.", new[] { "Space" }),
            new(Morph, "morph", "enters and exits alt form.", new[] { "C" }),
            new(Bombs, "bombs", "lays bombs in the morph ball.", new[] { "Q" }),
            new(ScanVisor, "scan\nvisor", "turn the scan visor on or off in campaign.", new[] { "E" }),
            new(WeaponMenu, "weapon\nmenu", "hold for the weapon ring, move the mouse toward a weapon, let go to equip it.", new[] { "Mouse.Middle" }),
            new(NextWeapon, "next\nweapon", "the next weapon you have with ammo.", new[] { "Wheel.Down" }),
            new(PrevWeapon, "previous\nweapon", "the previous weapon you have with ammo.", new[] { "Wheel.Up" }),
            new(PowerBeam, "power\nbeam", "picks the power beam.", new[] { "D1" }),
            new(Missiles, "missiles", "switches to missiles.", new[] { "D2" }),
            new(VoltDriver, "volt\ndriver", "picks the volt driver, when you have it.", new[] { "D3" }),
            new(Battlehammer, "battle-\nhammer", "picks the battlehammer, when you have it.", new[] { "D4" }),
            new(Imperialist, "imperialist", "picks the imperialist, when you have it.", new[] { "D5" }),
            new(Judicator, "judicator", "picks the judicator, when you have it.", new[] { "D6" }),
            new(Magmaul, "magmaul", "picks the magmaul, when you have it.", new[] { "D7" }),
            new(ShockCoil, "shock\ncoil", "picks the shock coil, when you have it.", new[] { "D8" }),
            new(OmegaCannon, "omega\ncannon", "picks the omega cannon, when you have it.", new[] { "D9" }),
            new(PirateBlaster, "pirate\nblaster", "picks the pirate blaster, when you have it.", new[] { "D0" }),
            new(Talk, "push to\ntalk", "online voice chat: hold to talk.", new[] { "V" }, Common),
            // (owner 2026-10-11: Tab, the usual PC scoreboard key; Esc pauses, M is the map)
            new(Scoreboard, "scoreboard", "in a match, hold for the live scoreboard.", new[] { "Tab" }, Common),
            new(DroneFire, "drone\nblasters", "hold to unfold the drone's blasters and fire.", new[] { "Mouse.Left" }, Drone),
            new(DroneLaser, "cutting\nlaser", "fire the drone's sweeping laser.", new[] { "Mouse.Right" }, Drone),
            new(DroneRise, "rise", "hold to fly the drone up, let go to sink.", new[] { "Space" }, Drone),
        };

        public static Function[] Functions(bool keys) => keys ? KeyFunctions : PadFunctions;

        private static Dictionary<string, string[]> BindsOf(RecompSettings s, bool keys) => keys ? s.KeyBinds : s.PadBinds;

        private static Function? Find(bool keys, string id) => Functions(keys).FirstOrDefault(f => f.Id == id);

        public static string LabelOf(bool keys, string id) => Find(keys, id)?.Label.Replace('\n', ' ') ?? id;

        // can these two functions be pressed at the same time (then a button on both does both)
        public static bool Clash(Function a, Function b)
        {
            if (a.Group == Common || b.Group == Common || a.Group == b.Group) return true;
            // (MORPH deploys and recalls the Trooper's drone: read while it's out too)
            return a.Id == Morph && b.Group == Drone || b.Id == Morph && a.Group == Drone;
        }

        // functions made to share a button (their defaults do; BuildInput / KeyboardMouseInput sort a press out between
        // them): no heads-up for these
        private static readonly (string, string)[] Together =
        {
            (WeaponMenu, Scan), (WeaponMenu, Zoom), (Scan, Zoom), (Scan, Bombs), (Map, Scoreboard),
        };

        private static bool WorkTogether(string a, string b) => Array.Exists(Together, p => p == (a, b) || p == (b, a));

        // the buttons (and combos) a function has now, slot by slot ("" = a blank slot)
        public static string[] Get(RecompSettings s, bool keys, string id)
            => BindsOf(s, keys).TryGetValue(id, out string[]? b) && b != null ? b : Find(keys, id)?.Defaults ?? Array.Empty<string>();

        // one slot (0 PRIMARY, 1 SECONDARY): its button or combo, "" = blank
        public static string Slot(RecompSettings s, bool keys, string id, int slot)
        {
            string[] now = Get(s, keys, id);
            return slot < now.Length ? now[slot] : "";
        }

        // a combo's buttons; a plain button is a combo of one
        public static string[] Parts(string button) => button.Split('+', StringSplitOptions.RemoveEmptyEntries);

        public static bool IsCombo(string button) => button.Contains('+');

        // a combo's buttons in one order (the controller's in PadButton order, keys alphabetical), so the same combo
        // pressed in another order is the same binding
        public static string Normalize(bool keys, string button)
        {
            if (!IsCombo(button)) return button;
            IEnumerable<string> parts = Parts(button).Distinct();
            parts = keys ? parts.OrderBy(p => p, StringComparer.Ordinal) : parts.OrderBy(p => Array.IndexOf(PadOrder, p));
            return string.Join("+", parts);
        }

        // `button` in one slot of `id` (owner 2026-10-17, C11: never refused, never taken from another function); the
        // function's other slot loses it if it had it. Returns the other functions (that can be pressed with this one) it
        // also does, for the customizer's heads-up.
        public static IReadOnlyList<string> SetSlot(RecompSettings s, bool keys, string id, int slot, string button)
        {
            button = Normalize(keys, button);
            string[] now = Get(s, keys, id);
            var slots = new string[MaxButtons];
            for (int k = 0; k < MaxButtons; k++) slots[k] = k < now.Length ? now[k] : "";
            for (int k = 0; k < MaxButtons; k++)
            {
                if (k != slot && slots[k] == button) slots[k] = "";
            }
            slots[Math.Clamp(slot, 0, MaxButtons - 1)] = button;
            Store(s, keys, id, Trim(slots));
            return SharedWith(s, keys, id, button).ToList();
        }

        // CLEAR: both slots blank (the function then has no button)
        public static void Clear(RecompSettings s, bool keys, string id) => Store(s, keys, id, Array.Empty<string>());

        // the other functions (that can be pressed with `id`) the same button or combo also does, by label
        public static IEnumerable<string> SharedWith(RecompSettings s, bool keys, string id, string button)
        {
            button = Normalize(keys, button);
            Function? me = Find(keys, id);
            foreach (Function f in Functions(keys))
            {
                if (f.Id != id && (me == null || Clash(me, f)) && !WorkTogether(id, f.Id) && Get(s, keys, f.Id).Contains(button))
                {
                    yield return f.Label.Replace('\n', ' ');
                }
            }
        }

        // the other functions (that can be pressed with `id`) a combo's buttons also do on their own, for the customizer's
        // heads-up: pressing the combo's first button still does that one for a moment
        public static IEnumerable<(string Button, string Label)> ComboOverlaps(RecompSettings s, bool keys, string id, string combo)
        {
            Function? me = Find(keys, id);
            foreach (string part in Parts(combo))
            {
                foreach (Function f in Functions(keys))
                {
                    if (f.Id != id && (me == null || Clash(me, f)) && Get(s, keys, f.Id).Contains(part))
                    {
                        yield return (part, f.Label.Replace('\n', ' '));
                    }
                }
            }
        }

        // trailing blanks dropped ("L2", "" is just "L2"; "", "L2" keeps its blank PRIMARY)
        private static string[] Trim(string[] slots)
        {
            int n = slots.Length;
            while (n > 0 && slots[n - 1].Length == 0) n--;
            return slots.Take(n).ToArray();
        }

        // only what differs from the defaults is kept, so a later change of a default reaches untouched functions
        private static void Store(RecompSettings s, bool keys, string id, string[] buttons)
        {
            if (Find(keys, id) is Function f && f.Defaults.SequenceEqual(buttons)) BindsOf(s, keys).Remove(id);
            else BindsOf(s, keys)[id] = buttons;
        }

        public static void Reset(RecompSettings s, bool keys) => BindsOf(s, keys).Clear();

        public static bool IsDefault(RecompSettings s, bool keys) => BindsOf(s, keys).Count == 0;

        // The saved lists in today's layout (PadBindings.Load, KeyboardMouseInput.LoadBinds and the customizer call it; it
        // only changes anything once): from before C10 (BindsVersion 0), scan / zoom (pad), zoom / scan and bombs / scan
        // (keys) become their functions, keeping what each button did (a custom scan / zoom button: scan and zoom, the
        // weapon menu's tap zoom kept), and a custom map / scoreboard button does both. Always: ids no longer listed (the
        // controller's weapon buttons, C9) go, and a function keeps its first two buttons or combos (owner 2026-10-17).
        public static void Tidy(RecompSettings s)
        {
            if (s.BindsVersion < Version)
            {
                Dictionary<string, string[]> pad = s.PadBinds, keys = s.KeyBinds;
                if (pad.Remove("scanzoom", out string[]? scanZoom) && scanZoom != null)
                {
                    string[] zoom = Get(s, false, WeaponMenu).Concat(pad.TryGetValue(Zoom, out string[]? z) ? z : Array.Empty<string>())
                        .Concat(scanZoom).ToArray();
                    Store(s, false, Scan, Two(scanZoom));
                    Store(s, false, Zoom, Two(zoom));
                }
                if (pad.TryGetValue(Scoreboard, out string[]? board) && !pad.ContainsKey(Map)) Store(s, false, Map, Two(board));
                keys.Remove("zoomscan", out string[]? zoomScan);
                keys.Remove("bombscan", out string[]? bombScan);
                if (zoomScan != null || bombScan != null)
                {
                    if (zoomScan != null) Store(s, true, Zoom, Two(zoomScan));
                    if (bombScan != null) Store(s, true, Bombs, Two(bombScan));
                    Store(s, true, Scan, Two((zoomScan ?? new[] { "Mouse.Right" }).Concat(bombScan ?? new[] { "Q" }).ToArray()));
                }
                s.BindsVersion = Version;
            }
            foreach (bool k in new[] { false, true })
            {
                Dictionary<string, string[]> map = BindsOf(s, k);
                foreach (string id in map.Keys.ToList())
                {
                    if (Find(k, id) == null) map.Remove(id);
                    else if (map[id] == null || map[id].Length > MaxButtons) Store(s, k, id, Two(map[id] ?? Array.Empty<string>()));
                }
            }
        }

        // the first two different buttons (blanks kept in place)
        private static string[] Two(string[] buttons)
        {
            var kept = new List<string>();
            foreach (string b in buttons)
            {
                if (kept.Count == MaxButtons) break;
                if (b.Length == 0 || !kept.Contains(b)) kept.Add(b);
            }
            return Trim(kept.ToArray());
        }

        // the controller's buttons a player can give out, in PadButton order (combos are written in it)
        private static readonly string[] PadOrder =
        {
            "A", "B", "X", "Y", "L1", "R1", "L2", "R2", "ThumbR", "DpadUp", "DpadDown", "DpadLeft", "DpadRight", "Select",
        };

        private static readonly HashSet<string> PadNames = new(PadOrder, StringComparer.Ordinal);

        private static readonly HashSet<string> ReservedKeys = new(StringComparer.Ordinal)
        {
            "Unknown", "Escape", "M", "Up", "Down", "Left", "Right", // (1-9: the weapons' defaults since 2026-10-15, free to move)
        };

        // can a player give this button (or combo: two or three different allowed buttons, no mouse wheel) to a function
        // (see the header: the fixed ones can't)
        public static bool Allowed(bool keys, string button)
        {
            if (IsCombo(button))
            {
                string[] parts = Parts(button);
                return parts.Length >= 2 && parts.Length <= MaxCombo && parts.Distinct().Count() == parts.Length
                    && parts.All(p => !p.StartsWith("Wheel.", StringComparison.Ordinal) && Allowed(keys, p));
            }
            if (!keys) return PadNames.Contains(button);
            if (ReservedKeys.Contains(button)) return false;
            return !(button.Length > 1 && button[0] == 'F' && int.TryParse(button.AsSpan(1), out _)); // F1-F25
        }

        // a button's name as a cell shows it (about seven letters fit); a combo's joined by '+'
        public static string Short(string button) => IsCombo(button) ? string.Join("+", Parts(button).Select(Short)) : button switch
        {
            "ThumbL" => "L3", "ThumbR" => "R3", "DpadUp" => "D-UP", "DpadDown" => "D-DOWN", "DpadLeft" => "D-LEFT",
            "DpadRight" => "D-RIGHT",
            "Mouse.Left" => "LCLICK", "Mouse.Right" => "RCLICK", "Mouse.Middle" => "MCLICK", "Mouse.Button4" => "MOUSE4",
            "Mouse.Button5" => "MOUSE5", "Wheel.Up" => "WHL UP", "Wheel.Down" => "WHL DN",
            "LeftShift" => "LSHIFT", "RightShift" => "RSHIFT", "LeftControl" => "LCTRL", "RightControl" => "RCTRL",
            "LeftAlt" => "LALT", "RightAlt" => "RALT", "Backspace" => "BKSP", "CapsLock" => "CAPS", "KeyPadEnter" => "NUMENTR",
            _ when button.StartsWith("KeyPad", StringComparison.Ordinal) => "NUM" + Cut(button[6..].ToUpperInvariant(), 4),
            _ when button.Length == 2 && button[0] == 'D' && char.IsAsciiDigit(button[1]) => button[1..], // number row (OpenTK D0-D9)
            _ => Cut(button.ToUpperInvariant(), 7),
        };

        private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];
    }
}
