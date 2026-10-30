using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;
using AndroidLog = Android.Util.Log;

namespace MphRecomp.App;

// The launcher's online test (docs/ONLINE_PLAY.md O3; testing UI until the vanilla menus get online, O9): log in to a
// SecondHunt server with this device's key, then host a public game (the launcher's match settings, START when the
// others are in) or join the first open one. The match plays through the server's UDP relay; each device opens
// CampaignActivity with --es net online (OnlineMatch.Current). Every step is logged under MPHOnline (adb logcat -s
// MPHOnline MPHMatch). PC side: SecondHuntServer --urls http://0.0.0.0:8080, then MphRead.Tools -onlinejoin
// server=ws://<pc>:8080/v1/ws game=<id> (or -onlinetest server=... players=2 for a PC host the device can join).
internal static class MatchOnline
{
    const string Tag = "MPHOnline";
    const string Prefs = "online_test";
    public const string DefaultServer = MphRecomp.Frontend.RecompMenus.OnlineServer;
    // the remembered address's key. Renamed from "server" so an address saved before the live server (the old PC test
    // server, no longer run) is never read back
    const string ServerKey = "server_v2";

    static void Info(string text) => AndroidLog.Info(Tag, text);

    // the server address: the intent's --es server, else the last one used, else the live server
    public static string Server(Activity activity)
    {
        string? last = activity.GetSharedPreferences(Prefs, FileCreationMode.Private)!.GetString(ServerKey, null);
        return activity.Intent?.GetStringExtra("server") ?? last ?? DefaultServer;
    }

    static void Remember(Activity activity, string server)
    {
        ISharedPreferencesEditor e = activity.GetSharedPreferences(Prefs, FileCreationMode.Private)!.Edit()!;
        e.PutString(ServerKey, server);
        e.Apply();
    }

    // log in (the identity file lives in the app's private files: online_identity.json)
    static async Task<(OnlineClient Client, Uri Uri)> Connect(Activity activity, string server)
    {
        var uri = new Uri(server);
        OnlineIdentity id = OnlineIdentity.LoadOrCreate(activity.FilesDir!.AbsolutePath);
        Info($"connecting to {uri} as friend code {FriendCodes.Format(id.FriendCode)}");
        // a server that isn't there fails in 15 s instead of leaving the box on "connecting..."
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        OnlineClient client;
        try
        {
            client = await OnlineClient.ConnectAsync(uri, id, Build.Model ?? "Player", "android", timeout.Token);
        }
        catch (System.OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new OnlineException("timeout", $"no answer from {uri.Host} in 15 s");
        }
        Info($"logged in: {client.Me}, friend code {FriendCodes.Format(client.Me.Code)}");
        MphRecomp.Config.StreamerPrivacy.SetOnline(client.Me.Name, client.Me.Tag, client.Me.Code); // (STREAMING: whose name to hide)
        client.EventReceived += m => Info("event " + m.GetType().Name + (m is GameClosedEvent c ? ": " + c.Reason : ""));
        return (client, uri);
    }

    static (AlertDialog Dialog, TextView Status, EditText Address) Dialog(Activity activity, string title, string action)
    {
        var layout = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        layout.SetPadding(48, 24, 48, 8);
        var address = new EditText(activity) { Text = Server(activity), Hint = DefaultServer };
        layout.AddView(address);
        var status = new TextView(activity) { Text = $"server address above, then {action}", TextSize = 15f };
        status.SetPadding(0, 16, 0, 0);
        layout.AddView(status);
        AlertDialog dialog = new AlertDialog.Builder(activity)
            .SetTitle(title)!
            .SetView(layout)!
            .SetPositiveButton(action, (EventHandler<DialogClickEventArgs>?)null)!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Create()!;
        return (dialog, status, address);
    }

    static void Fail(Activity activity, TextView status, string what, Exception ex)
    {
        string text = ex is OnlineException o ? $"{o.Message} ({o.Code})" : ex.Message;
        AndroidLog.Error(Tag, $"{what}: {ex}");
        activity.RunOnUiThread(() => status.Text = MphRecomp.Config.StreamerPrivacy.Scrub($"{what}: {text}"));
    }

    public static void Host(Activity activity, MatchSettings template, string mphKey, string hunter, string gun,
        int maxPlayers = 4)
    {
        string romKey = LanSession.RomKeyFor(mphKey);
        OnlineMatch.Current?.Dispose();
        (AlertDialog dialog, TextView status, EditText address) = Dialog(activity, "Host an online match (test)", "Host");
        OnlineMatch? match = null;
        bool started = false;
        dialog.DismissEvent += (_, _) =>
        {
            if (!started)
            {
                match?.Dispose();
            }
        };
        dialog.ShowEvent += (_, _) =>
        {
            Button go = dialog.GetButton((int)DialogButtonType.Positive)!;
            go.Click += (_, _) =>
            {
                if (match != null)
                {
                    if (match.Net.Joined == 0)
                    {
                        status.Text = "nobody has joined yet";
                        return;
                    }
                    // START: the others get the final match, this device opens it
                    started = true;
                    match.Start();
                    Info("start: seats " + string.Join(", ", match.Net.Settings!.Players.Select(p =>
                        $"{p.Hunter}{(p.Bot ? " bot" : p.Remote ? " " + p.Nickname : " local")}")));
                    dialog.Dismiss();
                    activity.StartActivity(new Intent(activity, typeof(CampaignActivity))
                        .PutExtra("net", "online").PutExtra("hunter", hunter).PutExtra("gun", gun));
                    return;
                }
                string server = address.Text?.Trim() ?? "";
                go.Enabled = false;
                status.Text = $"connecting to {MphRecomp.Config.StreamerPrivacy.Scrub(server)}...";
                Task.Run(async () =>
                {
                    try
                    {
                        Remember(activity, server);
                        (OnlineClient client, Uri uri) = await Connect(activity, server);
                        match = await OnlineMatch.HostAsync(client, uri, template, romKey, maxPlayers);
                        OnlineMatch.Current = match;
                        Info($"hosting game {match.Game.Id}, relay {match.Relay.Relay} slot {match.Relay.LocalSlot}");
                        void Show() => activity.RunOnUiThread(() =>
                        {
                            if (!started)
                            {
                                status.Text = $"game {match.Game.Id} on the server as {MphRecomp.Config.StreamerPrivacy.Display(client.Me.Name, client.Me.Tag)}\n{match.Net.Joined} joined "
                                    + $"({match.Net.Status}), relay ping {match.Relay.PingMs} ms\nSTART when everyone's in";
                            }
                        });
                        match.Net.PlayersChanged += () =>
                        {
                            Info($"players: {match.Net.Joined} joined ({match.Net.Status})");
                            Show();
                        };
                        Show();
                        activity.RunOnUiThread(() =>
                        {
                            go.Text = "Start";
                            go.Enabled = true;
                        });
                    }
                    catch (Exception ex)
                    {
                        Fail(activity, status, "can't host", ex);
                        match?.Dispose();
                        match = null;
                        activity.RunOnUiThread(() => go.Enabled = true);
                    }
                });
            };
        };
        dialog.Show();
    }

    // gameId: the game to join (--es game), else the first open one for this game version
    public static void Join(Activity activity, string mphKey, string hunter, string gun)
    {
        string romKey = LanSession.RomKeyFor(mphKey);
        OnlineMatch.Current?.Dispose();
        string? gameId = activity.Intent?.GetStringExtra("game");
        (AlertDialog dialog, TextView status, EditText address) = Dialog(activity, "Join an online match (test)", "Join");
        OnlineMatch? match = null;
        bool started = false;
        dialog.DismissEvent += (_, _) =>
        {
            if (!started)
            {
                match?.Dispose();
            }
        };
        dialog.ShowEvent += (_, _) =>
        {
            Button go = dialog.GetButton((int)DialogButtonType.Positive)!;
            go.Click += (_, _) =>
            {
                string server = address.Text?.Trim() ?? "";
                go.Enabled = false;
                status.Text = $"connecting to {MphRecomp.Config.StreamerPrivacy.Scrub(server)}...";
                Task.Run(async () =>
                {
                    try
                    {
                        Remember(activity, server);
                        (OnlineClient client, Uri uri) = await Connect(activity, server);
                        match = await OnlineMatch.JoinAsync(client, uri, romKey, gameId);
                        OnlineMatch.Current = match;
                        Info($"joined game {match.Game.Id} by {match.Game.Host}, relay {match.Relay.Relay} slot "
                            + match.Relay.LocalSlot);
                        if (!match.Net.Join(Enum.Parse<MphRead.Hunter>(hunter), Build.Model ?? "Player", timeoutMs: 15000))
                        {
                            throw new OnlineException("refused", match.Net.Status);
                        }
                        Info($"in the lobby: {match.Net.Status}, relay ping {match.Relay.PingMs} ms");
                        activity.RunOnUiThread(() => status.Text = $"in game {match.Game.Id} as {MphRecomp.Config.StreamerPrivacy.Display(client.Me.Name, client.Me.Tag)}: {match.Net.Status}"
                            + "\nwaiting for the host to start");
                        if (!match.Net.WaitForStart(10 * 60 * 1000))
                        {
                            throw new OnlineException("no_start", match.Net.Status);
                        }
                        Info("start: " + match.Net.Status);
                        activity.RunOnUiThread(() =>
                        {
                            if (!dialog.IsShowing)
                            {
                                return; // cancelled while waiting
                            }
                            started = true;
                            dialog.Dismiss();
                            activity.StartActivity(new Intent(activity, typeof(CampaignActivity))
                                .PutExtra("net", "online").PutExtra("hunter", hunter).PutExtra("gun", gun));
                        });
                    }
                    catch (Exception ex)
                    {
                        Fail(activity, status, "can't join", ex);
                        match?.Dispose();
                        match = null;
                        activity.RunOnUiThread(() => go.Enabled = true);
                    }
                });
            };
        };
        dialog.Show();
    }
}
