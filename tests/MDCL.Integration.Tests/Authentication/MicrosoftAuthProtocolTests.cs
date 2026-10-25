using System.Net;
using System.Text;
using System.Text.Json;
using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Infrastructure.Authentication;
using Xunit;

namespace MDCL.Integration.Tests.Authentication;

public sealed class MicrosoftAuthProtocolTests
{
    private static readonly MicrosoftAuthOptions Options = new("public-test-client", 34127);

    [Fact]
    public async Task FullProtocol_ReturnsProfileAndRefreshToken()
    {
        var handler = new FakeHandler();
        var protocol = new MicrosoftAuthProtocol(new HttpClient(handler), Options);
        var result = await protocol.LoginAsync("code", "verifier", CancellationToken.None);
        Assert.Equal("Player", result.Session.Profile.DisplayName);
        Assert.Equal("minecraft-uuid", result.Session.Profile.MinecraftUuid);
        Assert.Equal("mc-access", result.Session.AccessToken);
        Assert.Equal("refresh-1", result.RefreshToken);
        Assert.DoesNotContain(result.RefreshToken, result.Session.ToString(), StringComparison.Ordinal);
        Assert.Contains(handler.Paths, x => x == "/consumers/oauth2/v2.0/token");
        Assert.Contains(handler.Paths, x => x == "/authentication/login_with_xbox");
    }

    [Fact]
    public async Task Refresh_RotatesRefreshToken()
    {
        var handler = new FakeHandler { RotateRefresh = true };
        var protocol = new MicrosoftAuthProtocol(new HttpClient(handler), Options);
        var result = await protocol.RefreshAsync("old-refresh", CancellationToken.None);
        Assert.Equal("refresh-2", result.RefreshToken);
    }

    [Fact]
    public async Task EmptyEntitlements_IsDistinctError()
    {
        var handler = new FakeHandler { EmptyEntitlements = true };
        var protocol = new MicrosoftAuthProtocol(new HttpClient(handler), Options);
        var error = await Assert.ThrowsAsync<MdclException>(() => protocol.LoginAsync("code", "verifier", CancellationToken.None));
        Assert.Equal("AUTH_ENTITLEMENT_MISSING", error.Error.ErrorCode);
    }

    [Fact]
    public async Task XstsXErr_IsMappedNotTreatedAsNetworkError()
    {
        var handler = new FakeHandler { XstsError = 2148916233 };
        var protocol = new MicrosoftAuthProtocol(new HttpClient(handler), Options);
        var error = await Assert.ThrowsAsync<MdclException>(() => protocol.LoginAsync("code", "verifier", CancellationToken.None));
        Assert.Equal("AUTH_NO_XBOX_ACCOUNT", error.Error.ErrorCode);
    }

    [Fact]
    public async Task InvalidGrant_MapsToReauthentication()
    {
        var handler = new FakeHandler { InvalidGrant = true };
        var protocol = new MicrosoftAuthProtocol(new HttpClient(handler), Options);
        var error = await Assert.ThrowsAsync<MdclException>(() => protocol.RefreshAsync("expired", CancellationToken.None));
        Assert.Equal("AUTH_REFRESH_EXPIRED", error.Error.ErrorCode);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public bool RotateRefresh { get; init; }
        public bool EmptyEntitlements { get; init; }
        public long? XstsError { get; init; }
        public bool InvalidGrant { get; init; }
        public int TokenRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path == "/consumers/oauth2/v2.0/token" ? path : path);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (path.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
            {
                TokenRequests++;
                if (InvalidGrant)
                    return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant" });
                var refresh = RotateRefresh ? "refresh-2" : "refresh-1";
                return Json(HttpStatusCode.OK, new { access_token = "msa-access", refresh_token = refresh, expires_in = 3600 });
            }
            if (path == "/user/authenticate")
                return Json(HttpStatusCode.OK, new { Token = "xbox-token", DisplayClaims = new { xui = new[] { new { uhs = "user-hash" } } } });
            if (path == "/xsts/authorize")
            {
                if (XstsError is not null) return Json((HttpStatusCode)401, new { XErr = XstsError });
                return Json(HttpStatusCode.OK, new { Token = "xsts-token" });
            }
            if (path == "/authentication/login_with_xbox")
                return Json(HttpStatusCode.OK, new { access_token = "mc-access", expires_in = 86400 });
            if (path == "/entitlements/mcstore")
                return Json(HttpStatusCode.OK, new { items = EmptyEntitlements ? Array.Empty<object>() : new object[] { new { name = "game_minecraft" } } });
            if (path == "/minecraft/profile")
                return Json(HttpStatusCode.OK, new { id = "minecraft-uuid", name = "Player" });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }
}
