using System;
using Android.Content;
using Android.Net.Wifi;

namespace MphRecomp.App;

// The vanilla multiplayer menu's LAN (Core Frontend/MultiplayerMenu, M3) on Android: hearing the hosts' broadcast
// beacons on CREATE OR JOIN's AVAILABLE GAMES needs Wi-Fi's multicast lock, as the launcher's MatchLan join dialog takes
// one. GameActivity hands this to MultiplayerMenu.MulticastLock; the menu holds it while it listens.
internal static class MenuLan
{
    public static IDisposable? AcquireMulticast(Context context)
    {
        var wifi = (WifiManager?)context.ApplicationContext!.GetSystemService(Context.WifiService);
        WifiManager.MulticastLock? multicast = wifi?.CreateMulticastLock("mph-menu-lan");
        if (multicast == null) return null;
        multicast.SetReferenceCounted(false);
        multicast.Acquire();
        return new Hold(multicast);
    }

    sealed class Hold : IDisposable
    {
        readonly WifiManager.MulticastLock _lock;

        public Hold(WifiManager.MulticastLock multicast) => _lock = multicast;

        public void Dispose()
        {
            if (_lock.IsHeld) _lock.Release();
        }
    }
}
