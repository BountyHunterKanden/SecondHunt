using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SecondHunt.Desktop;

// SETTINGS > LOOK > frame rate (RecompSettings.FpsLimit, owner 2026-10-18): a cap in frames a second with vsync off. Each
// frame starts on its own deadline, one period after the last (a frame that came in late moves the schedule on rather
// than rushing the next ones to catch up). Thread.Sleep(1) on Windows sleeps anywhere from 1 to 15.6 ms, which can't hold
// 240 (4.17 ms), so the wait is a high-resolution waitable timer (Windows 10 1803+; timeBeginPeriod(1) + Sleep before
// that) to about half a millisecond short of the deadline, then a spin for the rest.
internal sealed class FrameLimiter : IDisposable
{
    const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003, Infinite = 0xFFFFFFFF;
    const double SpinMs = 0.5;

    readonly IntPtr _timer;
    readonly bool _periodRaised;
    long _next; // the next frame's start, in Stopwatch ticks (0: none yet)

    public FrameLimiter()
    {
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (_timer == IntPtr.Zero)
        {
            _periodRaised = timeBeginPeriod(1) == 0;
        }
    }

    // starts the schedule over (the cap changed, or the loop waited for something else: minimized)
    public void Reset() => _next = 0;

    // waits until this frame's deadline at fps frames a second
    public void Wait(int fps)
    {
        long period = Stopwatch.Frequency / Math.Max(1, fps);
        long now = Stopwatch.GetTimestamp();
        if (_next == 0 || now - _next > period)
        {
            _next = now + period; // first frame, or more than a frame late: start the schedule from here
            return;
        }
        long spin = (long)(Stopwatch.Frequency * SpinMs / 1000);
        long sleepTicks = _next - now - spin;
        if (sleepTicks > 0)
        {
            double ms = sleepTicks * 1000.0 / Stopwatch.Frequency;
            if (_timer != IntPtr.Zero)
            {
                long due = -(long)(ms * 10_000); // relative, in 100 ns units
                if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    WaitForSingleObject(_timer, Infinite);
                }
            }
            else if (ms >= 1)
            {
                Thread.Sleep((int)ms);
            }
        }
        while (Stopwatch.GetTimestamp() < _next)
        {
            Thread.SpinWait(20);
        }
        _next += period;
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
        if (_periodRaised) timeEndPeriod(1);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint period);
}
