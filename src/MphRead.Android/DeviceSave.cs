using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Util;
using Android.Widget;

namespace MphRecomp.App;

// SAVE LOG / a replay's SAVE (2026-10-19; the file can still be opened with Second Hunt after): the file into Downloads/Second Hunt through MediaStore (Android
// 10+: no permission, the Files app and any file manager see it; a replay opened from there comes back through
// ReplayImportActivity). Android 8-9 have no such folder for apps without a storage permission: the share sheet instead.
internal static class DeviceSave
{
    const string Folder = "Second Hunt";

    // true = saved (and told); false = this Android can't (the caller shares instead)
    internal static bool ToDownloads(Activity activity, string name, string mime, Action<Stream> write)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return false;
        ContentResolver resolver = activity.ContentResolver!;
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, name);
        values.Put(MediaStore.IMediaColumns.MimeType, mime);
        values.Put(MediaStore.IMediaColumns.RelativePath, Android.OS.Environment.DirectoryDownloads + "/" + Folder);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);
        Android.Net.Uri? uri = null;
        try
        {
            uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values) ?? throw new IOException("Downloads didn't take it");
            using (Stream os = resolver.OpenOutputStream(uri) ?? throw new IOException("it can't be written"))
            {
                write(os);
            }
            var done = new ContentValues();
            done.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, done, null, null);
            Toast.MakeText(activity, $"Saved to Downloads/{Folder}/{name}", ToastLength.Long)!.Show();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Java.Lang.Exception)
        {
            Log.Warn("MPHSave", $"save of {name} failed: {ex.Message}");
            if (uri != null)
            {
                try { resolver.Delete(uri, null, null); } catch (Java.Lang.Exception) { }
            }
            Toast.MakeText(activity, "It couldn't be saved: " + ex.Message, ToastLength.Long)!.Show();
            return true; // (told why: no share sheet on top of it)
        }
    }

    // the share sheet with SAVE TO DOWNLOADS among its targets (2026-10-19, the crash log and log sheets): the file is staged in <cache>/outbox/ and SaveToDownloadsActivity
    // copies it to Downloads/Second Hunt when picked (Android 10+: 8-9 have no such folder for us). Our own Import replay is
    // never offered (Second Hunt isn't in its own share list)
    internal static Intent Chooser(Activity activity, Intent send, string title, string name, string mime, Action<Stream> write)
    {
        Intent chooser = Intent.CreateChooser(send, title)!;
        chooser.PutExtra(Intent.ExtraExcludeComponents, new IParcelable[] { new ComponentName(activity, "com.mphrecomp.app.ReplayImportActivity") });
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return chooser;
        try
        {
            string dir = OutboxDir(activity);
            Directory.CreateDirectory(dir);
            foreach (string old in Directory.GetFiles(dir))
            {
                try { File.Delete(old); } catch (IOException) { }
            }
            using (FileStream f = File.Create(Path.Combine(dir, name)))
            {
                write(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("MPHSave", $"staging {name} failed: {ex.Message}");
            return chooser;
        }
        var save = new Intent(activity, typeof(SaveToDownloadsActivity));
        save.PutExtra(SaveToDownloadsActivity.ExtraName, name);
        save.PutExtra(SaveToDownloadsActivity.ExtraMime, mime);
        chooser.PutExtra(Intent.ExtraInitialIntents, new IParcelable[] { new Android.Content.PM.LabeledIntent(save, activity.PackageName, "Save to Downloads", 0) });
        return chooser;
    }

    internal static string OutboxDir(Context context) => Path.Combine(context.CacheDir!.AbsolutePath, "outbox");
}

// the share sheet's SAVE TO DOWNLOADS (DeviceSave.Chooser): the staged file into Downloads/Second Hunt; no screen. Exported
// so the system sheet can start it on every Android; it only ever copies a file already in our own outbox, by bare name
[Activity(Name = "com.mphrecomp.app.SaveToDownloadsActivity", Exported = true, Label = "Save to Downloads",
    Theme = "@android:style/Theme.Translucent.NoTitleBar", NoHistory = true)]
public class SaveToDownloadsActivity : Activity
{
    internal const string ExtraName = "name", ExtraMime = "mime";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        string? name = Intent?.GetStringExtra(ExtraName);
        string mime = Intent?.GetStringExtra(ExtraMime) ?? "application/octet-stream";
        string? file = name == null || name.Contains('/') || name.Contains('\\') || name.StartsWith('.')
            ? null : Path.Combine(DeviceSave.OutboxDir(this), name);
        if (file == null || !File.Exists(file))
        {
            Toast.MakeText(this, "Nothing to save.", ToastLength.Short)!.Show();
        }
        else
        {
            DeviceSave.ToDownloads(this, name!, mime, s =>
            {
                using FileStream f = File.OpenRead(file);
                f.CopyTo(s);
            });
        }
        Finish();
    }
}
