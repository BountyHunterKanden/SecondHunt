using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // 0.9.0: shared replays (HubReplays.cs) through the app's own calls (Core Online/ReplayMail.cs): a friend gets it pushed
    // and listed, downloads the same bytes, drops it (the file goes once nobody waits); who can't get one and why; the block
    // setting; tickets used once; a file that isn't a replay or isn't the size it said refused.
    public static partial class SelfTest
    {
        private static async Task Replays(string dir)
        {
            (WebApplication app, Uri uri) = await Start(Path.Combine(dir, "replays.db"));
            Hub hub = app.Services.GetRequiredService<Hub>();
            OnlineClient a = await Connect(uri, OnlineIdentity.Create(), "Sender");
            OnlineClient b = await Connect(uri, OnlineIdentity.Create(), "Friend1");
            OnlineClient c = await Connect(uri, OnlineIdentity.Create(), "Friend2");
            OnlineClient s = await Connect(uri, OnlineIdentity.Create(), "Stranger");
            foreach (OnlineClient f in new[] { b, c })
            {
                await a.RequestAsync<OkMsg>(new AddReq { Code = f.Me.Code, Kind = LinkKind.Friend });
                await f.RequestAsync<OkMsg>(new AddReq { Code = a.Me.Code, Kind = LinkKind.Friend });
            }
            Check(ReplayMail.Supported(a), $"shared replays: the server says {a.ServerVersion} (>= {ReplaySendReq.SinceServer})");

            byte[] file = ReplayMailRules.Magic.Concat(Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7))).ToArray();
            await c.RequestAsync<OkMsg>(new ReplayBlockReq { Block = true });
            var pushed = Next<ReplayOfferEvent>(b);
            ReplaySendMsg sent = await ReplayMail.SendAsync(a, uri, file, new[] { b.Me.Code, c.Me.Code, s.Me.Code }, "my best game",
                "Battle", "Combat Hall", 300, 1_760_000_000);
            ReplayOfferEvent? ev = await pushed;
            Check(sent.Sent.SequenceEqual(new[] { b.Me.Code })
                && sent.Refused.Any(r => r.Code == c.Me.Code && r.Reason == "blocks_replays")
                && sent.Refused.Any(r => r.Code == s.Me.Code && r.Reason == "not_friend"),
                "send to a friend, a friend who blocks shared replays and a stranger: only the friend gets it, the others say why");
            Check(ev != null && ev.Offer.Id == sent.ReplayId && ev.Offer.From.Code == a.Me.Code && ev.Offer.Title == "my best game"
                && ev.Offer.Bytes == file.Length && ev.Offer.Seconds == 300, "the friend online is told as the upload ends (who, title, size)");
            ReplaysMsg waiting = await ReplayMail.WaitingAsync(b);
            ReplaysMsg blocked = await ReplayMail.WaitingAsync(c);
            Check(waiting.Offers.Count == 1 && waiting.Offers[0].Id == sent.ReplayId && !waiting.Block
                && blocked.Offers.Count == 0 && blocked.Block, "get_replays: the friend's queue has it; the blocking friend's is empty, block on");
            Check(await ErrorOf(() => ReplayMail.DownloadAsync(s, uri, sent.ReplayId)) == "not_found",
                "a player it doesn't wait for can't download it");
            byte[] got = await ReplayMail.DownloadAsync(b, uri, sent.ReplayId);
            Check(got.SequenceEqual(file), $"the friend downloads the same {file.Length / 1024} KB");
            string path = Path.Combine(hub.ReplayDir, sent.ReplayId + ".mphreplay");
            bool fileThere = File.Exists(path);
            await ReplayMail.DropAsync(b, sent.ReplayId);
            Check(fileThere && !File.Exists(path) && (await ReplayMail.WaitingAsync(b)).Offers.Count == 0,
                "dropped by the last friend it waited for: off the queue and the file deleted");

            // DELETE without downloading, and a used ticket
            var second = await ReplayMail.SendAsync(a, uri, file, new[] { b.Me.Code }, "", "Survival", "Sic Transit", 60, 0);
            var t = await b.RequestAsync<ReplayTicketMsg>(new ReplayFetchReq { ReplayId = second.ReplayId });
            using var http = new HttpClient();
            Uri get = new(ReplayMail.HttpBase(uri), $"v1/replays/{second.ReplayId}?t={t.Ticket}");
            var first = await http.GetAsync(get);
            var again = await http.GetAsync(get);
            Check(first.IsSuccessStatusCode && (int)again.StatusCode == 404, "a download ticket works once");
            await ReplayMail.DropAsync(b, second.ReplayId);
            Check((await ReplayMail.WaitingAsync(b)).Offers.Count == 0, "DELETE takes it off the queue without downloading");

            // uploads that aren't a replay, or not the size announced, are refused and go
            var bad = await a.RequestAsync<ReplaySendMsg>(new ReplaySendReq { To = { b.Me.Code }, Bytes = 1000, Title = "x" });
            var put = await http.PutAsync(new Uri(ReplayMail.HttpBase(uri), $"v1/replays/{bad.ReplayId}?t={bad.Ticket}"),
                new ByteArrayContent(new byte[1000]));
            var shortOne = await a.RequestAsync<ReplaySendMsg>(new ReplaySendReq { To = { b.Me.Code }, Bytes = 5000, Title = "y" });
            var put2 = await http.PutAsync(new Uri(ReplayMail.HttpBase(uri), $"v1/replays/{shortOne.ReplayId}?t={shortOne.Ticket}"),
                new ByteArrayContent(file.Take(4000).ToArray()));
            Check((int)put.StatusCode == 400 && (int)put2.StatusCode == 400 && (await ReplayMail.WaitingAsync(b)).Offers.Count == 0,
                "an upload without the replay header, or shorter than announced, is refused and never reaches the friend");
            Check(await ErrorOf(() => a.RequestAsync<ReplaySendMsg>(new ReplaySendReq { To = { b.Me.Code }, Bytes = ReplayMailRules.MaxBytes + 1 }))
                == "too_big", "a share bigger than the limit is refused before any upload");

            // unblocking: the friend can get them again
            await c.RequestAsync<OkMsg>(new ReplayBlockReq { Block = false });
            var third = await ReplayMail.SendAsync(a, uri, file, new[] { c.Me.Code }, "again", "Battle", "Combat Hall", 10, 0);
            Check(third.Sent.Count == 1 && (await ReplayMail.WaitingAsync(c)).Offers.Count == 1, "block off: shared replays come in again");

            // unfriended after the share: held back from the list and the download
            await c.RequestAsync<OkMsg>(new RemoveReq { Code = a.Me.Code, Kind = LinkKind.Friend });
            Check((await ReplayMail.WaitingAsync(c)).Offers.Count == 0
                && await ErrorOf(() => ReplayMail.DownloadAsync(c, uri, third.ReplayId)) == "not_found",
                "unfriended after the share: not listed, can't be downloaded");

            // a header split over reads: the first read 3 bytes
            var split = await a.RequestAsync<ReplaySendMsg>(new ReplaySendReq { To = { b.Me.Code }, Bytes = file.Length, Title = "split" });
            var put3 = await http.PutAsync(new Uri(ReplayMail.HttpBase(uri), $"v1/replays/{split.ReplayId}?t={split.Ticket}"),
                new StreamContent(new TrickleStream(file, 3)));
            Check(put3.IsSuccessStatusCode, $"an upload whose header comes over two reads is taken ({(int)put3.StatusCode})");

            // download tickets: TicketsEach live at once
            int tickets = 0;
            string? why = null;
            for (int i = 0; i < 12 && why == null; i++)
            {
                why = await ErrorOf(() => b.RequestAsync<ReplayTicketMsg>(new ReplayFetchReq { ReplayId = split.ReplayId }));
                if (why == null) tickets++;
            }
            Check(tickets == 8 && why == "slow_down", $"download tickets: 8 live at once, then slow_down ({tickets}, {why})");

            // Mode and Arena go through the word filter
            var named = await ReplayMail.SendAsync(a, uri, file, new[] { b.Me.Code }, "", "fuck", "Combat Hall", 10, 0);
            ReplayOffer? o = (await ReplayMail.WaitingAsync(b)).Offers.FirstOrDefault(x => x.Id == named.ReplayId);
            Check(o != null && o.Mode != "fuck", $"a share's mode is word-filtered ({o?.Mode})");

            foreach (OnlineClient x in new[] { a, b, c, s }) await x.DisposeAsync();
            await app.StopAsync();
        }

        // a body handed out a few bytes first (a slow link's first read)
        private sealed class TrickleStream(byte[] data, int first) : MemoryStream(data)
        {
            private bool _first = true;

            public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Take(count));

            public override async System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer,
                System.Threading.CancellationToken ct = default)
            {
                bool second = !_first && Position == first;
                if (second) await Task.Delay(150, ct); // (so the server's first read gets the few bytes alone)
                return await base.ReadAsync(buffer[..Take(buffer.Length)], ct);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken ct)
                => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

            private int Take(int count)
            {
                if (!_first) return count;
                _first = false;
                return Math.Min(count, first);
            }
        }
    }
}
