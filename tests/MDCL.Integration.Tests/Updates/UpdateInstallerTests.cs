using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using MDCL.Core.Errors;
using MDCL.Core.Updates;
using MDCL.Infrastructure.Updates;
using Xunit;

namespace MDCL.Integration.Tests.Updates;

public sealed class UpdateInstallerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedReplacement_RecoveryIsIdempotentAndPreservesUserData(bool newPlaced)
    {
        using var temp = new TestDirectory();
        var target = Path.Combine(temp.Path, "app");
        var backup = Path.Combine(temp.Path, ".mdcl-update-backup-test");
        var staging = Path.Combine(temp.Path, ".mdcl-update-staging-test");
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(Path.Combine(backup, "old.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "user-save.txt"), "preserved");
        if (newPlaced) { Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(target, "new.txt"), "new"); }
        var package = Path.Combine(temp.Path, "update.zip"); CreatePackage(package);
        var statePath = Path.Combine(temp.Path, ".mdcl-update-state-test.json");
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new UpdateTransactionState(target, backup, staging,
            newPlaced ? "NewPlaced" : "BackupCreated", Manifest(package))));
        using var rsa = RSA.Create(2048);
        var installer = new UpdateInstaller(new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0"));
        await installer.RecoverAsync(statePath, CancellationToken.None);
        await installer.RecoverAsync(statePath, CancellationToken.None);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(target, "old.txt")));
        Assert.False(File.Exists(Path.Combine(target, "new.txt")));
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(temp.Path, "user-save.txt")));
    }

    [Fact]
    public async Task FailedHealthCheck_AutomaticallyRestoresOldApplication()
    {
        using var temp = new TestDirectory();
        var target = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "old.txt"), "old");
        var package = Path.Combine(temp.Path, "update.zip");
        CreatePackage(package);
        using var rsa = RSA.Create(2048);
        var installer = new UpdateInstaller(new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0"),
            (_, _) => Task.FromResult(false));
        var error = await Assert.ThrowsAsync<MdclException>(() => installer.ApplyAsync(
            Envelope(Manifest(package), rsa), package, target, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("UPDATE_HEALTHCHECK_FAILED", error.Error.ErrorCode);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(target, "old.txt")));
        Assert.False(File.Exists(Path.Combine(target, "new.txt")));
    }

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    public async Task MacAppTar_UpdatesActualBundleLayoutAndRestoresExecutionMode(string rid)
    {
        using var temp = new TestDirectory();
        var target = Path.Combine(temp.Path, "MDCL.app");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "old.txt"), "old");
        var package = Path.Combine(temp.Path, "update.tar.gz");
        using (var stream = File.Create(package))
        using (var gzip = new GZipStream(stream, CompressionMode.Compress))
        using (var tar = new TarWriter(gzip))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "MDCL.app/Contents/MacOS/MDCL.Desktop")
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                DataStream = new MemoryStream("executable"u8.ToArray())
            });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "MDCL.app/Contents/Info.plist")
            { DataStream = new MemoryStream("<plist/>"u8.ToArray()) });
        }
        using var rsa = RSA.Create(2048);
        var manifest = Manifest(package) with { Rid = rid };
        var installer = new UpdateInstaller(new UpdateManifestVerifier(rsa, "stable", rid, "0.1.0"));
        var result = await installer.ApplyAsync(Envelope(manifest, rsa), package, target, DateTimeOffset.UtcNow, CancellationToken.None);
        var executable = Path.Combine(target, "Contents/MacOS/MDCL.Desktop");
        Assert.Equal("executable", await File.ReadAllTextAsync(executable));
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(executable).HasFlag(UnixFileMode.UserExecute));
        await installer.RollbackAsync(result, target, CancellationToken.None);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(target, "old.txt")));
    }

    [Fact]
    public async Task Apply_BackupAndRollback_RestoreOldDirectory()
    {
        using var temp = new TestDirectory();
        var target = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "old.txt"), "old");
        var package = Path.Combine(temp.Path, "update.zip");
        CreatePackage(package);
        var manifest = Manifest(package);
        using var rsa = RSA.Create(2048);
        var envelope = Envelope(manifest, rsa);
        var verifier = new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0");
        var installer = new UpdateInstaller(verifier);
        var result = await installer.ApplyAsync(envelope, package, target, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(target, "MDCL.Desktop.exe")));
        Assert.True(File.Exists(Path.Combine(target, "new.txt")));
        Assert.False(File.Exists(Path.Combine(target, "old.txt")));
        Assert.NotNull(result.BackupDirectory);
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory!, "old.txt")));
        await installer.RollbackAsync(result, target, CancellationToken.None);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(target, "old.txt")));
        Assert.False(File.Exists(Path.Combine(target, "new.txt")));
    }

    [Fact]
    public async Task CorruptPackage_IsRejectedAndOldTargetUntouched()
    {
        using var temp = new TestDirectory();
        var target = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "old.txt"), "old");
        var package = Path.Combine(temp.Path, "update.zip");
        CreatePackage(package);
        var manifest = Manifest(package);
        using var rsa = RSA.Create(2048);
        var envelope = Envelope(manifest, rsa);
        await File.AppendAllTextAsync(package, "corrupt");
        var installer = new UpdateInstaller(new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0"));
        var error = await Assert.ThrowsAsync<MdclException>(() =>
            installer.ApplyAsync(envelope, package, target, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("UPDATE_PACKAGE_HASH_MISMATCH", error.Error.ErrorCode);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(target, "old.txt")));
    }

    private static void CreatePackage(string path)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("MDCL.Desktop.exe").Open())) writer.Write("exe");
        using (var writer = new StreamWriter(zip.CreateEntry("new.txt").Open())) writer.Write("new");
    }

    private static UpdateManifest Manifest(string package)
    {
        var bytes = File.ReadAllBytes(package);
        return new UpdateManifest("0.2.0", "stable", "win-x64", new Uri("https://example.invalid/update.zip"),
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            DateTimeOffset.UtcNow.AddDays(1), "0.1.0");
    }

    private static string Envelope(UpdateManifest manifest, RSA rsa)
    {
        var signature = rsa.SignData(UpdateSignature.CanonicalBytes(manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.Serialize(new SignedUpdateDocument(manifest, Convert.ToBase64String(signature)), UpdateSignature.Options);
    }
}
