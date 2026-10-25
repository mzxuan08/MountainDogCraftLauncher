using MDCL.Platform.MacOS.Paths;
using MDCL.Platform.Windows.Paths;
using Xunit;

namespace MDCL.Integration.Tests.Storage;

public sealed class PlatformPathTests
{
    [Fact]
    public void WindowsPaths_AreOutsideInstallationDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "user-local");
        var paths = new WindowsAppPaths(root);
        Assert.Equal(Path.Combine(root, "MDCL", "data"), paths.DataRoot);
        Assert.DoesNotContain(Path.Combine("Program Files", "MDCL"), paths.DataRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(paths.ConfigRoot, "mdcl.db"), paths.DatabasePath);
    }

    [Fact]
    public void ExternalLibraryAndCaseIdentity_AreNotCollapsedByLauncher()
    {
        var external = Path.Combine(Path.GetTempPath(), "external volume", "游戏库");
        var upper = Path.Combine(external, "Native");
        var lower = Path.Combine(external, "native");
        Assert.Equal(external, Path.GetFullPath(external));
        Assert.NotEqual(upper, lower, StringComparer.Ordinal);
        Assert.False(string.Equals(upper, lower, StringComparison.Ordinal));
    }

    [Fact]
    public void MacPaths_UseDistinctSupportCacheAndLogsDirectories()
    {
        var home = Path.Combine(Path.GetTempPath(), "mac home");
        var paths = new MacOSAppPaths(home);
        Assert.Equal(Path.Combine(home, "Library", "Application Support", "MDCL", "data"), paths.DataRoot);
        Assert.Equal(Path.Combine(home, "Library", "Caches", "MDCL"), paths.CacheRoot);
        Assert.Equal(Path.Combine(home, "Library", "Logs", "MDCL"), paths.LogRoot);
        Assert.DoesNotContain(".app", paths.DataRoot, StringComparison.Ordinal);
    }
}
