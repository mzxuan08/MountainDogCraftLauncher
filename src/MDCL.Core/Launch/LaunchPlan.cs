using MDCL.Core.Instances;

namespace MDCL.Core.Launch;

public sealed record LaunchPlan(
    InstanceId InstanceId,
    string JavaExecutable,
    string WorkingDirectory,
    IReadOnlyList<string> JvmArguments,
    string MainClass,
    IReadOnlyList<string> GameArguments,
    IReadOnlyList<string> ClassPath,
    string NativeDirectory,
    IReadOnlyList<string> DependencyHashes,
    IReadOnlyList<string> PrecheckEvidence,
    int JavaMajorVersion);

public sealed record LaunchRequest(InstanceId InstanceId, bool Offline = false);
