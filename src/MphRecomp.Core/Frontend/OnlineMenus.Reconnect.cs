using System;
using System.Threading.Tasks;
using MphRecomp.Online;

namespace MphRecomp.Frontend
{
    // The hub's auto-reconnect (0.10.3, IDEAS_SURVEY B "hub auto-reconnect"): a lost server connection is tried again
    // after a growing, jittered wait (ReconnectBackoff: 1, 2, 4 ... 30 s) while "reconnecting..." shows; once back, the
    // party this player was in is joined again (server 0.10.3 keeps a dropped member invited; an older one says no and
    // nothing happens) and a search that the drop or a server restart ended starts again. Login, presence and friends
    // come back with the login itself. A held match seat is the rejoin's (OfferRejoin), not this. "logged in on another
    // device" is never retried by itself (the two devices would throw each other off).
    // The server's restart notice (ServerNoticeEvent / WelcomeMsg.Restart) shows once as a message.
    public sealed partial class RecompMenus
    {
        private readonly Random _backoffRng = new();
        private bool _reconnecting;   // the connection dropped and the app is trying again by itself
        private string? _lostParty;   // the party this player was in when it dropped
        private bool _lostSearch;     // a search was on (or a restart ended it)
        private long _lostAt;
        private long _noticeSeen;     // OnlineClient.NoticeAt of the notice already shown
        private bool _noticeRestartShown; // this restart's message showed (a Cancel clears it)
        private bool _refreshSoon;    // a push changed the lists: refresh, at most once a second
        private long _eventRefreshAt;
        private const int ReconnectingTries = 6; // (~1 min of tries, then the plain "offline" text)
        private const long RestoreWithinMs = 120_000;

        // the status lines: "reconnecting..." after a drop, "connecting..." for a first connect
        private bool Trying => _connecting || _reconnecting;
        private string TryingText => _reconnecting ? "reconnecting..." : "connecting...";

        // tools (-onlinereconnecttest)
        internal OnlineClient? OnlineClientForTools => _client;
        internal bool OnlineReconnecting => _reconnecting;
        internal PartyInfo? OnlineParty => _party;

        // TickOnline: the server connection dropped (before CloseOnline)
        private void LostServer(OnlineClient c)
        {
            long now = Environment.TickCount64;
            if (c.CloseReason?.Contains("another device", StringComparison.OrdinalIgnoreCase) == true)
            {
                _reconnecting = false;
                _lostParty = null;
                _lostSearch = false;
                _retryAt = long.MaxValue; // (opening an online page again connects: this device takes the login back)
                _netError = "logged in on another device.";
                return;
            }
            if (_party != null && _partyConn == c) _lostParty = _party.Id;
            if (_finding) _lostSearch = true;
            _finding = false; // (the server ended the search with the connection)
            _findEv = null;
            if (_onlineMenu?.Page?.Index == s_onVote) _onlineMenu.GoTo(s_onFind);
            _lostAt = now;
            _reconnecting = true;
            _retries = c.ClosedForRestart || c.Notice != null ? ReconnectBackoff.AfterRestartAttempt : 0;
            ConnectFailed();
            _netError = c.ClosedForRestart ? "the server is restarting." : "lost the server.";
        }

        // RefreshOnline's failed connect: the next try after a growing, jittered wait
        private void ConnectFailed()
        {
            if (_retryAt == long.MaxValue && !_reconnecting) return; // (another device has the login)
            _retryAt = Environment.TickCount64 + ReconnectBackoff.DelayMs(_retries, _backoffRng);
            _retries++;
            if (_retries > ReconnectingTries) _reconnecting = false;
        }

        // the connect's Post: back online (a reconnect puts back the party and the search)
        private void RestoreAfterReconnect(OnlineClient c)
        {
            bool was = _reconnecting;
            _reconnecting = false;
            string? party = _lostParty;
            bool search = _lostSearch;
            _lostParty = null;
            _lostSearch = false;
            if (!was && party == null && !search || Environment.TickCount64 - _lostAt > RestoreWithinMs) return;
            Console.WriteLine($"online: reconnected{(party != null ? ", joining the party again" : "")}{(search ? ", searching again" : "")}");
            if (party == null)
            {
                if (search) Post(ResumeSearch);
                return;
            }
            Task.Run(async () =>
            {
                try
                {
                    PartyMsg joined = await c.RequestAsync<PartyMsg>(new PartyJoinReq { PartyId = party });
                    Post(() =>
                    {
                        if (_client != c) return;
                        _partyConn = c;
                        _party = joined.Party;
                        Redraw();
                        if (search) ResumeSearch();
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Console.WriteLine("online: the party is gone after the reconnect: " + ex.Message);
                    if (search) Post(ResumeSearch); // (alone now: search alone)
                }
            });
        }

        // a search the drop ended starts again (the leader's or a solo player's; a member's leader starts it again)
        private void ResumeSearch()
        {
            if (_client == null || _finding || _joining || _creating || _hosting != null || _joined != null || !FindOn || !PartyLeader) return;
            StartSearch();
        }

        // TickOnline: the server's restart notice, once (a FindEvent "restart" cancel says it too)
        private void TickNotice()
        {
            OnlineClient? c = _client ?? (_hosting ?? _joined)?.Client;
            if (c != null && c.Notice == null) _noticeRestartShown = false; // (called off)
            if (c?.Notice is not ServerNoticeEvent n || c.NoticeAt == _noticeSeen || Import.Popup is not MenuPopup popup || popup.IsOpen) return;
            _noticeSeen = c.NoticeAt;
            // (no match running: the server drains at once and the Drained notice can land in the same frame as the
            // Restart one; it says "restarting now", shown unless this restart's message already showed)
            bool shown = _noticeRestartShown;
            _noticeRestartShown = n.Kind is NoticeKind.Restart or NoticeKind.Drained;
            if (n.Kind != NoticeKind.Restart && (n.Kind != NoticeKind.Drained || shown)) return;
            Console.WriteLine($"online: server notice: {(n.Kind == NoticeKind.Drained ? "restarting now" : $"restart in {n.Seconds} s")}");
            popup.Inform(n.Text.Length > 0 ? n.Text
                : n.Kind == NoticeKind.Drained ? "the server is restarting for an update now. back in a moment."
                : $"the server restarts for an update in about {(n.Seconds < 90 ? n.Seconds + " s" : (n.Seconds + 30) / 60 + " min")}."
                    + "\n\nmatches already playing finish first.");
        }

        // TickOnline: a push changed the lists (presence, friends, a game closed): refresh, at most once a second
        private bool RefreshDue(bool changed)
        {
            long now = Environment.TickCount64;
            if (changed) _refreshSoon = true;
            if (!(now >= _nextRefresh || _refreshSoon && now >= _eventRefreshAt)) return false;
            _refreshSoon = false;
            _eventRefreshAt = now + 1000;
            return true;
        }
    }
}
