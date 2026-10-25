using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MDCL.Core.Errors;
using MDCL.Core.Java;
using MDCL.Core.Versions;

namespace MDCL.Core.Versions;

public sealed record MetadataSource(string VersionId, string Url, string? Sha1);

public sealed record ResolvedLibrary(
    string Coordinate,
    string ClassPathRelativePath,
    Uri? ArtifactUri,
    string? Sha1,
    long? Size,
    ResolvedNative? Native,
    IReadOnlyList<string> ExtractExclude);

public sealed record ResolvedNative(string Classifier, string RelativePath, Uri? Uri, string? Sha1, long? Size);

public sealed record ResolvedAssetIndex(string Id, Uri? Uri, string? Sha1, long? Size, long? TotalSize);
public sealed record ResolvedLogging(string Argument, string FileId, Uri Uri, string? Sha1, long? Size, string Type);

public sealed record ResolvedVersion(
    string VersionId,
    string MainClass,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<string> GameArguments,
    IReadOnlyList<ResolvedLibrary> Libraries,
    IReadOnlyList<ResolvedLibrary> NativeLibraries,
    ResolvedAssetIndex? AssetIndex,
    ResolvedLogging? Logging,
    ClientDownload? Client,
    JavaRequirement JavaRequirement,
    IReadOnlyList<MetadataSource> Sources,
    string ContentHash);

public sealed class ResolvedVersionBuilder
{
    public ResolvedVersion Build(
        VersionDocument document,
        TargetPlatform target,
        IReadOnlyList<MetadataSource> sources,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        var context = RuleContext.For(target, features);
        var libraries = new List<ResolvedLibrary>();
        var natives = new List<ResolvedLibrary>();
        foreach (var library in document.Libraries)
        {
            if (library.Rules.Count > 0 && !RuleEvaluator.IsAllowed(library.Rules, context, false)) continue;
            MavenCoordinate coordinate;
            try { coordinate = MavenCoordinate.Parse(library.Name); }
            catch (FormatException ex)
            {
                throw new MdclException(MdclError.Of("VERSION_LIBRARY_INVALID", "VersionResolution",
                    $"无效库坐标：{library.Name}", false, ex.Message), ex);
            }

            var primary = library.Downloads?.Artifact;
            var path = primary?.Path ?? coordinate.DefaultPath;
            var baseUrl = library.BaseUrl;
            var artifactUri = primary?.Url;
            if (artifactUri is null && !string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
                artifactUri = new Uri(baseUri, path);
            var resolved = new ResolvedLibrary(coordinate.Identity, path, artifactUri, primary?.Sha1,
                primary?.Size, ResolveNative(library, target, context), library.Extract?.Exclude ?? []);
            libraries.Add(resolved);
            if (resolved.Native is not null) natives.Add(resolved);
        }

        var jvmArguments = ArgumentMerger.Resolve(document.Arguments.Jvm, context);
        var gameArguments = document.Arguments.Game.Count > 0
            ? ArgumentMerger.Resolve(document.Arguments.Game, context)
            : ArgumentMerger.ParseLegacy(document.MinecraftArguments);

        var java = BuildJavaRequirement(document, target);
        var h = ComputeHash(document, target, jvmArguments, gameArguments, libraries, java, sources);
        return new ResolvedVersion(
            document.Id,
            document.MainClass ?? throw new MdclException(MdclError.Of("VERSION_MAIN_CLASS_MISSING", "VersionResolution", "版本缺少 mainClass。")),
            jvmArguments,
            gameArguments,
            libraries,
            natives,
            document.AssetIndex is null ? null : new ResolvedAssetIndex(document.AssetIndex.Id, document.AssetIndex.Url,
                document.AssetIndex.Sha1, document.AssetIndex.Size, document.AssetIndex.TotalSize),
            document.Logging?.Clients.TryGetValue("client", out var logging) == true
                ? new ResolvedLogging(logging.Argument, logging.File.Id, logging.File.Url, logging.File.Sha1, logging.File.Size, logging.Type)
                : null,
            document.Downloads?.Client,
            java,
            sources,
            h);
    }

    private static ResolvedNative? ResolveNative(LibraryDocument library, TargetPlatform target, RuleContext context)
    {
        if (!library.Natives.TryGetValue(context.OsName, out var classifierTemplate)) return null;
        var archToken = target.GameArchitecture == CpuArchitecture.X64 ? "64" : "arm64";
        var classifier = classifierTemplate.Replace("${arch}", archToken, StringComparison.Ordinal);
        if (library.Downloads?.Classifiers.TryGetValue(classifier, out var artifact) != true || artifact is null) return null;
        return new ResolvedNative(classifier, artifact.Path, artifact.Url, artifact.Sha1, artifact.Size);
    }

    private static JavaRequirement BuildJavaRequirement(VersionDocument document, TargetPlatform target)
    {
        if (document.JavaVersion is { MajorVersion: > 0 } info)
        {
            return new JavaRequirement(
                info.MajorVersion,
                info.MajorVersion,
                null,
                target.GameArchitecture,
                [],
                [new JavaConstraint(info.MajorVersion, null, $"mojang:{info.Component ?? document.Id}")]);
        }
        return new JavaRequirement(8, 8, null, target.GameArchitecture, [],
            [new JavaConstraint(8, null, "legacy-java8-table")]);
    }

    private static string ComputeHash(
        VersionDocument document,
        TargetPlatform target,
        IReadOnlyList<string> jvm,
        IReadOnlyList<string> game,
        IReadOnlyList<ResolvedLibrary> libraries,
        JavaRequirement java,
        IReadOnlyList<MetadataSource> sources)
    {
        var payload = JsonSerializer.Serialize(new
        {
            document.Id,
            document.MainClass,
            target = new { os = target.OS.ToString(), gameArch = target.GameArchitecture.ToString() },
            jvm,
            game,
            libraries = libraries.Select(x => new { x.Coordinate, x.ClassPathRelativePath, sha1 = x.Sha1, native = x.Native?.Classifier }),
            java = new { java.PreferredMajor, java.MinimumMajor, java.MaximumMajor, arch = java.Architecture.ToString(), constraints = java.Constraints.Select(x => new { x.MinimumMajor, x.MaximumMajor, x.Source }) },
            sources = sources.Select(x => new { x.VersionId, x.Url, x.Sha1 })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
