// Best-effort return of unused memory to the OS.
//
// This extension is a background process: it wakes once a minute (poll, chart
// render, metadata, icons) and is otherwise untouched, yet .NET keeps the GC
// heap committed and the pages resident after every burst. That is how a
// process with a ~20 MB live heap ended up sitting on ~150 MB.
//
// Nothing here changes behaviour: an idle-time collecting GC (which also
// compacts the large object heap, where the chart canvases land) followed by a
// working-set trim, throttled to a few minutes. The next access simply faults
// the pages back in.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CmdPal.Ext.CryptoMonitor.Services;

internal static class Memory
{
    private static readonly long TrimIntervalTicks = TimeSpan.FromMinutes(3).Ticks;

    private static long _lastTrimUtcTicks;

    /// Throttled, thread-safe and never throws. Returns immediately: the actual
    /// collection runs on the thread pool so a slow finalizer queue can never
    /// stall the caller (the poll loop calls this on its way out).
    public static void TrimIdle()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastTrimUtcTicks);
        if (now - last < TrimIntervalTicks)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _lastTrimUtcTicks, now, last) != last)
        {
            return; // another caller just took this turn
        }

        _ = Task.Run(TrimCore);
    }

    private static void TrimCore()
    {
        try
        {
            // Aggressive = compact everything, LOH included. Without it the
            // freed chart canvases stay as holes in the LOH segments forever
            // (sweeping never gives that space back).
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        catch
        {
            // a failed collection is not worth reporting
        }

        try
        {
            _ = EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
            // trimming is opportunistic
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
