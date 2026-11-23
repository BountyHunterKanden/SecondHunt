using System.Collections.Generic;
using Android.Content;
using Android.Hardware.Input;
using Android.Views;

namespace MphRecomp.App;

// Releases everything the game thinks is held (PadState.Clear) when Android stops delivering the matching key-up /
// stick-centred events, as Desktop's DesktopInput.Release does on focus loss: CampaignActivity calls Clear from
// OnPause and on window focus loss, and this listener clears when a game controller disconnects (a stick or button
// held as the pad drops would otherwise stay "held"). Only controllers count, so another device coming or going
// (a keyboard, a headset) doesn't drop a held touch button. Callbacks arrive on the UI thread, like the key and
// motion events that already write the pad.
internal sealed class PadRelease : Java.Lang.Object, InputManager.IInputDeviceListener
{
    readonly PadState _pad;
    readonly HashSet<int> _controllers = new();
    InputManager? _input;

    PadRelease(PadState pad) => _pad = pad;

    // from OnCreate; Detach from OnDestroy
    public static PadRelease Attach(Context context, PadState pad)
    {
        PadRelease r = new(pad);
        r._input = context.GetSystemService(Context.InputService) as InputManager;
        foreach (int id in InputDevice.GetDeviceIds() ?? System.Array.Empty<int>())
        {
            r.Track(id);
        }
        r._input?.RegisterInputDeviceListener(r, null);
        return r;
    }

    public void Detach() => _input?.UnregisterInputDeviceListener(this);

    // same test as MatchTouchOverlay.HasGameController (the Odin's built-in pad counts)
    void Track(int id)
    {
        InputDevice? d = InputDevice.GetDevice(id);
        if (d != null && !d.IsVirtual && (d.Sources.HasFlag(InputSourceType.Gamepad) || d.Sources.HasFlag(InputSourceType.Joystick)))
        {
            _controllers.Add(id);
        }
        else
        {
            _controllers.Remove(id);
        }
    }

    public void OnInputDeviceAdded(int deviceId) => Track(deviceId);
    public void OnInputDeviceChanged(int deviceId) => Track(deviceId);

    public void OnInputDeviceRemoved(int deviceId)
    {
        if (_controllers.Remove(deviceId))
        {
            _pad.Clear();
        }
    }
}
