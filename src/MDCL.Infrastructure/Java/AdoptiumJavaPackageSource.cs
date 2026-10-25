using System.Text.Json;
using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Java;

namespace MDCL.Infrastructure.Java;

public sealed class AdoptiumJavaPackageSource : IJavaRuntimePackageSource
{
    private readonly HttpClient _http;
    public AdoptiumJavaPackageSource(HttpClient http) => _http = http;

    public async Task<JavaPackageDownload> GetPackageAsync(JavaRequirement requirement, TargetPlatform target, CancellationToken ct)
    {
        var os = target.OS == LauncherOperatingSystem.Windows ? "windows" : "mac";
        var arch = target.GameArchitecture == CpuArchitecture.X64 ? "x64" : "aarch64";
        var uri = new Uri($"https://api.adoptium.net/v3/assets/latest/{requirement.PreferredMajor}/hotspot?architecture={arch}&image_type=jdk&os={os}&vendor=eclipse");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new MdclException(MdclError.Of("JAVA_PACKAGE_HTTP", "JavaInstall", $"Java 元数据返回 HTTP {(int)response.StatusCode}。", true));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            throw new MdclException(MdclError.Of("JAVA_PACKAGE_NOT_FOUND", "JavaInstall", "没有找到匹配的 Temurin JDK 包。", true));
        var item = document.RootElement[0];
        var version = item.GetProperty("version").GetProperty("semver").GetString() ?? requirement.PreferredMajor.ToString();
        var package = item.GetProperty("binary").GetProperty("package");
        var link = package.GetProperty("link").GetString();
        var checksum = package.GetProperty("checksum").GetString();
        var size = package.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : (long?)null;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var packageUri) || packageUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(checksum) || checksum.Length != 64)
            throw new MdclException(MdclError.Of("JAVA_PACKAGE_INVALID", "JavaInstall", "Temurin 包缺少 HTTPS 地址或 SHA-256。", false));
        var runtimeId = $"temurin-{version}-{arch}".Replace('+', '-');
        return new JavaPackageDownload(runtimeId, packageUri, checksum.ToLowerInvariant(), size);
    }
}
