using System.Text.Json.Serialization;

namespace MDCL.Core.Accounts;

public readonly record struct AccountId(Guid Value)
{
    public static AccountId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public enum AccountKind { Local, Microsoft }

public sealed record AccountProfile(AccountId Id, string DisplayName, AccountKind Kind, string? MinecraftUuid = null);

public sealed class AuthSession
{
    public AuthSession(AccountProfile profile, string accessToken, DateTimeOffset expiresAt)
    {
        Profile = profile;
        AccessToken = accessToken;
        ExpiresAt = expiresAt;
    }

    public AccountProfile Profile { get; }
    [JsonIgnore]
    public string AccessToken { get; }
    public DateTimeOffset ExpiresAt { get; }
    public override string ToString() => $"{Profile.Kind} session for {Profile.DisplayName}; token=<redacted>";
}
