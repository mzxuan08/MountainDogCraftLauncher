using System.Text;
using MDCL.Platform.Windows.Credentials;
using Xunit;

namespace MDCL.Integration.Tests.Authentication;

public sealed class CredentialVaultTests
{
    [Fact]
    public async Task DpapiVault_RoundTripsAndDeletesWithoutPlaintext()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TestDirectory();
        var vault = new DpapiCredentialVault(temp.Path);
        var secret = Encoding.UTF8.GetBytes("refresh-token-secret");
        await vault.ReplaceAsync("account/test/refresh-token", secret, CancellationToken.None);
        var loaded = await vault.ReadAsync("account/test/refresh-token", CancellationToken.None);
        Assert.Equal(secret, loaded!.Value.ToArray());
        var files = Directory.GetFiles(temp.Path);
        var single = Assert.Single(files);
        var encrypted = await File.ReadAllBytesAsync(single);
        Assert.DoesNotContain("refresh-token-secret", Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);
        await vault.DeleteAsync("account/test/refresh-token", CancellationToken.None);
        Assert.Null(await vault.ReadAsync("account/test/refresh-token", CancellationToken.None));
    }
}
