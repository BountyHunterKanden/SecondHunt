using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MphRecomp.Online;

// Cloudflare TURN for match traffic (Netcode O5, decision 18; the client side is Core Multiplayer/Net TurnClient +
// HybridTransport). turn_creds: a player in a game (lobby or started) gets short-lived TURN credentials from Cloudflare's API
// (POST {api}/v1/turn/keys/{TURN_KEY_ID}/credentials/generate-ice-servers, Bearer TURN_KEY_API_TOKEN, {"ttl": 7200}), only its
// "turn:...?transport=udp" URLs; anything else answers UseRelay (our own UDP relay): no key here, the month's cap reached, not
// in a game, or Cloudflare failing (logged without secrets). Cloudflare bills egress to the client ($0.05/GB past 1,000 GB a
// month), so turn_usage counts what clients report per UTC month in the database (turn_usage table), at most 200 KB/s since
// that player's last report or its credentials, and past --turn-cap-gb (900) new credentials answer UseRelay (clients
// already on TURN keep it). The key lives only on the VPS (/etc/secondhunt/turn.env, root-only, the systemd unit's
// EnvironmentFile=-), never in the repo, the deploy kit or the logs.
namespace SecondHunt.Server
{
    public sealed class TurnConfig
    {
        public const int TtlSeconds = 7200;
        public const long MaxBytesPerSecond = 200 * 1024;
        public const string CloudflareApi = "https://rtc.live.cloudflare.com";

        public string KeyId { get; set; } = "";
        public string ApiToken { get; set; } = "";
        public string ApiBase { get; set; } = CloudflareApi;
        public double CapGb { get; set; } = 900;
        // tests: fixed credentials instead of Cloudflare's (still under the cap and in-a-game rules)
        public (string Url, string User, string Pass)? Static { get; set; }

        public bool HasKey => Static != null || (KeyId.Length > 0 && ApiToken.Length > 0);
        public long CapBytes => (long)(CapGb * 1e9);

        public static TurnConfig FromArgs(string[] args, Func<string[], string, string?> arg)
        {
            var c = new TurnConfig
            {
                KeyId = Environment.GetEnvironmentVariable("TURN_KEY_ID")?.Trim() ?? "",
                ApiToken = Environment.GetEnvironmentVariable("TURN_KEY_API_TOKEN")?.Trim() ?? "",
                ApiBase = arg(args, "--turn-api") ?? CloudflareApi,
            };
            if (Double.TryParse(arg(args, "--turn-cap-gb"), NumberStyles.Float, CultureInfo.InvariantCulture, out double cap))
            {
                c.CapGb = cap;
            }
            if (arg(args, "--turn-static") is string s && s.Split('|') is { Length: 3 } p)
            {
                c.Static = (p[0], p[1], p[2]);
            }
            Console.WriteLine(c.Static != null ? "TURN: static test credentials" : c.HasKey ? $"TURN: key set, cap {c.CapGb} GB a month"
                : "TURN: no key (everyone uses the relay)");
            return c;
        }
    }

    public sealed partial class Hub
    {
        private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private readonly Dictionary<string, DateTime> _turnSince = new(); // player -> its credentials or last usage report
        private readonly object _turnLock = new();

        public TurnConfig Turn { get; set; } = new();

        private static string Month(DateTime utc) => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        public double TurnGbThisMonth => _store.TurnBytes(Month(DateTime.UtcNow)) / 1e9;

        private async Task<TurnCredsMsg> TurnCreds(string me)
        {
            if (!Turn.HasKey)
            {
                return new TurnCredsMsg { UseRelay = true, Reason = "no TURN key" };
            }
            if (_store.TurnBytes(Month(DateTime.UtcNow)) >= Turn.CapBytes)
            {
                return new TurnCredsMsg { UseRelay = true, Reason = "monthly TURN cap reached" };
            }
            lock (_gamesLock)
            {
                if (!_gameOf.ContainsKey(me))
                {
                    return new TurnCredsMsg { UseRelay = true, Reason = "not in a game" };
                }
            }
            TurnCredsMsg? creds = Turn.Static is var (url, user, pass)
                ? new TurnCredsMsg { Urls = new List<string> { url }, Username = user, Credential = pass, TtlSeconds = TurnConfig.TtlSeconds }
                : await Cloudflare();
            if (creds == null)
            {
                return new TurnCredsMsg { UseRelay = true, Reason = "TURN unavailable" };
            }
            lock (_turnLock)
            {
                _turnSince[me] = DateTime.UtcNow;
            }
            return creds;
        }

        // Cloudflare's generate-ice-servers: 201 {"iceServers":[{"urls":[stun...]},{"urls":[turn...],"username","credential"}]}
        private async Task<TurnCredsMsg?> Cloudflare()
        {
            string url = $"{Turn.ApiBase.TrimEnd('/')}/v1/turn/keys/{Uri.EscapeDataString(Turn.KeyId)}/credentials/generate-ice-servers";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent($"{{\"ttl\": {TurnConfig.TtlSeconds}}}", Encoding.UTF8, "application/json"),
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Turn.ApiToken);
                using HttpResponseMessage res = await s_http.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                {
                    Console.WriteLine($"TURN: Cloudflare answered {(int)res.StatusCode}");
                    return null;
                }
                using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                foreach (JsonElement server in doc.RootElement.GetProperty("iceServers").EnumerateArray())
                {
                    if (!server.TryGetProperty("username", out JsonElement user) || !server.TryGetProperty("credential", out JsonElement cred))
                    {
                        continue;
                    }
                    JsonElement urls = server.GetProperty("urls");
                    IEnumerable<string> all = urls.ValueKind == JsonValueKind.Array
                        ? urls.EnumerateArray().Select(u => u.GetString() ?? "") : new[] { urls.GetString() ?? "" };
                    List<string> udp = all.Where(u => u.StartsWith("turn:", StringComparison.Ordinal)
                        && u.Contains("transport=udp", StringComparison.Ordinal)).ToList();
                    if (udp.Count == 0)
                    {
                        break;
                    }
                    return new TurnCredsMsg
                    {
                        Urls = udp, Username = user.GetString() ?? "", Credential = cred.GetString() ?? "", TtlSeconds = TurnConfig.TtlSeconds,
                    };
                }
                Console.WriteLine("TURN: Cloudflare's answer had no UDP TURN server");
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException
                or InvalidOperationException)
            {
                Console.WriteLine($"TURN: Cloudflare failed ({ex.GetType().Name})");
                return null;
            }
        }

        // counted: at most 200 KB/s since this player's last report or its credentials (nothing without credentials)
        private OkMsg TurnUsage(string me, long bytes)
        {
            DateTime now = DateTime.UtcNow;
            long counted;
            lock (_turnLock)
            {
                if (bytes <= 0 || !_turnSince.TryGetValue(me, out DateTime since))
                {
                    return new OkMsg { Done = false };
                }
                double seconds = Math.Max(0, (now - since).TotalSeconds);
                counted = Math.Min(bytes, (long)(seconds * TurnConfig.MaxBytesPerSecond));
                _turnSince[me] = now;
            }
            if (counted > 0)
            {
                _store.AddTurnBytes(Month(now), counted);
            }
            return new OkMsg { Done = counted > 0 };
        }
    }
}
