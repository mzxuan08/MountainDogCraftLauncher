using MDCL.Application.Versions;
using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Versions;
using Xunit;

namespace MDCL.Integration.Tests.Versions;

public sealed class VersionResolverTests
{
    [Fact]
    public async Task ResolvesExactIdsAndMergesParent()
    {
        var source = new FakeMetadataSource();
        var resolver = new VersionResolver(source);
        var target = new TargetPlatform(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64);
        var child = await resolver.ResolveAsync(new VersionSelection("child-1.20.1"), target, CancellationToken.None);
        Assert.Equal("child-1.20.1", child.VersionId);
        Assert.Contains(child.Libraries, x => x.Coordinate.StartsWith("com.example:child:", StringComparison.Ordinal));
        Assert.Equal(17, child.JavaRequirement.PreferredMajor);
        Assert.Equal(2, child.Sources.Count);
    }

    [Fact]
    public async Task MissingParent_ReturnsStructuredError()
    {
        var source = new FakeMetadataSource();
        source.Documents["orphan"] = VersionDocumentParser.Parse("""
            { "id": "orphan", "inheritsFrom": "missing-parent", "mainClass": "example.Main" }
            """);
        var resolver = new VersionResolver(source);
        var target = new TargetPlatform(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64);
        var error = await Assert.ThrowsAsync<MdclException>(() => resolver.ResolveAsync(new VersionSelection("orphan"), target, CancellationToken.None));
        Assert.Equal("VERSION_PARENT_MISSING", error.Error.ErrorCode);
    }

    [Fact]
    public async Task OpaqueVersionIds_ResolveWithoutSemverOrdering()
    {
        var source = new FakeMetadataSource();
        var ids = new[] { "26.3", "24w14a", "1.21-pre1", "1.9", "1.10" };
        foreach (var id in ids)
        {
            source.Documents[id] = VersionDocumentParser.Parse($$"""
                { "id": "{{id}}", "mainClass": "example.Main", "javaVersion": { "majorVersion": 25 } }
                """);
        }
        var resolver = new VersionResolver(source);
        var target = new TargetPlatform(LauncherOperatingSystem.MacOS, CpuArchitecture.Arm64, CpuArchitecture.Arm64);
        foreach (var id in ids)
        {
            var resolved = await resolver.ResolveAsync(new VersionSelection(id), target, CancellationToken.None);
            Assert.Equal(id, resolved.VersionId);
        }
    }

    [Fact]
    public async Task SameInput_ProducesSameContentHash()
    {
        var resolver = new VersionResolver(new FakeMetadataSource());
        var target = new TargetPlatform(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64);
        var first = await resolver.ResolveAsync(new VersionSelection("1.20.1"), target, CancellationToken.None);
        var second = await resolver.ResolveAsync(new VersionSelection("1.20.1"), target, CancellationToken.None);
        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    private sealed class FakeMetadataSource : MDCL.Application.Ports.IVersionMetadataSource
    {
        public Dictionary<string, VersionDocument> Documents { get; } = LoadFixtures();

        public Task<VersionManifest> GetManifestAsync(CancellationToken ct)
        {
            var entries = Documents.Values.OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => new VersionManifestEntry(x.Id, $"https://example.invalid/{x.Id}.json", x.SourceSha1, "release")).ToArray();
            return Task.FromResult(new VersionManifest("26.3", "24w14a", entries));
        }

        public Task<VersionMetadataRecord> GetVersionAsync(VersionManifestEntry entry, CancellationToken ct)
        {
            if (!Documents.TryGetValue(entry.Id, out var document))
                throw new MdclException(MdclError.Of("VERSION_PARENT_MISSING", "VersionResolution", $"missing {entry.Id}"));
            return Task.FromResult(new VersionMetadataRecord(document, entry.Url, entry.Sha1));
        }

        private static Dictionary<string, VersionDocument> LoadFixtures()
        {
            var result = new Dictionary<string, VersionDocument>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "versions"), "*.json"))
            {
                var document = VersionDocumentParser.Parse(File.ReadAllText(file), "fixture://" + Path.GetFileName(file));
                result[document.Id] = document;
            }
            return result;
        }
    }
}
