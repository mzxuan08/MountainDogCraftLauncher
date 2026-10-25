using MDCL.Core.Accounts;

namespace MDCL.Application.Ports;

public interface IAccountStore
{
    Task<IReadOnlyList<AccountRecord>> ListAsync(CancellationToken ct);
    Task<AccountRecord?> GetAsync(AccountId id, CancellationToken ct);
    Task SaveAsync(AccountRecord account, CancellationToken ct);
    Task DeleteAsync(AccountId id, CancellationToken ct);
}

public interface IAccountService
{
    Task<IReadOnlyList<AccountProfile>> ListAsync(CancellationToken ct);
    Task<AccountProfile> AddLocalAsync(string displayName, CancellationToken ct);
    Task<AccountProfile> LoginMicrosoftAsync(IProgress<AuthStageEvent>? progress, CancellationToken ct);
    Task<AuthSession> GetSessionAsync(AccountId accountId, bool allowInteractive, CancellationToken ct);
    Task LogoutAsync(AccountId accountId, CancellationToken ct);
}

public sealed record MicrosoftAuthOptions(
    string ClientId,
    int RedirectPort = 34127,
    string Tenant = "consumers",
    string Scopes = "XboxLive.signin offline_access")
{
    public string RedirectUri => $"http://127.0.0.1:{RedirectPort}/callback";
    public string Authority => $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0";
}

public interface IAuthorizationCodeProvider
{
    Task<string> AcquireCodeAsync(MicrosoftAuthOptions options, string state, string challenge, CancellationToken ct);
}

public interface IBrowserLauncher
{
    Task OpenAsync(Uri uri, CancellationToken ct);
}
