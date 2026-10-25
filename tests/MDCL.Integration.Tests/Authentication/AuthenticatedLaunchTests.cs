using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Accounts;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Core.Java;
using MDCL.Core.Versions;
using MDCL.Infrastructure.Launcher;
using MDCL.Infrastructure.Storage;
using MDCL.Platform.Windows.Paths;
using Xunit;

namespace MDCL.Integration.Tests.Authentication;

public sealed class AuthenticatedLaunchTests
{
    [Theory]
    [InlineData(AccountKind.Local, "", false, "AUTH_MICROSOFT_REQUIRED")]
    [InlineData(AccountKind.Microsoft, "", false, "AUTH_SESSION_INVALID")]
    [InlineData(AccountKind.Microsoft, "expired-fixture-token", true, "AUTH_SESSION_INVALID")]
    public async Task InvalidIdentity_IsRejectedBeforeJavaOrProcessWork_AndRetainsAllData(
        AccountKind kind, string token, bool expired, string code)
    {
        using var temp = new TestDirectory();
        var paths = new WindowsAppPaths(temp.Path); paths.EnsureDirectories();
        var repository = new FileInstanceRepository(paths.InstancesRoot);
        var leases = new FileInstanceLeaseManager(paths.InstancesRoot);
        var instance = new InstanceManifest { InstanceId = InstanceId.New(), DisplayName = "preserved", BaseVersion = "1.20.1", GameDirectory = ".minecraft" };
        await repository.SaveAsync(instance, 0, CancellationToken.None);
        var directory = Path.Combine(paths.InstancesRoot, instance.InstanceId.ToString());
        var canary = Path.Combine(directory, "save-canary.txt");
        await File.WriteAllTextAsync(canary, "preserved");
        var resolved = new ResolvedVersion("1.20.1", "Test.Main", [], [], [], [], null, null, null,
            new JavaRequirement(17,17,null,CpuArchitecture.X64,[],[]), [], "fixture");
        await new ResolvedVersionStore().SaveAsync(directory, resolved, CancellationToken.None);
        var profile = new AccountProfile(AccountId.New(), "fixture", kind, "fixture-uuid");
        var accounts = new FixedAccount(new AuthSession(profile, token, expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(1)));
        // Null dependencies deliberately detect any Java/process work before authentication.
        var workflow = new LauncherWorkflow(paths, repository, null!, leases, null!, null!, null!, null!, null!, null!, null!, accounts, null!, null!);
        var error = await Assert.ThrowsAsync<MdclException>(() => workflow.LaunchAsync(instance.InstanceId, profile.Id, null, CancellationToken.None));
        Assert.Equal(code, error.Error.ErrorCode);
        Assert.Equal("preserved", await File.ReadAllTextAsync(canary));
        Assert.Single(await repository.ListAsync(CancellationToken.None));
        await using var reacquired = await leases.AcquireAsync(instance.InstanceId, "after-rejection", CancellationToken.None);
    }

    private sealed class FixedAccount(AuthSession session) : IAccountService
    {
        public Task<AuthSession> GetSessionAsync(AccountId id, bool allowInteractive, CancellationToken ct) => Task.FromResult(session);
        public Task<IReadOnlyList<AccountProfile>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AccountProfile> AddLocalAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<AccountProfile> LoginMicrosoftAsync(IProgress<AuthStageEvent>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task LogoutAsync(AccountId id, CancellationToken ct) => throw new NotSupportedException();
    }
}
