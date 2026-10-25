using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MDCL.Application.Ports;
using MDCL.Core.Accounts;
using MDCL.Core.Errors;

namespace MDCL.Infrastructure.Authentication;

public sealed class MicrosoftAuthProtocol
{
    private readonly HttpClient _http;
    private readonly MicrosoftAuthOptions _options;

    public MicrosoftAuthProtocol(HttpClient http, MicrosoftAuthOptions options)
    {
        _http = http;
        _options = options;
    }

    public static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    public Uri BuildAuthorizeUri(string state, string challenge) => new(
        $"{_options.Authority}/authorize?client_id={Uri.EscapeDataString(_options.ClientId)}" +
        $"&response_type=code&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}" +
        $"&response_mode=query&scope={Uri.EscapeDataString(_options.Scopes)}" +
        $"&state={Uri.EscapeDataString(state)}&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256");

    public async Task<MicrosoftLoginResult> LoginAsync(string code, string verifier, CancellationToken ct)
    {
        var tokens = await ExchangeCodeAsync(code, verifier, ct);
        return await CompleteMinecraftLoginAsync(tokens, ct);
    }

    public async Task<MicrosoftLoginResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new MdclException(MdclError.Of("AUTH_REQUIRED", "Authentication", "没有可用的刷新令牌。", false));
        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = _options.Scopes
        }, refreshToken, ct);
        return await CompleteMinecraftLoginAsync(tokens, ct);
    }

    private async Task<OAuthTokens> ExchangeCodeAsync(string code, string verifier, CancellationToken ct) =>
        await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["code_verifier"] = verifier,
            ["scope"] = _options.Scopes
        }, null, ct);

    private async Task<OAuthTokens> PostTokenAsync(Dictionary<string, string> fields, string? previousRefresh, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await _http.PostAsync($"{_options.Authority}/token", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw MapOAuthError((int)response.StatusCode, body);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (!root.TryGetProperty("access_token", out var access) || string.IsNullOrWhiteSpace(access.GetString()))
            throw new MdclException(MdclError.Of("AUTH_TOKEN_INVALID", "Authentication", "Microsoft 响应缺少访问令牌。", false));
        var refresh = root.TryGetProperty("refresh_token", out var refreshNode) ? refreshNode.GetString() : previousRefresh;
        var seconds = 3600;
        if (root.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt32(out var parsedSeconds)) seconds = parsedSeconds;
        return new OAuthTokens(access.GetString()!, refresh, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    private async Task<MicrosoftLoginResult> CompleteMinecraftLoginAsync(OAuthTokens tokens, CancellationToken ct)
    {
        var xbox = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + tokens.AccessToken },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        }, ct);
        var xboxToken = xbox.GetProperty("Token").GetString()!;
        var uhs = xbox.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;

        using var xstsRequest = new HttpRequestMessage(HttpMethod.Post, "https://xsts.auth.xboxlive.com/xsts/authorize")
        {
            Content = JsonContent(new
            {
                Properties = new { UserTokens = new[] { xboxToken }, SiteName = "user.auth.xboxlive.com" },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT"
            })
        };
        using var xstsResponse = await _http.SendAsync(xstsRequest, ct);
        var xstsBody = await xstsResponse.Content.ReadAsStringAsync(ct);
        if (!xstsResponse.IsSuccessStatusCode) throw MapXstsError((int)xstsResponse.StatusCode, xstsBody);
        using var xstsJson = JsonDocument.Parse(xstsBody);
        var xstsToken = xstsJson.RootElement.GetProperty("Token").GetString()!;

        using var mcLogin = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/authentication/login_with_xbox")
        {
            Content = JsonContent(new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" })
        };
        using var mcLoginResponse = await _http.SendAsync(mcLogin, ct);
        var mcLoginBody = await mcLoginResponse.Content.ReadAsStringAsync(ct);
        if (!mcLoginResponse.IsSuccessStatusCode)
            throw new MdclException(MdclError.Of("AUTH_MINECRAFT_TOKEN_FAILED", "Authentication", "Minecraft 会话令牌获取失败。", false, $"HTTP {(int)mcLoginResponse.StatusCode}"));
        using var mcLoginJson = JsonDocument.Parse(mcLoginBody);
        var mcAccess = mcLoginJson.RootElement.GetProperty("access_token").GetString()!;
        var mcExpires = mcLoginJson.RootElement.TryGetProperty("expires_in", out var mcExp) && mcExp.TryGetInt32(out var sec) ? sec : 86400;

        using var entitlementRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/entitlements/mcstore");
        entitlementRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcAccess);
        using var entitlementResponse = await _http.SendAsync(entitlementRequest, ct);
        if (entitlementResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new MdclException(MdclError.Of("AUTH_ENTITLEMENT_MISSING", "Authentication", "Minecraft 会话无权读取权益。", false));
        if (!entitlementResponse.IsSuccessStatusCode)
            throw new MdclException(MdclError.Of("AUTH_ENTITLEMENT_FAILED", "Authentication", "读取 Minecraft 权益失败。", true));
        using var entitlementJson = JsonDocument.Parse(await entitlementResponse.Content.ReadAsStringAsync(ct));
        if (!entitlementJson.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
            throw new MdclException(MdclError.Of("AUTH_ENTITLEMENT_MISSING", "Authentication", "账号没有可用的 Minecraft Java 版权益。", false));

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profileRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcAccess);
        using var profileResponse = await _http.SendAsync(profileRequest, ct);
        if (profileResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new MdclException(MdclError.Of("AUTH_PROFILE_MISSING", "Authentication", "账号尚未创建 Minecraft profile。", false));
        if (!profileResponse.IsSuccessStatusCode)
            throw new MdclException(MdclError.Of("AUTH_PROFILE_FAILED", "Authentication", "读取 Minecraft profile 失败。", true));
        using var profileJson = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync(ct));
        var name = profileJson.RootElement.GetProperty("name").GetString()!;
        var uuid = profileJson.RootElement.GetProperty("id").GetString()!;
        var profile = new AccountProfile(AccountId.New(), name, AccountKind.Microsoft, uuid);
        var session = new AuthSession(profile, mcAccess, DateTimeOffset.UtcNow.AddSeconds(mcExpires));
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new MdclException(MdclError.Of("AUTH_REFRESH_MISSING", "Authentication", "Microsoft 响应缺少刷新令牌。", false));
        return new MicrosoftLoginResult(session, tokens.RefreshToken);
    }

    private async Task<JsonElement> PostJsonAsync(string uri, object payload, CancellationToken ct)
    {
        using var response = await _http.PostAsync(uri, JsonContent(payload), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new MdclException(MdclError.Of("AUTH_XBOX_FAILED", "Authentication", "Xbox Live 认证失败。", false, $"HTTP {(int)response.StatusCode}"));
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static MdclException MapOAuthError(int status, string body)
    {
        var code = "AUTH_OAUTH_FAILED";
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                var value = error.GetString();
                if (value == "invalid_grant") code = "AUTH_REFRESH_EXPIRED";
                else if (value == "access_denied") code = "AUTH_CANCELED";
            }
        }
        catch (JsonException) { }
        return new MdclException(MdclError.Of(code, "Authentication", $"Microsoft OAuth 失败 HTTP {status}。", status >= 500));
    }

    private static MdclException MapXstsError(int status, string body)
    {
        var xerr = 0L;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("XErr", out var value)) xerr = value.GetInt64();
        }
        catch (JsonException) { }
        var code = xerr switch
        {
            2148916233 => "AUTH_NO_XBOX_ACCOUNT",
            2148916235 => "AUTH_REGION_BLOCKED",
            2148916238 => "AUTH_FAMILY_RESTRICTED",
            _ => "AUTH_XSTS_FAILED"
        };
        return new MdclException(MdclError.Of(code, "Authentication", $"Xbox XSTS 拒绝，XErr={xerr}，HTTP {status}。", false));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);
}
