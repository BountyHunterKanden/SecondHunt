using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace MphRecomp.Online
{
    // Shared replays (2026-10-19: sent straight to friends, shown in a shared section of the replay menu, waiting there
    // to be downloaded or deleted, with a setting to block them; server
    // 0.9.0, SecondHunt.Server HubReplays.cs). A replay goes to friends through the server: replay_send names the friends and
    // the file's size and description; the server answers who it goes to (and who not, and why: not a friend, blocked, they
    // block shared replays, their queue is full) with an upload ticket; the file goes up over HTTPS (PUT /v1/replays/{id}?t=,
    // too big for one WebSocket message), and each friend online is told (ev_replay); the rest see it in get_replays at their
    // next visit. It waits on the server (ReplayMailRules.KeepDays) until they DOWNLOAD it (replay_fetch: a download ticket,
    // GET /v1/replays/{id}?t=, then replay_drop) or DELETE it (replay_drop). set_replay_block = refuse every shared replay
    // (the app's setting, sent at each login and when it changes); a replay already waiting stays until it's dropped.
    // An older server drops a connection that sends any of these: check ServerAtLeast(ReplaySendReq.SinceServer).
    public static class ReplayMailRules
    {
        public const long MaxBytes = 16L << 20; // a replay file's most (an hour-long match is a few MB)
        public const int MaxRecipients = 8;
        public const int MaxWaiting = 20; // per player: shared replays waiting for them
        public const int KeepDays = 7;
        public const int TitleMax = 40;
        public static readonly byte[] Magic = "MPHRPLY\0"u8.ToArray(); // (ReplayFile's: the server checks only this)
    }

    // what a shared replay says about itself (from its file, filled in by the sender's app)
    public sealed class ReplayOffer
    {
        public string Id { get; set; } = "";
        public PlayerCard From { get; set; } = new();
        public string Title { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Arena { get; set; } = "";
        public int Seconds { get; set; }
        public long Bytes { get; set; }
        public long Recorded { get; set; } // unix seconds, the match's day (0 = not known)
        public long Sent { get; set; } // unix seconds
    }

    public sealed class ReplayRefusal
    {
        public string Code { get; set; } = "";
        // not_friend, blocked, blocks_replays, queue_full
        public string Reason { get; set; } = "";
    }

    // share a replay with friends (their codes); the reply is a ReplaySendMsg
    public sealed class ReplaySendReq : OnlineMsg
    {
        public const string SinceServer = "0.9.0";
        public List<string> To { get; set; } = new();
        public long Bytes { get; set; }
        public string Title { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Arena { get; set; } = "";
        public int Seconds { get; set; }
        public long Recorded { get; set; }
    }

    // ReplayId + Ticket: PUT the file there (empty ReplayId = it goes to nobody: every friend refused); Sent = who will get it
    public sealed class ReplaySendMsg : OnlineMsg
    {
        public string ReplayId { get; set; } = "";
        public string Ticket { get; set; } = "";
        public List<string> Sent { get; set; } = new();
        public List<ReplayRefusal> Refused { get; set; } = new();
    }

    // the shared replays waiting for this player; the reply is a ReplaysMsg
    public sealed class ReplaysReq : OnlineMsg
    {
    }

    public sealed class ReplaysMsg : OnlineMsg
    {
        public List<ReplayOffer> Offers { get; set; } = new();
        public bool Block { get; set; }
    }

    // DOWNLOAD: a ticket to GET it; the reply is a ReplayTicketMsg
    public sealed class ReplayFetchReq : OnlineMsg
    {
        public string ReplayId { get; set; } = "";
    }

    public sealed class ReplayTicketMsg : OnlineMsg
    {
        public string ReplayId { get; set; } = "";
        public string Ticket { get; set; } = "";
    }

    // DELETE (or done downloading): off this player's queue; the reply is an OkMsg
    public sealed class ReplayDropReq : OnlineMsg
    {
        public string ReplayId { get; set; } = "";
    }

    // the "block shared replays" setting; the reply is an OkMsg
    public sealed class ReplayBlockReq : OnlineMsg
    {
        public bool Block { get; set; }
    }

    // pushed: a friend shared a replay with this player (it's uploaded and waiting)
    public sealed class ReplayOfferEvent : OnlineMsg
    {
        public ReplayOffer Offer { get; set; } = new();
    }

    // The app's side: the requests above plus the file's trip over HTTPS (the WebSocket server's own host: wss -> https)
    public static class ReplayMail
    {
        private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromMinutes(2) };

        public static bool Supported(OnlineClient client) => client.ServerAtLeast(ReplaySendReq.SinceServer);

        // the HTTP(S) address of the server whose WebSocket is `ws` (ws://host:port/v1/ws -> http://host:port)
        public static Uri HttpBase(Uri ws) => new UriBuilder(ws) { Scheme = ws.Scheme == "wss" ? "https" : "http", Path = "/",
            Port = ws.IsDefaultPort ? -1 : ws.Port }.Uri;

        private static Uri FileUri(Uri ws, string id, string ticket) => new(HttpBase(ws),
            $"v1/replays/{Uri.EscapeDataString(id)}?t={Uri.EscapeDataString(ticket)}");

        // send `file` (a replay's bytes) to these friends; the reply says who gets it (the upload done) and who not
        public static async Task<ReplaySendMsg> SendAsync(OnlineClient client, Uri server, byte[] file, IEnumerable<string> friends,
            string title, string mode, string arena, int seconds, long recorded, CancellationToken ct = default)
        {
            if (file.LongLength > ReplayMailRules.MaxBytes) throw new OnlineException("too_big", "the replay is too big to share");
            var req = new ReplaySendReq { To = new List<string>(friends), Bytes = file.LongLength, Title = title, Mode = mode,
                Arena = arena, Seconds = seconds, Recorded = recorded };
            ReplaySendMsg reply = await client.RequestAsync<ReplaySendMsg>(req, ct);
            if (reply.ReplayId.Length == 0) return reply;
            using var body = new ByteArrayContent(file);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using HttpResponseMessage r = await s_http.PutAsync(FileUri(server, reply.ReplayId, reply.Ticket), body, ct);
            if (!r.IsSuccessStatusCode)
            {
                throw new OnlineException("upload_failed", $"the server didn't take the file ({(int)r.StatusCode})");
            }
            return reply;
        }

        public static async Task<ReplaysMsg> WaitingAsync(OnlineClient client, CancellationToken ct = default)
            => await client.RequestAsync<ReplaysMsg>(new ReplaysReq(), ct);

        // DOWNLOAD: the file's bytes (still on the queue: DropAsync once it's saved)
        public static async Task<byte[]> DownloadAsync(OnlineClient client, Uri server, string id, CancellationToken ct = default)
        {
            ReplayTicketMsg t = await client.RequestAsync<ReplayTicketMsg>(new ReplayFetchReq { ReplayId = id }, ct);
            using HttpResponseMessage r = await s_http.GetAsync(FileUri(server, t.ReplayId, t.Ticket), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!r.IsSuccessStatusCode)
            {
                throw new OnlineException("download_failed", $"the server didn't send the file ({(int)r.StatusCode})");
            }
            await using Stream s = await r.Content.ReadAsStreamAsync(ct);
            var bytes = new MemoryStream();
            byte[] buffer = new byte[1 << 16];
            int n;
            while ((n = await s.ReadAsync(buffer, ct)) > 0)
            {
                bytes.Write(buffer, 0, n);
                if (bytes.Length > ReplayMailRules.MaxBytes) throw new OnlineException("too_big", "too big to be a replay");
            }
            return bytes.ToArray();
        }

        public static async Task DropAsync(OnlineClient client, string id, CancellationToken ct = default)
            => await client.RequestAsync<OkMsg>(new ReplayDropReq { ReplayId = id }, ct);

        public static async Task SetBlockAsync(OnlineClient client, bool block, CancellationToken ct = default)
            => await client.RequestAsync<OkMsg>(new ReplayBlockReq { Block = block }, ct);
    }
}
