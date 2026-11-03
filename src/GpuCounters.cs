// GpuCounters.cs - the game process's GPU load from Windows' own counters (what Task Manager shows): "GPU Engine" 3D
// utilisation and "GPU Process Memory" dedicated usage, filtered to one PID. No admin rights. Copyright (c) 2026 Jared Brown.
// MIT licence.
namespace FSPerf;

public sealed unsafe class GpuCounters : IDisposable
{
    private IntPtr _query, _engine, _memory;
    public bool Available { get; }

    public GpuCounters()
    {
        if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
        bool e = Native.PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engine) == 0;
        bool m = Native.PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _memory) == 0;
        Available = e;
        if (!m) _memory = IntPtr.Zero;
        Native.PdhCollectQueryData(_query);  // rate counters need two samples
    }

    private static List<(string name, double value)> Read(IntPtr counter)
    {
        var result = new List<(string, double)>();
        if (counter == IntPtr.Zero) return result;
        uint size = 0;
        uint r = Native.PdhGetFormattedCounterArrayW(counter, Native.PDH_FMT_DOUBLE, ref size, out uint count, null);
        if (r != Native.PDH_MORE_DATA || size == 0) return result;
        var mem = new byte[size];
        fixed (byte* p = mem) {
            if (Native.PdhGetFormattedCounterArrayW(counter, Native.PDH_FMT_DOUBLE, ref size, out count, p) != 0) return result;
            for (uint i = 0; i < count; i++) {
                byte* item = p + i * Native.PDH_ITEM_SIZE;
                string name = new string(*(char**)item);
                uint status = *(uint*)(item + 8);
                double v = *(double*)(item + 16);
                if (status == 0 || status == 1) result.Add((name, v));  // PDH_CSTATUS_VALID_DATA / NEW_DATA
            }
        }
        return result;
    }

    // collect once per tick; 3D = the busiest 3D engine of this process (Task Manager's "3D"), memory in MB
    public (double threeD, double dedicatedMb) Sample(int pid)
    {
        if (!Available) return (double.NaN, double.NaN);
        Native.PdhCollectQueryData(_query);
        string prefix = $"pid_{pid}_";
        var perEngine = new Dictionary<string, double>();
        foreach (var (name, v) in Read(_engine)) {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith("engtype_3D", StringComparison.Ordinal)) continue;
            // instance = pid_<pid>_luid_<luid>_phys_<n>_eng_<n>_engtype_3D: sum per engine (several contexts share one)
            int at = name.IndexOf("_engtype_", StringComparison.Ordinal);
            string engine = name.Substring(prefix.Length, at - prefix.Length);  // luid_.._phys_n_eng_n
            perEngine[engine] = (perEngine.TryGetValue(engine, out double s) ? s : 0) + v;
        }
        double threeD = perEngine.Count > 0 ? Math.Min(100, perEngine.Values.Max()) : 0;
        double mem = 0;
        foreach (var (name, v) in Read(_memory))
            if (name.StartsWith(prefix, StringComparison.Ordinal)) mem += v;
        return (threeD, _memory == IntPtr.Zero ? double.NaN : mem / 1048576.0);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) Native.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}
