using System.IO.Compression;
using MDCL.Infrastructure.Diagnostics;
using MDCL.Platform.Windows.Paths;
using Xunit;

namespace MDCL.Integration.Tests.Launch;

public sealed class DiagnosticArchiveTests
{
    [Fact]
    public async Task Export_ContainsOnlyReviewedRedactedReport()
    {
        using var temp=new TestDirectory();
        var paths=new WindowsAppPaths(temp.Path); paths.EnsureDirectories();
        await File.WriteAllTextAsync(Path.Combine(paths.ConfigRoot,"accounts.json"),"PRIVATE_ACCOUNT_CANARY");
        var service=new DiagnosticArchiveService(paths);
        var preview=service.CreatePreview(["refresh_token=short-private-token",paths.DataRoot+"/instance"]);
        Assert.DoesNotContain("short-private-token",preview,StringComparison.Ordinal);
        Assert.DoesNotContain(paths.DataRoot,preview,StringComparison.Ordinal);
        using(var json=System.Text.Json.JsonDocument.Parse(preview))
            Assert.All(json.RootElement.GetProperty("logs").EnumerateArray(),line=>Assert.DoesNotContain(paths.DataRoot,line.GetString()!,StringComparison.Ordinal));
        var file=await service.ExportAsync(preview,CancellationToken.None);
        using var zip=ZipFile.OpenRead(file);
        var entry=Assert.Single(zip.Entries);
        Assert.Equal("report.json",entry.FullName);
        using var reader=new StreamReader(entry.Open());
        var report=await reader.ReadToEndAsync();
        Assert.DoesNotContain("PRIVATE_ACCOUNT_CANARY",report,StringComparison.Ordinal);
        Assert.DoesNotContain("short-private-token",report,StringComparison.Ordinal);
    }
}
