using System;

namespace MphRecomp.Online.Voice
{
    // The music gain that follows "somebody is being heard" (Thread C, C3, 2026-10-23; the fade itself is App/Audio/MusicDuck.cs).
    // A short burst doesn't duck (the voice has to be heard for OnsetMs first, and VoiceChat's Speaking already carries 250 ms
    // past the last sound), the gain holds down HoldMs after the voice stops so gaps between words don't pump the music, then
    // it comes back slower than it went down. Pure: the caller gives the time, so -ducktest drives it without a clock.
    public sealed class DuckEnvelope
    {
        public const float DuckGain = 0.15f;    // about -16 dB (owner 2026-10-31 "should probably be more intense"; was 0.3, -10 dB)
        public const int OnsetMs = 350;         // heard this long before the fade starts
        public const int HoldMs = 900;          // stays down this long after the last sound
        public const int AttackMs = 250;        // full gain -> DuckGain
        public const int ReleaseMs = 900;       // DuckGain -> full gain

        // your own push to talk on a loudspeaker (2026-10-31, owner: "the music should also duck significantly when you
        // yourself are pushing to talk too so players dont get your loud music"): deeper, no onset (the first word must
        // already go out without the music), quick down, short hold, then back
        public const float SelfDuckGain = 0.08f; // about -22 dB
        public const int SelfHoldMs = 250;
        public const int SelfAttackMs = 80;
        public const int SelfReleaseMs = 600;

        private readonly float _duckGain;
        private readonly int _onsetMs, _holdMs, _attackMs, _releaseMs;

        private long _lastMs;
        private long _heardSince;               // 0 = not heard now
        private long _lastHeard;
        private bool _down;                     // the target is DuckGain
        private float _gain = 1;

        public DuckEnvelope() : this(DuckGain, OnsetMs, HoldMs, AttackMs, ReleaseMs) { }

        public DuckEnvelope(float duckGain, int onsetMs, int holdMs, int attackMs, int releaseMs)
        {
            _duckGain = duckGain;
            _onsetMs = onsetMs;
            _holdMs = holdMs;
            _attackMs = attackMs;
            _releaseMs = releaseMs;
        }

        public static DuckEnvelope Self() => new(SelfDuckGain, 0, SelfHoldMs, SelfAttackMs, SelfReleaseMs);

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
                if (nowMs - _heardSince >= _onsetMs) _down = true;
            }
            else
            {
                _heardSince = 0;
                if (_down && nowMs - _lastHeard >= _holdMs) _down = false;
            }
            float span = 1 - _duckGain;
            if (_down) _gain = MathF.Max(_duckGain, _gain - span * dt / _attackMs);
            else _gain = MathF.Min(1, _gain + span * dt / _releaseMs);
            return _gain;
        }
    }
}
