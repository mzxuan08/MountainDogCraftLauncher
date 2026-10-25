using System.Security.Cryptography;
using MDCL.Core.Errors;
using MDCL.Core.Updates;

namespace MDCL.Infrastructure.Updates;

public sealed record UpdateCheckResult(UpdateManifest Manifest, string PackagePath);

public sealed class UpdateCheckService
{
    private readonly HttpClient _http;
    private readonly UpdateManifestVerifier _verifier;
    private readonly string _cacheDirectory;

    public UpdateCheckService(HttpClient http, UpdateManifestVerifier verifier, string cacheDirectory)
    {
        _http = http;
        _verifier = verifier;
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public async Task<UpdateCheckResult> CheckAndDownloadAsync(Uri envelopeUri, CancellationToken ct)
    {
        if (envelopeUri.Scheme != Uri.UriSchemeHttps)
            throw new MdclException(MdclError.Of("UPDATE_URL_INVALID", "Update", "更新清单必须使用 HTTPS。", false));
        using var envelopeResponse = await _http.GetAsync(envelopeUri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!envelopeResponse.IsSuccessStatusCode)
            throw new MdclException(MdclError.Of("UPDATE_MANIFEST_HTTP", "Update", $"更新清单 HTTP {(int)envelopeResponse.StatusCode}。", true));
        if (envelopeResponse.Content.Headers.ContentLength is > 1024 * 1024)
            throw new MdclException(MdclError.Of("UPDATE_MANIFEST_TOO_LARGE", "Update", "更新清单过大。", false));
        await using var envelopeInput = await envelopeResponse.Content.ReadAsStreamAsync(ct);
        using var boundedEnvelope = new MemoryStream();
        await CopyBoundedAsync(envelopeInput, boundedEnvelope, 1024 * 1024, "UPDATE_MANIFEST_TOO_LARGE", ct);
        var envelope = System.Text.Encoding.UTF8.GetString(boundedEnvelope.ToArray());
        var manifest = _verifier.Verify(envelope, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(_cacheDirectory);
        var temporary = Path.Combine(_cacheDirectory, "update-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var packageResponse = await _http.GetAsync(manifest.PackageUri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!packageResponse.IsSuccessStatusCode)
                throw new MdclException(MdclError.Of("UPDATE_PACKAGE_HTTP", "Update", $"更新包 HTTP {(int)packageResponse.StatusCode}。", true));
            if (packageResponse.Content.Headers.ContentLength is { } length && length != manifest.Size)
                throw new MdclException(MdclError.Of("UPDATE_PACKAGE_SIZE_MISMATCH", "Update", "更新包大小与清单不符。", false));
            await using (var input = await packageResponse.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await CopyBoundedAsync(input, output, manifest.Size, "UPDATE_PACKAGE_SIZE_MISMATCH", ct);
            if (new FileInfo(temporary).Length != manifest.Size)
                throw new MdclException(MdclError.Of("UPDATE_PACKAGE_SIZE_MISMATCH", "Update", "更新包大小与清单不符。", false));
            await _verifier.VerifyPackageAsync(temporary, manifest, ct);
            var extension = manifest.Rid.StartsWith("osx-", StringComparison.Ordinal) ? ".tar.gz" : ".zip";
            var final = Path.Combine(_cacheDirectory, $"MDCL-{manifest.Version}-{manifest.Rid}{extension}");
            File.Move(temporary, final, true);
            return new UpdateCheckResult(manifest, final);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, string errorCode, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long done = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - done + 1)), ct);
            if (count == 0) break;
            if (done + count > limit)
                throw new MdclException(MdclError.Of(errorCode, "Update", "更新响应超过允许大小。", false));
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            done += count;
        }
    }
}
