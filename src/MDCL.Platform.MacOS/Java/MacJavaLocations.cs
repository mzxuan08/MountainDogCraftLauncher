using MDCL.Application.Ports;

namespace MDCL.Platform.MacOS.Java;

public sealed class MacJavaLocations : IJavaSearchLocations
{
    public IEnumerable<string> CandidateExecutables()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) yield return Path.Combine(javaHome, "bin", "java");
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(path.Trim(), "java");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        var roots = new[]
        {
            "/Library/Java/JavaVirtualMachines",
            "/System/Library/Java/JavaVirtualMachines",
            Path.Combine(home, "Library", "Java", "JavaVirtualMachines")
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var bundle in Directory.EnumerateDirectories(root))
            {
                var executable = Path.Combine(bundle, "Contents", "Home", "bin", "java");
                if (File.Exists(executable)) yield return executable;
                foreach (var nested in Directory.EnumerateDirectories(bundle, "Contents", SearchOption.AllDirectories))
                {
                    var candidate = Path.Combine(nested, "Home", "bin", "java");
                    if (File.Exists(candidate)) yield return candidate;
                }
            }
        }
    }
}
