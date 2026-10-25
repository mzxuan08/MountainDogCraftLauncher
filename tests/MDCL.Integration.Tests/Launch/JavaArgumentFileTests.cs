using MDCL.Infrastructure.Processes;
using Xunit;

namespace MDCL.Integration.Tests.Launch;

public sealed class JavaArgumentFileTests
{
    [Fact]
    public async Task Writer_QuotesSpacesQuotesAndBackslashesWithoutLosingArguments()
    {
        using var temp = new TestDirectory();
        var arguments = new[] { "plain", "含 空格", "quote\"inside", @"back\slash", "trailing\\" };
        var path = await JavaArgumentFileWriter.WriteAsync(temp.Path, arguments, CancellationToken.None);
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(arguments.Length, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("\"", line, StringComparison.Ordinal));
        Assert.Contains("quote\\\"inside", lines[2], StringComparison.Ordinal);
        Assert.Contains("back\\\\slash", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writer_RejectsNewlines()
    {
        using var temp = new TestDirectory();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            JavaArgumentFileWriter.WriteAsync(temp.Path, ["bad\narg"], CancellationToken.None));
    }
}
