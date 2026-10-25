using System.Text.Json;
using MDCL.Core.Errors;

namespace MDCL.Infrastructure.Metadata;

public sealed record ModrinthProject(string Id, string Slug, string Title, string Description, string ProjectType, int Downloads, IReadOnlyList<string> Loaders, IReadOnlyList<string> GameVersions);
public sealed record ModrinthFile(string FileName, Uri Url, long Size, string? Sha512, string? Sha1, bool Primary);
public sealed record ModrinthVersion(string Id, string ProjectId, string VersionNumber, string VersionType, IReadOnlyList<string> GameVersions, IReadOnlyList<string> Loaders, IReadOnlyList<ModrinthFile> Files);

public sealed class ModrinthProvider
{
    private const string ApiRoot = "https://api.modrinth.com/v2";
    private readonly HttpClient _http;
    public ModrinthProvider(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<ModrinthProject>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        limit = Math.Clamp(limit, 1, 100);
        var uri = new Uri($"{ApiRoot}/search?query={Uri.EscapeDataString(query)}&limit={limit}&facets={Uri.EscapeDataString("[[\"project_type:mod\"]]")}");
        using var response = await _http.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode)
            throw new MdclException(MdclError.Of("MODRINTH_HTTP", "Modrinth", $"Modrinth 搜索 HTTP {(int)response.StatusCode}。", true));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var projects = new List<ModrinthProject>();
        foreach (var item in json.RootElement.GetProperty("hits").EnumerateArray())
        {
            projects.Add(new ModrinthProject(
                item.GetProperty("project_id").GetString() ?? "",
                item.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
                item.GetProperty("title").GetString() ?? "",
                item.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                item.TryGetProperty("project_type", out var type) ? type.GetString() ?? "mod" : "mod",
                item.TryGetProperty("downloads", out var downloads) ? downloads.GetInt32() : 0,
                item.TryGetProperty("loaders", out var loaders) ? loaders.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [],
                item.TryGetProperty("versions", out var versions) ? versions.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : []));
        }
        return projects;
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectId, string? gameVersion, string? loader, CancellationToken ct)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion)) query.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion })));
        if (!string.IsNullOrWhiteSpace(loader)) query.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader })));
        var uri = new Uri($"{ApiRoot}/project/{Uri.EscapeDataString(projectId)}/version" + (query.Count > 0 ? "?" + string.Join('&', query) : ""));
        using var response = await _http.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode)
            throw new MdclException(MdclError.Of("MODRINTH_HTTP", "Modrinth", $"Modrinth 版本 HTTP {(int)response.StatusCode}。", true));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var result = new List<ModrinthVersion>();
        foreach (var item in json.RootElement.EnumerateArray()) result.Add(ParseVersion(item));
        return result;
    }

    public async Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{ApiRoot}/version/{Uri.EscapeDataString(versionId)}", ct);
        if (!response.IsSuccessStatusCode)
            throw new MdclException(MdclError.Of("MODRINTH_HTTP", "Modrinth", $"Modrinth 版本详情 HTTP {(int)response.StatusCode}。", true));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return ParseVersion(json.RootElement);
    }

    private static ModrinthVersion ParseVersion(JsonElement item)
    {
        var files = new List<ModrinthFile>();
        foreach (var file in item.GetProperty("files").EnumerateArray())
        {
            var url = file.GetProperty("url").GetString() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new MdclException(MdclError.Of("MODRINTH_URL_INVALID", "Modrinth", "Modrinth 文件不是 HTTPS。", false));
            var hashes = file.GetProperty("hashes");
            files.Add(new ModrinthFile(
                file.GetProperty("filename").GetString() ?? "",
                uri,
                file.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                hashes.TryGetProperty("sha512", out var sha512) ? sha512.GetString() : null,
                hashes.TryGetProperty("sha1", out var sha1) ? sha1.GetString() : null,
                file.TryGetProperty("primary", out var primary) && primary.GetBoolean()));
        }
        return new ModrinthVersion(
            item.GetProperty("id").GetString() ?? "",
            item.GetProperty("project_id").GetString() ?? "",
            item.GetProperty("version_number").GetString() ?? "",
            item.TryGetProperty("version_type", out var type) ? type.GetString() ?? "" : "",
            item.GetProperty("game_versions").EnumerateArray().Select(x => x.GetString() ?? "").ToArray(),
            item.GetProperty("loaders").EnumerateArray().Select(x => x.GetString() ?? "").ToArray(),
            files);
    }
}
