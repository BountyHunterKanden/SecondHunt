using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MphRecomp.Online;

namespace MphRecomp.Replay
{
    // Friends' shared replays on this device (2026-10-19; the server side: Online/ReplayMail.cs). Waiting = the ones a
    // friend sent that wait on the server for this player to DOWNLOAD (into replays/shared/, then off the server) or DELETE;
    // filled at each ONLINE PLAY login (SyncAsync) and by the server's pushes (OnOffer). Block = refuse every shared replay:
    // kept in <data>/shared_replays.json, sent to the server at each login and when it changes. Send = a replay file to friends.
    // The menus read Waiting / Block and call these (REPLAYS' SHARED section, a replay's SEND TO FRIEND, the setting); the
    // calls that need the server take the connection (null or an older server: they refuse with why).
    // Own offers (server 0.9.7: this player's recording of a served match that didn't reach them over the match) aren't
    // shown or blocked: they're downloaded into REPLAYS as soon as they're seen (TakeOwnAsync) and dropped off the server.
    public static class SharedReplays
    {
        private const string FileName = "shared_replays.json";
        private static readonly object _lock = new();
        private static List<ReplayOffer> _waiting = new();
        private static bool? _block;
        private static readonly HashSet<string> _taking = new(); // own offers being downloaded (under _lock)

        // something changed (Waiting, Block): the menus redraw; may come on any thread
        public static event Action? Changed;

        public static IReadOnlyList<ReplayOffer> Waiting
        {
            get
            {
                lock (_lock) return _waiting.ToList();
            }
        }

        private sealed class Prefs
        {
            public bool Block { get; set; }
        }

        public static bool Blocked(string? dataDir)
        {
            if (_block is bool b) return b;
            try
            {
                string? path = dataDir == null ? null : Path.Combine(dataDir, FileName);
                _block = path != null && File.Exists(path) && JsonSerializer.Deserialize<Prefs>(File.ReadAllText(path))?.Block == true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _block = false;
            }
            return _block.Value;
        }

        // the setting: kept here, and told to the server when connected (else at the next login)
        public static async Task SetBlockAsync(string? dataDir, bool block, OnlineClient? client)
        {
            _block = block;
            if (dataDir != null)
            {
                try
                {
                    File.WriteAllText(Path.Combine(dataDir, FileName), JsonSerializer.Serialize(new Prefs { Block = block }));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine("shared replays: setting not saved: " + ex.Message);
                }
            }
            if (block)
            {
                lock (_lock) _waiting.Clear(); // (what waits on the server stays there until it expires or is unblocked)
            }
            Changed?.Invoke();
            if (client is { Connected: true } && ReplayMail.Supported(client))
            {
                await ReplayMail.SetBlockAsync(client, block);
                if (!block) await RefreshAsync(client);
            }
        }

        // ONLINE PLAY's login: the setting to the server, then what waits for this player (own recordings: taken at once)
        public static async Task SyncAsync(OnlineClient client, string? dataDir, Uri? server = null)
        {
            if (!ReplayMail.Supported(client)) return;
            try
            {
                await ReplayMail.SetBlockAsync(client, Blocked(dataDir));
                List<ReplayOffer> own = await RefreshAsync(client);
                if (own.Count > 0 && server != null && dataDir != null) await TakeOwnAsync(client, server, dataDir, own);
            }
            catch (OnlineException ex)
            {
                Console.Error.WriteLine("shared replays: " + ex.Message);
            }
        }

        // returns the own offers among them (not kept in Waiting)
        public static async Task<List<ReplayOffer>> RefreshAsync(OnlineClient client)
        {
            ReplaysMsg m = await ReplayMail.WaitingAsync(client);
            lock (_lock) _waiting = m.Block ? new List<ReplayOffer>() : m.Offers.Where(o => !o.Own).ToList();
            Changed?.Invoke();
            return m.Offers.Where(o => o.Own).ToList();
        }

        // the server's push: a friend shared one, or this player's own recording came (taken at once, with the connection)
        public static void OnOffer(ReplayOfferEvent e, OnlineClient? client = null, Uri? server = null, string? dataDir = null)
        {
            if (e.Offer.Own)
            {
                if (client != null && server != null && dataDir != null) _ = TakeOwnAsync(client, server, dataDir, [e.Offer]);
                return;
            }
            if (_block == true) return;
            lock (_lock)
            {
                if (_waiting.Any(o => o.Id == e.Offer.Id)) return;
                _waiting.Add(e.Offer);
            }
            Changed?.Invoke();
        }

        private static void Remove(string id)
        {
            lock (_lock) _waiting.RemoveAll(o => o.Id == id);
            Changed?.Invoke();
        }

        // DOWNLOAD: saved into replays/shared/ (checked whole first), then off the server; returns the saved file's path
        public static async Task<string> DownloadAsync(OnlineClient client, Uri server, string dataDir, ReplayOffer offer,
            CancellationToken ct = default)
        {
            byte[] bytes = await ReplayMail.DownloadAsync(client, server, offer.Id, ct);
            string from = offer.From.Name.Length > 0 ? $"{offer.From.Name}#{offer.From.Tag}" : "a friend";
            string path = ReplayLibrary.SaveShared(dataDir, bytes, from);
            // saved: a download now, even if taking it off the server fails (it goes when the server's offers expire)
            try
            {
                await ReplayMail.DropAsync(client, offer.Id, ct);
            }
            catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException)
            {
                Console.Error.WriteLine("shared replays: saved, not dropped: " + ex.Message);
            }
            Remove(offer.Id);
            return path;
        }

        // own recordings of served matches: into REPLAYS as if they'd come over the match, then off the server (one that
        // fails stays there for the next login)
        public static async Task TakeOwnAsync(OnlineClient client, Uri server, string dataDir, IEnumerable<ReplayOffer> offers)
        {
            foreach (ReplayOffer offer in offers)
            {
                lock (_lock)
                {
                    if (!_taking.Add(offer.Id)) continue;
                }
                try
                {
                    byte[] bytes = await ReplayMail.DownloadAsync(client, server, offer.Id);
                    ReplayFile file = ReplayFile.Read(new MemoryStream(bytes)); // (checked whole; OwnSeat is this player's)
                    DateTime when = offer.Recorded > 0 ? DateTimeOffset.FromUnixTimeSeconds(offer.Recorded).LocalDateTime : DateTime.Now;
                    string path = ReplayLibrary.Save(dataDir, file, when);
                    Console.WriteLine($"shared replays: your served match's recording saved as {Path.GetFileName(path)} "
                        + $"({bytes.Length / 1024} KB, from the server's mailbox)");
                    await ReplayMail.DropAsync(client, offer.Id);
                    Changed?.Invoke();
                }
                catch (Exception ex) when (ex is OnlineException or IOException or InvalidDataException or EndOfStreamException
                    or UnauthorizedAccessException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException
                    or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                {
                    Console.Error.WriteLine("shared replays: your served match's recording not taken yet: " + ex.Message);
                }
                finally
                {
                    lock (_lock) _taking.Remove(offer.Id);
                }
            }
        }

        // DELETE: off the server without downloading it
        public static async Task DeleteAsync(OnlineClient client, ReplayOffer offer, CancellationToken ct = default)
        {
            await ReplayMail.DropAsync(client, offer.Id, ct);
            Remove(offer.Id);
        }

        // SEND TO FRIEND: the replay file at `path` to these friends (their friend codes); the reply says who gets it and who
        // not (ReplayRefusal.Reason: not_friend, blocked, blocks_replays, queue_full)
        public static async Task<ReplaySendMsg> SendAsync(OnlineClient client, Uri server, string path, IEnumerable<string> friends,
            CancellationToken ct = default)
        {
            if (!ReplayMail.Supported(client)) throw new OnlineException("old_server", "the server can't pass replays on yet");
            byte[] bytes = await File.ReadAllBytesAsync(path, ct);
            ReplayInfo info = ReplayLibrary.Describe(path, pinned: false);
            string title = info.Title.Length > 0 ? info.Title : $"{info.Mode} {info.Arena}";
            long recorded = info.Recorded is DateTime when ? new DateTimeOffset(when).ToUnixTimeSeconds() : 0;
            return await ReplayMail.SendAsync(client, server, bytes, friends, title, info.Mode?.ToString() ?? "", info.Arena, info.Seconds,
                recorded, ct);
        }

        // a refusal for the player: "kanden#421 doesn't take shared replays"
        public static string Why(ReplayRefusal r, string who) => r.Reason switch
        {
            "not_friend" => $"{who} isn't your friend",
            "blocked" => $"{who} can't get replays from you",
            "blocks_replays" => $"{who} doesn't take shared replays",
            "queue_full" => $"{who} has too many shared replays waiting",
            _ => $"{who}: {r.Reason}",
        };
    }
}
