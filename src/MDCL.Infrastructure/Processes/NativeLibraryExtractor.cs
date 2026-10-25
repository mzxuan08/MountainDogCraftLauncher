using MDCL.Infrastructure.Archives;

namespace MDCL.Infrastructure.Processes;

public sealed class NativeLibraryExtractor
{
    private readonly SafeArchiveExtractor _extractor = new();

    public async Task ExtractAsync(IReadOnlyList<string> nativeJars, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var jar in nativeJars)
        {
            ct.ThrowIfCancellationRequested();
            var temp = Path.Combine(Path.GetDirectoryName(destination)!, "native-extract-" + Guid.NewGuid().ToString("N"));
            try
            {
                await _extractor.ExtractAsync(jar, temp, null, ct);
                foreach (var source in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(temp, source);
                    if (relative.StartsWith("META-INF" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.Combine(destination, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, true);
                }
            }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (IOException) { }
            }
        }
    }
}
