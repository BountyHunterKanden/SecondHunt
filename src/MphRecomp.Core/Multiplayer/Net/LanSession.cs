using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;

namespace MphRecomp.Multiplayer.Net
{
    // A match between devices on one Wi-Fi network (plain UDP; works the same over Wi-Fi Direct or a hotspot).
    //   host: Host(template, romKey, name) beacons on the network every second while a seat is open and takes up to
    //         three JOINs; a joiner with the same ROM revision gets the next seat after the host (in the order they came;
    //         the last bots give way) and the match settings (WELCOME), then the match starts on all of them.
    //   client: Browse(romKey) lists hosts from their beacons (or JoinAddress for a typed IP); Join(host, hunter).
    // In the match the host sends a Snapshot every frame and takes the newest NetInput; the client the reverse.
    // The vanilla multiplayer menu (Core Frontend/MultiplayerMenu) keeps a joiner in its lobby after the WELCOME: the
    // host's START GAME sends START with the final match (SendStart), and the joiner starts on it (Started; the host's
    // first snapshot counts too). A BYE before the match tells the menu the other device left (PeerLeft).
    // Receiving runs on a background thread; the game thread polls TryTakeInput / TryTakeSnapshot.
    // With more than one joiner the host's START (SendStart) gives every joiner its final seat: the WELCOMEs' seats move
    // as others join. MatchNetDriver drives it as an IMatchNet (as the online NetMatchSession), one remote seat per joiner.
    public sealed class LanSession : IDisposable, IMatchNet
    {
        public const int GamePort = 47770;
        public const int BeaconPort = 47771;
        private const byte Protocol = 24; // 2: snapshots carry the last hit (kill messages); 3: items; 4: START;
                                         // 5: JOIN carries the game logic version (Replay ReplayFile.CurrentSimVersion);
                                         // 6: JOIN and the players carry the Hunter License rank level;
                                         // 7: JOIN and the players carry the character + skin (MatchPlayer.Character/Skin);
                                         // 8: snapshots carry the mode's state (NetObjective: octoliths, nodes, timers)
                                         // 9: the match carries Abilities, snapshots the Trooper's cling + drone (SeatAbilities)
                                         // 10: the match carries PirateBlaster (the Trooper's affinity weapon); the
                                         //     Weapons bits carry bit 11 (the Pirate Blaster)
                                         // 11 (P8): up to 8 seats (SeatLimit; the beacon carries it)
                                         // 12: the match carries ExtraCharacters (the EXTRA CHARACTERS rule); the beacon
                                         //     carries both extra rules (LanHostInfo.ExtraPickups / ExtraCharacters)
                                         // 13: inputs carry the touch controls' stylus (NetInput.TouchDx/Dy/Touching, T11)
                                         // 14: snapshots carry the detail stats (Snapshot.Stats: RESULTS efficiency,
                                         //     kills by weapon, headshots... as the host counted them, release 0.2.0 F1)
                                         // 15: no wire change; the arenas carry the baked pickups (Arenas/PlacedPickups.cs)
                                         // 16: the match carries Crates + Breakables; snapshots carry the Echoes crates +
                                         //     breakables (Snapshot.World, Net/NetWorld.cs; 0.2.0 owner 2026-10-11)
                                         // 17: SNAPSHOT starts with the joiner's input ack (u16, 0xFFFF none) + frames held
                                         //     on it (u8): LAN guests predict + reconcile like online ones (0.2.0 owner
                                         //     2026-10-11: rubber banding on the Odin as a LAN guest over Wi-Fi)
                                         // 18: Dark Samus's ball hop + Hyper Ball (sim); SeatAbilities carry the Hyper
                                         //     Ball (2 bytes a seat; docs/DARK_SAMUS_HYPER.md)
                                         // 19: Zero Suit's Paralyzer + 90 health (sim); SeatAbilities carry each seat's
                                         //     Paralyzer stun (1 byte a seat; docs/ZERO_SUIT_SAMUS.md section 7)
                                         // 20: the Trooper's Squad Mark; SeatAbilities carry each seat's marks (1 byte a
                                         //     seat: the marking seats, PlayerEntity.NetSquadMark)
                                         // 21: Shooting Gallery's turret on; snapshots carry it (NetWorld.Turret, 14 bytes)
                                         // 22: no wire change; 0.3.6's rules (one wall cling per airtime, Pirate Blaster
                                         //     10 / 14, the drone fixes, body splash) so a 0.3.5 app can't join a 0.3.6 game
                                         // 23: JOIN and the players carry the seat's colour pick after the look
                                         //     (MatchPlayer.ColourPick, sbyte, -1 = auto; LookAlikes.CopyIndices)
                                         // 24: LOOK, a joiner's colour pick changed in the lobby (sbyte; SendLook)
        private const byte MsgBeacon = 1, MsgJoin = 2, MsgWelcome = 3, MsgRefuse = 4, MsgInput = 5, MsgSnapshot = 6, MsgBye = 7,
            MsgStart = 8, MsgLobby = 9, MsgLook = 10;
        // SNAPSHOT: type, the joiner's input ack (u16), frames held on it (u8), then the snapshot (protocol 17)
        private const int SnapshotHeader = 4;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MPHR");

        // the session an activity hands to the next (lobby in the launcher -> match in CampaignActivity)
        public static LanSession? Current { get; set; }

        public bool IsHost { get; }
        public string RomKey { get; }
        public MatchSettings? Settings { get; private set; } // host: the full match; client: MatchClient.ForSeat
        public int RemoteSeat => FirstJoiner?.Seat ?? 1; // host: the first joiner's seat (one-joiner callers)
        public string Status { get; private set; } = "";
        public event Action? Joined; // raised on the receive thread (host: each new joiner; client: the WELCOME)
        public event Action<string>? StatusChanged;
        // client: the host started the match (START, or its first snapshot)
        public bool Started { get; private set; }
        // client: the host's lobby as the host has it (bots as bots), LocalSeat = this player's seat; LobbyVersion counts
        // each WELCOME / LOBBY that brought it (the joiner's lobby redraws on a new one)
        public MatchSettings? LobbySettings { get; private set; }
        public int LobbyVersion => _lobbyVersion;
        private int _lobbyVersion;
        // client: the host said BYE; host: every joiner did (each one: Joiners)
        public bool PeerLeft { get; private set; }
        // the other device (host: the first joiner still in; joiner: the host)
        public IPEndPoint? Peer => IsHost ? FirstJoiner?.EndPoint : _peer;

        // host: up to 8 seats (P8, MatchSettings.MaxSeats), the host's own and up to seven joiners
        public const int MaxJoiners = MatchSettings.MaxSeats - 1;

        // host: how many seats this match has, the host's included (the game's 4, up to 8: P8); joiners past it are refused and
        // the template's last bots give way to joiners. A template with more seats than this raises it
        public int SeatLimit
        {
            get => Math.Clamp(Math.Max(_seatLimit, _template?.Players.Count ?? 0), PlayerEntity.VanillaMaxPlayers, MatchSettings.MaxSeats);
            set => _seatLimit = value;
        }
        private int _seatLimit = PlayerEntity.VanillaMaxPlayers;

        // the revision both sides compare (JOIN): US rev 0 and rev 1 share every file a match uses (docs/ROM_VERSIONS.md
        // 3e), so they count as one (as MatchLobby.LanRomKey)
        public static string RomKeyFor(string mphKey) => mphKey is Ver.AMHE0 or Ver.AMHE1 ? "MPH USA" : mphKey;

        private readonly UdpClient _socket;
        private UdpClient? _beaconSocket;
        private IPEndPoint? _peer; // client: the host
        private readonly Thread _receiver;
        private Thread? _beaconThread;
        private volatile bool _running = true;
        private long _lastHeard = Environment.TickCount64; // client: the host

        // host
        private sealed class Joiner
        {
            public int Id;
            public IPEndPoint EndPoint = null!;
            public MatchPlayer Player = new();
            public int Seat = -1;
            public long LastHeard = Environment.TickCount64;
            public NetInput Latest;
            public bool HasInput;
            public int LastSeq = -1;
            // the input the match last took (its sequence; -1 none yet) and the frames it ran on it since (protocol 17)
            public int Applied = -1;
            public int Held;
            // the inputs not applied yet by sequence (unwrapped) and the newest seen: Wi-Fi delivers them in bunches, and
            // taking only the newest skipped the rest (the guest's prediction then pulled it back)
            public readonly SortedDictionary<int, NetInput> Queue = new();
            public int NewestSeq = -1;
            public int Inputs;
            public bool Left;
            public byte[]? Start; // its START (sent again if it asks again)
            public byte[]? Lobby; // the last LOBBY it was sent (SendLobby: again only when it changes, or every LobbyResendMs)
            public long LobbyAt;
            public HashSet<string>? Arenas; // its extra arenas (ExtraArenas: JOIN's last field; null = an older joiner, none)
        }

        private MatchSettings? _template;
        private string _name = "";
        private readonly object _inputLock = new(); // the joiners and their inputs
        private readonly List<Joiner> _joiners = new();
        private int _nextJoinerId = 1;
        private bool _hostStarted;
        private int[] _remoteSeats = Array.Empty<int>();
        private int _inputsReceived;
        // client
        private readonly ConcurrentDictionary<string, LanHostInfo> _hosts = new();
        private readonly object _snapLock = new();
        private Snapshot? _latestSnap;
        private uint _lastSnapTick;
        private int _snapsReceived;
        private readonly byte[] _sendBuffer = new byte[4096];

        public int InputsReceived => _inputsReceived;
        public int SnapshotsReceived => _snapsReceived;
        public bool PeerAlive => PeerSilentMs < 3000;
        // milliseconds since the other device was last heard (host: the joiner heard last; a BYE makes it huge)
        public long PeerSilentMs
        {
            get
            {
                if (!IsHost)
                {
                    return _peer == null ? Int64.MaxValue / 2 : Environment.TickCount64 - _lastHeard;
                }
                lock (_inputLock)
                {
                    long last = _joiners.Where(j => !j.Left).Select(j => j.LastHeard).DefaultIfEmpty(0).Max();
                    return Environment.TickCount64 - last;
                }
            }
        }
        public IReadOnlyList<LanHostInfo> Hosts => _hosts.Values.Where(h => Environment.TickCount64 - h.LastSeen < 4000)
            .OrderBy(h => h.Name).ToList();

        // host: everyone who joined, in the order they came (the menu's lobby seats them); Left = said BYE
        public IReadOnlyList<LanJoinerInfo> Joiners
        {
            get
            {
                lock (_inputLock)
                {
                    return _joiners.Select(j => new LanJoinerInfo(j.Id, j.Player, j.EndPoint, j.Left)).ToList();
                }
            }
        }

        // host: a JOIN's arena check (the menu's: the lobby's arena, RANDOM aside); null = the template's arena. Returns why the
        // joiner (its extra arenas, null = none) can't play, or null
        public Func<IReadOnlyCollection<string>?, string?>? ArenaCheck { get; set; }

        // host: the joiners still here who don't have this arena (their nicknames; empty = everyone can play it)
        public List<string> Lacking(string arena)
        {
            lock (_inputLock)
            {
                return _joiners.Where(j => !j.Left && !ExtraArenas.Has(j.Arenas, arena)).Select(j => j.Player.Nickname ?? "a player").ToList();
            }
        }

        // host: does every joiner still here have this arena (RANDOM's picks)
        public bool AllHave(string arena) => Lacking(arena).Count == 0;

        private Joiner? FirstJoiner
        {
            get
            {
                lock (_inputLock)
                {
                    return _joiners.FirstOrDefault(j => !j.Left);
                }
            }
        }

        // (under _inputLock)
        private Joiner? BySeat(int seat) => _joiners.FirstOrDefault(j => !j.Left && j.Seat == seat);
        private Joiner? ByEndPoint(IPEndPoint from) => _joiners.FirstOrDefault(j => !j.Left && j.EndPoint.Equals(from));
        // (under _inputLock) a joiner of the running match gone quiet: its seat waits for it (LAN rejoin)
        private const long HeldAfterMs = 3000;
        private bool IsHeld(Joiner j) => Environment.TickCount64 - j.LastHeard > HeldAfterMs;
        private List<string> HeldNames() => _hostStarted
            ? _joiners.Where(j => !j.Left && j.Start != null && IsHeld(j)).Select(j => j.Player.Nickname).ToList() : new List<string>();
        // host: joiners that came back to the running match
        public int Rejoins { get; private set; }
        private int Humans => _template!.Players.Count(p => !p.Bot && !p.Remote);
        private int OpenSeats => Math.Min(SeatLimit - 1, SeatLimit - Humans) - _joiners.Count(j => !j.Left);

        // IMatchNet: one remote seat per joiner
        string IMatchNet.Kind => "LAN";
        IReadOnlyList<int> IMatchNet.RemoteSeats => _remoteSeats;
        bool IMatchNet.TryTakeInput(int seat, out NetInput input)
        {
            lock (_inputLock)
            {
                return TakeInput(BySeat(seat), out input);
            }
        }
        int IMatchNet.InputsFrom(int seat)
        {
            lock (_inputLock)
            {
                return _joiners.Where(j => j.Seat == seat).Sum(j => j.Inputs);
            }
        }
        long IMatchNet.SilentMs(int seat)
        {
            if (!IsHost)
            {
                return PeerSilentMs;
            }
            lock (_inputLock)
            {
                Joiner? j = BySeat(seat);
                return j == null ? Int64.MaxValue / 2 : Environment.TickCount64 - j.LastHeard;
            }
        }

        private LanSession(bool host, string romKey, int port)
        {
            IsHost = host;
            RomKey = romKey;
            _socket = new UdpClient(AddressFamily.InterNetwork);
            // never shared (no ReuseAddress): a second host on GamePort on this device (an older lobby's session still
            // closing, another app, a test) took the JOINs sent there and the joiner waited in a lobby nobody saw (release
            // 0.2.0, -lanagaintest STALE HOST). Taken = Host falls back to any port; its beacons carry it
            if (OperatingSystem.IsWindows())
            {
                _socket.ExclusiveAddressUse = true;
            }
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _socket.EnableBroadcast = true;
            _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "MphLanReceive" };
            _receiver.Start();
        }

        // ---------------------------------------------------------------- host

        // template: the match as set up on this device (seat 0 = this player, then bots); joiners are seated after it in
        // the order they come (the last bots give way when all seats are taken: seatLimit, 4 up to 8).
        // port: GamePort; 0 = any free port (tests: a test host never shares GamePort with a real one on this PC, whose
        // packets it could take: the socket reuses its address)
        public static LanSession Host(MatchSettings template, string romKey, string name, int seatLimit = PlayerEntity.VanillaMaxPlayers,
            int port = GamePort)
        {
            LanSession s;
            try
            {
                s = new LanSession(host: true, romKey, port);
            }
            catch (SocketException)
            {
                s = new LanSession(host: true, romKey, 0); // the fixed port is taken: any port (beacons carry it)
            }
            s._template = template;
            s._seatLimit = seatLimit;
            s._name = name;
            s._beaconSocket = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            s._beaconThread = new Thread(s.BeaconLoop) { IsBackground = true, Name = "MphLanBeacon" };
            s._beaconThread.Start();
            s.Settings = s.Build();
            s.SetStatus($"hosting \"{name}\" on {LocalAddress()}:{s.Port} -- waiting for players");
            return s;
        }

        public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

        private void BeaconLoop()
        {
            var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, BeaconPort) };
            targets.AddRange(DirectedBroadcasts().Select(a => new IPEndPoint(a, BeaconPort)));
            while (_running)
            {
                bool open;
                lock (_inputLock)
                {
                    // (and during a match while a joiner's seat is held for it: LAN rejoin, release test T1)
                    open = !_hostStarted && OpenSeats > 0 || _hostStarted && HeldNames().Count > 0;
                }
                if (!open)
                {
                    Thread.Sleep(250);
                    continue;
                }
                byte[] beacon = Beacon();
                foreach (IPEndPoint t in targets)
                {
                    try
                    {
                        _beaconSocket!.Send(beacon, beacon.Length, t);
                    }
                    catch (SocketException)
                    {
                    }
                }
                Thread.Sleep(1000);
            }
        }

        private byte[] Beacon()
        {
            MatchSettings template;
            lock (_inputLock)
            {
                template = _template!;
            }
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgBeacon);
            w.Write((ushort)Port);
            w.Write(_name);
            w.Write(RomKey);
            w.Write((byte)template.Mode);
            w.Write(template.Arena);
            w.Write((byte)template.Players.Count);
            w.Write((byte)SeatLimit); // P8 (protocol 11)
            // the extra rules (protocol 12): bit 0 = EXTRA PICKUPS, bit 1 = EXTRA CHARACTERS (a joiner's SELECT HUNTER greys
            // the characters a host bans)
            // bit 2 (release test T1): a match running with held seats (joiners gone quiet), then their names: only they see
            // it in the list, as a game to rejoin
            List<string> held;
            bool running;
            lock (_inputLock)
            {
                running = _hostStarted;
                held = running ? HeldNames() : new List<string>();
            }
            w.Write((byte)((template.PirateBlaster != false ? 1 : 0) | (template.PirateBlaster != false && template.ExtraCharacters != false ? 2 : 0)
                | (running ? 4 : 0)));
            if (running)
            {
                w.Write((byte)held.Count);
                foreach (string name in held)
                {
                    w.Write(name);
                }
            }
            return ms.ToArray();
        }

        private void OnJoin(BinaryReader r, IPEndPoint from)
        {
            string romKey = r.ReadString();
            var hunter = (Hunter)r.ReadByte();
            string nickname = r.ReadString();
            int simVersion = r.ReadInt32();
            int rankLevel = r.ReadSByte();
            (string? character, string? skin, int colourPick) = ReadLook(r);
            HashSet<string>? arenas = ExtraArenas.Read(r);
            Joiner? known;
            lock (_inputLock)
            {
                known = ByEndPoint(from);
                if (known != null)
                {
                    known.LastHeard = Environment.TickCount64;
                }
            }
            if (known == null)
            {
                // a held seat's player asking again from wherever it is now (its Wi-Fi came back, or the app restarted: a
                // new address or port): the same seat (release test T1)
                lock (_inputLock)
                {
                    known = _hostStarted && romKey == RomKey && simVersion == MphRecomp.Replay.ReplayFile.CurrentSimVersion
                        ? _joiners.FirstOrDefault(j => !j.Left && j.Start != null && IsHeld(j)
                            && String.Equals(j.Player.Nickname, nickname, StringComparison.OrdinalIgnoreCase))
                        : null;
                    if (known != null)
                    {
                        known.EndPoint = from;
                    }
                }
                if (known != null)
                {
                    SetStatus($"{nickname} rejoined from {from.Address}");
                }
            }
            if (known != null)
            {
                lock (_inputLock)
                {
                    if (known.Start != null && Environment.TickCount64 - known.LastHeard > 1000)
                    {
                        // back after a gap (rejoin): its inputs count again from its own first one
                        known.LastSeq = -1;
                        known.HasInput = false;
                        known.Applied = -1;
                        known.Held = 0;
                        known.Queue.Clear();
                        known.NewestSeq = -1;
                        Rejoins++;
                    }
                    known.LastHeard = Environment.TickCount64;
                }
                // asked again (a lost WELCOME or START, or a rejoin: a new session needs the WELCOME before its START): the
                // same answer
                if (known.Start != null)
                {
                    Send(Welcome(known), from);
                }
                Send(known.Start ?? Welcome(known), from);
                return;
            }
            bool full;
            lock (_inputLock)
            {
                full = _hostStarted || OpenSeats <= 0;
            }
            if (full)
            {
                Send(Refuse(_hostStarted ? "this match has started" : "this match is already full"), from);
                return;
            }
            if (romKey != RomKey)
            {
                Send(Refuse($"ROM revision differs (host {RomKey}, you {romKey}): both players need the same one"), from);
                SetStatus($"refused {from.Address}: ROM {romKey} vs {RomKey}");
                return;
            }
            // builds whose matches play differently can't play together (and the host's replay wouldn't play on the
            // guest's: docs/REPLAY_MODE.md R6)
            if (simVersion != MphRecomp.Replay.ReplayFile.CurrentSimVersion)
            {
                Send(Refuse($"game version differs (host plays version {MphRecomp.Replay.ReplayFile.CurrentSimVersion}, "
                    + $"you {simVersion}): update both devices"), from);
                SetStatus($"refused {from.Address}: game version {simVersion} vs {MphRecomp.Replay.ReplayFile.CurrentSimVersion}");
                return;
            }
            // an arena this joiner lacks (owner 2026-10-10, N27: it would fail to load it and the match start without it)
            string? lacks = ArenaCheck != null ? ArenaCheck(arenas) : ExtraArenas.Refusal(arenas, _template!.Arena);
            if (lacks != null)
            {
                Send(Refuse(lacks), from);
                SetStatus($"refused {from.Address}: no arena {_template!.Arena}");
                return;
            }
            Joiner joiner;
            lock (_inputLock)
            {
                joiner = new Joiner
                {
                    Id = _nextJoinerId++,
                    EndPoint = from,
                    Player = new MatchPlayer
                    {
                        Hunter = hunter, Remote = true, Nickname = nickname, RankLevel = rankLevel, Character = character, Skin = skin,
                        ColourPick = colourPick
                    },
                    Arenas = arenas,
                };
                _joiners.Add(joiner);
                Settings = Build();
            }
            Send(Welcome(joiner), from);
            SetStatus($"{nickname} ({hunter}) joined from {from.Address}");
            Joined?.Invoke();
        }

        private byte[] Welcome(Joiner joiner)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgWelcome);
            lock (_inputLock)
            {
                w.Write((byte)joiner.Seat);
                WriteSettings(w, Settings!);
            }
            return ms.ToArray();
        }

        // the match: this player, the joiners in the order they came, then the template's bots, cut to the seats MphRead
        // has (bots give way). (under _inputLock)
        private MatchSettings Build()
        {
            MatchSettings t = _template!;
            var s = new MatchSettings
            {
                Mode = t.Mode, Arena = t.Arena, PointGoal = t.PointGoal, TimeLimitSeconds = t.TimeLimitSeconds,
                TimeGoalSeconds = t.TimeGoalSeconds, DamageLevel = t.DamageLevel, FriendlyFire = t.FriendlyFire,
                OctolithReset = t.OctolithReset, RadarPlayers = t.RadarPlayers, AffinityWeapons = t.AffinityWeapons,
                Crates = t.Crates, Breakables = t.Breakables,
                Abilities = true, // the Trooper's Wall Cling + Scout Drone (owner 2026-10-09)
                PirateBlaster = t.PirateBlaster, ExtraCharacters = t.ExtraCharacters
            };
            List<MatchPlayer> own = t.Players.Where(p => !p.Remote).ToList();
            s.Players.AddRange(own.Take(1));
            foreach (Joiner j in _joiners.Where(j => !j.Left))
            {
                // the same hunter twice: the next palette, to tell them apart
                j.Player.Recolor = Math.Min(s.Players.Count(p => p.Hunter == j.Player.Hunter)
                    + own.Skip(1).Count(p => p.Hunter == j.Player.Hunter), 3);
                j.Seat = s.Players.Count;
                s.Players.Add(j.Player);
            }
            s.Players.AddRange(own.Skip(1));
            while (s.Players.Count > SeatLimit)
            {
                s.Players.RemoveAt(s.Players.Count - 1);
            }
            _remoteSeats = _joiners.Where(j => !j.Left && j.Seat >= 0).Select(j => j.Seat).ToArray();
            Characters.ApplyExtraRules(s); // (a joiner's TROOPER / DARK SAMUS plays its base hunter when the host bans them)
            return s;
        }

        // host: the lobby took this joiner's seat away: it hears BYE (its lobby says the host left)
        public void Kick(int joinerId)
        {
            IPEndPoint? to = null;
            lock (_inputLock)
            {
                Joiner? j = _joiners.FirstOrDefault(x => x.Id == joinerId && !x.Left);
                if (j != null)
                {
                    j.Left = true;
                    j.LastHeard = 0;
                    to = j.EndPoint;
                    if (!_hostStarted)
                    {
                        Settings = Build();
                    }
                    _remoteSeats = _joiners.Where(x => !x.Left && x.Seat >= 0).Select(x => x.Seat).ToArray();
                }
            }
            if (to != null)
            {
                Send(new[] { MsgBye }, to);
            }
        }

        // host: the lobby's match changed (mode, arena, bots...): the beacons and the next WELCOME carry it
        public void UpdateTemplate(MatchSettings template)
        {
            lock (_inputLock)
            {
                if (_hostStarted)
                {
                    return;
                }
                _template = template;
                Settings = Build();
            }
        }

        // host: the lobby's seats as this player's lobby shows them (full: BuildMatch; seats: joiner id -> seat, as START's):
        // each joiner's lobby shows the host's (bots, seat count, mode, arena) instead of only what its WELCOME carried
        // (Odin host + Samsung guest 2026-10-10: bots added after the join never showed and the lobby never grew). Sent when
        // it changes and every LobbyResendMs (UDP); an older app ignores the message.
        private const long LobbyResendMs = 2000;
        public void SendLobby(MatchSettings full, IReadOnlyDictionary<int, int> seats)
        {
            var sends = new List<(IPEndPoint To, byte[] Lobby)>();
            long now = Environment.TickCount64;
            lock (_inputLock)
            {
                if (_hostStarted)
                {
                    return;
                }
                foreach (Joiner j in _joiners.Where(j => !j.Left))
                {
                    if (!seats.TryGetValue(j.Id, out int seat))
                    {
                        continue;
                    }
                    var ms = new MemoryStream();
                    var w = new BinaryWriter(ms);
                    Header(w, MsgLobby);
                    w.Write((byte)seat);
                    WriteSettings(w, full);
                    byte[] lobby = ms.ToArray();
                    if (j.Lobby != null && j.Lobby.AsSpan().SequenceEqual(lobby) && now - j.LobbyAt < LobbyResendMs)
                    {
                        continue;
                    }
                    j.Lobby = lobby;
                    j.LobbyAt = now;
                    sends.Add((j.EndPoint, lobby));
                }
            }
            foreach ((IPEndPoint to, byte[] lobby) in sends)
            {
                Send(lobby, to);
            }
        }

        // joiner: its colour pick changed in the lobby (the badge's pop-up): the host's seat takes it and the next LOBBY
        // shows it. UDP: the lobby resends it until the host's LOBBY carries the pick (MultiplayerMenu.JoinTick).
        public void SendLook(int colourPick)
        {
            IPEndPoint? to = _peer;
            if (IsHost || to == null || Started)
            {
                return;
            }
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgLook);
            w.Write((sbyte)colourPick);
            Send(ms.ToArray(), to);
        }

        // (protocol: an older app's own number, so it can read the REFUSE: that message is the same in every protocol)
        private byte[] Refuse(string reason, byte protocol = Protocol)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgRefuse, protocol);
            w.Write(reason);
            return ms.ToArray();
        }

        // host: the first joiner's newest input (one-joiner callers; IMatchNet takes it by seat)
        public bool TryTakeInput(out NetInput input)
        {
            lock (_inputLock)
            {
                Joiner? j = _joiners.FirstOrDefault(j => !j.Left);
                input = j?.Latest ?? default;
                if (j == null || !j.HasInput)
                {
                    return false;
                }
                j.HasInput = false;
                j.Queue.Clear();
                j.Applied = j.NewestSeq;
                j.Held = 0;
                return true;
            }
        }

        // more than this many inputs waiting (a burst after a stall): the ones in between are folded into one
        private const int MaxBacklog = 6;

        // buttons whose short press must survive being folded into a later input
        private const CampaignButtons PressButtons = CampaignButtons.Shoot | CampaignButtons.Jump | CampaignButtons.Morph
            | CampaignButtons.Boost | CampaignButtons.AltAttack | CampaignButtons.Zoom | CampaignButtons.ZoomIn | CampaignButtons.ZoomOut | CampaignButtons.NextWeapon
            | CampaignButtons.PrevWeapon | CampaignButtons.AffinitySlot;

        // (under _inputLock) the next input in sequence, like NetMatchSession.TryTakeInput. Nothing new = false: the seat
        // holds its last input this frame (Held counts it).
        private static bool TakeInput(Joiner? j, out NetInput input)
        {
            input = j?.Latest ?? default;
            if (j == null || j.Queue.Count == 0)
            {
                if (j != null && j.Applied >= 0)
                {
                    j.Held++; // (the match runs on the last one again this frame)
                }
                return false;
            }
            int target = j.Queue.Keys.First();
            if (j.NewestSeq - j.Applied > MaxBacklog && j.Applied >= 0)
            {
                target = j.NewestSeq - 1;
            }
            CampaignButtons pressed = CampaignButtons.None;
            foreach (int seq in j.Queue.Keys.ToList())
            {
                if (seq > target)
                {
                    break;
                }
                NetInput q = j.Queue[seq];
                j.Queue.Remove(seq);
                if (seq < target)
                {
                    pressed |= q.Buttons;
                    continue;
                }
                input = q;
            }
            input.Buttons |= pressed & PressButtons;
            j.HasInput = j.Queue.Count > 0;
            j.Applied = target;
            j.Held = 0;
            return true;
        }

        // host: START GAME: the final match replaces the one the WELCOMEs carried, and each joiner gets its seat in it
        // (seats: joiner id -> seat; a joiner left out of it is told BYE). Sent a few times (UDP; a joiner that missed
        // them asks again with a JOIN), and the match's snapshots start it anyway.
        public void SendStart(MatchSettings final, IReadOnlyDictionary<int, int> seats)
        {
            var starts = new List<(IPEndPoint To, byte[] Start)>();
            var byes = new List<IPEndPoint>();
            lock (_inputLock)
            {
                Settings = final;
                _hostStarted = true;
                foreach (Joiner j in _joiners.Where(j => !j.Left))
                {
                    if (!seats.TryGetValue(j.Id, out int seat))
                    {
                        j.Left = true;
                        j.Seat = -1;
                        byes.Add(j.EndPoint);
                        continue;
                    }
                    j.Seat = seat;
                    // the match's "never showed up" grace (MatchNetDriver: 20 s) counts from here: a joiner sends nothing
                    // in the lobby, so its JOIN can be minutes old (Odin host 2026-10-10: ~100 s of lobby, the guest was
                    // dropped at the match's first step and its FIRE / respawns never reached the host)
                    j.LastHeard = Environment.TickCount64;
                    var ms = new MemoryStream();
                    var w = new BinaryWriter(ms);
                    Header(w, MsgStart);
                    w.Write((byte)seat);
                    WriteSettings(w, final);
                    j.Start = ms.ToArray();
                    starts.Add((j.EndPoint, j.Start));
                }
                _remoteSeats = _joiners.Where(j => !j.Left && j.Seat >= 0).Select(j => j.Seat).ToArray();
            }
            foreach (IPEndPoint to in byes)
            {
                Send(new[] { MsgBye }, to);
            }
            for (int i = 0; i < 3; i++)
            {
                foreach ((IPEndPoint to, byte[] start) in starts)
                {
                    Send(start, to);
                }
            }
        }

        // host: START GAME with one joiner (seat 0 this player, the joiner at remoteSeat)
        public void SendStart(MatchSettings final, int remoteSeat)
        {
            Joiner? first = FirstJoiner;
            SendStart(final, first == null ? new Dictionary<int, int>() : new Dictionary<int, int> { [first.Id] = remoteSeat });
        }

        // host: START GAME with the match as the joins built it (Settings)
        public void SendStart()
        {
            MatchSettings final;
            Dictionary<int, int> seats;
            lock (_inputLock)
            {
                final = Settings!;
                seats = _joiners.Where(j => !j.Left).ToDictionary(j => j.Id, j => j.Seat);
            }
            SendStart(final, seats);
        }

        public void SendSnapshot(Snapshot snap)
        {
            (IPEndPoint EndPoint, int Ack, int Held)[] to;
            lock (_inputLock)
            {
                if (!_hostStarted)
                {
                    // (a match started without SendStart: its grace counts from the first snapshot, as SendStart's does)
                    foreach (Joiner j in _joiners.Where(j => !j.Left))
                    {
                        j.LastHeard = Environment.TickCount64;
                    }
                }
                _hostStarted = true; // (a match started without SendStart: no more joins, the seats stay)
                to = _joiners.Where(j => !j.Left && j.Seat >= 0)
                    .Select(j => (j.EndPoint, j.Applied < 0 ? 0xFFFF : j.Applied & 0xFFFF, Math.Min(j.Held, 255))).ToArray();
            }
            if (to.Length == 0)
            {
                return;
            }
            // each joiner's copy starts with its own input ack (protocol 17), the snapshot after it is the same for all
            _sendBuffer[0] = MsgSnapshot;
            int size = SnapshotHeader + snap.Write(_sendBuffer.AsSpan(SnapshotHeader));
            foreach ((IPEndPoint t, int ack, int held) in to)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(_sendBuffer.AsSpan(1), (ushort)ack);
                _sendBuffer[3] = (byte)held;
                Send(_sendBuffer, size, t);
            }
        }

        // ---------------------------------------------------------------- client

        public static LanSession Browse(string romKey)
        {
            var s = new LanSession(host: false, romKey, 0);
            try
            {
                s._beaconSocket = new UdpClient(AddressFamily.InterNetwork);
                s._beaconSocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s._beaconSocket.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
                s._beaconThread = new Thread(s.BeaconListen) { IsBackground = true, Name = "MphLanBeaconListen" };
                s._beaconThread.Start();
                s.SetStatus("looking for matches on this network...");
            }
            catch (SocketException ex)
            {
                s.SetStatus($"can't listen for hosts ({ex.SocketErrorCode}): join by address instead");
            }
            return s;
        }

        private void BeaconListen()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] data = _beaconSocket!.Receive(ref from);
                    var r = new BinaryReader(new MemoryStream(data));
                    if (ReadHeader(r) != MsgBeacon)
                    {
                        continue;
                    }
                    int port = r.ReadUInt16();
                    var info = new LanHostInfo
                    {
                        Address = new IPEndPoint(from.Address, port),
                        Name = r.ReadString(),
                        RomKey = r.ReadString(),
                        Mode = (GameMode)r.ReadByte(),
                        Arena = r.ReadString(),
                        Seats = r.ReadByte(),
                        LastSeen = Environment.TickCount64
                    };
                    info.MaxSeats = r.BaseStream.Position < r.BaseStream.Length ? r.ReadByte() : PlayerEntity.VanillaMaxPlayers;
                    int extras = r.BaseStream.Position < r.BaseStream.Length ? r.ReadByte() : 3;
                    info.ExtraPickups = (extras & 1) != 0;
                    info.ExtraCharacters = (extras & 2) != 0;
                    if ((extras & 4) != 0)
                    {
                        info.Running = true;
                        var held = new string[r.ReadByte()];
                        for (int i = 0; i < held.Length; i++)
                        {
                            held[i] = r.ReadString();
                        }
                        info.Held = held;
                    }
                    _hosts[info.Address.ToString()] = info;
                }
                catch (Exception) when (!_running)
                {
                    return;
                }
                catch (Exception)
                {
                    // a stray packet on the beacon port: ignore
                }
            }
        }

        // asks the host for a seat (retries for a few seconds); true once the match settings arrived. character / skin:
        // this player's MatchPlayer.Character / Skin (null = the hunter's own), shown on every device
        public bool Join(IPEndPoint host, Hunter hunter, string nickname, int timeoutMs = 5000, int rankLevel = -1,
            string? character = null, string? skin = null, int colourPick = -1)
        {
            _peer = host;
            SetStatus($"joining {host}...");
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgJoin);
            w.Write(RomKey);
            w.Write((byte)hunter);
            w.Write(nickname);
            w.Write(MphRecomp.Replay.ReplayFile.CurrentSimVersion);
            w.Write((sbyte)rankLevel);
            WriteLook(w, character, skin, colourPick);
            ExtraArenas.Write(w); // (optional, last)
            byte[] join = ms.ToArray();
            long end = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < end && Settings == null && _running)
            {
                Send(join, host);
                for (int i = 0; i < 25 && Settings == null && _refused == null; i++)
                {
                    Thread.Sleep(10);
                }
                if (_refused != null)
                {
                    SetStatus("refused: " + _refused);
                    _peer = null;
                    return false;
                }
            }
            if (Settings == null)
            {
                SetStatus($"no answer from {host}");
                _peer = null;
                return false;
            }
            SetStatus($"joined {host}: seat {Settings.LocalSeat}");
            return true;
        }

        private volatile string? _refused;
        private bool _startHeard;

        public bool TryTakeSnapshot(out Snapshot snap)
        {
            lock (_snapLock)
            {
                snap = _latestSnap!;
                bool had = _latestSnap != null;
                _latestSnap = null;
                return had;
            }
        }

        // IMatchNet: the replay link's side channel (MatchNetSide.MsgSide, then the payload as is)
        public event Action<int, byte[]>? SideReceived;

        public void SendSide(int seat, ReadOnlySpan<byte> payload)
        {
            IPEndPoint? to;
            if (IsHost)
            {
                lock (_inputLock)
                {
                    to = BySeat(seat)?.EndPoint;
                }
            }
            else
            {
                to = _peer;
            }
            if (to == null)
            {
                return;
            }
            byte[] packet = new byte[1 + payload.Length];
            packet[0] = MatchNetSide.MsgSide;
            payload.CopyTo(packet.AsSpan(1));
            Send(packet, to);
        }

        public void SendInput(in NetInput input)
        {
            if (_peer == null)
            {
                return;
            }
            _sendBuffer[0] = MsgInput;
            input.Write(_sendBuffer.AsSpan(1));
            Send(_sendBuffer, 1 + NetInput.Size, _peer);
        }

        // ---------------------------------------------------------------- both

        private void ReceiveLoop()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try
                {
                    data = _socket.Receive(ref from);
                }
                catch (Exception) when (!_running)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue; // e.g. ICMP port unreachable after the peer left
                }
                if (data.Length == 0)
                {
                    continue;
                }
                try
                {
                    Handle(data, from);
                }
                catch (Exception ex)
                {
                    SetStatus("bad packet: " + ex.Message);
                }
            }
        }

        private void Handle(byte[] data, IPEndPoint from)
        {
            if (data[0] == MatchNetSide.MsgSide)
            {
                int fromSeat = 0;
                if (IsHost)
                {
                    lock (_inputLock)
                    {
                        Joiner? j = ByEndPoint(from);
                        if (j == null)
                        {
                            return;
                        }
                        fromSeat = j.Seat;
                    }
                }
                else if (_peer == null || !_peer.Equals(from))
                {
                    return;
                }
                SideReceived?.Invoke(fromSeat, data[1..]);
                return;
            }
            // the in-match messages are raw (one type byte) to keep them small; the rest carry the MPHR header
            if (data[0] == MsgInput && IsHost)
            {
                NetInput input = NetInput.Read(data.AsSpan(1));
                lock (_inputLock)
                {
                    Joiner? j = ByEndPoint(from);
                    if (j == null)
                    {
                        return;
                    }
                    j.LastHeard = Environment.TickCount64;
                    // queued in sequence (UDP may reorder): the 16-bit sequence unwrapped against the newest seen
                    int seq = j.NewestSeq < 0 ? input.Seq : j.NewestSeq + (short)(input.Seq - (ushort)j.NewestSeq);
                    if (seq > j.Applied && !j.Queue.ContainsKey(seq))
                    {
                        j.Queue[seq] = input;
                        if (seq > j.NewestSeq)
                        {
                            j.NewestSeq = seq;
                            j.LastSeq = input.Seq;
                            j.Latest = input;
                            j.HasInput = true;
                        }
                        // a queue far behind (a long stall): only the newest few matter
                        while (j.Queue.Count > 32)
                        {
                            j.Queue.Remove(j.Queue.Keys.First());
                        }
                    }
                    j.Inputs++;
                }
                Interlocked.Increment(ref _inputsReceived);
                return;
            }
            if (data[0] == MsgSnapshot && !IsHost)
            {
                if (data.Length < SnapshotHeader)
                {
                    return;
                }
                _lastHeard = Environment.TickCount64;
                Started = true;
                Snapshot snap = Snapshot.Read(data.AsSpan(SnapshotHeader));
                int ack = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1));
                snap.InputAck = ack == 0xFFFF ? -1 : ack;
                snap.InputHeld = data[3];
                lock (_snapLock)
                {
                    if (snap.Tick > _lastSnapTick || _lastSnapTick == 0)
                    {
                        _lastSnapTick = snap.Tick;
                        _latestSnap = snap;
                    }
                }
                Interlocked.Increment(ref _snapsReceived);
                return;
            }
            if (data[0] == MsgBye)
            {
                if (IsHost)
                {
                    string? who = null;
                    lock (_inputLock)
                    {
                        Joiner? j = ByEndPoint(from);
                        if (j != null)
                        {
                            j.Left = true;
                            j.LastHeard = 0;
                            who = j.Player.Nickname;
                            if (!_hostStarted)
                            {
                                Settings = Build();
                            }
                            _remoteSeats = _joiners.Where(x => !x.Left && x.Seat >= 0).Select(x => x.Seat).ToArray();
                            PeerLeft = _joiners.All(x => x.Left);
                        }
                    }
                    if (who != null)
                    {
                        SetStatus($"{who} left");
                    }
                }
                else if (_peer != null && _peer.Equals(from))
                {
                    SetStatus("the other player left");
                    _lastHeard = 0;
                    PeerLeft = true;
                }
                return;
            }
            if (IsHost && OlderJoin(data, Magic, Protocol) is byte old)
            {
                Send(Refuse($"this host has a newer version of the game (match protocol {Protocol}, you {old}): update the game", old), from);
                SetStatus($"refused {from.Address}: protocol {old} vs {Protocol}");
                return;
            }
            var r = new BinaryReader(new MemoryStream(data));
            byte type = ReadHeader(r);
            if (type == MsgJoin && IsHost)
            {
                OnJoin(r, from);
            }
            else if (type == MsgWelcome && !IsHost && Settings == null)
            {
                int seat = r.ReadByte();
                MatchSettings host = ReadSettings(r);
                Settings = MatchClient.ForSeat(host, seat);
                SetLobby(host, seat);
                _lastHeard = Environment.TickCount64;
                Joined?.Invoke();
            }
            else if (type == MsgLobby && !IsHost && Settings != null && !_startHeard && !Started)
            {
                // the host's lobby changed (SendLobby): the match this joiner would start on, until START replaces it
                int seat = r.ReadByte();
                MatchSettings host = ReadSettings(r);
                Settings = MatchClient.ForSeat(host, seat);
                SetLobby(host, seat);
                _lastHeard = Environment.TickCount64;
            }
            else if (type == MsgLook && IsHost)
            {
                int colour = r.ReadSByte();
                lock (_inputLock)
                {
                    Joiner? j = ByEndPoint(from);
                    if (j != null && !_hostStarted && j.Player.ColourPick != colour)
                    {
                        j.Player.ColourPick = colour;
                        Settings = Build();
                    }
                }
            }
            else if (type == MsgRefuse && !IsHost)
            {
                _refused = r.ReadString();
            }
            else if (type == MsgStart && !IsHost && Settings != null && !_startHeard)
            {
                _startHeard = true; // (a snapshot may have come first: the final match still counts)
                int seat = r.ReadByte();
                Settings = MatchClient.ForSeat(ReadSettings(r), seat);
                _lastHeard = Environment.TickCount64;
                Started = true;
            }
        }

        private void SetLobby(MatchSettings host, int seat)
        {
            host.LocalSeat = seat;
            host.IsClient = true;
            LobbySettings = host;
            Interlocked.Increment(ref _lobbyVersion);
        }

        private void Send(byte[] data, IPEndPoint to) => Send(data, data.Length, to);

        // test hook (-lantest delay=/jitter=): each send leaves this many ms later, plus 0..jitter (so they also reorder
        // and arrive in bunches, like Wi-Fi). 0 = off (the app never sets them).
        public volatile int TestDelayMs, TestJitterMs;
        private readonly Random _testRandom = new Random(17);

        private void Send(byte[] data, int length, IPEndPoint to)
        {
            if (TestDelayMs > 0 || TestJitterMs > 0)
            {
                int wait;
                lock (_testRandom)
                {
                    wait = TestDelayMs + (TestJitterMs > 0 ? _testRandom.Next(TestJitterMs + 1) : 0);
                }
                byte[] copy = data.AsSpan(0, length).ToArray();
                _ = System.Threading.Tasks.Task.Delay(wait).ContinueWith(_ => SendNow(copy, copy.Length, to));
                return;
            }
            SendNow(data, length, to);
        }

        private void SendNow(byte[] data, int length, IPEndPoint to)
        {
            try
            {
                _socket.Send(data, length, to);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void Header(BinaryWriter w, byte type, byte protocol = Protocol)
        {
            w.Write(type);
            w.Write(Magic);
            w.Write(protocol);
        }

        // a JOIN from an app on an older protocol (its number), else null (NetMatchSession too, with its own magic)
        internal static byte? OlderJoin(ReadOnlySpan<byte> data, byte[] magic, byte protocol) =>
            data.Length >= 6 && data[0] == MsgJoin && data.Slice(1, 4).SequenceEqual(magic) && data[5] < protocol ? data[5] : null;

        private static byte ReadHeader(BinaryReader r)
        {
            byte type = r.ReadByte();
            byte[] magic = r.ReadBytes(4);
            if (!magic.AsSpan().SequenceEqual(Magic))
            {
                throw new InvalidDataException("not an MPH Recomp packet");
            }
            byte protocol = r.ReadByte();
            if (protocol != Protocol)
            {
                throw new InvalidDataException($"protocol {protocol}, this app speaks {Protocol}: update both devices");
            }
            return type;
        }

        // (internal: NetMatchSession sends the match in the same format)
        internal static void WriteSettings(BinaryWriter w, MatchSettings s)
        {
            w.Write((byte)s.Mode);
            w.Write(s.Arena);
            WriteOpt(w, s.PointGoal);
            WriteOpt(w, s.TimeLimitSeconds);
            WriteOpt(w, s.TimeGoalSeconds);
            WriteOpt(w, s.DamageLevel);
            WriteOpt(w, s.FriendlyFire);
            WriteOpt(w, s.OctolithReset);
            WriteOpt(w, s.RadarPlayers);
            WriteOpt(w, s.AffinityWeapons);
            w.Write((byte)s.Players.Count);
            foreach (MatchPlayer p in s.Players)
            {
                w.Write((byte)p.Hunter);
                w.Write((byte)p.Recolor);
                w.Write((sbyte)p.Team);
                w.Write(p.Bot);
                w.Write(p.Remote);
                w.Write((byte)p.BotLevel);
                w.Write(p.Nickname ?? "");
                w.Write((sbyte)p.RankLevel);
                WriteLook(w, p.Character, p.Skin, p.ColourPick);
            }
            WriteOpt(w, s.Abilities); // LAN protocol 9 / online 8
            WriteOpt(w, s.PirateBlaster); // LAN protocol 10 / online 9
            WriteOpt(w, s.ExtraCharacters); // LAN protocol 12 / online 11
            WriteOpt(w, s.Crates); // LAN protocol 16 / online 15 (crates + breakables synced, Net/NetWorld.cs)
            WriteOpt(w, s.Breakables);
        }

        // a seat's character + skin (protocol 7): two strings, "" = none. Only ids travel, never a model (docs/
        // CHARACTERS_AND_SKINS.md "Mixed imports"); a skin this device hasn't imported draws the hunter's own model. Then
        // the seat's colour pick (LAN protocol 23 / online 20): sbyte, -1 = auto (MatchPlayer.ColourPick)
        internal static void WriteLook(BinaryWriter w, string? character, string? skin, int colourPick)
        {
            w.Write(LookId(character) ?? "");
            w.Write(LookId(skin) ?? "");
            w.Write((sbyte)ColourId(colourPick));
        }

        internal static (string? Character, string? Skin, int ColourPick) ReadLook(BinaryReader r) =>
            (LookId(r.ReadString()), LookId(r.ReadString()), ColourId(r.ReadSByte()));

        private static int ColourId(int pick) => pick >= 0 && pick < LookAlikes.Colours ? pick : -1;

        // an id from another device names a folder under hd/: letters, digits, '_' and '-' only, else none
        private static string? LookId(string? id) =>
            string.IsNullOrEmpty(id) || id.Length > 64 || !id.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
                or (>= '0' and <= '9') or '_' or '-') ? null : id;

        internal static MatchSettings ReadSettings(BinaryReader r)
        {
            var s = new MatchSettings
            {
                Mode = (GameMode)r.ReadByte(),
                Arena = r.ReadString(),
                PointGoal = ReadOptInt(r),
                TimeLimitSeconds = ReadOptFloat(r),
                TimeGoalSeconds = ReadOptFloat(r),
                DamageLevel = ReadOptInt(r),
                FriendlyFire = ReadOptBool(r),
                OctolithReset = ReadOptBool(r),
                RadarPlayers = ReadOptBool(r),
                AffinityWeapons = ReadOptBool(r)
            };
            int count = r.ReadByte();
            for (int i = 0; i < count; i++)
            {
                var p = new MatchPlayer
                {
                    Hunter = (Hunter)r.ReadByte(),
                    Recolor = r.ReadByte(),
                    Team = r.ReadSByte(),
                    Bot = r.ReadBoolean(),
                    Remote = r.ReadBoolean(),
                    BotLevel = r.ReadByte()
                };
                string nick = r.ReadString();
                p.Nickname = nick.Length == 0 ? null : nick;
                p.RankLevel = r.ReadSByte();
                (p.Character, p.Skin, p.ColourPick) = ReadLook(r);
                s.Players.Add(p);
            }
            s.Abilities = ReadOptBool(r);
            s.PirateBlaster = ReadOptBool(r);
            s.ExtraCharacters = ReadOptBool(r);
            s.Crates = ReadOptBool(r);
            s.Breakables = ReadOptBool(r);
            return s;
        }

        private static void WriteOpt(BinaryWriter w, int? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static void WriteOpt(BinaryWriter w, float? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static void WriteOpt(BinaryWriter w, bool? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static int? ReadOptInt(BinaryReader r) => r.ReadBoolean() ? r.ReadInt32() : null;
        private static float? ReadOptFloat(BinaryReader r) => r.ReadBoolean() ? r.ReadSingle() : null;
        private static bool? ReadOptBool(BinaryReader r) => r.ReadBoolean() ? r.ReadBoolean() : null;

        private void SetStatus(string status)
        {
            Status = status;
            StatusChanged?.Invoke(status);
        }

        // this device's IPv4 address on the local network, for the host screen
        public static string LocalAddress()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                        {
                            return a.Address.ToString();
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return "this device";
        }

        // x.y.z.255-style broadcasts for each local IPv4 network (some routers drop 255.255.255.255)
        private static IEnumerable<IPAddress> DirectedBroadcasts()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork || a.IPv4Mask == null)
                        {
                            continue;
                        }
                        byte[] ip = a.Address.GetAddressBytes();
                        byte[] mask = a.IPv4Mask.GetAddressBytes();
                        if (mask.All(b => b == 0))
                        {
                            continue;
                        }
                        for (int i = 0; i < 4; i++)
                        {
                            ip[i] = (byte)(ip[i] | ~mask[i]);
                        }
                        list.Add(new IPAddress(ip));
                    }
                }
            }
            catch (Exception)
            {
            }
            return list;
        }

        public void Dispose()
        {
            if (!_running)
            {
                return;
            }
            if (_peer != null)
            {
                Send(new[] { MsgBye }, _peer);
            }
            IPEndPoint[] joiners;
            lock (_inputLock)
            {
                joiners = _joiners.Where(j => !j.Left).Select(j => j.EndPoint).ToArray();
            }
            foreach (IPEndPoint j in joiners)
            {
                Send(new[] { MsgBye }, j);
            }
            _running = false;
            _socket.Dispose();
            _beaconSocket?.Dispose();
            if (Current == this)
            {
                Current = null;
            }
        }
    }

    // a LAN host's joiner (LanSession.Joiners): its id (SendStart's seats), its player, where it is, whether it left
    public sealed record LanJoinerInfo(int Id, MatchPlayer Player, IPEndPoint Address, bool Left);

    public sealed class LanHostInfo
    {
        public IPEndPoint Address = new(IPAddress.None, 0);
        public string Name = "";
        public string RomKey = "";
        public GameMode Mode;
        public string Arena = "";
        public int Seats;
        public int MaxSeats = PlayerEntity.VanillaMaxPlayers; // the host's seat limit (P8: up to 8)
        public bool ExtraPickups = true, ExtraCharacters = true; // the host's extra rules (protocol 12; an older host: on)
        // a match running whose host holds these players' seats (they went quiet): theirs to rejoin, nobody else's
        public bool Running;
        public string[] Held = Array.Empty<string>();
        public bool HeldFor(string nickname) => Held.Any(n => String.Equals(n, nickname, StringComparison.OrdinalIgnoreCase));
        public long LastSeen;
        public string Label => $"{Name}: {Mode} in {MatchArenas.Find(Arena)?.InGameName ?? Arena} ({Address.Address}, ROM {RomKey})";
    }
}
