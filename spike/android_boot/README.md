# M1 boot spike — Android GLES surface

Purpose: de-risk the single biggest unknown before the real M1 work — does the
`.NET 9 + net9.0-android + direct GLES` stack run efficiently on the **Odin 2
Portal**? This is a throwaway spike, standalone from the MphRead runtime.

What it does on-device: opens a landscape `GLSurfaceView` with a GLES 3 context,
clears every frame to dark blue, and logs `GL_VENDOR / GL_RENDERER / GL_VERSION`
to logcat. If that appears (expect an Adreno 740 on the 8 Gen 2), the toolchain is
proven and the renderer port has a home.

## Build (once the toolchain is provisioned)

Prereqs (being provisioned this session): .NET 9 SDK ✓, `android` workload ✓,
a JDK 17, and an Android platform SDK (API 35).

```
dotnet build spike/android_boot/BootSpike.csproj -c Release \
  -p:JavaSdkDirectory=<jdk17> -p:AndroidSdkDirectory=<android-sdk>
```

Then install the produced APK via `adb install`, or re-sign with the buusfury
pipeline (V1+V2+V3) if the debug signature won't install on the Odin.

Status: scaffolded; not yet compiled (waiting on JDK 17 + Android platform SDK
download). GLES binding choice for the *real* renderer (Silk.NET vs. hand-rolled
vs. these platform bindings) is the M1 decision this spike informs.
