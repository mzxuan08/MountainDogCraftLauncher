using System.Text.Json;

namespace MDCL.Core.Versions;

public static class VersionManifestParser
{
    public static VersionManifest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var latest = root.GetProperty("latest");
        var versions = new List<VersionManifestEntry>();
        foreach (var item in root.GetProperty("versions").EnumerateArray())
        {
            versions.Add(new VersionManifestEntry(
                item.GetProperty("id").GetString()!,
                item.GetProperty("url").GetString()!,
                item.TryGetProperty("sha1", out var sha) ? sha.GetString() : null,
                item.TryGetProperty("type", out var type) ? type.GetString() ?? "unknown" : "unknown"));
        }
        return new VersionManifest(latest.GetProperty("release").GetString()!, latest.GetProperty("snapshot").GetString()!, versions);
    }
}
