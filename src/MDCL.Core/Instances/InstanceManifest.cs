using System.Text.Json.Serialization;

namespace MDCL.Core.Instances;

public readonly record struct InstanceId(Guid Value)
{
    public static InstanceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public enum LoaderKind { Vanilla, Fabric, Forge, NeoForge }

public sealed record LoaderDescriptor(LoaderKind Kind, string? Version = null)
{
    public static LoaderDescriptor Vanilla { get; } = new(LoaderKind.Vanilla);
}

public sealed record JavaPolicy(
    string? BoundRuntimeId,
    bool PreferManaged,
    CpuArchitecture GameArchitecture,
    int? PreferredMajor = null);

public sealed record InstanceManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public InstanceId InstanceId { get; init; }
    public required string DisplayName { get; init; }
    public required string BaseVersion { get; init; }
    public LoaderDescriptor Loader { get; init; } = LoaderDescriptor.Vanilla;
    public required string GameDirectory { get; init; }
    public JavaPolicy JavaPolicy { get; init; } = new(null, true, CpuArchitecture.X64);
    public int MemoryMiB { get; init; } = 4096;
    public IReadOnlyList<string> ExtraJvmArgs { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public long Revision { get; init; }

    [JsonIgnore]
    public string InstanceDirectoryName => InstanceId.ToString();
}
