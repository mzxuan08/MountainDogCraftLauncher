using System.Text;

namespace MDCL.Infrastructure.Processes;

public static class JavaArgumentFileWriter
{
    public static async Task<string> WriteAsync(string directory, IEnumerable<string> arguments, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"mdcl-{Guid.NewGuid():N}.args");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var argument in arguments)
        {
            if (argument.Contains('\n') || argument.Contains('\r'))
                throw new ArgumentException("Java args cannot contain newlines.", nameof(arguments));
            await writer.WriteLineAsync(Quote(argument));
        }
        await writer.FlushAsync(ct);
        stream.Flush(true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    public static string Quote(string argument)
    {
        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');
        foreach (var character in argument)
        {
            if (character is '"' or '\\') builder.Append('\\');
            builder.Append(character);
        }
        builder.Append('"');
        return builder.ToString();
    }
}
