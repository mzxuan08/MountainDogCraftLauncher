using System.Security.Cryptography;
using System.Text.Json;
using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Core.Versions;

namespace MDCL.Infrastructure.Installers;

public sealed class ForgeInstalledVersionProvider : IComponentVersionProvider
{
    private readonly IAppPaths _paths;
    public ForgeInstalledVersionProvider(IAppPaths paths, LoaderKind kind)
    {
        _paths = paths;
        Kind = kind;
    }

    public LoaderKind Kind { get; }

    public async Task<ComponentVersion> GetVersionAsync(
        VersionSelection selection, VersionManifestEntry baseEntry, TargetPlatform target, CancellationToken ct)
    {
        var root = Path.Combine(_paths.SharedRoot, "versions");
        if (!Directory.Exists(root))
            throw new MdclException(MdclError.Of("LOADER_NOT_INSTALLED", "Forge", "共享版本目录不存在。", false));
        var candidates = new List<VersionDocument>();
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(file);
                var document = VersionDocumentParser.Parse(await File.ReadAllTextAsync(fullPath, ct), fullPath);
                if (Matches(document, selection)) candidates.Add(document);
            }
            catch (JsonException) { }
        }
        var selected = candidates.OrderByDescending(x => x.Id, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new MdclException(MdclError.Of("LOADER_VERSION_NOT_FOUND", "Forge",
                $"没有找到 {Kind} {selection.LoaderVersion} 的已安装版本 JSON。", false));
        var bytes = await File.ReadAllBytesAsync(selected.SourceUrl, ct);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        return new ComponentVersion(selected, selected.SourceUrl, sha1,
            selection.LoaderVersion ?? selected.Id);
    }

    private bool Matches(VersionDocument document, VersionSelection selection)
    {
        if (!string.Equals(document.InheritsFrom, selection.BaseVersionId, StringComparison.Ordinal)) return false;
        var marker = Kind == LoaderKind.Forge ? "forge" : "neoforge";
        if (!document.Id.Contains(marker, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(selection.LoaderVersion)) return true;
        return document.Id.Contains(selection.LoaderVersion, StringComparison.OrdinalIgnoreCase);
    }
}
