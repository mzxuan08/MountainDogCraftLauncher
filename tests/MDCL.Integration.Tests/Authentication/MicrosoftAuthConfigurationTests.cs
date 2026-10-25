using MDCL.Core.Errors;
using MDCL.Infrastructure.Authentication;
using Xunit;

namespace MDCL.Integration.Tests.Authentication;

public sealed class MicrosoftAuthConfigurationTests
{
    private const string First = "cf4175e2-8cc4-4a52-9c12-603f1f46d8bb";
    private const string Second = "e90842dd-45bb-43fa-b9b2-724c0ce2f8a0";

    [Fact]
    public async Task BundledIdentity_IsDefault_AndClearingOverrideRestoresIt()
    {
        using var temp = new TestDirectory();
        var store = new MicrosoftAuthConfigurationStore(temp.Path, bundledClientId: First);
        Assert.Equal(First, store.CurrentOptions.ClientId);
        Assert.Equal("MDCL 内置应用", store.Source);
        await store.SaveAsync(Second, CancellationToken.None);
        Assert.Equal(Second, new MicrosoftAuthConfigurationStore(temp.Path, bundledClientId: First).CurrentOptions.ClientId);
        await store.SaveAsync("", CancellationToken.None);
        Assert.Equal(First, new MicrosoftAuthConfigurationStore(temp.Path, bundledClientId: First).CurrentOptions.ClientId);
    }

    [Fact]
    public async Task SaveAndReopen_UsesPublicIdOnly_AndDoesNotMutateInFlightOptions()
    {
        using var temp = new TestDirectory();
        var store = new MicrosoftAuthConfigurationStore(temp.Path);
        Assert.Empty(store.CurrentOptions.ClientId);
        await store.SaveAsync(" " + First.ToUpperInvariant() + " ", CancellationToken.None);
        Assert.Empty(store.CurrentOptions.ClientId);
        var reopened = new MicrosoftAuthConfigurationStore(temp.Path);
        Assert.Equal(First, reopened.CurrentOptions.ClientId);
        Assert.Equal("本地配置", reopened.Source);
        var text = await File.ReadAllTextAsync(store.ConfigurationPath);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<MdclException>(() => store.SaveAsync("not-an-app-id", CancellationToken.None));
        Assert.Equal(text, await File.ReadAllTextAsync(store.ConfigurationPath));
    }

    [Fact]
    public async Task EnvironmentOverride_IsExplicit_AndNeverOverwritesSavedIdentity()
    {
        using var temp = new TestDirectory();
        await new MicrosoftAuthConfigurationStore(temp.Path).SaveAsync(First, CancellationToken.None);
        var store = new MicrosoftAuthConfigurationStore(temp.Path, Second);
        Assert.Equal(Second, store.CurrentOptions.ClientId);
        Assert.Equal(First, store.SavedClientId);
        Assert.True(store.HasEnvironmentOverride);
        Assert.Empty(new MicrosoftAuthConfigurationStore(temp.Path, "invalid").CurrentOptions.ClientId);
        Assert.Equal(First, new MicrosoftAuthConfigurationStore(temp.Path).CurrentOptions.ClientId);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":99,\"clientId\":\"" + First + "\"}")]
    [InlineData("{\"clientId\":\"" + First + "\",\"clientSecret\":\"unexpected\"}")]
    public async Task InvalidConfiguration_DisablesMicrosoftWithoutBreakingLocalStartup(string document)
    {
        using var temp = new TestDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, MicrosoftAuthConfigurationStore.FileName), document);
        var store = new MicrosoftAuthConfigurationStore(temp.Path);
        Assert.Empty(store.CurrentOptions.ClientId);
        Assert.NotNull(store.LoadIssue);
        await store.SaveAsync(First, CancellationToken.None);
        Assert.Equal(First, new MicrosoftAuthConfigurationStore(temp.Path).CurrentOptions.ClientId);
    }

    [Fact]
    public async Task CanceledSave_PreservesExistingConfigurationAndCleansTemporaryFile()
    {
        using var temp = new TestDirectory();
        var store = new MicrosoftAuthConfigurationStore(temp.Path);
        await store.SaveAsync(First, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Second, new CancellationToken(true)));
        Assert.Equal(First, new MicrosoftAuthConfigurationStore(temp.Path).CurrentOptions.ClientId);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp"));
    }
}
