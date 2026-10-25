using System.IO.Compression;
using MDCL.Core.Errors;

namespace MDCL.Infrastructure.Archives;

public sealed record ArchiveExtractionPolicy(
    int MaxEntries = 10_000,
    long MaxEntryBytes = 2L * 1024 * 1024 * 1024,
    long MaxTotalBytes = 8L * 1024 * 1024 * 1024,
    long MaxCompressionRatio = 200,
    bool RestoreUnixExecutable = false);

public sealed record ExtractedEntry(string RelativePath, long Length);

public sealed class SafeArchiveExtractor
{
    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public async Task<IReadOnlyList<ExtractedEntry>> ExtractAsync(
        string archivePath, string destinationRoot, ArchiveExtractionPolicy? policy, CancellationToken ct)
    {
        policy ??= new ArchiveExtractionPolicy();
        var root = Path.GetFullPath(destinationRoot);
        var rootExisted = Directory.Exists(root);
        if (rootExisted && Directory.EnumerateFileSystemEntries(root).Any())
            throw new MdclException(MdclError.Of("ARCHIVE_ROOT_NOT_EMPTY", "Archives", "解压目标不是空目录。", false, root));
        Directory.CreateDirectory(root);
        var extracted = new List<ExtractedEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var foldedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        try
        {
            await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > policy.MaxEntries)
                throw Limit("条目数量超过限制。");
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var relative = Normalize(entry.FullName);
                if (relative.Length == 0) continue;
                if (!names.Add(relative) || !foldedNames.Add(relative))
                    throw new MdclException(MdclError.Of("ARCHIVE_DUPLICATE_ENTRY", "Archives", $"归档包含重复或大小写冲突条目：{relative}。", false, relative));
                var isDirectory = string.IsNullOrEmpty(entry.Name);
                var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
                if ((mode & 0xF000) == 0xA000)
                    throw new MdclException(MdclError.Of("ARCHIVE_LINK_REJECTED", "Archives", $"归档包含链接条目：{relative}。", false, relative));
                if (entry.Length < 0 || entry.Length > policy.MaxEntryBytes) throw Limit($"条目过大：{relative}。");
                total = checked(total + entry.Length);
                if (total > policy.MaxTotalBytes) throw Limit("解压总大小超过限制。");
                if (entry.Length > 10 * 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > policy.MaxCompressionRatio)
                    throw Limit($"条目压缩比异常：{relative}。");
                var target = SafeTarget(root, relative);
                if (isDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                EnsureNoReparseAncestors(root, Path.GetDirectoryName(target)!);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await input.CopyToAsync(output, ct);
                await output.FlushAsync(ct);
                output.Flush(true);
                if (policy.RestoreUnixExecutable && !OperatingSystem.IsWindows() && (mode & 0x49) != 0)
                    File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                extracted.Add(new ExtractedEntry(relative, entry.Length));
            }
            return extracted;
        }
        catch
        {
            if (!rootExisted)
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
            throw;
        }
    }

    private static string Normalize(string archiveName)
    {
        var name = archiveName.Replace('\\', '/');
        if (name.IndexOf('\0') >= 0 || name.StartsWith('/') || name.Contains(':'))
            throw Unsafe(archiveName);
        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return "";
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') || WindowsReserved.Contains(segment))
                throw Unsafe(archiveName);
            if (segment.Length == 0 || segment.Any(char.IsControl)) throw Unsafe(archiveName);
        }
        return string.Join('/', segments);
    }

    private static string SafeTarget(string root, string relative)
    {
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rel = Path.GetRelativePath(root, target);
        if (rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(rel))
            throw Unsafe(relative);
        return target;
    }

    private static void EnsureNoReparseAncestors(string root, string directory)
    {
        var current = Path.GetFullPath(directory);
        while (current.Length >= root.Length)
        {
            if (Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || new DirectoryInfo(current).LinkTarget is not null)
                    throw new MdclException(MdclError.Of("ARCHIVE_REPARSE_POINT", "Archives", "解压父目录包含重解析点或链接。", false, current));
            }
            if (string.Equals(current, root, StringComparison.Ordinal)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static MdclException Limit(string message) => new(MdclError.Of("ARCHIVE_LIMIT_EXCEEDED", "Archives", message, false));
    private static MdclException Unsafe(string path) => new(MdclError.Of("ARCHIVE_UNSAFE_ENTRY", "Archives", $"归档路径越界或非法：{path}。", false, path));
}
