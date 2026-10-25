using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Infrastructure.Launcher;
using MDCL.Infrastructure.Storage;

namespace MDCL.Infrastructure.Installers;

public sealed record MrpackImportOutcome(InstanceManifest Instance, MrpackPreview Preview);

public sealed class MrpackImportService
{
    public const string PendingFileName = ".mdcl-import-pending.json";
    private static readonly SemaphoreSlim ImportGate = new(1, 1);
    private readonly LauncherWorkflow _workflow;
    private readonly MrpackReader _reader;
    private readonly IOperationCoordinator _coordinator;
    private readonly MrpackOverrideExtractor _overrides = new();

    public MrpackImportService(LauncherWorkflow workflow, MrpackReader reader, IOperationCoordinator coordinator)
    {
        _workflow = workflow;
        _reader = reader;
        _coordinator = coordinator;
    }

    public async Task<MrpackImportOutcome> ImportAsync(
        string packPath, string instanceName, IProgress<MDCL.Core.Installation.OperationEvent>? progress, CancellationToken ct,
        string? expectedSha256 = null)
    {
        await ImportGate.WaitAsync(ct);
        try
        {
        var preview = await _reader.ReadAsync(packPath, ct);
        if (expectedSha256 is not null && !string.Equals(preview.ContentSha256, expectedSha256, StringComparison.Ordinal))
            throw new MdclException(MdclError.Of("MRPACK_CHANGED", "Modpack", "整合包已在预览后变化，请重新预览。", false));
        if (preview.UnsupportedFeatures is { Count: > 0 })
            throw new MdclException(MdclError.Of("MRPACK_FEATURE_UNSUPPORTED", "Modpack", string.Join("\n", preview.UnsupportedFeatures), false));
        if (preview.LoaderKind is not ("vanilla" or "fabric" or "forge" or "neoforge"))
            throw new MdclException(MdclError.Of("MRPACK_LOADER_UNSUPPORTED", "Modpack",
                $"当前不支持自动导入 {preview.LoaderKind} 整合包。", false));
        InstanceManifest? instance = null;
        foreach (var candidate in await _workflow.ListInstancesAsync(ct))
        {
            var pending = Path.Combine(_workflow.Paths.InstancesRoot, candidate.InstanceId.ToString(), PendingFileName);
            if (candidate.DisplayName == instanceName.Trim() && File.Exists(pending) &&
                (await File.ReadAllTextAsync(pending, ct)).Trim() == preview.ContentSha256)
            { instance = candidate; break; }
        }
        instance ??= await _workflow.CreateInstanceAsync(instanceName, preview.GameVersion, ct);
        var pendingPath = Path.Combine(_workflow.Paths.InstancesRoot, instance.InstanceId.ToString(), PendingFileName);
        await File.WriteAllTextAsync(pendingPath, preview.ContentSha256, ct);
        switch (preview.LoaderKind)
        {
            case "fabric":
                await _workflow.InstallFabricAsync(instance.InstanceId, preview.LoaderVersion, progress, ct);
                break;
            case "forge":
            case "neoforge":
                await _workflow.InstallAsync(instance.InstanceId, progress, ct);
                var kind = preview.LoaderKind == "forge" ? LoaderKind.Forge : LoaderKind.NeoForge;
                await _workflow.InstallForgeAsync(instance.InstanceId, kind, preview.LoaderVersion, progress, ct);
                break;
            default:
                await _workflow.InstallAsync(instance.InstanceId, progress, ct);
                break;
        }

        instance = (await _workflow.ListInstancesAsync(ct)).First(x => x.InstanceId == instance.InstanceId);
        var gameDirectory = Path.IsPathRooted(instance.GameDirectory)
            ? instance.GameDirectory
            : Path.Combine(_workflow.Paths.InstancesRoot, instance.InstanceId.ToString(), instance.GameDirectory);
        Directory.CreateDirectory(gameDirectory);
        var plan = _reader.CreatePlan(preview, instance.InstanceId);
        await using var lease = await new FileInstanceLeaseManager(_workflow.Paths.InstancesRoot)
            .AcquireAsync(instance.InstanceId, "mrpack-files", ct);
        var result = await _coordinator.RunAsync(plan, gameDirectory, progress, ct);
        ct.ThrowIfCancellationRequested();
        if (result.State != MDCL.Core.Installation.OperationState.Completed)
            throw new MdclException(result.Error ?? MdclError.Of("MRPACK_FILES_FAILED", "Modpack", "整合包文件下载未完成。", true));
        await _overrides.ExtractAsync(packPath, gameDirectory, _workflow.Paths.StagingRoot, ct);
        File.Delete(pendingPath);
        return new MrpackImportOutcome(instance, preview);
        }
        finally { ImportGate.Release(); }
    }

    public Task<MrpackPreview> PreviewAsync(string packPath, CancellationToken ct) => _reader.ReadAsync(packPath, ct);
}
