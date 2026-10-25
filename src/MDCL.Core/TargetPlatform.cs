namespace MDCL.Core;

public enum CpuArchitecture { X64, Arm64 }
public enum LauncherOperatingSystem { Windows, MacOS }

public sealed record TargetPlatform(
    LauncherOperatingSystem OS,
    CpuArchitecture LauncherArchitecture,
    CpuArchitecture GameArchitecture)
{
    public string RuntimeIdentifier => (OS, LauncherArchitecture) switch
    {
        (LauncherOperatingSystem.Windows, CpuArchitecture.X64) => "win-x64",
        (LauncherOperatingSystem.MacOS, CpuArchitecture.X64) => "osx-x64",
        (LauncherOperatingSystem.MacOS, CpuArchitecture.Arm64) => "osx-arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(LauncherArchitecture))
    };

    public string MojangOsName => OS == LauncherOperatingSystem.MacOS ? "osx" : "windows";
}
