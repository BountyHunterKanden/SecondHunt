using System;

// LOOK > HUD > CUSTOMIZE HUD (owner 2026-11-15, wave 2): opens the HUD editor over the paused game, where each part of
// the HUD (energy, ammo, the reticle...) can be moved, sized, faded or hidden (MphRecomp.App/Hud/HudEditor.cs; the
// choices are RecompSettings.HudLayouts). Only the in-game pause has a HUD to edit, so the front end's copy is greyed.
// Dev-only for now (HiddenInPublic).
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private RowModel HudEditorRow() => new()
        {
            Label = "customize\nhud",
            Description = "move, resize, fade or hide each part of the hud, over the paused game.",
            Value = () => "EDIT",
            Step = _ => Requested?.Invoke(FrontendRequestKind.HudEditor),
            Action = true,
            Disabled = () => !_inPause
        };
    }
}
