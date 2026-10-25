using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Java;

namespace MDCL.Infrastructure.Java;

public sealed class ProcessJavaProbe : IJavaProbe
{
    private readonly TimeSpan _timeout;
    public ProcessJavaProbe(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(5);

    public async Task<JavaProbeResult> ProbeAsync(string executablePath, CancellationToken ct)
    {
        if (!File.Exists(executablePath)) return new JavaProbeResult(null, "missing", "可执行文件不存在");
        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        start.ArgumentList.Add("-XshowSettings:properties");
        start.ArgumentList.Add("-version");
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new JavaProbeResult(null, "start-failed", "进程未启动");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return new JavaProbeResult(null, "start-failed", ex.Message);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = (await stdout) + "\n" + (await stderr);
            if (process.ExitCode != 0)
                return new JavaProbeResult(null, $"exit-{process.ExitCode}", Trim(output));
            var properties = ParseProperties(output);
            if (!properties.TryGetValue("java.version", out var javaVersion) || !TryMajor(javaVersion, out var major))
                return new JavaProbeResult(null, "version-unknown", Trim(output));
            var arch = properties.GetValueOrDefault("os.arch", "");
            var architecture = arch.ToLowerInvariant() switch
            {
                "amd64" or "x86_64" or "x64" => CpuArchitecture.X64,
                "aarch64" or "arm64" => CpuArchitecture.Arm64,
                _ => (CpuArchitecture?)null
            };
            if (architecture is null) return new JavaProbeResult(null, "arch-unknown", arch);
            var home = properties.GetValueOrDefault("java.home", Path.GetDirectoryName(Path.GetDirectoryName(executablePath)!)!);
            var vendor = properties.GetValueOrDefault("java.vendor", "unknown");
            var id = "external:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executablePath)))).ToLowerInvariant()[..16];
            return new JavaProbeResult(new JavaRuntime(id, home, Path.GetFullPath(executablePath), major, vendor,
                architecture.Value, "ok", DateTimeOffset.UtcNow, false), "ok", null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            return new JavaProbeResult(null, "timeout", $"探测超过 {_timeout.TotalSeconds:0} 秒");
        }
        catch (OperationCanceledException) { throw; }
    }

    private static Dictionary<string, string> ParseProperties(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            var equals = trimmed.IndexOf(" = ", StringComparison.Ordinal);
            if (equals <= 0) continue;
            var key = trimmed[..equals].Trim();
            if (key.Contains(' ')) continue;
            result[key] = trimmed[(equals + 3)..].Trim();
        }
        return result;
    }

    public static bool TryMajor(string version, out int major)
    {
        major = 0;
        if (version.StartsWith("1.", StringComparison.Ordinal))
        {
            var rest = version[2..];
            var dot = rest.IndexOf('.');
            return int.TryParse(dot < 0 ? rest : rest[..dot], out major);
        }
        var first = version.TakeWhile(char.IsDigit).ToArray();
        return first.Length > 0 && int.TryParse(first, out major);
    }

    private static string Trim(string value) => value.Length <= 500 ? value.Trim() : value[..500].Trim();
}
