using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // SecondHuntServer --selftest: the identity maths against the platform crypto, then a real server on a free localhost
    // port with real WebSocket clients through every O1/O2 flow and the O3 games + UDP relay (docs/ONLINE_PLAY.md section
    // 11), then a restart on the same database. Prints PASS/FAIL per check; exit code 0 = all passed.
    public static partial class SelfTest
    {
        private static int _pass, _fail;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
            if (ok)
            {
                _pass++;
            }
            else
            {
                _fail++;
            }
        }

        public static async Task<int> Run()
        {
            Identity();
            string dir = Path.Combine(Path.GetTempPath(), "secondhunt-selftest-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                // SELFTEST_ONLY=safety runs just the chat and safety checks (a faster loop while working on them)
                if (Environment.GetEnvironmentVariable("SELFTEST_ONLY") == "records")
                {
                    await Records(dir);
                    Console.WriteLine($"{_pass} passed, {_fail} failed");
                    return _fail == 0 ? 0 : 1;
                }
                if (Environment.GetEnvironmentVariable("SELFTEST_ONLY") == "safety")
                {
                    await Safety(dir);
                    Console.WriteLine($"{_pass} passed, {_fail} failed");
                    return _fail == 0 ? 0 : 1;
                }
                await Server(Path.Combine(dir, "test.db"));
                FindRulesTests();
                await FindGame(Path.Combine(dir, "find.db"));
                await TurnTests(dir);
                await Safety(dir);
                await Records(dir); // (T3 0.7.0: friend OR rival, head to head: SelfTestRecords.cs)
            }
            catch (Exception ex)
            {
                Check(false, $"server run threw {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
            Console.WriteLine($"{_pass} passed, {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }

        private static void Identity()
        {
            // our P-256 multiply = the platform's own key generation
            bool same = true;
            for (int i = 0; i < 4; i++)
            {
                using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                ECParameters p = ec.ExportParameters(includePrivateParameters: true);
                (byte[] x, byte[] y) = P256.MultiplyBase(new BigInteger(p.D, isUnsigned: true, isBigEndian: true));
                same &= x.SequenceEqual(p.Q.X!) && y.SequenceEqual(p.Q.Y!);
            }
            Check(same, "P-256 public point from the private scalar = the platform's (4 keys)");

            byte[] seed = Enumerable.Range(1, OnlineIdentity.SeedBytes).Select(i => (byte)(i * 37)).ToArray();
            string backup = BackupCodes.Encode(seed);
            OnlineIdentity? a = OnlineIdentity.FromBackupCode(backup);
            Check(a != null && backup.Length == 34 && backup.Count(c => c == '-') == 6, $"backup code {backup}");
            if (a == null)
            {
                return;
            }
            OnlineIdentity? again = OnlineIdentity.FromBackupCode(backup.ToLowerInvariant().Replace('1', 'l').Replace("-", " "));
            Check(again != null && again.PublicKey.SequenceEqual(a.PublicKey) && again.FriendCode == a.FriendCode,
                "backup code typed lower case, l for 1, spaces = the same player");
            const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            int caught = 0, typos = 0;
            for (int i = 0; i < backup.Length; i++)
            {
                if (backup[i] == '-')
                {
                    continue;
                }
                char swapped = alphabet[(alphabet.IndexOf(backup[i]) + 1) % 32];
                typos++;
                caught += OnlineIdentity.FromBackupCode(backup[..i] + swapped + backup[(i + 1)..]) == null ? 1 : 0;
            }
            Check(caught == typos, $"backup code: {caught}/{typos} one-character typos refused");

            byte[] data = "hello"u8.ToArray();
            byte[] sig = a.Sign(data);
            var other = OnlineIdentity.Create();
            Check(OnlineIdentity.Verify(a.PublicKey, data, sig) && !OnlineIdentity.Verify(a.PublicKey, "hellp"u8.ToArray(), sig)
                && !OnlineIdentity.Verify(other.PublicKey, data, sig), "signatures verify only with the right key and data");

            string code = a.FriendCode;
            bool typoCaught = Enumerable.Range(0, 12).All(i =>
                !FriendCodes.IsValid(code[..i] + (char)('0' + (code[i] - '0' + 1) % 10) + code[(i + 1)..]));
            Check(FriendCodes.IsValid(code) && FriendCodes.Parse(FriendCodes.Format(code)) == code
                && FriendCodes.Parse(code.Insert(4, " ")) == code && FriendCodes.Parse("1234-5678-901x") == null && typoCaught,
                $"friend code {FriendCodes.Format(code)}: parses, every one-digit typo refused");

            (string n1, int? t1) = OnlineNames.ParseQuery("kanden#421");
            (string n2, int? t2) = OnlineNames.ParseQuery("  Sylux ");
            Check(n1 == "kanden" && t1 == 421 && n2 == "Sylux" && t2 == null && OnlineNames.Display("KANDEN", 7) == "KANDEN#0007"
                && OnlineNames.Clean("abcdefghijklmn") == "abcdefghij", "names: query parse, tag display, 10-character limit");
        }

        // device: the scrambled device ID to log in with ("" = none, so this PC's own never joins a test's bans)
        private static async Task<OnlineClient> Connect(Uri uri, OnlineIdentity id, string name, string device = "")
            => await OnlineClient.ConnectAsync(uri, id, name, "selftest", default, device);

        // the next push of type T on c (that matches), or null after the timeout; start waiting before the action
        private static Task<T?> Next<T>(OnlineClient c, Func<T, bool>? match = null, int ms = 3000) where T : OnlineMsg
        {
            var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<OnlineMsg>? handler = null;
            handler = m =>
            {
                if (m is T t && (match == null || match(t)))
                {
                    c.EventReceived -= handler;
                    tcs.TrySetResult(t);
                }
            };
            c.EventReceived += handler;
            _ = Task.Delay(ms).ContinueWith(_ =>
            {
                c.EventReceived -= handler;
                tcs.TrySetResult(null);
            });
            return tcs.Task;
        }

        private static async Task<string?> ErrorOf(Func<Task> action)
        {
            try
            {
                await action();
                return null;
            }
            catch (OnlineException ex)
            {
                return ex.Code;
            }
        }

        private static async Task<(WebApplication App, Uri Uri)> Start(string db, FindConfig? find = null, TurnConfig? turn = null)
        {
            WebApplication app = ServerApp.Build(db, "http://127.0.0.1:0", quiet: true, relayPort: 0, find: find, turn: turn);
            await app.StartAsync();
            string http = app.Urls.First();
            return (app, new Uri(http.Replace("http://", "ws://") + "/v1/ws"));
        }

        private static async Task Server(string db)
        {
            WordFilter.Set(new[] { "badword" });
            (WebApplication app, Uri uri) = await Start(db);
            var idA = OnlineIdentity.Create();
            var idB = OnlineIdentity.Create();
            var idC = OnlineIdentity.Create();

            OnlineClient a = await Connect(uri, idA, "Kanden");
            OnlineClient b = await Connect(uri, idB, "kanden");
            OnlineClient c = await Connect(uri, idC, "Sylux");
            Check(a.Me.Code == idA.FriendCode && b.Me.Code == idB.FriendCode, "friend code on the server = from the key");
            Check(a.Me.Name == "Kanden" && b.Me.Name == "kanden" && a.Me.Tag != b.Me.Tag,
                $"two Kandens, two tags: {a.Me} and {b.Me}");
            await using (OnlineClient d = await Connect(uri, OnlineIdentity.Create(), "x B4dw0rd"))
            {
                Check(d.Me.Name == OnlineNames.Default, $"filtered name becomes {OnlineNames.Default}: {d.Me}");
            }

            // search: by name, by name#tag, by friend code; "friend code only" hides from name search
            var found = await c.RequestAsync<PlayersMsg>(new SearchReq { Query = "KANDEN" });
            Check(found.Players.Count == 2, $"search KANDEN finds 2 ({found.Players.Count})");
            found = await c.RequestAsync<PlayersMsg>(new SearchReq { Query = $"kanden#{a.Me.Tag}" });
            Check(found.Players.Count == 1 && found.Players[0].Code == a.Me.Code, "search kanden#tag finds just that one");
            await b.RequestAsync<OkMsg>(new SetFindableReq { Findable = false });
            found = await c.RequestAsync<PlayersMsg>(new SearchReq { Query = "kanden" });
            var byCode = await c.RequestAsync<PlayersMsg>(new SearchReq { Query = FriendCodes.Format(b.Me.Code) });
            Check(found.Players.Count == 1 && found.Players[0].Code == a.Me.Code && byCode.Players.Count == 1
                && byCode.Players[0].Code == b.Me.Code, "'friend code only': hidden from name search, found by code");
            Check(found.Players[0].Presence == Presence.Offline, "a stranger's presence isn't shown");

            // friend request C -> A, accepted
            var request = Next<RequestEvent>(a);
            var sent = await c.RequestAsync<OkMsg>(new AddReq { Code = a.Me.Code, Kind = LinkKind.Friend });
            RequestEvent? got = await request;
            Check(!sent.Done && got?.From.Code == c.Me.Code && got.Kind == LinkKind.Friend, "friend request arrives as a push");
            var roster = await a.RequestAsync<RosterMsg>(new RosterReq());
            Check(roster.Requests.Any(q => q.Incoming && q.Player.Code == c.Me.Code), "roster lists the incoming request");
            var addedC = Next<AddedEvent>(c);
            await a.RequestAsync<OkMsg>(new AnswerReq { Code = c.Me.Code, Kind = LinkKind.Friend, Accept = true });
            Check((await addedC)?.Player.Code == a.Me.Code, "accept: the asker hears it");
            roster = await a.RequestAsync<RosterMsg>(new RosterReq());
            Check(roster.Friends.Count == 1 && roster.Friends[0].Code == c.Me.Code && roster.Friends[0].Presence == Presence.Online
                && roster.Requests.Count == 0, "friends list: C, online, request gone");

            // vanilla: both enter each other's code -> friends at once
            var ab = await a.RequestAsync<OkMsg>(new AddReq { Code = b.Me.Code });
            var ba = await b.RequestAsync<OkMsg>(new AddReq { Code = a.Me.Code });
            roster = await b.RequestAsync<RosterMsg>(new RosterReq());
            Check(!ab.Done && ba.Done && roster.Friends.Any(f => f.Code == a.Me.Code), "both entered the code: friends");
            Check(await ErrorOf(() => a.RequestAsync<OkMsg>(new AddReq { Code = a.Me.Code })) == "self"
                && await ErrorOf(() => a.RequestAsync<OkMsg>(new AddReq { Code = "000000000000" })) == "not_found",
                "own code and unknown codes refused");

            // presence: B leaves, A hears; a rival request waits for B; B comes back
            var offline = Next<PresenceEvent>(a, e => e.Code == idB.FriendCode && e.Presence == Presence.Offline);
            await b.DisposeAsync();
            Check(await offline != null, "a friend going offline is pushed");
            await c.RequestAsync<OkMsg>(new AddReq { Code = idB.FriendCode, Kind = LinkKind.Rival });
            var online = Next<PresenceEvent>(a, e => e.Code == idB.FriendCode && e.Presence == Presence.Online);
            b = await Connect(uri, idB, "kanden");
            Check(await online != null, "a friend coming online is pushed");
            roster = await b.RequestAsync<RosterMsg>(new RosterReq());
            Check(roster.Requests.Any(q => q.Incoming && q.Kind == LinkKind.Rival && q.Player.Code == c.Me.Code),
                "a rival request sent while offline waits for the player");
            var rival = Next<AddedEvent>(c, e => e.Kind == LinkKind.Rival);
            await b.RequestAsync<OkMsg>(new AnswerReq { Code = c.Me.Code, Kind = LinkKind.Rival, Accept = true });
            Check((await rival)?.Player.Code == b.Me.Code, "rival accepted");

            // licenses: anyone can open yours
            await a.RequestAsync<OkMsg>(new SetLicenseReq { License = "{\"rank\":3,\"hunter\":\"Kanden\"}" });
            var card = await b.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = a.Me.Code });
            Check(card.Player.License?.Contains("Kanden") == true && card.Player.Friend, "B opens A's license");
            Check(await ErrorOf(() => a.RequestAsync<OkMsg>(new SetLicenseReq { License = "not json" })) == "bad_license",
                "a license that isn't JSON is refused");

            // report (server 0.2.0): kept once a day per reporter, player and reason; a bad reason or nobody refused
            Check(b.ServerAtLeast(ReportReason.SinceServer), $"the challenge says the server takes reports ({b.ServerVersion})");
            var reported = await b.RequestAsync<OkMsg>(new ReportReq { Code = a.Me.Code, Reason = ReportReason.Name });
            var again = await b.RequestAsync<OkMsg>(new ReportReq { Code = a.Me.Code, Reason = ReportReason.Name });
            Check(reported.Done && !again.Done, "a report is kept, the same one again the same day isn't");
            Check(await ErrorOf(() => b.RequestAsync<OkMsg>(new ReportReq { Code = a.Me.Code, Reason = "spam" })) == "bad_reason",
                "a report with an unknown reason is refused");
            Check(await ErrorOf(() => b.RequestAsync<OkMsg>(new ReportReq { Code = "000000000000" })) == "not_found",
                "a report on nobody is refused");
            Check(await ErrorOf(() => b.RequestAsync<OkMsg>(new ReportReq { Code = b.Me.Code })) == "not_found",
                "a report on yourself is refused");

            // delete_profile (server 0.2.0): D, A's friend, deletes itself: A is told, D is gone, its connection closes
            var idD = OnlineIdentity.Create();
            OnlineClient doomed = await Connect(uri, idD, "Doomed");
            await doomed.RequestAsync<OkMsg>(new AddReq { Code = a.Me.Code, Kind = LinkKind.Friend });
            await a.RequestAsync<OkMsg>(new AddReq { Code = doomed.Me.Code, Kind = LinkKind.Friend });
            var gone = Next<RemovedEvent>(a, e => e.Code == idD.FriendCode);
            var deleted = await doomed.RequestAsync<OkMsg>(new DeleteProfileReq());
            Check(deleted.Done && (await gone)?.Kind == LinkKind.Friend, "a deleted profile's friends are told");
            Check(await ErrorOf(() => a.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = idD.FriendCode })) == "not_found"
                && !(await a.RequestAsync<RosterMsg>(new RosterReq())).Friends.Any(f => f.Code == idD.FriendCode),
                "a deleted profile is gone from the server and from its friends' rosters");
            await Task.Delay(300);
            Check(!doomed.Connected, "the server closes a deleted profile's connection");
            await doomed.DisposeAsync();
            OnlineClient reborn = await Connect(uri, idD, "Doomed");
            Check(reborn.Me.Code == idD.FriendCode && !(await reborn.RequestAsync<RosterMsg>(new RosterReq())).Friends.Any(),
                "the same key logging in again starts a new profile");
            await reborn.DisposeAsync();

            // rename keeps the tag when it's free
            int tag = a.Me.Tag;
            var renamed = await a.RequestAsync<PlayerMsg>(new SetNameReq { Name = "Kanden2" });
            Check(renamed.Player.Name == "Kanden2" && renamed.Player.Tag == tag && a.Me.Name == "Kanden2", $"rename: {a.Me}");

            // block: A blocks C
            var removed = Next<RemovedEvent>(c);
            await a.RequestAsync<OkMsg>(new BlockReq { Code = c.Me.Code });
            Check((await removed)?.Code == a.Me.Code, "block ends the friendship");
            found = await c.RequestAsync<PlayersMsg>(new SearchReq { Query = "kanden2" });
            var quiet = Next<RequestEvent>(a, ms: 500);
            var tryAgain = await c.RequestAsync<OkMsg>(new AddReq { Code = a.Me.Code });
            Check(found.Players.Count == 0 && !tryAgain.Done && await quiet == null,
                "blocked: not in their search, their requests never arrive");
            Check(await ErrorOf(() => c.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = a.Me.Code })) == "not_found",
                "blocked: they can't open your card");
            roster = await a.RequestAsync<RosterMsg>(new RosterReq());
            Check(roster.Blocked.Count == 1 && roster.Blocked[0].Code == c.Me.Code && roster.Friends.All(f => f.Code != c.Me.Code),
                "blocked list");

            // a bad signature is refused
            using (var raw = new ClientWebSocket())
            {
                await raw.ConnectAsync(uri, CancellationToken.None);
                var challenge = (ChallengeMsg)(await OnlineProtocol.ReceiveAsync(raw, CancellationToken.None))!;
                await OnlineProtocol.SendAsync(raw, new HelloMsg
                {
                    Key = Convert.ToBase64String(idA.PublicKey),
                    Sig = Convert.ToBase64String(idC.Sign(OnlineProtocol.LoginPayload(Convert.FromBase64String(challenge.Nonce)))),
                    Proto = OnlineProtocol.Version,
                    Name = "thief",
                }, CancellationToken.None);
                var refused = await OnlineProtocol.ReceiveAsync(raw, CancellationToken.None);
                Check(refused is ErrorMsg { Code: "bad_signature" }, "someone else's key with a wrong signature is refused");
            }

            // the same player on a second device: the first is told and closed
            var kicked = Next<KickedEvent>(a);
            OnlineClient a2 = await Connect(uri, idA, "Kanden2");
            Check(await kicked != null && a2.Me.Code == idA.FriendCode, "logging in elsewhere closes the old connection");

            await a.DisposeAsync();
            await a2.DisposeAsync();
            await b.DisposeAsync();
            await c.DisposeAsync();

            await Games(uri, app.Services.GetRequiredService<Relay>());
            await app.StopAsync();
            await app.DisposeAsync();

            // restart on the same database: everything is still there
            (app, uri) = await Start(db);
            OnlineClient a3 = await Connect(uri, idA, "Kanden2");
            roster = await a3.RequestAsync<RosterMsg>(new RosterReq());
            Check(a3.Me.Code == idA.FriendCode && a3.Me.Tag == tag && roster.Friends.Any(f => f.Code == idB.FriendCode)
                && roster.Blocked.Count == 1, "server restart: same code, tag, friends, blocks");
            OnlineClient restored = await Connect(uri, OnlineIdentity.FromBackupCode(idA.BackupCode)!, "Kanden2");
            Check(restored.Me.Code == idA.FriendCode, "the backup code logs in as the same player");
            await a3.DisposeAsync();
            await restored.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }

        // O3: hosted games (who sees them, joining, leaving, presence) and the UDP relay with real sockets
        private static async Task Games(Uri uri, Relay relay)
        {
            OnlineClient h = await Connect(uri, OnlineIdentity.Create(), "Host");
            OnlineClient f = await Connect(uri, OnlineIdentity.Create(), "Friend");
            OnlineClient r = await Connect(uri, OnlineIdentity.Create(), "Rival");
            OnlineClient s = await Connect(uri, OnlineIdentity.Create(), "Stranger");
            OnlineClient x = await Connect(uri, OnlineIdentity.Create(), "Blocked");
            await h.RequestAsync<OkMsg>(new AddReq { Code = f.Me.Code });
            await f.RequestAsync<OkMsg>(new AddReq { Code = h.Me.Code });
            await h.RequestAsync<OkMsg>(new AddReq { Code = r.Me.Code, Kind = LinkKind.Rival });
            await r.RequestAsync<OkMsg>(new AddReq { Code = h.Me.Code, Kind = LinkKind.Rival });
            await h.RequestAsync<OkMsg>(new BlockReq { Code = x.Me.Code });
            const string version = "rom1/sim3";

            async Task<bool> Sees(OnlineClient who, string id)
                => (await who.RequestAsync<GamesMsg>(new ListGamesReq())).Games.Any(g => g.Id == id);

            // friends only, 3 players
            var lobby = Next<PresenceEvent>(f, e => e.Code == h.Me.Code && e.Presence == Presence.Lobby);
            var info = new GameInfo
            {
                Mode = "Battle", Arena = "Combat Hall", MaxPlayers = 3, Friends = true, Rivals = false, Version = version,
            };
            GameMsg hosted = await h.RequestAsync<GameMsg>(new HostGameReq { Info = info });
            string id = hosted.Game.Id;
            Check(hosted.Relay.Slot == 0 && hosted.Relay.Port == relay.Port && RelayWire.ParseToken(hosted.Relay.Token) != 0
                && hosted.Game.Players == 1, $"host a game: {id}, relay slot 0 on UDP {relay.Port}");
            Check((await lobby)?.GameId == id, "the host's friend sees them in a lobby they can join");
            Check(await Sees(f, id) && !await Sees(r, id) && !await Sees(s, id) && !await Sees(x, id),
                "friends-only game: listed for the friend, not the rival, a stranger or a blocked player");

            // an invite lets the rival in; going public lets the stranger in, never the blocked player
            var invite = Next<InviteEvent>(r);
            await h.RequestAsync<OkMsg>(new InviteReq { Code = r.Me.Code });
            Check((await invite)?.Game.Id == id && await Sees(r, id), "an invited rival gets the invite and sees the game");
            Check(await ErrorOf(() => h.RequestAsync<OkMsg>(new InviteReq { Code = s.Me.Code })) == "not_friend",
                "only friends and rivals can be invited");
            await h.RequestAsync<GameMsg>(new UpdateGameReq
            {
                Info = new GameInfo { Mode = "Battle", Arena = "Combat Hall", MaxPlayers = 3, Public = true, Version = version },
            });
            Check(await Sees(s, id) && !await Sees(x, id), "public game: the stranger sees it, the blocked player doesn't");
            Check(await ErrorOf(() => x.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = version }))
                == "not_found", "a blocked player can't join");

            // joining: the version must match; slots 1, 2; members hear it; then it's full
            Check(await ErrorOf(() => s.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = "rom0/sim3" }))
                == "version", "a different game version can't join");
            var hJoin = Next<MemberEvent>(h, e => e.Joined && e.Player.Code == f.Me.Code);
            GameMsg fj = await f.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = version });
            var hJoin2 = Next<MemberEvent>(h, e => e.Joined && e.Player.Code == r.Me.Code);
            var fJoin2 = Next<MemberEvent>(f, e => e.Joined && e.Player.Code == r.Me.Code);
            GameMsg rj = await r.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = version });
            Check(fj.Relay.Slot == 1 && rj.Relay.Slot == 2 && (await hJoin)?.Slot == 1 && (await hJoin2)?.Slot == 2
                && await fJoin2 != null && rj.Game.Players == 3, "two join: slots 1 and 2, the members hear each join");
            Check(!await Sees(s, id) && await ErrorOf(() => s.RequestAsync<GameMsg>(new JoinGameReq
            {
                GameId = id, Version = version,
            })) == "full", "a full game leaves the list and refuses joins");

            // the relay: real UDP sockets with the three tokens
            await Relays(relay, hosted.Relay, fj.Relay, rj.Relay);

            // start: presence match; only the host starts; back to the lobby
            var match = Next<PresenceEvent>(f, e => e.Code == h.Me.Code && e.Presence == Presence.Match);
            await h.RequestAsync<OkMsg>(new StartGameReq { Started = true });
            Check(await match != null, "start: friends see the host in a match");
            Check(await ErrorOf(() => f.RequestAsync<OkMsg>(new StartGameReq())) == "not_host", "only the host starts");
            await h.RequestAsync<OkMsg>(new StartGameReq { Started = false });

            // a member leaves; the host leaves = the game closes for everyone
            var left = Next<MemberEvent>(h, e => !e.Joined && e.Slot == 2);
            await r.RequestAsync<OkMsg>(new LeaveGameReq());
            Check(await left != null && await Sees(s, id), "a member leaves: the host hears it, the game is listed again");
            var closed = Next<GameClosedEvent>(f, e => e.GameId == id);
            var back = Next<PresenceEvent>(f, e => e.Code == h.Me.Code && e.Presence == Presence.Online);
            await h.RequestAsync<OkMsg>(new LeaveGameReq());
            Check(await closed != null && await back != null && !await Sees(s, id), "the host leaves: the game closes");

            // a lobby member whose connection drops leaves the game
            GameMsg again = await h.RequestAsync<GameMsg>(new HostGameReq { Info = info });
            await f.RequestAsync<GameMsg>(new JoinGameReq { GameId = again.Game.Id, Version = version });
            var dropped = Next<MemberEvent>(h, e => !e.Joined && e.Slot == 1);
            await f.DisposeAsync();
            Check(await dropped != null, "a member who goes offline in the lobby leaves it");

            await h.DisposeAsync();
            await r.DisposeAsync();
            await s.DisposeAsync();
            await x.DisposeAsync();

            await Recent(uri);
            await ExtraRulesListed(uri);
            await Parties(uri);
            await Mods(uri);
            await Rejoin(uri);
            await HeldSeats(uri);
            await SeatReturn(uri);
            await MigrateTests(uri);
        }

        private static async Task Friends(OnlineClient a, OnlineClient b, string kind = LinkKind.Friend)
        {
            await a.RequestAsync<OkMsg>(new AddReq { Code = b.Me.Code, Kind = kind });
            await b.RequestAsync<OkMsg>(new AddReq { Code = a.Me.Code, Kind = kind });
        }

        private static GameInfo Info(bool pub, string mods = GameInfo.NoMods) => new()
        {
            Mode = "Battle", Arena = "Combat Hall", MaxPlayers = 4, Friends = true, Rivals = true, Public = pub,
            Version = "rom1/sim3", Mods = mods,
        };

        // 0.6.0: a game's extra rules reach the list as the host set them (characters only with pickups); an older client's
        // game (no fields) lists as allowing both
        private static async Task ExtraRulesListed(Uri uri)
        {
            OnlineClient a = await Connect(uri, OnlineIdentity.Create(), "Vanilla");
            OnlineClient b = await Connect(uri, OnlineIdentity.Create(), "Charless");
            OnlineClient v = await Connect(uri, OnlineIdentity.Create(), "Viewer");
            GameInfo noPickups = Info(pub: true);
            noPickups.ExtraPickups = false; // (characters asked on: the server turns them off with the pickups)
            GameInfo noChars = Info(pub: true);
            noChars.ExtraCharacters = false;
            GameMsg ga = await a.RequestAsync<GameMsg>(new HostGameReq { Info = noPickups });
            GameMsg gb = await b.RequestAsync<GameMsg>(new HostGameReq { Info = noChars });
            System.Collections.Generic.List<GameCard> list = (await v.RequestAsync<GamesMsg>(new ListGamesReq())).Games;
            GameCard? la = list.FirstOrDefault(g => g.Id == ga.Game.Id), lb = list.FirstOrDefault(g => g.Id == gb.Game.Id);
            Check(la != null && !la.Info.ExtraPickups && !la.Info.ExtraCharacters && lb != null && lb.Info.ExtraPickups && !lb.Info.ExtraCharacters,
                "extra rules: the list carries each game's EXTRA PICKUPS / EXTRA CHARACTERS (no pickups = no characters)");
            await a.DisposeAsync();
            await b.DisposeAsync();
            await v.DisposeAsync();
        }

        // O2: recent players = everyone you were in a started match with; a block hides them both ways
        private static async Task Recent(Uri uri)
        {
            OnlineClient p1 = await Connect(uri, OnlineIdentity.Create(), "One");
            OnlineClient p2 = await Connect(uri, OnlineIdentity.Create(), "Two");
            OnlineClient p3 = await Connect(uri, OnlineIdentity.Create(), "Three");
            GameMsg g = await p1.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: true) });
            await p2.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" });
            await p3.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" });
            var before = await p1.RequestAsync<PlayersMsg>(new RecentReq());
            await p1.RequestAsync<OkMsg>(new StartGameReq { Started = true });
            var r1 = await p1.RequestAsync<PlayersMsg>(new RecentReq());
            var r2 = await p2.RequestAsync<PlayersMsg>(new RecentReq());
            Check(before.Players.Count == 0 && r1.Players.Select(p => p.Code).OrderBy(c => c)
                .SequenceEqual(new[] { p2.Me.Code, p3.Me.Code }.OrderBy(c => c))
                && r2.Players.Any(p => p.Code == p1.Me.Code) && r2.Players.Any(p => p.Code == p3.Me.Code),
                "recent players: everyone in a started match, both ways (none from a lobby)");
            await p3.RequestAsync<OkMsg>(new BlockReq { Code = p1.Me.Code });
            r1 = await p1.RequestAsync<PlayersMsg>(new RecentReq());
            var r3 = await p3.RequestAsync<PlayersMsg>(new RecentReq());
            Check(r1.Players.Count == 1 && r1.Players[0].Code == p2.Me.Code && r3.Players.Count == 1
                && r3.Players[0].Code == p2.Me.Code, "recent players: a block hides them from each other's list");
            await p1.DisposeAsync();
            await p2.DisposeAsync();
            await p3.DisposeAsync();
        }

        // O5: back into a started game after a socket drop or an app restart: the same relay slot, a new token; an empty
        // game id = "my current started game"
        private static async Task Rejoin(Uri uri)
        {
            OnlineIdentity jid = OnlineIdentity.Create();
            OnlineClient h = await Connect(uri, OnlineIdentity.Create(), "Rehost");
            OnlineClient j = await Connect(uri, jid, "Rejoin");
            GameMsg g = await h.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: true) });
            GameMsg first = await j.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" });
            string? lobby = await ErrorOf(() => j.RequestAsync<GameMsg>(new JoinGameReq { GameId = "", Version = "rom1/sim3" }));
            await h.RequestAsync<OkMsg>(new StartGameReq { Started = true });
            await j.DisposeAsync();
            await Task.Delay(300);
            OnlineClient j2 = await Connect(uri, jid, "Rejoin");
            GameMsg back = await j2.RequestAsync<GameMsg>(new JoinGameReq { GameId = "", Version = "rom1/sim3" });
            GameMsg byId = await j2.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" });
            OnlineClient o = await Connect(uri, OnlineIdentity.Create(), "Outsider");
            string? none = await ErrorOf(() => o.RequestAsync<GameMsg>(new JoinGameReq { GameId = "", Version = "rom1/sim3" }));
            Check(lobby == "not_found" && back.Game.Id == g.Game.Id && back.Relay.Slot == first.Relay.Slot
                && byId.Relay.Slot == first.Relay.Slot && back.Relay.Token != first.Relay.Token && byId.Relay.Token != back.Relay.Token
                && none == "not_found",
                "rejoin: a dropped player gets its started game's slot back (empty id or the id, new token); nobody else does");
            await h.DisposeAsync();
            await j2.DisposeAsync();
            await o.DisposeAsync();
        }

        // O3: parties (invite, join, follow the leader into a game it isn't listed for, hand-over)
        private static async Task Parties(Uri uri)
        {
            OnlineClient lead = await Connect(uri, OnlineIdentity.Create(), "Lead");
            OnlineClient mem = await Connect(uri, OnlineIdentity.Create(), "Member");
            OnlineClient str = await Connect(uri, OnlineIdentity.Create(), "Stranger");
            await Friends(lead, mem);

            Check(await ErrorOf(() => lead.RequestAsync<OkMsg>(new PartyInviteReq { Code = str.Me.Code })) == "not_friend",
                "party: only friends and rivals can be invited");
            var made = Next<PartyEvent>(lead, e => e.Party != null);
            var invite = Next<PartyInviteEvent>(mem);
            var sent = await lead.RequestAsync<OkMsg>(new PartyInviteReq { Code = mem.Me.Code });
            PartyInviteEvent? inv = await invite;
            PartyEvent? madeEv = await made;
            Check(sent.Done && inv?.From.Code == lead.Me.Code && madeEv?.Party?.Id == inv.PartyId
                && madeEv.Party.Leader == lead.Me.Code, "party: the invite arrives, the leader's party exists");
            string partyId = inv?.PartyId ?? "";
            Check(await ErrorOf(() => str.RequestAsync<PartyMsg>(new PartyJoinReq { PartyId = partyId })) == "not_found",
                "party: someone not invited can't join");
            var grew = Next<PartyEvent>(lead, e => e.Party?.Members.Count == 2);
            PartyMsg joined = await mem.RequestAsync<PartyMsg>(new PartyJoinReq { PartyId = partyId });
            Check(joined.Party.Members.Count == 2 && joined.Party.Leader == lead.Me.Code && await grew != null,
                "party: join, the leader hears it");

            // the leader hosts a game nobody else may see: the member is told and gets in
            var follow = Next<PartyGameEvent>(mem);
            GameMsg g = await lead.RequestAsync<GameMsg>(new HostGameReq
            {
                Info = new GameInfo { Mode = "Battle", Arena = "Combat Hall", Friends = false, Rivals = false, Version = "v" },
            });
            Check((await follow)?.GameId == g.Game.Id, "party: the leader hosts, the member gets ev_party_game");
            bool listed = (await mem.RequestAsync<GamesMsg>(new ListGamesReq())).Games.Any(x => x.Id == g.Game.Id);
            bool hidden = !(await str.RequestAsync<GamesMsg>(new ListGamesReq())).Games.Any(x => x.Id == g.Game.Id);
            GameMsg mj = await mem.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "v" });
            Check(listed && hidden && mj.Relay.Slot == 1, "party: the member joins the leader's private game, a stranger can't see it");

            // the leader leaves the party: the member leads
            var handed = Next<PartyEvent>(mem, e => e.Party?.Leader == mem.Me.Code);
            await lead.RequestAsync<OkMsg>(new PartyLeaveReq());
            PartyEvent? h = await handed;
            Check(h?.Party?.Members.Count == 1, "party: the leader leaves, the next member leads");
            var gone = Next<PartyEvent>(mem, e => e.Party == null);
            await mem.RequestAsync<OkMsg>(new PartyLeaveReq());
            Check(await gone != null && await ErrorOf(() => lead.RequestAsync<PartyMsg>(new PartyJoinReq { PartyId = partyId }))
                == "not_found", "party: the last one out ends it");
            await lead.DisposeAsync();
            await mem.DisposeAsync();
            await str.DisposeAsync();
        }

        // decision 10: public games without mods; joiners match the mods; modded games only among friends/rivals
        private static async Task Mods(Uri uri)
        {
            OnlineClient a = await Connect(uri, OnlineIdentity.Create(), "ModA");
            OnlineClient b = await Connect(uri, OnlineIdentity.Create(), "ModB");
            OnlineClient c = await Connect(uri, OnlineIdentity.Create(), "ModC");
            await Friends(a, b);
            await Friends(a, c);
            Check(await ErrorOf(() => a.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: true, mods: "abc") })) == "mods",
                "mods: a public game with mods is refused");
            GameMsg g = await a.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: false, mods: "abc") });
            string? none = await ErrorOf(() => b.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" }));
            GameMsg bj = await b.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3", Mods = "abc" });
            Check(none == "mods" && bj.Relay.Slot == 1, "mods: joiners need the same mods");
            // C is A's friend but a stranger to B
            Check(await ErrorOf(() => c.RequestAsync<GameMsg>(new JoinGameReq
            {
                GameId = g.Game.Id, Version = "rom1/sim3", Mods = "abc",
            })) == "mods", "mods: a modded game refuses someone who isn't everyone's friend or rival");
            await a.DisposeAsync();
            await b.DisposeAsync();
            await c.DisposeAsync();
        }

        private static async Task Relays(Relay relay, RelayInfo hostInfo, RelayInfo aInfo, RelayInfo bInfo)
        {
            var at = new IPEndPoint(IPAddress.Loopback, relay.Port);
            using var host = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var a = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var b = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var stranger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            async Task Send(UdpClient u, RelayInfo info, byte to, string text)
            {
                byte[] packet = new byte[RelayWire.SendHeader + text.Length];
                RelayWire.WriteHeader(packet, RelayWire.ParseToken(info.Token), to);
                System.Text.Encoding.ASCII.GetBytes(text, 0, text.Length, packet, RelayWire.SendHeader);
                await u.SendAsync(packet, packet.Length, at);
            }

            static async Task<byte[]?> Receive(UdpClient u, int ms = 1000)
            {
                using var cts = new CancellationTokenSource(ms);
                try
                {
                    return (await u.ReceiveAsync(cts.Token)).Buffer;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }

            static string Text(byte[]? p) => p == null ? "(none)" : $"{(char)p[0]}{p[1]}:"
                + System.Text.Encoding.ASCII.GetString(p, RelayWire.ReceiveHeader, p.Length - RelayWire.ReceiveHeader);

            // keepalives register each device's address and are answered
            bool answered = true;
            foreach ((UdpClient u, RelayInfo info) in new[] { (host, hostInfo), (a, aInfo), (b, bInfo) })
            {
                await Send(u, info, RelayWire.KeepAlive, "");
                answered &= await Receive(u) is [RelayWire.Magic, RelayWire.KeepAlive];
            }
            Check(answered, "relay answers each member's keepalive");

            await Send(host, hostInfo, RelayWire.ToAll, "snap");
            string ta = Text(await Receive(a)), tb = Text(await Receive(b));
            Check(ta == "R0:snap" && tb == "R0:snap", $"host -> everyone: one packet fanned out ({ta}, {tb})");
            await Send(a, aInfo, 0, "in");
            string th = Text(await Receive(host));
            byte[]? leak = await Receive(b, 300);
            Check(th == "R1:in" && leak == null, $"member -> host only ({th}), the other member gets nothing");

            var fake = new RelayInfo { Token = RelayWire.TokenText(12345) };
            await Send(stranger, fake, RelayWire.KeepAlive, "");
            await Send(stranger, fake, RelayWire.ToAll, "spam");
            Check(await Receive(stranger, 300) == null && await Receive(host, 300) == null,
                "an unknown token gets no answer and reaches nobody");
        }
    }
}
