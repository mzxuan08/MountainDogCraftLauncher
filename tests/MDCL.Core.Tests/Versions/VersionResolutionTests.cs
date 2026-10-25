using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Versions;
using Xunit;

namespace MDCL.Core.Tests.Versions;

public sealed class VersionResolutionTests
{
    private static readonly TargetPlatform WinX64 = new(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64);
    private static readonly TargetPlatform MacArm = new(LauncherOperatingSystem.MacOS, CpuArchitecture.Arm64, CpuArchitecture.Arm64);

    [Fact]
    public async Task Inheritance_MergesParentAndChildArgumentOrder()
    {
        var documents = Fixtures.LoadAll();
        var resolver = new VersionInheritanceResolver();
        var merged = await resolver.ResolveAsync("child-1.20.1", (id, _) => Task.FromResult(documents[id]), CancellationToken.None);
        Assert.Equal("net.minecraft.client.main.Main", merged.MainClass);
        Assert.Equal("1.20.1", merged.InheritsFrom);
        Assert.Equal("child-1.20.1", merged.Id);
        Assert.Equal("-Dchild.property=yes", merged.Arguments.Jvm[^1].Values[^1]);
        Assert.Contains(merged.Arguments.Game, x => x.Values.Contains("--child-flag"));
        Assert.Equal(3, merged.Libraries.Count);
    }

    [Fact]
    public async Task Inheritance_MissingParentIsExplicit()
    {
        var child = Fixtures.Load("child-1.20.1.json");
        var resolver = new VersionInheritanceResolver();
        var ex = await Assert.ThrowsAsync<MdclException>(() => resolver.ResolveAsync("child-1.20.1",
            (id, _) => string.Equals(id, "child-1.20.1", StringComparison.Ordinal)
                ? Task.FromResult(child)
                : throw new MdclException(MdclError.Of("VERSION_PARENT_MISSING", "VersionResolution", "missing")),
            CancellationToken.None));
        Assert.Equal("VERSION_PARENT_MISSING", ex.Error.ErrorCode);
    }

    [Fact]
    public async Task Inheritance_CycleIsRejected()
    {
        var documents = Fixtures.LoadAll();
        var resolver = new VersionInheritanceResolver();
        var ex = await Assert.ThrowsAsync<MdclException>(() => resolver.ResolveAsync("cycle-a",
            (id, _) => Task.FromResult(documents[id]), CancellationToken.None));
        Assert.Equal("VERSION_INHERITANCE_CYCLE", ex.Error.ErrorCode);
    }

    [Fact]
    public void LibraryRules_SelectWindowsX64AndNatives()
    {
        var document = Fixtures.Load("library-rules.json");
        var resolved = new ResolvedVersionBuilder().Build(document, WinX64,
            [new MetadataSource(document.Id, "fixture", null)]);
        Assert.Contains(resolved.Libraries, x => x.Coordinate.StartsWith("com.example:always:", StringComparison.Ordinal));
        Assert.Contains(resolved.Libraries, x => x.Coordinate.StartsWith("com.example:windows:", StringComparison.Ordinal));
        Assert.DoesNotContain(resolved.Libraries, x => x.Coordinate.StartsWith("com.example:mac:", StringComparison.Ordinal));
        Assert.DoesNotContain(resolved.Libraries, x => x.Coordinate.StartsWith("com.example:arm:", StringComparison.Ordinal));
        var native = Assert.Single(resolved.NativeLibraries).Native;
        Assert.NotNull(native);
        Assert.Equal("natives-windows-64", native.Classifier);
        Assert.EndsWith("natives-windows-64.jar", native.RelativePath, StringComparison.Ordinal);
        Assert.Contains("-Dwindows=1", resolved.JvmArguments);
        Assert.DoesNotContain("-Dmac=1", resolved.JvmArguments);
        Assert.DoesNotContain("--demo", resolved.JvmArguments);
    }

    [Fact]
    public void LibraryRules_SelectMacArmLibraries()
    {
        var document = Fixtures.Load("library-rules.json");
        var resolved = new ResolvedVersionBuilder().Build(document, MacArm,
            [new MetadataSource(document.Id, "fixture", null)]);
        Assert.Contains(resolved.Libraries, x => x.Coordinate.StartsWith("com.example:mac:", StringComparison.Ordinal));
        Assert.Contains(resolved.Libraries, x => x.Coordinate.StartsWith("com.example:arm:", StringComparison.Ordinal));
        Assert.DoesNotContain(resolved.Libraries, x => x.Coordinate.StartsWith("com.example:windows:", StringComparison.Ordinal));
        Assert.Contains("-Dmac=1", resolved.JvmArguments);
        Assert.Contains("-Darm=1", resolved.JvmArguments);
        Assert.Empty(resolved.NativeLibraries);
    }

    [Fact]
    public void LegacyArguments_AreTokenizedSeparatelyFromModern()
    {
        var document = Fixtures.Load("legacy-1.6.4.json");
        var resolved = new ResolvedVersionBuilder().Build(document, WinX64,
            [new MetadataSource(document.Id, "fixture", null)]);
        Assert.Equal(["--username", "${auth_player_name}", "--session", "${auth_session}", "--gameDir", "${game_directory}", "--demo"],
            resolved.GameArguments);
        Assert.Equal(8, resolved.JavaRequirement.PreferredMajor);
        Assert.Equal(8, resolved.JavaRequirement.MinimumMajor);
        Assert.Empty(resolved.JvmArguments);
    }

    [Fact]
    public void VersionId_IsOpaqueAndOfficialJavaRequirementIs25()
    {
        foreach (var id in new[] { "26.3", "24w14a", "1.21-pre1" })
        {
            var json = id == "26.3" ? Fixtures.Read("26.3.json") : Fixtures.Read("26.3.json").Replace("\"26.3\"", $"\"{id}\"", StringComparison.Ordinal);
            var document = VersionDocumentParser.Parse(json);
            var resolved = new ResolvedVersionBuilder().Build(document, WinX64,
                [new MetadataSource(id, "fixture", null)]);
            Assert.Equal(id, resolved.VersionId);
            Assert.Equal(25, resolved.JavaRequirement.PreferredMajor);
        }
    }

    [Fact]
    public void ResolvedHash_IsStableAndPlatformSensitive()
    {
        var document = Fixtures.Load("library-rules.json");
        var builder = new ResolvedVersionBuilder();
        var first = builder.Build(document, WinX64, [new MetadataSource(document.Id, "fixture", null)]);
        var second = builder.Build(document, WinX64, [new MetadataSource(document.Id, "fixture", null)]);
        var mac = builder.Build(document, MacArm, [new MetadataSource(document.Id, "fixture", null)]);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.NotEqual(first.ContentHash, mac.ContentHash);
    }

    [Fact]
    public async Task LibraryVersionConflict_IsExplicitWhenPolicyRejects()
    {
        var merger = new LibraryMerger();
        var a = Fixtures.Load("parent-1.20.1.json").Libraries;
        var conflicting = new[]
        {
            new LibraryDocument("com.example:base:2.0", new Dictionary<string, string>(), [], null, null, null)
        };
        Assert.Throws<MdclException>(() => merger.Merge(a, conflicting, preferChildVersion: false));
        var merged = merger.Merge(a, conflicting, preferChildVersion: true);
        Assert.Contains(merged, x => x.Name == "com.example:base:2.0");
    }

    private static class Fixtures
    {
        public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "versions", name));

        public static VersionDocument Load(string name) => VersionDocumentParser.Parse(Read(name), "fixture://" + name);

        public static Dictionary<string, VersionDocument> LoadAll()
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
