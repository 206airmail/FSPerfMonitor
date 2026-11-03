// ModuleMap.cs - the target process's loaded modules as address ranges, each with a display name and a kind, so a sampled
// instruction pointer can be attributed. Copyright (c) 2026 Jared Brown. MIT licence.
namespace FSPerf;

public enum ModuleKind { Game, Plugin, Host, Graphics, Windows, Runtime, Other, Unknown }

public sealed record ModuleRange(ulong Start, ulong End, string Name, ModuleKind Kind);

public sealed unsafe class ModuleMap
{
    private ModuleRange[] _ranges = Array.Empty<ModuleRange>();

    public IReadOnlyList<ModuleRange> Ranges => _ranges;

    public static ModuleKind Classify(string path, string exeName, out string display)
    {
        string file = Path.GetFileName(path);
        string lower = path.ToLowerInvariant();
        string f = file.ToLowerInvariant();
        display = file;
        if (string.Equals(file, exeName, StringComparison.OrdinalIgnoreCase)) return ModuleKind.Game;
        // FSPluginHost layout: x64\fs25plugins\<Name>\<Name>.dll; the host is fs25plugins\fsp\fsp.dll, the loader x64\version.dll
        int pi = lower.IndexOf("\\fs25plugins\\", StringComparison.Ordinal);
        if (pi >= 0) {
            string rest = path.Substring(pi + "\\fs25plugins\\".Length);
            string folder = rest.Contains('\\') ? rest.Substring(0, rest.IndexOf('\\')) : Path.GetFileNameWithoutExtension(rest);
            if (folder.Equals("fsp", StringComparison.OrdinalIgnoreCase)) { display = "FSPluginHost (fsp.dll)"; return ModuleKind.Host; }
            display = folder;
            return ModuleKind.Plugin;
        }
        if (f == "version.dll" && !lower.Contains("\\windows\\")) { display = "FSP loader (version.dll)"; return ModuleKind.Host; }
        if (f.StartsWith("nvwgf2um") || f.StartsWith("nvoglv") || f.StartsWith("nvldumd") || f.StartsWith("amdxx") || f.StartsWith("atidxx")
            || f.StartsWith("amdvlk") || f.StartsWith("igd") || f.StartsWith("igc") || f.StartsWith("intelocl") || f == "d3d12core.dll"
            || f == "d3d12.dll" || f == "d3d11.dll" || f == "dxgi.dll" || f == "d3d12sdklayers.dll" || f.StartsWith("dxcore"))
            return ModuleKind.Graphics;
        if (lower.Contains("\\windows\\")) return ModuleKind.Windows;
        if (f.StartsWith("msvcp") || f.StartsWith("vcruntime") || f.StartsWith("ucrtbase")) return ModuleKind.Runtime;
        return ModuleKind.Other;
    }

    public bool Refresh(IntPtr process, string exeName)
    {
        var handles = new IntPtr[2048];
        if (!Native.EnumProcessModulesEx(process, handles, handles.Length * IntPtr.Size, out int needed, Native.LIST_MODULES_64BIT)) return false;
        int n = Math.Min(handles.Length, needed / IntPtr.Size);
        var list = new List<ModuleRange>(n);
        char* buf = stackalloc char[1024];
        for (int i = 0; i < n; i++) {
            if (!Native.GetModuleInformation(process, handles[i], out var mi, sizeof(Native.MODULEINFO))) continue;
            uint len = Native.GetModuleFileNameExW(process, handles[i], buf, 1024);
            string path = len > 0 ? new string(buf, 0, (int)len) : "?";
            var kind = Classify(path, exeName, out string display);
            ulong start = (ulong)mi.lpBaseOfDll;
            list.Add(new ModuleRange(start, start + mi.SizeOfImage, display, kind));
        }
        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        _ranges = list.ToArray();
        return true;
    }

    public ModuleRange? Find(ulong address)
    {
        var r = _ranges;
        int lo = 0, hi = r.Length - 1;
        while (lo <= hi) {
            int mid = (lo + hi) >> 1;
            if (address < r[mid].Start) hi = mid - 1;
            else if (address >= r[mid].End) lo = mid + 1;
            else return r[mid];
        }
        return null;
    }
}
