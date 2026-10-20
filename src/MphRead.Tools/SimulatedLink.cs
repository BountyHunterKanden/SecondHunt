using System;
using System.Threading;
using System.Threading.Tasks;
using MphRecomp.Multiplayer.Net;

namespace MphRead
{
    // -onlinetest loss= jitter= (O5): a bad internet link on this device's side of the relay. Every packet this device
    // sends is dropped with probability loss, and the rest wait 0..jitter ms (uniform, so they also arrive out of order).
    // The relay's own delay= is on top. Keepalives (the relay ping) are the RelayTransport's and aren't touched.
    // lag=<ms> (the device test, external=1): a round trip added on this device only, half on its sends and half on what
    // it receives, so a phone joining a PC host over the live server sees that much more ping than its real one.
    // burst=<ms> (2026-10-19, match B3H0BA: a phone on mobile data whose inputs reached the host in bunches): about
    // once a second (at random) the link stalls for 1/2-1 of that, then lets everything held go together, in order.
    internal sealed class SimulatedLink : IDatagramTransport
    {
        private readonly IDatagramTransport _inner;
        private readonly float _loss;
        private readonly int _jitterMs;
        private readonly int _sendLagMs, _receiveLagMs;
        private readonly int _burstMs;
        private long _release;
        private readonly Random _random;
        private readonly object _lock = new();
        private readonly object _deliver = new();
        private DatagramHandler? _received;
        public int Dropped, Sent;

        public SimulatedLink(IDatagramTransport inner, float loss, int jitterMs, int seed, int lagMs = 0, int burstMs = 0)
        {
            _burstMs = burstMs;
            _inner = inner;
            _loss = loss;
            _jitterMs = jitterMs;
            _sendLagMs = lagMs / 2;
            _receiveLagMs = lagMs - lagMs / 2;
            _random = new Random(seed);
            if (_receiveLagMs > 0)
            {
                _inner.Received += Late;
            }
        }

        public int LocalSlot => _inner.LocalSlot;
        public int MaxPayload => _inner.MaxPayload;

        public event DatagramHandler? Received
        {
            add
            {
                if (_receiveLagMs > 0)
                {
                    _received += value;
                }
                else
                {
                    _inner.Received += value;
                }
            }
            remove
            {
                if (_receiveLagMs > 0)
                {
                    _received -= value;
                }
                else
                {
                    _inner.Received -= value;
                }
            }
        }

        // lag=: deliveries wait too, one at a time (the session never gets two receives at once, as from the transport)
        private void Late(int from, ReadOnlySpan<byte> payload)
        {
            byte[] copy = payload.ToArray();
            _ = Task.Delay(_receiveLagMs).ContinueWith(_ =>
            {
                lock (_deliver)
                {
                    _received?.Invoke(from, copy);
                }
            });
        }

        public void Send(int slot, ReadOnlySpan<byte> payload) => Pass(payload, p => _inner.Send(slot, p));

        public void SendAll(ReadOnlySpan<byte> payload) => Pass(payload, p => _inner.SendAll(p));

        private void Pass(ReadOnlySpan<byte> payload, Action<byte[]> send)
        {
            int wait;
            lock (_lock)
            {
                Sent++;
                if (_random.NextDouble() < _loss)
                {
                    Dropped++;
                    return;
                }
                wait = _sendLagMs + (_jitterMs > 0 ? _random.Next(_jitterMs + 1) : 0);
                if (_burstMs > 0)
                {
                    long now = Environment.TickCount64;
                    if (now >= _release && _random.Next(60) == 0)
                    {
                        _release = now + _burstMs / 2 + _random.Next(_burstMs / 2 + 1);
                    }
                    wait += (int)Math.Max(0, _release - now);
                }
            }
            byte[] copy = payload.ToArray();
            if (wait == 0)
            {
                send(copy);
                return;
            }
            _ = Task.Delay(wait).ContinueWith(_ => send(copy));
        }

        public void Dispose()
        {
            if (_receiveLagMs > 0)
            {
                _inner.Received -= Late;
            }
            _inner.Dispose();
        }
    }
}
