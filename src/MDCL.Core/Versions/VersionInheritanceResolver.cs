using MDCL.Core.Errors;

namespace MDCL.Core.Versions;

public sealed class VersionInheritanceResolver
{
    private readonly ILibraryMerger _libraryMerger;

    public VersionInheritanceResolver(ILibraryMerger? libraryMerger = null) => _libraryMerger = libraryMerger ?? new LibraryMerger();

    public async Task<VersionDocument> ResolveAsync(
        string versionId,
        Func<string, CancellationToken, Task<VersionDocument>> loader,
        CancellationToken ct)
    {
        var chain = new List<VersionDocument>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = versionId;
        while (true)
        {
            if (!seen.Add(current))
                throw new MdclException(MdclError.Of("VERSION_INHERITANCE_CYCLE", "VersionResolution",
                    $"版本继承出现环：{string.Join(" -> ", chain.Select(x => x.Id))} -> {current}。", false));
            var document = await loader(current, ct);
            if (!string.Equals(document.Id, current, StringComparison.Ordinal))
                throw new MdclException(MdclError.Of("VERSION_ID_MISMATCH", "VersionResolution",
                    $"请求 {current}，响应为 {document.Id}。", false));
            chain.Add(document);
            if (string.IsNullOrWhiteSpace(document.InheritsFrom)) break;
            current = document.InheritsFrom;
            if (chain.Count > 32)
                throw new MdclException(MdclError.Of("VERSION_INHERITANCE_DEPTH", "VersionResolution", "版本继承深度超过 32。", false));
        }

        var merged = chain[^1];
        for (var index = chain.Count - 2; index >= 0; index--) merged = Merge(merged, chain[index]);

        if (string.IsNullOrWhiteSpace(merged.MainClass))
            throw new MdclException(MdclError.Of("VERSION_MAIN_CLASS_MISSING", "VersionResolution", "解析后的版本缺少 mainClass。", false));
        return merged;
    }

    private VersionDocument Merge(VersionDocument parent, VersionDocument child) => new()
    {
        Id = child.Id,
        InheritsFrom = child.InheritsFrom,
        MainClass = child.MainClass ?? parent.MainClass,
        MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
        Arguments = new VersionArguments(
            parent.Arguments.Game.Concat(child.Arguments.Game).ToArray(),
            parent.Arguments.Jvm.Concat(child.Arguments.Jvm).ToArray()),
        Assets = child.Assets ?? parent.Assets,
        AssetIndex = child.AssetIndex ?? parent.AssetIndex,
        Downloads = child.Downloads ?? parent.Downloads,
        JavaVersion = child.JavaVersion ?? parent.JavaVersion,
        Libraries = _libraryMerger.Merge(parent.Libraries, child.Libraries, preferChildVersion: true),
        Logging = child.Logging ?? parent.Logging,
        Type = child.Type ?? parent.Type,
        SourceUrl = child.SourceUrl,
        SourceSha1 = child.SourceSha1
    };
}
