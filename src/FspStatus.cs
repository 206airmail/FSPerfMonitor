// FspStatus.cs - FSPluginHost's runtime status (<profile>\fsp\fsp.status.xml, documented in FSPluginHost docs/ABI.md
// "Runtime files"): host version, each plugin's state and the counters it publishes as status fields. This file format
// is FSPerf's ONLY link to FSPluginHost. Copyright (c) 2026 Jared Brown. MIT licence.
using System.Xml;

namespace FSPerf;

public sealed record PluginStatus(string Name, string Version, string State, string Reason, Dictionary<string, string> Fields);

public sealed record FspStatusInfo(bool Present, string Version, string Channel, string Build, List<PluginStatus> Plugins,
    List<string> Services, DateTime? Written);

public static class FspStatus
{
    public static FspStatusInfo Read(string profileDir)
    {
        string path = Path.Combine(profileDir, "fsp", "fsp.status.xml");
        var none = new FspStatusInfo(false, "", "", "", new(), new(), null);
        try {
            if (!File.Exists(path)) return none;
            var doc = new XmlDocument();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                doc.Load(fs);
            var root = doc.DocumentElement;
            if (root == null || root.Name != "fsp") return none;
            var plugins = new List<PluginStatus>();
            foreach (XmlElement p in root.GetElementsByTagName("plugin")) {
                var fields = new Dictionary<string, string>();
                foreach (XmlElement f in p.GetElementsByTagName("field")) fields[f.GetAttribute("key")] = f.GetAttribute("value");
                plugins.Add(new PluginStatus(p.GetAttribute("name"), p.GetAttribute("version"), p.GetAttribute("state"), p.GetAttribute("reason"), fields));
            }
            var services = new List<string>();
            foreach (XmlElement s in root.GetElementsByTagName("service"))
                services.Add($"{s.GetAttribute("name")} {s.GetAttribute("version")} ({s.GetAttribute("owner")})");
            return new FspStatusInfo(true, root.GetAttribute("version"), root.GetAttribute("channel"), root.GetAttribute("build"), plugins,
                services, File.GetLastWriteTime(path));
        } catch (Exception) {
            return none;  // the host rewrites it while we read: next tick
        }
    }
}
