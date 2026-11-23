using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace MphRecomp.App;

// Dev probe (2026-11-16, menu GC leftovers): which thread allocates on the managed heap, and how fast. The menus' Mono
// heap grew ~0.4 MB/s on Android vs 0.21 MB/s from the PC's render thread; this names the rest. Each probed thread calls
// Tick (one slot per thread, alloc-free after its first call); the render thread calls Report, which every 10 s logs
// "MPHPerf alloc by thread: render 205 KB/s, mph-sfx-mixer 0 KB/s, ..., others 12 KB/s; heap +X MB, gen0 N".
// "others" is the process total (GC.GetTotalAllocatedBytes) minus the named threads: the GC bridge, the thread pool,
// threads nobody ticks. Off unless asked: Android `--es allocprobe 1` (GameActivity), PC MPH_ALLOCPROBE=1; never in a
// public build. RELEASE GATE: remove or keep gated before a release (memory project_next_release_gates).
internal static class ThreadAllocProbe
{
    sealed class Slot
    {
        public string Name = "";
        public long Bytes, Last;
    }

    const double ReportSeconds = 10;
    static bool _enabled = Environment.GetEnvironmentVariable("MPH_ALLOCPROBE") == "1";
    static readonly List<Slot> _slots = new();
    [ThreadStatic] static Slot? t_slot;
    static long _start, _total0, _heap0;
    static int _gen0;

    public static bool Enabled => _enabled && !MphRecomp.Config.RecompSettings.PublicBuild;

    public static void Enable() => _enabled = true;

    // Parts of one thread's allocations (2026-11-17, mph-music 240-610 KB/s on the Odin): a call site brackets a step
    // with Mark() and Part(n, mark); the report adds "part name N KB/s" for each
    public enum PartId { MusicRender, MusicWrite, Count }
    static readonly long[] _parts = new long[(int)PartId.Count], _partsLast = new long[(int)PartId.Count];

    public static long Mark() => Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;

    public static void Part(PartId part, long mark)
    {
        if (Enabled)
        {
            Interlocked.Add(ref _parts[(int)part], GC.GetAllocatedBytesForCurrentThread() - mark);
        }
    }

    // the calling thread's allocated bytes so far (its name is taken on the first call)
    public static void Tick(string name)
    {
        if (!Enabled)
        {
            return;
        }
        Slot? slot = t_slot;
        if (slot == null)
        {
            slot = t_slot = new Slot { Name = name, Last = GC.GetAllocatedBytesForCurrentThread() };
            lock (_slots)
            {
                _slots.Add(slot);
            }
        }
        Volatile.Write(ref slot.Bytes, GC.GetAllocatedBytesForCurrentThread());
    }

    // the render thread, once a frame: ticks itself, logs every ReportSeconds
    public static void Report(string name)
    {
        if (!Enabled)
        {
            return;
        }
        Tick(name);
        long now = Stopwatch.GetTimestamp();
        if (_start == 0)
        {
            Restart(now);
            return;
        }
        double secs = Stopwatch.GetElapsedTime(_start, now).TotalSeconds;
        if (secs < ReportSeconds)
        {
            return;
        }
        long total = GC.GetTotalAllocatedBytes(false) - _total0, named = 0;
        var line = new System.Text.StringBuilder("alloc by thread:");
        lock (_slots)
        {
            foreach (Slot slot in _slots)
            {
                long bytes = Volatile.Read(ref slot.Bytes), delta = bytes - slot.Last;
                slot.Last = bytes;
                named += delta;
                line.Append($" {slot.Name} {delta / 1024.0 / secs:0} KB/s,");
            }
        }
        for (int i = 0; i < _parts.Length; i++)
        {
            long bytes = Volatile.Read(ref _parts[i]), delta = bytes - _partsLast[i];
            _partsLast[i] = bytes;
            line.Append($" part {(PartId)i} {delta / 1024.0 / secs:0} KB/s,");
        }
        line.Append($" others {(total - named) / 1024.0 / secs:0} KB/s (all {total / 1024.0 / secs:0} KB/s); heap "
            + $"{(GC.GetTotalMemory(false) - _heap0) / 1048576.0:+0.0;-0.0} MB, gen0 {GC.CollectionCount(0) - _gen0} in {secs:0} s");
        MphRecomp.App.Platform.Log.Info("MPHPerf", line.ToString());
        Restart(Stopwatch.GetTimestamp());
    }

    static void Restart(long now)
    {
        _start = now;
        _total0 = GC.GetTotalAllocatedBytes(false);
        _heap0 = GC.GetTotalMemory(false);
        _gen0 = GC.CollectionCount(0);
    }
}
