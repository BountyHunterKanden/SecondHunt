using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SecondHunt.Server
{
    // SecondHuntServer [--urls http://127.0.0.1:8080] [--db secondhunt.db] [--words words.txt]
    //                  [--relay-port 47780] [--relay-host <name devices send match traffic to; default = the server's>]
    //                  [--relay-delay <ms>: tests only] [--find-fast: Find Game timings in seconds not tens of them, tests only]
    //                  [--turn-cap-gb 900] [--turn-api <Cloudflare's API base: tests>] [--turn-static "<url>|<user>|<pass>": tests]
    //                  Cloudflare TURN key: environment TURN_KEY_ID + TURN_KEY_API_TOKEN (on the VPS: /etc/secondhunt/turn.env,
    //                  root-only, loaded by the systemd unit); none = every turn_creds answers UseRelay
    //                  [--matchhost <SecondHuntMatchHost> --matchhost-data <its game files' folder> [--max-matches 4]]: served
    //                  games (0.8.0, HubServed.cs; none = every game player-hosted as before)
    // SecondHuntServer admin [--db secondhunt.db] <command>   (the owner's moderation tool: see Admin.cs, deploy/admin.ps1)
    // SecondHuntServer --selftest
    // Behind Caddy (HTTPS) on the VPS: Caddy terminates TLS and forwards wss://<domain>/v1/ws here. The relay is plain
    // UDP on its own port (open it in the firewall).
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Contains("--selftest"))
            {
                return await SelfTest.Run();
            }
            if (args.Length > 0 && args[0] == "--fake-matchhost")
            {
                return SelfTest.FakeMatchHost(args[1..]); // (the selftest's stand-in match host: SelfTestServed.cs)
            }
            if (args.Length > 0 && args[0] == "admin")
            {
                return Admin.Run(args[1..], Console.Out); // the owner's tool (Social O7): reports, bans, chat mutes
            }
            WordFilter.Load(Arg(args, "--words") ?? "words.txt");
            WebApplication app = ServerApp.Build(Arg(args, "--db") ?? "secondhunt.db",
                Arg(args, "--urls") ?? "http://127.0.0.1:8080", quiet: false,
                Int32.TryParse(Arg(args, "--relay-port"), out int relayPort) ? relayPort : ServerApp.DefaultRelayPort,
                Arg(args, "--relay-host") ?? "",
                args.Contains("--find-fast") ? FindConfig.Fast : null, TurnConfig.FromArgs(args, Arg));
            Hub hub = app.Services.GetRequiredService<Hub>();
            hub.Served = ServedConfig.FromArgs(args, Arg);
            hub.StartServed();
            Relay relay = app.Services.GetRequiredService<Relay>();
            // test only (MphRead.Tools -onlinetest delay=): every relayed packet waits this long, a far-away player
            relay.DelayMs = Int32.TryParse(Arg(args, "--relay-delay"), out int delay) ? delay : 0;
            Console.WriteLine($"relay on UDP port {relay.Port}" + (relay.DelayMs > 0 ? $", {relay.DelayMs} ms test delay" : ""));
            await app.RunAsync();
            return 0;
        }

        private static string? Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }

    public static class ServerApp
    {
        public const int DefaultRelayPort = 47780;

        // relayPort 0 = any free port (tests read Relay.Port)
        public static WebApplication Build(string dbPath, string urls, bool quiet, int relayPort = DefaultRelayPort,
            string relayHost = "", FindConfig? find = null, TurnConfig? turn = null)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(urls);
            if (quiet)
            {
                builder.Logging.ClearProviders();
            }
            var store = new Store(dbPath);
            var relay = new Relay(relayPort);
            var hub = new Hub(store, relay, relayHost);
            hub.Find = find ?? new FindConfig();
            hub.Turn = turn ?? new TurnConfig();
            hub.DrainFile = Admin.DrainPath(dbPath); // (served games: admin drain on)
            hub.ReportAlertUrl = Environment.GetEnvironmentVariable("REPORT_ALERT_URL")?.Trim() ?? ""; // HubAlerts.cs
            Console.WriteLine(hub.ReportAlertUrl.Length > 0 ? "report alerts: on" : "report alerts: off (no REPORT_ALERT_URL)");
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton(relay);
            builder.Services.AddSingleton(hub);
            WebApplication app = builder.Build();
            app.Lifetime.ApplicationStopped.Register(store.Dispose);
            app.Lifetime.ApplicationStopped.Register(relay.Dispose);
            app.Lifetime.ApplicationStopped.Register(hub.Dispose);
            // (O5b: pings every 5 s and a 10 s timeout notice a dropped host within ~15 s, for host migration)
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5), KeepAliveTimeout = TimeSpan.FromSeconds(10) });
            app.MapGet("/v1/health", () => FormattableString.Invariant($"ok {Hub.ServerVersion} online {hub.OnlineCount} turn {hub.TurnGbThisMonth:0.00} GB{hub.ServedHealth}"));
            app.Map("/v1/ws", async (HttpContext ctx) =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await hub.Run(ws, ctx.RequestAborted);
            });
            return app;
        }
    }
}
