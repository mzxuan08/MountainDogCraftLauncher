using MDCL.Infrastructure.Updates;
using Xunit;

namespace MDCL.Integration.Tests.Updates;

public sealed class UpdateProcessGuardTests
{
    [Fact]
    public async Task MissingProcess_ReturnsImmediately()
    {
        await UpdateProcessGuard.WaitForExitAsync(int.MaxValue, TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    [Fact]
    public async Task RunningCurrentProcess_TimesOut()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            UpdateProcessGuard.WaitForExitAsync(Environment.ProcessId, TimeSpan.FromMilliseconds(100), CancellationToken.None));
    }
}
