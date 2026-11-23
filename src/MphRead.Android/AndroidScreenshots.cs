using System;
using System.IO;
using Android.Content;
using Android.Graphics;
using Android.Provider;

namespace MphRecomp.App;

// Photo mode's screenshots on Android (CampaignRenderer.PhotoMode.cs): into the gallery's Pictures/SecondHunt through
// MediaStore (Android 10+, no permission). Android 8-9 have no such folder for an app without a storage permission: the
// app's own Pictures/SecondHunt (Android/data/<app>/files/Pictures), which a file manager still reaches.
internal static class AndroidScreenshots
{
    const string Folder = "SecondHunt";

    // worker thread: where it went (shown to the player), or null
    internal static string? Save(Context context, byte[] rgba, int w, int h, string name)
    {
        using Bitmap bmp = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
        bmp.CopyPixelsFromBuffer(Java.Nio.ByteBuffer.Wrap(rgba));
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            string dir = System.IO.Path.Combine(context.GetExternalFilesDir(Android.OS.Environment.DirectoryPictures)?.AbsolutePath
                ?? context.FilesDir!.AbsolutePath, Folder);
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, name);
            using (FileStream fs = File.Create(path))
            {
                if (!bmp.Compress(Bitmap.CompressFormat.Png!, 100, fs)) return null;
            }
            return path;
        }
        ContentResolver resolver = context.ContentResolver!;
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, name);
        values.Put(MediaStore.IMediaColumns.MimeType, "image/png");
        values.Put(MediaStore.IMediaColumns.RelativePath, Android.OS.Environment.DirectoryPictures + "/" + Folder);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);
        Android.Net.Uri? uri = null;
        try
        {
            uri = resolver.Insert(MediaStore.Images.Media.ExternalContentUri!, values) ?? throw new IOException("the gallery didn't take it");
            using (Stream os = resolver.OpenOutputStream(uri) ?? throw new IOException("it can't be written"))
            {
                if (!bmp.Compress(Bitmap.CompressFormat.Png!, 100, os)) throw new IOException("the PNG wasn't written");
            }
            var done = new ContentValues();
            done.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, done, null, null);
            return Android.OS.Environment.DirectoryPictures + "/" + Folder + "/" + name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Java.Lang.Exception)
        {
            Android.Util.Log.Warn("MPHPhoto", $"screenshot {name} not saved: {ex.Message}");
            if (uri != null)
            {
                try { resolver.Delete(uri, null, null); } catch (Java.Lang.Exception) { }
            }
            return null;
        }
    }
}
