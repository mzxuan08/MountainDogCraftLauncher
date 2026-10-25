using System.Security.Cryptography;
using MDCL.Core.Errors;

namespace MDCL.Infrastructure.Installers;

public sealed record ForgeMergeResult(int CopiedLibraries, int CopiedVersions, int SkippedIdentical);

public sealed class ForgeStagingMerger
{
    public async Task<ForgeMergeResult> MergeAsync(string stagingDirectory, string sharedRoot, CancellationToken ct)
    {
        var staging = Path.GetFullPath(stagingDirectory);
        var shared = Path.GetFullPath(sharedRoot);
        if (!Directory.Exists(staging)) throw new DirectoryNotFoundException(staging);
        var libraries = 0;
        var versions = 0;
        var skipped = 0;
        foreach (var area in new[] { "libraries", "versions" })
        {
            var sourceRoot = Path.Combine(staging, area);
            if (!Directory.Exists(sourceRoot)) continue;
            var targetRoot = Path.Combine(shared, area);
            foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceRoot, source);
                if (IsUnsafe(relative)) throw new MdclException(MdclError.Of("LOADER_PATH_UNSAFE", "Forge",
                    $"安装器产物路径越界：{relative}", false));
                var target = Path.Combine(targetRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    if (await SameHashAsync(source, target, ct)) { skipped++; continue; }
                    throw new MdclException(MdclError.Of("LOADER_STAGING_CONFLICT", "Forge",
                        $"安装器产物与共享库冲突：{relative}", false));
                }
                await using var input = File.OpenRead(source);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await input.CopyToAsync(output, ct);
                if (area == "libraries") libraries++; else versions++;
            }
        }
        return new ForgeMergeResult(libraries, versions, skipped);
    }

    private static bool IsUnsafe(string relative) =>
        Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => x == "..");

    private static async Task<bool> SameHashAsync(string a, string b, CancellationToken ct)
    {
        await using var first = File.OpenRead(a);
        await using var second = File.OpenRead(b);
        var hashA = await SHA256.HashDataAsync(first, ct);
        var hashB = await SHA256.HashDataAsync(second, ct);
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }
}
