using System;
using System.Threading;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // CLOUD SAVE (server 0.11.0; the protocol and the rules: Core Online/CloudSave.cs, docs/PROFILE_SYNC.md Phase 2). The hub
    // keeps one sealed copy of a player's profile (StoreCloud.cs) and never reads it: only its version byte and size are
    // checked. Only for a player with a started online match (safety.matches), a put at most every 30 s and 100 a day, a
    // banned player can't put or get, and an hourly purge drops copies of players gone or not seen for a year.
    public sealed partial class Hub
    {
        private Timer? _cloudTimer;

        private void StartCloudTimer() => _cloudTimer = new Timer(_ => PurgeCloudSafe(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));

        private void PurgeCloudSafe()
        {
            try
            {
                int gone = _store.PurgeCloud();
                if (gone > 0) Console.WriteLine($"cloud: {gone} cloud save(s) purged (player gone or unseen {CloudSaveRules.KeepDays} days)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"cloud: purge failed: {ex.Message}");
            }
        }

        private void CloudAllowed(string me)
        {
            if (_store.Banned(me)) throw new OnlineException("banned", "a banned profile has no cloud save");
        }

        private CloudSaveMsg CloudGet(string me)
        {
            CloudAllowed(me);
            Store.CloudRow? row = _store.Cloud(me);
            return row?.Blob == null
                ? new CloudSaveMsg { Rev = row?.Rev ?? 0 }
                : new CloudSaveMsg { Rev = row.Rev, At = row.At, Blob = Convert.ToBase64String(row.Blob) };
        }

        private CloudPutMsg CloudPut(string me, CloudPutReq r)
        {
            CloudAllowed(me);
            if (_store.Matches(me) < CloudSaveRules.MinMatches)
            {
                throw new OnlineException("no_matches", "cloud save starts after your first online match");
            }
            if (r.Blob.Length > (CloudSaveRules.MaxBlob + 2) / 3 * 4)
            {
                throw new OnlineException("too_big", "the cloud save is too big");
            }
            byte[] blob;
            try
            {
                blob = Convert.FromBase64String(r.Blob);
            }
            catch (FormatException)
            {
                throw new OnlineException("bad_blob", "not a sealed profile");
            }
            if (blob.Length > CloudSaveRules.MaxBlob)
            {
                throw new OnlineException("too_big", "the cloud save is too big");
            }
            if (!CloudSaveRules.LooksSealed(blob))
            {
                throw new OnlineException("bad_blob", "not a sealed profile");
            }
            switch (_store.PutCloud(me, r.BaseRev, blob, out long rev))
            {
            case Store.CloudPut.TooSoon:
                throw new OnlineException("too_soon", $"cloud save: wait {CloudSaveRules.PutGapSeconds} seconds between saves");
            case Store.CloudPut.TooMany:
                throw new OnlineException("too_many", $"cloud save: {CloudSaveRules.PutsPerDay} saves a day at most");
            case Store.CloudPut.Conflict:
                return new CloudPutMsg { Done = false, Rev = rev };
            default:
                return new CloudPutMsg { Done = true, Rev = rev };
            }
        }
    }
}
