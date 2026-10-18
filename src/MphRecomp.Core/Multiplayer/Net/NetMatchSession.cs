using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;

namespace MphRecomp.Multiplayer.Net
{
    // An online match: one host and up to 7 joiners, each device a slot of the relay room the server gave the game
    // (docs/ONLINE_PLAY.md sections 7 and 9). The same messages and match flow as LanSession, for N players:
    //   joiner: JOIN (ROM key, game logic version, hunter, name, rank, character + skin) to the host, repeated until WELCOME (its seat and
    //           the match so far) or REFUSE; then it waits in the lobby for START (its final seat and the match).
    //   host:   each joiner gets the next seat after the humans already in (bots give way past PlayerEntity.MaxPlayers);
    //           SendStart sends every joiner its START; in the match one snapshot per frame goes to everyone at once
    //           (the relay fans it out) and each slot's newest input drives its seat.
    // BYE (Dispose) tells the others this device left. Receiving runs on the transport's thread; the game thread polls.
    // The lobby (protocol 4, Online play menus 2026-10-09): the host sends every joiner LOBBY (its seat, the match so far and
    // each seat's relay ping / choosing a hunter / back from the results / bot) on each change and once a second (its own
    // timer, whoever runs the host: the menus, the dev launcher, the PC tools); a
    // joiner sends LOOK (its hunter, character + skin, choosing, ping) on each change and once a second while it's in the
    // lobby; the host's SetTeam sets a seat's team. After a match the session goes on (EndMatch, from the match screen for
    // RESULTS): back in the lobby the same players play again (PLAY AGAIN), each joiner counted back by its LOOK; KICK
    // takes out one that didn't come back when the host starts without it.
    public sealed partial class NetMatchSession : IMatchNet, IDisposable
    {
        private const byte Protocol = 19; // 2: JOIN and the players carry the character + skin (MatchPlayer.Character/Skin);
                                         // 3: snapshots carry the mode's state (NetObjective: octoliths, nodes, timers);
                                         // 4: the lobby (LOBBY, LOOK, KICK) and back to it after a match;
                                         // 5 (O5): delta snapshots per joiner at 30 Hz (20 on a weak link) with its input
                                         //    ack; inputs carry the newest snapshot tick taken + the last 4 inputs;
                                         // 6 (O5): snapshots carry the extras (SeatExtras: afflictions, power-ups, turrets, stats)
                                         // 7 (O5b): snapshots carry the item handoff (HostHandoff); REHOST; host migration
                                         // 8: the match carries Abilities, snapshots the Trooper's cling + drone (SeatAbilities)
                                         // 9: the match carries PirateBlaster (the Trooper's affinity weapon); the Weapons
                                         //    bits carry bit 11 (the Pirate Blaster)
                                         // 10 (P8): up to 8 seats (SeatLimit)
                                         // 11: the match carries ExtraCharacters (the EXTRA CHARACTERS rule)
                                         // 12: inputs carry the touch controls' stylus (NetInput.TouchDx/Dy/Touching, T11)
                                         // 13: the detail stats carry BeamDamageMax (RESULTS efficiency, release 0.2.0 F1)
                                         // 14: no wire change; the arenas carry the baked pickups (Arenas/PlacedPickups.cs)
                                         // 15: the match carries Crates + Breakables; snapshots carry the Echoes crates +
                                         //     breakables (Snapshot.World, Net/NetWorld.cs; 0.2.0 owner 2026-10-11)
                                         // 16: Dark Samus's ball hop + Hyper Ball (sim); SeatAbilities carry the Hyper
                                         //     Ball (2 bytes a seat; docs/DARK_SAMUS_HYPER.md)
                                         // 17: Zero Suit's Paralyzer + 90 health (sim); SeatAbilities carry each seat's
                                         //     Paralyzer stun (1 byte a seat; docs/ZERO_SUIT_SAMUS.md section 7)
                                         // 18: the Trooper's Squad Mark; SeatAbilities carry each seat's marks (1 byte a
                                         //     seat: the marking seats, PlayerEntity.NetSquadMark)
                                         // 19: Shooting Gallery's turret on; snapshots carry it (NetWorld.Turret, 14 bytes)
        private const byte MsgJoin = 2, MsgWelcome = 3, MsgRefuse = 4, MsgInput = 5, MsgSnapshot = 6, MsgBye = 7, MsgStart = 8,
            MsgLobby = 9, MsgLook = 10, MsgKick = 11, MsgRehost = 12;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MPHO");

        private readonly IDatagramTransport _net;
        private readonly object _lock = new();
        private readonly byte[] _sendBuffer = new byte[2048];
        private volatile bool _running = true;

        public bool IsHost { get; private set; }
        public string Kind => "online";
        public string RomKey { get; }
        // host: the match with everyone who joined so far (final after SendStart); client: MatchClient.ForSeat
        public MatchSettings? Settings { get; private set; }
        public string Status { get; private set; } = "";
        // the lobby changed: someone joined or left, a LOOK, a LOBBY (receive thread)
        public event Action? PlayersChanged;

        // the menus taking the session over again (RESULTS' PLAY AGAIN, maybe from menus made again after the match): the old
        // menus' handlers off, so they don't pile up
        public void ClearPlayersChanged() => PlayersChanged = null;
        // client: the host's START arrived (the final match is in Settings)
        public bool Started { get; private set; }
        // client: the host said BYE
        public bool HostLeft { get; private set; }
        // client: the host took this player out (it started without them), and why; null = not
        public string? Kicked { get; private set; }
        public int LocalSlot => _net.LocalSlot;
        // the host's relay slot: 0 until a host migration (O5b) moves it (a client joining a game whose host moved before gets
        // it from the server's game card: Client(net, romKey, hostSlot))
        public int HostSlot => _hostSlot;
        private volatile int _hostSlot;

        // the lobby by seat of Settings: the player's round trip to the relay (-1 = not known), choosing a hunter right now,
        // back in the lobby (after a match: off until its device says so)
        public readonly record struct LobbySeat(int PingMs, bool Choosing, bool Back, bool Bot = false);
        public IReadOnlyList<LobbySeat> Lobby { get; private set; } = Array.Empty<LobbySeat>();
        // this device's round trip to the relay (OnlineMatch: RelayTransport.PingMs)
        public Func<int>? LocalPingMs { get; set; }
        // host: the match as this device set it up (seat 0 = this player, then bots)
        public MatchSettings? Template => _template;

        // ---------------------------------------------------------------- host state
        private sealed class Joiner
        {
            public int Slot;
            public MatchPlayer Player = new();
            public HashSet<string>? Arenas; // its extra arenas (ExtraArenas: JOIN's last field; null = an older joiner, none)
            public int Seat;
            public long LastHeard = Environment.TickCount64;
            public NetInput Latest;
            public bool HasInput;
            public int LastSeq = -1;
            public int Inputs;
            // O5: the inputs not applied yet by sequence (unwrapped), the last one applied and the frames held on it since
            public readonly SortedDictionary<int, NetInput> Queue = new();
            public int NewestSeq = -1;
            public int Applied = -1;
            public int Held;
            public int Merged, Starved; // inputs folded into one to catch up; frames with nothing new to apply
            // O5: the newest snapshot it has rebuilt (the base of its next delta), when the host last sent it one, its rate
            public uint AckedTick;
            public uint LastSentTick;
            public int Interval = SnapshotIntervalNormal;
            public int RttMs = -1;
            public long WindowStart;
            public int WindowPackets, WindowFirstSeq = -1;
            public float Loss;
            public long BytesSent;
            public int MaxSnapshot, Oversize; // (P8: the biggest snapshot sent, and those over the transport's MaxPayload)
            public string OversizeNote = ""; // (P8: the first oversize packet's blocks)
            // (P8: whole snapshots sent without extras + handoff because they didn't fit: the base this joiner really has)
            public readonly Dictionary<uint, SnapshotDelta.Encoded> Trimmed = new();
            public int TrimmedSends;
            // arrival jitter: each packet's arrival time less its input's due time (seq at 60 Hz), spread per window
            public double TransitMin = Double.MaxValue, TransitMax = Double.MinValue;
            public int JitterFrames, JitterFramesBefore;
            public int MaxBacklog => Math.Clamp(Math.Max(JitterFrames, JitterFramesBefore) + 2, MinBacklog, 12);
            public bool Left;
            public int Rejoins; // (O5) came back to the running match
            public int PingMs = -1;
            public bool Choosing;
            public bool Back = true;
            public int TeamAsk = -1; // (the last team ask applied: LOOK's tail)
        }

        private MatchSettings? _template;
        private readonly List<Joiner> _joiners = new();
        private int[] _remoteSeats = Array.Empty<int>();
        private bool _hostStarted;
        private bool _hostChoosing;
        private int _inputsReceived;
        private long _lastLobbyTick;
        // O5: the snapshots sent lately (the joiners' delta bases), oldest first
        private readonly Dictionary<uint, SnapshotDelta.Encoded> _sent = new();
        private readonly Queue<uint> _sentOrder = new();
        private const int SnapshotIntervalNormal = 2, SnapshotIntervalWeak = 3; // frames: 30 Hz, 20 Hz on a weak link
        // inputs waiting past the jitter (in frames) + 2 are folded into one (the jitter buffer catches up), never fewer than 3
        private const int MinBacklog = 3;
        private Timer? _lobbyTimer; // host: LOBBY once a second while there's no match

        // ---------------------------------------------------------------- client state
        private Snapshot? _latestSnap;
        private uint _lastSnapTick;
        private int _snapsReceived;
        // O5: deltas rebuilt here (and acked in every input), the last inputs sent (each packet carries 4)
        private readonly SnapshotDelta.Receiver _receiver = new();
        private uint _ackTick;
        private readonly NetInput[] _recentInputs = new NetInput[4];
        private int _recentCount;
        private long _bytesReceived;
        private long _hostHeard = Environment.TickCount64;
        private volatile string? _refused;
        private int _welcomeSeat = -1;
        private long _lastRejoin;
        // client: a match ended here and the host's LOBBY hasn't come since: the old match's last snapshots (and a START the
        // host answers them with) are dropped, not taken for the next match
        private volatile bool _betweenMatches;
        private Hunter _lookHunter;
        private string? _lookCharacter, _lookSkin;
        private bool _lookChoosing;
        private int _lookRank = -1;
        // client: the team this player asked for (-1 = none yet) and the ask's number: LOOK's optional tail (release 0.2.0
        // F2, no protocol bump: an older host stops reading before it); the host applies an ask once, by its number, so its
        // own TEAM swaps aren't undone by the LOOKs that repeat each second
        private int _lookTeam = -1;
        private byte _lookTeamAsk;

        private NetMatchSession(IDatagramTransport net, bool host, string romKey)
        {
            _net = net;
            IsHost = host;
            RomKey = romKey;
            _net.Received += OnReceived;
        }

        // template: the match as set up on this device (seat 0 = this player, then bots). The transport is slot 0.
        // seatLimit: the match's seats, the host's included (the game's 4, up to 8: P8; the server's game max refuses more)
        public static NetMatchSession Host(IDatagramTransport net, MatchSettings template, string romKey,
            int seatLimit = PlayerEntity.VanillaMaxPlayers)
        {
            var s = new NetMatchSession(net, host: true, romKey) { _template = template, SeatLimit = seatLimit };
            s.Settings = s.Build();
            s.SetStatus("hosting: waiting for players");
            s._lobbyTimer = new Timer(_ => s.HostLobbyTick(), null, 1000, 1000);
            return s;
        }

        public static NetMatchSession Client(IDatagramTransport net, string romKey, int hostSlot = 0) =>
            new(net, host: false, romKey) { _hostSlot = hostSlot };

        public IReadOnlyList<int> RemoteSeats => _remoteSeats;

        // host: how many seats this match has (the game's 4, up to 8: P8); joiners past it are refused and the template's
        // last bots give way to joiners. A template with more seats than this raises it
        public int SeatLimit
        {
            get => Math.Clamp(Math.Max(_seatLimit, _template?.Players.Count ?? 0), PlayerEntity.VanillaMaxPlayers, MatchSettings.MaxSeats);
            set => _seatLimit = value;
        }
        private int _seatLimit = PlayerEntity.VanillaMaxPlayers;

        public int Joined
        {
            get
            {
                if (!IsHost && IsOwner)
                {
                    return OwnerOthers().Count; // (a served game's owner: NetMatchOwner.cs)
                }
                lock (_lock)
                {
                    return _joiners.Count(j => !j.Left);
                }
            }
        }

        // the match: the template's humans, then the joiners in the order they came, then the template's bots, cut to
        // the seats MphRead has (bots go first)
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
                PirateBlaster = t.PirateBlaster, ExtraCharacters = t.ExtraCharacters,
                ServedHost = t.ServedHost // a served match host: no human in the template, every player a joiner
            };
            s.Players.AddRange(t.Players.Where(p => !p.Bot));
            foreach (Joiner j in _joiners.Where(j => !j.Left))
            {
                // the same hunter twice: the next palette, to tell them apart
                j.Player.Recolor = Math.Min(s.Players.Count(p => p.Hunter == j.Player.Hunter), 3);
                j.Seat = s.Players.Count;
                s.Players.Add(j.Player);
            }
            s.Players.AddRange(t.Players.Where(p => p.Bot));
            while (s.Players.Count > SeatLimit)
            {
                s.Players.RemoveAt(s.Players.Count - 1);
            }
            _remoteSeats = _joiners.Where(j => !j.Left).Select(j => j.Seat).ToArray();
            Characters.ApplyExtraRules(s); // (a joiner's TROOPER / DARK SAMUS plays its base hunter when the host bans them)
            return s;
        }

        private int Humans => _template!.Players.Count(p => !p.Bot) + _joiners.Count(j => !j.Left);

        // host: START GAME: every joiner gets its seat and the final match (three times: UDP); a joiner that missed them
        // asks again with a JOIN and gets its START again
        public void SendStart()
        {
            if (!IsHost && IsOwner)
            {
                OwnerSendStart(); // (a served game: the owner asks the match host, NetMatchOwner.cs)
                return;
            }
            lock (_lock)
            {
                _hostStarted = true;
                _hostChoosing = false;
                MatchOver = false;
                foreach (Joiner j in _joiners.Where(j => !j.Left))
                {
                    j.Choosing = false;
                    // (each match's driver counts its inputs from 0: a rematch's would look older than the last match's)
                    j.LastSeq = -1;
                    j.HasInput = false;
                    j.Inputs = 0;
                    ResetMatchState(j);
                    j.LastHeard = Environment.TickCount64; // loading the match is quiet: the 20 s rule starts now
                    byte[] start = Message(MsgStart, j.Seat, Settings!);
                    for (int i = 0; i < 3; i++)
                    {
                        _net.Send(j.Slot, start);
                    }
                }
            }
            SetStatus($"started with {Joined} joined");
        }

        // a joiner's per-match network state (inputs by sequence, acks, rates): fresh for each match
        private void ResetMatchState(Joiner j)
        {
            j.Queue.Clear();
            j.NewestSeq = -1;
            j.Applied = -1;
            j.Held = 0;
            j.AckedTick = 0;
            j.LastSentTick = 0;
            j.Trimmed.Clear();
            j.Interval = SnapshotIntervalNormal;
            j.WindowStart = 0;
            j.WindowFirstSeq = -1;
            j.WindowPackets = 0;
            j.Loss = 0;
            j.JitterFrames = j.JitterFramesBefore = 0;
            j.TransitMin = Double.MaxValue;
            j.TransitMax = Double.MinValue;
            _sent.Clear();
            _sentOrder.Clear();
        }

        // host, once per frame per remote seat: the next input in sequence (the jitter buffer). Nothing new = false: the
        // seat holds its last input this frame (Held counts it). More than MaxBacklog waiting (a burst after a stall): the
        // ones in between are folded into one (their button presses kept), so the delay doesn't stay.
        public bool TryTakeInput(int seat, out NetInput input)
        {
            input = default;
            lock (_lock)
            {
                Joiner? j = _joiners.FirstOrDefault(j => j.Seat == seat && !j.Left);
                if (j == null)
                {
                    return false;
                }
                if (j.Queue.Count == 0)
                {
                    if (j.Applied >= 0)
                    {
                        j.Held++;
                        j.Starved++;
                    }
                    return false;
                }
                int target = j.Queue.Keys.First();
                if (j.NewestSeq - j.Applied > j.MaxBacklog && j.Applied >= 0)
                {
                    target = j.NewestSeq - 1;
                }
                CampaignButtons pressed = CampaignButtons.None;
                int folded = 0;
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
                        folded++;
                        continue;
                    }
                    input = q;
                }
                input.Buttons |= pressed & PressButtons;
                j.Merged += folded;
                j.Applied = target;
                j.Held = 0;
                return true;
            }
        }

        // buttons whose short press must survive being folded into a later input
        private const CampaignButtons PressButtons = CampaignButtons.Shoot | CampaignButtons.Jump | CampaignButtons.Morph
            | CampaignButtons.Boost | CampaignButtons.AltAttack | CampaignButtons.Zoom | CampaignButtons.ZoomIn | CampaignButtons.ZoomOut | CampaignButtons.NextWeapon
            | CampaignButtons.PrevWeapon | CampaignButtons.AffinitySlot;

        public int InputsFrom(int seat)
        {
            lock (_lock)
            {
                // (also for one that left: a seat's device is fixed once the match started)
                return _joiners.FirstOrDefault(j => j.Seat == seat)?.Inputs ?? 0;
            }
        }

        public int InputsReceived => _inputsReceived;

        public long SilentMs(int seat)
        {
            long now = Environment.TickCount64;
            if (!IsHost)
            {
                return now - Interlocked.Read(ref _hostHeard);
            }
            lock (_lock)
            {
                Joiner? j = _joiners.FirstOrDefault(j => j.Seat == seat);
                return j == null || j.Left ? Int64.MaxValue / 2 : now - j.LastHeard;
            }
        }

        public bool PeerAlive
        {
            get
            {
                if (!IsHost)
                {
                    return SilentMs(0) < 3000;
                }
                lock (_lock)
                {
                    long now = Environment.TickCount64;
                    return _joiners.Any(j => !j.Left && now - j.LastHeard < 3000);
                }
            }
        }

        // host, every frame: each joiner whose turn it is (30 Hz, 20 on a weak link) gets this snapshot as a delta against
        // the newest one it acked (or whole), with its own input ack
        public void SendSnapshot(Snapshot snap)
        {
            if (_remoteSeats.Length == 0)
            {
                return;
            }
            SnapshotDelta.Encoded? cur = null;
            lock (_lock)
            {
                foreach (Joiner j in _joiners)
                {
                    if (j.Left || j.Slot < 0 || !(j.LastSentTick == 0 || snap.Tick < j.LastSentTick
                        || snap.Tick - j.LastSentTick >= (uint)j.Interval))
                    {
                        continue;
                    }
                    if (cur == null)
                    {
                        cur = SnapshotDelta.Encode(snap);
                        cur.SentAt = Environment.TickCount64;
                        _sent[cur.Tick] = cur;
                        _sentOrder.Enqueue(cur.Tick);
                        while (_sentOrder.Count > SnapshotDelta.History)
                        {
                            _sent.Remove(_sentOrder.Dequeue());
                        }
                    }
                    SnapshotDelta.Encoded? baseSnap = null;
                    if (j.AckedTick != 0 && j.AckedTick < cur.Tick)
                    {
                        baseSnap = j.Trimmed.TryGetValue(j.AckedTick, out SnapshotDelta.Encoded? t) ? t
                            : _sent.TryGetValue(j.AckedTick, out SnapshotDelta.Encoded? b) ? b : null;
                    }
                    byte[] packet = new byte[1 + SnapshotDelta.MaxSize(cur)];
                    packet[0] = MsgSnapshot;
                    int size = 1 + SnapshotDelta.Write(packet.AsSpan(1), cur, baseSnap, j.Applied < 0 ? -1 : j.Applied & 0xFFFF,
                        j.Held);
                    if (size > _net.MaxPayload && baseSnap == null && (cur.Extras.Length > 0 || cur.Handoff.Length > 0))
                    {
                        // P8: a whole 8-seat snapshot can pass MaxPayload (Capture: ~1.5 KB). Send it without the online
                        // extras + handoff (the client reads empty blocks as none) and remember that copy as this joiner's
                        // base, so the next delta carries them literal against it.
                        var trimmed = new SnapshotDelta.Encoded
                        {
                            Tick = cur.Tick, Bytes = cur.Bytes, SeatCount = cur.SeatCount, ItemsStart = cur.ItemsStart,
                            ObjectiveStart = cur.ObjectiveStart, SentAt = cur.SentAt
                        };
                        size = 1 + SnapshotDelta.Write(packet.AsSpan(1), trimmed, null, j.Applied < 0 ? -1 : j.Applied & 0xFFFF,
                            j.Held);
                        if (size <= _net.MaxPayload)
                        {
                            j.Trimmed[cur.Tick] = trimmed;
                            j.TrimmedSends++;
                            foreach (uint old in j.Trimmed.Keys.Where(k => cur.Tick - k > SnapshotDelta.History * 4).ToList())
                            {
                                j.Trimmed.Remove(old);
                            }
                        }
                    }
                    if (size <= _net.MaxPayload)
                    {
                        _net.Send(j.Slot, packet.AsSpan(0, size));
                        j.BytesSent += size;
                        j.MaxSnapshot = Math.Max(j.MaxSnapshot, size);
                    }
                    else
                    {
                        if (j.Oversize++ == 0)
                        {
                            j.OversizeNote = $" (first: {size} B {(baseSnap == null ? "whole" : "delta")}, seats {cur.SeatCount}, items "
                                + $"{cur.Items.Length}, mode {cur.Objective.Length}, extras {cur.Extras.Length}, handoff {cur.Handoff.Length})";
                            Console.WriteLine($"snapshot to seat {j.Seat} over {_net.MaxPayload} B{j.OversizeNote}");
                        }
                    }
                    j.LastSentTick = snap.Tick;
                }
            }
        }

        // the link to each joiner (host) or the host (client), for status lines and the tests
        public string NetStats()
        {
            lock (_lock)
            {
                if (!IsHost)
                {
                    return $"snapshots {_snapsReceived}, {_bytesReceived / 1024.0:0.0} KB in, missing bases {_receiver.MissingBase}";
                }
                return string.Join("; ", _joiners.Where(j => !j.Left).Select(j => $"seat {j.Seat}: {60 / j.Interval} Hz, rtt "
                    + $"{j.RttMs} ms, loss {j.Loss * 100:0}%, jitter {j.JitterFrames} frames, {j.BytesSent / 1024.0:0.0} KB out, "
                    + $"inputs merged {j.Merged}, starved {j.Starved}, biggest snapshot {j.MaxSnapshot} B, oversize {j.Oversize}{j.OversizeNote}"
                    + (j.TrimmedSends > 0 ? $", whole without extras {j.TrimmedSends}" : "")));
            }
        }

        // ---------------------------------------------------------------- the lobby

        // host: the lobby changed on this device (its player's hunter, the template): the match again, and everyone told
        public void LobbyChanged()
        {
            lock (_lock)
            {
                if (!IsHost || _hostStarted)
                {
                    return;
                }
                Settings = Build();
            }
            BroadcastLobby();
            PlayersChanged?.Invoke();
        }

        // host: this player is choosing a hunter (SELECT HUNTER from the lobby) or done
        public void SetHostChoosing(bool choosing)
        {
            _hostChoosing = choosing;
            BroadcastLobby();
        }

        // host: a seat's team in a team mode (0 orange, 1 green; -1 = whichever team is smaller)
        public void SetTeam(int seat, int team)
        {
            if (!IsHost && IsOwner)
            {
                OwnerEdit(s => { if (seat >= 0 && seat < s.Players.Count) s.Players[seat].Team = team; }); // (NetMatchOwner.cs)
                return;
            }
            lock (_lock)
            {
                if (!IsHost || _hostStarted || Settings == null || seat < 0 || seat >= Settings.Players.Count)
                {
                    return;
                }
                // (the seats hold the template's and the joiners' own players: the team stays through every Build)
                Settings.Players[seat].Team = team;
                Settings = Build();
            }
            BroadcastLobby();
            PlayersChanged?.Invoke();
        }

        // client: asks the host for this team (0 orange, 1 green) in a team mode: the guest's TEAM box (release 0.2.0 F2)
        public void RequestTeam(int team)
        {
            if (IsHost || Settings == null || Started)
            {
                return;
            }
            _lookTeam = team is 0 or 1 ? team : -1;
            _lookTeamAsk++;
            SendLook();
        }

        // host: the lobby's bot controls (release 0.2.0 F2). A bot is a template player (Build adds the template's bots
        // after the joiners), so these change the template and build again.
        // the bot on this seat becomes `bot` (its hunter / level / name from the menus), keeping its team
        public void ReplaceBot(int seat, MatchPlayer bot)
        {
            EditBots(seat, (t, old) =>
            {
                bot.Bot = true;
                bot.Team = old.Team;
                t.Players[t.Players.IndexOf(old)] = bot;
            });
        }

        // the bot on this seat leaves the game
        public void RemoveBot(int seat) => EditBots(seat, (t, old) => t.Players.Remove(old));

        // a new bot (team -1: the smaller team), while a seat is left
        public void AddBot(MatchPlayer bot)
        {
            if (!IsHost && IsOwner)
            {
                bot.Bot = true;
                OwnerEdit(s => { if (s.Players.Count < MatchSettings.MaxSeats) s.Players.Add(bot); }); // (NetMatchOwner.cs)
                return;
            }
            lock (_lock)
            {
                if (!IsHost || _hostStarted || Settings == null || _template == null || Settings.Players.Count >= MatchSettings.MaxSeats)
                {
                    return;
                }
                bot.Bot = true;
                _template.Players.Add(bot);
                _seatLimit = Math.Max(_seatLimit, _template.Players.Count + _joiners.Count(j => !j.Left));
                Settings = Build();
            }
            BroadcastLobby();
            PlayersChanged?.Invoke();
        }

        private void EditBots(int seat, Action<MatchSettings, MatchPlayer> edit)
        {
            if (!IsHost && IsOwner)
            {
                OwnerEdit(s => { if (seat >= 0 && seat < s.Players.Count && s.Players[seat].Bot) edit(s, s.Players[seat]); }); // (NetMatchOwner.cs)
                return;
            }
            lock (_lock)
            {
                if (!IsHost || _hostStarted || Settings == null || _template == null || seat < 0 || seat >= Settings.Players.Count)
                {
                    return;
                }
                MatchPlayer old = Settings.Players[seat];
                if (!old.Bot || !_template.Players.Contains(old))
                {
                    return;
                }
                edit(_template, old);
                Settings = Build();
            }
            BroadcastLobby();
            PlayersChanged?.Invoke();
        }

        // host: the joiners not back from the last match's results yet (their names)
        public IReadOnlyList<string> NotBack()
        {
            if (!IsHost && IsOwner)
            {
                return OwnerOthers().Where(o => !o.Seat.Back).Select(o => o.Name).ToList();
            }
            lock (_lock)
            {
                return _joiners.Where(j => !j.Left && !j.Back).Select(j => j.Player.Nickname ?? "").ToList();
            }
        }

        // host: the joiners still here who don't have this arena (their names; empty = everyone can play it: owner 2026-10-10, N27)
        public IReadOnlyList<string> Lacking(string arena)
        {
            lock (_lock)
            {
                return _joiners.Where(j => !j.Left && !ExtraArenas.Has(j.Arenas, arena)).Select(j => j.Player.Nickname ?? "a player").ToList();
            }
        }

        // host: the joiners still choosing a hunter (their names)
        public IReadOnlyList<string> StillChoosing()
        {
            if (!IsHost && IsOwner)
            {
                return OwnerOthers().Where(o => o.Seat.Back && o.Seat.Choosing).Select(o => o.Name).ToList();
            }
            lock (_lock)
            {
                return _joiners.Where(j => !j.Left && j.Back && j.Choosing).Select(j => j.Player.Nickname ?? "").ToList();
            }
        }

        // host: takes out the joiners not back from the results (KICK, three times: UDP), before starting without them
        public void KickNotBack(string reason)
        {
            if (!IsHost && IsOwner)
            {
                _ownerKickReason = reason; // (the match host takes them out when the owner's START comes: NetMatchOwner.cs)
                return;
            }
            bool changed = false;
            lock (_lock)
            {
                foreach (Joiner j in _joiners.Where(j => !j.Left && !j.Back).ToList())
                {
                    byte[] kick = Kick(reason);
                    for (int i = 0; i < 3; i++)
                    {
                        _net.Send(j.Slot, kick);
                    }
                    j.Left = true;
                    _joiners.Remove(j);
                    changed = true;
                }
                if (changed)
                {
                    Settings = Build();
                }
            }
            if (changed)
            {
                BroadcastLobby();
                PlayersChanged?.Invoke();
            }
        }

        // the match is over and the session goes on (the match screen hands it back for RESULTS): host: the lobby again (who
        // left is gone; each joiner counts as back once its LOOK comes); client: ready for the next START
        public void EndMatch()
        {
            if (IsHost)
            {
                lock (_lock)
                {
                    _hostStarted = false;
                    _joiners.RemoveAll(j => j.Left || j.Slot < 0); // (O5b: a seat whose device never found the new host)
                    foreach (Joiner j in _joiners)
                    {
                        j.Back = false;
                        j.Choosing = false;
                        j.HasInput = false;
                        j.LastSeq = -1; // (the next match's inputs count from 0 again)
                        ResetMatchState(j);
                    }
                    Settings = Build();
                }
            }
            else
            {
                _betweenMatches = true;
                lock (_lock)
                {
                    _latestSnap = null;
                    _lastSnapTick = 0; // (the next match's snapshots count from its own first tick)
                    ResetClientMatchState();
                }
                _snapsReceived = 0;
                Started = false;
            }
            SetStatus("the match is over: the lobby again");
        }

        // client: this player's hunter and look, and whether it's choosing one right now (and its rank level: a match may have
        // changed it): the host hears it now (LOOK) and then once a second from LobbyTick
        public void SetLook(Hunter hunter, string? character, string? skin, bool choosing, int rankLevel = -2)
        {
            if (rankLevel >= -1) _lookRank = rankLevel;
            _lookHunter = hunter;
            _lookCharacter = character;
            _lookSkin = skin;
            _lookChoosing = choosing;
            if (!IsHost && Settings != null && !Started)
            {
                SendLook();
            }
        }

        // the menus, each frame while this device is in the game (inLobby: its player is in the lobby, not on RESULTS): once a
        // second a joiner in the lobby sends its LOOK (the host's LOBBY goes out from its own timer)
        public void LobbyTick(bool inLobby)
        {
            long now = Environment.TickCount64;
            if (IsHost || now - _lastLobbyTick < 1000 || !_running)
            {
                return;
            }
            _lastLobbyTick = now;
            if (inLobby && Settings != null && !Started && Kicked == null && !HostLeft)
            {
                SendLook();
            }
        }

        // host, once a second (also while it's on RESULTS: the joiners hear it's there)
        private void HostLobbyTick()
        {
            if (_running && !_hostStarted)
            {
                BroadcastLobby();
            }
        }

        private void SendLook()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgLook);
            w.Write((byte)_lookHunter);
            LanSession.WriteLook(w, _lookCharacter, _lookSkin);
            w.Write(_lookChoosing);
            w.Write((short)Math.Clamp(LocalPingMs?.Invoke() ?? -1, -1, short.MaxValue));
            w.Write((sbyte)_lookRank);
            w.Write((sbyte)_lookTeam); // (the optional tail: RequestTeam)
            w.Write(_lookTeamAsk);
            _net.Send(_hostSlot, ms.ToArray());
        }

        // host: every joiner gets its seat, the match so far and the seats' lobby state (nothing while a match runs)
        private void BroadcastLobby()
        {
            lock (_lock)
            {
                if (!IsHost || _hostStarted || Settings == null || !_running)
                {
                    return;
                }
                var seats = new LobbySeat[Settings.Players.Count];
                int hosts = _template!.Players.Count(p => !p.Bot);
                for (int i = 0; i < seats.Length; i++)
                {
                    Joiner? j = _joiners.FirstOrDefault(j => !j.Left && j.Seat == i);
                    seats[i] = j != null ? new LobbySeat(j.PingMs, j.Choosing, j.Back)
                        : i < hosts ? new LobbySeat(LocalPingMs?.Invoke() ?? -1, _hostChoosing, true)
                        : new LobbySeat(-1, false, true, Bot: true);
                }
                Lobby = seats;
                foreach (Joiner j in _joiners.Where(j => !j.Left))
                {
                    var ms = new MemoryStream();
                    var w = new BinaryWriter(ms);
                    Header(w, MsgLobby);
                    w.Write((byte)j.Seat);
                    LanSession.WriteSettings(w, Settings);
                    w.Write((byte)seats.Length);
                    foreach (LobbySeat seat in seats)
                    {
                        w.Write((short)Math.Clamp(seat.PingMs, -1, short.MaxValue));
                        w.Write(seat.Choosing);
                        w.Write(seat.Back);
                        w.Write(seat.Bot);
                    }
                    _net.Send(j.Slot, ms.ToArray());
                }
            }
        }

        // ---------------------------------------------------------------- client

        // asks the host for a seat (retries for a few seconds); true once the WELCOME arrived. character / skin: this
        // player's MatchPlayer.Character / Skin (null = the hunter's own), shown on every device
        public bool Join(Hunter hunter, string nickname, int rankLevel = -1, int timeoutMs = 8000, string? character = null,
            string? skin = null)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgJoin);
            w.Write(RomKey);
            w.Write(Replay.ReplayFile.CurrentSimVersion);
            w.Write((byte)hunter);
            w.Write(nickname);
            w.Write((sbyte)rankLevel);
            LanSession.WriteLook(w, character, skin);
            ExtraArenas.Write(w); // (optional, last)
            _join = ms.ToArray();
            (_lookHunter, _lookCharacter, _lookSkin, _lookChoosing, _lookRank) = (hunter, character, skin, false, rankLevel);
            SetStatus("joining the host...");
            long end = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < end && Settings == null && _refused == null && _running)
            {
                _net.Send(_hostSlot, _join);
                for (int i = 0; i < 25 && Settings == null && _refused == null; i++)
                {
                    Thread.Sleep(10);
                }
            }
            if (_refused != null)
            {
                SetStatus("refused: " + _refused);
                return false;
            }
            if (Settings == null)
            {
                SetStatus("no answer from the host");
                return false;
            }
            SetStatus($"joined: seat {Settings.LocalSeat}, waiting for the host to start");
            return true;
        }

        private byte[]? _join;

        // client: waits for START (or the host leaving); true = started
        public bool WaitForStart(int timeoutMs)
        {
            long end = Environment.TickCount64 + timeoutMs;
            while (!Started && !HostLeft && _running && Environment.TickCount64 < end)
            {
                Thread.Sleep(5);
            }
            return Started;
        }

        public bool TryTakeSnapshot(out Snapshot snap)
        {
            lock (_lock)
            {
                snap = _latestSnap!;
                bool had = _latestSnap != null;
                _latestSnap = null;
                return had;
            }
        }

        public int SnapshotsReceived => _snapsReceived;

        // IMatchNet: the replay link's side channel (MatchNetSide.MsgSide, then the payload as is)
        public event Action<int, byte[]>? SideReceived;

        public void SendSide(int seat, ReadOnlySpan<byte> payload)
        {
            int slot = _hostSlot;
            if (IsHost)
            {
                lock (_lock)
                {
                    Joiner? j = _joiners.FirstOrDefault(j => j.Seat == seat && !j.Left);
                    if (j == null || j.Slot < 0)
                    {
                        return;
                    }
                    slot = j.Slot;
                }
            }
            byte[] packet = new byte[1 + payload.Length];
            packet[0] = MatchNetSide.MsgSide;
            payload.CopyTo(packet.AsSpan(1));
            _net.Send(slot, packet);
        }

        // client: this frame's input, with the 3 before it (a lost packet loses nothing) and the newest snapshot rebuilt here
        // (the host's next delta base)
        public void SendInput(in NetInput input)
        {
            Array.Copy(_recentInputs, 0, _recentInputs, 1, _recentInputs.Length - 1);
            _recentInputs[0] = input;
            _recentCount = Math.Min(_recentCount + 1, _recentInputs.Length);
            Span<byte> packet = stackalloc byte[6 + 4 * NetInput.RecordSize];
            packet[0] = MsgInput;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet[1..], Volatile.Read(ref _ackTick));
            packet[5] = (byte)_recentCount;
            for (int i = 0; i < _recentCount; i++)
            {
                _recentInputs[i].WriteRecord(packet[(6 + i * NetInput.RecordSize)..]);
            }
            _net.Send(_hostSlot, packet[..(6 + _recentCount * NetInput.RecordSize)]);
            if (_rehosting && Environment.TickCount64 - _lastRehost > 200)
            {
                SendRehost();
            }
        }

        // ---------------------------------------------------------------- receiving

        private void OnReceived(int from, ReadOnlySpan<byte> data)
        {
            if (data.Length == 0 || !_running)
            {
                return;
            }
            if (data[0] == MatchNetVoice.MsgVoice)
            {
                return; // (Voice O8's packets: VoiceChat reads them off the transport itself)
            }
            if (data[0] == MatchNetSide.MsgSide)
            {
                int fromSeat = 0;
                if (IsHost)
                {
                    lock (_lock)
                    {
                        Joiner? j = _joiners.FirstOrDefault(j => j.Slot == from && !j.Left);
                        if (j == null)
                        {
                            return;
                        }
                        fromSeat = j.Seat;
                    }
                }
                else if (from != _hostSlot)
                {
                    return;
                }
                SideReceived?.Invoke(fromSeat, data[1..].ToArray());
                return;
            }
            if (IsHost && data[0] == MsgSnapshot)
            {
                return; // (O5b: the old host's last snapshots, still in flight when this device took over)
            }
            try
            {
                if (IsHost)
                {
                    HostReceive(from, data);
                }
                else if (from == _hostSlot)
                {
                    ClientReceive(data);
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IndexOutOfRangeException
                or ArgumentException)
            {
                SetStatus($"bad packet (type {data[0]} from slot {from}): " + ex.Message);
            }
        }

        private void HostReceive(int from, ReadOnlySpan<byte> data)
        {
            if (data[0] == MsgInput)
            {
                if (data.Length < 6)
                {
                    return;
                }
                uint ack = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
                int count = Math.Min((int)data[5], (data.Length - 6) / NetInput.RecordSize);
                long now = Environment.TickCount64;
                lock (_lock)
                {
                    Joiner? j = _joiners.FirstOrDefault(j => j.Slot == from && !j.Left);
                    if (j == null || count == 0)
                    {
                        return;
                    }
                    j.LastHeard = now;
                    if (ack > j.AckedTick && _sent.TryGetValue(ack, out SnapshotDelta.Encoded? acked))
                    {
                        int rtt = (int)(now - acked.SentAt);
                        j.RttMs = j.RttMs < 0 ? rtt : (j.RttMs * 7 + rtt) / 8;
                        j.AckedTick = ack;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        NetInput input = NetInput.ReadRecord(data[(6 + i * NetInput.RecordSize)..]);
                        // the 16-bit sequence unwrapped against the newest seen
                        int seq = j.NewestSeq < 0 ? input.Seq : j.NewestSeq + (short)(input.Seq - (ushort)j.NewestSeq);
                        if (i == 0)
                        {
                            CountPacket(j, seq, now);
                        }
                        if (seq <= j.Applied || j.Queue.ContainsKey(seq))
                        {
                            continue;
                        }
                        j.Queue[seq] = input;
                        if (seq > j.NewestSeq)
                        {
                            j.NewestSeq = seq;
                            j.LastSeq = input.Seq;
                            j.Latest = input;
                        }
                    }
                    // a queue far behind (a long stall): only the newest few matter
                    while (j.Queue.Count > 32)
                    {
                        j.Queue.Remove(j.Queue.Keys.First());
                    }
                    j.Inputs++;
                }
                Interlocked.Increment(ref _inputsReceived);
                return;
            }
            if (data[0] == MsgBye)
            {
                bool changed = false;
                lock (_lock)
                {
                    Joiner? j = _joiners.FirstOrDefault(j => j.Slot == from && !j.Left);
                    if (j != null)
                    {
                        j.Left = true;
                        changed = true;
                        if (!_hostStarted)
                        {
                            // still in the lobby: the seats close up
                            _joiners.Remove(j);
                            Settings = Build();
                        }
                    }
                }
                if (changed)
                {
                    SetStatus($"slot {from} left");
                    BroadcastLobby();
                    PlayersChanged?.Invoke();
                }
                return;
            }
            if (LanSession.OlderJoin(data, Magic, Protocol) is byte old)
            {
                _net.Send(from, Refuse($"this host has a newer version of the game (match protocol {Protocol}, you {old}): "
                    + "update the game", old));
                return;
            }
            var r = new BinaryReader(new MemoryStream(data.ToArray()));
            byte type = ReadHeader(r);
            if (type == MsgJoin)
            {
                OnJoin(r, from);
            }
            else if (type == MsgLook)
            {
                OnLook(r, from);
            }
            else if (type == MsgRehost)
            {
                OnRehost(r, from);
            }
            else if (type == MsgOwner)
            {
                OnOwner(r, from); // (a served game's lobby owner: NetMatchOwner.cs)
            }
        }

        // a joiner in the lobby: its hunter and look, choosing, ping; it's back (after a match)
        private void OnLook(BinaryReader r, int from)
        {
            var hunter = (Hunter)r.ReadByte();
            (string? character, string? skin) = LanSession.ReadLook(r);
            bool choosing = r.ReadBoolean();
            int ping = r.ReadInt16();
            int rank = r.ReadSByte();
            // (the optional tail, from release 0.2.0: the team this joiner asked for and the ask's number; an older joiner's
            // LOOK ends before it)
            int team = -1, ask = -1;
            if (r.BaseStream.Length - r.BaseStream.Position >= 2)
            {
                team = r.ReadSByte();
                ask = r.ReadByte();
            }
            bool changed = false;
            lock (_lock)
            {
                Joiner? j = _joiners.FirstOrDefault(j => j.Slot == from && !j.Left);
                if (j == null || _hostStarted)
                {
                    return;
                }
                j.LastHeard = Environment.TickCount64;
                j.PingMs = ping;
                j.Player.RankLevel = rank; // (the match's ranking points use it: the next LOBBY carries it)
                if (j.Player.Hunter != hunter || j.Player.Character != character || j.Player.Skin != skin)
                {
                    j.Player.Hunter = hunter;
                    j.Player.Character = character;
                    j.Player.Skin = skin;
                    Settings = Build();
                    changed = true;
                }
                if (j.Choosing != choosing || !j.Back)
                {
                    j.Choosing = choosing;
                    j.Back = true;
                    changed = true;
                }
                if (team is 0 or 1 && ask != j.TeamAsk)
                {
                    j.TeamAsk = ask;
                    if (j.Player.Team != team)
                    {
                        j.Player.Team = team;
                        Settings = Build();
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                BroadcastLobby();
                PlayersChanged?.Invoke();
            }
        }

        private void OnJoin(BinaryReader r, int from)
        {
            string romKey = r.ReadString();
            int simVersion = r.ReadInt32();
            var hunter = (Hunter)r.ReadByte();
            string nickname = r.ReadString();
            int rankLevel = r.ReadSByte();
            (string? character, string? skin) = LanSession.ReadLook(r);
            HashSet<string>? arenas = ExtraArenas.Read(r);
            bool joined = false;
            lock (_lock)
            {
                Joiner? known = _joiners.FirstOrDefault(j => j.Slot == from && !j.Left);
                if (known == null && _hostStarted && romKey == RomKey && simVersion == Replay.ReplayFile.CurrentSimVersion)
                {
                    // O5 rejoin: a player of this match coming back after it left (the server gives it its slot again;
                    // or the same name from another slot)
                    known = _joiners.FirstOrDefault(j => j.Left && j.Slot == from)
                        ?? _joiners.FirstOrDefault(j => j.Left && j.Player.Nickname == nickname);
                    if (known != null)
                    {
                        known.Left = false;
                        known.Slot = from;
                        known.Inputs = Math.Max(known.Inputs, 1);
                    }
                }
                if (known != null)
                {
                    if (_hostStarted && known.Inputs > 0)
                    {
                        // O5 rejoin: back in the running match (its app restarted, or it left and came back): its inputs
                        // start again from 0 and its snapshots from a full one (a JOIN repeated before the START got
                        // through comes before any input; one repeated while rejoining counts once)
                        if (known.LastSeq >= 0)
                        {
                            known.Rejoins++;
                            SetStatus($"{known.Player.Nickname} rejoined from slot {from}");
                        }
                        known.LastSeq = -1;
                        known.HasInput = false;
                        ResetMatchState(known);
                        known.LastHeard = Environment.TickCount64;
                    }
                    // a repeated JOIN: the answer got lost (or, after START, the START did)
                    _net.Send(from, Message(_hostStarted ? MsgStart : MsgWelcome, known.Seat, Settings!));
                    return;
                }
                string? refuse = romKey != RomKey
                    ? $"ROM revision differs (host {RomKey}, you {romKey}): both players need the same one"
                    : simVersion != Replay.ReplayFile.CurrentSimVersion
                    ? $"game version differs (host plays version {Replay.ReplayFile.CurrentSimVersion}, you {simVersion}): "
                        + "update the game"
                    : _hostStarted ? "this match already started"
                    : Humans >= SeatLimit ? "this match is full"
                    // an arena this joiner lacks (owner 2026-10-10, N27: it would fail to load it and the match start without it)
                    : ExtraArenas.Refusal(arenas, Settings?.Arena ?? _template?.Arena ?? "");
                if (refuse != null)
                {
                    _net.Send(from, Refuse(refuse));
                    return;
                }
                var joiner = new Joiner
                {
                    Slot = from,
                    Player = new MatchPlayer
                    {
                        Hunter = hunter, Remote = true, Nickname = nickname, RankLevel = rankLevel, Character = character, Skin = skin
                    },
                    Arenas = arenas,
                };
                _joiners.Add(joiner);
                Settings = Build();
                _net.Send(from, Message(MsgWelcome, joiner.Seat, Settings));
                joined = true;
            }
            if (joined)
            {
                SetStatus($"{nickname} ({hunter}) joined from slot {from}");
                BroadcastLobby();
                PlayersChanged?.Invoke();
            }
        }

        private void ClientReceive(ReadOnlySpan<byte> data)
        {
            if (data[0] == MsgSnapshot)
            {
                Interlocked.Exchange(ref _hostHeard, Environment.TickCount64);
                if (_betweenMatches)
                {
                    return; // (the match that just ended: not the next one's, and no JOIN to ask for its START again)
                }
                Snapshot? snap;
                lock (_lock)
                {
                    _bytesReceived += data.Length;
                    snap = _receiver.Read(data[1..]);
                    if (snap == null)
                    {
                        return; // its base is gone here: the next one (against a newer ack) will do
                    }
                    if (snap.Tick > _ackTick)
                    {
                        Volatile.Write(ref _ackTick, snap.Tick);
                    }
                    if (snap.Tick > _lastSnapTick || _lastSnapTick == 0)
                    {
                        _lastSnapTick = snap.Tick;
                        _latestSnap = snap;
                    }
                }
                Interlocked.Increment(ref _snapsReceived);
                // the match runs but no START arrived: ask again (the host answers a known slot with its START)
                long now = Environment.TickCount64;
                if (!Started && _join != null && now - _lastRejoin > 200)
                {
                    _lastRejoin = now;
                    _net.Send(_hostSlot, _join);
                }
                if (_rehosting)
                {
                    _rehosting = false; // (the new host has this device's seat: its snapshots come)
                    SetStatus($"the new host (slot {_hostSlot}) took over");
                }
                return;
            }
            if (data[0] == MsgBye)
            {
                // (a match that may move to a new host: the host counts as gone from now, and the driver waits for the
                // server's word on who hosts next before it ends the match)
                Interlocked.Exchange(ref _hostHeard, CanMigrate && Started ? Environment.TickCount64 - ByeCountsAsSilentMs : 0);
                HostLeft = true;
                SetStatus("the host left");
                return;
            }
            var r = new BinaryReader(new MemoryStream(data.ToArray()));
            byte type = ReadHeader(r);
            if (type == MsgRefuse)
            {
                _refused = r.ReadString();
            }
            else if (type == MsgWelcome && Settings == null)
            {
                int seat = r.ReadByte();
                MatchSettings host = LanSession.ReadSettings(r);
                _welcomeSeat = seat;
                Interlocked.Exchange(ref _hostHeard, Environment.TickCount64);
                Settings = MatchClient.ForSeat(host, seat);
            }
            else if (type == MsgLobby && !Started && Kicked == null)
            {
                int seat = r.ReadByte();
                MatchSettings host = LanSession.ReadSettings(r);
                var seats = new LobbySeat[r.ReadByte()];
                for (int i = 0; i < seats.Length; i++)
                {
                    seats[i] = new LobbySeat(r.ReadInt16(), r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean());
                }
                _welcomeSeat = seat;
                _betweenMatches = false; // (the host is in its lobby: what comes next is the next match's)
                Interlocked.Exchange(ref _hostHeard, Environment.TickCount64);
                lock (_lock)
                {
                    _hostLobby = host; // (a served game's owner edits from it: NetMatchOwner.cs)
                }
                Settings = MatchClient.ForSeat(host, seat);
                Lobby = seats;
                PlayersChanged?.Invoke();
            }
            else if (type == MsgKick && Kicked == null)
            {
                Kicked = r.ReadString();
                SetStatus("taken out: " + Kicked);
                PlayersChanged?.Invoke();
            }
            else if (type == MsgStart && !Started && !_betweenMatches)
            {
                int seat = r.ReadByte();
                _hostSettings = LanSession.ReadSettings(r);
                Settings = MatchClient.ForSeat(_hostSettings, seat);
                // a fresh match: its snapshots count from its own first tick, and the driver gives a loading host 20 s again
                lock (_lock)
                {
                    _latestSnap = null;
                    _lastSnapTick = 0;
                    ResetClientMatchState();
                }
                _snapsReceived = 0;
                Interlocked.Exchange(ref _hostHeard, Environment.TickCount64);
                SetStatus($"started: seat {seat}" + (seat != _welcomeSeat && _welcomeSeat >= 0 ? $" (was {_welcomeSeat})" : ""));
                Started = true;
                MatchOver = false;
            }
        }

        // ---------------------------------------------------------------- O5b host migration
        //   The server names the new host when the host of a started match leaves or its connection drops (OnlineMatch:
        //   MemberEvent.HostSlot, the lowest slot still online). That device becomes the host (Promote) from its own copy of
        //   the match; every other device points at the new host's slot and sends REHOST (its seat) until the new host's
        //   snapshots arrive. The relay room's slots stay as they are; the old host's is out of the game.

        // the server can move this match to a new host (OnlineMatch sets it for a server that can, 0.5.0+): the driver
        // waits for that instead of ending the match when the host goes quiet or says BYE
        public bool CanMigrate { get; set; }
        // a client waits this long past its usual silence rule (10 s) for the server to name a new host; a BYE counts as
        // that much silence at once (so a host that quit gets MigrateWaitMs, then the match ends)
        public const long MigrateWaitMs = 15000, ByeCountsAsSilentMs = 10000;
        // host changes seen (the driver restarts prediction on each), this device promoted to host (once)
        // (bumped last, after IsHost/Promoted/_hostSlot changed: the match thread reads it without the lock)
        public int Migrations => _migrations;
        private volatile int _migrations;
        public Promotion? Promoted { get; private set; }
        // the host's own match (START's settings, every seat as the host has it: its bots and their levels)
        private MatchSettings? _hostSettings;
        private volatile bool _rehosting;
        private long _lastRehost;

        public sealed record Promotion(IReadOnlyList<int> OldHostSeats, IReadOnlyList<(int Seat, int Level)> Bots);

        /// <summary>Set by the driver once the match reached GameOver (cleared by the next START): the scores are final,
        /// so a host that leaves from here on (its results, its app closing) is not followed.</summary>
        public volatile bool MatchOver;

        // the server's word (any thread): slot hosts the match now
        public void HostMoved(int slot)
        {
            lock (_lock)
            {
                if (IsHost || slot == _hostSlot || !Started || MatchOver || _hostSettings == null || Settings == null)
                {
                    return;
                }
                if (slot == _net.LocalSlot)
                {
                    Promote();
                    _migrations++;
                    return;
                }
                _hostSlot = slot;
                _latestSnap = null;
                _lastSnapTick = 0; // (the new host's ticks are its own frames)
                ResetClientMatchState();
                HostLeft = false;
                Interlocked.Exchange(ref _hostHeard, Environment.TickCount64);
                _rehosting = true;
                _migrations++;
            }
            SetStatus($"the host left: slot {slot} hosts now");
            SendRehost();
        }

        private void SendRehost()
        {
            _lastRehost = Environment.TickCount64;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgRehost);
            w.Write((byte)Settings!.LocalSeat);
            _net.Send(_hostSlot, ms.ToArray());
        }

        // this device hosts the match now (under _lock): its joiners are the other humans' seats, each waiting for its
        // device's REHOST (20 s, as a player loading a match); the old host's bots are this device's
        private void Promote()
        {
            MatchSettings hs = _hostSettings!;
            int own = Settings!.LocalSeat;
            var oldHost = new List<int>();
            var bots = new List<(int, int)>();
            _template = new MatchSettings
            {
                Mode = hs.Mode, Arena = hs.Arena, PointGoal = hs.PointGoal, TimeLimitSeconds = hs.TimeLimitSeconds,
                TimeGoalSeconds = hs.TimeGoalSeconds, DamageLevel = hs.DamageLevel, FriendlyFire = hs.FriendlyFire,
                OctolithReset = hs.OctolithReset, RadarPlayers = hs.RadarPlayers, AffinityWeapons = hs.AffinityWeapons,
                Crates = hs.Crates, Breakables = hs.Breakables, Abilities = hs.Abilities, PirateBlaster = hs.PirateBlaster,
                ExtraCharacters = hs.ExtraCharacters
            };
            _seatLimit = hs.Players.Count; // (P8: the match's own size, for PLAY AGAIN)
            MatchPlayer me = hs.Players[own];
            me.Remote = false;
            _template.Players.Add(me);
            _joiners.Clear();
            long now = Environment.TickCount64;
            for (int i = 0; i < hs.Players.Count; i++)
            {
                MatchPlayer p = hs.Players[i];
                if (i == own)
                {
                    continue;
                }
                if (p.Bot)
                {
                    bots.Add((i, p.BotLevel));
                    _template.Players.Add(p);
                }
                else if (!p.Remote)
                {
                    oldHost.Add(i); // (the old host's own player)
                }
                else
                {
                    _joiners.Add(new Joiner { Slot = -1, Seat = i, Player = p, LastHeard = now, Back = true });
                }
            }
            _remoteSeats = _joiners.Select(j => j.Seat).ToArray();
            _sent.Clear();
            _sentOrder.Clear();
            _hostSlot = _net.LocalSlot;
            _rehosting = false;
            _hostStarted = true;
            IsHost = true;
            Promoted = new Promotion(oldHost, bots);
            _lobbyTimer ??= new Timer(_ => HostLobbyTick(), null, 1000, 1000);
            SetStatus($"hosting now (the host left): {_joiners.Count} players to hear from");
        }

        // new host: a device of this match says which seat it plays (it was told this device hosts now)
        private void OnRehost(BinaryReader r, int from)
        {
            int seat = r.ReadByte();
            lock (_lock)
            {
                Joiner? j = _joiners.FirstOrDefault(j => j.Seat == seat && !j.Left);
                if (j == null || !_hostStarted)
                {
                    return;
                }
                if (j.Slot != from)
                {
                    j.Slot = from;
                    ResetMatchState(j);
                    j.LastSeq = -1;
                    j.HasInput = false;
                }
                j.LastHeard = Environment.TickCount64;
            }
        }

        private void ResetClientMatchState()
        {
            _receiver.Reset();
            _ackTick = 0;
            _recentCount = 0;
        }

        // a joiner's input packets per 2 s window: the share of its sequence numbers that never came = loss; a lossy or
        // slow link gets 20 snapshots a second, a good one 30 (with a margin, so it doesn't flip back and forth)
        private static void CountPacket(Joiner j, int seq, long now)
        {
            if (j.WindowFirstSeq < 0)
            {
                j.WindowStart = now;
                j.WindowFirstSeq = seq;
                j.WindowPackets = 0;
            }
            j.WindowPackets++;
            double transit = now - seq * (1000 / 60.0);
            j.TransitMin = Math.Min(j.TransitMin, transit);
            j.TransitMax = Math.Max(j.TransitMax, transit);
            if (now - j.WindowStart < 2000)
            {
                return;
            }
            j.JitterFramesBefore = j.JitterFrames;
            j.JitterFrames = (int)Math.Ceiling((j.TransitMax - j.TransitMin) * 60 / 1000);
            j.TransitMin = Double.MaxValue;
            j.TransitMax = Double.MinValue;
            int span = seq - j.WindowFirstSeq + 1;
            j.Loss = span <= 0 ? 0 : Math.Clamp(1 - j.WindowPackets / (float)span, 0, 1);
            if (j.Loss > 0.08f || j.RttMs > 250)
            {
                j.Interval = SnapshotIntervalWeak;
            }
            else if (j.Loss < 0.03f && j.RttMs < 200)
            {
                j.Interval = SnapshotIntervalNormal;
            }
            j.WindowFirstSeq = -1;
        }

        // ---------------------------------------------------------------- format

        private static byte[] Message(byte type, int seat, MatchSettings settings)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, type);
            w.Write((byte)seat);
            LanSession.WriteSettings(w, settings);
            return ms.ToArray();
        }

        private static byte[] Kick(string reason)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgKick);
            w.Write(reason);
            return ms.ToArray();
        }

        // (protocol: an older app's own number, so it can read the REFUSE: that message is the same in every protocol)
        private static byte[] Refuse(string reason, byte protocol = Protocol)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgRefuse, protocol);
            w.Write(reason);
            return ms.ToArray();
        }

        private static void Header(BinaryWriter w, byte type, byte protocol = Protocol)
        {
            w.Write(type);
            w.Write(Magic);
            w.Write(protocol);
        }

        private static byte ReadHeader(BinaryReader r)
        {
            byte type = r.ReadByte();
            byte[] magic = r.ReadBytes(4);
            if (!magic.AsSpan().SequenceEqual(Magic))
            {
                throw new InvalidDataException("not a Second Hunt online packet");
            }
            byte protocol = r.ReadByte();
            if (protocol != Protocol)
            {
                throw new InvalidDataException($"online match protocol {protocol}, this app speaks {Protocol}: update the game");
            }
            return type;
        }

        private void SetStatus(string status) => Status = status;

        // ---------------------------------------------------------------- reconnect (release test T1, 2026-10-10)
        //   A joiner whose own connection drops mid-match (Wi-Fi off and on) keeps its match: the relay keeps its slot and token
        //   (the server holds the seat), RelayTransport makes its socket again once the network is back, and the host takes
        //   the seat back when its inputs come again. Meanwhile the driver waits up to RejoinHoldMs instead of ending the match,
        //   and the HUD says "reconnecting...". Once the link is back but the host is still quiet, the JOIN goes out again: the
        //   host answers a seat it knows with START and a full snapshot (OnJoin's rejoin), as for an app restart.

        // online client: this device's own link to the relay is up (OnlineMatch: RelayTransport.RelayAlive); null = up
        public Func<bool>? LinkAlive { get; set; }
        // client: this device lost its own connection mid-match and waits to get back in
        public bool Reconnecting { get; private set; }
        // client: times it got back into the running match that way
        public int Reconnects { get; private set; }
        // how long a joiner waits for its own connection to come back (the server holds a quiet seat 90 s, then gives it back
        // when the device sends again: 0.7.2; was 60 s, so a 90 s outage ended this device's match: release 0.2.0 o-wifi)
        public const long RejoinHoldMs = 120_000;
        private bool _cut;
        private long _lastReconnectJoin;

        // client, each match step (MatchNetDriver)
        public void ReconnectTick()
        {
            if (IsHost || !Started || _join == null || MatchOver)
            {
                Reconnecting = false;
                return;
            }
            long now = Environment.TickCount64;
            long silent = now - Interlocked.Read(ref _hostHeard);
            bool link = LinkAlive?.Invoke() ?? true;
            if (silent < 1000)
            {
                if (_cut)
                {
                    _cut = false;
                    Reconnects++;
                    SetStatus("reconnected");
                    Console.WriteLine($"online: reconnected to the match ({Reconnects})");
                }
                Reconnecting = false;
                return;
            }
            if (!link)
            {
                // (our own network: the host may be fine)
                if (!_cut)
                {
                    Console.WriteLine($"online: own link down (host heard {silent} ms ago): reconnecting");
                }
                _cut = true;
                Reconnecting = silent > 1500;
                if (Reconnecting)
                {
                    SetStatus("reconnecting...");
                }
                return;
            }
            if (_cut)
            {
                Reconnecting = true;
                if (now - _lastReconnectJoin > 500 && !HostLeft && !_rehosting)
                {
                    _lastReconnectJoin = now;
                    _net.Send(_hostSlot, _join);
                }
            }
        }

        // host: how often a joiner came back to the running match (O5 rejoin)
        public int Rejoins
        {
            get
            {
                lock (_lock)
                {
                    return _joiners.Sum(j => j.Rejoins);
                }
            }
        }

        // stops without a word, as a crash would (no BYE): the PC tests' rejoin
        public void Vanish()
        {
            _running = false;
            _lobbyTimer?.Dispose();
            _net.Received -= OnReceived;
        }

        public void Dispose()
        {
            if (!_running)
            {
                return;
            }
            ReadOnlySpan<byte> bye = stackalloc byte[] { MsgBye };
            for (int i = 0; i < 2; i++)
            {
                _net.SendAll(bye);
            }
            _running = false;
            _lobbyTimer?.Dispose();
            _net.Received -= OnReceived;
        }
    }
}
