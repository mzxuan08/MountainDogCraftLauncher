using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MDCL.Core.Errors;
using MDCL.Core.Updates;
using MDCL.Infrastructure.Updates;
using Xunit;

namespace MDCL.Integration.Tests.Updates;

public sealed class UpdateCheckServiceTests
{
    [Fact]
    public async Task SignedManifestAndPackage_AreDownloadedAndVerified()
    {
        using var temp = new TestDirectory();
        var package = Encoding.UTF8.GetBytes("update-package");
        using var rsa = RSA.Create(2048);
        var manifest = new UpdateManifest("0.2.0", "stable", "win-x64", new Uri("https://example.invalid/update.zip"),
            package.Length, Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(), DateTimeOffset.UtcNow.AddDays(1), "0.1.0");
        var envelope = Envelope(manifest, rsa);
        var handler = new Handler(envelope, package);
        var verifier = new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0");
        var service = new UpdateCheckService(new HttpClient(handler), verifier, Path.Combine(temp.Path, "cache"));
        var result = await service.CheckAndDownloadAsync(new Uri("https://example.invalid/latest.json"), CancellationToken.None);
        Assert.Equal("0.2.0", result.Manifest.Version);
        Assert.True(File.Exists(result.PackagePath));
        Assert.Equal(package, await File.ReadAllBytesAsync(result.PackagePath));
    }

    [Fact]
    public async Task TamperedEnvelope_BlocksPackageDownload()
    {
        using var temp = new TestDirectory();
        var package = Encoding.UTF8.GetBytes("update-package");
        using var rsa = RSA.Create(2048);
        var manifest = new UpdateManifest("0.2.0", "stable", "win-x64", new Uri("https://example.invalid/update.zip"),
            package.Length, new string('a', 64), DateTimeOffset.UtcNow.AddDays(1), "0.1.0");
        var envelope = Envelope(manifest, rsa).Replace("\"0.2.0\"", "\"0.2.1\"", StringComparison.Ordinal);
        var handler = new Handler(envelope, package);
        var verifier = new UpdateManifestVerifier(rsa, "stable", "win-x64", "0.1.0");
        var service = new UpdateCheckService(new HttpClient(handler), verifier, Path.Combine(temp.Path, "cache"));
        var error = await Assert.ThrowsAsync<MdclException>(() => service.CheckAndDownloadAsync(new Uri("https://example.invalid/latest.json"), CancellationToken.None));
        Assert.Equal("UPDATE_SIGNATURE_INVALID", error.Error.ErrorCode);
        Assert.Equal(0, handler.PackageRequests);
    }

    private static string Envelope(UpdateManifest manifest, RSA rsa)
    {
        var signature = rsa.SignData(UpdateSignature.CanonicalBytes(manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.Serialize(new SignedUpdateDocument(manifest, Convert.ToBase64String(signature)), UpdateSignature.Options);
    }

    private sealed class Handler(string envelope, byte[] package) : HttpMessageHandler
    {
        public int PackageRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope) });
            PackageRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
        }
    }
}
