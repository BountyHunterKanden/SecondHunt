namespace MphRecomp.Online
{
    // Server notices (server 0.10.3, docs/ONLINE_PLAY.md section 13). Backward compatible without a protocol bump: the
    // server pushes ev_notice only to sessions whose HelloMsg set Notices (an older client would drop the connection on
    // an unknown "t"), and an older server ignores the Notices field. Refusals while draining are plain ErrorMsg code
    // "draining" with readable text, which older clients show as is.
    public static class NoticeKind
    {
        public const string Restart = "restart"; // the server restarts for an update in about Seconds
        public const string Cancel = "cancel";   // the restart was called off
        public const string Drained = "drained"; // every match has ended; the server goes down any moment now
    }

    public sealed class ServerNoticeEvent : OnlineMsg
    {
        public string Kind { get; set; } = NoticeKind.Restart;
        // seconds until the restart (0 = now / unknown)
        public int Seconds { get; set; }
        public string Text { get; set; } = "";
    }
}
