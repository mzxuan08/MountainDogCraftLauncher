using System.Net;
using System.Text;
using System.Text.Json;
using MDCL.Application.Ports;
using MDCL.Core.Accounts;
using MDCL.Core.Errors;
using MDCL.Infrastructure.Authentication;
using Xunit;

namespace MDCL.Integration.Tests.Authentication;

public sealed class MicrosoftAccountServiceTests
{
    private static readonly MicrosoftAuthOptions Options = new("public-test-client", 34127);

    [Fact]
    public async Task LocalAccount_IsExplicitlyLocalAndDoesNotNeedSecrets()
    {
        using var temp = new TestDirectory();
        var service = CreateService(temp, new FakeVault(), new FakeAuthCode());
        var profile = await service.AddLocalAsync("本地玩家", CancellationToken.None);
        var session = await service.GetSessionAsync(profile.Id, false, CancellationToken.None);
        Assert.Equal(AccountKind.Local, session.Profile.Kind);
        Assert.Equal("", session.AccessToken);
        Assert.Equal("本地身份", session.Profile.Kind == AccountKind.Local ? "本地身份" : "other");
    }

    [Fact]
    public async Task Login_PersistsPublicProfileAndRefreshToken_AndLogoutDeletesBoth()
    {
        using var temp = new TestDirectory();
        var vault = new FakeVault();
        var service = CreateService(temp, vault, new FakeAuthCode());
        var profile = await service.LoginMicrosoftAsync(null, CancellationToken.None);
        Assert.Equal(AccountKind.Microsoft, profile.Kind);
        Assert.Single(await service.ListAsync(CancellationToken.None));
        Assert.Single(vault.Values);
        var session = await service.GetSessionAsync(profile.Id, false, CancellationToken.None);
        Assert.Equal("mc-access", session.AccessToken);
        await service.LogoutAsync(profile.Id, CancellationToken.None);
        Assert.Empty(await service.ListAsync(CancellationToken.None));
        Assert.Empty(vault.Values);
    }

    [Fact]
    public async Task ExpiredCachedSession_RefreshesOnlyOnceUnderConcurrency()
    {
        using var temp = new TestDirectory();
        var vault = new FakeVault();
        var store = new JsonAccountStore(Path.Combine(temp.Path, "accounts.json"));
        var profile = new AccountProfile(AccountId.New(), "Player", AccountKind.Microsoft, "uuid");
        await store.SaveAsync(new AccountRecord(profile, "account/" + profile.Id + "/refresh-token"), CancellationToken.None);
        await vault.ReplaceAsync("account/" + profile.Id + "/refresh-token", Encoding.UTF8.GetBytes("old"), CancellationToken.None);
        var handler = new CountingHandler();
        var protocol = new MicrosoftAuthProtocol(new HttpClient(handler), Options);
        var service = new MicrosoftAccountService(Options, store, vault, protocol, new FakeAuthCode());
        var first = service.GetSessionAsync(profile.Id, false, CancellationToken.None);
        var second = service.GetSessionAsync(profile.Id, false, CancellationToken.None);
        var sessions = await Task.WhenAll(first, second);
        Assert.All(sessions, x => Assert.Equal(profile.Id, x.Profile.Id));
        Assert.Equal(1, handler.TokenRequests);
        Assert.Equal("mc-access", sessions[0].AccessToken);
    }

    [Fact]
    public async Task CredentialReadFailure_DoesNotFallBackToPlaintext()
    {
        using var temp = new TestDirectory();
        var store = new JsonAccountStore(Path.Combine(temp.Path, "accounts.json"));
        var profile = new AccountProfile(AccountId.New(), "Player", AccountKind.Microsoft, "uuid");
        var key = "account/" + profile.Id + "/refresh-token";
        await store.SaveAsync(new AccountRecord(profile, key), CancellationToken.None);
        var service = CreateService(temp, new FakeVault { FailRead = true }, new FakeAuthCode());
        var error = await Assert.ThrowsAsync<MdclException>(() => service.GetSessionAsync(profile.Id, false, CancellationToken.None));
        Assert.Equal("CREDENTIAL_DENIED", error.Error.ErrorCode);
    }

    [Fact]
    public async Task NoClientId_MakesRealLoginConditionUnavailable()
    {
        using var temp = new TestDirectory();
        var service = CreateService(temp, new FakeVault(), new FakeAuthCode(), new MicrosoftAuthOptions(""));
        var error = await Assert.ThrowsAsync<MdclException>(() => service.LoginMicrosoftAsync(null, CancellationToken.None));
        Assert.Equal("AUTH_CLIENT_NOT_CONFIGURED", error.Error.ErrorCode);
    }

    private static MicrosoftAccountService CreateService(
        TestDirectory temp, FakeVault vault, FakeAuthCode code, MicrosoftAuthOptions? options = null)
    {
        options ??= Options;
        var store = new JsonAccountStore(Path.Combine(temp.Path, "accounts.json"));
        var protocol = new MicrosoftAuthProtocol(new HttpClient(new CountingHandler()), options);
        return new MicrosoftAccountService(options, store, vault, protocol, code);
    }

    private sealed class FakeAuthCode : IAuthorizationCodeProvider
    {
        public Task<string> AcquireCodeAsync(MicrosoftAuthOptions options, string state, string challenge, CancellationToken ct) =>
            Task.FromResult("auth-code");
    }

    private sealed class FakeVault : ICredentialVault
    {
        public Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);
        public bool FailRead { get; init; }

        public Task<ReadOnlyMemory<byte>?> ReadAsync(string key, CancellationToken ct)
        {
            if (FailRead) throw new MdclException(MdclError.Of("CREDENTIAL_DENIED", "Credentials", "vault denied", true));
            return Task.FromResult(Values.TryGetValue(key, out var value) ? (ReadOnlyMemory<byte>?)value : null);
        }

        public Task ReplaceAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
        {
            Values[key] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int TokenRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
            {
                TokenRequests++;
                await Task.Delay(100, cancellationToken);
                return Json(new { access_token = "msa-access", refresh_token = "rotated", expires_in = 3600 });
            }
            if (path == "/user/authenticate")
                return Json(new { Token = "xbox-token", DisplayClaims = new { xui = new[] { new { uhs = "user-hash" } } } });
            if (path == "/xsts/authorize") return Json(new { Token = "xsts-token" });
            if (path == "/authentication/login_with_xbox") return Json(new { access_token = "mc-access", expires_in = 86400 });
            if (path == "/entitlements/mcstore") return Json(new { items = new object[] { new { name = "game_minecraft" } } });
            if (path == "/minecraft/profile") return Json(new { id = "uuid", name = "Player" });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }
}
