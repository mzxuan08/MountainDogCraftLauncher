using MDCL.Core.Installation;

namespace MDCL.Application.Ports;

public interface IArtifactDownloader
{
    Task<ArtifactReceipt> EnsureAsync(
        ArtifactSpec artifact,
        string targetRoot,
        IProgress<TransferEvent>? progress,
        CancellationToken ct);
}

public interface IInstallPlanner
{
    Task<InstallPlan> CreateAsync(InstallRequest request, CancellationToken ct);
}

public interface IOperationCoordinator
{
    Task<OperationResult> RunAsync(
        InstallPlan plan,
        string targetRoot,
        IProgress<OperationEvent>? progress,
        CancellationToken ct);
}
