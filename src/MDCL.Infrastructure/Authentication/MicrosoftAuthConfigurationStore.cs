using System.Text.Json;
using System.Text.Json.Serialization;
using MDCL.Application.Ports;
using MDCL.Core.Errors;

namespace MDCL.Infrastructure.Authentication;

/// <summary>Public application coordinates only. Tokens and client secrets never belong here.</summary>
public sealed class MicrosoftAuthConfigurationStore
{
    public const string FileName = "microsoft-auth.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public MicrosoftAuthConfigurationStore(string configRoot, string? clientIdOverride = null, string? bundledClientId = null)
    {
        ConfigurationPath = Path.Combine(Path.GetFullPath(configRoot), FileName);
        try
        {
            if (File.Exists(ConfigurationPath))
            {
                if (new FileInfo(ConfigurationPath).Length > 8192) throw new JsonException();
                var document = JsonSerializer.Deserialize<ConfigurationDocument>(File.ReadAllText(ConfigurationPath), JsonOptions)
                    ?? throw new JsonException();
                if (document.SchemaVersion != 1) throw new JsonException();
                SavedClientId = NormalizeClientId(document.ClientId);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or MdclException)
        {
            LoadIssue = "微软登录配置无法读取或格式无效；可重新保存 Client ID 修复，本地资料仍可管理。";
        }

        var bundled = NormalizeClientId(bundledClientId);
        var active = string.IsNullOrEmpty(SavedClientId) ? bundled : SavedClientId;
        Source = !string.IsNullOrEmpty(SavedClientId) ? "本地配置" : !string.IsNullOrEmpty(bundled) ? "MDCL 内置应用" : "未配置";
        if (!string.IsNullOrWhiteSpace(clientIdOverride))
        {
            try { active = NormalizeClientId(clientIdOverride); Source = "环境变量（优先）"; }
            catch (MdclException)
            {
                active = "";
                Source = "环境变量无效";
                LoadIssue = "MDCL_MICROSOFT_CLIENT_ID 不是有效的应用 Client ID；请修正或移除该环境变量后重启。";
            }
        }
        CurrentOptions = new MicrosoftAuthOptions(active);
    }

    public string ConfigurationPath { get; }
    public string SavedClientId { get; private set; } = "";
    public MicrosoftAuthOptions CurrentOptions { get; }
    public string Source { get; }
    public string? LoadIssue { get; }
    public bool HasEnvironmentOverride => Source.StartsWith("环境变量", StringComparison.Ordinal);

    public static bool IsValidClientId(string? value) =>
        string.IsNullOrWhiteSpace(value) || Guid.TryParse(value.Trim(), out var id) && id != Guid.Empty;

    public static string NormalizeClientId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!Guid.TryParse(value.Trim(), out var id) || id == Guid.Empty)
            throw new MdclException(MdclError.Of("AUTH_CLIENT_ID_INVALID", "Authentication",
                "Client ID 应为应用概述页的 GUID，不是 Client Secret、租户 ID 或登录令牌。", false));
        return id.ToString("D");
    }

    public async Task SaveAsync(string? clientId, CancellationToken ct)
    {
        var normalized = NormalizeClientId(clientId);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
        var temporary = ConfigurationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new ConfigurationDocument { ClientId = normalized }, JsonOptions, ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, ConfigurationPath, true);
            SavedClientId = normalized;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record ConfigurationDocument
    {
        public int SchemaVersion { get; init; } = 1;
        public string ClientId { get; init; } = "";
    }
}
