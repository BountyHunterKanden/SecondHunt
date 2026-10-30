using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Util;
using Android.Widget;
using MphRecomp.Replay;

namespace MphRecomp.App;

// Replays in and out of the app (2026-10-19; MphRecomp Replay/ReplayShare.cs). EXPORT (was SHARE) copies the replay to
// <cache>/shared/ and hands the system share sheet a content:// link to it through ReplayShareProvider (read-only, only that
// folder, not exported: the receiving app gets a one-off grant for the one file). A file shared to the app or opened with it
// (ReplayImportActivity: any binary file, since apps rarely keep a .mphreplay's name or type) is checked whole and kept with
// the shared replays (replays/shared/), or refused with the reason.
internal static class ReplaySharing
{
    internal static string Authority(Context context) => context.PackageName + ".replays";

    static string ShareDir(Context context) => Path.Combine(context.CacheDir!.AbsolutePath, "shared");

    internal static void Share(Activity activity, string? path)
    {
        if (path == null || !File.Exists(path))
        {
            Toast.MakeText(activity, "That replay is gone.", ToastLength.Short)!.Show();
            return;
        }
        string copy;
        try
        {
            copy = ReplayShare.ShareCopy(path, ShareDir(activity));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn("MPHReplay", "share copy failed: " + ex.Message);
            Toast.MakeText(activity, "The replay couldn't be shared: " + ex.Message, ToastLength.Long)!.Show();
            return;
        }
        Android.Net.Uri uri = Android.Net.Uri.Parse($"content://{Authority(activity)}/{Android.Net.Uri.Encode(Path.GetFileName(copy))}")!;
        var send = new Intent(Intent.ActionSend);
        send.SetType(ReplayShare.MimeType);
        send.PutExtra(Intent.ExtraStream, uri);
        send.PutExtra(Intent.ExtraSubject, "Second Hunt replay: " + Path.GetFileNameWithoutExtension(copy));
        send.ClipData = ClipData.NewRawUri(Path.GetFileName(copy), uri);
        send.AddFlags(ActivityFlags.GrantReadUriPermission);
        activity.StartActivity(DeviceSave.Chooser(activity, send, "Export the replay", Path.GetFileName(copy), ReplayShare.MimeType, s =>
        {
            using FileStream f = File.OpenRead(copy);
            f.CopyTo(s);
        }));
    }

    // a text report (the log, a crash report) as a .txt through the same provider: the share sheet gets a link to the whole
    // file, not the text in the intent (the Binder takes about 1 MB, a long log is more; owner 2026-10-24)
    internal static Android.Net.Uri? ShareTextFile(Context context, string name, byte[] bytes)
    {
        try
        {
            string dir = ShareDir(context);
            Directory.CreateDirectory(dir);
            foreach (string old in Directory.GetFiles(dir, "second-hunt-*.txt"))
            {
                try { File.Delete(old); } catch (IOException) { }
            }
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
            return Android.Net.Uri.Parse($"content://{Authority(context)}/{Android.Net.Uri.Encode(name)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("MPHReplay", "text share copy failed: " + ex.Message);
            return null;
        }
    }

    internal static string? SharedFile(Context context, Android.Net.Uri uri)
    {
        string? name = uri.LastPathSegment;
        if (name == null || name.Contains('/') || name.Contains('\\') || name.StartsWith('.')) return null;
        string file = Path.Combine(ShareDir(context), name);
        return File.Exists(file) ? file : null;
    }
}

// the share sheet's way to the one shared copy (AndroidManifest.xml's <provider>, authority <package>.replays)
[Register("com.mphrecomp.app.ReplayShareProvider")]
public class ReplayShareProvider : ContentProvider
{
    public override bool OnCreate() => true;

    public override string? GetType(Android.Net.Uri uri) => uri.LastPathSegment?.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) == true ? "text/plain" : ReplayShare.MimeType;

    public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
    {
        string? file = Context != null ? ReplaySharing.SharedFile(Context, uri) : null;
        if (file == null) return null;
        string[] columns = { "_display_name", "_size" }; // (OpenableColumns)
        var cursor = new MatrixCursor(columns, 1);
        cursor.AddRow(new Java.Lang.Object[] { new Java.Lang.String(Path.GetFileName(file)), new Java.Lang.Long(new FileInfo(file).Length) });
        return cursor;
    }

    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
    {
        if (mode != "r") throw new Java.Lang.SecurityException("read only");
        string? file = Context != null ? ReplaySharing.SharedFile(Context, uri) : null;
        if (file == null) throw new Java.IO.FileNotFoundException(uri.ToString());
        return ParcelFileDescriptor.Open(new Java.IO.File(file), ParcelFileMode.ReadOnly);
    }

    public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) => throw new Java.Lang.UnsupportedOperationException();
    public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) => 0;
    public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;
}

// a replay file shared to the app, or opened with it: kept with the shared replays (REPLAYS), or refused with the reason; no screen
[Activity(Name = "com.mphrecomp.app.ReplayImportActivity", Exported = true, Label = "Import replay",
    Theme = "@android:style/Theme.Translucent.NoTitleBar", NoHistory = true)]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = ReplayShare.MimeType)]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataMimeType = ReplayShare.MimeType)]
public class ReplayImportActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Intent? intent = Intent;
        Android.Net.Uri? uri = intent?.Action == Intent.ActionSend
            ? (OperatingSystem.IsAndroidVersionAtLeast(33)
                ? intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Android.Net.Uri))) as Android.Net.Uri
#pragma warning disable CA1422 // (API 26-32: the untyped form)
                : intent.GetParcelableExtra(Intent.ExtraStream) as Android.Net.Uri)
#pragma warning restore CA1422
            : intent?.Data;
        string message;
        if (uri == null)
        {
            message = "Nothing to import.";
        }
        else
        {
            try
            {
                using Stream? stream = ContentResolver!.OpenInputStream(uri);
                if (stream == null) throw new IOException("it can't be opened");
                string dataDir = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath; // (GameActivity's ExternalDir)
                string saved = ReplayShare.Import(dataDir, stream);
                Log.Info("MPHReplay", $"imported {uri} as {Path.GetFileName(saved)}");
                message = "Replay added to MULTIPLAYER > REPLAYS > SHARED WITH ME.";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                or Java.Lang.SecurityException or Java.IO.FileNotFoundException)
            {
                Log.Warn("MPHReplay", $"import of {uri} refused: {ex.Message}");
                message = "Not imported: " + ex.Message;
            }
        }
        Toast.MakeText(this, message, ToastLength.Long)!.Show();
        Finish();
    }
}
