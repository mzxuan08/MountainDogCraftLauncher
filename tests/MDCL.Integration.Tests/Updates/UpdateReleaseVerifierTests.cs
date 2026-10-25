using System.Security.Cryptography;
using System.Text.Json;
using MDCL.Core.Errors;
using MDCL.Core.Updates;
using MDCL.Infrastructure.Updates;
using Xunit;

namespace MDCL.Integration.Tests.Updates;

public sealed class UpdateReleaseVerifierTests
{
    [Fact]
    public void PemVerifier_AcceptsOnlyMatchingSignedEnvelope()
    {
        using var rsa = RSA.Create(2048);
        var manifest = new UpdateManifest("0.2.0", "stable", "win-x64", new Uri("https://example.invalid/update.zip"),
            100, new string('a', 64), DateTimeOffset.UtcNow.AddDays(1), "0.1.0");
        var signature = rsa.SignData(UpdateSignature.CanonicalBytes(manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var envelope = JsonSerializer.Serialize(new SignedUpdateDocument(manifest, Convert.ToBase64String(signature)), UpdateSignature.Options);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        var verified = UpdateReleaseVerifier.VerifyEnvelope(envelope, pem, "stable", "win-x64", "0.1.0", DateTimeOffset.UtcNow);
        Assert.Equal("0.2.0", verified.Version);
        var tampered = envelope.Replace("\"0.2.0\"", "\"0.2.1\"", StringComparison.Ordinal);
        var error = Assert.Throws<MdclException>(() =>
            UpdateReleaseVerifier.VerifyEnvelope(tampered, pem, "stable", "win-x64", "0.1.0", DateTimeOffset.UtcNow));
        Assert.Equal("UPDATE_SIGNATURE_INVALID", error.Error.ErrorCode);
    }
}
