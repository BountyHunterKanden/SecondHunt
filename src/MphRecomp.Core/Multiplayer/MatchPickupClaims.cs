using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // Pickup claims (Net/PickupClaims.cs): a claim the served host judged and took goes in here between steps and is handed to
    // the seat's PickUpItems at the start of the next one (PlayerEntity.HostClaimedPickups: taken as if in reach, by the
    // pickup's own rules), through PickupClaimTap, so the host's recording has it at that step and a replay puts it back there
    // (ReplaySession). A replay finds the item again by its type and place (PickupRecord).
    public sealed partial class CampaignHost
    {
        // the recording tap for a claimed pickup: (seat, the item as PickupRecord's bytes)
        public Action<int, byte[]>? PickupClaimTap;

        private readonly List<(int Seat, ItemInstanceEntity Item)> _claimedPickups = new();

        public const int PickupRecordSize = 13; // u8 item type, 3 x f32 where it lies

        // a claim the host took: false when another seat's claim took the item this step already
        public bool ClaimPickup(int seat, ItemInstanceEntity item)
        {
            foreach ((int _, ItemInstanceEntity other) in _claimedPickups)
            {
                if (ReferenceEquals(other, item)) return false;
            }
            _claimedPickups.Add((seat, item));
            return true;
        }

        public static byte[] PickupRecord(ItemInstanceEntity item)
        {
            byte[] b = new byte[PickupRecordSize];
            b[0] = (byte)item.ItemType;
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(1), item.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(5), item.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(9), item.Position.Z);
            return b;
        }

        // a replay: the recorded claim's item, the one of that type lying nearest where it lay (within 0.5)
        public void ClaimPickupRecord(int seat, byte[] b)
        {
            if (b.Length < PickupRecordSize) return;
            var at = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(1)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(5)),
                BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(9)));
            ItemInstanceEntity? best = null;
            float bestDist = 0.5f * 0.5f;
            foreach (ItemInstanceEntity item in Scene.GetItemInstanceEntities())
            {
                if ((byte)item.ItemType != b[0] || item.DespawnTimer == 0) continue;
                float dist = (item.Position - at).LengthSquared;
                if (dist <= bestDist)
                {
                    best = item;
                    bestDist = dist;
                }
            }
            if (best != null) ClaimPickup(seat, best);
        }

        // claimed pickups handed to the seats (the match host's log)
        public int PickupsApplied { get; private set; }

        // Step, before the frame runs (after ApplyClaimedHits)
        private void ApplyClaimedPickups()
        {
            if (PlayerEntity.HostClaimedPickups.Count > 0) PlayerEntity.HostClaimedPickups.Clear(); // (the last step's: taken or not)
            if (_claimedPickups.Count == 0) return;
            foreach ((int seat, ItemInstanceEntity item) in _claimedPickups)
            {
                PickupClaimTap?.Invoke(seat, PickupRecord(item));
                if (item.DespawnTimer != 0 && PlayerEntity.HostClaimedPickups.TryAdd(item, seat)) PickupsApplied++;
            }
            _claimedPickups.Clear();
        }

        // the item sync's ids (Net/PickupClaims.cs): host, the item it numbered `id` while it lists it, and who took one lately
        // (-1 = none known); a device, the host's id for an item it shows (0 = none) and the item it shows for one
        public ItemInstanceEntity? NetHostItem(ushort id) => _itemSync?.HostItem(id);
        public int NetHostTakenBy(ushort id) => _itemSync?.HostTakenBy(id) ?? -1;
        public ushort NetLocalItemId(ItemInstanceEntity item) => _itemSync?.LocalId(item) ?? 0;
        public ItemInstanceEntity? NetLocalItem(ushort id) => _itemSync?.LocalItem(id);
    }
}
