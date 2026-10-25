using MDCL.Core.Instances;

namespace MDCL.Core.Installation;

public enum ArtifactTrustLevel { TrustedMetadata, VerifiedCache, PublicDownload }

public sealed record ArtifactSpec(
    string ArtifactKey,
    IReadOnlyList<Uri> CandidateUris,
    long? ExpectedSize,
    string? ExpectedHash,
    string HashAlgorithm,
    string HashSource,
    string RelativeTargetPath,
    ArtifactTrustLevel TrustLevel);

public sealed record InstallStep(
    string StepId,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<ArtifactSpec> Artifacts);

public sealed record InstallPlan(
    string PlanId,
    InstanceId InstanceId,
    string PlanHash,
    long Revision,
    IReadOnlyList<InstallStep> Steps,
    string TargetSnapshot);

public sealed record InstallRequest(InstanceId InstanceId, string BaseVersion, LoaderDescriptor Loader);

public enum OperationState { Created, Planning, Queued, Running, Verifying, Committing, Completed, Canceled, Failed, RecoveryRequired }

public sealed record OperationEvent(
    string OperationId,
    string StepId,
    long Sequence,
    OperationState Phase,
    long? DoneBytes,
    long? TotalBytes,
    string MessageCode,
    DateTimeOffset Timestamp);

public sealed record OperationResult(OperationState State, Errors.MdclError? Error = null);
public sealed record ArtifactReceipt(string ArtifactKey, string Path, string Hash, Uri Source, bool CacheHit);
public sealed record TransferEvent(string ArtifactKey, long DoneBytes, long? TotalBytes, string Phase);
