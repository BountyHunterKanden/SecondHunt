using System;
using System.Collections.Generic;
using MphRecomp.Config;

namespace MphRecomp.App;

// The custom HUD's state between the campaign / match renderer and the HUD editor (owner 2026-11-15, wave 2): the edits
// drawn now (the saved ones, or the editor's working copy while it is open) and, per HUD element, its box as the game
// draws it (Home: screen pixels, Y down). CampaignRenderer.HudLayout.cs fills Homes each frame from the HUD's element
// tags (MphRead Scene.HostHudTag) and applies Edits to the HUD items; HudEditor.cs reads Homes to show and pick the
// parts and writes Edits. One profile, "all", for now (per hunter profiles come in P4).
internal sealed class HudLayout
{
    public const string AllProfile = "all";

    public struct Box
    {
        public float Left, Top, Right, Bottom; // pixels, Y down
        public float CentreX => (Left + Right) / 2;
        public float CentreY => (Top + Bottom) / 2;
        public float Width => Right - Left;
        public float Height => Bottom - Top;
    }

    // element id -> the player's edit (overrides only; LayoutEdit)
    public Dictionary<string, LayoutEdit> Edits = new();
    // the editor is open: hidden parts draw faintly, the world behind the HUD is dimmed
    public bool Editing;
    // element id -> where the game draws it, as shipped: the union of its boxes since the screen, hunter or HUD style
    // last changed, so a counter's width or a blinking icon doesn't shift the centre an edit is measured from
    public readonly Dictionary<string, Box> Homes = new();
    // the elements in the order the HUD draws them (the editor's L / R order)
    public readonly List<string> Order = new();
    // the elements drawn in the frame just shown (the editor shows the others as outlines only)
    public readonly HashSet<string> Live = new();
    public int Width, Height;

    public void Load(RecompSettings settings)
    {
        Edits = Clone(settings.HudLayouts.TryGetValue(AllProfile, out Dictionary<string, LayoutEdit>? all) ? all : null);
    }

    public static Dictionary<string, LayoutEdit> Clone(Dictionary<string, LayoutEdit>? edits)
    {
        Dictionary<string, LayoutEdit> copy = new();
        if (edits == null) return copy;
        foreach ((string id, LayoutEdit edit) in edits)
        {
            if (edit != null && !edit.IsDefault) copy[id] = edit.Copy();
        }
        return copy;
    }

    // the element's box as drawn with its edit (the editor's ring and picking; the renderer moves items the same way)
    public Box Drawn(string id)
    {
        Box home = Homes[id];
        if (!Edits.TryGetValue(id, out LayoutEdit? edit)) return home;
        (float cx, float cy) = Centre(id, home, edit);
        float s = edit.Scale;
        return new Box
        {
            Left = cx - home.Width / 2 * s,
            Right = cx + home.Width / 2 * s,
            Top = cy - home.Height / 2 * s,
            Bottom = cy + home.Height / 2 * s,
        };
    }

    // where an element's centre goes: its stored place, else where it ships
    public (float x, float y) Centre(string id, Box home, LayoutEdit edit)
    {
        if (!edit.Moved || !Movable(id)) return (home.CentreX, home.CentreY);
        return LayoutEdit.Place(edit.Anchor, edit.X!.Value, edit.Y!.Value, Width, Height);
    }

    // the reticle stays on the aim (the aim view moves it, CampaignLookAhead.cs): it can be sized, faded or hidden only
    public static bool Movable(string id) => id != "reticle";

    public static string Name(string id) => id switch
    {
        "energy" => "ENERGY",
        "ammo" => "AMMO",
        "weapon" => "WEAPON",
        "reticle" => "RETICLE",
        "radar" => "RADAR",
        "minimap" => "MINI MAP",
        "match" => "MATCH INFO",
        "messages" => "MESSAGES",
        "escape" => "ESCAPE TIMER",
        "opponent" => "OPPONENT",
        "bombs" => "BOMBS",
        "drone" => "DRONE",
        "doubledamage" => "DOUBLE DAMAGE",
        "cloak" => "CLOAK",
        "target" => "TARGET",
        _ => id.ToUpperInvariant()
    };
}
