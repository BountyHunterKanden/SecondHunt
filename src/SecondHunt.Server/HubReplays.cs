using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Shared replays (server 0.9.0; the protocol and the rules: Core Online/ReplayMail.cs). A friend's replay waits here, in
    // the replays folder beside the database (one file per shared replay, named by its id), until every friend it went to
    // downloaded or deleted it, or ReplayMailRules.KeepDays passed (a file never uploaded goes after an hour). Files come and
    // go over HTTP with one-use tickets (10 minutes) the WebSocket requests hand out: PUT /v1/replays/{id}?t= (the sender's,
    // at most the size it announced), GET /v1/replays/{id}?t= (a friend it waits for). The folder holds at most ReplayCapBytes
    // (--replay-cap-mb, 2048); past it new shares are refused, and one sender's waiting files at most SenderCapBytes. A friend
    // gets one only while still a friend and not blocked either way (checked again at the list, the push and the fetch), with
    // at most TicketsEach download tickets live.
    public sealed partial class Hub
    {
        private sealed record ReplayTicket(string Id, string Code, bool Upload, DateTime Until);

        private readonly ConcurrentDictionary<string, ReplayTicket> _replayTickets = new();
        private Timer? _replayTimer;
        private string _replayDir = "";
        private static readonly TimeSpan TicketLife = TimeSpan.FromMinutes(10);
        private const int SendsPerHour = 20;
        private const int TicketsEach = 8; // live tickets per player (a fetch each)
        private const long SenderCapBytes = 128L << 20; // one sender's shared replays waiting on the server

        public long ReplayCapBytes { get; set; } = 2048L << 20;

        // the replays folder (set at start; "" = shared replays off: every request refused)
        public string ReplayDir
        {
            get => _replayDir;
            set
            {
                _replayDir = value;
                _replayTimer?.Dispose();
                _replayTimer = null;
                if (value.Length == 0) return;
                Directory.CreateDirectory(value);
                _replayTimer = new Timer(_ => PurgeReplaysSafe(), null, TimeSpan.Zero, TimeSpan.FromMinutes(10));
            }
        }

        private string ReplayPath(string id) => Path.Combine(_replayDir, id + ".mphreplay");

        private void RequireReplays()
        {
            if (_replayDir.Length == 0) throw new OnlineException("no_replays", "this server doesn't keep shared replays");
        }

        private ReplaySendMsg ReplaySend(string me, ReplaySendReq r)
        {
            RequireReplays();
            if (r.Bytes <= ReplayMailRules.Magic.Length || r.Bytes > ReplayMailRules.MaxBytes)
            {
                throw new OnlineException("too_big", "that isn't a replay's size");
            }
            List<string> to = r.To.Where(c => c != me).Distinct().ToList();
            if (to.Count == 0 || to.Count > ReplayMailRules.MaxRecipients)
            {
                throw new OnlineException("bad_request", $"send a replay to 1-{ReplayMailRules.MaxRecipients} friends");
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_store.ReplaysSentSince(me, now - 3600) >= SendsPerHour)
            {
                throw new OnlineException("slow_down", "you've shared a lot of replays this hour: try again later");
            }
            if (_store.ReplayBytes() + r.Bytes > ReplayCapBytes)
            {
                throw new OnlineException("server_full", "the server can't take more shared replays right now");
            }
            if (_store.ReplayBytesFrom(me) + r.Bytes > SenderCapBytes)
            {
                throw new OnlineException("slow_down", "your shared replays haven't been picked up yet: try again later");
            }
            var reply = new ReplaySendMsg();
            foreach (string c in to)
            {
                string? why = !_store.HasLink(me, c, LinkKind.Friend) ? "not_friend"
                    : _store.Blocked(me, c) ? "blocked"
                    : _store.BlocksReplays(c) ? "blocks_replays"
                    : _store.ReplaysQueued(c) >= ReplayMailRules.MaxWaiting ? "queue_full"
                    : null;
                if (why != null) reply.Refused.Add(new ReplayRefusal { Code = c, Reason = why });
                else reply.Sent.Add(c);
            }
            if (reply.Sent.Count == 0) return reply;
            string title = WordFilter.Mask(Cut(r.Title?.Trim(), ReplayMailRules.TitleMax) ?? "");
            var row = new Store.ReplayMailRow(NewReplayId(), me, title, WordFilter.Mask(Cut(r.Mode, 32) ?? ""),
                WordFilter.Mask(Cut(r.Arena, 64) ?? ""),
                Math.Clamp(r.Seconds, 0, 24 * 3600), r.Bytes, Math.Max(r.Recorded, 0), now, Uploaded: false);
            _store.AddReplayMail(row, reply.Sent);
            reply.ReplayId = row.Id;
            reply.Ticket = NewTicket(row.Id, me, upload: true);
            return reply;
        }

        private ReplaysMsg ReplaysWaiting(string me)
        {
            var reply = new ReplaysMsg { Block = _store.BlocksReplays(me) };
            if (_replayDir.Length == 0) return reply;
            foreach (Store.ReplayMailRow row in _store.ReplaysWaiting(me))
            {
                if (StillFor(row.Sender, me)) reply.Offers.Add(Offer(row, me));
            }
            return reply;
        }

        // a shared replay still goes to them: friends, nobody blocking (unfriended or blocked since it was sent: held back,
        // and it goes with the rest at KeepDays)
        private bool StillFor(string sender, string me) => _store.HasLink(sender, me, LinkKind.Friend) && !_store.Blocked(sender, me);

        private ReplayTicketMsg ReplayFetch(string me, string id)
        {
            RequireReplays();
            if (_store.ReplayMail(id) is not { Uploaded: true } row || !_store.ReplayWaitsFor(id, me) || !StillFor(row.Sender, me))
            {
                throw new OnlineException("not_found", "that shared replay isn't waiting for you");
            }
            DateTime now = DateTime.UtcNow;
            if (_replayTickets.Values.Count(t => t.Code == me && t.Until > now) >= TicketsEach)
            {
                throw new OnlineException("slow_down", "too many downloads at once: try again in a few minutes");
            }
            return new ReplayTicketMsg { ReplayId = id, Ticket = NewTicket(id, me, upload: false) };
        }

        private OkMsg ReplayDrop(string me, string id)
        {
            if (_replayDir.Length == 0 || !_store.ReplayWaitsFor(id, me)) return new OkMsg { Done = false };
            if (_store.DropReplay(id, me)) DeleteReplay(id);
            return new OkMsg { Done = true };
        }

        private OkMsg ReplayBlock(string me, bool block)
        {
            _store.SetBlocksReplays(me, block);
            return new OkMsg { Done = true };
        }

        private ReplayOffer Offer(Store.ReplayMailRow row, string viewer) => new()
        {
            Id = row.Id,
            From = _store.Get(row.Sender) is Store.Player p ? Card(p, viewer) : new PlayerCard { Code = row.Sender, Name = "?" },
            Title = row.Title, Mode = row.Mode, Arena = row.Arena, Seconds = row.Seconds, Bytes = row.Bytes, Recorded = row.Recorded,
            Sent = row.At,
        };

        private string NewTicket(string id, string code, bool upload)
        {
            string t = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _replayTickets[t] = new ReplayTicket(id, code, upload, DateTime.UtcNow + TicketLife);
            return t;
        }

        private static string NewReplayId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

        private ReplayTicket? TakeTicket(string id, string? token, bool upload)
        {
            if (string.IsNullOrEmpty(token) || !_replayTickets.TryRemove(token, out ReplayTicket? t)) return null;
            return t.Id == id && t.Upload == upload && t.Until > DateTime.UtcNow ? t : null;
        }

        // PUT /v1/replays/{id}?t=: the sender's file (a status code back)
        public async Task<int> PutReplay(string id, string? token, Stream body, CancellationToken ct)
        {
            if (_replayDir.Length == 0) return StatusCodes.Status404NotFound;
            if (TakeTicket(id, token, upload: true) is null || _store.ReplayMail(id) is not { Uploaded: false } row)
            {
                return StatusCodes.Status403Forbidden;
            }
            string path = ReplayPath(id), tmp = path + ".part";
            long length = 0;
            byte[] head = new byte[ReplayMailRules.Magic.Length]; // (the header, over as many reads as it comes in)
            try
            {
                await using (FileStream f = File.Create(tmp))
                {
                    byte[] buffer = new byte[1 << 16];
                    int n;
                    while ((n = await body.ReadAsync(buffer, ct)) > 0)
                    {
                        if (length < head.Length)
                        {
                            int take = (int)Math.Min(n, head.Length - length);
                            buffer.AsSpan(0, take).CopyTo(head.AsSpan((int)length));
                            if (length + take == head.Length && !head.AsSpan().SequenceEqual(ReplayMailRules.Magic))
                            {
                                throw new InvalidDataException("not a replay");
                            }
                        }
                        length += n;
                        if (length > row.Bytes) throw new InvalidDataException("bigger than it said");
                        await f.WriteAsync(buffer.AsMemory(0, n), ct);
                    }
                }
                if (length != row.Bytes) throw new InvalidDataException("smaller than it said");
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or BadHttpRequestException)
            {
                TryDelete(tmp);
                DeleteReplay(id);
                return ex is InvalidDataException ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
            }
            _store.MarkReplayUploaded(id, length);
            Store.ReplayMailRow done = row with { Bytes = length, Uploaded = true };
            foreach (string c in _store.ReplayRecipients(id))
            {
                if (StillFor(row.Sender, c)) Push(c, new ReplayOfferEvent { Offer = Offer(done, c) });
            }
            Console.WriteLine($"replays: {row.Sender} shared {id} ({length / 1024} KB) with {_store.ReplayRecipients(id).Count}");
            return StatusCodes.Status200OK;
        }

        // GET /v1/replays/{id}?t=: a friend's download
        public async Task GetReplay(HttpContext ctx, string id, string? token)
        {
            string path = ReplayPath(id);
            if (_replayDir.Length == 0 || TakeTicket(id, token, upload: false) is not ReplayTicket t || !_store.ReplayWaitsFor(id, t.Code)
                || _store.ReplayMail(id) is not Store.ReplayMailRow row || !StillFor(row.Sender, t.Code) || !File.Exists(path))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = new FileInfo(path).Length;
            await ctx.Response.SendFileAsync(path, ctx.RequestAborted);
        }

        private void DeleteReplay(string id)
        {
            _store.DeleteReplayMail(id);
            TryDelete(ReplayPath(id));
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private void PurgeReplaysSafe()
        {
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int gone = 0;
                foreach (string id in _store.StaleReplays(now - ReplayMailRules.KeepDays * 86400L, now - 3600))
                {
                    DeleteReplay(id);
                    gone++;
                }
                // files nobody knows (a crash between the two steps)
                var known = _store.ReplayIds().ToHashSet();
                foreach (string f in Directory.GetFiles(_replayDir))
                {
                    string name = Path.GetFileName(f);
                    string id = name.Split('.')[0];
                    if (!known.Contains(id) && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1))
                    {
                        TryDelete(f);
                        gone++;
                    }
                }
                foreach (var (token, t) in _replayTickets)
                {
                    if (t.Until < DateTime.UtcNow) _replayTickets.TryRemove(token, out _);
                }
                if (gone > 0) Console.WriteLine($"replays: {gone} expired or orphaned shared replay(s) deleted");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("replays: purge failed: " + ex.Message);
            }
        }
    }
}
