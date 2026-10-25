using MDCL.Application.Installation;
using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Installation;
using MDCL.Core.Instances;
using MDCL.Core.Java;
using MDCL.Core.Versions;
using Xunit;

namespace MDCL.Integration.Tests.Recovery;

public sealed class VanillaInstallPlannerTests
{
    private static readonly TargetPlatform Win = new(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64);

    [Fact]
    public async Task Planner_BuildsClientLibrariesAssetsAndLoggingArtifacts()
    {
        var resolved = Resolved(sha1: new string('a', 40));
        var planner = new VanillaInstallPlanner(new StubResolver(resolved), new StubAssetIndex());
        var plan = await planner.CreateAsync(new InstallRequest(InstanceId.New(), "1.20.1", LoaderDescriptor.Vanilla, Win), CancellationToken.None);
        Assert.Contains(plan.Steps, x => x.StepId == "client");
        Assert.Contains(plan.Steps, x => x.StepId == "libraries");
        Assert.Contains(plan.Steps, x => x.StepId == "assets-index");
        Assert.Contains(plan.Steps, x => x.StepId == "assets");
        Assert.Contains(plan.Steps, x => x.StepId == "logging");
        var artifacts = plan.Steps.SelectMany(x => x.Artifacts).ToArray();
        Assert.Contains(artifacts, x => x.RelativeTargetPath == "versions/1.20.1/client.jar");
        Assert.Contains(artifacts, x => x.RelativeTargetPath == "libraries/g/a/1/a-1.jar");
        Assert.Contains(artifacts, x => x.RelativeTargetPath == "libraries/g/a/1/a-1-natives-windows.jar");
        Assert.Equal(2, artifacts.Count(x => x.ArtifactKey.StartsWith("asset:", StringComparison.Ordinal)));
        Assert.Equal("SHA1", artifacts[0].HashAlgorithm);
        Assert.Contains("win-x64", plan.TargetSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingOfficialHash_RejectsInstallPlan()
    {
        var resolved = Resolved(sha1: null);
        var planner = new VanillaInstallPlanner(new StubResolver(resolved), new StubAssetIndex());
        var error = await Assert.ThrowsAsync<MdclException>(() =>
            planner.CreateAsync(new InstallRequest(InstanceId.New(), "1.20.1", LoaderDescriptor.Vanilla, Win), CancellationToken.None));
        Assert.Equal("INSTALL_ARTIFACT_HASH_MISSING", error.Error.ErrorCode);
    }

    private static ResolvedVersion Resolved(string? sha1)
    {
        var library = new ResolvedLibrary("g:a:1", "g/a/1/a-1.jar", new Uri("https://example.invalid/a.jar"), sha1, 10,
            new ResolvedNative("natives-windows", "g/a/1/a-1-natives-windows.jar", new Uri("https://example.invalid/a-natives.jar"), new string('b', 40), 11), []);
        return new ResolvedVersion("1.20.1", "net.minecraft.client.main.Main", ["-cp"], ["--version", "1.20.1"],
            [library], [library],
            new ResolvedAssetIndex("5", new Uri("https://example.invalid/assets.json"), new string('c', 40), 10, 20),
            new ResolvedLogging("-Dlog4j.configurationFile=${path}", "client-1.12.xml", new Uri("https://example.invalid/log.xml"), new string('d', 40), 5, "log4j2-xml"),
            new ClientDownload(new Uri("https://example.invalid/client.jar"), sha1, 100),
            new JavaRequirement(17, 17, null, CpuArchitecture.X64, [], []),
            [new MetadataSource("1.20.1", "https://example.invalid/1.20.1.json", null)], "resolvedhash");
    }

    private sealed class StubResolver(ResolvedVersion resolved) : IVersionResolver
    {
        public Task<ResolvedVersion> ResolveAsync(VersionSelection selection, TargetPlatform target, CancellationToken ct) =>
            Task.FromResult(resolved);
    }

    private sealed class StubAssetIndex : IAssetIndexSource
    {
        public Task<IReadOnlyList<AssetObject>> GetObjectsAsync(ResolvedAssetIndex index, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AssetObject>>([
                new AssetObject(new string('1', 40), 1),
                new AssetObject(new string('2', 40), 2),
                new AssetObject(new string('1', 40), 1)
            ]);
    }
}
