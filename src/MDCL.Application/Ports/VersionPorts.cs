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
