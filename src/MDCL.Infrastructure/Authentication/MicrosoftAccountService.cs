using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MDCL.Application.Ports;
using MDCL.Core.Accounts;
using MDCL.Core.Errors;

namespace MDCL.Infrastructure.Authentication;

public sealed class MicrosoftAccountService : IAccountService
{
    private readonly MicrosoftAuthOptions _options;
    private readonly IAccountStore _store;
    private readonly ICredentialVault _vault;
    private readonly MicrosoftAuthProtocol _protocol;
    private readonly IAuthorizationCodeProvider _authorization;
    private readonly ConcurrentDictionary<AccountId, AuthSession> _cache = new();
    private readonly ConcurrentDictionary<AccountId, SemaphoreSlim> _refreshLocks = new();

    public MicrosoftAccountService(
        MicrosoftAuthOptions options, IAccountStore store, ICredentialVault vault,
        MicrosoftAuthProtocol protocol, IAuthorizationCodeProvider authorization)
    {
        _options = options;
        _store = store;
        _vault = vault;
        _protocol = protocol;
        _authorization = authorization;
    }

    public async Task<IReadOnlyList<AccountProfile>> ListAsync(CancellationToken ct) =>
        (await _store.ListAsync(ct)).Select(x => x.Profile).ToArray();

    public async Task<AccountProfile> AddLocalAsync(string displayName, CancellationToken ct)
    {
        var name = (displayName ?? "").Trim();
        if (name.Length is < 1 or > 32)
            throw new MdclException(MdclError.Of("ACCOUNT_NAME_INVALID", "Accounts", "本地身份名称必须为 1—32 个字符。", false));
        var profile = new AccountProfile(AccountId.New(), name, AccountKind.Local, Guid.NewGuid().ToString("N"));
        await _store.SaveAsync(new AccountRecord(profile, ""), ct);
        return profile;
    }

    public async Task<AccountProfile> LoginMicrosoftAsync(IProgress<AuthStageEvent>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new MdclException(MdclError.Of("AUTH_CLIENT_NOT_CONFIGURED", "Authentication",
                "尚未配置 MDCL 专属 Microsoft public client ID；真实登录不可用。", false));
        progress?.Report(new AuthStageEvent(AuthStage.Starting, "auth-starting", DateTimeOffset.UtcNow));
        var (verifier, challenge) = MicrosoftAuthProtocol.CreatePkce();
        var state = MicrosoftAuthProtocol.CreateState();
        var code = await _authorization.AcquireCodeAsync(_options, state, challenge, ct);
        progress?.Report(new AuthStageEvent(AuthStage.BrowserOpened, "auth-code-received", DateTimeOffset.UtcNow));
        var result = await _protocol.LoginAsync(code, verifier, ct, progress);
        var record = new AccountRecord(result.Session.Profile, CredentialKey(result.Session.Profile.Id));
        await _vault.ReplaceAsync(record.CredentialKey, Encoding.UTF8.GetBytes(result.RefreshToken), ct);
        await _store.SaveAsync(record, ct);
        _cache[record.Profile.Id] = result.Session;
        progress?.Report(new AuthStageEvent(AuthStage.Completed, "auth-completed", DateTimeOffset.UtcNow));
        return record.Profile;
    }

    public async Task<AuthSession> GetSessionAsync(AccountId accountId, bool allowInteractive, CancellationToken ct)
    {
        var record = await _store.GetAsync(accountId, ct)
            ?? throw new MdclException(MdclError.Of("ACCOUNT_NOT_FOUND", "Authentication", "账号不存在。", false));
        if (record.Profile.Kind == AccountKind.Local)
            return new AuthSession(record.Profile, "", DateTimeOffset.MaxValue);
        if (_cache.TryGetValue(accountId, out var cached) && cached.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2))
            return cached;

        var gate = _refreshLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(accountId, out cached) && cached.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2))
                return cached;
            var key = string.IsNullOrWhiteSpace(record.CredentialKey) ? CredentialKey(accountId) : record.CredentialKey;
            var bytes = await _vault.ReadAsync(key, ct);
            if (bytes is null)
                throw new MdclException(MdclError.Of("AUTH_REQUIRED", "Authentication", "刷新令牌不可用，需要重新登录。", false));
            var refresh = Encoding.UTF8.GetString(bytes.Value.Span);
            MicrosoftLoginResult refreshed;
            try { refreshed = await _protocol.RefreshAsync(refresh, ct); }
            catch (MdclException ex) when (ex.Error.ErrorCode is "AUTH_REFRESH_EXPIRED" or "AUTH_CANCELED")
            {
                throw new MdclException(MdclError.Of("AUTH_REFRESH_EXPIRED", "Authentication", "授权已失效，需要重新登录。", false));
            }
            if (!string.Equals(refresh, refreshed.RefreshToken, StringComparison.Ordinal))
                await _vault.ReplaceAsync(key, Encoding.UTF8.GetBytes(refreshed.RefreshToken), ct);
            var profile = record.Profile with
            {
                DisplayName = refreshed.Session.Profile.DisplayName,
                MinecraftUuid = refreshed.Session.Profile.MinecraftUuid
            };
            var session = new AuthSession(profile, refreshed.Session.AccessToken, refreshed.Session.ExpiresAt);
            if (profile != record.Profile) await _store.SaveAsync(record with { Profile = profile }, ct);
            _cache[accountId] = session;
            return session;
        }
        finally { gate.Release(); }
    }

    public async Task LogoutAsync(AccountId accountId, CancellationToken ct)
    {
        var record = await _store.GetAsync(accountId, ct);
        _cache.TryRemove(accountId, out _);
        if (record is not null && !string.IsNullOrWhiteSpace(record.CredentialKey))
            await _vault.DeleteAsync(record.CredentialKey, ct);
        await _store.DeleteAsync(accountId, ct);
    }

    private static string CredentialKey(AccountId id) => $"account/{id}/refresh-token";
}
