using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Instances;

namespace MDCL.Infrastructure.Mods;

public sealed record LocalModInfo(string FileName, string Name, string Version, string Loader, bool Enabled);

public sealed partial class LocalModService
{
    private readonly IAppPaths _paths;
    private readonly IInstanceLeaseManager _leases;

    public LocalModService(IAppPaths paths, IInstanceLeaseManager leases)
    {
        _paths = paths;
        _leases = leases;
    }

    public Task<IReadOnlyList<LocalModInfo>> ListAsync(InstanceManifest instance, CancellationToken ct)
    {
        var directory = ModsDirectory(instance);
        if (!Directory.Exists(directory)) return Task.FromResult<IReadOnlyList<LocalModInfo>>([]);
        var result = new List<LocalModInfo>();
        foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!IsModFile(name)) continue;
            result.Add(ReadMetadata(path));
        }
        return Task.FromResult<IReadOnlyList<LocalModInfo>>(result);
    }

    public async Task SetEnabledAsync(InstanceManifest instance, string fileName, bool enabled, CancellationToken ct)
    {
        await using var lease = await _leases.AcquireAsync(instance.InstanceId, "mods", ct);
        var directory = ModsDirectory(instance);
        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(safeName, fileName, StringComparison.Ordinal) || !IsModFile(safeName))
            throw new MdclException(MdclError.Of("MOD_FILE_INVALID", "Mods", "模组文件名无效。", false, fileName));
        var current = Path.Combine(directory, safeName);
        if (!File.Exists(current))
            throw new MdclException(MdclError.Of("MOD_NOT_FOUND", "Mods", "模组文件不存在。", false, safeName));
        var alreadyEnabled = !safeName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        if (alreadyEnabled == enabled) return;
        var targetName = enabled
            ? safeName[..^".disabled".Length]
            : safeName + ".disabled";
        var target = Path.Combine(directory, targetName);
        if (File.Exists(target))
            throw new MdclException(MdclError.Of("MOD_TARGET_EXISTS", "Mods", "启停目标文件已存在。", false, targetName));
        File.Move(current, target);
    }

    private string ModsDirectory(InstanceManifest instance)
    {
        var instanceRoot = Path.Combine(_paths.InstancesRoot, instance.InstanceId.ToString());
        var gameDir = Path.IsPathRooted(instance.GameDirectory)
            ? instance.GameDirectory
            : Path.Combine(instanceRoot, instance.GameDirectory);
        return Path.Combine(Path.GetFullPath(gameDir), "mods");
    }

    private static bool IsModFile(string name) =>
        name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);

    private static LocalModInfo ReadMetadata(string path)
    {
        var fileName = Path.GetFileName(path);
        var enabled = !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var fabric = archive.GetEntry("fabric.mod.json");
            if (fabric is not null)
            {
                using var stream = fabric.Open();
                using var json = JsonDocument.Parse(stream);
                var root = json.RootElement;
                return new LocalModInfo(fileName,
                    root.TryGetProperty("name", out var name) ? name.GetString() ?? fileName : fileName,
                    root.TryGetProperty("version", out var version) ? version.ToString() : "unknown",
                    "fabric", enabled);
            }
            var forge = archive.GetEntry("META-INF/mods.toml");
            if (forge is not null)
            {
                using var reader = new StreamReader(forge.Open());
                var text = reader.ReadToEnd();
                var modId = Regex.Match(text, "(?m)^\\s*modId\\s*=\\s*[\"']([^\"']+)").Groups[1].Value;
                var display = Regex.Match(text, "(?m)^\\s*displayName\\s*=\\s*[\"']([^\"']+)").Groups[1].Value;
                var version = Regex.Match(text, "(?m)^\\s*version\\s*=\\s*[\"']([^\"']+)").Groups[1].Value;
                return new LocalModInfo(fileName, string.IsNullOrEmpty(display) ? modId : display,
                    string.IsNullOrEmpty(version) ? "unknown" : version, "forge/neoforge", enabled);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException) { }
        return new LocalModInfo(fileName, Path.GetFileNameWithoutExtension(fileName), "unknown", "unknown", enabled);
    }
}
