using System.Runtime.Versioning;
using MDCL.Application.Ports;
using Microsoft.Win32;

namespace MDCL.Platform.Windows.Java;

[SupportedOSPlatform("windows")]
public sealed class WindowsJavaLocations : IJavaSearchLocations
{
    public IEnumerable<string> CandidateExecutables()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) yield return Path.Combine(javaHome, "bin", "java.exe");
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(path.Trim(), "java.exe");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var vendors = new[]
        {
            Path.Combine(programFiles, "Java"),
            Path.Combine(programFiles, "Eclipse Adoptium"),
            Path.Combine(programFiles, "Eclipse Foundation"),
            Path.Combine(programFiles, "Microsoft"),
            Path.Combine(programFiles, "Zulu"),
            Path.Combine(programFiles, "BellSoft", "LibericaJDK"),
            Path.Combine(programFiles, "Amazon Corretto"),
            Path.Combine(programFiles, "IBM", "Semeru")
        };
        foreach (var vendor in vendors)
        {
            if (!Directory.Exists(vendor)) continue;
            foreach (var directory in EnumerateSafely(vendor, "bin"))
                yield return Path.Combine(directory, "java.exe");
        }

        foreach (var keyName in new[] { @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\Java Development Kit", @"SOFTWARE\JavaSoft\Java Runtime Environment" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyName);
            if (key is null) continue;
            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey?.GetValue("JavaHome") is string home && !string.IsNullOrWhiteSpace(home))
                    yield return Path.Combine(home, "bin", "java.exe");
            }
        }
    }

    private static IEnumerable<string> EnumerateSafely(string root, string leaf)
    {
        string[] directories;
        try { directories = Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { directories = []; }
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, leaf);
            if (Directory.Exists(candidate)) yield return candidate;
        }
    }
}
