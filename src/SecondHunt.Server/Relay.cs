using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // The UDP match relay (RelayWire): rooms of up to 8 slots, one per hosted game. A device's token says which room
    // and slot it is; the relay learns its address from its packets (so it works behind any home router) and forwards
    // to one slot or to everyone else in the room (a host sends each snapshot once, the relay fans it out). Payloads are
    // never read. A room lives while its game is open, then 30 s more (for the last BYEs). A room that isn't Kept (a
    // started match, or one whose host went away) also closes after 60 s without traffic, and Expired says so.
    public sealed class Relay : IDisposable
    {
        private const int PacketsPerSecond = 500;
        private const int BytesPerSecond = 256 * 1024;
        private static readonly long IdleMs = 60_000;
        private static readonly long CloseGraceMs = 30_000;

        public sealed class Room
        {
            public string Id { get; }
            public readonly Member?[] Slots = new Member?[RelayWire.MaxSlots];
            public long LastActive = Environment.TickCount64;
            public long CloseAt = Int64.MaxValue;
            // kept open without traffic (a lobby whose host is online); see Relay.Keep
            public volatile bool Kept = true;

            public Room(string id)
            {
                Id = id;
            }
        }

        public sealed class Member
        {
            public Room Room { get; }
            public int Slot { get; }
            public ulong Token { get; }
            public volatile IPEndPoint? EndPoint;
            // its last packet (or when it got its token): a seat whose device stopped sending is "held" (Hub.HeldGame)
            public long LastSeen = Environment.TickCount64;
            public long WindowStart;
            public int WindowPackets;
            public int WindowBytes;
            // the last time a member out of its slot asked to come back (Returned), at most once a second
            public long ReturnAsked;

            public Member(Room room, int slot, ulong token)
            {
                Room = room;
                Slot = slot;
                Token = token;
            }
        }

        private readonly UdpClient _socket;
        private readonly ConcurrentDictionary<ulong, Member> _members = new();
        private readonly ConcurrentDictionary<string, Room> _rooms = new();
        private readonly Thread _receiver;
        private readonly Timer _sweeper;
        private volatile bool _running = true;
        private long _forwarded;

        public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        public long Forwarded => Interlocked.Read(ref _forwarded);
        // test hook: extra one-way delay added to every forwarded packet (simulated distance), 0 = none
        public int DelayMs { get; set; }
        // a room closed for lack of traffic (not by CloseRoom); the hub closes its game
        public event Action<Room>? Expired;
        // a member taken out of its slot (RemoveMember) sends again while its room is open: true = the hub gave it the slot
        // back (RestoreMember). Without it the device was one-way: the host still heard it, nothing reached it (release
        // 0.2.0 F1: a seat whose sends were lost past the hub's seat hold came back invisible, then ended its match)
        public Func<Member, bool>? Returned;

        public Relay(int port)
        {
            _socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            if (OperatingSystem.IsWindows())
            {
                // don't let an ICMP "port unreachable" from a device that left kill the receive loop
                const int SioUdpConnreset = -1744830452;
                _socket.Client.IOControl(SioUdpConnreset, new byte[] { 0 }, null);
            }
            _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "Relay" };
            _receiver.Start();
            _sweeper = new Timer(_ => Sweep(), null, 5000, 5000);
        }

        public Room OpenRoom(string id) => _rooms.GetOrAdd(id, i => new Room(i));

        // a new token for this slot of the room (replacing an older one for the slot)
        public Member AddMember(Room room, int slot)
        {
            lock (room)
            {
                // drop every older token for this slot, also one that left (its late packets would look like the new one's)
                foreach (Member old in _members.Values)
                {
                    if (old.Room == room && old.Slot == slot)
                    {
                        _members.TryRemove(old.Token, out _);
                    }
                }
                ulong token;
                do
                {
                    token = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
                }
                while (token == 0 || _members.ContainsKey(token));
                var member = new Member(room, slot, token);
                room.Slots[slot] = member;
                _members[token] = member;
                room.LastActive = Environment.TickCount64;
                return member;
            }
        }

        public void RemoveMember(Room room, int slot)
        {
            lock (room)
            {
                if (room.Slots[slot] is Member m)
                {
                    room.Slots[slot] = null;
                    // the token keeps working until the room closes, so a leaving device's BYE still gets through
                    m.WindowStart = 0;
                }
            }
        }

        // a member taken out of its slot gets it back, if nobody else has it and its token still works
        public bool RestoreMember(Member m)
        {
            lock (m.Room)
            {
                if (m.Room.Slots[m.Slot] != null || !_members.TryGetValue(m.Token, out Member? same) || same != m
                    || m.Room.CloseAt != Int64.MaxValue)
                {
                    return false;
                }
                m.Room.Slots[m.Slot] = m;
                Interlocked.Exchange(ref m.LastSeen, Environment.TickCount64);
                return true;
            }
        }

        // how long this slot of the room has been silent (no member = forever)
        public long QuietMs(Room room, int slot) =>
            room.Slots[slot] is Member m ? Environment.TickCount64 - Interlocked.Read(ref m.LastSeen) : Int64.MaxValue / 2;

        public void CloseRoom(Room room) => room.CloseAt = Environment.TickCount64 + CloseGraceMs;

        // kept: no idle expiry (lobby); not kept: expires after IdleMs without traffic, counted from now
        public void Keep(Room room, bool kept)
        {
            room.LastActive = Environment.TickCount64;
            room.Kept = kept;
        }

        private void Sweep()
        {
            long now = Environment.TickCount64;
            foreach (Room room in _rooms.Values)
            {
                bool idle = !room.Kept && now - room.LastActive > IdleMs;
                if (now > room.CloseAt || idle)
                {
                    _rooms.TryRemove(room.Id, out _);
                    if (idle && room.CloseAt == Int64.MaxValue)
                    {
                        Expired?.Invoke(room);
                    }
                    foreach (Member m in _members.Values)
                    {
                        if (m.Room == room)
                        {
                            _members.TryRemove(m.Token, out _);
                        }
                    }
                }
            }
        }

        private void ReceiveLoop()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] outBuffer = new byte[RelayWire.ReceiveHeader + RelayWire.MaxPayload];
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
                    continue;
                }
                if (data.Length < RelayWire.SendHeader || data[0] != RelayWire.Magic
                    || data.Length > RelayWire.SendHeader + RelayWire.MaxPayload)
                {
                    continue;
                }
                ulong token = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(1));
                if (!_members.TryGetValue(token, out Member? sender))
                {
                    continue; // not a member of an open room: dropped without an answer
                }
                long now = Environment.TickCount64;
                if (sender.Room.Slots[sender.Slot] == null && sender.Room.CloseAt == Int64.MaxValue
                    && now - sender.ReturnAsked >= 1000 && Returned != null)
                {
                    sender.ReturnAsked = now;
                    Returned(sender); // (else it sends on as before: a leaving device's BYE still gets through)
                }
                if (now - sender.WindowStart >= 1000)
                {
                    sender.WindowStart = now;
                    sender.WindowPackets = 0;
                    sender.WindowBytes = 0;
                }
                if (++sender.WindowPackets > PacketsPerSecond || (sender.WindowBytes += data.Length) > BytesPerSecond)
                {
                    continue;
                }
                if (sender.EndPoint == null || !sender.EndPoint.Equals(from))
                {
                    sender.EndPoint = new IPEndPoint(from.Address, from.Port);
                }
                Room room = sender.Room;
                room.LastActive = now;
                Interlocked.Exchange(ref sender.LastSeen, now);
                byte to = data[9];
                if (to == RelayWire.KeepAlive)
                {
                    Send(new[] { RelayWire.Magic, RelayWire.KeepAlive }, 2, sender.EndPoint);
                    continue;
                }
                int payload = data.Length - RelayWire.SendHeader;
                byte[] packet = DelayMs > 0 ? new byte[RelayWire.ReceiveHeader + payload] : outBuffer;
                packet[0] = RelayWire.Magic;
                packet[1] = (byte)sender.Slot;
                Buffer.BlockCopy(data, RelayWire.SendHeader, packet, RelayWire.ReceiveHeader, payload);
                int length = RelayWire.ReceiveHeader + payload;
                for (int slot = 0; slot < RelayWire.MaxSlots; slot++)
                {
                    if (slot == sender.Slot || (to != RelayWire.ToAll && to != slot))
                    {
                        continue;
                    }
                    IPEndPoint? target = room.Slots[slot]?.EndPoint;
                    if (target != null)
                    {
                        Forward(packet, length, target);
                    }
                }
            }
        }

        private void Forward(byte[] packet, int length, IPEndPoint target)
        {
            Interlocked.Increment(ref _forwarded);
            int delay = DelayMs;
            if (delay <= 0)
            {
                Send(packet, length, target);
                return;
            }
            _ = System.Threading.Tasks.Task.Delay(delay).ContinueWith(_ => Send(packet, length, target));
        }

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

        public void Dispose()
        {
            _running = false;
            _sweeper.Dispose();
            _socket.Dispose();
        }
    }
}
