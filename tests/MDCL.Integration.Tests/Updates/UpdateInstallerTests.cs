using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MDCL.Core.Errors;
using MDCL.Core.Updates;
using MDCL.Infrastructure.Updates;
using Xunit;

namespace MDCL.Integration.Tests.Updates;

public sealed class UpdateInstallerTests
{
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
