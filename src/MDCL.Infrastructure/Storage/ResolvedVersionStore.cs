using System.Text.Json;
using System.Text.Json.Serialization;
using MDCL.Core.Errors;
using MDCL.Core.Versions;

namespace MDCL.Infrastructure.Storage;

public sealed class ResolvedVersionStore
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

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
