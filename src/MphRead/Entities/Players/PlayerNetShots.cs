using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp (2026-10-19, players close together firing straight at each other with no hit):
    // per seat, what each fire attempt came to, counted where that seat's shots are decided. A match host counts its remote
    // seats (HostRemote), a match client its own player (NetClient, Main), so the host's log can put a player's shots and hits
    // on the host beside what that player's device saw (MphRecomp Net/ShotReport.cs): "never fired there" (refused: the
    // weapon's cooldown, the gun lowered or raising, no shot spawned) tells apart from "fired but missed". Counting only:
    // nothing reads these in the match. A press refused is counted once (a held trigger's repeats aren't).
    public partial class PlayerEntity
    {
        public const int NetShotCooldown = 0, NetShotGunLowered = 1, NetShotNoSpawn = 2, NetShotOutcomes = 3;
        public static readonly int[] NetShotsFired = new int[16];
        public static readonly int[,] NetShotsRefused = new int[16, NetShotOutcomes];

        // each shot as it went out (the shot check, MphRecomp Net/ShotCheck.cs): a match client's own player's, and a host's
        // remote seats', with where it left the gun and its direction; and each hit a client's own beam made on its screen,
        // with where the player hit was then. Drained by the shot check every step; off (nothing kept) unless it runs.
        // (Number: the shooter's shot count, HitClaimShotNumber: what hit claims name the shot by)
        public readonly record struct NetShotOut(int Seat, Vector3 Origin, Vector3 Direction, sbyte Weapon, int Number);
        public readonly record struct NetHitSeen(int Victim, Vector3 VictimPosition);
        public static bool NetShotLogOn { get; set; }
        public static readonly List<NetShotOut> NetShotLog = new();
        public static readonly List<NetHitSeen> NetHitLog = new();

        public static void ResetNetShots()
        {
            Array.Clear(NetShotsFired);
            Array.Clear(NetShotsRefused);
            Array.Clear(NetBeamHits);
            NetShotLog.Clear();
            NetHitLog.Clear();
        }

        // NetCountBeamHit, a match client: its own player's beam hit someone here
        private static void NetLogHitSeen(PlayerEntity shooter, PlayerEntity target)
        {
            if (NetShotLogOn && NetClient && shooter == Main && target != shooter)
            {
                NetHitLog.Add(new NetHitSeen(target.SlotIndex, target.Position));
            }
        }

        // TryFireWeapon: -1 = a shot went out (from origin along direction), else the refusal (only for a press)
        private void NetCountShot(int outcome, bool pressed, Vector3 origin = default, Vector3 direction = default)
        {
            if (!(HostRemote || NetClient && this == Main) || SlotIndex < 0 || SlotIndex >= NetShotsFired.Length)
            {
                return;
            }
            if (outcome < 0)
            {
                NetShotsFired[SlotIndex]++;
                _hitClaimShotNumber++; // (the beams Spawn just made were tagged with this number: PlayerHitClaims.cs)
                if (NetShotLogOn) NetShotLog.Add(new NetShotOut(SlotIndex, origin, direction, (sbyte)CurrentWeapon, _hitClaimShotNumber));
            }
            else if (pressed)
            {
                NetShotsRefused[SlotIndex, outcome]++;
            }
        }
    }
}
