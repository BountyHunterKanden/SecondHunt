using System;
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

namespace MphRecomp.Multiplayer.Net
{
    // A match between devices on one Wi-Fi network (plain UDP; works the same over Wi-Fi Direct or a hotspot).
    //   host: Host(template, romKey, name) beacons on the network every second and waits for a JOIN; a joiner with the
    //         same ROM revision gets seat 1 and the full match settings (WELCOME), then the match starts on both.
    //   client: Browse(romKey) lists hosts from their beacons (or JoinAddress for a typed IP); Join(host, hunter).
    // In the match the host sends a Snapshot every frame and takes the newest NetInput; the client the reverse.
    // Receiving runs on a background thread; the game thread polls TryTakeInput / TryTakeSnapshot.
    public sealed class LanSession : IDisposable
    {
        public const int GamePort = 47770;
        public const int BeaconPort = 47771;
        private const byte Protocol = 3; // 2: snapshots carry the last hit (kill messages); 3: items
        private const byte MsgBeacon = 1, MsgJoin = 2, MsgWelcome = 3, MsgRefuse = 4, MsgInput = 5, MsgSnapshot = 6, MsgBye = 7;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MPHR");

        // the session an activity hands to the next (lobby in the launcher -> match in CampaignActivity)
        public static LanSession? Current { get; set; }

        public bool IsHost { get; }
        public string RomKey { get; }
        public MatchSettings? Settings { get; private set; } // host: the full match; client: MatchClient.ForSeat
        public int RemoteSeat { get; private set; } = 1; // host: the joiner's seat
        public string Status { get; private set; } = "";
        public event Action? Joined; // raised on the receive thread
        public event Action<string>? StatusChanged;

        private readonly UdpClient _socket;
        private UdpClient? _beaconSocket;
        private IPEndPoint? _peer;
        private readonly Thread _receiver;
        private Thread? _beaconThread;
        private volatile bool _running = true;
        private long _lastHeard = Environment.TickCount64;

        // host
        private MatchSettings? _template;
        private string _name = "";
        private readonly object _inputLock = new();
        private NetInput _latestInput;
        private bool _hasInput;
        private int _lastInputSeq = -1;
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
        public bool PeerAlive => _peer != null && Environment.TickCount64 - _lastHeard < 3000;
        // milliseconds since the other device was last heard (a BYE makes it huge)
        public long PeerSilentMs => Environment.TickCount64 - _lastHeard;
        public IReadOnlyList<LanHostInfo> Hosts => _hosts.Values.Where(h => Environment.TickCount64 - h.LastSeen < 4000)
            .OrderBy(h => h.Name).ToList();

        private LanSession(bool host, string romKey, int port)
        {
            IsHost = host;
            RomKey = romKey;
            _socket = new UdpClient(AddressFamily.InterNetwork);
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _socket.EnableBroadcast = true;
            _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "MphLanReceive" };
            _receiver.Start();
        }

        // ---------------------------------------------------------------- host

        // template: the match as set up on this device (seat 0 = this player, then bots); the joiner is inserted as
        // seat 1 (the last bot gives way if all four seats are taken).
        public static LanSession Host(MatchSettings template, string romKey, string name)
        {
            LanSession s;
            try
            {
                s = new LanSession(host: true, romKey, GamePort);
            }
            catch (SocketException)
            {
                s = new LanSession(host: true, romKey, 0); // the fixed port is taken: any port (beacons carry it)
            }
            s._template = template;
            s._name = name;
            s._beaconSocket = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            s._beaconThread = new Thread(s.BeaconLoop) { IsBackground = true, Name = "MphLanBeacon" };
            s._beaconThread.Start();
            s.SetStatus($"hosting \"{name}\" on {LocalAddress()}:{s.Port} -- waiting for a player");
            return s;
        }

        public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

        private void BeaconLoop()
        {
            var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, BeaconPort) };
            targets.AddRange(DirectedBroadcasts().Select(a => new IPEndPoint(a, BeaconPort)));
            while (_running && _peer == null)
            {
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
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgBeacon);
            w.Write((ushort)Port);
            w.Write(_name);
            w.Write(RomKey);
            w.Write((byte)_template!.Mode);
            w.Write(_template.Arena);
            w.Write((byte)_template.Players.Count);
            return ms.ToArray();
        }

        private void OnJoin(BinaryReader r, IPEndPoint from)
        {
            string romKey = r.ReadString();
            var hunter = (Hunter)r.ReadByte();
            string nickname = r.ReadString();
            if (_peer != null && !_peer.Equals(from))
            {
                Send(Refuse("this match is already full"), from);
                return;
            }
            if (romKey != RomKey)
            {
                Send(Refuse($"ROM revision differs (host {RomKey}, you {romKey}): both players need the same one"), from);
                SetStatus($"refused {from.Address}: ROM {romKey} vs {RomKey}");
                return;
            }
            bool first = _peer == null;
            _peer = from;
            _lastHeard = Environment.TickCount64;
            if (first)
            {
                Settings = WithJoiner(_template!, hunter, nickname);
            }
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgWelcome);
            w.Write((byte)RemoteSeat);
            WriteSettings(w, Settings!);
            Send(ms.ToArray(), from);
            if (first)
            {
                SetStatus($"{nickname} ({hunter}) joined from {from.Address}");
                Joined?.Invoke();
            }
        }

        private MatchSettings WithJoiner(MatchSettings template, Hunter hunter, string nickname)
        {
            var s = new MatchSettings
            {
                Mode = template.Mode, Arena = template.Arena, PointGoal = template.PointGoal,
                TimeLimitSeconds = template.TimeLimitSeconds, TimeGoalSeconds = template.TimeGoalSeconds,
                DamageLevel = template.DamageLevel, FriendlyFire = template.FriendlyFire,
                OctolithReset = template.OctolithReset, RadarPlayers = template.RadarPlayers,
                AffinityWeapons = template.AffinityWeapons
            };
            s.Players.AddRange(template.Players);
            var joiner = new MatchPlayer { Hunter = hunter, Remote = true, Nickname = nickname };
            if (s.Players.Any(p => p.Hunter == hunter))
            {
                joiner.Recolor = 1; // the same hunter twice: tell them apart
            }
            s.Players.Insert(1, joiner);
            while (s.Players.Count > 4)
            {
                s.Players.RemoveAt(s.Players.Count - 1);
            }
            RemoteSeat = 1;
            return s;
        }

        private byte[] Refuse(string reason)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgRefuse);
            w.Write(reason);
            return ms.ToArray();
        }

        public bool TryTakeInput(out NetInput input)
        {
            lock (_inputLock)
            {
                input = _latestInput;
                bool had = _hasInput;
                _hasInput = false;
                return had;
            }
        }

        public void SendSnapshot(Snapshot snap)
        {
            if (_peer == null)
            {
                return;
            }
            _sendBuffer[0] = MsgSnapshot;
            int size = 1 + snap.Write(_sendBuffer.AsSpan(1));
            Send(_sendBuffer, size, _peer);
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

        // asks the host for a seat (retries for a few seconds); true once the match settings arrived
        public bool Join(IPEndPoint host, Hunter hunter, string nickname, int timeoutMs = 5000)
        {
            _peer = host;
            SetStatus($"joining {host}...");
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Header(w, MsgJoin);
            w.Write(RomKey);
            w.Write((byte)hunter);
            w.Write(nickname);
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
            // the in-match messages are raw (one type byte) to keep them small; the rest carry the MPHR header
            if (data[0] == MsgInput && IsHost)
            {
                if (_peer == null || !_peer.Equals(from))
                {
                    return;
                }
                _lastHeard = Environment.TickCount64;
                NetInput input = NetInput.Read(data.AsSpan(1));
                lock (_inputLock)
                {
                    // newest wins (UDP may reorder); a wrapped 16-bit sequence counts as newer
                    if (_lastInputSeq < 0 || (short)(input.Seq - _lastInputSeq) > 0)
                    {
                        _lastInputSeq = input.Seq;
                        _latestInput = input;
                        _hasInput = true;
                    }
                }
                Interlocked.Increment(ref _inputsReceived);
                return;
            }
            if (data[0] == MsgSnapshot && !IsHost)
            {
                _lastHeard = Environment.TickCount64;
                Snapshot snap = Snapshot.Read(data.AsSpan(1));
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
                if (_peer != null && _peer.Equals(from))
                {
                    SetStatus("the other player left");
                    _lastHeard = 0;
                }
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
                _lastHeard = Environment.TickCount64;
                Joined?.Invoke();
            }
            else if (type == MsgRefuse && !IsHost)
            {
                _refused = r.ReadString();
            }
        }

        private void Send(byte[] data, IPEndPoint to) => Send(data, data.Length, to);

        private void Send(byte[] data, int length, IPEndPoint to)
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

        private static void Header(BinaryWriter w, byte type)
        {
            w.Write(type);
            w.Write(Magic);
            w.Write(Protocol);
        }

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

        private static void WriteSettings(BinaryWriter w, MatchSettings s)
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
            }
        }

        private static MatchSettings ReadSettings(BinaryReader r)
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
                s.Players.Add(p);
            }
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
            _running = false;
            _socket.Dispose();
            _beaconSocket?.Dispose();
            if (Current == this)
            {
                Current = null;
            }
        }
    }

    public sealed class LanHostInfo
    {
        public IPEndPoint Address = new(IPAddress.None, 0);
        public string Name = "";
        public string RomKey = "";
        public GameMode Mode;
        public string Arena = "";
        public int Seats;
        public long LastSeen;
        public string Label => $"{Name}: {Mode} in {MatchArenas.Find(Arena)?.InGameName ?? Arena} ({Address.Address}, ROM {RomKey})";
    }
}
