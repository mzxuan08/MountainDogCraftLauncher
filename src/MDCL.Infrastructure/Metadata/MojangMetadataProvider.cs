using System.Security.Cryptography;
using System.Text;
using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Versions;

namespace MDCL.Infrastructure.Metadata;

public sealed class MojangMetadataProvider : IVersionMetadataSource, IAssetIndexSource
{
    public static readonly Uri VersionManifestUri = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    private const long MaximumMetadataBytes = 16 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _manifestLock = new(1, 1);
    private VersionManifest? _manifest;

    public MojangMetadataProvider(HttpClient http, string cacheDirectory)
    {
        _http = http;
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public async Task<VersionManifest> GetManifestAsync(CancellationToken ct)
    {
        if (_manifest is not null) return _manifest;
        await _manifestLock.WaitAsync(ct);
        try
        {
            if (_manifest is not null) return _manifest;
            var cache = Path.Combine(_cacheDirectory, "version_manifest_v2.json");
            var bytes = await ReadWithCacheAsync(VersionManifestUri, cache, null, ct);
            _manifest = VersionManifestParser.Parse(Encoding.UTF8.GetString(bytes));
            return _manifest;
        }
        finally { _manifestLock.Release(); }
    }

    public async Task<VersionMetadataRecord> GetVersionAsync(VersionManifestEntry entry, CancellationToken ct)
    {
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new MdclException(MdclError.Of("VERSION_METADATA_URL_INVALID", "Metadata", $"版本元数据 URL 无效：{entry.Url}", false));
        var safeName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Id))).ToLowerInvariant();
        var cache = Path.Combine(_cacheDirectory, "versions", $"{safeName}.json");
        var bytes = await ReadWithCacheAsync(uri, cache, entry.Sha1, ct);
        var json = Encoding.UTF8.GetString(bytes);
        var actualSha1 = Hash(bytes);
        VersionDocument document;
        try { document = VersionDocumentParser.Parse(json, entry.Url, actualSha1); }
        catch (System.Text.Json.JsonException ex)
        {
            throw new MdclException(MdclError.Of("VERSION_METADATA_INVALID", "Metadata", $"版本 {entry.Id} 的 JSON 无效。", false, entry.Url), ex);
        }
        if (!string.Equals(document.Id, entry.Id, StringComparison.Ordinal))
            throw new MdclException(MdclError.Of("VERSION_ID_MISMATCH", "Metadata", $"请求 {entry.Id}，响应 {document.Id}。", false));
        return new VersionMetadataRecord(document, entry.Url, actualSha1, json);
    }

    public async Task<IReadOnlyList<AssetObject>> GetObjectsAsync(ResolvedAssetIndex index, CancellationToken ct)
    {
        if (index.Uri is null) throw new MdclException(MdclError.Of("ASSET_INDEX_URL_MISSING", "Metadata", "版本没有可用 assets 索引地址。", false));
        var safeName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index.Id))).ToLowerInvariant();
        var cache = Path.Combine(_cacheDirectory, "assets", $"{safeName}.json");
        var bytes = await ReadWithCacheAsync(index.Uri, cache, index.Sha1, ct);
        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idNode) ? idNode.GetString() : index.Id;
        if (!string.Equals(id, index.Id, StringComparison.Ordinal))
            throw new MdclException(MdclError.Of("ASSET_INDEX_ID_MISMATCH", "Metadata", $"assets 索引 ID 不匹配：{id}。", false));
        var result = new List<AssetObject>();
        if (root.TryGetProperty("objects", out var objects))
        {
            foreach (var property in objects.EnumerateObject())
            {
                if (!property.Value.TryGetProperty("hash", out var hash) || !property.Value.TryGetProperty("size", out var size)) continue;
                var hashText = hash.GetString();
                if (string.IsNullOrWhiteSpace(hashText) || hashText.Length != 40 || !size.TryGetInt64(out var sizeValue)) continue;
                result.Add(new AssetObject(hashText.ToLowerInvariant(), sizeValue));
            }
        }
        return result;
    }

    private async Task<byte[]> ReadWithCacheAsync(Uri uri, string cachePath, string? expectedSha1, CancellationToken ct)
    {
        if (File.Exists(cachePath))
        {
            var cached = await File.ReadAllBytesAsync(cachePath, ct);
            if (expectedSha1 is null || string.Equals(Hash(cached), expectedSha1, StringComparison.OrdinalIgnoreCase)) return cached;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new MdclException(MdclError.Of("VERSION_METADATA_HTTP", "Metadata",
                    $"读取官方元数据返回 HTTP {(int)response.StatusCode}。", true, uri.Host));
            if (response.Content.Headers.ContentLength is > MaximumMetadataBytes)
                throw new MdclException(MdclError.Of("VERSION_METADATA_TOO_LARGE", "Metadata", "元数据超过大小限制。", false));
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaximumMetadataBytes)
                throw new MdclException(MdclError.Of("VERSION_METADATA_TOO_LARGE", "Metadata", "元数据超过大小限制。", false));
            if (expectedSha1 is not null && !string.Equals(Hash(bytes), expectedSha1, StringComparison.OrdinalIgnoreCase))
                throw new MdclException(MdclError.Of("VERSION_METADATA_HASH_MISMATCH", "Metadata",
                    "官方版本 JSON 的 SHA-1 与版本清单不符。", true, $"expected={expectedSha1}", $"actual={Hash(bytes)}"));
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, ct);
                File.Move(temporary, cachePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return bytes;
        }
        catch (HttpRequestException ex)
        {
            throw new MdclException(MdclError.Of("VERSION_METADATA_NETWORK", "Metadata", "无法读取官方版本元数据。", true, uri.Host), ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MdclException(MdclError.Of("VERSION_METADATA_TIMEOUT", "Metadata", "读取官方版本元数据超时。", true, uri.Host));
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
}
