using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Social O7: the word filter, chat and its limits, mutes, reports with evidence, bans, the admin tool.
    public static partial class SelfTest
    {
        private static string Admin(string db, params string[] args)
        {
            var w = new StringWriter();
            global::SecondHunt.Server.Admin.Run(new[] { "--db", db, "--words", Path.Combine(AppContext.BaseDirectory, "wordlists", "ldnoobw_en.txt") }.Concat(args).ToArray(), w);
            return w.ToString();
        }

        private static async Task Safety(string dir)
        {
            WordFilterTests(dir);
            string db = Path.Combine(dir, "safety.db");
            WordFilter.Load(Path.Combine(AppContext.BaseDirectory, "wordlists", "ldnoobw_en.txt"));
            (WebApplication app, Uri uri) = await Start(db);
            var hub = app.Services.GetRequiredService<Hub>();
            hub.ChatUnlockMatches = 3;

            var idH = OnlineIdentity.Create();
            var idS = OnlineIdentity.Create();
            string devS = DeviceTag.Hash("selftest stranger device")!, devS2 = DeviceTag.Hash("selftest stranger phone")!;
            if (OperatingSystem.IsWindows())
            {
                Check(DeviceTag.Current.Length == 64, "this Windows PC has a device ID (MachineGuid, scrambled)");
            }
            OnlineClient h = await Connect(uri, idH, "Hosty");
            OnlineClient f = await Connect(uri, OnlineIdentity.Create(), "Friendy");
            OnlineClient s = await Connect(uri, idS, "Stranger", devS);
            OnlineClient x = await Connect(uri, OnlineIdentity.Create(), "Blocky");
            OnlineClient lone = await Connect(uri, OnlineIdentity.Create(), "Lone");
            await Friends(h, f);
            Check(h.ChatLeft == 3 && h.ChatMutedUntil == 0, "welcome: a new identity has 3 matches to go before typed chat reaches strangers");

            Check(await ErrorOf(() => lone.RequestAsync<OkMsg>(new ChatReq { Preset = 0 })) == "no_game", "chat outside a game: no_game");

            GameMsg g = await h.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: true) });
            await f.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" });
            await s.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" });
            await x.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" });

            // quick messages: everyone but the player who blocked the sender
            var gotH = Next<ChatEvent>(h, e => e.Preset == 0);
            var gotF = Next<ChatEvent>(f, e => e.Preset == 0);
            var gotS = Next<ChatEvent>(s, e => e.Preset == 0);
            var gotX = Next<ChatEvent>(x, e => e.Preset == 0, 700);
            await h.RequestAsync<OkMsg>(new ChatReq { Preset = 0 });
            ChatEvent? ev = await gotH;
            Check(ev != null && ev.Code == h.Me.Code && ev.Name == h.Me.ToString() && ev.GameId == g.Game.Id && ev.Text == ""
                && await gotF != null && await gotS != null && await gotX != null, "a quick message reaches the whole game, the sender too");
            // x blocked by s: x's message doesn't reach s, and s's doesn't reach x
            await s.RequestAsync<OkMsg>(new BlockReq { Code = x.Me.Code }); // (after joining: a block keeps people out of each other's games)
            var xToS = Next<ChatEvent>(s, e => e.Preset == 3, 700);
            var xToH = Next<ChatEvent>(h, e => e.Preset == 3, 700);
            await x.RequestAsync<OkMsg>(new ChatReq { Preset = 3 });
            Check(await xToS == null && await xToH != null, "a blocked pair never sees each other's chat");
            await s.RequestAsync<OkMsg>(new BlockReq { Code = x.Me.Code, Block = false });
            Check(await ErrorOf(() => h.RequestAsync<OkMsg>(new ChatReq { Preset = 99 })) == "bad_text"
                && await ErrorOf(() => h.RequestAsync<OkMsg>(new ChatReq { Text = "   " })) == "bad_text", "bad quick message / empty text refused");
            Check(h.Chat.Latest >= 1 && h.Chat.Recent(10).Any(l => l.IsPreset && l.Shown == "GG"), "the client's chat log holds the line");

            // new identity: s (no friends in the game, 0 matches) can't type to strangers; h can, to the friend f only
            await Task.Delay(800);
            string? locked = await ErrorOf(() => s.RequestAsync<OkMsg>(new ChatReq { Text = "hello there" }));
            Check(locked == "chat_locked", $"a new identity's typed chat to strangers is refused ({locked})");
            var fGot = Next<ChatEvent>(f, e => e.Text == "hi friend");
            var sGot = Next<ChatEvent>(s, e => e.Text == "hi friend", 700);
            await h.RequestAsync<OkMsg>(new ChatReq { Text = "hi friend" });
            Check(await fGot != null && await sGot == null, "...but typed chat still reaches the sender's friend, not the strangers");

            // matches with people unlock it: 3 matches started in this game
            for (int i = 0; i < 3; i++)
            {
                await h.RequestAsync<OkMsg>(new StartGameReq { Started = true });
                await h.RequestAsync<OkMsg>(new StartGameReq { Started = false });
            }
            await using (OnlineClient s2 = await Connect(uri, idS, "Stranger", devS))
            {
                Check(s2.ChatLeft == 0, "after 3 matches with people the welcome says typed chat is open");
            }
            s = await Connect(uri, idS, "Stranger", devS);
            await s.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" });
            await Task.Delay(800);

            // typed text is masked, in the echo and for the others; the original goes only into a report
            var maskedH = Next<ChatEvent>(h, e => e.Code == s.Me.Code && e.Text.Length > 0);
            await s.RequestAsync<OkMsg>(new ChatReq { Text = "you are a fucking idiot" });
            ChatEvent? masked = await maskedH;
            Check(masked != null && masked.Text == "you are a ******* idiot", $"typed chat is masked by the word filter ({masked?.Text})");
            await Task.Delay(800);
            var longH = Next<ChatEvent>(h, e => e.Text.StartsWith("long"));
            await s.RequestAsync<OkMsg>(new ChatReq { Text = "long " + new string('x', 200) });
            ChatEvent? longEv = await longH;
            Check(longEv != null && longEv.Text.Length == ChatReq.MaxChat, "typed chat is cut to 80 characters");

            // rate limit: the same line again, and a burst
            await Task.Delay(800);
            await s.RequestAsync<OkMsg>(new ChatReq { Text = "again" });
            Check(await ErrorOf(() => s.RequestAsync<OkMsg>(new ChatReq { Text = "again" })) == "chat_rate", "the same typed line twice in 10 s: chat_rate");
            int refused = 0;
            for (int i = 0; i < 8; i++)
            {
                if (await ErrorOf(() => s.RequestAsync<OkMsg>(new ChatReq { Preset = 5 })) == "chat_rate")
                {
                    refused++;
                }
            }
            Check(refused >= 2, $"a burst of quick messages is limited ({refused} of 8 refused)");
            await Task.Delay(10_500); // (the rate window)

            // report from chat carries what was said, unmasked; the same report again within a day is a repeat
            Check((await f.RequestAsync<OkMsg>(new ReportReq { Code = s.Me.Code, Reason = ReportReason.Chat })).Done, "report (chat) accepted");
            Check(!(await f.RequestAsync<OkMsg>(new ReportReq { Code = s.Me.Code, Reason = ReportReason.Chat })).Done, "...a repeat within a day isn't kept twice");
            Check((await h.RequestAsync<OkMsg>(new ReportReq { Code = s.Me.Code, Reason = ReportReason.Voice })).Done, "report (voice) accepted");
            string reports = Admin(db, "reports");
            Check(reports.Contains("chat") && reports.Contains("voice") && reports.Contains(s.Me.ToString()) && reports.Contains("[has chat text]"),
                "admin: the report queue lists both, the chat one with its text");
            long id = Int64.Parse(reports.Split('\n').First(l => l.Contains(" chat ")).Trim().Split(' ')[0].TrimStart('#'));
            string shown = Admin(db, "show", id.ToString());
            Check(shown.Contains("you are a fucking idiot") && shown.Contains("(quick)"),
                "admin show: what the player said, as typed, plus the game's chat around it");
            Check(Admin(db, "dismiss", id.ToString(), "false alarm").Contains("erased")
                && !Admin(db, "show", id.ToString()).Contains("fucking"), "admin dismiss: the chat text is erased from the report");

            // owner chat mute: typed chat refused, quick messages still work, then lifted
            Check(Admin(db, "mute", s.Me.ToString(), "1").Contains("typed chat is off"), "admin mute (by NAME#tag)");
            await Task.Delay(800);
            Check(await ErrorOf(() => s.RequestAsync<OkMsg>(new ChatReq { Text = "mute test" })) == "chat_muted"
                && (await s.RequestAsync<OkMsg>(new ChatReq { Preset = 7 })).Done, "a muted player: typed refused, quick message sent");
            Admin(db, "unmute", s.Me.Code);
            await Task.Delay(800);
            Check((await s.RequestAsync<OkMsg>(new ChatReq { Text = "unmuted" })).Done, "unmute: typed chat works again");

            // bans by key: the live connection is kicked, login refused, unban lets the key back in
            var kicked = Next<KickedEvent>(s, null, 8000); // (the full run is busy: a kick can take a few seconds to arrive)
            string banned = Admin(db, "ban", FriendCodes.Format(s.Me.Code), "1", "harassing", "players");
            hub.SafetyTick();
            bool wasKicked = await kicked != null;
            Check(banned.Contains("banned") && wasKicked, "admin ban: the connected player is kicked" + (wasKicked ? "" : $" [{banned.Trim()}; events {String.Join(",", s.Events.Select(e => e.GetType().Name))}, connected {s.Connected}, reason {s.CloseReason}, online {hub.OnlineCount}, bans: {Admin(db, "bans").Trim()}]"));
            string? refusal = null;
            try
            {
                await using var again = await Connect(uri, idS, "Stranger", devS);
            }
            catch (OnlineException ex)
            {
                refusal = ex.Code + ": " + ex.Message;
            }
            Check(refusal != null && refusal.StartsWith("banned") && refusal.Contains("harassing"), $"a banned key can't log in ({refusal})");
            Check(Admin(db, "bans").Contains(s.Me.Code) && Admin(db, "player", s.Me.Code).Contains("BANNED"), "admin bans / player show the ban");

            // device bans: the kicked player's device was recorded, so a NEW profile on it is refused; another device is fine;
            // the banned key on a second device is refused and that device joins the ban
            string? deviceRefusal = null;
            try
            {
                await using var fresh = await Connect(uri, OnlineIdentity.Create(), "Freshstart", devS);
            }
            catch (OnlineException ex)
            {
                deviceRefusal = ex.Code + ": " + ex.Message;
            }
            Check(deviceRefusal != null && deviceRefusal.StartsWith("banned") && deviceRefusal.Contains("device") && deviceRefusal.Contains("harassing"),
                $"a new profile on a banned device can't log in ({deviceRefusal})");
            await using (OnlineClient elsewhere = await Connect(uri, OnlineIdentity.Create(), "Otherdevice", DeviceTag.Hash("selftest other device")!))
            {
                Check(elsewhere.Connected, "a new profile on another device logs in");
            }
            try
            {
                await using var phone = await Connect(uri, idS, "Stranger", devS2);
            }
            catch (OnlineException)
            {
            }
            Check(Admin(db, "bans").Contains("2 devices"), $"the banned key's second device joins the ban ({Admin(db, "bans").Trim()})");
            Check(Admin(db, "unban", s.Me.Code).Contains("unbanned"), "admin unban");
            await using (OnlineClient back = await Connect(uri, idS, "Stranger", devS))
            {
                Check(back.Me.Code == s.Me.Code, "after unban the key logs in again");
            }
            await using (OnlineClient fresh = await Connect(uri, OnlineIdentity.Create(), "Freshstart", devS2))
            {
                Check(fresh.Connected, "after unban a new profile on that device logs in too");
            }

            // a name that the list refuses
            await using (OnlineClient d = await Connect(uri, OnlineIdentity.Create(), "sh1thead"))
            {
                Check(d.Me.Name == OnlineNames.Default, $"the filter refuses names too ({d.Me.Name})");
            }
            Check(Admin(db, "words", "what the fuck").Contains("masked: what the ****"), "admin words tests the filter");
            Check(Admin(db, "stats").Contains("players"), "admin stats");

            foreach (OnlineClient c in new[] { h, f, s, x, lone })
            {
                await c.DisposeAsync();
            }
            await app.StopAsync();
        }

        private static void WordFilterTests(string dir)
        {
            WordFilter.Load(Path.Combine(AppContext.BaseDirectory, "wordlists", "ldnoobw_en.txt"));
            string M(string t) => WordFilter.Mask(t);
            Check(WordFilter.Count > 300, $"LDNOOBW list loaded ({WordFilter.Count} entries)");
            Check(M("you fucking idiot") == "you ******* idiot" && M("Fuck this") == "**** this" && M("what the SHIT") == "what the ****",
                "filter: plain words and endings");
            Check(M("class assist pass Scunthorpe hello shell grape drape Dickens") == "class assist pass Scunthorpe hello shell grape drape Dickens",
                "filter: innocent words containing a short bad word are left alone");
            Check(M("sh1t") == "****" && M("$h!t") == "****" && M("fuuuuck") == "*******" && M("f u c k") == "* * * *" && M("a$$") == "***",
                "filter: digit and symbol swaps, stretched, spaced out");
            Check(M("two girls one cup") == "*** ***** *** ***", "filter: phrases");
            Check(WordFilter.Allowed("Kanden") && WordFilter.Allowed("Samus") && !WordFilter.Allowed("xxfuckxx") && !WordFilter.Allowed("S H I T"),
                "filter: names");
            // the owner's additions file: a word, an inside-words entry, and an allowed-again entry
            string main = Path.Combine(dir, "list.txt");
            string owner = Path.Combine(dir, "words_owner.txt");
            File.WriteAllText(main, "hell\nbadword\n");
            File.WriteAllText(owner, "# owner\n-hell\nnastything\n*zork\n");
            WordFilter.Load(main, owner);
            Check(M("go to hell") == "go to hell" && M("badword") == "*******" && M("nastythings") == "***********" && M("mzorkm") == "******"
                && M("zorg") == "zorg", "filter: owner file adds words, '*' catches inside words, '-' allows a list entry again");
            File.WriteAllText(owner, "hell\n");
            File.SetLastWriteTimeUtc(owner, DateTime.UtcNow.AddMinutes(1));
            WordFilter.Load(main, owner);
            Check(M("go to hell") == "go to ****", "filter: an edited owner file takes effect");
            WordFilter.Set(new[] { "badword" });
        }
    }
}
