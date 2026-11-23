using System;
using System.Collections.Generic;
using MphRead.Entities;

namespace MphRecomp.Campaign
{
    // Fruity P2 shot events (Net/NetMatchSession.Shots.cs, MphRead PlayerNetShotEvents.cs): a served host's seat's new shot
    // events, and whether its device sends them, go in here between steps and are handed to the seat at the start of the
    // next one (PlayerEntity.NetShotEventsDriven, NetQueueShotEvent), through ShotEventTap, so the host's recording has them
    // at that step and a replay puts them back there (ReplaySession). A record is [driven u8][n u8][n x NetShotEvent].
    public sealed partial class CampaignHost
    {
        // the recording tap for a seat's shot events: (seat, ShotRecord's bytes)
        public Action<int, byte[]>? ShotEventTap;

        private readonly List<(int Seat, byte[] Record)> _remoteShots = new();

        // host, once per frame per remote seat: kept only when there's news (new events, or the seat's device started or
        // stopped sending them: its player, made new each scene, starts on its trigger)
        public void SetRemoteShots(int seat, bool driven, List<NetShotEvent> events)
        {
            if (seat < 0 || seat >= PlayerEntity.Players.Count || PlayerEntity.Players[seat] is not PlayerEntity p
                || !p.HostRemote || driven == p.NetShotEventsDriven && events.Count == 0)
            {
                return;
            }
            int n = Math.Min(events.Count, PlayerEntity.NetShotCapacity);
            byte[] rec = new byte[2 + n * NetShotEvent.Size];
            rec[0] = (byte)(driven ? 1 : 0);
            rec[1] = (byte)n;
            for (int i = 0; i < n; i++)
            {
                events[events.Count - n + i].Write(rec.AsSpan(2 + i * NetShotEvent.Size));
            }
            _remoteShots.Add((seat, rec));
        }

        // a replay: the recorded record, as it came
        public void SetRemoteShotsRecord(int seat, byte[] rec)
        {
            if (IsShotRecord(rec))
            {
                _remoteShots.Add((seat, rec));
            }
        }

        public static bool IsShotRecord(byte[] rec)
        {
            return rec.Length >= 2 && rec[0] <= 1 && rec[1] <= PlayerEntity.NetShotCapacity && rec.Length == 2 + rec[1] * NetShotEvent.Size;
        }

        // Step, before the frame runs (after ApplyClaimedPickups)
        private void ApplyRemoteShots()
        {
            if (_remoteShots.Count == 0) return;
            foreach ((int seat, byte[] rec) in _remoteShots)
            {
                ShotEventTap?.Invoke(seat, rec);
                if (seat < 0 || seat >= PlayerEntity.Players.Count || PlayerEntity.Players[seat] is not PlayerEntity p || !p.HostRemote)
                {
                    continue;
                }
                p.NetShotEventsDriven = rec[0] != 0;
                for (int i = 0; i < rec[1]; i++)
                {
                    p.NetQueueShotEvent(NetShotEvent.Read(rec.AsSpan(2 + i * NetShotEvent.Size)));
                }
            }
            _remoteShots.Clear();
        }
    }
}
