namespace MDCL.Core.Java;

public sealed record JavaConstraint(int? MinimumMajor, int? MaximumMajor, string Source)
{
    public bool Allows(int major) => (!MinimumMajor.HasValue || major >= MinimumMajor) && (!MaximumMajor.HasValue || major <= MaximumMajor);
}

public sealed record JavaRequirement(
    int PreferredMajor,
    int MinimumMajor,
    int? MaximumMajor,
    CpuArchitecture Architecture,
    IReadOnlyList<string> RequiredModules,
    IReadOnlyList<JavaConstraint> Constraints);

public sealed record JavaRuntime(
    string RuntimeId,
    string Home,
    string Executable,
    int MajorVersion,
    string Vendor,
    CpuArchitecture Architecture,
    string ProbeStatus,
    DateTimeOffset LastProbedAt,
    bool IsManaged = false,
    IReadOnlyList<string>? Modules = null);

public sealed record JavaPolicySelection(bool PreferManaged, string? BoundRuntimeId);
public sealed record JavaCandidateRejection(JavaRuntime Candidate, string Reason);
public sealed record JavaSelection(JavaRuntime? Selected, IReadOnlyList<JavaCandidateRejection> Rejections, Errors.MdclError? Error);
