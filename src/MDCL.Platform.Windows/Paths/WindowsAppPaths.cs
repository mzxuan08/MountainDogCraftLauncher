using MDCL.Application.Paths;

namespace MDCL.Platform.Windows.Paths;

public sealed class WindowsAppPaths : AppPathsBase
{
    public WindowsAppPaths(string? localApplicationData = null)
        : base(
            Path.Combine(localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDCL"),
            Path.Combine(localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDCL", "cache"),
            Path.Combine(localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDCL", "logs"))
    {
    }
}
