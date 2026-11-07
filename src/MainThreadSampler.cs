// MainThreadSampler.cs - which code the game's main thread is running, from OUTSIDE the game: a few hundred times a second
// the thread is suspended for microseconds, its instruction pointer read and the thread resumed; the address is attributed
// to a module (game, each FSPluginHost plugin, the host, the graphics driver, Windows). No injection, no admin rights.
// Leaf attribution: time a plugin's hook spends in game code it calls counts as game (inclusive time needs stack walks).
// Copyright (c) 2026 Jared Brown. MIT licence.
using System.Runtime.InteropServices;

namespace FSPerf;

public sealed unsafe class MainThreadSampler : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (ModuleKind kind, long count)> _window = new();
    private long _windowTotal;
    // one worker per Start: it owns its thread handle (closed by the worker itself on exit) and its stop flag, so a Stop()
    // whose Join times out never closes a handle the worker may still be about to ResumeThread with
    private sealed class Worker { public volatile bool Stop; public IntPtr Handle; public Thread? Thread; }
    private Worker? _worker;
    private readonly ModuleMap _modules;
    public int Hz { get; }

    public MainThreadSampler(ModuleMap modules, int hz)
    {
        _modules = modules;
        Hz = Math.Clamp(hz, 10, 2000);
    }

    public bool Start(uint threadId)
    {
        Stop();
        var h = Native.OpenThread(Native.THREAD_SUSPEND_RESUME | Native.THREAD_GET_CONTEXT | Native.THREAD_QUERY_LIMITED_INFORMATION,
            false, threadId);
        if (h == IntPtr.Zero) return false;
        var w = new Worker { Handle = h };
        w.Thread = new Thread(() => Loop(w)) { IsBackground = true, Name = "fsperf-sampler", Priority = ThreadPriority.AboveNormal };
        _worker = w;
        w.Thread.Start();
        return true;
    }

    private void Loop(Worker w)
    {
        IntPtr timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        void* ctx = NativeMemory.AlignedAlloc(Native.CONTEXT_SIZE, 16);
        long due = -(10_000_000L / Hz);  // relative, 100 ns units
        try {
            while (!w.Stop) {
                if (timer != IntPtr.Zero && Native.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    Native.WaitForSingleObject(timer, 1000);
                else
                    Thread.Sleep(1);
                ulong rip = 0;
                bool ok = false;
                // between Suspend and Resume: no allocation, no locks (the target thread may hold anything)
                if (Native.SuspendThread(w.Handle) != 0xFFFFFFFF) {
                    try {
                        NativeMemory.Clear(ctx, Native.CONTEXT_SIZE);
                        *(uint*)((byte*)ctx + Native.CONTEXT_FLAGS_OFFSET) = Native.CONTEXT_CONTROL;
                        ok = Native.GetThreadContext(w.Handle, ctx);
                        if (ok) rip = *(ulong*)((byte*)ctx + Native.CONTEXT_RIP_OFFSET);
                    } finally {
                        Native.ResumeThread(w.Handle);  // always: a suspended game main thread is a frozen game
                    }
                } else if (Marshal.GetLastWin32Error() != 0) {
                    w.Stop = true;  // the thread is gone (game exited)
                    break;
                }
                var m = ok ? _modules.Find(rip) : null;
                string key = m?.Name ?? (ok ? "(no module: JIT / stubs)" : "(unreadable)");
                var kind = m?.Kind ?? ModuleKind.Unknown;
                lock (_lock) {
                    _window[key] = _window.TryGetValue(key, out var e) ? (e.kind, e.count + 1) : (kind, 1);
                    _windowTotal++;
                }
            }
        } finally {
            NativeMemory.AlignedFree(ctx);
            if (timer != IntPtr.Zero) Native.CloseHandle(timer);
            Native.CloseHandle(w.Handle);  // the worker's own handle: closed only once it can no longer be used
        }
    }

    public sealed record Share(string Name, string Kind, double Percent, long Samples);

    // the shares since the last call (one dashboard tick), largest first
    public (List<Share> shares, long samples) TakeWindow()
    {
        lock (_lock) {
            var list = _window.Select(kv => new Share(kv.Key, kv.Value.kind.ToString(),
                _windowTotal > 0 ? 100.0 * kv.Value.count / _windowTotal : 0, kv.Value.count))
                .OrderByDescending(s => s.Samples).ToList();
            long n = _windowTotal;
            _window.Clear();
            _windowTotal = 0;
            return (list, n);
        }
    }

    public bool Running => _worker?.Thread != null && _worker.Thread.IsAlive;

    public void Stop()
    {
        var w = _worker;
        _worker = null;
        if (w == null) return;
        w.Stop = true;
        w.Thread?.Join(2000);  // a worker still running after this exits by itself at its next loop check (and closes its handle)
    }

    public void Dispose() => Stop();
}
