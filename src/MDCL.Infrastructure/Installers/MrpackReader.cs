using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MDCL.Core.Errors;
using MDCL.Core.Installation;
using MDCL.Core.Instances;

namespace MDCL.Infrastructure.Installers;

public sealed record MrpackFile(string RelativePath, Uri DownloadUri, string Sha512, string Sha1, long Size);
public sealed record MrpackPreview(
    string Name, string VersionId, string GameVersion, string LoaderKind, string LoaderVersion,
    IReadOnlyList<MrpackFile> Files, int OverrideFileCount, long OverrideBytes, IReadOnlyList<string> Warnings);

public sealed class MrpackReader
{
    private const int MaximumIndexBytes = 8 * 1024 * 1024;

    public async Task<MrpackPreview> ReadAsync(string packPath, CancellationToken ct)
    {
        await using var stream = new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var indexEntry = archive.GetEntry("modrinth.index.json")
            ?? throw new MdclException(MdclError.Of("MRPACK_INDEX_MISSING", "Modpack", "mrpack 缺少 modrinth.index.json。", false));
        if (indexEntry.Length > MaximumIndexBytes)
            throw new MdclException(MdclError.Of("MRPACK_INDEX_TOO_LARGE", "Modpack", "mrpack 索引过大。", false));
        using var indexStream = indexEntry.Open();
        using var document = await JsonDocument.ParseAsync(indexStream, cancellationToken: ct);
        var root = document.RootElement;
        if (!root.TryGetProperty("formatVersion", out var format) || format.GetInt32() != 1)
            throw new MdclException(MdclError.Of("MRPACK_FORMAT_UNSUPPORTED", "Modpack", "只支持 mrpack formatVersion=1。", false));
        if (!root.TryGetProperty("game", out var game) || game.GetString() != "minecraft")
            throw new MdclException(MdclError.Of("MRPACK_GAME_UNSUPPORTED", "Modpack", "只能导入 Minecraft mrpack。", false));
        var dependencies = root.GetProperty("dependencies");
        var gameVersion = dependencies.TryGetProperty("minecraft", out var mc) ? mc.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(gameVersion))
            throw new MdclException(MdclError.Of("MRPACK_GAME_VERSION_MISSING", "Modpack", "mrpack 未声明 Minecraft 版本。", false));
        var loaderKey = dependencies.EnumerateObject().FirstOrDefault(x => x.Name is "fabric-loader" or "forge" or "neo-forge" or "quilt-loader");
        var loaderKind = loaderKey.Name switch
        {
            "fabric-loader" => "fabric",
            "forge" => "forge",
            "neo-forge" => "neoforge",
            "quilt-loader" => "quilt",
            _ => "vanilla"
        };
        var loaderVersion = loaderKey.Value.ValueKind == JsonValueKind.String ? loaderKey.Value.GetString() ?? "" : "";
        var warnings = new List<string>();
        if (loaderKind is "quilt") warnings.Add("Quilt 尚未支持，只能导入文件，不能保证启动。");
        foreach (var property in dependencies.EnumerateObject())
        {
            if (property.Name is not ("minecraft" or "fabric-loader" or "forge" or "neo-forge" or "quilt-loader"))
                warnings.Add($"未知依赖 {property.Name}，保留为外部输入。");
        }

        var files = new List<MrpackFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            var path = SafeRelative(file.GetProperty("path").GetString() ?? "");
            if (!seen.Add(path))
                throw new MdclException(MdclError.Of("MRPACK_PATH_DUPLICATE", "Modpack", $"mrpack 文件路径重复：{path}", false));
            var hashes = file.GetProperty("hashes");
            var sha512 = hashes.TryGetProperty("sha512", out var s512) ? s512.GetString() ?? "" : "";
            var sha1 = hashes.TryGetProperty("sha1", out var s1) ? s1.GetString() ?? "" : "";
            if (sha512.Length != 128 && sha1.Length != 40)
                throw new MdclException(MdclError.Of("MRPACK_HASH_MISSING", "Modpack", $"文件缺少 SHA-512/SHA-1：{path}", false));
            var downloads = file.GetProperty("downloads").EnumerateArray()
                .Select(x => x.GetString()).Where(x => Uri.TryCreate(x, UriKind.Absolute, out _)).Select(x => new Uri(x!)).ToArray();
            if (downloads.Length == 0 || downloads.Any(x => x.Scheme != Uri.UriSchemeHttps))
                throw new MdclException(MdclError.Of("MRPACK_DOWNLOAD_INVALID", "Modpack", $"文件没有可信 HTTPS 下载地址：{path}", false));
            var size = file.TryGetProperty("fileSize", out var sizeNode) ? sizeNode.GetInt64() : 0;
            files.Add(new MrpackFile(path, downloads[0], sha512, sha1, size));
        }

        var overrideFiles = 0L;
        var overrideBytes = 0L;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith("overrides/", StringComparison.Ordinal) ||
                entry.FullName.StartsWith("client-overrides/", StringComparison.Ordinal))
            {
                overrideFiles++;
                overrideBytes += entry.Length;
            }
        }
        var name = root.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "导入的整合包" : "导入的整合包";
        var versionId = root.TryGetProperty("versionId", out var versionNode) ? versionNode.GetString() ?? "" : "";
        return new MrpackPreview(name, versionId, gameVersion, loaderKind, loaderVersion,
            files, (int)overrideFiles, overrideBytes, warnings);
    }

    public InstallPlan CreatePlan(MrpackPreview preview, InstanceId instanceId)
    {
        var artifacts = preview.Files.Select(file => new ArtifactSpec(
            "mrpack:" + file.RelativePath, [file.DownloadUri], file.Size,
            string.IsNullOrEmpty(file.Sha512) ? file.Sha1 : file.Sha512,
            string.IsNullOrEmpty(file.Sha512) ? "SHA1" : "SHA512",
            "mrpack-index", file.RelativePath, ArtifactTrustLevel.TrustedMetadata)).ToArray();
        var planData = string.Join('\n', preview.Files.Select(x => $"{x.RelativePath}|{x.Sha512}|{x.Sha1}|{x.Size}|{x.DownloadUri}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planData))).ToLowerInvariant();
        return new InstallPlan("mrpack-" + hash[..24], instanceId, hash, 1,
            [new InstallStep("modpack-files", [], artifacts)],
            $"{preview.GameVersion}|{preview.LoaderKind}|{preview.LoaderVersion}|{preview.Files.Count}");
    }

    private static string SafeRelative(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':') ||
            normalized.Split('/').Any(x => x is "" or "." or ".."))
            throw new MdclException(MdclError.Of("MRPACK_PATH_UNSAFE", "Modpack", $"mrpack 路径不安全：{path}", false));
        return normalized;
    }
}
