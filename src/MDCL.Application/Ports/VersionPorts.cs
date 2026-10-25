using MDCL.Core;
using MDCL.Core.Versions;

namespace MDCL.Application.Ports;

public interface IVersionMetadataSource
{
    Task<VersionManifest> GetManifestAsync(CancellationToken ct);
    Task<VersionMetadataRecord> GetVersionAsync(VersionManifestEntry entry, CancellationToken ct);
}

public interface IVersionResolver
{
    Task<ResolvedVersion> ResolveAsync(VersionSelection selection, TargetPlatform target, CancellationToken ct);
}

public interface IAssetIndexSource
{
    Task<IReadOnlyList<AssetObject>> GetObjectsAsync(ResolvedAssetIndex index, CancellationToken ct);
}

public sealed record ComponentVersion(VersionDocument Document, string SourceUrl, string? Sha1, string LoaderVersion);

public interface IComponentVersionProvider
{
    MDCL.Core.Instances.LoaderKind Kind { get; }
    Task<ComponentVersion> GetVersionAsync(
        VersionSelection selection, VersionManifestEntry baseEntry, TargetPlatform target, CancellationToken ct);
}
