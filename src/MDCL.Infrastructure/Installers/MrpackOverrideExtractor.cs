using MDCL.Core.Errors;
using MDCL.Infrastructure.Archives;
using System.Security.Cryptography;

namespace MDCL.Infrastructure.Installers;

public sealed class MrpackOverrideExtractor
{
    private readonly SafeArchiveExtractor _extractor = new();

    public async Task ExtractAsync(string packPath, string gameDirectory, string stagingRoot, CancellationToken ct)
    {
        Directory.CreateDirectory(stagingRoot);
        var temp = Path.Combine(stagingRoot, "mrpack-overrides-" + Guid.NewGuid().ToString("N"));
        try
        {
            await _extractor.ExtractAsync(packPath, temp, new ArchiveExtractionPolicy(MaxEntries: 20_000), ct);
            await MergeDirectoryAsync(Path.Combine(temp, "overrides"), gameDirectory, ct);
            await MergeDirectoryAsync(Path.Combine(temp, "client-overrides"), gameDirectory, ct);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    private static async Task MergeDirectoryAsync(string source, string target, CancellationToken ct)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.GetFullPath(Path.Combine(target, relative));
            var relativeCheck = Path.GetRelativePath(Path.GetFullPath(target), destination);
            if (relativeCheck == ".." || relativeCheck.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new MdclException(MdclError.Of("MRPACK_OVERRIDE_ESCAPE", "Modpack", "overrides 越过实例目录。", false));
            if (File.Exists(destination))
            {
                await using var existing = File.OpenRead(destination);
                await using var expected = File.OpenRead(file);
                var existingHash = await SHA256.HashDataAsync(existing, ct);
                var expectedHash = await SHA256.HashDataAsync(expected, ct);
                if (existingHash.SequenceEqual(expectedHash)) continue;
                throw new MdclException(MdclError.Of("MRPACK_OVERRIDE_CONFLICT", "Modpack",
                    $"overrides 与现有文件冲突：{relative}", false));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = File.OpenRead(file);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await input.CopyToAsync(output, ct);
        }
    }
}
