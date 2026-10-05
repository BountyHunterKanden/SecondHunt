using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Net.Wifi;
using Android.OS;
using Android.Views;
using Android.Widget;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

// LAN matches talk UDP on the local network; finding a host needs its broadcasts (multicast lock)
[assembly: UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessNetworkState)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessWifiState)]
[assembly: UsesPermission(Android.Manifest.Permission.ChangeWifiMulticastState)]

namespace MphRecomp.App;

// The launcher's LAN lobby (testing UI until the ROM's own multiplayer screens are wired): Host waits for a player on
// this Wi-Fi network, Join lists the hosts it hears (or takes a typed address). Once both sides agree, each device
// opens CampaignActivity with --es net host|client, which plays the match through LanSession.Current.
internal static class MatchLan
{
    // the revision check both sides compare (LanSession JOIN): US rev 0 and rev 1 share every file a match uses
    // (docs/ROM_VERSIONS.md 3e), so they count as one
    static string LanRomKey(string romKey) => romKey is MphRead.Ver.AMHE0 or MphRead.Ver.AMHE1 ? "MPH USA" : romKey;

    // onStarted: called once the match screen has been opened (the lobby sets AppNav and closes itself)
    public static void Host(Activity activity, MatchSettings template, string romKey, string hunter, string gun,
        Action? onStarted = null)
    {
        romKey = LanRomKey(romKey);
        LanSession.Current?.Dispose();
        LanSession session;
        try
        {
            session = LanSession.Host(template, romKey, Build.Model ?? "MPH Recomp");
        }
        catch (Exception ex)
        {
            Toast.MakeText(activity, "Can't host: " + ex.Message, ToastLength.Long)!.Show();
            return;
        }
        LanSession.Current = session;
        var status = new TextView(activity) { Text = session.Status, TextSize = 16f };
        status.SetPadding(48, 32, 48, 16);
        AlertDialog dialog = new AlertDialog.Builder(activity)
            .SetTitle("Hosting a LAN match")!
            .SetView(status)!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Create()!;
        bool started = false;
        dialog.DismissEvent += (_, _) =>
        {
            if (!started)
            {
                session.Dispose();
            }
        };
        session.StatusChanged += s => activity.RunOnUiThread(() => status.Text = s);
        session.Joined += () => activity.RunOnUiThread(() =>
        {
            started = true;
            dialog.Dismiss();
            activity.StartActivity(new Intent(activity, typeof(CampaignActivity))
                .PutExtra("net", "host").PutExtra("hunter", hunter).PutExtra("gun", gun));
            onStarted?.Invoke();
        });
        dialog.Show();
    }

    public static void Join(Activity activity, string romKey, string hunter, string gun, Action? onStarted = null)
    {
        romKey = LanRomKey(romKey);
        LanSession.Current?.Dispose();
        var wifi = (WifiManager?)activity.ApplicationContext!.GetSystemService(Context.WifiService);
        WifiManager.MulticastLock? multicast = wifi?.CreateMulticastLock("mph-lan");
        multicast?.SetReferenceCounted(false);
        multicast?.Acquire();
        LanSession session = LanSession.Browse(romKey);
        LanSession.Current = session;
        var layout = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        layout.SetPadding(48, 24, 48, 8);
        var status = new TextView(activity) { Text = session.Status, TextSize = 15f };
        layout.AddView(status);
        var list = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        layout.AddView(list);
        var address = new EditText(activity) { Hint = "or type the host's address, e.g. 10.0.0.23" };
        string mine = LanSession.LocalAddress();
        int dot = mine.LastIndexOf('.');
        if (dot > 0)
        {
            address.Text = mine[..(dot + 1)];
        }
        layout.AddView(address);
        var joinTyped = new Button(activity) { Text = "Join this address" };
        layout.AddView(joinTyped);
        var scroll = new ScrollView(activity);
        scroll.AddView(layout);
        AlertDialog dialog = new AlertDialog.Builder(activity)
            .SetTitle("Join a LAN match")!
            .SetView(scroll)!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Create()!;
        bool started = false, joining = false;
        var handler = new Handler(Looper.MainLooper!);
        void Refresh()
        {
            if (!dialog.IsShowing || started)
            {
                return;
            }
            list.RemoveAllViews();
            foreach (LanHostInfo host in session.Hosts)
            {
                var b = new Button(activity) { Text = host.Label };
                LanHostInfo target = host;
                b.Click += (_, _) => JoinTarget(target.Address);
                list.AddView(b);
            }
            if (!joining)
            {
                status.Text = session.Hosts.Count == 0 ? session.Status : "tap a match to join:";
            }
            handler.PostDelayed(Refresh, 1000);
        }
        void JoinTarget(System.Net.IPEndPoint target)
        {
            if (joining)
            {
                return;
            }
            joining = true;
            status.Text = $"joining {target.Address}...";
            Task.Run(() =>
            {
                bool ok = session.Join(target, Enum.Parse<MphRead.Hunter>(hunter), Build.Model ?? "Player");
                activity.RunOnUiThread(() =>
                {
                    joining = false;
                    if (!ok)
                    {
                        status.Text = session.Status;
                        return;
                    }
                    started = true;
                    multicast?.Release();
                    dialog.Dismiss();
                    activity.StartActivity(new Intent(activity, typeof(CampaignActivity))
                        .PutExtra("net", "client").PutExtra("hunter", hunter).PutExtra("gun", gun));
                    onStarted?.Invoke();
                });
            });
        }
        joinTyped.Click += (_, _) =>
        {
            string text = address.Text?.Trim() ?? "";
            string host = text;
            int port = LanSession.GamePort;
            int colon = text.LastIndexOf(':');
            if (colon > 0 && int.TryParse(text[(colon + 1)..], out int p))
            {
                host = text[..colon];
                port = p;
            }
            if (System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? ip))
            {
                JoinTarget(new System.Net.IPEndPoint(ip, port));
            }
            else
            {
                status.Text = $"\"{text}\" isn't an address like 10.0.0.23";
            }
        };
        dialog.DismissEvent += (_, _) =>
        {
            if (!started)
            {
                multicast?.Release();
                session.Dispose();
            }
        };
        dialog.Show();
        handler.PostDelayed(Refresh, 300);
    }
}
