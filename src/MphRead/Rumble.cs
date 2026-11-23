using System;
using System.Diagnostics;

namespace MphRead
{
    // recomp: the game's Rumble Pak wrapper (ARM9 0x205d03c set, 0x205d1c4 30 Hz update, 0x205d178 pause).
    // The game only keeps a strength (0-100) and a duration in 30 Hz ticks; the pak driver turns the strength
    // into a pulse rate. Hosts give us a motor via Output. With no Output (tools, tests, replay checks) this
    // does nothing at all, and it never touches game state, so matches stay byte-identical either way.
    public interface IRumbleOutput
    {
        // strength 1-100, ms > 0
        void Start(int strength, int ms);
        void Stop();
    }

    public static class Rumble
    {
        public static IRumbleOutput? Output { get; set; }

        // the RUMBLE PAK option (game options bit at 0x020e8318+0x10 bit 7, off by default)
        public static bool Enabled { get; set; }

        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static int _strength = 0;
        private static long _endMs = 0;
        private static bool _paused = false;
        // recomp: a longer, weaker rumble that a stronger one cut in on (the death buzz under the killing hit's):
        // it carries on for what is left of its time once the stronger one ends (Tick). The DS wrapper keeps one
        // request only, so there the hit's short buzz ended the death's long one (owner 2026-11-18: no long buzz)
        private static int _heldStrength = 0;
        private static long _heldEndMs = 0;

        private static int RemainingMs => _strength == 0 ? 0 : (int)Math.Max(0, _endMs - _clock.ElapsedMilliseconds);

        // 0x205d03c: strength -1 stops; while a stronger rumble is still running, a weaker one is dropped
        public static void Start(int strength, int ticks)
        {
            IRumbleOutput? output = Output;
            if (output == null || !Enabled)
            {
                return;
            }
            if (strength < 0)
            {
                Stop();
                return;
            }
            long now = _clock.ElapsedMilliseconds;
            long end = now + ticks * 1000L / 30;
            if (ticks > 0 && RemainingMs > 0 && _strength > strength)
            {
                // (dropped, as on the DS; kept for later if it outlasts the one running)
                if (end > _endMs && end > _heldEndMs)
                {
                    _heldStrength = Math.Min(strength, 100);
                    _heldEndMs = end;
                }
                return;
            }
            if (ticks > 0 && RemainingMs > 0 && _strength > 0 && _endMs > end && _endMs > _heldEndMs)
            {
                _heldStrength = _strength; // the running one is weaker (or equal) and longer: it waits
                _heldEndMs = _endMs;
            }
            _strength = Math.Min(strength, 100);
            _endMs = end;
            if (_strength == 0 || ticks <= 0)
            {
                Stop();
                return;
            }
            if (!_paused)
            {
                output.Start(_strength, RemainingMs);
            }
        }

        public static void Stop()
        {
            _strength = 0;
            _endMs = 0;
            _heldStrength = 0;
            _heldEndMs = 0;
            Output?.Stop();
        }

        // recomp: once a frame (the viewer's player): the waiting rumble starts when the one that cut in has ended
        public static void Tick()
        {
            if (_heldStrength == 0 || RemainingMs > 0)
            {
                return;
            }
            int strength = _heldStrength;
            int ms = (int)(_heldEndMs - _clock.ElapsedMilliseconds);
            _heldStrength = 0;
            _heldEndMs = 0;
            IRumbleOutput? output = Output;
            if (ms <= 0 || output == null || !Enabled)
            {
                return;
            }
            _strength = strength;
            _endMs = _clock.ElapsedMilliseconds + ms;
            if (!_paused)
            {
                output.Start(_strength, ms);
            }
        }

        // 1P death (0x2017f3c): the game asks for (15, 110). On the pak, 15 is a slow rattle you can feel; a motor at
        // 15% of its amplitude is not, so the motor gets DeathStrength for the same 110 ticks
        public const int DeathStrength = 40;

        public static void Death() => Start(DeathStrength, 110);

        // 0x205d178: the motor stops while paused (the timer keeps running) and picks up again on unpause.
        // The game calls it only on lid close (ARM9 0x2029860), not from the pause menu, so hosts call it when
        // the app goes to the background.
        public static void SetPaused(bool paused)
        {
            if (paused == _paused)
            {
                return;
            }
            _paused = paused;
            IRumbleOutput? output = Output;
            if (output == null)
            {
                return;
            }
            if (paused)
            {
                output.Stop();
            }
            else if (Enabled && RemainingMs > 0)
            {
                output.Start(_strength, RemainingMs);
            }
        }

        // damage rumble (0x20195f0), main player only
        public static void Damage(uint damage)
        {
            if (damage > 50)
            {
                Start(100, 10);
            }
            else
            {
                Start((int)(7 + damage * 93 / 50), (int)(3 + damage * 7 / 50));
            }
        }
    }
}
