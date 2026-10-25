using System.Security.Cryptography;
using System.Text.Json;
using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Core.Versions;

namespace MDCL.Infrastructure.Installers;

public sealed class FabricComponentVersionProvider : IComponentVersionProvider
{
    private readonly HttpClient _http;
    public FabricComponentVersionProvider(HttpClient http) => _http = http;

    public LoaderKind Kind => LoaderKind.Fabric;

    public async Task<ComponentVersion> GetVersionAsync(
        VersionSelection selection, VersionManifestEntry baseEntry, TargetPlatform target, CancellationToken ct)
    {
        var loaderVersion = selection.LoaderVersion;
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            var loadersUri = new Uri($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(selection.BaseVersionId)}");
            using var loadersResponse = await _http.GetAsync(loadersUri, ct);
            if (!loadersResponse.IsSuccessStatusCode)
                throw new MdclException(MdclError.Of("FABRIC_METADATA_HTTP", "Fabric", $"Fabric loader 元数据 HTTP {(int)loadersResponse.StatusCode}。", true));
            using var loadersJson = JsonDocument.Parse(await loadersResponse.Content.ReadAsStreamAsync(ct));
            var loader = loadersJson.RootElement.EnumerateArray()
                .FirstOrDefault(x => x.GetProperty("loader").TryGetProperty("stable", out var stable) && stable.GetBoolean());
            if (loader.ValueKind == JsonValueKind.Undefined) loader = loadersJson.RootElement[0];
            loaderVersion = loader.GetProperty("loader").GetProperty("version").GetString()
                ?? throw new MdclException(MdclError.Of("FABRIC_LOADER_MISSING", "Fabric", "Fabric 没有可用 loader 版本。", false));
        }

        var profileUri = new Uri($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(selection.BaseVersionId)}/{Uri.EscapeDataString(loaderVersion)}/profile/json");
        using var profileResponse = await _http.GetAsync(profileUri, ct);
        if (!profileResponse.IsSuccessStatusCode)
            throw new MdclException(MdclError.Of("FABRIC_PROFILE_HTTP", "Fabric", $"Fabric profile HTTP {(int)profileResponse.StatusCode}。", false));
        var bytes = await profileResponse.Content.ReadAsByteArrayAsync(ct);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var document = VersionDocumentParser.Parse(System.Text.Encoding.UTF8.GetString(bytes), profileUri.AbsoluteUri, sha1);
        if (string.IsNullOrWhiteSpace(document.InheritsFrom))
            document = document with { InheritsFrom = selection.BaseVersionId };
        if (!string.Equals(document.InheritsFrom, selection.BaseVersionId, StringComparison.Ordinal))
            throw new MdclException(MdclError.Of("FABRIC_PROFILE_PARENT", "Fabric",
                $"Fabric profile 继承 {document.InheritsFrom}，与实例版本 {selection.BaseVersionId} 不一致。", false));

        var libraries = new List<LibraryDocument>();
        foreach (var library in document.Libraries)
        {
            if (library.Downloads?.Artifact is { Sha1: { Length: 40 } }) { libraries.Add(library); continue; }
            if (string.IsNullOrWhiteSpace(library.BaseUrl))
                throw new MdclException(MdclError.Of("FABRIC_LIBRARY_NO_URL", "Fabric", $"Fabric 库缺少下载地址：{library.Name}", false));
            var coordinate = MavenCoordinate.Parse(library.Name);
            var artifactUri = new Uri(new Uri(library.BaseUrl, UriKind.Absolute), coordinate.DefaultPath);
            var sidecar = artifactUri.AbsoluteUri + ".sha1";
            using var sidecarResponse = await _http.GetAsync(sidecar, ct);
            if (!sidecarResponse.IsSuccessStatusCode)
                throw new MdclException(MdclError.Of("FABRIC_HASH_MISSING", "Fabric", $"无法获取 Fabric 库摘要：{coordinate.Identity}", false));
            var expected = (await sidecarResponse.Content.ReadAsStringAsync(ct)).Trim().Split(' ')[0];
            if (expected.Length != 40 || expected.Any(c => !Uri.IsHexDigit(c)))
                throw new MdclException(MdclError.Of("FABRIC_HASH_INVALID", "Fabric", $"Fabric 库 SHA-1 无效：{coordinate.Identity}", false));
            var artifact = new LibraryArtifact(coordinate.DefaultPath, artifactUri, expected.ToLowerInvariant(), null);
            libraries.Add(library with
            {
                Downloads = new LibraryDownloads(artifact, library.Downloads?.Classifiers ?? new Dictionary<string, LibraryArtifact>()),
                HashSource = "fabric-maven-sha1"
            });
        }
        document = document with { Libraries = libraries };
        return new ComponentVersion(document, profileUri.AbsoluteUri, sha1, loaderVersion);
    }
}
