using System.IO.Compression;
using System.Security.Cryptography;
using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Installation;
using MDCL.Core.Instances;
using MDCL.Core.Java;
using MDCL.Infrastructure.Java;
using Microsoft.Win32;
using Xunit;

namespace MDCL.Integration.Tests.Java;

public sealed class JavaIntegrationTests
{
    [Fact]
    public async Task RealInstalledJava_IsDiscoveredAndProbed()
    {
        var locations = OperatingSystem.IsWindows()
            ? (IJavaSearchLocations)new MDCL.Platform.Windows.Java.WindowsJavaLocations()
            : new MDCL.Platform.MacOS.Java.MacJavaLocations();
        var discovery = new JavaDiscovery(new ProcessJavaProbe(), Path.Combine(Path.GetTempPath(), "MDCL-none", Guid.NewGuid().ToString("N")), locations);
        var results = await discovery.DiscoverAsync(CancellationToken.None);
        var valid = results.Where(x => x.Runtime is not null).Select(x => x.Runtime!).ToArray();
        if (valid.Length == 0) return;
        Assert.Contains(valid, x => x.MajorVersion >= 8 && x.Architecture is CpuArchitecture.X64 or CpuArchitecture.Arm64);
    }

    [Theory]
    [InlineData("1.8.0_291", 8)]
    [InlineData("17.0.4", 17)]
    [InlineData("21.0.7", 21)]
    [InlineData("25", 25)]
    public void JavaVersionParsing(string version, int expected)
    {
        Assert.True(ProcessJavaProbe.TryMajor(version, out var major));
        Assert.Equal(expected, major);
    }

    [Fact]
    public async Task Discovery_DeduplicatesAndKeepsProbeFailuresVisible()
    {
        using var temp = new TestDirectory();
        var one = Path.Combine(temp.Path, "bin", "java.exe");
        var two = Path.Combine(temp.Path, "bin2", "java.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(one)!);
        Directory.CreateDirectory(Path.GetDirectoryName(two)!);
        await File.WriteAllTextAsync(one, "");
        await File.WriteAllTextAsync(two, "");
        var locations = new FixedLocations(one, one, two);
        var probe = new FakeProbe();
        var discovery = new JavaDiscovery(probe, Path.Combine(temp.Path, "managed"), locations);
        var results = await discovery.DiscoverAsync(CancellationToken.None);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, x => x.Runtime?.RuntimeId == "fake-17");
        Assert.Contains(results, x => x.Runtime is null && x.Status == "broken");
    }

    [Fact]
    public async Task ManagedInstaller_DownloadsExtractsProbesAndRegisters()
    {
        using var temp = new TestDirectory();
        var archive = Path.Combine(temp.Path, "jdk.zip");
        using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("jdk/bin/java.exe");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("fake");
        }
        var downloader = new CopyingDownloader(archive);
        var installer = new JavaRuntimeInstaller(new FakePackageSource(), downloader, new FakeProbe(),
            Path.Combine(temp.Path, "runtimes"), Path.Combine(temp.Path, "staging"));
        var requirement = JavaRequirementFactory.Create(17, CpuArchitecture.X64, [new JavaConstraint(17, null, "game")]);
        var result = await installer.InstallAsync(requirement,
            new TargetPlatform(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64),
            null, CancellationToken.None);
        Assert.True(result.IsManaged);
        Assert.StartsWith("managed:", result.RuntimeId, StringComparison.Ordinal);
        Assert.True(File.Exists(result.Executable));
        Assert.StartsWith(Path.Combine(temp.Path, "runtimes"), result.Home, StringComparison.OrdinalIgnoreCase);
        var rediscovered = await new JavaDiscovery(new FakeProbe(), Path.Combine(temp.Path, "runtimes")).DiscoverAsync(CancellationToken.None);
        Assert.Contains(rediscovered, x => x.Runtime?.RuntimeId == result.RuntimeId && x.Runtime.IsManaged);
        // A repeated managed installation validates and reuses the destination.
        Assert.Equal(result.RuntimeId, (await installer.InstallAsync(requirement,
            new TargetPlatform(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64), null, CancellationToken.None)).RuntimeId);
    }

    [Fact]
    public async Task FailedFinalProbe_DoesNotPoisonRuntimeDestination()
    {
        using var temp = new TestDirectory();
        var archive = Path.Combine(temp.Path, "jdk.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        await using (var writer = new StreamWriter(zip.CreateEntry("jdk/bin/" + (OperatingSystem.IsWindows() ? "java.exe" : "java")).Open()))
            await writer.WriteAsync("fake");
        var probe = new FailFinalProbe();
        var installer = new JavaRuntimeInstaller(new FakePackageSource(), new CopyingDownloader(archive), probe,
            Path.Combine(temp.Path, "runtimes"), Path.Combine(temp.Path, "staging"));
        var requirement = JavaRequirementFactory.Create(17, CpuArchitecture.X64, [new JavaConstraint(17,null,"game")]);
        var target = new TargetPlatform(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64);
        await Assert.ThrowsAsync<MDCL.Core.Errors.MdclException>(() => installer.InstallAsync(requirement, target, null, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(temp.Path, "runtimes"), "java*", SearchOption.AllDirectories));
        var recovered = await installer.InstallAsync(requirement, target, null, CancellationToken.None);
        Assert.True(recovered.IsManaged);
        Assert.True(File.Exists(recovered.Executable));
    }

    private sealed class FailFinalProbe : IJavaProbe
    {
        private int _calls;
        public Task<JavaProbeResult> ProbeAsync(string path, CancellationToken ct) => ++_calls == 2
            ? Task.FromResult(new JavaProbeResult(null, "broken", "injected final probe failure"))
            : new FakeProbe().ProbeAsync(path, ct);
    }

    private sealed class FixedLocations(params string[] paths) : IJavaSearchLocations
    {
        public IEnumerable<string> CandidateExecutables() => paths;
    }

    private sealed class FakeProbe : IJavaProbe
    {
        public Task<JavaProbeResult> ProbeAsync(string executablePath, CancellationToken ct)
        {
            if (executablePath.Contains("bin2", StringComparison.Ordinal))
                return Task.FromResult(new JavaProbeResult(null, "broken", "intentional"));
            var home = Path.GetDirectoryName(Path.GetDirectoryName(executablePath))!;
            return Task.FromResult(new JavaProbeResult(new JavaRuntime("fake-17", home, executablePath, 17, "fake",
                CpuArchitecture.X64, "ok", DateTimeOffset.UtcNow), "ok", null));
        }
    }

    private sealed class CopyingDownloader(string archivePath) : IArtifactDownloader
    {
        public Task<ArtifactReceipt> EnsureAsync(ArtifactSpec artifact, string targetRoot, IProgress<TransferEvent>? progress, CancellationToken ct)
        {
            Directory.CreateDirectory(targetRoot);
            var target = Path.Combine(targetRoot, artifact.RelativeTargetPath);
            File.Copy(archivePath, target, true);
            return Task.FromResult(new ArtifactReceipt(artifact.ArtifactKey, target, artifact.ExpectedHash!, artifact.CandidateUris[0], false));
        }
    }

    private sealed class FakePackageSource : IJavaRuntimePackageSource
    {
        public Task<JavaPackageDownload> GetPackageAsync(JavaRequirement requirement, TargetPlatform target, CancellationToken ct) =>
            Task.FromResult(new JavaPackageDownload("temurin-17.0.4", new Uri("https://example.invalid/jdk.zip"), new string('a', 64), 100));
    }
}
