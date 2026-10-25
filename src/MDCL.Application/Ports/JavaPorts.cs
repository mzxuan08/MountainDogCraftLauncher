using MDCL.Core;
using MDCL.Core.Installation;
using MDCL.Core.Java;

namespace MDCL.Application.Ports;

public interface IJavaProbe
{
    Task<JavaProbeResult> ProbeAsync(string executablePath, CancellationToken ct);
}

public sealed record JavaProbeResult(JavaRuntime? Runtime, string Status, string? Error);

public interface IJavaSearchLocations
{
    IEnumerable<string> CandidateExecutables();
}

public interface IJavaDiscovery
{
    Task<IReadOnlyList<JavaProbeResult>> DiscoverAsync(CancellationToken ct);
}

public interface IJavaSelector
{
    Task<JavaSelection> SelectAsync(
        JavaRequirement requirement, MDCL.Core.Instances.JavaPolicy policy,
        IReadOnlyList<JavaRuntime> candidates, CancellationToken ct);
}

public sealed record JavaPackageDownload(string RuntimeId, Uri Uri, string Sha256, long? Size);

public interface IJavaRuntimePackageSource
{
    Task<JavaPackageDownload> GetPackageAsync(JavaRequirement requirement, TargetPlatform target, CancellationToken ct);
}

public interface IJavaRuntimeInstaller
{
    Task<JavaRuntime> InstallAsync(
        JavaRequirement requirement, TargetPlatform target,
        IProgress<OperationEvent>? progress, CancellationToken ct);
}
