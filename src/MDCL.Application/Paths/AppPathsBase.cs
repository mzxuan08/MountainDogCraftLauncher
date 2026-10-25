using MDCL.Application.Ports;

namespace MDCL.Application.Paths;

public abstract class AppPathsBase : IAppPaths
{
    protected AppPathsBase(string configRoot, string cacheRoot, string logRoot)
    {
        ConfigRoot = Path.GetFullPath(configRoot);
        CacheRoot = Path.GetFullPath(cacheRoot);
        LogRoot = Path.GetFullPath(logRoot);
        DataRoot = Path.Combine(ConfigRoot, "data");
        InstancesRoot = Path.Combine(DataRoot, "instances");
        SharedRoot = Path.Combine(DataRoot, "shared");
        RuntimesRoot = Path.Combine(DataRoot, "runtimes");
        StagingRoot = Path.Combine(DataRoot, "staging");
        TrashRoot = Path.Combine(DataRoot, "trash");
        DatabasePath = Path.Combine(ConfigRoot, "mdcl.db");
    }

    public string ConfigRoot { get; }
    public string CacheRoot { get; }
    public string LogRoot { get; }
    public string DataRoot { get; }
    public string InstancesRoot { get; }
    public string SharedRoot { get; }
    public string RuntimesRoot { get; }
    public string StagingRoot { get; }
    public string TrashRoot { get; }
    public string DatabasePath { get; }

    public void EnsureDirectories()
    {
        foreach (var path in new[] { ConfigRoot, CacheRoot, LogRoot, DataRoot, InstancesRoot, SharedRoot, RuntimesRoot, StagingRoot, TrashRoot })
            Directory.CreateDirectory(path);
    }
}
