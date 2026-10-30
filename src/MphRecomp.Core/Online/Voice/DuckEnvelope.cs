using System;

namespace MphRecomp.Online.Voice
{
    // The music gain that follows "somebody is being heard" (Thread C, C3, 2026-10-23; the fade itself is App/Audio/MusicDuck.cs).
    // A short burst doesn't duck (the voice has to be heard for OnsetMs first, and VoiceChat's Speaking already carries 250 ms
    // past the last sound), the gain holds down HoldMs after the voice stops so gaps between words don't pump the music, then
    // it comes back slower than it went down. Pure: the caller gives the time, so -ducktest drives it without a clock.
    public sealed class DuckEnvelope
    {
        public const float DuckGain = 0.3f;     // about -10 dB
        public const int OnsetMs = 350;         // heard this long before the fade starts
        public const int HoldMs = 900;          // stays down this long after the last sound
        public const int AttackMs = 250;        // full gain -> DuckGain
        public const int ReleaseMs = 900;       // DuckGain -> full gain

        private long _lastMs;
        private long _heardSince;               // 0 = not heard now
        private long _lastHeard;
        private bool _down;                     // the target is DuckGain
        private float _gain = 1;

        public float Gain => _gain;

        public float Step(long nowMs, bool heard, bool enabled)
        {
            long dt = _lastMs == 0 ? 0 : Math.Clamp(nowMs - _lastMs, 0, 60); // (a stalled timer steps on, it doesn't jump)
            _lastMs = nowMs;
            if (!enabled)
            {
                _heardSince = 0;
                _down = false;
            }
            else if (heard)
            {
                if (_heardSince == 0) _heardSince = nowMs;
                _lastHeard = nowMs;
                if (nowMs - _heardSince >= OnsetMs) _down = true;
            }
            else
            {
                _heardSince = 0;
                if (_down && nowMs - _lastHeard >= HoldMs) _down = false;
            }
            float span = 1 - DuckGain;
            if (_down) _gain = MathF.Max(DuckGain, _gain - span * dt / AttackMs);
            else _gain = MathF.Min(1, _gain + span * dt / ReleaseMs);
            return _gain;
        }
    }
}
