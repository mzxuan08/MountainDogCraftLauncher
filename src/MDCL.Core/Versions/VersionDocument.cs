using System.Text.Json;

namespace MDCL.Core.Versions;

public sealed record VersionRule(
    string Action,
    IReadOnlyDictionary<string, string>? Os,
    IReadOnlyDictionary<string, bool>? Features)
{
    public bool IsAllow => string.Equals(Action, "allow", StringComparison.OrdinalIgnoreCase);
}

public sealed record ArgumentEntry(IReadOnlyList<VersionRule> Rules, IReadOnlyList<string> Values);

public sealed record VersionArguments(IReadOnlyList<ArgumentEntry> Game, IReadOnlyList<ArgumentEntry> Jvm)
{
    public static VersionArguments Empty { get; } = new([], []);
}

public sealed record LibraryArtifact(string Path, Uri Url, string? Sha1, long? Size, string? Sha256 = null);

public sealed record LibraryDownloads(
    LibraryArtifact? Artifact,
    IReadOnlyDictionary<string, LibraryArtifact> Classifiers);

public sealed record LibraryExtract(IReadOnlyList<string> Exclude);

public sealed record LibraryDocument(
    string Name,
    IReadOnlyDictionary<string, string> Natives,
    IReadOnlyList<VersionRule> Rules,
    LibraryDownloads? Downloads,
    LibraryExtract? Extract,
    string? BaseUrl,
    string? HashSource = null);

public sealed record JavaVersionInfo(string? Component, int MajorVersion);

public sealed record AssetIndexInfo(string Id, Uri? Url, string? Sha1, long? Size, long? TotalSize);

public sealed record ClientDownload(Uri Url, string? Sha1, long? Size);

public sealed record VersionDownloads(ClientDownload? Client);

public sealed record LoggingFile(string Id, Uri Url, string? Sha1, long? Size);

public sealed record LoggingClient(string Argument, LoggingFile File, string Type);

public sealed record VersionLogging(IReadOnlyDictionary<string, LoggingClient> Clients);

public sealed record VersionDocument
{
    public required string Id { get; init; }
    public string? InheritsFrom { get; init; }
    public string? MainClass { get; init; }
    public string? MinecraftArguments { get; init; }
    public VersionArguments Arguments { get; init; } = VersionArguments.Empty;
    public string? Assets { get; init; }
    public AssetIndexInfo? AssetIndex { get; init; }
    public VersionDownloads? Downloads { get; init; }
    public JavaVersionInfo? JavaVersion { get; init; }
    public IReadOnlyList<LibraryDocument> Libraries { get; init; } = [];
    public VersionLogging? Logging { get; init; }
    public string? Type { get; init; }
    public string SourceUrl { get; init; } = "";
    public string? SourceSha1 { get; init; }
}

public sealed record VersionManifestEntry(string Id, string Url, string? Sha1, string Type);
public sealed record VersionManifest(string LatestRelease, string LatestSnapshot, IReadOnlyList<VersionManifestEntry> Versions);
public sealed record VersionMetadataRecord(VersionDocument Document, string SourceUrl, string? Sha1, string? RawJson = null);

public sealed record VersionSelection(string BaseVersionId, Instances.LoaderKind LoaderKind = Instances.LoaderKind.Vanilla, string? LoaderVersion = null);

public static class VersionDocumentParser
{
    public static VersionDocument Parse(string json, string sourceUrl = "", string? sourceSha1 = null)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement, sourceUrl, sourceSha1);
    }

    private static VersionDocument Parse(JsonElement root, string sourceUrl, string? sourceSha1)
    {
        if (!root.TryGetProperty("id", out var idNode) || string.IsNullOrWhiteSpace(idNode.GetString()))
            throw new JsonException("Version JSON has no id.");

        var args = root.TryGetProperty("arguments", out var argsNode) ? ParseArguments(argsNode) : VersionArguments.Empty;
        var libraries = new List<LibraryDocument>();
        if (root.TryGetProperty("libraries", out var librariesNode))
        {
            foreach (var item in librariesNode.EnumerateArray()) libraries.Add(ParseLibrary(item));
        }

        return new VersionDocument
        {
            Id = idNode.GetString()!,
            InheritsFrom = OptionalString(root, "inheritsFrom"),
            MainClass = OptionalString(root, "mainClass"),
            MinecraftArguments = OptionalString(root, "minecraftArguments"),
            Arguments = args,
            Assets = OptionalString(root, "assets"),
            AssetIndex = root.TryGetProperty("assetIndex", out var assetIndex) ? ParseAssetIndex(assetIndex) : null,
            Downloads = root.TryGetProperty("downloads", out var downloads) ? ParseDownloads(downloads) : null,
            JavaVersion = root.TryGetProperty("javaVersion", out var javaVersion) && javaVersion.TryGetProperty("majorVersion", out var major) && major.TryGetInt32(out var majorValue)
                ? new JavaVersionInfo(OptionalString(javaVersion, "component"), majorValue)
                : null,
            Libraries = libraries,
            Logging = root.TryGetProperty("logging", out var logging) ? ParseLogging(logging) : null,
            Type = OptionalString(root, "type"),
            SourceUrl = sourceUrl,
            SourceSha1 = sourceSha1
        };
    }

    private static VersionArguments ParseArguments(JsonElement node)
    {
        var game = node.TryGetProperty("game", out var gameNode) ? ParseArgumentEntries(gameNode) : [];
        var jvm = node.TryGetProperty("jvm", out var jvmNode) ? ParseArgumentEntries(jvmNode) : [];
        return new VersionArguments(game, jvm);
    }

    private static IReadOnlyList<ArgumentEntry> ParseArgumentEntries(JsonElement array)
    {
        var result = new List<ArgumentEntry>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(new ArgumentEntry([], [item.GetString()!]));
                continue;
            }
            var rules = item.TryGetProperty("rules", out var ruleNode) ? ParseRules(ruleNode) : [];
            var values = new List<string>();
            if (item.TryGetProperty("value", out var value))
            {
                if (value.ValueKind == JsonValueKind.String) values.Add(value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in value.EnumerateArray()) if (entry.ValueKind == JsonValueKind.String) values.Add(entry.GetString()!);
                }
            }
            result.Add(new ArgumentEntry(rules, values));
        }
        return result;
    }

    private static IReadOnlyList<VersionRule> ParseRules(JsonElement array)
    {
        var rules = new List<VersionRule>();
        foreach (var item in array.EnumerateArray())
        {
            Dictionary<string, string>? os = null;
            if (item.TryGetProperty("os", out var osNode))
            {
                os = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in osNode.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String) os[property.Name] = property.Value.GetString()!;
            }
            Dictionary<string, bool>? features = null;
            if (item.TryGetProperty("features", out var featureNode))
            {
                features = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var property in featureNode.EnumerateObject())
                    if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) features[property.Name] = property.Value.GetBoolean();
            }
            rules.Add(new VersionRule(OptionalString(item, "action") ?? "", os, features));
        }
        return rules;
    }

    private static LibraryDocument ParseLibrary(JsonElement item)
    {
        var natives = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.TryGetProperty("natives", out var nativesNode))
        {
            foreach (var property in nativesNode.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String) natives[property.Name] = property.Value.GetString()!;
        }
        var downloads = item.TryGetProperty("downloads", out var downloadNode) ? ParseLibraryDownloads(downloadNode) : null;
        var extract = item.TryGetProperty("extract", out var extractNode) && extractNode.TryGetProperty("exclude", out var excludeNode)
            ? new LibraryExtract(excludeNode.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray())
            : null;
        return new LibraryDocument(
            OptionalString(item, "name") ?? throw new JsonException("Library name is missing."),
            natives,
            item.TryGetProperty("rules", out var rules) ? ParseRules(rules) : [],
            downloads,
            extract,
            OptionalString(item, "url"),
            "mojang-library-json");
    }

    private static LibraryDownloads ParseLibraryDownloads(JsonElement node)
    {
        var artifact = node.TryGetProperty("artifact", out var artifactNode) ? ParseArtifact(artifactNode) : null;
        var classifiers = new Dictionary<string, LibraryArtifact>(StringComparer.Ordinal);
        if (node.TryGetProperty("classifiers", out var classifiersNode))
        {
            foreach (var property in classifiersNode.EnumerateObject())
            {
                var parsed = ParseArtifact(property.Value);
                if (parsed is not null) classifiers[property.Name] = parsed;
            }
        }
        return new LibraryDownloads(artifact, classifiers);
    }

    private static LibraryArtifact? ParseArtifact(JsonElement node)
    {
        if (!node.TryGetProperty("path", out var path) || !node.TryGetProperty("url", out var url)) return null;
        var urlText = url.GetString();
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri)) return null;
        return new LibraryArtifact(path.GetString()!, uri, OptionalString(node, "sha1"), node.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) ? value : null);
    }

    private static AssetIndexInfo ParseAssetIndex(JsonElement node) => new(
        OptionalString(node, "id") ?? "",
        Uri.TryCreate(OptionalString(node, "url"), UriKind.Absolute, out var uri) ? uri : null,
        OptionalString(node, "sha1"),
        node.TryGetProperty("size", out var size) && size.TryGetInt64(out var sizeValue) ? sizeValue : null,
        node.TryGetProperty("totalSize", out var total) && total.TryGetInt64(out var totalValue) ? totalValue : null);

    private static VersionDownloads ParseDownloads(JsonElement node)
    {
        ClientDownload? client = null;
        if (node.TryGetProperty("client", out var clientNode) && clientNode.TryGetProperty("url", out var urlNode) && Uri.TryCreate(urlNode.GetString(), UriKind.Absolute, out var uri))
            client = new ClientDownload(uri, OptionalString(clientNode, "sha1"), clientNode.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) ? value : null);
        return new VersionDownloads(client);
    }

    private static VersionLogging ParseLogging(JsonElement node)
    {
        var clients = new Dictionary<string, LoggingClient>(StringComparer.Ordinal);
        if (node.TryGetProperty("client", out var clientNode))
        {
            var fileNode = clientNode.GetProperty("file");
            if (Uri.TryCreate(OptionalString(fileNode, "url"), UriKind.Absolute, out var uri))
            {
                var file = new LoggingFile(OptionalString(fileNode, "id") ?? "", uri, OptionalString(fileNode, "sha1"),
                    fileNode.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) ? value : null);
                clients["client"] = new LoggingClient(OptionalString(clientNode, "argument") ?? "", file, OptionalString(clientNode, "type") ?? "");
            }
        }
        return new VersionLogging(clients);
    }

    private static string? OptionalString(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
