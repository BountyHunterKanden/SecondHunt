using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;

namespace MphRecomp.Multiplayer.Net
{
    // O5 relays (owner decision 18): Cloudflare Realtime TURN first, our Chicago relay as the fallback. The relay
    // (RelayTransport) always runs: lobby messages, SendAll and every peer whose TURN path isn't up go through it. Each device
    // also allocates on TURN (TurnClient) and tells the peers it hears from its relayed address over the relay (OFFER); both
    // sides bind a channel to the other's relayed address and PROBE over TURN; a peer's sends switch to TURN once a probe was
    // answered over TURN (both directions work), and back to the relay when TURN goes quiet for SilentMs (or the allocation
    // fails). The other side switches on its own; packets are taken from either path at any time, so a switch loses nothing.
    // Only relayed addresses travel (never a device's own address). The match session above sees one transport.
    public sealed class HybridTransport : IDatagramTransport
    {
        // control messages (first byte; no match message starts with it): OFFER relayed address, PROBE / ACK nonce
        public const byte Control = 0xE7;
        private const byte Offer = 1, Probe = 2, Ack = 3;
        private const int OfferEveryMs = 1000, ProbeEveryMs = 250, KeepAliveMs = 500, SilentMs = 1500;

        private sealed class Peer
        {
            public int Slot;
            public IPEndPoint? Relayed; // its TURN relayed address (from its OFFER)
            public bool Up; // sends go over TURN
            public bool TheyKnowUs; // it answered a probe (or its probe reached us over TURN)
            public long LastOffer, LastProbe, LastTurnHeard;
            // our last probe answered over TURN: staying up needs these (hearing its probes alone says nothing about
            // our sends reaching it; after a network change a device can still hear TURN while its sends are lost)
            public long LastAck;
            public uint Nonce;
            public int Switches, Fallbacks;
        }

        private readonly IDatagramTransport _relay;
        private volatile TurnClient? _turn; // (set once: null until Attach)
        private readonly object _lock = new();
        private readonly object _deliver = new();
        private readonly Dictionary<int, Peer> _peers = new();
        private readonly Dictionary<IPEndPoint, Peer> _byAddress = new();
        private readonly Timer _timer;
        private long _turnPackets, _relayPackets;
        private volatile bool _running = true;

        public int LocalSlot => _relay.LocalSlot;
        public int MaxPayload => _relay.MaxPayload;
        public event DatagramHandler? Received;
        public TurnClient? Turn => _turn;
        // match packets sent over TURN / over the relay
        public long TurnPackets => Interlocked.Read(ref _turnPackets);
        public long RelayPackets => Interlocked.Read(ref _relayPackets);
        // times a peer went back to the relay (TURN went quiet or failed)
        public int Fallbacks
        {
            get
            {
                lock (_lock)
                {
                    return _peers.Values.Sum(p => p.Fallbacks);
                }
            }
        }

        // turn = an allocated TurnClient (null = relay only until Attach, as before)
        public HybridTransport(IDatagramTransport relay, TurnClient? turn = null)
        {
            _relay = relay;
            _relay.Received += OnRelay;
            if (turn != null)
            {
                Attach(turn);
            }
            _timer = new Timer(_ => Tick(), null, 100, 100);
        }

        // TURN arrives later (credentials from the server, then the allocation: the match doesn't wait for it); false (and the
        // client disposed) when it isn't usable or one is already attached
        public bool Attach(TurnClient turn)
        {
            lock (_lock)
            {
                if (_running && _turn == null && turn.Relayed != null && !turn.Failed)
                {
                    turn.Received += OnTurn;
                    _turn = turn;
                    return true;
                }
            }
            turn.Dispose();
            return false;
        }

        public bool IsUp(int slot)
        {
            lock (_lock)
            {
                return _peers.TryGetValue(slot, out Peer? p) && p.Up;
            }
        }

        public void Send(int slot, ReadOnlySpan<byte> payload)
        {
            IPEndPoint? via = null;
            lock (_lock)
            {
                if (_turn != null && _peers.TryGetValue(slot, out Peer? p) && p.Up)
                {
                    via = p.Relayed;
                }
            }
            if (via != null && _turn!.Send(via, payload))
            {
                Interlocked.Increment(ref _turnPackets);
                return;
            }
            Interlocked.Increment(ref _relayPackets);
            _relay.Send(slot, payload);
        }

        // (lobby, BYE: rare; the relay reaches everyone)
        public void SendAll(ReadOnlySpan<byte> payload)
        {
            Interlocked.Increment(ref _relayPackets);
            _relay.SendAll(payload);
        }

        private Peer PeerFor(int slot)
        {
            if (!_peers.TryGetValue(slot, out Peer? p))
            {
                p = new Peer { Slot = slot, Nonce = (uint)Random.Shared.Next() };
                _peers[slot] = p;
            }
            return p;
        }

        private void OnRelay(int from, ReadOnlySpan<byte> data)
        {
            if (data.Length > 0 && data[0] == Control)
            {
                if (data.Length >= 8 && data[1] == Offer && _turn != null)
                {
                    var address = new IPEndPoint(new IPAddress(data.Slice(2, 4)), BinaryPrimitives.ReadUInt16BigEndian(data[6..]));
                    bool bind = false;
                    lock (_lock)
                    {
                        Peer p = PeerFor(from);
                        if (p.Relayed == null || !p.Relayed.Equals(address))
                        {
                            if (p.Relayed != null)
                            {
                                _byAddress.Remove(p.Relayed);
                            }
                            p.Relayed = address;
                            p.Up = false;
                            p.TheyKnowUs = false; // (a restarted app: a new allocation that doesn't know ours yet)
                            _byAddress[address] = p;
                            bind = true;
                        }
                    }
                    if (bind)
                    {
                        _turn.Bind(address);
                    }
                }
                return;
            }
            lock (_lock)
            {
                PeerFor(from); // a peer to offer TURN to (once there's TURN)
            }
            Deliver(from, data);
        }

        private void OnTurn(IPEndPoint from, ReadOnlySpan<byte> data)
        {
            Peer? p;
            lock (_lock)
            {
                if (!_byAddress.TryGetValue(from, out p))
                {
                    return;
                }
                p.LastTurnHeard = Environment.TickCount64;
            }
            if (data.Length > 0 && data[0] == Control)
            {
                if (data.Length >= 6 && data[1] == Probe)
                {
                    lock (_lock)
                    {
                        p.TheyKnowUs = true;
                    }
                    byte[] ack = data[..6].ToArray();
                    ack[1] = Ack;
                    _turn!.Send(from, ack);
                }
                else if (data.Length >= 6 && data[1] == Ack && BinaryPrimitives.ReadUInt32BigEndian(data[2..]) == p.Nonce)
                {
                    lock (_lock)
                    {
                        p.TheyKnowUs = true;
                        p.LastAck = p.LastTurnHeard;
                        if (!p.Up)
                        {
                            p.Up = true;
                            p.Switches++;
                            Console.WriteLine($"TURN: slot {p.Slot} up (switch {p.Switches})");
                        }
                    }
                }
                return;
            }
            Deliver(p.Slot, data);
        }

        // one receive path at a time into the session (the relay and TURN have their own receive threads)
        private void Deliver(int from, ReadOnlySpan<byte> data)
        {
            lock (_deliver)
            {
                Received?.Invoke(from, data);
            }
        }

        private void Tick()
        {
            TurnClient? turn = _turn;
            if (!_running || turn == null)
            {
                return;
            }
            long now = Environment.TickCount64;
            var offers = new List<int>();
            var probes = new List<(IPEndPoint, uint)>();
            lock (_lock)
            {
                foreach (Peer p in _peers.Values)
                {
                    if (_turn.Failed)
                    {
                        if (p.Up)
                        {
                            p.Up = false;
                            p.Fallbacks++;
                            Console.WriteLine($"TURN: slot {p.Slot} back to the relay (TURN failed: {_turn.Error})");
                        }
                        continue;
                    }
                    if (p.Up && now - p.LastAck > SilentMs)
                    {
                        p.Up = false; // our keepalive probes unanswered: back to the relay (and probing again)
                        p.Fallbacks++;
                        Console.WriteLine($"TURN: slot {p.Slot} back to the relay (probes unanswered {now - p.LastAck} ms, heard {now - p.LastTurnHeard} ms ago)");
                    }
                    // OFFER until it's seen ours over TURN (then keep one a few seconds apart, for a restarted app)
                    if (now - p.LastOffer > (p.TheyKnowUs ? 5000 : OfferEveryMs))
                    {
                        p.LastOffer = now;
                        offers.Add(p.Slot);
                    }
                    if (p.Relayed != null && _turn.IsBound(p.Relayed) && now - p.LastProbe > (p.Up ? KeepAliveMs : ProbeEveryMs))
                    {
                        p.LastProbe = now;
                        probes.Add((p.Relayed, p.Nonce));
                    }
                }
            }
            if (offers.Count > 0 && _turn.Relayed is IPEndPoint mine)
            {
                var offer = new byte[8];
                offer[0] = Control;
                offer[1] = Offer;
                mine.Address.MapToIPv4().GetAddressBytes().CopyTo(offer, 2);
                BinaryPrimitives.WriteUInt16BigEndian(offer.AsSpan(6), (ushort)mine.Port);
                foreach (int slot in offers)
                {
                    _relay.Send(slot, offer);
                }
            }
            var probe = new byte[6];
            probe[0] = Control;
            probe[1] = Probe;
            foreach ((IPEndPoint to, uint nonce) in probes)
            {
                BinaryPrimitives.WriteUInt32BigEndian(probe.AsSpan(2), nonce);
                _turn.Send(to, probe);
            }
        }

        // e.g. "TURN slot 0 up (1 switch), relay 12 / TURN 3400 packets, 210 KB from TURN"
        public string Summary()
        {
            if (_turn == null)
            {
                return "relay only";
            }
            lock (_lock)
            {
                return "TURN " + (_peers.Count == 0 ? "no peers" : string.Join(", ", _peers.Values.OrderBy(p => p.Slot).Select(p =>
                    $"slot {p.Slot} {(p.Up ? "up" : p.Relayed == null ? "no offer" : "down")}"
                    + (p.Switches > 0 || p.Fallbacks > 0 ? $" ({p.Switches} up, {p.Fallbacks} back to relay)" : ""))))
                    + $"; packets relay {RelayPackets} / TURN {TurnPackets}; {_turn.BytesIn / 1024} KB from TURN"
                    + (_turn.Failed ? $"; TURN failed: {_turn.Error}" : "");
            }
        }

        public void Dispose()
        {
            TurnClient? turn;
            lock (_lock)
            {
                if (!_running)
                {
                    return;
                }
                _running = false; // (no Attach after this)
                turn = _turn;
            }
            _timer.Dispose();
            _relay.Received -= OnRelay;
            if (turn != null)
            {
                turn.Received -= OnTurn;
                turn.Dispose();
            }
            _relay.Dispose();
        }
    }
}
