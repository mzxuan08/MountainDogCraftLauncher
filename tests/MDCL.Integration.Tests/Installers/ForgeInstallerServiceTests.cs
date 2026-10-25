using System.Net;
using System.Text;
using MDCL.Application.Ports;
using MDCL.Core.Installation;
using MDCL.Core.Instances;
using MDCL.Infrastructure.Installers;
using Xunit;

namespace MDCL.Integration.Tests.Installers;

public sealed class ForgeInstallerServiceTests
{
    [Theory]
    [InlineData(LoaderKind.Forge, "1.20.1", "47.2.0", "forge-1.20.1-47.2.0-installer.jar")]
    [InlineData(LoaderKind.NeoForge, "1.20.1", "47.1.106", "neoforge-47.1.106-installer.jar")]
    public async Task DownloaderAndRunner_ReceiveOfficialInstallerAndInstallClientArgs(
        LoaderKind kind, string game, string loader, string expectedFile)
    {
        using var temp = new TestDirectory();
        var sha = new string('a', 40);
        var downloader = new FakeDownloader(sha);
        var runner = new FakeRunner();
        var service = new ForgeInstallerService(new HttpClient(new Handler(sha)), downloader, runner);
        var result = await service.InstallAsync(new ForgeInstallRequest(kind, game, loader),
            "java.exe", temp.Path, null, CancellationToken.None);
        Assert.Equal(sha, downloader.Artifact?.ExpectedHash);
        Assert.EndsWith(expectedFile, downloader.Artifact!.CandidateUris[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("java.exe", runner.Executable);
        Assert.Equal(["-Djava.io.tmpdir=" + Path.Combine(result.StagingDirectory, "tmp"),
            "-jar", Path.Combine(result.StagingDirectory, "installer.jar"), "--installClient", result.StagingDirectory], runner.Arguments);
        Assert.Equal(0, result.Process.ExitCode);
        Assert.True(File.Exists(Path.Combine(result.StagingDirectory, "launcher_profiles.json")));
        Assert.True(File.Exists(Path.Combine(result.StagingDirectory, "versions", game, game + ".json")));
    }

    [Fact]
    public async Task FailedInstaller_IsStructuredError()
    {
        using var temp = new TestDirectory();
        var sha = new string('b', 40);
        var service = new ForgeInstallerService(new HttpClient(new Handler(sha)), new FakeDownloader(sha),
            new FakeRunner { ExitCode = 3, Stderr = "processor failed" });
        var error = await Assert.ThrowsAsync<MDCL.Core.Errors.MdclException>(() =>
            service.InstallAsync(new ForgeInstallRequest(LoaderKind.Forge, "1.20.1", "47.2.0"),
                "java.exe", temp.Path, null, CancellationToken.None));
        Assert.Equal("INSTALLER_FAILED", error.Error.ErrorCode);
        Assert.Contains("processor failed", error.Error.Evidence[0], StringComparison.Ordinal);
    }

    private sealed class Handler(string sha) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sha + "  installer.jar") });
    }

    private sealed class FakeDownloader(string sha) : IArtifactDownloader
    {
        public ArtifactSpec? Artifact { get; private set; }
        public Task<ArtifactReceipt> EnsureAsync(ArtifactSpec artifact, string targetRoot, IProgress<TransferEvent>? progress, CancellationToken ct)
        {
            Artifact = artifact;
            var path = Path.Combine(targetRoot, artifact.RelativeTargetPath);
            Directory.CreateDirectory(targetRoot);
            File.WriteAllText(path, "jar");
            return Task.FromResult(new ArtifactReceipt(artifact.ArtifactKey, path, sha, artifact.CandidateUris[0], false));
        }
    }

    private sealed class FakeRunner : IInstallerProcessRunner
    {
        public string Executable { get; private set; } = "";
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public int ExitCode { get; init; }
        public string Stderr { get; init; } = "";
        public Task<InstallerProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            Executable = executable;
            Arguments = arguments;
            return Task.FromResult(new InstallerProcessResult(ExitCode, "", Stderr));
        }
    }
}
