using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Installation;

namespace MDCL.Infrastructure.Downloads;

public sealed class ArtifactDownloader : IArtifactDownloader
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http;

    public ArtifactDownloader(HttpClient http) => _http = http;

    public async Task<ArtifactReceipt> EnsureAsync(
        ArtifactSpec artifact,
        string targetRoot,
        IProgress<TransferEvent>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifact.ExpectedHash))
            throw new MdclException(MdclError.Of("DOWNLOAD_HASH_REQUIRED", "Download", "拒绝下载没有可信摘要的文件。", false, artifact.ArtifactKey));
        var target = ResolveTarget(targetRoot, artifact.RelativeTargetPath);
        var gate = Gates.GetOrAdd($"{artifact.ArtifactKey}|{artifact.ExpectedHash}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(target) && await MatchesAsync(target, artifact, ct))
            {
                progress?.Report(new TransferEvent(artifact.ArtifactKey, new FileInfo(target).Length, artifact.ExpectedSize, "cache-hit"));
                return new ArtifactReceipt(artifact.ArtifactKey, target, artifact.ExpectedHash!.ToLowerInvariant(),
                    artifact.CandidateUris[0], true);
            }

            if (File.Exists(target)) File.Delete(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var result = await DownloadWithRetriesAsync(artifact, target, progress, ct);
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task<ArtifactReceipt> DownloadWithRetriesAsync(
        ArtifactSpec artifact, string target, IProgress<TransferEvent>? progress, CancellationToken ct)
    {
        var part = target + ".part";
        var metaPath = part + ".meta";
        Exception? lastError = null;
        for (var sourceIndex = 0; sourceIndex < artifact.CandidateUris.Count; sourceIndex++)
        {
            var uri = artifact.CandidateUris[sourceIndex];
            for (var hashAttempt = 0; hashAttempt < 3; hashAttempt++)
            {
                try
                {
                    await TransferAsync(artifact, uri, part, metaPath, progress, ct);
                    var actual = await HashFileAsync(part, artifact.HashAlgorithm, ct);
                    if (!string.Equals(actual, artifact.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        DeleteIfExists(part);
                        DeleteIfExists(metaPath);
                        if (hashAttempt < 2)
                        {
                            progress?.Report(new TransferEvent(artifact.ArtifactKey, 0, artifact.ExpectedSize, "hash-retry"));
                            continue;
                        }
                        throw new MdclException(MdclError.Of("DOWNLOAD_HASH_MISMATCH", "Download",
                            "下载文件摘要不匹配，已拒绝提交。", true, artifact.ArtifactKey, $"expected={artifact.ExpectedHash}", $"actual={actual}"));
                    }
                    if (artifact.ExpectedSize is { } expected && new FileInfo(part).Length != expected)
                        throw new MdclException(MdclError.Of("DOWNLOAD_SIZE_MISMATCH", "Download", "下载文件大小不匹配。", true, artifact.ArtifactKey));
                    File.Move(part, target, true);
                    DeleteIfExists(metaPath);
                    progress?.Report(new TransferEvent(artifact.ArtifactKey, new FileInfo(target).Length, artifact.ExpectedSize, "committed"));
                    return new ArtifactReceipt(artifact.ArtifactKey, target, actual, uri, false);
                }
                catch (MdclException ex) when (ex.Error.Retryable && hashAttempt < 2)
                {
                    lastError = ex;
                    DeleteIfExists(part);
                    DeleteIfExists(metaPath);
                }
                catch (MdclException ex)
                {
                    lastError = ex;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (IOException ex)
                {
                    lastError = new MdclException(MdclError.Of("DOWNLOAD_IO", "Download", "写入下载文件失败。", false, artifact.ArtifactKey), ex);
                    break;
                }
            }
        }
        throw lastError as MdclException ?? new MdclException(MdclError.Of("DOWNLOAD_FAILED", "Download", "所有下载来源均失败。", true, artifact.ArtifactKey));
    }

    private async Task TransferAsync(
        ArtifactSpec artifact, Uri uri, string part, string metaPath,
        IProgress<TransferEvent>? progress, CancellationToken ct)
    {
        var metadata = ReadMetadata(metaPath);
        if (!File.Exists(part) || metadata is null || metadata.Uri != uri.AbsoluteUri)
        {
            DeleteIfExists(part);
            DeleteIfExists(metaPath);
            metadata = new PartMetadata(uri.AbsoluteUri, null, null);
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                if (!string.IsNullOrEmpty(metadata.ETag) && EntityTagHeaderValue.TryParse(metadata.ETag, out var entityTag))
                    request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt == 3) throw new MdclException(MdclError.Of("DOWNLOAD_TIMEOUT", "Download", "下载请求超时。", true, uri.Host));
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << attempt)), ct);
                continue;
            }
            catch (HttpRequestException ex)
            {
                if (attempt == 3) throw new MdclException(MdclError.Of("DOWNLOAD_NETWORK", "Download", "下载网络错误。", true, uri.Host), ex);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << attempt)), ct);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    DeleteIfExists(part);
                    DeleteIfExists(metaPath);
                    if (attempt < 3) continue;
                    throw new MdclException(MdclError.Of("DOWNLOAD_RANGE_INVALID", "Download", "服务器拒绝续传范围。", true, uri.Host));
                }
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    if (attempt == 3) throw new MdclException(MdclError.Of("DOWNLOAD_HTTP", "Download", $"下载失败 HTTP {(int)response.StatusCode}。", true, uri.Host));
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(250 * (1 << attempt));
                    if (delay > TimeSpan.FromSeconds(10)) delay = TimeSpan.FromSeconds(10);
                    await Task.Delay(delay, ct);
                    continue;
                }
                if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent)
                    throw new MdclException(MdclError.Of("DOWNLOAD_HTTP", "Download", $"下载失败 HTTP {(int)response.StatusCode}。", response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, uri.Host));

                var append = false;
                long total;
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var changedValidator = !string.IsNullOrEmpty(metadata.ETag) &&
                                           !string.Equals(response.Headers.ETag?.Tag, metadata.ETag, StringComparison.Ordinal);
                    if (offset == 0 || changedValidator || response.Content.Headers.ContentRange is not { } range || range.From != offset)
                    {
                        DeleteIfExists(part);
                        DeleteIfExists(metaPath);
                        if (attempt < 3) continue;
                        throw new MdclException(MdclError.Of("DOWNLOAD_RANGE_INVALID", "Download", "服务器 Content-Range 或验证器与续传位置不一致。", true, uri.Host));
                    }
                    append = true;
                    total = range.Length ?? (response.Content.Headers.ContentLength ?? 0) + offset;
                }
                else
                {
                    if (offset > 0)
                    {
                        DeleteIfExists(part);
                        DeleteIfExists(metaPath);
                    }
                    total = response.Content.Headers.ContentLength ?? artifact.ExpectedSize ?? 0;
                }

                metadata = metadata with
                {
                    ETag = response.Headers.ETag?.Tag,
                    LastModified = response.Content.Headers.LastModified?.ToString("O")
                };
                await WriteMetadataAsync(metaPath, metadata, ct);
                await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[81920];
                    long done = append ? offset : 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        progress?.Report(new TransferEvent(artifact.ArtifactKey, done, total > 0 ? total : null, "download"));
                    }
                    await output.FlushAsync(ct);
                    output.Flush(true);
                }
                return;
            }
        }
    }

    private static async Task<bool> MatchesAsync(string path, ArtifactSpec artifact, CancellationToken ct)
    {
        if (artifact.ExpectedSize is { } expected && new FileInfo(path).Length != expected) return false;
        var actual = await HashFileAsync(path, artifact.HashAlgorithm, ct);
        return string.Equals(actual, artifact.ExpectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> HashFileAsync(string path, string algorithm, CancellationToken ct)
    {
        using var hash = algorithm.ToUpperInvariant() switch
        {
            "SHA1" => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
            "SHA256" => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            "SHA512" => IncrementalHash.CreateHash(HashAlgorithmName.SHA512),
            _ => throw new MdclException(MdclError.Of("DOWNLOAD_HASH_ALGORITHM", "Download", $"不支持的摘要算法 {algorithm}。", false))
        };
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ResolveTarget(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new ArgumentException("Artifact target must be relative.", nameof(relativePath));
        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var relative = Path.GetRelativePath(fullRoot, target);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new MdclException(MdclError.Of("DOWNLOAD_PATH_ESCAPE", "Download", "下载目标越过受控根目录。", false, relativePath));
        return target;
    }

    private static PartMetadata? ReadMetadata(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<PartMetadata>(File.ReadAllText(path)) : null; }
        catch (JsonException) { return null; }
    }

    private static async Task WriteMetadataAsync(string path, PartMetadata metadata, CancellationToken ct)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(metadata), ct);
        File.Move(temporary, path, true);
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    private sealed record PartMetadata(string Uri, string? ETag, string? LastModified);
}
