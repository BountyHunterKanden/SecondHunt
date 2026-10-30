using System;
using System.Threading;
using MphRead.Entities;

namespace MphRecomp.App;

// The campaign's dialogs (MphRead PlayerDialog: pickups, messages, scan results, YES / NO questions) answered by tapping
// or clicking the buttons they draw, as on the DS touch screen (owner 2026-10-14: "for all of the pop up notifications in
// campaign that have an onscreen ok button i want them to be touchable/clickable"). PlayerDialog.HostDialogButtonAtView
// maps a tap into the DS's 256x192 layout (the dialogs sit in a centred 4:3 box, Scene.HostHudDialogBox) and names the
// button under it: OK, YES / NO, the page arrows (PC check: Tools -dialogtouch). The press reaches the game
// as the pad's do (BuildInput -> CampaignInput.DialogButton), and vanilla decides whether the button is live yet.
// Callers: MatchTouchOverlay.Down and CampaignActivity.OnTouchEvent (Android), DesktopInput.OnMouseDown (PC).
internal sealed partial class CampaignRenderer
{
    int _dialogTap = (int)HostDialogButton.None; // UI thread -> the next sim step

    // UI thread: a tap or click at (x, y) in view pixels. True when it hit a dialog button (the tap is used up).
    public bool DialogTouch(float x, float y)
    {
        var host = _host;
        int width = _width, height = _height;
        if (host == null || !host.DialogPaused || width <= 0 || height <= 0)
        {
            return false;
        }
        HostDialogButton button = PlayerEntity.HostDialogButtonAtView(x, y, width, height, host.Dialog == DialogType.YesNo);
        if (button == HostDialogButton.None)
        {
            return false;
        }
        Volatile.Write(ref _dialogTap, (int)button);
        return true;
    }

    // a PC: the mouse is free while a dialog is up (DesktopInput.Poll), so the pointer can reach its buttons
    public bool DialogUp => TouchDialog != 0;

    // a PC's click on a dialog: a left click on a button presses it, anywhere else on an OK-type dialog it is OK / next
    // page (as a left click always answered); a right click is NO (KeyboardMouseInput.DialogButton's rules)
    public void DialogClick(float x, float y, bool right)
    {
        int dialog = TouchDialog;
        if (dialog == 0)
        {
            return;
        }
        if (right)
        {
            if (dialog == 2) Volatile.Write(ref _dialogTap, (int)HostDialogButton.No);
        }
        else if (!DialogTouch(x, y) && dialog == 1)
        {
            Volatile.Write(ref _dialogTap, (int)HostDialogButton.Advance);
        }
    }

    // BuildInput, while a dialog is up: the button tapped since the last step, once
    HostDialogButton TakeDialogTap() => (HostDialogButton)Interlocked.Exchange(ref _dialogTap, (int)HostDialogButton.None);
}
