using System.IO.Compression;
using System.Text;
using MDCL.Infrastructure.Diagnostics;
using MDCL.Infrastructure.Processes;
using Xunit;

namespace MDCL.Integration.Tests.Launch;

public sealed class DiagnosticsAndNativeTests
{
    [Fact]
    public void Redactor_RemovesHeadersTokensQueriesAndKnownSecrets()
    {
        var redactor = new SensitiveDataRedactor();
        var text = "Authorization: Bearer abcdef token=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature123 " +
                   "https://example.invalid/cb?code=oauth-code&state=ok known-refresh-token";
        var result = redactor.Redact(text, ["known-refresh-token"]);
        Assert.DoesNotContain("abcdef", result, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth-code", result, StringComparison.Ordinal);
        Assert.DoesNotContain("known-refresh-token", result, StringComparison.Ordinal);
        Assert.Contains("<redacted>", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeExtractor_MergesNativeJarsAndDropsMetaInf()
    {
        using var temp = new TestDirectory();
        var first = Path.Combine(temp.Path, "one.jar");
        var second = Path.Combine(temp.Path, "two.jar");
        CreateZip(first, ("natives/one.dll", "one"), ("META-INF/MANIFEST.MF", "meta"));
        CreateZip(second, ("natives/two.dylib", "two"), ("META-INF/versions/9/x", "meta"));
        var destination = Path.Combine(temp.Path, "natives");
        await new NativeLibraryExtractor().ExtractAsync([first, second], destination, CancellationToken.None);
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(destination, "natives", "one.dll")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(destination, "natives", "two.dylib")));
        Assert.False(Directory.Exists(Path.Combine(destination, "META-INF")));
    }

    private static void CreateZip(string path, params (string Name, string Body)[] entries)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, body) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(body);
        }
    }
}
