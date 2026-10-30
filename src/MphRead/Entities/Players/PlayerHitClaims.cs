using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: hit claims (2026-10-19, the idea from Fruity Prime's hit registration at 250-300 ms, built our own
    // way: MphRecomp Net/HitClaims.cs has the network side and the checks). The host stays the judge of every hit: its own
    // copy of each shot still hits as before (lag compensated). On top of that an online guest tells its host each hit its
    // own weapon shots made on its screen, and when the host's copy of that shot missed that player and the claim checks out
    // against the host's own history, the host applies the hit itself (CampaignHost.ClaimHit, recorded in its replay).
    // This side: each weapon shot of a remote seat on the host (HostRemote, with HostHitClaims) and of a guest's own player
    // gets a number; its beams carry it; the host keeps the last ClaimShots of each seat's shots (whether it may be claimed,
    // where it left the gun, which players it hit, a stand-in to hit with); the guest logs its own shots' hits.
    public partial class PlayerEntity
    {
        // host rule (MatchSettings.HitClaims, set per match by MatchStart; older replays: off): keep the remote seats' shots
        // for hit claims and take the claims the host checked. Off: nothing is kept, no claim is ever applied
        public static bool HostHitClaims { get; set; }
        // a claimed shot hits one player at most (MatchSettings.ClaimOneHit, 2026-10-19: a single straight beam
        // can't hit two, so a second claim on the same shot is refused); off for replays before format 53
        public static bool HostClaimOneHit { get; set; }

        private const int ClaimShots = 32;
        private int _hitClaimShotNumber = 0; // this player's weapon shots so far (NetCountShot)
        private bool _hitClaimFiring = false; // TryFireWeapon's Spawn is running: its beams are the next shot's
        private ClaimShot[]? _claimShots;

        private sealed class ClaimShot
        {
            public int Number;
            public bool Claimable;
            public uint HitMask; // the players this shot's own beams hit on the host
            public float Reach; // how far along its line the beam can get: its speed over its lifespan (+ a step)
            public int FlightFrames; // its lifespan in steps
            public BeamProjectileEntity? StandIn;
        }

        // a guest's own shots' hits on its screen (drained by Net/HitClaims.cs every step): the shot, who, where they were
        // then, where the beam struck them, and whether it was a headshot there
        // (Predicted: the instant hit feedback's token for this hit, PlayerNetPredict.cs; the host's verdict on the claim takes it
        // back when refused)
        public readonly record struct NetHitClaim(int Shot, int Victim, Vector3 VictimPosition, Vector3 Point, bool Head, int Predicted);
        public static readonly List<NetHitClaim> NetHitClaimLog = new();

        // what the host's check reads of a kept shot
        public readonly record struct ClaimShotInfo(bool Claimable, Vector3 Origin, Vector3 Direction, BeamType Beam, uint HitMask,
            int BeamsInFlight, float Radius, float Reach, int FlightFrames);

        private bool HitClaimTagging => HostRemote && HostHitClaims || NetClient && this == Main && NetShotLogOn;

        // BeamProjectileEntity.Spawn, every beam: tag it with its shot, and keep the shot (host) from its first beam
        public static void HitClaimBeamSpawned(BeamProjectileEntity beam)
        {
            beam.HostShotNumber = 0;
            if (beam.Owner is not PlayerEntity p || !p._hitClaimFiring || !p.HitClaimTagging)
            {
                return;
            }
            int number = p._hitClaimShotNumber + 1; // (NetCountShot counts it once Spawn is back)
            beam.HostShotNumber = number;
            if (!(p.HostRemote && HostHitClaims))
            {
                return;
            }
            p._claimShots ??= new ClaimShot[ClaimShots];
            ref ClaimShot? slot = ref p._claimShots[number % ClaimShots];
            slot ??= new ClaimShot();
            if (slot.Number == number)
            {
                // a second beam of the same shot: a shot that spreads isn't claimed (each beam would need its own claim)
                slot.Claimable = false;
                return;
            }
            slot.Number = number;
            slot.HitMask = 0;
            // claimable: a straight shot (no homing, no continuous beam, no gravity): the host checks the claimed hit against
            // the shot's line; the rest stay the host's own copy's call
            slot.Claimable = !beam.Flags.TestFlag(BeamFlags.Homing) && !beam.Flags.TestFlag(BeamFlags.Continuous)
                && beam.Acceleration == Vector3.Zero;
            // (speed per 60 Hz step, lifespan in seconds: BeamProjectileEntity.Spawn)
            slot.FlightFrames = (int)MathF.Ceiling(MathF.Max(beam.Lifespan, 0) * 60);
            slot.Reach = MathF.Max(beam.InitialSpeed, beam.FinalSpeed) * (slot.FlightFrames + 1);
            slot.StandIn ??= new BeamProjectileEntity(p._scene);
            slot.StandIn.HostCopyHitFields(beam);
        }

        // ProcessCollision, a beam's hit on a player or their Halfturret: the host notes it on the shot (a claim on that player
        // for that shot then adds nothing); the guest logs its claim (not a Halfturret's: claims are on the player)
        public static void HitClaimBeamHit(BeamProjectileEntity beam, PlayerEntity target, Vector3 point, bool head, bool halfturret,
            uint damage = 0)
        {
            int predicted = NetClient && beam.Owner == Main && !halfturret
                ? target.NetPredictHit(damage, head) // (instant hit feedback, any of its own beams: PlayerNetPredict.cs)
                : 0;
            if (beam.HostShotNumber == 0 || beam.Owner is not PlayerEntity p || p == target)
            {
                return;
            }
            if (p.FindClaimShot(beam.HostShotNumber) is ClaimShot shot && target.SlotIndex is >= 0 and < 32)
            {
                shot.HitMask |= 1u << target.SlotIndex;
            }
            if (NetClient && p == Main && NetShotLogOn && !halfturret)
            {
                NetHitClaimLog.Add(new NetHitClaim(beam.HostShotNumber, target.SlotIndex, target.Position, point, head, predicted));
            }
        }

        private ClaimShot? FindClaimShot(int number)
        {
            ClaimShot? shot = _claimShots?[number % ClaimShots];
            return shot != null && shot.Number == number ? shot : null;
        }

        // MatchStart, every seat: a new match numbers its shots from the start (seats are reused from match to match, and a
        // replay starts from nothing: its claims name shots by these numbers)
        public void ResetHitClaims()
        {
            _hitClaimShotNumber = 0;
            _hitClaimFiring = false;
            if (_claimShots != null)
            {
                foreach (ClaimShot? shot in _claimShots)
                {
                    if (shot != null) shot.Number = 0;
                }
            }
        }

        // the host's check (outside the simulation: reads only): a kept shot of this player's, with its beams still flying
        public bool TryClaimShot(int number, out ClaimShotInfo info)
        {
            info = default;
            if (FindClaimShot(number) is not ClaimShot shot || shot.StandIn == null)
            {
                return false;
            }
            int flying = 0;
            if (EquipInfo.Beams is BeamProjectileEntity[] beams)
            {
                foreach (BeamProjectileEntity b in beams)
                {
                    if (b.Lifespan > 0 && b.Owner == this && b.HostShotNumber == number) flying++;
                }
            }
            info = new ClaimShotInfo(shot.Claimable, shot.StandIn.SpawnPosition, shot.StandIn.Direction, shot.StandIn.Beam,
                shot.HitMask, flying, shot.StandIn.CylinderRadius, shot.Reach, shot.FlightFrames);
            return true;
        }

        // CampaignHost, the start of a step (a claim the host took, or a replay putting it back): shot `number` of `shooter`'s
        // hits `victim` at `point`, unless that shot already hit them (with HostClaimOneHit: anyone) or they're down. True = applied
        // rescue (P2, FRUITY_PORT.md): no host shot carried the claim, so `number` is the seat's newest claimable shot, used for
        // its weapon's damage only: the shot's own hit mask is no bar, and the falloff reads from where the shooter stands now
        public static bool HostApplyClaimedHit(int shooter, int number, int victim, Vector3 point, bool head, bool rescue = false)
        {
            if (!HostHitClaims || shooter < 0 || shooter >= MaxPlayers || victim < 0 || victim >= MaxPlayers || shooter == victim)
            {
                return false;
            }
            PlayerEntity p = Players[shooter], target = Players[victim];
            if (p.FindClaimShot(number) is not ClaimShot shot || shot.StandIn == null || !shot.Claimable
                || (!rescue && (HostClaimOneHit ? shot.HitMask != 0 : (shot.HitMask & (1u << victim)) != 0))
                || target.Health == 0 || !target.LoadFlags.TestFlag(LoadFlags.Spawned))
            {
                return false;
            }
            shot.HitMask |= 1u << victim;
            shot.StandIn.Owner = p;
            if (rescue)
            {
                Vector3 spawn = shot.StandIn.SpawnPosition;
                shot.StandIn.SpawnPosition = p.Position;
                shot.StandIn.HostClaimHit(target, point, head);
                shot.StandIn.SpawnPosition = spawn;
                return true;
            }
            shot.StandIn.HostClaimHit(target, point, head);
            return true;
        }
    }
}
