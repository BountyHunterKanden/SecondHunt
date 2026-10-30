using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // ONLINE PLAY's chat (Social track O7; Core Frontend/OnlineMenus.Chat.cs, server SecondHunt.Server/HubChat.cs), on the PC:
    //   -chattest [server=]   live (server 0.5.0+, default the live one): the menus CREATE a battle, a PC hunter joins it, then
    //                         1 the joiner's quick message shows in the menus' lobby strip and on CHAT; 2 the menus' quick
    //                         message (CHAT's second button) reaches the joiner; 3 the joiner's typed line is refused
    //                         goes through (no match minimum; both are new hunters and strangers); 4 as friends it goes through, the
    //                         bad word masked; 5 MUTE (yes) hides the joiner's lines on CHAT and in the lobby, the license's
    //                         UNMUTE brings them back; 6 the line's REPORT (yes) is taken by the server.
    //                         Renders -> extract_out/online_menus/build/chat_live_<page>.png
    internal static class ChatTool
    {
        private static string RomKey => LanSession.RomKeyFor(Paths.MphKey);
        private static T Wait<T>(System.Threading.Tasks.Task<T> task) => task.GetAwaiter().GetResult();

        public static void Test(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = args.Skip(1).Select(x => x.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            var uri = new Uri(a.GetValueOrDefault("server", RecompMenus.OnlineServer));
            Console.WriteLine($"chattest: {uri}");
            string? fail = null;
            string dir = Path.Combine(Path.GetTempPath(), "chat-test-" + Guid.NewGuid().ToString("N")[..6]);
            FrontendSession s = Session(dir);
            OnlineMatch? joiner = null;
            OnlineIdentity joinerId = OnlineIdentity.Create();
            try
            {
                (int whoPage, int lobby) = RecompMenus.OnlineCreatePages;
                (int chatPage, int reportPage) = RecompMenus.OnlineChatPages;
                if (chatPage < 0)
                {
                    fail = "the chat pages aren't installed";
                    return;
                }
                // ---- the menus host a 2-player battle ----
                s.Recomp.OpenOnline(s.Menu);
                if (!RunUntil(s, () => s.Recomp.OnlineConnected, 20))
                {
                    fail = "the menus didn't connect";
                    return;
                }
                Run(s, 30);
                s.Recomp.OnCall(s.Menu, 2502, inPause: false); // CREATE
                if (!RunUntil(s, () => s.Menu.Page?.Index == MultiplayerMenu.ModePage && s.Menu.PendingPage == -1, 5))
                {
                    fail = "CREATE didn't open the mode page";
                    return;
                }
                Run(s, 30);
                s.TouchDs(44, 68); // battle -> its rules
                Run(s, 40);
                s.TouchDs(212, 173); // -> SELECT HUNTER
                if (!RunUntil(s, () => s.Menu.Page?.Index == MultiplayerMenu.HunterPage && s.Menu.PendingPage == -1, 5))
                {
                    fail = "the rules page didn't go on to SELECT HUNTER";
                    return;
                }
                Run(s, 30);
                s.Multiplayer.OnCall(s.Menu, new MenuAction(), 132, -1); // Samus
                if (!RunUntil(s, () => s.Menu.Page?.Index == whoPage && s.Menu.PendingPage == -1, 5))
                {
                    fail = "SELECT HUNTER didn't go on to WHO CAN JOIN";
                    return;
                }
                s.Recomp.OnCall(s.Menu, 2560, inPause: false); // public
                for (int i = 0; i < 4; i++) s.Recomp.OnCall(s.Menu, 2565, inPause: false); // PLAYERS down to 2
                Run(s, 30);
                s.Recomp.OnCall(s.Menu, 2570, inPause: false); // CREATE
                if (!RunUntil(s, () => s.Menu.Page?.Index == lobby && s.Recomp.OnlineHosting != null && s.Menu.PendingPage == -1, 15))
                {
                    fail = "CREATE didn't open the lobby";
                    return;
                }
                OnlineMatch host = s.Recomp.OnlineHosting!;
                OnlineClient hc = host.Client;
                if (!hc.ServerAtLeast(ChatReq.SinceServer))
                {
                    fail = $"the server is {hc.ServerVersion}: chat needs {ChatReq.SinceServer}";
                    return;
                }
                string hostCode = hc.Me!.Code;
                Console.WriteLine($"  hosting {host.Game.Id}; menus {hostCode}, chat left {hc.ChatLeft}");
                OnlineClient jc = Wait(OnlineClient.ConnectAsync(uri, joinerId, "PCCHAT", "chattest"));
                joiner = Wait(OnlineMatch.JoinAsync(jc, uri, RomKey, host.Game.Id));
                if (!joiner.Net.Join(Hunter.Kanden, "PCCHAT", 3, timeoutMs: 15000) || !RunUntil(s, () => host.Net.Joined >= 1, 10))
                {
                    fail = "the PC joiner got no seat";
                    return;
                }
                string joinCode = jc.Me!.Code;
                Run(s, 60);
                Shot(s, "chat_live_lobby_empty");

                // 1 the joiner's quick message: the lobby strip, then CHAT
                Wait(jc.RequestAsync<OkMsg>(new ChatReq { Preset = 0 }));
                if (!RunUntil(s, () => HostLines(host).Any(l => l.Code == joinCode && l.Preset == 0), 10))
                {
                    fail = "1: the joiner's quick message never reached the menus";
                    return;
                }
                Run(s, 30);
                Shot(s, "chat_live_lobby");
                s.Recomp.OnCall(s.Menu, 2740, inPause: false); // the lobby's CHAT
                if (!RunUntil(s, () => s.Menu.Page?.Index == chatPage && s.Menu.PendingPage == -1, 5))
                {
                    fail = "1: the lobby's CHAT didn't open the chat page";
                    return;
                }
                Run(s, 30);
                if (s.Recomp.OnlineHosting != host || host.Disposed)
                {
                    fail = "1: going to CHAT dropped the hosted game";
                    return;
                }
                if (!s.Recomp.OnlineChatShown.Any(l => l.Code == joinCode))
                {
                    fail = "1: CHAT doesn't list the joiner's line";
                    return;
                }
                Console.WriteLine("  1 ok: the joiner's GG shows in the lobby and on CHAT (the game stays)");

                // 2 the menus' quick message (the second: "Nice shot!") reaches the joiner
                s.Recomp.OnCall(s.Menu, 2751, inPause: false);
                if (!RunUntil(s, () => jc.Chat.Recent(20).Any(l => l.Code == hostCode && l.Preset == 1), 10))
                {
                    fail = "2: the menus' quick message never reached the joiner";
                    return;
                }
                Console.WriteLine("  2 ok: the menus' NICE SHOT! reached the joiner");

                // 3 typed, strangers, both new: goes through (the server has no match minimum for typed chat since 2026-10-23)
                string? locked = null;
                try
                {
                    Wait(jc.RequestAsync<OkMsg>(new ChatReq { Text = "hi, good luck out there" }));
                }
                catch (OnlineException ex)
                {
                    locked = ex.Code;
                    Console.WriteLine($"  3: {ex.Code}: {ex.Message}");
                }
                if (locked != null)
                {
                    fail = $"3: a new stranger's typed line was refused ({locked}); typed chat should be open from the start";
                    return;
                }
                Console.WriteLine("  3 ok: a new hunter's typed line to a stranger goes through");

                // 4 friends: typed goes through, masked
                Wait(jc.RequestAsync<OkMsg>(new AddReq { Code = hostCode, Kind = LinkKind.Friend }));
                Wait(hc.RequestAsync<OkMsg>(new AddReq { Code = joinCode, Kind = LinkKind.Friend }));
                Thread.Sleep(1200); // (the server wants 700 ms between typed lines)
                Wait(jc.RequestAsync<OkMsg>(new ChatReq { Text = "what the fuck, nice shot" }));
                if (!RunUntil(s, () => HostLines(host).Any(l => l.Code == joinCode && l.Preset < 0), 10))
                {
                    fail = "4: the friend's typed line never reached the menus";
                    return;
                }
                string got = HostLines(host).Last(l => l.Code == joinCode && l.Preset < 0).Text;
                if (got != "what the ****, nice shot")
                {
                    fail = $"4: the typed line came as \"{got}\" (expected the bad word masked)";
                    return;
                }
                Wait(hc.RequestAsync<OkMsg>(new ChatReq { Text = "thanks, rematch after this one?" }));
                Wait(jc.RequestAsync<OkMsg>(new ChatReq { Preset = 3 }));
                RunUntil(s, () => HostLines(host).Count(l => l.Code == joinCode) >= 3, 5);
                Run(s, 30);
                Shot(s, "chat_live_chat");
                s.Recomp.OnCall(s.Menu, 2741, inPause: false); // TYPE A MESSAGE: the keyboard
                Run(s, 60);
                Shot(s, "chat_live_keyboard");
                if (s.Recomp.OnlineHosting != host)
                {
                    fail = "4: the chat keyboard dropped the hosted game";
                    return;
                }
                s.Press(MenuKeys.B); // (cancel: back to CHAT)
                RunUntil(s, () => s.Menu.Page?.Index == chatPage && s.Menu.PendingPage == -1, 5);
                Run(s, 30);
                if (s.Menu.Page?.Index != chatPage || s.Recomp.OnlineHosting != host)
                {
                    fail = $"4: the keyboard's cancel didn't come back to CHAT with the game (page {s.Menu.Page?.Index})";
                    return;
                }
                Console.WriteLine($"  4 ok: friends: \"{got}\" (masked); the keyboard keeps the game");

                // 5 MUTE the joiner's line (yes): hidden on CHAT
                int row = s.Recomp.OnlineChatShown.ToList().FindIndex(l => l.Code == joinCode);
                s.Recomp.OnCall(s.Menu, 2770 + row, inPause: false);
                Run(s, 10);
                Yes(s);
                Run(s, 20);
                if (!PlayerMutes.Current.Chat(joinCode) || s.Recomp.OnlineChatShown.Any(l => l.Code == joinCode))
                {
                    fail = "5: MUTE didn't hide the joiner's lines";
                    return;
                }
                Shot(s, "chat_live_chat_muted");
                s.Popup?.Close();
                Console.WriteLine("  5 ok: MUTE hides the joiner's chat (mutes file " + File.Exists(Path.Combine(dir, PlayerMutes.FileName)) + ")");

                // 6 unmute (a second MUTE on the license is UNMUTE: through the lobby's seat card), then REPORT from the line
                s.Menu.GoTo(lobby);
                RunUntil(s, () => s.Menu.Page?.Index == lobby && s.Menu.PendingPage == -1, 5);
                Run(s, 30);
                Shot(s, "chat_live_lobby_muted");
                s.Recomp.OnCall(s.Menu, 2581, inPause: false); // the joiner's seat card -> their license
                RunUntil(s, () => s.Menu.Page?.Index == RecompMenus.OnlineLicensePage && s.Menu.PendingPage == -1, 5);
                Run(s, 60);
                Shot(s, "chat_live_license");
                s.Recomp.OnCall(s.Menu, 2742, inPause: false); // UNMUTE (no question)
                Run(s, 10);
                s.Popup?.Close();
                if (PlayerMutes.Current.Any(joinCode))
                {
                    fail = "6: the license's UNMUTE didn't unmute";
                    return;
                }
                s.Recomp.OnCall(s.Menu, 2685, inPause: false); // REPORT -> REPORT A HUNTER
                RunUntil(s, () => s.Menu.Page?.Index == reportPage && s.Menu.PendingPage == -1, 5);
                Run(s, 30);
                Shot(s, "chat_live_report");
                s.Press(MenuKeys.B); // back to the license, then the lobby, then CHAT
                RunUntil(s, () => s.Menu.Page?.Index == RecompMenus.OnlineLicensePage && s.Menu.PendingPage == -1, 5);
                s.Press(MenuKeys.B);
                RunUntil(s, () => s.Menu.Page?.Index == lobby && s.Menu.PendingPage == -1, 5);
                if (s.Recomp.OnlineHosting != host)
                {
                    fail = $"6: the license / report pages dropped the hosted game (page {s.Menu.Page?.Index})";
                    return;
                }
                s.Recomp.OnCall(s.Menu, 2740, inPause: false);
                RunUntil(s, () => s.Menu.Page?.Index == chatPage && s.Menu.PendingPage == -1, 5);
                Run(s, 30);
                row = s.Recomp.OnlineChatShown.ToList().FindIndex(l => l.Code == joinCode && l.Preset < 0);
                if (row < 0)
                {
                    fail = "6: after UNMUTE the joiner's lines aren't on CHAT";
                    return;
                }
                s.Recomp.OnCall(s.Menu, 2776 + row, inPause: false); // REPORT (asks)
                Run(s, 10);
                Yes(s);
                bool reported = RunUntil(s, () => s.Popup?.IsOpen == true, 5);
                Run(s, 30);
                Shot(s, "chat_live_reported");
                s.Popup?.Close();
                if (!reported || !hc.Connected)
                {
                    fail = "6: REPORT got no answer (or dropped the connection)";
                    return;
                }
                Console.WriteLine("  6 ok: UNMUTE on the license, REPORT A HUNTER page, a line's REPORT sent (admin: reports)");
            }
            catch (Exception ex) when (ex is OnlineException or InvalidOperationException or IOException
                or System.Net.WebSockets.WebSocketException or OperationCanceledException)
            {
                fail = "exception: " + ex.Message;
            }
            finally
            {
                joiner?.Dispose();
                s.Recomp.OnlineHosting?.Dispose();
                Thread.Sleep(500);
                DeleteTestProfiles(uri, dir, (joinerId, "PCCHAT"));
                Console.WriteLine(fail == null ? "PASS chattest: lobby + CHAT quick messages both ways, new-hunter lock, friends' typed chat masked, MUTE / UNMUTE, REPORT"
                    : "FAIL chattest: " + fail);
            }
        }

        private static List<ChatLog.Line> HostLines(OnlineMatch m) => m.Client.Chat.Recent(ChatLog.Keep).Where(l => l.GameId == m.Game.Id).ToList();

        private static FrontendSession Session(string dir)
        {
            RecompMenus.OnlineNoticeSkipped = true;
            Directory.CreateDirectory(dir);
            return FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
        }

        private static void Run(FrontendSession s, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                s.Tick();
                Thread.Sleep(16);
            }
        }

        private static bool RunUntil(FrontendSession s, Func<bool> done, int seconds)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed.TotalSeconds < seconds)
            {
                s.Tick();
                Thread.Sleep(16);
            }
            return done();
        }

        private static void Yes(FrontendSession s)
        {
            s.Press(MenuKeys.A);
            Run(s, 3);
            s.Press(MenuKeys.A);
            Run(s, 40);
        }

        private static void Shot(FrontendSession s, string name)
        {
            s.Build(1920, 1080);
            using Image<Rgba32> img = FrontendTool.RasterCanvas(s.DrawList, 1920, 1080);
            string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "online_menus", "build"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".png");
            img.SaveAsPng(path);
            Console.WriteLine($"  page {s.Menu.Page?.Index} -> {path}");
        }

        private static void DeleteTestProfiles(Uri uri, string menusDir, params (OnlineIdentity Id, string Name)[] pcs)
        {
            var all = pcs.ToList();
            if (OnlineIdentity.Load(menusDir) is OnlineIdentity menus) all.Add((menus, OnlineNames.Default));
            foreach ((OnlineIdentity id, string name) in all)
            {
                try
                {
                    OnlineClient c = Wait(OnlineClient.ConnectAsync(uri, id, name, "chattest"));
                    Wait(c.RequestAsync<OkMsg>(new DeleteProfileReq()));
                    Wait(c.DisposeAsync().AsTask().ContinueWith(_ => 0));
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or IOException
                    or InvalidOperationException)
                {
                    Console.WriteLine($"  cleanup: {name}: {ex.Message}");
                }
            }
        }
    }
}
