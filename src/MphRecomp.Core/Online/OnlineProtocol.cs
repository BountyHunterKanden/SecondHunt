using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MphRecomp.Online
{
    // The online protocol (docs/ONLINE_PLAY.md section 13): one JSON object per WebSocket message, "t" = its kind.
    // Requests carry an "id" and are answered by a message with the same id (a reply or an error); messages without an
    // id are the server's pushes (events). The game and SecondHunt.Server compile these same files.
    public static class OnlineProtocol
    {
        // 2 (2026-10-19): the 0.3.6 match netcode (the host's pace byte, hit claims and their verdicts, the new match rules):
        // an older app gets "update the game" at login instead of a match it can't follow
        public const int Version = 2;
        public const int MaxMessage = 64 * 1024;
        public const int MaxLicense = 8 * 1024;

        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowOutOfOrderMetadataProperties = true,
        };

        // what the player signs to log in: a fixed context + the server's random challenge
        public static byte[] LoginPayload(byte[] nonce)
        {
            byte[] context = "secondhunt-login-v1\n"u8.ToArray();
            byte[] payload = new byte[context.Length + nonce.Length];
            context.CopyTo(payload, 0);
            nonce.CopyTo(payload, context.Length);
            return payload;
        }

        public static async Task SendAsync(WebSocket ws, OnlineMsg msg, CancellationToken ct)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(msg, Json);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }

        // the next message, or null when the other side closed
        public static async Task<OnlineMsg?> ReceiveAsync(WebSocket ws, CancellationToken ct)
        {
            byte[] buffer = new byte[4096];
            using var ms = new MemoryStream();
            while (true)
            {
                ValueWebSocketReceiveResult r = await ws.ReceiveAsync(buffer.AsMemory(), ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > MaxMessage)
                {
                    throw new OnlineException("too_big", "message too big");
                }
                if (r.EndOfMessage)
                {
                    break;
                }
            }
            try
            {
                return JsonSerializer.Deserialize<OnlineMsg>(ms.GetBuffer().AsSpan(0, (int)ms.Length), Json)
                    ?? throw new OnlineException("bad_message", "empty message");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new OnlineException("bad_message", ex.Message);
            }
        }
    }

    public sealed class OnlineException : Exception
    {
        public string Code { get; }

        public OnlineException(string code, string text) : base(text)
        {
            Code = code;
        }
    }

    // presence values
    public static class Presence
    {
        public const string Offline = "offline";
        public const string Online = "online"; // in the menus
        public const string Lobby = "lobby"; // in a game's lobby (hosting or joined)
        public const string Match = "match"; // playing
    }

    // link kinds: friends and rivals are mutual; block is one-way
    public static class LinkKind
    {
        public const string Friend = "friend";
        public const string Rival = "rival";
        public const string Block = "block";

        public static bool IsMutual(string? kind) => kind is Friend or Rival;
    }

    // a player as another player sees them: name#tag, friend code, presence, their uploaded license, and the viewer's
    // relation to them (friend or rival, never both from server 0.7.0 / requests / blocked)
    public sealed class PlayerCard
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public int Tag { get; set; }
        public string Presence { get; set; } = Online.Presence.Offline;
        public bool Friend { get; set; }
        public bool Rival { get; set; }
        public string? License { get; set; }
        // the game this friend/rival is in, when the viewer may join it (JOIN in the friends list)
        public string? GameId { get; set; }
        // the relay slot, set on the members of a GameCard (null elsewhere)
        public int? Slot { get; set; }
        // server 0.7.0: the viewer's head to head against this player (null = none yet, or an older server)
        public HeadToHeadInfo? Record { get; set; }

        public override string ToString() => OnlineNames.Display(Name, Tag);
    }

    public sealed class RequestInfo
    {
        public PlayerCard Player { get; set; } = new();
        public string Kind { get; set; } = LinkKind.Friend;
        public bool Incoming { get; set; }
    }

    // a hosted game as the host describes it (vanilla MAKE THIS GAME AVAILABLE TO: FRIENDS / RIVALS, plus PUBLIC)
    public sealed class GameInfo
    {
        public string Mode { get; set; } = "";
        public string Arena { get; set; } = "";
        public int MaxPlayers { get; set; } = 4;
        public bool Friends { get; set; } = true;
        public bool Rivals { get; set; } = true;
        public bool Public { get; set; }
        // what must match to play together (ROM key + game logic version); others see the game but can't join
        public string Version { get; set; } = "";
        // a short line for the list (points, time, teams)
        public string? Rules { get; set; }
        // the mods this game runs with (doc section 8b): "none", or a fingerprint of the set; anything a stranger can be
        // in (PUBLIC, Find Game, someone who isn't everyone's friend/rival) must be "none", and joiners must match it
        public string Mods { get; set; } = NoMods;
        // the host's ADVANCED box extra rules (owner 2026-10-10; server 0.6.0): EXTRA PICKUPS (the Pirate Blaster) and EXTRA
        // CHARACTERS (TROOPER, DARK SAMUS). The list shows a small icon for each one a game bans, and ONLINE SETTINGS'
        // filters keep games that allow them out of the list and of FIND GAME. An older host or server leaves them on
        public bool ExtraPickups { get; set; } = true;
        public bool ExtraCharacters { get; set; } = true;
        // the game's arena is an imported one (Echoes: Multiplayer.ExtraArenas; owner 2026-10-10, N27-N29; server 0.7.0): FIND
        // GAME puts a search into it only when its players all have that arena; the list hides it from a device without it
        public bool ExtraArena { get; set; }
        // a served game (server 0.8.0, docs/DEDICATED_SERVERS.md): its match runs on the server (relay slot 0, no player)
        // and Host on its card is the lobby's owner, a joiner like the rest. In HostGameReq: serve it if you can (the server
        // answers with false when it can't: then this device hosts as before)
        public bool Served { get; set; }

        public const string NoMods = "none";
    }

    // a party as one of its members sees it: the leader (also first in Members) and everyone in it
    public sealed class PartyInfo
    {
        public string Id { get; set; } = "";
        public string Leader { get; set; } = "";
        public List<PlayerCard> Members { get; set; } = new();
    }

    public sealed class GameCard
    {
        public string Id { get; set; } = "";
        public PlayerCard Host { get; set; } = new();
        public GameInfo Info { get; set; } = new();
        public int Players { get; set; }
        public bool Started { get; set; }
        public List<PlayerCard> Members { get; set; } = new();
    }

    // where this device sends its match traffic: the relay's UDP address, its token and its slot (0 = the host)
    public sealed class RelayInfo
    {
        public string Host { get; set; } = ""; // empty = the server's own host name
        public int Port { get; set; }
        public string Token { get; set; } = "";
        public int Slot { get; set; }
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
    // handshake
    [JsonDerivedType(typeof(ChallengeMsg), "challenge")]
    [JsonDerivedType(typeof(HelloMsg), "hello")]
    [JsonDerivedType(typeof(WelcomeMsg), "welcome")]
    // replies
    [JsonDerivedType(typeof(ErrorMsg), "error")]
    [JsonDerivedType(typeof(OkMsg), "ok")]
    [JsonDerivedType(typeof(PlayerMsg), "player")]
    [JsonDerivedType(typeof(PlayersMsg), "players")]
    [JsonDerivedType(typeof(RosterMsg), "roster")]
    [JsonDerivedType(typeof(GameMsg), "game")]
    [JsonDerivedType(typeof(GamesMsg), "games")]
    [JsonDerivedType(typeof(PartyMsg), "party")]
    // requests
    [JsonDerivedType(typeof(SetNameReq), "set_name")]
    [JsonDerivedType(typeof(SetLicenseReq), "set_license")]
    [JsonDerivedType(typeof(SetFindableReq), "set_findable")]
    [JsonDerivedType(typeof(GetPlayerReq), "get_player")]
    [JsonDerivedType(typeof(SearchReq), "search")]
    [JsonDerivedType(typeof(AddReq), "add")]
    [JsonDerivedType(typeof(AnswerReq), "answer")]
    [JsonDerivedType(typeof(RemoveReq), "remove")]
    [JsonDerivedType(typeof(BlockReq), "block")]
    [JsonDerivedType(typeof(RosterReq), "get_roster")]
    [JsonDerivedType(typeof(HostGameReq), "host_game")]
    [JsonDerivedType(typeof(UpdateGameReq), "update_game")]
    [JsonDerivedType(typeof(ListGamesReq), "list_games")]
    [JsonDerivedType(typeof(JoinGameReq), "join_game")]
    [JsonDerivedType(typeof(LeaveGameReq), "leave_game")]
    [JsonDerivedType(typeof(StartGameReq), "start_game")]
    [JsonDerivedType(typeof(InviteReq), "invite")]
    [JsonDerivedType(typeof(RecentReq), "get_recent")]
    [JsonDerivedType(typeof(PartyInviteReq), "party_invite")]
    [JsonDerivedType(typeof(PartyJoinReq), "party_join")]
    [JsonDerivedType(typeof(PartyLeaveReq), "party_leave")]
    [JsonDerivedType(typeof(ReportReq), "report")]
    [JsonDerivedType(typeof(DeleteProfileReq), "delete_profile")]
    [JsonDerivedType(typeof(PingReq), "ping")]
    [JsonDerivedType(typeof(FindStartReq), "find_start")]
    [JsonDerivedType(typeof(FindCancelReq), "find_cancel")]
    [JsonDerivedType(typeof(FindVoteReq), "find_vote")]
    [JsonDerivedType(typeof(TurnCredsReq), "turn_creds")]
    [JsonDerivedType(typeof(TurnCredsMsg), "turn")]
    [JsonDerivedType(typeof(TurnUsageReq), "turn_usage")]
    [JsonDerivedType(typeof(ChatReq), "chat")]
    [JsonDerivedType(typeof(MatchResultReq), "match_result")]
    // events (server pushes)
    [JsonDerivedType(typeof(RequestEvent), "ev_request")]
    [JsonDerivedType(typeof(AddedEvent), "ev_added")]
    [JsonDerivedType(typeof(RemovedEvent), "ev_removed")]
    [JsonDerivedType(typeof(PresenceEvent), "ev_presence")]
    [JsonDerivedType(typeof(KickedEvent), "ev_kicked")]
    [JsonDerivedType(typeof(MemberEvent), "ev_member")]
    [JsonDerivedType(typeof(GameClosedEvent), "ev_game_closed")]
    [JsonDerivedType(typeof(InviteEvent), "ev_invite")]
    [JsonDerivedType(typeof(PartyInviteEvent), "ev_party_invite")]
    [JsonDerivedType(typeof(PartyEvent), "ev_party")]
    [JsonDerivedType(typeof(PartyGameEvent), "ev_party_game")]
    [JsonDerivedType(typeof(FindEvent), "ev_find")]
    [JsonDerivedType(typeof(ChatEvent), "ev_chat")]
    // shared replays (server 0.9.0: ReplayMail.cs)
    [JsonDerivedType(typeof(ReplaySendReq), "replay_send")]
    [JsonDerivedType(typeof(ReplaySendMsg), "replay_upload")]
    [JsonDerivedType(typeof(ReplaysReq), "get_replays")]
    [JsonDerivedType(typeof(ReplaysMsg), "replays")]
    [JsonDerivedType(typeof(ReplayFetchReq), "replay_fetch")]
    [JsonDerivedType(typeof(ReplayTicketMsg), "replay_ticket")]
    [JsonDerivedType(typeof(ReplayDropReq), "replay_drop")]
    [JsonDerivedType(typeof(ReplayBlockReq), "set_replay_block")]
    [JsonDerivedType(typeof(ReplayOfferEvent), "ev_replay")]
    public abstract class OnlineMsg
    {
        public int? Id { get; set; }
    }

    public sealed class ChallengeMsg : OnlineMsg
    {
        public string Nonce { get; set; } = "";
        public string Server { get; set; } = "";
        public int Proto { get; set; }
    }

    public sealed class HelloMsg : OnlineMsg
    {
        public string Key { get; set; } = "";
        public string Sig { get; set; } = "";
        public string App { get; set; } = "";
        public int Proto { get; set; }
        public string Name { get; set; } = "";
        // a scrambled device ID (DeviceTag; "" = none): a ban holds for the device too, not just the key
        public string Device { get; set; } = "";
    }

    public sealed class WelcomeMsg : OnlineMsg
    {
        public PlayerCard Me { get; set; } = new();
        public bool Findable { get; set; }
        // server 0.5.0: online matches still to play before typed chat reaches strangers (0 = unlocked), and the unix time
        // an owner chat mute ends (0 = none)
        public int ChatLeft { get; set; }
        public long ChatMutedUntil { get; set; }
        // server 0.7.0: a started match this player still has a seat in but isn't playing (the app closed or its connection
        // dropped mid-match): the hub offers REJOIN (JoinGameReq with no id) or leaves it (LeaveGameReq); null = none
        public GameCard? Held { get; set; }
    }

    public sealed class ErrorMsg : OnlineMsg
    {
        public string Code { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public sealed class OkMsg : OnlineMsg
    {
        public bool Done { get; set; }
    }

    public sealed class PlayerMsg : OnlineMsg
    {
        public PlayerCard Player { get; set; } = new();
    }

    public sealed class PlayersMsg : OnlineMsg
    {
        public List<PlayerCard> Players { get; set; } = new();
    }

    public sealed class RosterMsg : OnlineMsg
    {
        public List<PlayerCard> Friends { get; set; } = new();
        public List<PlayerCard> Rivals { get; set; } = new();
        public List<RequestInfo> Requests { get; set; } = new();
        public List<PlayerCard> Blocked { get; set; } = new();
    }

    // the license name changed: the reply is the player's own card with its (possibly new) tag
    public sealed class SetNameReq : OnlineMsg
    {
        public string Name { get; set; } = "";
    }

    // the Hunter License as others see it (JSON the game writes and reads; the server only checks size and shape)
    public sealed class SetLicenseReq : OnlineMsg
    {
        public string License { get; set; } = "";
    }

    // "who can find me": true = everyone (name search), false = friend code only
    public sealed class SetFindableReq : OnlineMsg
    {
        public bool Findable { get; set; }
    }

    public sealed class GetPlayerReq : OnlineMsg
    {
        public string Code { get; set; } = "";
    }

    // REPORT from a Hunter License (server 0.2.0+; an older server drops a connection that sends it): a player and why
    // (ReportReason), kept for the owner's review with the name they had then
    public sealed class ReportReq : OnlineMsg
    {
        public string Code { get; set; } = "";
        public string Reason { get; set; } = ReportReason.Name;
    }

    // delete this player (server 0.2.0+): out of any game and party, friends and rivals told (ev_removed), the player, its
    // links (friends, rivals, requests, blocks) and recent players gone; the server closes the connection after the reply.
    // Reports stay (the owner's record). The same key logging in again starts a new profile.
    public sealed class DeleteProfileReq : OnlineMsg
    {
        // the first server that takes it
        public const string SinceServer = "0.2.0";
    }

    public static class ReportReason
    {
        public const string Name = "name"; // an offensive name
        public const string Cheating = "cheating";
        public const string Other = "other";
        public const string Chat = "chat"; // typed chat (server 0.5.0): the server attaches what the player said
        public const string Voice = "voice"; // voice chat (Voice O8): voice is never recorded, so it is the reporter's word
        public static bool Valid(string? reason) => reason is Name or Cheating or Other or Chat or Voice;
        // the first server that takes REPORT
        public const string SinceServer = "0.2.0";
    }

    // a name ("kanden"), a name with its tag ("kanden#0421") or a friend code
    public sealed class SearchReq : OnlineMsg
    {
        public string Query { get; set; } = "";
    }

    // a friend or rival request; Done = it completed at once (the other player had already asked, as vanilla's
    // both-enter-the-code)
    public sealed class AddReq : OnlineMsg
    {
        public string Code { get; set; } = "";
        public string Kind { get; set; } = LinkKind.Friend;
    }

    public sealed class AnswerReq : OnlineMsg
    {
        public string Code { get; set; } = "";
        public string Kind { get; set; } = LinkKind.Friend;
        public bool Accept { get; set; }
    }

    // un-friend / un-rival, or take back a request you sent
    public sealed class RemoveReq : OnlineMsg
    {
        public string Code { get; set; } = "";
        public string Kind { get; set; } = LinkKind.Friend;
    }

    public sealed class BlockReq : OnlineMsg
    {
        public string Code { get; set; } = "";
        public bool Block { get; set; } = true;
    }

    public sealed class RosterReq : OnlineMsg
    {
    }

    public sealed class RequestEvent : OnlineMsg
    {
        public PlayerCard From { get; set; } = new();
        public string Kind { get; set; } = LinkKind.Friend;
    }

    public sealed class AddedEvent : OnlineMsg
    {
        public PlayerCard Player { get; set; } = new();
        public string Kind { get; set; } = LinkKind.Friend;
    }

    public sealed class RemovedEvent : OnlineMsg
    {
        public string Code { get; set; } = "";
        public string Kind { get; set; } = LinkKind.Friend;
    }

    public sealed class PresenceEvent : OnlineMsg
    {
        public string Code { get; set; } = "";
        public string Presence { get; set; } = Online.Presence.Offline;
        // the game they're in, when this player may join it (as PlayerCard.GameId)
        public string? GameId { get; set; }
    }

    // this connection is closing (e.g. the same player logged in on another device)
    public sealed class KickedEvent : OnlineMsg
    {
        public string Reason { get; set; } = "";
    }

    public sealed class GameMsg : OnlineMsg
    {
        public GameCard Game { get; set; } = new();
        public RelayInfo Relay { get; set; } = new();
    }

    public sealed class GamesMsg : OnlineMsg
    {
        public List<GameCard> Games { get; set; } = new();
    }

    // open a lobby: the reply is the game and this device's relay slot 0
    public sealed class HostGameReq : OnlineMsg
    {
        public GameInfo Info { get; set; } = new();
        // Find Game (server 0.3.0): the match (FindEvent.FindId) this game is the host's half of; the server then fixes the
        // mode, arena, seats and who may join, and tells the other players where to join. Optional: while a match waits for its
        // chosen host, that player's host_game is the match's game anyway (so clients just host as usual)
        public string? Find { get; set; }
    }

    // host: change the game's settings; the reply is a GameMsg with the new card (its Relay left empty)
    public sealed class UpdateGameReq : OnlineMsg
    {
        public GameInfo Info { get; set; } = new();
    }

    // the games this player may see: public ones, friends'/rivals' when the host allows it, ones they're invited to
    public sealed class ListGamesReq : OnlineMsg
    {
    }

    public sealed class JoinGameReq : OnlineMsg
    {
        public string GameId { get; set; } = "";
        public string Version { get; set; } = "";
        // must equal the game's GameInfo.Mods
        public string Mods { get; set; } = GameInfo.NoMods;
        // this app can play in a served game (server 0.8.0): an older one gets "update the game" instead
        public bool Served { get; set; }
    }

    public sealed class LeaveGameReq : OnlineMsg
    {
    }

    // host: the match started (true) or everyone is back in the lobby (false); drives presence and the list
    public sealed class StartGameReq : OnlineMsg
    {
        public bool Started { get; set; } = true;
    }

    // invite a friend or rival into your game (they may join even if it isn't listed for them); Done = they're online
    public sealed class InviteReq : OnlineMsg
    {
        public string Code { get; set; } = "";
    }

    // someone joined (Joined) or left your game
    public sealed class MemberEvent : OnlineMsg
    {
        public string GameId { get; set; } = "";
        public PlayerCard Player { get; set; } = new();
        public int Slot { get; set; }
        public bool Joined { get; set; }
        // host migration (O5b, server 0.5.0): the host of a started match left or its connection dropped and this slot hosts
        // the match now (Slot = the old host's, Joined = false); null = no host change
        public int? HostSlot { get; set; }
        // a served game (server 0.8.0): the lobby's owner left and this player owns it now; null = no owner change
        public string? Owner { get; set; }
    }

    public sealed class GameClosedEvent : OnlineMsg
    {
        public string GameId { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public sealed class InviteEvent : OnlineMsg
    {
        public PlayerCard From { get; set; } = new();
        public GameCard Game { get; set; } = new();
    }

    // the players you've been in a started match with, newest first (the server keeps 50; blocked ones left out)
    public sealed class RecentReq : OnlineMsg
    {
    }

    // leader: invite a friend or rival into your party (makes the party if you aren't in one); Done = they're online
    public sealed class PartyInviteReq : OnlineMsg
    {
        public string Code { get; set; } = "";
    }

    // accept a party invite; the reply is the party
    public sealed class PartyJoinReq : OnlineMsg
    {
        public string PartyId { get; set; } = "";
    }

    public sealed class PartyLeaveReq : OnlineMsg
    {
    }

    public sealed class PartyMsg : OnlineMsg
    {
        public PartyInfo Party { get; set; } = new();
    }

    public sealed class PartyInviteEvent : OnlineMsg
    {
        public PlayerCard From { get; set; } = new();
        public string PartyId { get; set; } = "";
    }

    // the party changed (someone joined or left, a new leader); Party null = you're no longer in it
    public sealed class PartyEvent : OnlineMsg
    {
        public PartyInfo? Party { get; set; }
    }

    // your party's leader hosted or joined this game: join it (party members get in even if it isn't listed for them)
    public sealed class PartyGameEvent : OnlineMsg
    {
        public string GameId { get; set; } = "";
    }

    // ---- Find Game (O6, server 0.3.0; docs/ONLINE_PLAY.md section 6) ----

    // the seven modes a search can ask for (GameMode names without the team variants; no modes = any of them)
    public static class FindMode
    {
        public static readonly string[] All = { "Battle", "Bounty", "Capture", "Defender", "Nodes", "PrimeHunter", "Survival" };
        public static bool Valid(string? mode) => Array.IndexOf(All, mode) >= 0;
        // a game's mode without "Teams" (a hosted BattleTeams game is a Battle game to a search)
        public static string Base(string? mode)
            => mode != null && mode.EndsWith("Teams", StringComparison.Ordinal) ? mode[..^5] : mode ?? "";
    }

    // tell the server how this device is doing: its round trip to the server in ms (the match traffic goes through the
    // server's relay, so two players' latency is about the sum of theirs); the reply is an OkMsg. Find Game uses it for REGION
    // and to pick the host. A device sends it every few seconds while it is in the online menus.
    public sealed class PingReq : OnlineMsg
    {
        public int Ms { get; set; }
    }

    // Cloudflare TURN (Netcode O5, decision 18): short-lived credentials for this player's match traffic, when it is in a game
    // (lobby or started); the reply is a TurnCredsMsg. UseRelay = use our own UDP relay instead (no key on the server, the
    // month's TURN cap reached, not in a game, or Cloudflare didn't answer). An older server drops a connection that sends it
    // (clients check OnlineClient.ServerAtLeast(TurnCredsReq.SinceServer)).
    public sealed class TurnCredsReq : OnlineMsg
    {
        public const string SinceServer = "0.4.0";
    }

    public sealed class TurnCredsMsg : OnlineMsg
    {
        public bool UseRelay { get; set; }
        public string Reason { get; set; } = "";
        // "turn:...?transport=udp" only
        public List<string> Urls { get; set; } = new();
        public string Username { get; set; } = "";
        public string Credential { get; set; } = "";
        public int TtlSeconds { get; set; }
    }

    // bytes this device received through TURN since its last report (every ~30 s and at close); the reply is an OkMsg. The
    // server counts at most 200 KB/s since the player's last report (or its credentials) toward the month's cap.
    public sealed class TurnUsageReq : OnlineMsg
    {
        public long Bytes { get; set; }
    }

    // put this player (their whole party, when they lead one) in the Find Game queue; the reply is an OkMsg and the
    // progress comes as FindEvents. Errors: `in_game`, `searching`, `not_leader` (a party member), `party_busy` (someone in the
    // party is in a game), `bad_mode`, `version` (empty). An older server drops a connection that sends it
    // (clients check OnlineClient.ServerAtLeast(FindStartReq.SinceServer)).
    public sealed class FindStartReq : OnlineMsg
    {
        public const string SinceServer = "0.3.0";
        // FindMode names this search accepts; empty = any mode
        public List<string> Modes { get; set; } = new();
        // REGION: true = NEARBY (players with a short route through the server first, widening as the wait goes on),
        // false = WORLDWIDE (anyone, at once)
        public bool Nearby { get; set; } = true;
        // what must match to play together (GameInfo.Version)
        public string Version { get; set; } = "";
        // per mode, the arenas this device can play (names as GameInfo.Arena); a mode with no list = any arena
        public Dictionary<string, List<string>> Arenas { get; set; } = new();
        // this device's round trip to the server just now (as PingReq), 0 = not measured
        public int PingMs { get; set; }
        // ONLINE SETTINGS' filters (owner 2026-10-10; server 0.6.0, an older one ignores them): never into a game that allows
        // EXTRA PICKUPS / EXTRA CHARACTERS; a match found for this search bans them (FindEvent.NoExtraPickups / ...)
        public bool NoExtraPickups { get; set; }
        public bool NoExtraCharacters { get; set; }
    }

    public sealed class FindCancelReq : OnlineMsg
    {
    }

    // VOTE FOR AN ARENA: one of the FindEvent vote's Arenas (error `bad_arena`, `no_vote`); changing your vote is fine
    public sealed class FindVoteReq : OnlineMsg
    {
        public string Arena { get; set; } = "";
    }

    public static class FindState
    {
        public const string Searching = "searching"; // in the queue (repeated every couple of seconds)
        public const string Vote = "vote"; // a match was found: vote for an arena
        public const string Host = "host"; // vote over, you host: a normal HostGameReq (with the match's mode + arena) opens the match's game
        public const string Join = "join"; // vote over, someone else hosts: a "game" event follows
        public const string Game = "game"; // JoinGameReq {GameId}
        public const string Cancelled = "cancelled"; // the search ended (Reason: you, your party, the host didn't open the game, ...)
    }

    // the search, as every member of the searching party sees it
    public sealed class FindEvent : OnlineMsg
    {
        public string State { get; set; } = FindState.Searching;
        // searching: seconds waited, players in the queue (this search's included), the route limit now in ms (0 = none: WORLDWIDE
        // or widened all the way), and whether the search is NEARBY
        public int Waited { get; set; }
        public int Queued { get; set; }
        public int LimitMs { get; set; }
        public bool Nearby { get; set; }
        // vote / host / join
        public string FindId { get; set; } = "";
        public string Mode { get; set; } = "";
        public List<PlayerCard> Players { get; set; } = new();
        // vote: the arenas to pick from, seconds to vote, how many have voted; host / join: the arena it will be
        public List<string> Arenas { get; set; } = new();
        public int Seconds { get; set; }
        public int Votes { get; set; }
        public string Arena { get; set; } = "";
        // host: the extra rules the match bans (a searcher's ONLINE SETTINGS filter: FindStartReq.NoExtraPickups / ...)
        public bool NoExtraPickups { get; set; }
        public bool NoExtraCharacters { get; set; }
        // host / join: who hosts; game: the game to join (the one JoinGameReq takes)
        public PlayerCard? HostCard { get; set; }
        public string GameId { get; set; } = "";
        // cancelled: why
        public string Reason { get; set; } = "";
    }

    // Social O7 (server 0.5.0): say something to everyone in the game you are in (its lobby and its match). Either a quick
    // message (Preset: an index into ChatPresets.All) or typed Text (at most MaxChat characters, masked by the word filter).
    // Errors: no_game, bad_text, chat_rate (too fast), chat_locked (new identity: the text says how many matches to go),
    // chat_muted (owner chat mute), unknown (a server older than 0.5.0 drops the connection: gate on ServerAtLeast).
    public sealed class ChatReq : OnlineMsg
    {
        public const int MaxChat = 80;
        public const string SinceServer = "0.5.0";
        public int Preset { get; set; } = -1;
        public string? Text { get; set; }
    }

    // a chat line, pushed to every member of the game who may see it (the sender included, so everyone shows the same
    // masked text). Preset >= 0 = a quick message (Text left empty). Text arrives already masked.
    public sealed class ChatEvent : OnlineMsg
    {
        public string GameId { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = ""; // NAME#0421
        public int Preset { get; set; } = -1;
        public string Text { get; set; } = "";
    }
}
