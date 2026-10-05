using Android.App;
using Android.OS;
using Android.Opengl;
using Android.Util;
using Javax.Microedition.Khronos.Opengles;

namespace MphRecomp.BootSpike;

// Minimal GLES boot spike. Direct GLES bindings (Android.Opengl.GLES30) — no ANGLE.
// Goal on-device (Odin 2): screen clears to a dark blue and logcat prints the
// GPU's GL_VENDOR / GL_RENDERER / GL_VERSION, confirming the surface + context.
[Activity(
    Label = "MPH Boot Spike",
    MainLauncher = true,
    ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation
        | Android.Content.PM.ConfigChanges.ScreenSize
        | Android.Content.PM.ConfigChanges.KeyboardHidden,
    ScreenOrientation = Android.Content.PM.ScreenOrientation.Landscape)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var view = new GLSurfaceView(this);
        view.SetEGLContextClientVersion(3); // GLES 3.x
        view.SetRenderer(new ClearRenderer());
        SetContentView(view);
    }
}

internal sealed class ClearRenderer : Java.Lang.Object, GLSurfaceView.IRenderer
{
    public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
    {
        Log.Info("MPHBoot", $"GL_VENDOR={GLES30.GlGetString(GLES30.GlVendor)}");
        Log.Info("MPHBoot", $"GL_RENDERER={GLES30.GlGetString(GLES30.GlRenderer)}");
        Log.Info("MPHBoot", $"GL_VERSION={GLES30.GlGetString(GLES30.GlVersion)}");
        GLES30.GlClearColor(0.05f, 0.10f, 0.20f, 1.0f);
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height)
        => GLES30.GlViewport(0, 0, width, height);

    public void OnDrawFrame(IGL10? gl)
        => GLES30.GlClear(GLES30.GlColorBufferBit);
}
