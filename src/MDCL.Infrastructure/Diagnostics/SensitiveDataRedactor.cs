using System.Net;
using System.Text.RegularExpressions;

namespace MDCL.Infrastructure.Diagnostics;

public sealed class SensitiveDataRedactor
{
    private static readonly Regex Authorization = new(@"(?i)(authorization\s*[:=]\s*bearer\s+)[^\s,;]+", RegexOptions.Compiled);
    private static readonly Regex QuerySecret = new(@"(?i)([?&](?:code|token|access_token|refresh_token|sig|signature)=)[^&\s]+", RegexOptions.Compiled);
    private static readonly Regex TokenLike = new(@"(?i)\b(?:eyJ[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,}|[A-Za-z0-9_-]{80,})\b", RegexOptions.Compiled);
    private static readonly Regex LabelledSecret = new("(?i)((?:[\"']?(?:access_?token|refresh_?token|code_verifier|client_secret|identityToken|xsts_?token)[\"']?)\\s*[:=]\\s*[\"']?|--(?:accessToken|clientToken)\\s+)[^\\s\"'&,;}]+", RegexOptions.Compiled);
    private static readonly Regex Cookie = new(@"(?im)((?:set-cookie|cookie)\s*[:=]\s*)[^\r\n]+", RegexOptions.Compiled);

    public string Redact(string input, IEnumerable<string>? knownSecrets = null)
    {
        var result = Authorization.Replace(input, "$1<redacted>");
        result = QuerySecret.Replace(result, "$1<redacted>");
        result = TokenLike.Replace(result, "<redacted>");
        result = LabelledSecret.Replace(result, "$1<redacted>");
        result = Cookie.Replace(result, "$1<redacted>");
        if (knownSecrets is not null)
        {
            foreach (var secret in knownSecrets.Where(x => !string.IsNullOrEmpty(x)))
                result = result.Replace(secret, "<redacted>", StringComparison.Ordinal);
        }
        return result;
    }

    public string RedactPath(string input, string userRoot)
    {
        if (string.IsNullOrWhiteSpace(userRoot)) return input;
        return input.Replace(userRoot, "<user>", StringComparison.OrdinalIgnoreCase);
    }

    public string EscapeForDiagnostics(string input) => WebUtility.HtmlEncode(Redact(input));
}
