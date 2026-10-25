using MDCL.Application.Ports;

namespace MDCL.Infrastructure.Java;

public sealed class JavaDiscovery : IJavaDiscovery
{
    private readonly IJavaProbe _probe;
    private readonly IReadOnlyList<IJavaSearchLocations> _locations;
    private readonly string _managedRoot;

    public JavaDiscovery(IJavaProbe probe, string managedRoot, params IJavaSearchLocations[] locations)
    {
        _probe = probe;
        _managedRoot = Path.GetFullPath(managedRoot);
        _locations = locations;
    }

    public async Task<IReadOnlyList<JavaProbeResult>> DiscoverAsync(CancellationToken ct)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var candidates = new HashSet<string>(comparer);
        if (Directory.Exists(_managedRoot))
        {
            foreach (var file in Directory.EnumerateFiles(_managedRoot, OperatingSystem.IsWindows() ? "java.exe" : "java", SearchOption.AllDirectories))
                candidates.Add(file);
        }
        foreach (var locations in _locations)
        {
            foreach (var file in locations.CandidateExecutables())
            {
                try
                {
                    var full = Path.GetFullPath(file);
                    if (File.Exists(full)) candidates.Add(full);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }

        var paths = candidates.ToArray();
        var results = new JavaProbeResult[paths.Length];
        using var gate = new SemaphoreSlim(2, 2);
        await Task.WhenAll(paths.Select(async (path, index) =>
        {
            await gate.WaitAsync(ct);
            try { results[index] = await _probe.ProbeAsync(path, ct); }
            finally { gate.Release(); }
        }));
        return results;
    }
}
