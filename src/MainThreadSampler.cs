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
    private long _windowTotal, _failed;
    private Thread? _thread;
    private volatile bool _stop;
    private IntPtr _threadHandle;
    private readonly ModuleMap _modules;
    public int Hz { get; }
    public long TotalSamples { get; private set; }

    public MainThreadSampler(ModuleMap modules, int hz)
    {
        _modules = modules;
        Hz = Math.Clamp(hz, 10, 2000);
    }

    public bool Start(uint threadId)
    {
        Stop();
        _threadHandle = Native.OpenThread(Native.THREAD_SUSPEND_RESUME | Native.THREAD_GET_CONTEXT | Native.THREAD_QUERY_LIMITED_INFORMATION,
            false, threadId);
        if (_threadHandle == IntPtr.Zero) return false;
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "fsperf-sampler", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return true;
    }

    private void Loop()
    {
        IntPtr timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        void* ctx = NativeMemory.AlignedAlloc(Native.CONTEXT_SIZE, 16);
        long due = -(10_000_000L / Hz);  // relative, 100 ns units
        try {
            while (!_stop) {
                if (timer != IntPtr.Zero && Native.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    Native.WaitForSingleObject(timer, 1000);
                else
                    Thread.Sleep(1);
                ulong rip = 0;
                bool ok = false;
                // between Suspend and Resume: no allocation, no locks (the target thread may hold anything)
                if (Native.SuspendThread(_threadHandle) != 0xFFFFFFFF) {
                    NativeMemory.Clear(ctx, Native.CONTEXT_SIZE);
                    *(uint*)((byte*)ctx + Native.CONTEXT_FLAGS_OFFSET) = Native.CONTEXT_CONTROL;
                    ok = Native.GetThreadContext(_threadHandle, ctx);
                    if (ok) rip = *(ulong*)((byte*)ctx + Native.CONTEXT_RIP_OFFSET);
                    Native.ResumeThread(_threadHandle);
                } else if (Marshal.GetLastWin32Error() != 0) {
                    _stop = true;  // the thread is gone (game exited)
                    break;
                }
                var m = ok ? _modules.Find(rip) : null;
                string key = m?.Name ?? (ok ? "(no module: JIT / stubs)" : "(unreadable)");
                var kind = m?.Kind ?? ModuleKind.Unknown;
                lock (_lock) {
                    _window[key] = _window.TryGetValue(key, out var e) ? (e.kind, e.count + 1) : (kind, 1);
                    _windowTotal++;
                    if (!ok) _failed++;
                    TotalSamples++;
                }
            }
        } finally {
            NativeMemory.AlignedFree(ctx);
            if (timer != IntPtr.Zero) Native.CloseHandle(timer);
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
            _failed = 0;
            return (list, n);
        }
    }

    public bool Running => _thread != null && _thread.IsAlive;

    public void Stop()
    {
        _stop = true;
        _thread?.Join(2000);
        _thread = null;
        if (_threadHandle != IntPtr.Zero) {
            Native.CloseHandle(_threadHandle);
            _threadHandle = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();
}
