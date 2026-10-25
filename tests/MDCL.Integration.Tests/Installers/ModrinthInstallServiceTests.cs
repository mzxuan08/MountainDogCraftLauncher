using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Installation;
using MDCL.Core.Instances;
using MDCL.Infrastructure.Installers;
using MDCL.Infrastructure.Metadata;
using MDCL.Infrastructure.Storage;
using MDCL.Platform.Windows.Paths;
using Xunit;

namespace MDCL.Integration.Tests.Installers;

public sealed class ModrinthInstallServiceTests
{
    [Fact]
    public async Task PrimaryFile_IsDownloadedToInstanceModsWithSha512()
    {
        using var temp = new TestDirectory();
        var setup = await CreateSetup(temp);
        var version = Version([new ModrinthFile("secondary.jar", Uri, 1, null, new string('b', 40), false),
            new ModrinthFile("demo.jar", Uri, 2, new string('a', 128), new string('c', 40), true)]);
        var downloader = new FakeDownloader();
        var service = new ModrinthInstallService(downloader, setup.Repository, setup.Leases, setup.Paths);
        var receipt = await service.InstallPrimaryFileAsync(setup.Id, version, null, CancellationToken.None);
        Assert.Equal("SHA512", downloader.Artifact?.HashAlgorithm);
        Assert.Equal(Path.Combine("mods", "demo.jar"), downloader.Artifact?.RelativeTargetPath);
        Assert.EndsWith(Path.Combine("mods", "demo.jar"), receipt.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingMod_AndUnsafeFilename_AreRejected()
    {
        using var temp = new TestDirectory();
        var setup = await CreateSetup(temp);
        var downloader = new FakeDownloader();
        var service = new ModrinthInstallService(downloader, setup.Repository, setup.Leases, setup.Paths);
        Directory.CreateDirectory(Path.Combine(setup.GameDir, "mods"));
        await File.WriteAllTextAsync(Path.Combine(setup.GameDir, "mods", "demo.jar"), "existing");
        var conflict = await Assert.ThrowsAsync<MdclException>(() =>
            service.InstallPrimaryFileAsync(setup.Id, Version([new ModrinthFile("demo.jar", Uri, 2, new string('a', 128), null, true)]), null, CancellationToken.None));
        Assert.Equal("MOD_FILE_EXISTS", conflict.Error.ErrorCode);
        var unsafeName = await Assert.ThrowsAsync<MdclException>(() =>
            service.InstallPrimaryFileAsync(setup.Id, Version([new ModrinthFile("../evil.jar", Uri, 2, new string('a', 128), null, true)]), null, CancellationToken.None));
        Assert.Equal("MODRINTH_FILENAME_INVALID", unsafeName.Error.ErrorCode);
    }

    private static Uri Uri => new("https://cdn.example/demo.jar");

    private static ModrinthVersion Version(IReadOnlyList<ModrinthFile> files) =>
        new("v1", "p1", "1.0", "release", ["1.20.1"], ["fabric"], files);

    private static async Task<Setup> CreateSetup(TestDirectory temp)
    {
        var paths = new WindowsAppPaths(temp.Path);
        var repository = new FileInstanceRepository(paths.InstancesRoot);
        var id = InstanceId.New();
        await repository.SaveAsync(new InstanceManifest
        {
            InstanceId = id, DisplayName = "mods", BaseVersion = "1.20.1",
            GameDirectory = ".minecraft", CreatedAt = DateTimeOffset.UtcNow
        }, 0, CancellationToken.None);
        return new Setup(id, paths, repository, new FileInstanceLeaseManager(paths.InstancesRoot),
            Path.Combine(paths.InstancesRoot, id.ToString(), ".minecraft"));
    }

    private sealed record Setup(InstanceId Id, WindowsAppPaths Paths, FileInstanceRepository Repository,
        FileInstanceLeaseManager Leases, string GameDir);

    private sealed class FakeDownloader : IArtifactDownloader
    {
        public ArtifactSpec? Artifact { get; private set; }
        public Task<ArtifactReceipt> EnsureAsync(ArtifactSpec artifact, string targetRoot, IProgress<TransferEvent>? progress, CancellationToken ct)
        {
            Artifact = artifact;
            var path = Path.Combine(targetRoot, artifact.RelativeTargetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "jar");
            return Task.FromResult(new ArtifactReceipt(artifact.ArtifactKey, path, artifact.ExpectedHash!, artifact.CandidateUris[0], false));
        }
    }
}
