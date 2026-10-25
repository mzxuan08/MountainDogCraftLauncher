using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Core.Versions;
using MDCL.Infrastructure.Installers;
using MDCL.Platform.Windows.Paths;
using Xunit;

namespace MDCL.Integration.Tests.Installers;

public sealed class ForgeInstalledVersionProviderTests
{
    [Fact]
    public async Task ReadsInstalledForgeVersionJsonWithMatchingParent()
    {
        using var temp = new TestDirectory();
        var paths = new WindowsAppPaths(temp.Path);
        var dir = Path.Combine(paths.SharedRoot, "versions", "1.20.1-forge-47.2.0");
        Directory.CreateDirectory(dir);
        var json = """
            { "id": "1.20.1-forge-47.2.0", "inheritsFrom": "1.20.1", "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
              "libraries": [] }
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "1.20.1-forge-47.2.0.json"), json);
        var provider = new ForgeInstalledVersionProvider(paths, LoaderKind.Forge);
        var result = await provider.GetVersionAsync(new VersionSelection("1.20.1", LoaderKind.Forge, "47.2.0"),
            new VersionManifestEntry("1.20.1", "https://piston-meta.mojang.com/1.20.1.json", null, "release"),
            new TargetPlatform(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64), CancellationToken.None);
        Assert.Equal("1.20.1-forge-47.2.0", result.Document.Id);
        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", result.Document.MainClass);
        Assert.Equal(40, result.Sha1?.Length);
    }

    [Fact]
    public async Task MissingMatchingVersion_IsStructuredError()
    {
        using var temp = new TestDirectory();
        var paths = new WindowsAppPaths(temp.Path);
        var provider = new ForgeInstalledVersionProvider(paths, LoaderKind.Forge);
        var error = await Assert.ThrowsAsync<MdclException>(() => provider.GetVersionAsync(
            new VersionSelection("1.20.1", LoaderKind.Forge, "47.2.0"),
            new VersionManifestEntry("1.20.1", "https://piston-meta.mojang.com/1.20.1.json", null, "release"),
            new TargetPlatform(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64), CancellationToken.None));
        Assert.Equal("LOADER_NOT_INSTALLED", error.Error.ErrorCode);
    }
}
