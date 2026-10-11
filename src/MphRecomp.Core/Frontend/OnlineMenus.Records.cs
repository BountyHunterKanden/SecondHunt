using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;
using MphRecomp.Replay;

// Head to head on the server (T3, owner note N30 2026-10-10: "for fesuasda and Ana there are discrepancies for head to head
// on either device"). Before, each device counted its own record as it reached RESULTS, by names matched to seats, and only
// online; a quit, a missed name or a LAN match left the two devices apart. Now the HOST of a network match (online or LAN)
// reports the finished match once: every human seat with an online identity (its friend code from the replay link's seat
// identities, ReplayLink.Latest), its place (a team mode: its team's best place) and team. The server (0.7.0) counts each pair
// of opponents once and every license reads the same record back (PlayerCard.Record). A report goes into
// online_pending_results.json first and leaves it once the server took it: online at once through the match's connection,
// a LAN match's (or one that couldn't reach the server) as the next online connection comes in. Local counting
// (CountHeadToHead) stays only for older servers; the server's numbers replace the device's as a license opens.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private const string PendingResultsFile = "online_pending_results.json";
        private const int MaxPendingResults = 50;
        private static MatchResults? s_resultReported;
        private static readonly object s_pendingLock = new();

        // RESULTS (any network match this device hosted): the report, queued, then sent when there's a connection
        private void ReportMatchResult()
        {
            MatchResults? r = MatchResults.Last;
            ReplayLink? link = ReplayLink.Latest;
            if (r == null || link == null || !link.IsHost || ReferenceEquals(r, s_resultReported)) return;
            s_resultReported = r;
            string? dir = ProfileDir;
            string? me = OnlineIdentity.Load(dir)?.FriendCode;
            if (me == null) return; // (never went online: nobody to count it for)
            var codes = new Dictionary<int, string>();
            foreach (ReplaySeatIdentity id in link.Identities)
            {
                if (id.FriendCode is string c && FriendCodes.Parse(c) is string code) codes[id.Seat] = code;
            }
            codes[r.LocalSeat] = me;
            var rows = new List<ResultRow>();
            foreach (MatchResults.Player p in r.Rows)
            {
                if (p.Bot || !codes.TryGetValue(p.Seat, out string? code) || rows.Any(x => x.Code == code)) continue;
                // a team mode is the team's result: its best place (the server compares across teams only)
                int place = r.Teams ? r.Rows.Where(x => x.Team == p.Team).Min(x => x.Place) : p.Place;
                rows.Add(new ResultRow { Code = code, Place = Math.Clamp(place, 0, MatchResultReq.MaxRows - 1), Team = r.Teams ? p.Team : -1 });
            }
            if (rows.Count < 2) return; // (only bots or players who never went online against this one)
            OnlineMatch? online = MultiplayerMenu.OnlineResults ? Kept : null;
            var report = new MatchResultReq
            {
                GameId = online?.Game.Id ?? "",
                Lan = online == null,
                MatchKey = Guid.NewGuid().ToString("N"),
                Teams = r.Teams,
                Rows = rows.Take(MatchResultReq.MaxRows).ToList(),
            };
            Console.WriteLine($"online: head to head report for {rows.Count} hunters ({(report.Lan ? "LAN" : "online " + report.GameId)})");
            lock (s_pendingLock)
            {
                List<MatchResultReq> pending = LoadPending(dir);
                pending.Add(report);
                SavePending(dir, pending.TakeLast(MaxPendingResults).ToList());
            }
            OnlineClient? client = online?.Client ?? Conn;
            if (client != null) _ = FlushResults(client, dir);
        }

        // the queued reports to a server that takes them (each one leaves the queue once the server answered it, counted or
        // not: a refused one would be refused again)
        private static async Task FlushResults(OnlineClient client, string? dir)
        {
            if (!client.Connected || !client.ServerAtLeast(MatchResultReq.SinceServer)) return;
            List<MatchResultReq> pending;
            lock (s_pendingLock) pending = LoadPending(dir);
            foreach (MatchResultReq report in pending)
            {
                try
                {
                    OkMsg ok = await client.RequestAsync<OkMsg>(report);
                    Console.WriteLine($"online: head to head report {(ok.Done ? "counted" : "already counted")}");
                }
                catch (OnlineException ex)
                {
                    Console.Error.WriteLine($"online: head to head report refused: {ex.Code} {ex.Message}");
                }
                catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    return; // (the connection went: the rest wait for the next one)
                }
                lock (s_pendingLock)
                {
                    List<MatchResultReq> now = LoadPending(dir);
                    now.RemoveAll(x => x.MatchKey == report.MatchKey);
                    SavePending(dir, now);
                }
            }
        }

        private static List<MatchResultReq> LoadPending(string? dir)
        {
            string? path = dir == null ? null : Path.Combine(dir, PendingResultsFile);
            try
            {
                if (path != null && File.Exists(path))
                {
                    return JsonSerializer.Deserialize<List<MatchResultReq>>(File.ReadAllText(path), OnlineProtocol.Json) ?? new();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                Console.Error.WriteLine($"online: {path}: {ex.Message}");
            }
            return path == null ? s_pendingMemory.ToList() : new List<MatchResultReq>();
        }

        // no profile folder (the PC tools): memory only
        private static List<MatchResultReq> s_pendingMemory = new();

        private static void SavePending(string? dir, List<MatchResultReq> pending)
        {
            if (dir == null)
            {
                s_pendingMemory = pending.ToList();
                return;
            }
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, PendingResultsFile), JsonSerializer.Serialize(pending, OnlineProtocol.Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"online: head to head reports not saved: {ex.Message}");
            }
        }

        // the server's record (0.7.0) as the license shows it; also kept on the device for when there's no connection
        private void KeepServerRecord(OnlineClient client, PlayerCard card)
        {
            if (!client.ServerAtLeast(MatchResultReq.SinceServer) || card.Code == MeCard?.Code) return;
            if (card.Record is HeadToHeadInfo h) HeadToHeads.Set(card.Code, new HeadToHead(h.Rounds, h.Victories, h.Last));
            else HeadToHeads.Remove(card.Code); // (the server's word: no finished match together yet)
        }

        // tools (-onlinerecordtest): queue and flush as the menus do
        internal void OnlineReportMatchResult() => ReportMatchResult();

        internal static int PendingResultCount(string? dir)
        {
            lock (s_pendingLock) return LoadPending(dir).Count;
        }
    }
}
