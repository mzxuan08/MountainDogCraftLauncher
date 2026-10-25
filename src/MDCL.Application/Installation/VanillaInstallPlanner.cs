using System.Security.Cryptography;
using System.Text;
using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Installation;
using MDCL.Core.Versions;

namespace MDCL.Application.Installation;

public sealed class VanillaInstallPlanner : IInstallPlanner
{
    private readonly IVersionResolver _resolver;
    private readonly IAssetIndexSource _assetIndexSource;

    public VanillaInstallPlanner(IVersionResolver resolver, IAssetIndexSource assetIndexSource)
    {
        _resolver = resolver;
        _assetIndexSource = assetIndexSource;
    }

    public async Task<InstallPlan> CreateAsync(InstallRequest request, CancellationToken ct)
    {
        var resolved = await _resolver.ResolveAsync(
            new VersionSelection(request.BaseVersion, request.Loader.Kind, request.Loader.Version), request.Target, ct);
        var steps = new List<InstallStep>();
        var artifacts = new List<ArtifactSpec>();
        var versionDirectory = SafeComponent(request.BaseVersion);

        if (resolved.Client is { } client)
        {
            artifacts.Add(Artifact($"client:{resolved.VersionId}", client.Url, client.Size, client.Sha1,
                $"versions/{versionDirectory}/client.jar", "mojang-version-json"));
        }
        if (artifacts.Count > 0) steps.Add(new InstallStep("client", [], artifacts.ToArray()));

        var libraryArtifacts = new List<ArtifactSpec>();
        foreach (var library in resolved.Libraries)
        {
            if (library.ArtifactUri is not null)
                libraryArtifacts.Add(Artifact($"library:{library.Coordinate}", library.ArtifactUri, library.Size, library.Sha1,
                    $"libraries/{library.ClassPathRelativePath}", "mojang-library-json"));
            if (library.Native?.Uri is not null)
                libraryArtifacts.Add(Artifact($"native:{library.Coordinate}:{library.Native.Classifier}", library.Native.Uri,
                    library.Native.Size, library.Native.Sha1, $"libraries/{library.Native.RelativePath}", "mojang-library-json"));
        }
        if (libraryArtifacts.Count > 0) steps.Add(new InstallStep("libraries", [], libraryArtifacts));

        if (resolved.AssetIndex is { Uri: not null } assetIndex)
        {
            steps.Add(new InstallStep("assets-index", [],
            [
                Artifact("asset-index:" + assetIndex.Id, assetIndex.Uri, assetIndex.Size, assetIndex.Sha1,
                    $"assets/indexes/{SafeComponent(assetIndex.Id)}.json", "mojang-version-json")
            ]));
            var objects = await _assetIndexSource.GetObjectsAsync(assetIndex, ct);
            var assetArtifacts = objects.DistinctBy(x => x.Hash)
                .Select(x => Artifact("asset:" + x.Hash,
                    new Uri($"https://resources.download.minecraft.net/{x.Hash[..2]}/{x.Hash}"), x.Size, x.Hash,
                    $"assets/objects/{x.Hash[..2]}/{x.Hash}", "mojang-asset-index"))
                .ToArray();
            steps.Add(new InstallStep("assets", ["assets-index"], assetArtifacts));
        }

        if (resolved.Logging is { } logging)
        {
            steps.Add(new InstallStep("logging", [],
            [
                Artifact("logging:" + logging.FileId, logging.Uri, logging.Size, logging.Sha1,
                    $"logging/{SafeComponent(logging.FileId)}", "mojang-logging-json")
            ]));
        }

        var planHash = HashPlan(resolved, request, steps);
        return new InstallPlan(Guid.NewGuid().ToString("N"), request.InstanceId, planHash, 1, steps,
            $"{request.Target.RuntimeIdentifier}|{resolved.ContentHash}|{steps.Sum(x => x.Artifacts.Count)}");
    }

    private static ArtifactSpec Artifact(string key, Uri uri, long? size, string? sha1, string target, string source)
    {
        if (string.IsNullOrWhiteSpace(sha1))
            throw new MdclException(MdclError.Of("INSTALL_ARTIFACT_HASH_MISSING", "InstallPlanning",
                $"资源 {key} 缺少官方摘要，拒绝无校验安装。", false, key));
        return new ArtifactSpec(key, [uri], size, sha1, "SHA1", source, target, ArtifactTrustLevel.TrustedMetadata);
    }

    private static string HashPlan(ResolvedVersion version, InstallRequest request, IReadOnlyList<InstallStep> steps)
    {
        var text = string.Join('\n', new[]
        {
            request.BaseVersion,
            request.Target.RuntimeIdentifier,
            version.ContentHash,
            string.Join(';', steps.Select(step => step.StepId + ":" + string.Join(',', step.Artifacts.Select(a =>
                $"{a.ArtifactKey}|{a.RelativeTargetPath}|{a.ExpectedHash}|{a.ExpectedSize}"))))
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string SafeComponent(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_');
        var result = builder.ToString().Trim('.');
        return result.Length == 0 || result == ".." ? "_" : result;
    }
}
