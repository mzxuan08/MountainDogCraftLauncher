using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Installation;
using MDCL.Core.Instances;
using MDCL.Infrastructure.Metadata;

namespace MDCL.Infrastructure.Installers;

public sealed class ModrinthInstallService
{
    private readonly IArtifactDownloader _downloader;
    private readonly IInstanceRepository _repository;
    private readonly IInstanceLeaseManager _leases;
    private readonly IAppPaths _paths;

    public ModrinthInstallService(IArtifactDownloader downloader, IInstanceRepository repository,
        IInstanceLeaseManager leases, IAppPaths paths)
    {
        _downloader = downloader;
        _repository = repository;
        _leases = leases;
        _paths = paths;
    }

    public async Task<ArtifactReceipt> InstallPrimaryFileAsync(
        InstanceId instanceId, ModrinthVersion version, IProgress<TransferEvent>? progress, CancellationToken ct)
    {
        var file = version.Files.FirstOrDefault(x => x.Primary) ?? version.Files.FirstOrDefault()
            ?? throw new MdclException(MdclError.Of("MODRINTH_FILE_MISSING", "Modrinth", "版本没有可下载文件。", false));
        if (string.IsNullOrWhiteSpace(file.FileName) || !string.Equals(Path.GetFileName(file.FileName), file.FileName, StringComparison.Ordinal) ||
            file.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new MdclException(MdclError.Of("MODRINTH_FILENAME_INVALID", "Modrinth", "文件名不安全。", false));
        var hash = !string.IsNullOrWhiteSpace(file.Sha512) && file.Sha512.Length == 128
            ? (file.Sha512, "SHA512")
            : !string.IsNullOrWhiteSpace(file.Sha1) && file.Sha1.Length == 40
                ? (file.Sha1, "SHA1")
                : throw new MdclException(MdclError.Of("MODRINTH_HASH_MISSING", "Modrinth", "文件缺少 SHA-512/SHA-1。", false));

        var manifest = await _repository.LoadAsync(instanceId, ct);
        await using var lease = await _leases.AcquireAsync(instanceId, "modrinth-install", ct);
        var instanceRoot = Path.Combine(_paths.InstancesRoot, instanceId.ToString());
        var gameDir = Path.IsPathRooted(manifest.GameDirectory) ? manifest.GameDirectory : Path.Combine(instanceRoot, manifest.GameDirectory);
        var target = Path.Combine(Path.GetFullPath(gameDir), "mods", file.FileName);
        if (File.Exists(target))
            throw new MdclException(MdclError.Of("MOD_FILE_EXISTS", "Modrinth", "目标模组文件已存在，拒绝覆盖。", false, file.FileName));
        var artifact = new ArtifactSpec(
            $"modrinth:{version.ProjectId}:{version.Id}", [file.Url], file.Size, hash.Item1, hash.Item2,
            "modrinth-api", Path.Combine("mods", file.FileName), ArtifactTrustLevel.TrustedMetadata);
        return await _downloader.EnsureAsync(artifact, Path.GetFullPath(gameDir), progress, ct);
    }
}
