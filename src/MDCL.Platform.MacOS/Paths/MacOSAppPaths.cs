using MDCL.Application.Paths;

namespace MDCL.Platform.MacOS.Paths;

public sealed class MacOSAppPaths : AppPathsBase
{
    public MacOSAppPaths(string? homeDirectory = null)
        : base(
            Path.Combine(homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support", "MDCL"),
            Path.Combine(homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Caches", "MDCL"),
            Path.Combine(homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Logs", "MDCL"))
    {
    }
}
