namespace MDCL.Core.Accounts;

public sealed record AccountRecord(AccountProfile Profile, string CredentialKey);

public sealed record AccountStoreDocument(IReadOnlyList<AccountRecord> Accounts);

public enum AuthStage
{
    Starting,
    BrowserOpened,
    MicrosoftTokenReceived,
    XboxAuthenticated,
    XstsAuthenticated,
    MinecraftTokenReceived,
    EntitlementsVerified,
    ProfileReceived,
    Completed
}

public sealed record AuthStageEvent(AuthStage Stage, string MessageCode, DateTimeOffset Timestamp);

public sealed record MicrosoftLoginResult(AuthSession Session, [property: System.Text.Json.Serialization.JsonIgnore] string RefreshToken)
{
    public override string ToString() => "MicrosoftLoginResult { credentials=<redacted> }";
}
