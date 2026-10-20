using System;
using System.Collections.Generic;

namespace MphRead.Entities
{
    // recomp: instant hit feedback (2026-10-19, after a match with a player in Japan). A match client's own shots already hit on its screen (impact, sound), but the hit's damage came
    // a round trip later, with the host's snapshot: the target's red flash, its health bar and HEADSHOT! all waited for it.
    // Now, when this device's own beam hits someone on its screen (PlayerHitClaims.HitClaimBeamHit, the same moment its hit
    // claim is made), the target flashes at once, its health bar shows the hit (never below 1: kills stay the host's), and
    // HEADSHOT! shows for a headshot. When the host's damage arrives (PlayerNet's snapshot apply) it confirms the predictions
    // it covers: no second flash or HEADSHOT!. As Fruity Prime's netcode does it (the idea, our own code): the host answers
    // each hit claim (MphRecomp Net/HitClaims.cs, side channel type 7); a refusal takes that hit back at once
    // (NetRollbackPredicted), and a prediction nobody answers (an older host, claims off, a shot that isn't claimed) is
    // dropped after NetPredictFrames: the health bar goes back. Kills stay the host's (never below 1). Display only: the
    // client's health stays the host's; a host (and every replay) never predicts.
    public partial class PlayerEntity
    {
        // tests and the option to compare (on by default)
        public static bool NetHitFeedback { get; set; } = true;

        private readonly List<(int Damage, long Until, bool Head, int Token)> _netPredicted = new();
        private static int _netPredictToken;

        // how long a prediction waits for the host: this device's round trip (the host's measure: HostSeatPingMs) + its input
        // queue and a snapshot interval; 45 frames when not known yet
        private static int NetPredictFrames
        {
            get
            {
                int ping = Main.SlotIndex >= 0 && Main.SlotIndex < HostSeatPingMs.Length ? HostSeatPingMs[Main.SlotIndex] : -1;
                return ping > 0 ? Math.Clamp(ping * 60 / 1000 + 24, 24, 90) : 45;
            }
        }

        private long NetPredictNow => (long)_scene.FrameCount;

        private int NetPredictedDamage
        {
            get
            {
                if (_netPredicted.Count == 0) return 0;
                long now = NetPredictNow;
                _netPredicted.RemoveAll(p => p.Until <= now || _health == 0);
                int sum = 0;
                foreach ((int damage, _, _, _) in _netPredicted) sum += damage;
                return sum;
            }
        }

        // what this device's HUD shows of this player's health: the host's, less this device's own hits it hasn't confirmed
        // yet (never below 1 while alive)
        public int HostShownHealth
        {
            get
            {
                int predicted = NetPredictedDamage;
                return predicted == 0 || _health == 0 ? _health : Math.Max(1, _health - predicted);
            }
        }

        // a match client's own beam hit this player on its screen for `damage` (as the beam reckoned it); returns the
        // prediction's token (its hit claim carries it, for the host's verdict), 0 = nothing predicted
        internal int NetPredictHit(uint damage, bool head)
        {
            if (!NetHitFeedback || !NetClient || this == Main || _health == 0 || damage == 0
                || GameState.Teams && !GameState.FriendlyFire && TeamIndex == Main.TeamIndex)
            {
                return 0;
            }
            int token = ++_netPredictToken;
            if (token <= 0) token = _netPredictToken = 1;
            _netPredicted.Add(((int)Math.Min(damage, (uint)Int32.MaxValue), NetPredictNow + NetPredictFrames, head, token));
            _timeSinceDamage = 0; // the red flash, now
            if (head)
            {
                QueueHudMessage(128, 40, 20 / 30f, 0, GameState.Multiplayer ? 228 : 121); // HEADSHOT! (as TakeDamage)
            }
            return token;
        }

        // the host refused the hit claim this prediction made (MphRecomp Net/HitClaims.cs): taken back now
        public void NetRollbackPredicted(int token)
        {
            if (token != 0) _netPredicted.RemoveAll(p => p.Token == token);
        }

        // PlayerNet's snapshot apply, as the host's damage comes in: the predictions it covers, oldest first; Flashed = one was
        // shown (no second flash), Head = a predicted headshot was told already
        private (bool Flashed, bool Head) NetConfirmPredicted(int damage)
        {
            if (_netPredicted.Count == 0) return (false, false);
            bool head = false, any = false;
            int left = damage;
            while (left > 0 && _netPredicted.Count > 0)
            {
                (int d, long until, bool h, _) = _netPredicted[0];
                _netPredicted.RemoveAt(0);
                if (until <= NetPredictNow) continue; // (expired: already rolled back on the HUD)
                any = true;
                head |= h;
                left -= d;
            }
            return (any, head);
        }
    }
}
