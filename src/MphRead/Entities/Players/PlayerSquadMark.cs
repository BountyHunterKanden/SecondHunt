using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: SQUAD MARK, the Trooper's affinity bonus with the Pirate Blaster (owner pick 2026-10-09; built 2026-10-16:
    // "we never got the tag to see players through walls working"). A full burst (all PirateBlaster.BurstBolts bolts on
    // one target) marks the target for SquadMarkFrames: while marked it's shown through walls by the vanilla locator icon
    // (the one over the Prime Hunter, ProcessHudSquadMark) and on the Prime HUD's radar. Team modes: the Trooper's whole
    // team sees it; free for all: the Trooper alone. No bonus damage (owner 2026-10-09). A seat marks when its affinity
    // weapon is the blaster (MatchStart: Characters.AffinityBeamFor = the Trooper). Sim on the host / offline / replays;
    // a match client takes each seat's marks from the host (MphRecomp SeatAbilities, NetApplySquadMark).
    public partial class PlayerEntity
    {
        public const int SquadMarkFrames = 4 * 30 * 2; // 4 s (x2 at 60 fps)
        public const int SquadMarkHits = PirateBlaster.BurstBolts; // a full burst

        // on the target: the seats whose mark is on it (bit = seat) and the frames left
        private byte _squadMarkedBy = 0;
        private ushort _squadMarkTimer = 0;
        // on the shooter: the current burst's hits, on one target
        private PlayerEntity? _squadMarkTarget = null;
        private int _squadMarkHitCount = 0;
        // HUD (the main player): which seats it saw marked last frame, for the TARGET MARKED message on a new mark
        private readonly bool[] _squadMarkSeen = new bool[SlotCapacity];

        // the seats marking this one (tests, network snapshots); 0 = not marked
        public byte NetSquadMark => _health > 0 ? _squadMarkedBy : (byte)0;
        public int HostSquadMarkTimer => _squadMarkTimer;

        // OnPirateBlasterShot: a new burst counts its hits afresh
        private void OnSquadMarkBurst()
        {
            _squadMarkTarget = null;
            _squadMarkHitCount = 0;
        }

        // TakeDamage: a beam of this seat hit target (and wasn't ignored as a team hit)
        private void OnSquadMarkHit(PlayerEntity target, BeamProjectileEntity beam)
        {
            if (!GameState.Multiplayer || beam.Beam != BeamType.PirateBlaster || AffinityBeam != BeamType.PirateBlaster)
            {
                return;
            }
            if (target != _squadMarkTarget)
            {
                _squadMarkTarget = target;
                _squadMarkHitCount = 0;
            }
            if (++_squadMarkHitCount >= SquadMarkHits)
            {
                _squadMarkHitCount = 0;
                target._squadMarkedBy |= (byte)(1 << SlotIndex);
                target._squadMarkTimer = SquadMarkFrames;
            }
        }

        // every frame (ProcessPlayer): the mark wears off; dying clears it
        private void UpdateSquadMark()
        {
            if (_health == 0)
            {
                _squadMarkedBy = 0;
                _squadMarkTimer = 0;
            }
            else if (_squadMarkTimer > 0 && --_squadMarkTimer == 0)
            {
                _squadMarkedBy = 0;
            }
        }

        // a match client: the host's marks on this seat (kept up while the host has any)
        public void NetApplySquadMark(byte mask)
        {
            _squadMarkedBy = mask;
            _squadMarkTimer = mask != 0 ? (ushort)SquadMarkFrames : (ushort)0;
        }

        // the viewer sees this seat's mark: its own, or in team modes a teammate's (never on its own team's seats)
        public bool SquadMarkSeenBy(PlayerEntity viewer)
        {
            if (_squadMarkedBy == 0 || _health == 0 || viewer == this || GameState.Teams && viewer.TeamIndex == TeamIndex)
            {
                return false;
            }
            for (int i = 0; i < MaxPlayers; i++)
            {
                if ((_squadMarkedBy & (1 << i)) == 0)
                {
                    continue;
                }
                PlayerEntity marker = Players[i];
                if (marker == viewer || GameState.Teams && marker.TeamIndex == viewer.TeamIndex)
                {
                    return true;
                }
            }
            return false;
        }

        // the locator's colour: the blaster's bolt (RGB555 31,12,4; the Prime Hunter's locator is 31,0,0)
        private static readonly ColorRgb _squadMarkColor = new ColorRgb(31, 12, 4);

        // ProcessModeHud (the main player): a locator on each seat it sees marked, through walls, every mode
        private void ProcessHudSquadMark()
        {
            bool newMark = false;
            foreach (PlayerEntity player in _scene.GetPlayerEntities())
            {
                if (player.SlotIndex < 0 || player.SlotIndex >= SlotCapacity)
                {
                    continue;
                }
                bool seen = player.SquadMarkSeenBy(this);
                if (seen)
                {
                    Vector3 pos = player.Position;
                    if (!player.IsAltForm)
                    {
                        pos.Y += 0.75f;
                    }
                    AddLocatorInfo(pos, _playerLocator, _squadMarkColor);
                    // (the message for this player's own marks only; a teammate's just shows the locator)
                    newMark |= !_squadMarkSeen[player.SlotIndex] && (player._squadMarkedBy & (1 << SlotIndex)) != 0;
                }
                _squadMarkSeen[player.SlotIndex] = seen;
            }
            if (newMark)
            {
                QueueHudMessage(128, 52, 30 / 30f, 0, "TARGET MARKED!");
            }
        }
    }
}
