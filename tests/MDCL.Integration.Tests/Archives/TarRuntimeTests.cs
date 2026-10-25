using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using MDCL.Core.Errors;
using MDCL.Infrastructure.Archives;
using Xunit;

namespace MDCL.Integration.Tests.Archives;

public sealed class TarRuntimeTests
{
    [Fact]
    public async Task GzipTarRuntime_IsDetectedByContentAndExtracted()
    {
        using var temp = new TestDirectory();
        var archive = Path.Combine(temp.Path, "package.zip"); // downloader's neutral name
        WriteTar(archive, "jdk/Contents/Home/bin/java", TarEntryType.RegularFile, "runtime");
        var target = Path.Combine(temp.Path, "out");
        var entries = await new SafeArchiveExtractor().ExtractAsync(archive, target,
            new ArchiveExtractionPolicy(RestoreUnixExecutable: true), CancellationToken.None);
        Assert.Equal("runtime", await File.ReadAllTextAsync(Path.Combine(target, "jdk/Contents/Home/bin/java")));
        Assert.Single(entries);
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(Path.Combine(target, "jdk/Contents/Home/bin/java")).HasFlag(UnixFileMode.UserExecute));
    }

    [Theory]
    [InlineData("../escape", TarEntryType.RegularFile, "ARCHIVE_UNSAFE_ENTRY")]
    [InlineData("link", TarEntryType.SymbolicLink, "ARCHIVE_LINK_REJECTED")]
    [InlineData("hardlink", TarEntryType.HardLink, "ARCHIVE_LINK_REJECTED")]
    public async Task UnsafeTar_IsRejected(string name, TarEntryType type, string code)
    {
        using var temp = new TestDirectory();
        var archive = Path.Combine(temp.Path, "runtime.tar.gz");
        WriteTar(archive, name, type, "x");
        var target = Path.Combine(temp.Path, "out");
        var error = await Assert.ThrowsAsync<MdclException>(() => new SafeArchiveExtractor().ExtractAsync(
            archive, target, null, CancellationToken.None));
        Assert.Equal(code, error.Error.ErrorCode);
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(temp.Path, "escape")));
    }

    [Fact]
    public async Task TarEntrySizeLimit_IsEnforced()
    {
        using var temp = new TestDirectory();
        var archive = Path.Combine(temp.Path, "runtime.tar.gz");
        WriteTar(archive, "large", TarEntryType.RegularFile, "123456789");
        var error = await Assert.ThrowsAsync<MdclException>(() => new SafeArchiveExtractor().ExtractAsync(
            archive, Path.Combine(temp.Path, "out"), new ArchiveExtractionPolicy(MaxEntryBytes: 4), CancellationToken.None));
        Assert.Equal("ARCHIVE_LIMIT_EXCEEDED", error.Error.ErrorCode);
    }

    private static void WriteTar(string path, string name, TarEntryType type, string content)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        using var writer = new TarWriter(gzip, leaveOpen: true);
        var entry = new PaxTarEntry(type, name) { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute };
        if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = "../../escape";
        else entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        writer.WriteEntry(entry);
    }
}
