using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // Hit claims (Net/HitClaims.cs): a claim the host checked and took goes in here between steps and is applied at the start
    // of the next one (PlayerEntity.HostApplyClaimedHit: the shot's stand-in hits as the shot's own hit would have), through
    // ClaimedHitTap, so the host's recording has it at the step it was applied and a replay puts it back there (ReplaySession).
    public sealed partial class CampaignHost
    {
        // the recording tap for a claimed hit: (shooter seat, the claim as ClaimRecord's bytes)
        public Action<int, byte[]>? ClaimedHitTap;

        public readonly record struct ClaimedHit(int Shooter, int Shot, int Victim, Vector3 Point, bool Head, bool Rescue = false);
        private readonly List<ClaimedHit> _claimedHits = new();

        public const int ClaimRecordSize = 18; // i32 shot, u8 victim, u8 head, 3 x f32 point

        // a claim the host took (Net/HitClaims.cs), or a replay putting one back: applied at the start of the next step
        public void ClaimHit(in ClaimedHit hit) => _claimedHits.Add(hit);

        public static byte[] ClaimRecord(in ClaimedHit hit)
        {
            byte[] b = new byte[ClaimRecordSize];
            BinaryPrimitives.WriteInt32LittleEndian(b, hit.Shot);
            b[4] = (byte)hit.Victim;
            b[5] = (byte)((hit.Head ? 1 : 0) | (hit.Rescue ? 2 : 0)); // (bit 1: a rescue, P2: no host shot carried the claim)
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(6), hit.Point.X);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(10), hit.Point.Y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(14), hit.Point.Z);
            return b;
        }

        public static ClaimedHit ReadClaimRecord(int shooter, byte[] b) => new(shooter, BinaryPrimitives.ReadInt32LittleEndian(b), b[4],
            new Vector3(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(6)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(10)),
                BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(14))), (b[5] & 1) != 0, (b[5] & 2) != 0);

        // claimed hits applied (the match host's log)
        public int ClaimsApplied { get; private set; }

        // Step, before the frame runs
        private void ApplyClaimedHits()
        {
            if (_claimedHits.Count == 0) return;
            foreach (ClaimedHit hit in _claimedHits)
            {
                ClaimedHitTap?.Invoke(hit.Shooter, ClaimRecord(hit));
                if (PlayerEntity.HostApplyClaimedHit(hit.Shooter, hit.Shot, hit.Victim, hit.Point, hit.Head, hit.Rescue)) ClaimsApplied++;
            }
            _claimedHits.Clear();
        }
    }
}
