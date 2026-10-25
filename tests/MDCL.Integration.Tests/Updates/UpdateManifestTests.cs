using System.Security.Cryptography;
using System.Text.Json;
using MDCL.Core.Errors;
using MDCL.Core.Updates;
using MDCL.Infrastructure.Updates;
using Xunit;

namespace MDCL.Integration.Tests.Updates;

public sealed class UpdateManifestTests
{
    [Theory]
    [InlineData("0.2.0-alpha.1", "0.2.0-alpha.2")]
    [InlineData("0.2.0-alpha.2", "0.2.0-alpha.10")]
    [InlineData("0.2.0-rc.1", "0.2.0")]
    public void PrereleaseDowngrade_IsRejected(string offered, string current)
    {
        using var rsa = RSA.Create(2048);
        var manifest = Manifest() with { Version = offered };
        var error = Assert.Throws<MdclException>(() => new UpdateManifestVerifier(rsa, "stable", "win-x64", current)
            .Verify(Envelope(manifest, rsa), DateTimeOffset.UtcNow));
        Assert.Equal("UPDATE_DOWNGRADE_REFUSED", error.Error.ErrorCode);
    }

    [Fact]
    public void MinimumLauncherVersion_IsEnforced()
    {
        using var rsa = RSA.Create(2048);
        var manifest = Manifest() with { MinimumLauncherVersion = "0.5.0" };
        var error = Assert.Throws<MdclException>(() => new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0")
            .Verify(Envelope(manifest, rsa), DateTimeOffset.UtcNow));
        Assert.Equal("UPDATE_MINIMUM_VERSION", error.Error.ErrorCode);
    }

    [Fact]
    public async Task PackageSizeMustMatchEvenWhenHashIsCorrect()
    {
        using var temp = new TestDirectory();
        var file = Path.Combine(temp.Path, "package");
        await File.WriteAllBytesAsync(file, [1,2,3]);
        using var rsa = RSA.Create(2048);
        var manifest = Manifest() with { Sha256 = Convert.ToHexString(SHA256.HashData([1,2,3])), Size = 4 };
        var error = await Assert.ThrowsAsync<MdclException>(() => new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0")
            .VerifyPackageAsync(file, manifest, CancellationToken.None));
        Assert.Equal("UPDATE_PACKAGE_SIZE_MISMATCH", error.Error.ErrorCode);
    }

    [Fact]
    public async Task SignedManifest_VerifiesAndPackageHashIsChecked()
    {
        using var rsa = RSA.Create(2048);
        var manifest = Manifest();
        var verifier = new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0");
        var verified = verifier.Verify(Envelope(manifest, rsa), DateTimeOffset.UtcNow);
        Assert.Equal("0.2.0", verified.Version);
        var path = Path.Combine(Path.GetTempPath(), "mdcl-update-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            var withHash = manifest with { Sha256 = hash, Size = 3 };
            await verifier.VerifyPackageAsync(path, withHash, CancellationToken.None);
            var error = await Assert.ThrowsAsync<MdclException>(() =>
                verifier.VerifyPackageAsync(path, withHash with { Sha256 = new string('0', 64) }, CancellationToken.None));
            Assert.Equal("UPDATE_PACKAGE_HASH_MISMATCH", error.Error.ErrorCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TamperedManifest_IsRejectedBeforeBusinessRules()
    {
        using var rsa = RSA.Create(2048);
        var signed = Envelope(Manifest(), rsa);
        var tampered = signed.Replace("\"0.2.0\"", "\"0.2.1\"", StringComparison.Ordinal);
        var error = Assert.Throws<MdclException>(() =>
            new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0").Verify(tampered, DateTimeOffset.UtcNow));
        Assert.Equal("UPDATE_SIGNATURE_INVALID", error.Error.ErrorCode);
    }

    [Fact]
    public void ExpiredSignedManifest_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var manifest = Manifest() with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var error = Assert.Throws<MdclException>(() =>
            new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0").Verify(Envelope(manifest, rsa), DateTimeOffset.UtcNow));
        Assert.Equal("UPDATE_EXPIRED", error.Error.ErrorCode);
    }

    [Fact]
    public void WrongRidAndDowngrade_AreRejected()
    {
        using var rsa = RSA.Create(2048);
        var wrongRid = Manifest() with { Rid = "osx-arm64" };
        Assert.Equal("UPDATE_RID_MISMATCH", Assert.Throws<MdclException>(() =>
            new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0").Verify(Envelope(wrongRid, rsa), DateTimeOffset.UtcNow)).Error.ErrorCode);
        var downgrade = Manifest() with { Version = "0.0.9" };
        Assert.Equal("UPDATE_DOWNGRADE_REFUSED", Assert.Throws<MdclException>(() =>
            new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0").Verify(Envelope(downgrade, rsa), DateTimeOffset.UtcNow)).Error.ErrorCode);
    }

    private static UpdateManifest Manifest() => new(
        "0.2.0", "stable", "win-x64", new Uri("https://example.invalid/update.zip"), 3,
        new string('a', 64), DateTimeOffset.UtcNow.AddDays(1), "0.1.0");

    private static string Envelope(UpdateManifest manifest, RSA rsa)
    {
        var signature = rsa.SignData(UpdateSignature.CanonicalBytes(manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.Serialize(new SignedUpdateDocument(manifest, Convert.ToBase64String(signature)), UpdateSignature.Options);
    }
}
