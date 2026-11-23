using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // 0.11.0: CLOUD SAVE (HubCloud.cs, Core Online/CloudSave.cs): the seal opens only for its identity; no copy before an
    // online match; put, get the same bytes back; compare-and-swap on rev; 30 s between puts, 100 a day; what isn't a sealed
    // blob or is too big refused; delete; a banned profile refused; delete_profile and a year unseen remove the copy.
    public static partial class SelfTest
    {
        private static async Task Cloud(string dir)
        {
            // the seal (client side)
            OnlineIdentity me = OnlineIdentity.Create(), other = OnlineIdentity.Create();
            byte[] plain = Enumerable.Range(0, 9000).Select(i => (byte)(i * 13)).ToArray();
            byte[] sealedBlob = CloudSeal.Seal(me, plain);
            byte[] tampered = (byte[])sealedBlob.Clone();
            tampered[40] ^= 1;
            Check(CloudSaveRules.LooksSealed(sealedBlob) && CloudSeal.Open(me, sealedBlob)?.SequenceEqual(plain) == true
                && !sealedBlob.Skip(13).Take(64).SequenceEqual(plain.Take(64)),
                "cloud: a sealed profile opens for its identity, and its bytes aren't the plain ones");
            Check(CloudSeal.Open(other, sealedBlob) == null && CloudSeal.Open(me, tampered) == null,
                "cloud: another identity's key, or one changed byte, doesn't open it");

            (WebApplication app, Uri uri) = await Start(Path.Combine(dir, "cloud.db"));
            Store store = app.Services.GetRequiredService<Store>();
            OnlineClient a = await Connect(uri, me, "Cloudy");
            OnlineClient b = await Connect(uri, OnlineIdentity.Create(), "Other");
            Check(CloudSaveRules.Supported(a), $"cloud: the server says {a.ServerVersion} (>= {CloudGetReq.SinceServer})");
            string blob = Convert.ToBase64String(sealedBlob);

            var none = await a.RequestAsync<CloudSaveMsg>(new CloudGetReq());
            Check(none.Rev == 0 && none.Blob == "", "cloud: no copy yet: rev 0");
            Check(await ErrorOf(() => a.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = 0, Blob = blob })) == "no_matches",
                "cloud: no copy is kept before a started online match");
            store.AddMatches(new[] { a.Me.Code, b.Me.Code });
            var put = await a.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = 0, Blob = blob });
            var got = await a.RequestAsync<CloudSaveMsg>(new CloudGetReq());
            Check(put.Done && put.Rev == 1 && got.Rev == 1 && got.Blob == blob && got.At > 0, "cloud: put, then get gives the same bytes at rev 1");
            var otherGet = await b.RequestAsync<CloudSaveMsg>(new CloudGetReq());
            Check(otherGet.Rev == 0, "cloud: another player sees only their own (none)");

            // a cloud-only connection (another device of the same player) doesn't kick this one, and does only cloud requests
            OnlineClient side = await OnlineClient.ConnectAsync(uri, me, "Cloudy", "selftest", default, "", cloudOnly: true);
            var sideGet = await side.RequestAsync<CloudSaveMsg>(new CloudGetReq());
            string? sideOther = await ErrorOf(() => side.RequestAsync<RosterMsg>(new RosterReq()));
            await side.DisposeAsync();
            await Task.Delay(200);
            var still = await a.RequestAsync<CloudSaveMsg>(new CloudGetReq());
            Check(a.Connected && still.Rev == 1 && sideGet.Blob == blob && sideOther == "cloud_only",
                $"cloud: a cloud-only login reads the copy, leaves the player's connection on, and is refused the rest ({sideOther})");

            Check(await ErrorOf(() => a.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = 1, Blob = blob })) == "too_soon",
                "cloud: a second put within 30 s is refused");
            store.AgeCloud(a.Me.Code, 31);
            var stale = await a.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = 0, Blob = blob });
            Check(!stale.Done && stale.Rev == 1, "cloud: a put from an older rev is a conflict that answers the rev there now");
            var next = await a.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = 1, Blob = blob });
            Check(next.Done && next.Rev == 2, "cloud: from the current rev it goes in as rev 2");

            store.AgeCloud(a.Me.Code, 31);
            string junk = Convert.ToBase64String(new byte[] { 9 }.Concat(new byte[100]).ToArray());
            string huge = Convert.ToBase64String(new byte[] { 1 }.Concat(new byte[CloudSaveRules.MaxBlob]).ToArray());
            Check(await ErrorOf(() => a.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = 2, Blob = junk })) == "bad_blob"
                && await ErrorOf(() => a.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = 2, Blob = "not base64!" })) == "bad_blob",
                "cloud: a blob that isn't a sealed profile is refused");
            Check(await ErrorOf(() => a.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = 2, Blob = huge })) == "too_big",
                "cloud: a blob over the cap is refused");

            // 100 a day (in the store: that many requests at once would trip the flood limit)
            long stored = 2;
            for (int i = 0; i < CloudSaveRules.PutsPerDay; i++)
            {
                store.AgeCloud(a.Me.Code, 31);
                if (store.PutCloud(a.Me.Code, stored, sealedBlob, out long r) != Store.CloudPut.Stored) break;
                stored = r;
            }
            store.AgeCloud(a.Me.Code, 31);
            Check(stored == CloudSaveRules.PutsPerDay && await ErrorOf(() => a.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = stored, Blob = blob })) == "too_many",
                $"cloud: {CloudSaveRules.PutsPerDay} puts a day, then refused (stored rev {stored})");

            // delete
            var del = await a.RequestAsync<OkMsg>(new CloudDeleteReq());
            var after = await a.RequestAsync<CloudSaveMsg>(new CloudGetReq());
            var delAgain = await a.RequestAsync<OkMsg>(new CloudDeleteReq());
            Check(del.Done && after.Blob == "" && after.Rev == stored && !delAgain.Done, "cloud: delete drops the copy (its rev stays, so nothing old comes back)");

            // banned
            OnlineClient c = await Connect(uri, OnlineIdentity.Create(), "Banned");
            store.AddMatches(new[] { c.Me.Code });
            store.Ban(store.Get(c.Me.Code)!, "test", 0);
            Check(await ErrorOf(() => c.RequestAsync<CloudSaveMsg>(new CloudGetReq())) == "banned"
                && await ErrorOf(() => c.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = 0, Blob = blob })) == "banned",
                "cloud: a banned profile can't put or get");

            // delete_profile, and a year unseen
            store.AgeCloud(b.Me.Code, 0);
            await b.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = 0, Blob = blob });
            OnlineIdentity idD = OnlineIdentity.Create();
            OnlineClient d = await Connect(uri, idD, "Gone");
            store.AddMatches(new[] { d.Me.Code });
            await d.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = 0, Blob = blob });
            await d.RequestAsync<OkMsg>(new DeleteProfileReq());
            Check(store.Cloud(d.Me.Code) == null, "cloud: delete_profile removes the copy");
            int purged0 = store.PurgeCloud();
            store.AgeCloud(b.Me.Code, 0, (CloudSaveRules.KeepDays + 1) * 86400L);
            int purged1 = store.PurgeCloud();
            Check(purged0 == 0 && purged1 == 1 && store.Cloud(b.Me.Code) == null && store.Cloud(a.Me.Code) != null,
                "cloud: a copy unseen for a year is purged, a recent one stays");

            await a.DisposeAsync();
            await b.DisposeAsync();
            await c.DisposeAsync();
            await app.StopAsync();
        }
    }
}
