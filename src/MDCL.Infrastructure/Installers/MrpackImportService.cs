using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Infrastructure.Launcher;

namespace MDCL.Infrastructure.Installers;

public sealed record MrpackImportOutcome(InstanceManifest Instance, MrpackPreview Preview);

public sealed class MrpackImportService
{
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
        string packPath, string instanceName, IProgress<MDCL.Core.Installation.OperationEvent>? progress, CancellationToken ct)
    {
        var preview = await _reader.ReadAsync(packPath, ct);
        if (preview.LoaderKind is not ("vanilla" or "fabric"))
            throw new MdclException(MdclError.Of("MRPACK_LOADER_UNSUPPORTED", "Modpack",
                $"当前不支持自动导入 {preview.LoaderKind} 整合包。", false));
        var instance = await _workflow.CreateInstanceAsync(instanceName, preview.GameVersion, ct);
        if (preview.LoaderKind == "fabric")
            await _workflow.InstallFabricAsync(instance.InstanceId, preview.LoaderVersion, progress, ct);
        else
            await _workflow.InstallAsync(instance.InstanceId, progress, ct);

        instance = (await _workflow.ListInstancesAsync(ct)).First(x => x.InstanceId == instance.InstanceId);
        var gameDirectory = Path.IsPathRooted(instance.GameDirectory)
            ? instance.GameDirectory
            : Path.Combine(_workflow.Paths.InstancesRoot, instance.InstanceId.ToString(), instance.GameDirectory);
        Directory.CreateDirectory(gameDirectory);
        var plan = _reader.CreatePlan(preview, instance.InstanceId);
        var result = await _coordinator.RunAsync(plan, gameDirectory, progress, ct);
        if (result.State != MDCL.Core.Installation.OperationState.Completed)
            throw new MdclException(result.Error ?? MdclError.Of("MRPACK_FILES_FAILED", "Modpack", "整合包文件下载未完成。", true));
        await _overrides.ExtractAsync(packPath, gameDirectory, _workflow.Paths.StagingRoot, ct);
        return new MrpackImportOutcome(instance, preview);
    }
}
