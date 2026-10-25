using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Core.Versions;

namespace MDCL.Application.Versions;

public sealed class VersionResolver : IVersionResolver
{
    private readonly IVersionMetadataSource _source;
    private readonly VersionInheritanceResolver _inheritance = new();
    private readonly ResolvedVersionBuilder _builder = new();

    public VersionResolver(IVersionMetadataSource source) => _source = source;

    public async Task<ResolvedVersion> ResolveAsync(VersionSelection selection, TargetPlatform target, CancellationToken ct)
    {
        if (selection.LoaderKind != LoaderKind.Vanilla)
            throw new MdclException(MdclError.Of("COMPONENT_NOT_AVAILABLE", "VersionResolution",
                $"加载器 {selection.LoaderKind} 尚未接入此解析入口。", false));
        var manifest = await _source.GetManifestAsync(ct);
        var entry = manifest.Versions.FirstOrDefault(x => string.Equals(x.Id, selection.BaseVersionId, StringComparison.Ordinal))
            ?? throw new MdclException(MdclError.Of("VERSION_NOT_FOUND", "VersionResolution",
                $"官方版本清单中不存在 {selection.BaseVersionId}。", false));
        var sources = new Dictionary<string, MetadataSource>(StringComparer.Ordinal);
        var merged = await _inheritance.ResolveAsync(selection.BaseVersionId, async (id, token) =>
        {
            if (string.Equals(id, selection.BaseVersionId, StringComparison.Ordinal))
            {
                var record = await _source.GetVersionAsync(entry, token);
                sources[id] = new MetadataSource(id, record.SourceUrl, record.Sha1);
                return record.Document;
            }
            var parentEntry = manifest.Versions.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal))
                ?? throw new MdclException(MdclError.Of("VERSION_PARENT_MISSING", "VersionResolution",
                    $"继承父版本 {id} 不在官方清单中。", false));
            var parentRecord = await _source.GetVersionAsync(parentEntry, token);
            sources[id] = new MetadataSource(id, parentRecord.SourceUrl, parentRecord.Sha1);
            return parentRecord.Document;
        }, ct);

        return _builder.Build(merged, target,
            sources.Values.OrderBy(x => x.VersionId, StringComparer.Ordinal).ToArray());
    }
}
