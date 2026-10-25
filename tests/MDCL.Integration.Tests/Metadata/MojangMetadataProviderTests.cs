using System.Net;
using System.Security.Cryptography;
using System.Text;
using MDCL.Core.Errors;
using MDCL.Core.Versions;
using MDCL.Infrastructure.Metadata;
using Xunit;

namespace MDCL.Integration.Tests.Metadata;

public sealed class MojangMetadataProviderTests
{
    [Fact]
    public async Task VerifiedVersionJson_IsCachedAndReused()
    {
        using var temp = new TestDirectory();
        const string json = """{ "id": "1.20.1", "mainClass": "example.Main", "javaVersion": { "majorVersion": 17 } }""";
        var bytes = Encoding.UTF8.GetBytes(json);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var handler = new CountingHandler(bytes);
        using var http = new HttpClient(handler);
        var provider = new MojangMetadataProvider(http, temp.Path);
        var entry = new VersionManifestEntry("1.20.1", "https://example.invalid/1.20.1.json", sha1, "release");
        var first = await provider.GetVersionAsync(entry, CancellationToken.None);
        var second = await provider.GetVersionAsync(entry, CancellationToken.None);
        Assert.Equal("1.20.1", first.Document.Id);
        Assert.Equal(sha1, second.Sha1);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task HashMismatch_IsRejectedAndNotCached()
    {
        using var temp = new TestDirectory();
        var handler = new CountingHandler(Encoding.UTF8.GetBytes("""{ "id": "1.20.1", "mainClass": "example.Main" }"""));
        using var http = new HttpClient(handler);
        var provider = new MojangMetadataProvider(http, temp.Path);
        var entry = new VersionManifestEntry("1.20.1", "https://example.invalid/1.20.1.json", new string('0', 40), "release");
        var error = await Assert.ThrowsAsync<MdclException>(() => provider.GetVersionAsync(entry, CancellationToken.None));
        Assert.Equal("VERSION_METADATA_HASH_MISMATCH", error.Error.ErrorCode);
    }

    private sealed class CountingHandler(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
