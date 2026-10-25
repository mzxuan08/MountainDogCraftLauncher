using System.Diagnostics;
using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Core.Instances;

namespace MDCL.Infrastructure.Installers;

public sealed record ForgeInstallRequest(LoaderKind Kind, string GameVersion, string LoaderVersion);
public sealed record InstallerProcessResult(int ExitCode, string StandardOutput, string StandardError);
public sealed record ForgeInstallResult(LoaderKind Kind, string LoaderVersion, string InstallerPath, string StagingDirectory,
    InstallerProcessResult Process);

public interface IInstallerProcessRunner
{
    Task<InstallerProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken ct);
}

public sealed class ProcessInstallerRunner : IInstallerProcessRunner
{
    public async Task<InstallerProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new InvalidOperationException("Installer did not start."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new MdclException(MdclError.Of("INSTALLER_START_FAILED", "Forge", "安装器进程启动失败。", false), ex);
        }
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return new InstallerProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new MdclException(MdclError.Of("INSTALLER_TIMEOUT", "Forge", "加载器安装器执行超时。", true));
        }
    }
}

public sealed class ForgeInstallerService
{
    private readonly HttpClient _http;
    private readonly IArtifactDownloader _downloader;
    private readonly IInstallerProcessRunner _runner;
    private readonly IAppPaths? _paths;

    public ForgeInstallerService(HttpClient http, IArtifactDownloader downloader, IInstallerProcessRunner runner, IAppPaths? paths = null)
    {
        _http = http;
        _downloader = downloader;
        _runner = runner;
        _paths = paths;
    }

    public async Task<ForgeInstallResult> InstallAsync(
        ForgeInstallRequest request, string javaExecutable, string stagingRoot,
        IProgress<MDCL.Core.Installation.TransferEvent>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.LoaderVersion))
            throw new MdclException(MdclError.Of("LOADER_VERSION_REQUIRED", "Forge", "Forge/NeoForge 必须固定 loader 版本。", false));
        var (installerUri, key) = request.Kind switch
        {
            LoaderKind.Forge => (new Uri($"https://maven.minecraftforge.net/net/minecraftforge/forge/{request.GameVersion}-{request.LoaderVersion}/forge-{request.GameVersion}-{request.LoaderVersion}-installer.jar"),
                $"forge-{request.GameVersion}-{request.LoaderVersion}"),
            LoaderKind.NeoForge => (new Uri($"https://maven.neoforged.net/releases/net/neoforged/neoforge/{request.LoaderVersion}/neoforge-{request.LoaderVersion}-installer.jar"),
                $"neoforge-{request.LoaderVersion}"),
            _ => throw new MdclException(MdclError.Of("LOADER_KIND_INVALID", "Forge", "不是 Forge/NeoForge 请求。", false))
        };
        using var shaResponse = await _http.GetAsync(installerUri.AbsoluteUri + ".sha1", ct);
        if (!shaResponse.IsSuccessStatusCode)
            throw new MdclException(MdclError.Of("INSTALLER_HASH_UNAVAILABLE", "Forge", "无法读取安装器 SHA-1。", true));
        var sha1 = (await shaResponse.Content.ReadAsStringAsync(ct)).Trim().Split(' ')[0];
        if (sha1.Length != 40 || sha1.Any(c => !Uri.IsHexDigit(c)))
            throw new MdclException(MdclError.Of("INSTALLER_HASH_INVALID", "Forge", "安装器 SHA-1 格式无效。", false));
        var stage = Path.Combine(Path.GetFullPath(stagingRoot), "loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        SeedLauncherLayout(stage, request.GameVersion);
        var artifact = new MDCL.Core.Installation.ArtifactSpec(key, [installerUri], null, sha1, "SHA1",
            request.Kind + "-maven-sha1", "installer.jar", MDCL.Core.Installation.ArtifactTrustLevel.TrustedMetadata);
        var receipt = await _downloader.EnsureAsync(artifact, stage, progress, ct);
        var result = await _runner.RunAsync(javaExecutable,
            ["-jar", receipt.Path, "--installClient", stage], stage, TimeSpan.FromMinutes(10), ct);
        if (result.ExitCode != 0)
            throw new MdclException(MdclError.Of("INSTALLER_FAILED", "Forge",
                $"加载器安装器失败，退出码 {result.ExitCode}。", false, Tail(result.StandardError)));
        return new ForgeInstallResult(request.Kind, request.LoaderVersion, receipt.Path, stage, result);
    }

    private void SeedLauncherLayout(string stage, string gameVersion)
    {
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "launcher_profiles.json"),
            """{"profiles":{},"settings":{"language":"zh_CN"},"version":3}""");
        var versionDir = Path.Combine(stage, "versions", gameVersion);
        Directory.CreateDirectory(versionDir);
        var versionJson = Path.Combine(versionDir, gameVersion + ".json");
        if (!File.Exists(versionJson))
            File.WriteAllText(versionJson, $$"""
                {"id":"{{gameVersion}}","type":"release","mainClass":"net.minecraft.client.main.Main","assets":"5","libraries":[]}
                """);
        var clientJar = _paths is null ? null : Path.Combine(_paths.SharedRoot, "versions", gameVersion, "client.jar");
        var targetJar = Path.Combine(versionDir, gameVersion + ".jar");
        if (clientJar is not null && File.Exists(clientJar) && !File.Exists(targetJar)) File.Copy(clientJar, targetJar);
    }

    private static string Tail(string value) => value.Length <= 1000 ? value.Trim() : value[^1000..].Trim();
}
