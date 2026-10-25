using System.Text.Json;
using System.Text.Json.Serialization;
using MDCL.Core.Errors;
using MDCL.Core.Versions;

namespace MDCL.Infrastructure.Storage;

public sealed class ResolvedVersionStore
{
    public const string PendingFileName = ".mdcl-install-pending.json";
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public async Task CommitAsync(string instanceDirectory, ResolvedVersion version, Func<Task> saveManifest, CancellationToken ct)
    {
        Directory.CreateDirectory(instanceDirectory);
        var pending = Path.Combine(instanceDirectory, PendingFileName);
        // A durable marker makes either side of the two-file commit non-launchable
        // after interruption. Install/repair can safely repeat the whole commit.
        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, new { version.VersionId, version.LoaderVersion }, Options);
            stream.Flush(true);
        }
        await SaveAsync(instanceDirectory, version, ct);
        await saveManifest();
        File.Delete(pending);
    }

    public async Task SaveAsync(string instanceDirectory, ResolvedVersion version, CancellationToken ct)
    {
        Directory.CreateDirectory(instanceDirectory);
        var path = Path.Combine(instanceDirectory, "resolved-version.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, version, Options, ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<ResolvedVersion> LoadAsync(string instanceDirectory, CancellationToken ct)
    {
        if (File.Exists(Path.Combine(instanceDirectory, PendingFileName)))
            throw new MdclException(MdclError.Of("INSTANCE_INSTALL_INCOMPLETE", "Installation",
                "实例安装提交尚未完成，请先执行安装 / 修复。", true));
        var path = Path.Combine(instanceDirectory, "resolved-version.json");
        if (!File.Exists(path))
            throw new MdclException(MdclError.Of(
                "INSTANCE_NOT_INSTALLED", "Installation", "实例尚未完成原版安装。", false));
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ResolvedVersion>(stream, Options, ct)
            ?? throw new MdclException(MdclError.Of(
                "RESOLVED_VERSION_INVALID", "Installation", "resolved-version.json 为空。", false));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
