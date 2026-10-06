using Android.App;
using Android.Content;
using Android.Content.PM;
using MphRecomp.Update;

// RECOMP SETTINGS > UPDATES installs the verified APK itself (owner 2026-10-06). Needs Android's per-app "install unknown
// apps" switch, which the player turns on once (AndroidUpdater.Blocker opens it).
[assembly: UsesPermission(Android.Manifest.Permission.RequestInstallPackages)]

namespace MphRecomp.App;

// The Android half of the in-game update (Core Update/UpdateInstall.cs): Core has downloaded the APK and checked it
// against the signed update file; this checks that it's this same app, not older, and signed with the same certificate,
// then hands it to PackageInstaller. Android shows its own "update this app?" screen, replaces the app (saves and
// settings stay: same package, same key) and closes it.
internal sealed class AndroidUpdater : IUpdateInstaller
{
    readonly Context _app;

    // the running install's answer, for UpdateInstallReceiver (one install at a time)
    internal static Action<string>? Pending;

    public AndroidUpdater(Context app)
    {
        _app = app;
        DownloadDir = Path.Combine(app.CacheDir!.AbsolutePath, "update");
    }

    public string DownloadDir { get; }

    public string? Blocker(UpdateResult offer, out Action? fix)
    {
        fix = null;
        if (_app.PackageManager!.CanRequestPackageInstalls()) return null;
        fix = () =>
        {
            try
            {
                var settings = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources,
                    Android.Net.Uri.Parse("package:" + _app.PackageName));
                settings.AddFlags(ActivityFlags.NewTask);
                _app.StartActivity(settings);
            }
            catch (ActivityNotFoundException) { Android.Util.Log.Warn("MPHUpdate", "no install-unknown-apps settings page"); }
        };
        return "second hunt needs your ok to install its updates. turn on allow from this source on the next screen, then press check again.";
    }

    public void Install(string path, UpdateResult offer, Action<string> done)
    {
        Task.Run(() =>
        {
            try
            {
                string? problem = CheckApk(path);
                if (problem != null)
                {
                    done(problem);
                    return;
                }
                PackageInstaller installer = _app.PackageManager!.PackageInstaller;
                var options = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
                options.SetAppPackageName(_app.PackageName);
                int id = installer.CreateSession(options);
                using PackageInstaller.Session session = installer.OpenSession(id);
                using (FileStream apk = File.OpenRead(path))
                using (Stream output = session.OpenWrite("base.apk", 0, apk.Length))
                {
                    apk.CopyTo(output);
                    session.Fsync(output);
                }
                Pending = done;
                var status = new Intent(_app, typeof(UpdateInstallReceiver));
                // the installer adds its status to this intent, so it must be mutable (Android 12+ asks for the flag)
                PendingIntentFlags flags = PendingIntentFlags.UpdateCurrent
                    | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
                PendingIntent sender = PendingIntent.GetBroadcast(_app, id, status, flags)!;
                Android.Util.Log.Info("MPHUpdate", $"installing {offer.Latest} (session {id})");
                session.Commit(sender.IntentSender!);
            }
            catch (Exception ex) when (ex is IOException or Java.Lang.Exception)
            {
                Android.Util.Log.Warn("MPHUpdate", "install failed: " + ex);
                done("the update couldn't be installed. the game is unchanged.");
            }
        });
    }

    // why this APK mustn't replace the running app, or null
    string? CheckApk(string path)
    {
        PackageManager pm = _app.PackageManager!;
        string self = _app.PackageName!;
        PackageInfo? apk;
        PackageInfo installed;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var flags = PackageManager.PackageInfoFlags.Of((long)(PackageInfoFlags.SigningCertificates | PackageInfoFlags.Signatures));
            apk = pm.GetPackageArchiveInfo(path, flags);
            installed = pm.GetPackageInfo(self, flags)!;
        }
        else
        {
#pragma warning disable CA1422, CS0618 // the pre-33 forms (SigningCertificates needs 28; checked below)
            PackageInfoFlags flags = OperatingSystem.IsAndroidVersionAtLeast(28)
                ? PackageInfoFlags.SigningCertificates | PackageInfoFlags.Signatures
                : PackageInfoFlags.Signatures;
            apk = pm.GetPackageArchiveInfo(path, flags);
            installed = pm.GetPackageInfo(self, flags)!;
#pragma warning restore CA1422, CS0618
        }
        if (apk == null) return "the download isn't an android app, so it wasn't installed.";
        if (apk.PackageName != self)
        {
            return $"the download is another app, {apk.PackageName}, so it wasn't installed.";
        }
        if (apk.LongVersionCode < installed.LongVersionCode) return "the download is older than this copy, so it wasn't installed.";
        string[] want = Signers(installed), got = Signers(apk);
        Android.Util.Log.Info("MPHUpdate", $"apk {apk.PackageName} {apk.VersionName} ({apk.LongVersionCode}), {got.Length} signer(s), match {got.Length > 0 && want.SequenceEqual(got)}");
        if (got.Length > 0 && want.Length > 0 && !want.SequenceEqual(got))
        {
            return "the download isn't signed like this copy of second hunt, so it wasn't installed.";
        }
        return null; // (no certificate to compare: Android itself refuses a different key)
    }

    static string[] Signers(PackageInfo info)
    {
        Signature[]? signers = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) signers = info.SigningInfo?.GetApkContentsSigners();
#pragma warning disable CA1422, CS0618
        if (signers == null || signers.Length == 0) signers = info.Signatures?.ToArray();
#pragma warning restore CA1422, CS0618
        return (signers ?? Array.Empty<Signature>()).Select(s => Convert.ToHexString(s.ToByteArray()!)).Order().ToArray();
    }
}

// PackageInstaller's answers for AndroidUpdater's session: shows Android's confirm screen, and passes the end back to the
// game's box (a successful self-update usually ends this process before it gets here).
[BroadcastReceiver(Name = "com.mphrecomp.app.UpdateInstallReceiver", Exported = false)]
public class UpdateInstallReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null) return;
        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
        string message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage) ?? "";
        Android.Util.Log.Info("MPHUpdate", $"install status {status} {message}");
        switch (status)
        {
        case PackageInstallStatus.PendingUserAction:
            Intent? confirm = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent))) as Intent
#pragma warning disable CA1422, CS0618
                : intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
#pragma warning restore CA1422, CS0618
            if (confirm == null)
            {
                AndroidUpdater.Pending?.Invoke("the update couldn't be installed. the game is unchanged.");
                return;
            }
            confirm.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(confirm);
            break;
        case PackageInstallStatus.Success:
            AndroidUpdater.Pending?.Invoke("updated. open second hunt again to play the new version.");
            break;
        case PackageInstallStatus.FailureAborted:
            AndroidUpdater.Pending?.Invoke("the update was cancelled. press check to try again.");
            break;
        case PackageInstallStatus.FailureStorage:
            AndroidUpdater.Pending?.Invoke("there isn't enough space for the update. free some up and try again.");
            break;
        default:
            AndroidUpdater.Pending?.Invoke("android refused the update, so the game is unchanged.");
            break;
        }
    }
}
