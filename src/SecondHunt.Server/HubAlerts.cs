using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using MphRecomp.Online;

// A phone alert for each new report (owner 2026-10-09: "will I get an email with a report? if not we need to set that up";
// picked a phone push through ntfy, one alert per report). REPORT_ALERT_URL = an ntfy topic URL (https://ntfy.sh/<secret
// topic>; the owner's phone subscribes to it in the ntfy app), kept beside the TURN key in /etc/secondhunt/turn.env
// (root-only, the unit's EnvironmentFile=-), never in the repo. The alert says only the report's kind: no names, codes or
// chat leave the server (docs/PRIVACY_NOTICE.md lists no other service), so it's a nudge to run `admin.ps1 reports`.
// Grooming, threats and the like come in as chat / voice / other: those three are sent at high priority. A failed send
// is logged and dropped (the report itself is already saved).
namespace SecondHunt.Server
{
    public sealed partial class Hub
    {
        public string ReportAlertUrl { get; set; } = "";

        private void AlertReport(string reason)
        {
            string url = ReportAlertUrl;
            if (url.Length == 0)
            {
                return;
            }
            bool urgent = reason is ReportReason.Chat or ReportReason.Voice or ReportReason.Other;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent($"new {reason} report. run: admin.ps1 reports", Encoding.UTF8, "text/plain"),
                    };
                    req.Headers.Add("Title", "Second Hunt report");
                    req.Headers.Add("Priority", urgent ? "high" : "default");
                    req.Headers.Add("Tags", "rotating_light");
                    using HttpResponseMessage res = await s_http.SendAsync(req);
                    if (!res.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"report alert: HTTP {(int)res.StatusCode}");
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    Console.WriteLine($"report alert failed: {ex.GetType().Name}");
                }
            });
        }
    }
}
