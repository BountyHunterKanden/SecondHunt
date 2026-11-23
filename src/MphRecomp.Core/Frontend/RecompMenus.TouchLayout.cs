using System;

// CONTROLS > TOUCH: EDIT TOUCH LAYOUT (owner 2026-11-15, wave 2, for finRatman): opens the touch controls' layout
// editor (MphRead.Android/TouchLayoutEditor.cs), which moves, sizes, hides and fades each on-screen button
// (RecompSettings.TouchLayouts / TouchOpacity). From the pause menu it edits over the paused game; from the main menu
// over a dim backdrop with a MATCH / CAMPAIGN switch.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        // opens the editor (the Android host: the pause menu's PauseOverlay.EditTouchLayout, the main menu's GameActivity)
        public Action? EditTouchLayout { get; set; }

        private RowModel TouchLayoutRow() => new()
        {
            Label = "edit touch\nlayout",
            Description = "move, resize, hide or fade any on-screen touch button. drag a button to move it, then use the sliders under it.",
            Value = () => "OPEN",
            Step = _ => EditTouchLayout?.Invoke(),
            Action = true
        };
    }
}
