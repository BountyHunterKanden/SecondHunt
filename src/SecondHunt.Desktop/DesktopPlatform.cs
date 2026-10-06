using System.Runtime.InteropServices;
using System.Text.Json;
using MphRecomp.App.Platform;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SixLabors.ImageSharp;
using Image = SixLabors.ImageSharp.Image;
using SixLabors.ImageSharp.PixelFormats;

namespace SecondHunt.Desktop;

// The shared app layer's platform seams on Windows (MphRecomp.App/Platform): the log file, the sound device, PNGs
// through ImageSharp (already MphRead's image library), the cache folder.
internal static class DesktopPlatform
{
    static DesktopAudio? _audio;

    public static void Install()
    {
        AppPlatform.CacheDir = DesktopPaths.Cache;
        AppPlatform.DecodeImage = DecodeImage;
        AppPlatform.EncodePng = EncodePng;
        _audio = DesktopAudio.TryOpen();
        if (_audio != null) AppPlatform.Audio = _audio;
    }

    public static string AudioStats() => _audio?.Stats() ?? "no sound device";

    public static void Shutdown()
    {
        _audio?.Dispose();
        _audio = null;
    }

    static (int[] Pixels, int Width, int Height)? DecodeImage(string path)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(path);
        var px = new int[image.Width * image.Height];
        image.CopyPixelDataTo(MemoryMarshal.AsBytes(px.AsSpan()));
        return (px, image.Width, image.Height);
    }

    public static bool EncodePng(string path, byte[] rgba, int w, int h)
    {
        using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(rgba, w, h);
        image.SaveAsPng(path);
        return true;
    }

    // the window's icon: the app icon's 64 px picture, embedded
    public static OpenTK.Windowing.Common.Input.WindowIcon? WindowIcon()
    {
        try
        {
            using Stream? s = typeof(DesktopPlatform).Assembly.GetManifestResourceStream("appicon64.png");
            if (s == null) return null;
            using Image<Rgba32> image = Image.Load<Rgba32>(s);
            var bytes = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(bytes);
            return new OpenTK.Windowing.Common.Input.WindowIcon(new OpenTK.Windowing.Common.Input.Image(image.Width, image.Height, bytes));
        }
        catch (Exception ex)
        {
            Log.Warn("MPHDesktop", "window icon: " + ex.Message);
            return null;
        }
    }
}

// The window's size and full-screen choice, kept between runs (window.json in the data folder)
internal static class WindowPrefs
{
    sealed class Prefs
    {
        public bool Fullscreen { get; set; }
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public bool Maximized { get; set; }
    }

    static string PathOf => Path.Combine(DesktopPaths.Root, "window.json");

    static Prefs Load()
    {
        try
        {
            if (File.Exists(PathOf)) return JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PathOf)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn("MPHDesktop", "window.json: " + ex.Message);
        }
        return new();
    }

    public static void Apply(NativeWindow w)
    {
        Prefs p = Load();
        w.ClientSize = new OpenTK.Mathematics.Vector2i(Math.Max(640, p.Width), Math.Max(360, p.Height));
        w.CenterWindow();
        if (p.Fullscreen) w.WindowState = WindowState.Fullscreen;
        else if (p.Maximized) w.WindowState = WindowState.Maximized;
    }

    public static void Toggle(NativeWindow w)
    {
        w.WindowState = w.WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;
        Save(w);
    }

    public static void Save(NativeWindow w)
    {
        try
        {
            Prefs p = Load();
            p.Fullscreen = w.WindowState == WindowState.Fullscreen;
            p.Maximized = w.WindowState == WindowState.Maximized;
            if (w.WindowState == WindowState.Normal)
            {
                p.Width = w.ClientSize.X;
                p.Height = w.ClientSize.Y;
            }
            File.WriteAllText(PathOf, JsonSerializer.Serialize(p));
        }
        catch (Exception ex)
        {
            Log.Warn("MPHDesktop", "window.json: " + ex.Message);
        }
    }
}
