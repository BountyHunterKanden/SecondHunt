using System.Runtime.InteropServices;
using MphRecomp.App.Platform;

namespace SecondHunt.Desktop;

// Whether the game's sound comes out of loudspeakers (VoiceDevices.OnLoudspeaker): the default playback device's form
// factor from Windows' audio endpoint properties. Headphones, a headset or a handset = no; anything else (speakers, a
// monitor over HDMI, line out, unknown) = yes, so the music ducks while you push to talk and the mic doesn't send it
// (owner 2026-10-31). A wrong yes only ducks your own music for a moment; a wrong no sends it, hence "unknown = yes".
// (owner 2026-11-05: a wired USB call-centre headset reads "speakers") a speaker or unknown endpoint that is one device
// with the default microphone (same PKEY_Device_ContainerId) is a headset too.
// Asked at most once a second (the duck timer calls this every 20 ms).
internal static class AudioOutputWindows
{
    static long s_at = -10_000; // (not long.MinValue: now - MinValue overflows and never asks)
    static volatile bool s_speaker = true;
    static int s_logged = -1;

    public static bool OnLoudspeaker()
    {
        long now = Environment.TickCount64;
        if (now - s_at < 1000)
        {
            return s_speaker;
        }
        s_at = now;
        uint form = FormFactor();
        bool oneDevice = (form is FormSpeakers or FormUnknown) && SameDeviceAsMic();
        bool speaker = !oneDevice && form is not (FormHeadphones or FormHeadset or FormHandset);
        int logKey = (int)form | (oneDevice ? 0x100 : 0);
        if (logKey != s_logged)
        {
            s_logged = logKey;
            Log.Info("MPHVoice", speaker
                ? $"sound on the speakers (form factor {FormName(form)}): music ducks while you push to talk"
                : oneDevice ? $"{FormName(form)} on the microphone's own device (a USB headset): no self duck"
                : $"{FormName(form)}: no self duck");
        }
        s_speaker = speaker;
        return speaker;
    }

    const uint FormSpeakers = 1, FormHeadphones = 3, FormHeadset = 5, FormHandset = 6, FormUnknown = 10;

    static string FormName(uint form) => form switch
    {
        0 => "remote", 1 => "speakers", 2 => "line level", 3 => "headphones", 4 => "microphone", 5 => "headset",
        6 => "handset", 7 => "digital passthrough", 8 => "S/PDIF", 9 => "display", _ => "unknown",
    };

    // the default console render endpoint's PKEY_AudioEndpoint_FormFactor, FormUnknown on any failure
    static uint FormFactor()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IPropertyStore? store = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out device) != 0 || device == null)
            {
                return FormUnknown;
            }
            if (device.OpenPropertyStore(0 /* STGM_READ */, out store) != 0 || store == null)
            {
                return FormUnknown;
            }
            var key = new PropertyKey { Fmtid = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), Pid = 0 };
            var value = new PropVariant();
            if (store.GetValue(ref key, out value) != 0)
            {
                return FormUnknown;
            }
            uint form = value.Vt == 19 /* VT_UI4 */ ? value.UInt : FormUnknown;
            PropVariantClear(ref value);
            return form;
        }
        catch (Exception ex)
        {
            if (s_logged != -2)
            {
                s_logged = -2;
                Log.Warn("MPHVoice", "output device form factor: " + ex.Message);
            }
            return FormUnknown;
        }
        finally
        {
            if (store != null) Marshal.ReleaseComObject(store);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    // the default playback and default capture endpoints belong to one physical device (PKEY_Device_ContainerId)
    static bool SameDeviceAsMic()
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Guid? render = ContainerId(enumerator, 0 /* eRender */), capture = ContainerId(enumerator, 1 /* eCapture */);
            return render != null && render == capture;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    static Guid? ContainerId(IMMDeviceEnumerator enumerator, int flow)
    {
        IMMDevice? device = null;
        IPropertyStore? store = null;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, 0 /* eConsole */, out device) != 0 || device == null) return null;
            if (device.OpenPropertyStore(0 /* STGM_READ */, out store) != 0 || store == null) return null;
            var key = new PropertyKey { Fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Pid = 2 };
            if (store.GetValue(ref key, out PropVariant value) != 0) return null;
            Guid? id = value.Vt == 72 /* VT_CLSID */ && value.Ptr != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(value.Ptr) : null;
            PropVariantClear(ref value);
            return id;
        }
        finally
        {
            if (store != null) Marshal.ReleaseComObject(store);
            if (device != null) Marshal.ReleaseComObject(device);
        }
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    // PROPVARIANT: vt, three reserved words, then the value (16 bytes on 32-bit, 24 on 64-bit)
    [StructLayout(LayoutKind.Explicit)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint UInt;
        [FieldOffset(8)] public IntPtr Ptr;
        [FieldOffset(16)] public IntPtr Ptr2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore? properties);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }
}
