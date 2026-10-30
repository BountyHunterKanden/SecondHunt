using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Cloudflare TURN (HubTurn.cs): against a local stand-in for Cloudflare's generate-ice-servers
    public static partial class SelfTest
    {
        private static async Task TurnTests(string dir)
        {
            bool fail = false;
            string? auth = null;
            WebApplicationBuilder sb = WebApplication.CreateBuilder();
            sb.WebHost.UseUrls("http://127.0.0.1:0");
            sb.Logging.ClearProviders();
            WebApplication stub = sb.Build();
            stub.MapPost("/v1/turn/keys/{id}/credentials/generate-ice-servers", (HttpContext ctx, string id) =>
            {
                auth = $"{id} {ctx.Request.Headers.Authorization}";
                return fail ? Results.StatusCode(500) : Results.Json(new
                {
                    iceServers = new object[]
                    {
                        new { urls = new[] { "stun:stun.cloudflare.com:3478" } },
                        new
                        {
                            urls = new[]
                            {
                                "turn:turn.cloudflare.com:3478?transport=udp", "turn:turn.cloudflare.com:3478?transport=tcp",
                                "turns:turn.cloudflare.com:5349?transport=tcp", "turn:turn.cloudflare.com:53?transport=udp",
                            },
                            username = "u1", credential = "c1",
                        },
                    },
                }, statusCode: 201);
            });
            await stub.StartAsync();
            string db = Path.Combine(dir, "turn.db");
            string month = DateTime.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

            // no key: the relay
            (WebApplication app, Uri uri) = await Start(Path.Combine(dir, "turn0.db"));
            OnlineClient n = await Connect(uri, OnlineIdentity.Create(), "Nokey");
            await n.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: false) });
            TurnCredsMsg none = await n.RequestAsync<TurnCredsMsg>(new TurnCredsReq());
            Check(none.UseRelay && none.Reason == "no TURN key", "turn: no key on the server -> the relay");
            await n.DisposeAsync();
            await app.DisposeAsync();

            // a key: Cloudflare's UDP TURN URLs for a player in a game (cap 100 KB for the cap check below)
            var turn = new TurnConfig { KeyId = "key1", ApiToken = "tok1", ApiBase = stub.Urls.First(), CapGb = 0.0001 };
            (app, uri) = await Start(db, turn: turn);
            Store store = app.Services.GetRequiredService<Store>();
            OnlineClient h = await Connect(uri, OnlineIdentity.Create(), "Turnhost");
            OnlineClient o = await Connect(uri, OnlineIdentity.Create(), "Turnout");
            TurnCredsMsg outside = await h.RequestAsync<TurnCredsMsg>(new TurnCredsReq());
            await h.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: false) });
            TurnCredsMsg creds = await h.RequestAsync<TurnCredsMsg>(new TurnCredsReq());
            Check(outside.UseRelay && outside.Reason == "not in a game" && !creds.UseRelay && auth == "key1 Bearer tok1"
                && creds.Urls.SequenceEqual(new[] { "turn:turn.cloudflare.com:3478?transport=udp", "turn:turn.cloudflare.com:53?transport=udp" })
                && creds.Username == "u1" && creds.Credential == "c1" && creds.TtlSeconds == 7200,
                "turn: in a game -> Cloudflare's credentials (its key + token, UDP TURN URLs only); not in a game -> the relay");
            // usage: at most 200 KB/s since the credentials; nothing for a player without them
            await Task.Delay(300);
            OkMsg used = await h.RequestAsync<OkMsg>(new TurnUsageReq { Bytes = 10_000_000_000 });
            long after = store.TurnBytes(month);
            OkMsg stranger = await o.RequestAsync<OkMsg>(new TurnUsageReq { Bytes = 5_000_000 });
            long after2 = store.TurnBytes(month);
            Check(used.Done && after > 0 && after <= 2 * TurnConfig.MaxBytesPerSecond && !stranger.Done && after2 == after,
                $"turn: usage counts at most 200 KB/s since the credentials ({after} bytes for 10 GB claimed after 0.3 s), none without them");
            // the cap: once over it, new credentials -> the relay
            await Task.Delay(600);
            await h.RequestAsync<OkMsg>(new TurnUsageReq { Bytes = 1_000_000 });
            TurnCredsMsg capped = await h.RequestAsync<TurnCredsMsg>(new TurnCredsReq());
            using var http = new HttpClient();
            string health = await http.GetStringAsync(new Uri(uri.ToString().Replace("ws://", "http://").Replace("/v1/ws", "/v1/health")));
            Check(capped.UseRelay && capped.Reason == "monthly TURN cap reached" && health.Contains(" turn 0.00 GB"),
                $"turn: past the monthly cap new credentials -> the relay; health shows the month's GB ({health})");
            // Cloudflare failing
            turn.CapGb = 900;
            fail = true;
            TurnCredsMsg down = await h.RequestAsync<TurnCredsMsg>(new TurnCredsReq());
            Check(down.UseRelay && down.Reason == "TURN unavailable", "turn: Cloudflare failing -> the relay");
            long counted = store.TurnBytes(month);
            await h.DisposeAsync();
            await o.DisposeAsync();
            await app.DisposeAsync();
            // the month's count survives a restart
            using (var again = new Store(db))
            {
                Check(counted > 100_000 && again.TurnBytes(month) == counted, $"turn: the month's count survives a restart ({counted} bytes)");
            }

            // --turn-static (Netcode's PC test): fixed credentials, the same rules
            var fixedTurn = new TurnConfig { Static = ("turn:127.0.0.1:3478?transport=udp", "su", "sp") };
            (app, uri) = await Start(Path.Combine(dir, "turn2.db"), turn: fixedTurn);
            OnlineClient f = await Connect(uri, OnlineIdentity.Create(), "Turnfix");
            TurnCredsMsg fo = await f.RequestAsync<TurnCredsMsg>(new TurnCredsReq());
            await f.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: false) });
            TurnCredsMsg fc = await f.RequestAsync<TurnCredsMsg>(new TurnCredsReq());
            Check(fo.UseRelay && !fc.UseRelay && fc.Urls.SequenceEqual(new[] { "turn:127.0.0.1:3478?transport=udp" })
                && fc.Username == "su" && fc.Credential == "sp", "turn: static test credentials, still only in a game");
            await f.DisposeAsync();
            await app.DisposeAsync();
            await stub.DisposeAsync();
        }
    }
}
