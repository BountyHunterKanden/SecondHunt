using MphRecomp.App;
using MphRecomp.App.Platform;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SecondHunt.Desktop.Screens;

// MAP MAKER E0 (dev builds, --screen mapmaker): the keyboard and mouse straight from the window for the editor screen
// (DesktopInput only routes to the menus and the game). WASD + Space/C fly, Shift faster, right mouse held looks, the
// wheel pushes and pulls (Ctrl a corner, Shift the whole face), [ ] the grid, Home back to the start, Ctrl+S saves,
// left click (or P) paints the aimed face with the paint texture (Shift: the whole block), middle click (or I) picks the
// aimed face's texture, . and , step through the textures, F5 plays the map in Solo (and F5 in the game comes back).
internal sealed class MapEditorHost
{
    readonly NativeWindow _w;
    public readonly MapEditorScreen Screen;
    bool _looking;
    readonly List<(int Frame, string Action)> _script = new();
    int _frame;

    // --mmscript (testing): "frame:action,..." with add@x;y, remove@x;y, corner@x;y (out), dent@x;y (in), face@x;y
    // (out), paint@x;y, paintblock@x;y, pick@x;y, texnext, texprev, save, play, back (in the game: to the editor)
    public void SetScript(string spec)
    {
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.IndexOf(':');
            if (colon > 0 && int.TryParse(part[..colon], out int f)) _script.Add((f, part[(colon + 1)..]));
        }
    }

    // this frame's script action, if any (counted over every frame, the game's too)
    public string? Scripted()
    {
        _frame++;
        foreach ((int f, string action) in _script)
        {
            if (f == _frame) return action;
        }
        return null;
    }

    static bool Scripted(string? action, MapEditorScreen.EditorInput input, out MapEditorScreen.EditorInput result)
    {
        result = input;
        if (action == null) return false;
        int at = action.IndexOf('@');
        if (at < 0)
        {
            if (action == "texnext") result.TextureStep = 1;
            else if (action == "texprev") result.TextureStep = -1;
            else if (action == "save") result.Save = true;
            return action == "play";
        }
        string[] xy = action[(at + 1)..].Split(';');
        result.MouseX = float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture);
        result.MouseY = float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture);
        switch (action[..at])
        {
        case "add": result.Wheel = 1; break;
        case "remove": result.Wheel = -1; break;
        case "corner": result.Wheel = 1; result.Ctrl = true; break;
        case "dent": result.Wheel = -1; result.Ctrl = true; break;
        case "face": result.Wheel = 1; result.Shift = true; break;
        case "paint": result.Paint = true; break;
        case "paintblock": result.Paint = true; result.Shift = true; break;
        case "pick": result.Eyedrop = true; break;
        }
        Log.Info("MapMaker", "script: " + action);
        return false;
    }

    public MapEditorHost(NativeWindow w, MapEditorScreen screen)
    {
        _w = w;
        Screen = screen;
    }

    // once a frame while the editor is on top; true = F5 (play)
    public bool Poll(string? action = null)
    {
        KeyboardState k = _w.KeyboardState;
        MouseState m = _w.MouseState;
        bool look = m.IsButtonDown(MouseButton.Right) && _w.IsFocused;
        if (look != _looking)
        {
            _looking = look;
            _w.CursorState = look ? CursorState.Grabbed : CursorState.Normal;
        }
        bool ctrl = k.IsKeyDown(Keys.LeftControl) || k.IsKeyDown(Keys.RightControl);
        bool shift = k.IsKeyDown(Keys.LeftShift) || k.IsKeyDown(Keys.RightShift);
        var input = new MapEditorScreen.EditorInput
        {
            Forward = k.IsKeyDown(Keys.W), Back = k.IsKeyDown(Keys.S) && !ctrl,
            Left = k.IsKeyDown(Keys.A), Right = k.IsKeyDown(Keys.D),
            Up = k.IsKeyDown(Keys.Space), Down = k.IsKeyDown(Keys.C),
            Fast = shift, Ctrl = ctrl, Shift = shift,
            Looking = look, LookDx = look ? m.Delta.X : 0, LookDy = look ? m.Delta.Y : 0,
            MouseX = m.Position.X, MouseY = m.Position.Y,
            Wheel = (int)MathF.Round(m.ScrollDelta.Y),
            GridStep = (k.IsKeyPressed(Keys.RightBracket) ? 1 : 0) - (k.IsKeyPressed(Keys.LeftBracket) ? 1 : 0),
            Home = k.IsKeyPressed(Keys.Home),
            Save = ctrl && k.IsKeyPressed(Keys.S),
            Paint = (m.IsButtonPressed(MouseButton.Left) && !ctrl) || k.IsKeyPressed(Keys.P),
            Eyedrop = m.IsButtonPressed(MouseButton.Middle) || k.IsKeyPressed(Keys.I),
            TextureStep = (k.IsKeyPressed(Keys.Period) ? 1 : 0) - (k.IsKeyPressed(Keys.Comma) ? 1 : 0),
            TestField = k.IsKeyPressed(Keys.F9),
        };
        bool play = Scripted(action, input, out input);
        Screen.Feed(input);
        return play || k.IsKeyPressed(Keys.F5);
    }

    public void Release()
    {
        _looking = false;
        _w.CursorState = CursorState.Normal;
    }
}
