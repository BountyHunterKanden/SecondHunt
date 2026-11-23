using System.Runtime.InteropServices;
using System.Threading;

namespace SecondHunt.Desktop;

// The game's Rumble Pak (MphRead/Rumble.cs, OPTIONS > RUMBLE PAK) on the pad SDL reads: SDL_RumbleGamepad with both
// motors at the game's strength (1-100 of full) for its time. The game asks from its own step; the request waits for
// the next Read (the thread that owns the pad) and is sent there. A pad without motors ignores it.
internal sealed unsafe partial class Sdl3Pads : MphRead.IRumbleOutput
{
    delegate* unmanaged[Cdecl]<IntPtr, ushort, ushort, uint, byte> _rumble;
    bool _rumbleLooked;
    int _rumbleAsked, _rumbleSent; // request counts: a new one waits while they differ
    int _rumbleStrength, _rumbleMs; // the latest request (strength 0 = stop)

    public void Start(int strength, int ms) => Ask(strength, ms);

    public void Stop() => Ask(0, 0);

    void Ask(int strength, int ms)
    {
        Volatile.Write(ref _rumbleStrength, strength);
        Volatile.Write(ref _rumbleMs, ms);
        Interlocked.Increment(ref _rumbleAsked);
    }

    // from Read, with a pad open
    void SendRumble()
    {
        int asked = Volatile.Read(ref _rumbleAsked);
        if (asked == _rumbleSent) return;
        _rumbleSent = asked;
        if (!_rumbleLooked)
        {
            _rumbleLooked = true;
            if (NativeLibrary.TryGetExport(_lib, "SDL_RumbleGamepad", out IntPtr fn))
            {
                _rumble = (delegate* unmanaged[Cdecl]<IntPtr, ushort, ushort, uint, byte>)fn;
            }
        }
        if (_rumble == null || _pad == IntPtr.Zero) return;
        int strength = Math.Clamp(Volatile.Read(ref _rumbleStrength), 0, 100);
        int ms = Math.Max(0, Volatile.Read(ref _rumbleMs));
        ushort level = (ushort)(strength * 0xFFFF / 100);
        _rumble(_pad, level, level, strength == 0 ? 0u : (uint)ms);
    }
}
