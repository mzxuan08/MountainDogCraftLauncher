using MDCL.Core.Instances;
using MDCL.Desktop.ViewModels;
using Xunit;

namespace MDCL.Desktop.Tests;

public sealed class InstanceRowTests
{
    [Fact]
    public void InstanceRow_ShowsStableVersionAndName()
    {
        var id = InstanceId.New();
        var row = new InstanceRow(new InstanceManifest
        {
            InstanceId = id,
            DisplayName = "生存",
            BaseVersion = "1.20.1",
            GameDirectory = ".minecraft",
            CreatedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(id, row.Id);
        Assert.Contains("生存", row.Display, StringComparison.Ordinal);
        Assert.Contains("1.20.1", row.Display, StringComparison.Ordinal);
    }
}
