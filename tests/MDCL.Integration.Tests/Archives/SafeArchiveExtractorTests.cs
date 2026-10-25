using System.IO.Compression;
using System.Text;
using MDCL.Core.Errors;
using MDCL.Infrastructure.Archives;
using Xunit;

namespace MDCL.Integration.Tests.Archives;

public sealed class SafeArchiveExtractorTests
{
    [Fact]
    public async Task Extract_ChineseNamesAndNestedDirectories()
    {
        using var temp = new TestDirectory();
        var archive = Path.Combine(temp.Path, "pack.zip");
        CreateZip(archive, ("目录/文件.txt", "内容"), ("root.txt", "ok"));
        var destination = Path.Combine(temp.Path, "out");
        var entries = await new SafeArchiveExtractor().ExtractAsync(archive, destination, null, CancellationToken.None);
        Assert.Equal(2, entries.Count);
        Assert.Equal("内容", await File.ReadAllTextAsync(Path.Combine(destination, "目录", "文件.txt")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("dir/../../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/drive.txt")]
    [InlineData("dir\\..\\escape.txt")]
    [InlineData("file.txt:ads")]
    [InlineData("trailing.")]
    public async Task UnsafePath_IsRejectedWithoutWritingOutside(string name)
    {
        using var temp = new TestDirectory();
        var archive = Path.Combine(temp.Path, "bad.zip");
        CreateZip(archive, (name, "bad"));
        var destination = Path.Combine(temp.Path, "out");
        var error = await Assert.ThrowsAsync<MdclException>(() =>
            new SafeArchiveExtractor().ExtractAsync(archive, destination, null, CancellationToken.None));
        Assert.Equal("ARCHIVE_UNSAFE_ENTRY", error.Error.ErrorCode);
        Assert.False(File.Exists(Path.Combine(temp.Path, "escape.txt")));
    }

    [Fact]
    public async Task CaseCollisionAndDuplicate_AreRejected()
    {
        using var temp = new TestDirectory();
        var archive = Path.Combine(temp.Path, "case.zip");
        CreateZip(archive, ("File.txt", "a"), ("file.txt", "b"));
        var error = await Assert.ThrowsAsync<MdclException>(() =>
            new SafeArchiveExtractor().ExtractAsync(archive, Path.Combine(temp.Path, "out"), null, CancellationToken.None));
        Assert.Equal("ARCHIVE_DUPLICATE_ENTRY", error.Error.ErrorCode);
    }

    [Fact]
    public async Task SymlinkEntry_IsRejected()
    {
        using var temp = new TestDirectory();
        var archive = Path.Combine(temp.Path, "link.zip");
        using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("link");
            entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync("target");
        }
        var error = await Assert.ThrowsAsync<MdclException>(() =>
            new SafeArchiveExtractor().ExtractAsync(archive, Path.Combine(temp.Path, "out"), null, CancellationToken.None));
        Assert.Equal("ARCHIVE_LINK_REJECTED", error.Error.ErrorCode);
    }

    [Fact]
    public async Task EntryLimit_IsEnforced()
    {
        using var temp = new TestDirectory();
        var archive = Path.Combine(temp.Path, "many.zip");
        CreateZip(archive, ("a.txt", "a"), ("b.txt", "b"));
        var error = await Assert.ThrowsAsync<MdclException>(() =>
            new SafeArchiveExtractor().ExtractAsync(archive, Path.Combine(temp.Path, "out"),
                new ArchiveExtractionPolicy(MaxEntries: 1), CancellationToken.None));
        Assert.Equal("ARCHIVE_LIMIT_EXCEEDED", error.Error.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "out")));
    }

    private static void CreateZip(string path, params (string Name, string Body)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, body) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(body);
        }
    }
}
