using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Core.Versions;

namespace MDCL.Application.Versions;

public sealed class VersionResolver : IVersionResolver
{
    private readonly IVersionMetadataSource _source;
    private readonly IReadOnlyDictionary<LoaderKind, IComponentVersionProvider> _components;
    private readonly VersionInheritanceResolver _inheritance = new();
    private readonly ResolvedVersionBuilder _builder = new();

    public VersionResolver(IVersionMetadataSource source, IEnumerable<IComponentVersionProvider>? components = null)
    {
        _source = source;
        _components = (components ?? []).ToDictionary(x => x.Kind);
    }

    public async Task<ResolvedVersion> ResolveAsync(VersionSelection selection, TargetPlatform target, CancellationToken ct)
    {
        var manifest = await _source.GetManifestAsync(ct);
        var entry = manifest.Versions.FirstOrDefault(x => string.Equals(x.Id, selection.BaseVersionId, StringComparison.Ordinal))
            ?? throw new MdclException(MdclError.Of("VERSION_NOT_FOUND", "VersionResolution",
                $"官方版本清单中不存在 {selection.BaseVersionId}。", false));

        ComponentVersion? component = null;
        if (selection.LoaderKind != LoaderKind.Vanilla)
        {
            if (!_components.TryGetValue(selection.LoaderKind, out var provider))
                throw new MdclException(MdclError.Of("COMPONENT_NOT_AVAILABLE", "VersionResolution",
                    $"加载器 {selection.LoaderKind} 尚未接入此解析入口。", false));
            component = await provider.GetVersionAsync(selection, entry, target, ct);
        }

        var sources = new Dictionary<string, MetadataSource>(StringComparer.Ordinal);
        var rootId = component?.Document.Id ?? selection.BaseVersionId;
        var merged = await _inheritance.ResolveAsync(rootId, async (id, token) =>
        {
            if (component is not null && string.Equals(id, component.Document.Id, StringComparison.Ordinal))
            {
                sources[id] = new MetadataSource(id, component.SourceUrl, component.Sha1);
                return component.Document;
            }
            var parentEntry = manifest.Versions.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal))
                ?? throw new MdclException(MdclError.Of("VERSION_PARENT_MISSING", "VersionResolution",
                    $"继承父版本 {id} 不在官方清单中。", false));
            var parentRecord = await _source.GetVersionAsync(parentEntry, token);
            sources[id] = new MetadataSource(id, parentRecord.SourceUrl, parentRecord.Sha1);
            return parentRecord.Document;
        }, ct);

        var resolved = _builder.Build(merged, target,
            sources.Values.OrderBy(x => x.VersionId, StringComparer.Ordinal).ToArray());
        return component is null ? resolved : resolved with { LoaderVersion = component.LoaderVersion };
    }
}
