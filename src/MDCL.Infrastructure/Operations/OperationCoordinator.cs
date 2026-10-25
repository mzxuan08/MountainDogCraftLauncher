using System.Security.Cryptography;
using System.Text;
using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Installation;
using MDCL.Infrastructure.Storage;

namespace MDCL.Infrastructure.Operations;

public sealed class OperationCoordinator : IOperationCoordinator
{
    private readonly IArtifactDownloader _downloader;
    private readonly string _stagingRoot;

    public OperationCoordinator(IArtifactDownloader downloader, string stagingRoot)
    {
        _downloader = downloader;
        _stagingRoot = Path.GetFullPath(stagingRoot);
    }

    public async Task<OperationResult> RunAsync(
        InstallPlan plan, string targetRoot, IProgress<OperationEvent>? progress, CancellationToken ct)
    {
        var operationDirectory = Path.Combine(_stagingRoot, SafeName(plan.PlanId));
        var store = new InstallJournalStore(Path.Combine(operationDirectory, "journal.json"));
        var journal = await store.LoadAsync(ct);
        if (journal is not null && !string.Equals(journal.PlanHash, plan.PlanHash, StringComparison.Ordinal))
            return new OperationResult(OperationState.RecoveryRequired,
                MdclError.Of("INSTALL_PLAN_MISMATCH", "Installation", "journal 与当前安装计划哈希不一致。", false));
        if (journal is { State: "Completed" }) return new OperationResult(OperationState.Completed);
        journal ??= InstallJournalStore.Create(plan.PlanId, plan.PlanHash, plan.Steps.Select(x => x.StepId));
        journal = journal with
        {
            State = "Running",
            Steps = journal.Steps.Select(x => x.State == "Completed" ? x : x with { State = "Pending", ErrorCode = null }).ToArray()
        };
        long sequence = 0;
        void Emit(InstallStep step, OperationState state, long? done, long? total, string message)
        {
            sequence++;
            progress?.Report(new OperationEvent(plan.PlanId, step.StepId, sequence, state, done, total, message, DateTimeOffset.UtcNow));
        }
        try
        {
            foreach (var step in plan.Steps)
            {
                ct.ThrowIfCancellationRequested();
                var current = journal.Steps.First(x => x.StepId == step.StepId);
                if (current.State == "Completed") continue;
                foreach (var dependency in step.DependsOn)
                {
                    var dependencyStep = journal.Steps.FirstOrDefault(x => x.StepId == dependency);
                    if (dependencyStep is null || dependencyStep.State != "Completed")
                        throw new MdclException(MdclError.Of("INSTALL_DEPENDENCY_MISSING", "Installation",
                            $"步骤 {step.StepId} 的依赖 {dependency} 未完成。", false));
                }
                journal = ReplaceStep(journal, current with { State = "Running" });
                await store.SaveAsync(journal, ct);
                Emit(step, OperationState.Running, null, null, "step-started");

                ArtifactReceipt? lastReceipt = null;
                foreach (var artifact in step.Artifacts)
                {
                    var transfer = new CallbackProgress<TransferEvent>(x =>
                        Emit(step, OperationState.Running, x.DoneBytes, x.TotalBytes, x.Phase));
                    lastReceipt = await _downloader.EnsureAsync(artifact, targetRoot, transfer, ct);
                }
                journal = ReplaceStep(journal, current with
                {
                    State = "Completed",
                    ArtifactKey = lastReceipt?.ArtifactKey,
                    Path = lastReceipt?.Path,
                    Hash = lastReceipt?.Hash
                });
                await store.SaveAsync(journal, ct);
                Emit(step, OperationState.Verifying, null, null, "step-completed");
            }
            journal = journal with { State = "Completed" };
            await store.SaveAsync(journal, ct);
            Emit(new InstallStep("operation", [], []), OperationState.Completed, null, null, "operation-completed");
            return new OperationResult(OperationState.Completed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            journal = journal with { State = "Canceled" };
            await store.SaveAsync(journal, CancellationToken.None);
            return new OperationResult(OperationState.Canceled);
        }
        catch (MdclException ex)
        {
            journal = journal with { State = "Failed" };
            await store.SaveAsync(journal, CancellationToken.None);
            return new OperationResult(OperationState.Failed, ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            journal = journal with { State = "Failed" };
            await store.SaveAsync(journal, CancellationToken.None);
            return new OperationResult(OperationState.Failed,
                MdclError.Of("INSTALL_IO", "Installation", "安装写入失败：" + ex.Message, false));
        }
    }

    private static InstallJournalDocument ReplaceStep(InstallJournalDocument journal, InstallJournalStep step) =>
        journal with
        {
            Revision = journal.Revision + 1,
            Steps = journal.Steps.Select(x => x.StepId == step.StepId ? step : x).ToArray()
        };

    private static string SafeName(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
