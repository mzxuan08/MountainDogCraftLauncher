using MDCL.Core.Errors;
using MDCL.Infrastructure.Installers;
using Xunit;

namespace MDCL.Integration.Tests.Installers;

public sealed class ForgeStagingMergerTests
{
    [Fact]
    public async Task Merge_CopiesLibrariesAndVersionJson_AndReusesIdentical()
    {
        using var temp = new TestDirectory();
        var staging = Path.Combine(temp.Path, "stage");
        Directory.CreateDirectory(Path.Combine(staging, "libraries", "net", "example"));
        Directory.CreateDirectory(Path.Combine(staging, "versions", "1.20.1-forge-47.2.0"));
        await File.WriteAllTextAsync(Path.Combine(staging, "libraries", "net", "example", "lib.jar"), "lib");
        await File.WriteAllTextAsync(Path.Combine(staging, "versions", "1.20.1-forge-47.2.0", "1.20.1-forge-47.2.0.json"), "{}");
        var merger = new ForgeStagingMerger();
        var first = await merger.MergeAsync(staging, Path.Combine(temp.Path, "shared"), CancellationToken.None);
        Assert.Equal(1, first.CopiedLibraries);
        Assert.Equal(1, first.CopiedVersions);
        var second = await merger.MergeAsync(staging, Path.Combine(temp.Path, "shared"), CancellationToken.None);
        Assert.Equal(2, second.SkippedIdentical);
    }

    [Fact]
    public async Task ConflictingLibrary_IsRejectedWithoutOverwriting()
    {
        using var temp = new TestDirectory();
        var staging = Path.Combine(temp.Path, "stage");
        var targetDir = Path.Combine(temp.Path, "shared", "libraries", "a");
        Directory.CreateDirectory(Path.Combine(staging, "libraries", "a"));
        Directory.CreateDirectory(targetDir);
        await File.WriteAllTextAsync(Path.Combine(staging, "libraries", "a", "lib.jar"), "new");
        await File.WriteAllTextAsync(Path.Combine(targetDir, "lib.jar"), "old");
        var error = await Assert.ThrowsAsync<MdclException>(() =>
            new ForgeStagingMerger().MergeAsync(staging, Path.Combine(temp.Path, "shared"), CancellationToken.None));
        Assert.Equal("LOADER_STAGING_CONFLICT", error.Error.ErrorCode);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(targetDir, "lib.jar")));
    }
}
