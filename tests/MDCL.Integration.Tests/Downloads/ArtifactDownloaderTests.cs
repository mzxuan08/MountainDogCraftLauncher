using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MDCL.Core.Errors;
using MDCL.Core.Installation;
using MDCL.Infrastructure.Downloads;
using Xunit;

namespace MDCL.Integration.Tests.Downloads;

public sealed class ArtifactDownloaderTests
{
    [Fact]
    public async Task Download_VerifiesAndReusesCache()
    {
        using var temp = new TestDirectory();
        var body = Encoding.UTF8.GetBytes("verified body");
        var handler = new ScriptedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body)));
        using var http = new HttpClient(handler);
        var downloader = new ArtifactDownloader(http);
        var spec = Spec("cache", body);
        var first = await downloader.EnsureAsync(spec, Path.Combine(temp.Path, "target"), null, CancellationToken.None);
        var second = await downloader.EnsureAsync(spec, Path.Combine(temp.Path, "target"), null, CancellationToken.None);
        Assert.True(File.Exists(first.Path));
        Assert.True(second.CacheHit);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(body, await File.ReadAllBytesAsync(first.Path));
    }

    [Fact]
    public async Task RangeResponse200_RestartsInsteadOfAppending()
    {
        using var temp = new TestDirectory();
        var body = Encoding.UTF8.GetBytes("complete-new-content");
        var root = Path.Combine(temp.Path, "target");
        var target = Path.Combine(root, "artifact.bin");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(target + ".part", "stale");
        await File.WriteAllTextAsync(target + ".part.meta",
            $$"""{"Uri":"https://example.invalid/file","ETag":null,"LastModified":null}""");
        var handler = new ScriptedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body)));
        var downloader = new ArtifactDownloader(new HttpClient(handler));
        var receipt = await downloader.EnsureAsync(Spec("range200", body), root, null, CancellationToken.None);
        Assert.Equal(body, await File.ReadAllBytesAsync(receipt.Path));
        Assert.Equal(1, handler.Requests);
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task Range416_RechecksFromZero()
    {
        using var temp = new TestDirectory();
        var body = Encoding.UTF8.GetBytes("after-416");
        var root = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "artifact.bin.part"), "stale");
        await File.WriteAllTextAsync(Path.Combine(root, "artifact.bin.part.meta"),
            """{"Uri":"https://example.invalid/file","ETag":null,"LastModified":null}""");
        var handler = new ScriptedHandler((request, count) =>
        {
            if (request.Headers.Range is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            return Task.FromResult(Response(HttpStatusCode.OK, body));
        });
        var downloader = new ArtifactDownloader(new HttpClient(handler));
        var receipt = await downloader.EnsureAsync(Spec("range416", body), root, null, CancellationToken.None);
        Assert.Equal(body, await File.ReadAllBytesAsync(receipt.Path));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task ChangedEtag_DiscardsPartialAndRestarts()
    {
        using var temp = new TestDirectory();
        var body = Encoding.UTF8.GetBytes("new-etag-content");
        var root = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "artifact.bin.part"), "old");
        await File.WriteAllTextAsync(Path.Combine(root, "artifact.bin.part.meta"),
            """{"Uri":"https://example.invalid/file","ETag":"\"v1\"","LastModified":null}""");
        var handler = new ScriptedHandler((request, _) =>
        {
            if (request.Headers.Range is not null)
                return Task.FromResult(Response(HttpStatusCode.OK, body, "\"v2\""));
            return Task.FromResult(Response(HttpStatusCode.OK, body, "\"v2\""));
        });
        var downloader = new ArtifactDownloader(new HttpClient(handler));
        var receipt = await downloader.EnsureAsync(Spec("etag", body), root, null, CancellationToken.None);
        Assert.Equal(body, await File.ReadAllBytesAsync(receipt.Path));
    }

    [Fact]
    public async Task HashMismatch_RetriesBoundedAndNeverCommits()
    {
        using var temp = new TestDirectory();
        var body = Encoding.UTF8.GetBytes("expected");
        var handler = new ScriptedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes("corrupt"))));
        var downloader = new ArtifactDownloader(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<MdclException>(() => downloader.EnsureAsync(Spec("badhash", body), Path.Combine(temp.Path, "target"), null, CancellationToken.None));
        Assert.Equal("DOWNLOAD_HASH_MISMATCH", error.Error.ErrorCode);
        Assert.Equal(3, handler.Requests);
        Assert.False(File.Exists(Path.Combine(temp.Path, "target", "artifact.bin")));
    }

    [Fact]
    public async Task CancelingWaiter_DoesNotCancelActiveSharedTransfer()
    {
        using var temp = new TestDirectory();
        var body = Encoding.UTF8.GetBytes("shared");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return Response(HttpStatusCode.OK, body);
        });
        var downloader = new ArtifactDownloader(new HttpClient(handler));
        var root = Path.Combine(temp.Path, "target");
        var spec = Spec("shared", body);
        var first = downloader.EnsureAsync(spec, root, null, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var second = downloader.EnsureAsync(spec, root, null, cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.TrySetResult();
        var receipt = await first;
        Assert.True(File.Exists(receipt.Path));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task PartialRange206_ResumesAndVerifies()
    {
        using var temp = new TestDirectory();
        var body = Encoding.UTF8.GetBytes("0123456789abcdef");
        var root = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "artifact.bin.part"), body[..7]);
        await File.WriteAllTextAsync(Path.Combine(root, "artifact.bin.part.meta"),
            """{"Uri":"https://example.invalid/file","ETag":"\"v1\"","LastModified":null}""");
        var handler = new ScriptedHandler((request, _) =>
        {
            Assert.Equal("bytes=7-", request.Headers.Range?.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body[7..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(7, body.Length - 1, body.Length);
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        });
        var downloader = new ArtifactDownloader(new HttpClient(handler));
        var receipt = await downloader.EnsureAsync(Spec("resume", body), root, null, CancellationToken.None);
        Assert.Equal(body, await File.ReadAllBytesAsync(receipt.Path));
    }

    private static ArtifactSpec Spec(string key, byte[] body) => new(
        key,
        [new Uri("https://example.invalid/file")],
        body.Length,
        Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(),
        "SHA256",
        "test-fixture",
        "artifact.bin",
        ArtifactTrustLevel.TrustedMetadata);

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body, string? etag = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        if (etag is not null) response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return handler(request, Requests);
        }
    }
}
