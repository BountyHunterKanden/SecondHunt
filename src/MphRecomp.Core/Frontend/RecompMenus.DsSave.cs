using System;

// SECOND HUNT SETTINGS > SAVES: IMPORT / EXPORT DS SAVE (owner 2026-10-28; was IMPORT DS SAVES + EXPORT DS SAVE, 10-26).
// The file select's IMPORT / EXPORT box (FileSelect.Transfer): a Metroid Prime Hunters DS save into a campaign file, or a
// campaign file out as one (Campaign/CartSave.cs).
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        // opens the box (FrontendSession: FileSelect.Transfer)
        public Action? DsSaveTransfer { get; set; }

        private RowModel DsSaveTransferRow() => new()
        {
            Label = "import/export\nds save",
            Description = "import a metroid prime hunters ds save into a campaign file, or export a file as a ds save for an emulator or flashcart.",
            Value = () => "OPEN",
            Step = _ => DsSaveTransfer?.Invoke(),
            Action = true,
            Disabled = () => _inPause // (the pause's menus have no saves folder, and the game in play isn't saved yet)
        };
    }
}
