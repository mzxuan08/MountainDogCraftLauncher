using System.Text.Json;
using System.Text.Json.Serialization;
using MDCL.Core.Errors;

namespace MDCL.Core.Instances;

public static class InstanceManifestSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(InstanceManifest manifest) => JsonSerializer.Serialize(manifest, Options);

    public static InstanceManifest Deserialize(string json)
    {
        var manifest = JsonSerializer.Deserialize<InstanceManifest>(json, Options)
            ?? throw new MdclException(MdclError.Of("INSTANCE_MANIFEST_INVALID", "Storage", "实例清单为空。"));
        if (manifest.SchemaVersion > InstanceManifest.CurrentSchemaVersion)
        {
            throw new MdclException(MdclError.Of("INSTANCE_SCHEMA_UNSUPPORTED", "Storage",
                $"实例使用较新的 schema {manifest.SchemaVersion}，当前版本只读拒绝覆盖。"));
        }
        if (manifest.SchemaVersion < 1 || manifest.InstanceId.Value == Guid.Empty)
            throw new MdclException(MdclError.Of("INSTANCE_MANIFEST_INVALID", "Storage", "实例清单缺少有效身份。"));
        return manifest;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
