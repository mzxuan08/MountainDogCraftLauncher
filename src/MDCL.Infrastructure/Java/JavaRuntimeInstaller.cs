using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Installation;
using MDCL.Core.Java;
using MDCL.Infrastructure.Archives;
using MDCL.Infrastructure.Downloads;

namespace MDCL.Infrastructure.Java;

public sealed class JavaRuntimeInstaller : IJavaRuntimeInstaller
{
    private readonly IJavaRuntimePackageSource _source;
    private readonly IArtifactDownloader _downloader;
    private readonly IJavaProbe _probe;
    private readonly SafeArchiveExtractor _extractor = new();
    private readonly string _runtimesRoot;
    private readonly string _stagingRoot;

    public JavaRuntimeInstaller(
        IJavaRuntimePackageSource source, IArtifactDownloader downloader, IJavaProbe probe,
        string runtimesRoot, string stagingRoot)
    {
        _source = source;
        _downloader = downloader;
        _probe = probe;
        _runtimesRoot = Path.GetFullPath(runtimesRoot);
        _stagingRoot = Path.GetFullPath(stagingRoot);
    }

    public async Task<JavaRuntime> InstallAsync(
        JavaRequirement requirement, TargetPlatform target,
        IProgress<OperationEvent>? progress, CancellationToken ct)
    {
        var package = await _source.GetPackageAsync(requirement, target, ct);
        var destination = Path.Combine(_runtimesRoot, $"{target.OS.ToString().ToLowerInvariant()}-{target.GameArchitecture.ToString().ToLowerInvariant()}", SafeName(package.RuntimeId));
        if (Directory.Exists(destination))
        {
            var existingExecutable = FindExecutable(destination);
            if (existingExecutable is not null && (await _probe.ProbeAsync(existingExecutable, ct)).Runtime is { } existing && Compatible(existing, requirement))
                return existing with { RuntimeId = "managed:" + package.RuntimeId, IsManaged = true };
            Directory.CreateDirectory(_stagingRoot);
            Directory.Move(destination, Path.Combine(_stagingRoot, "java-quarantine-" + Guid.NewGuid().ToString("N")));
        }
        var operationId = "java-" + Guid.NewGuid().ToString("N");
        var staging = Path.Combine(_stagingRoot, operationId);
        Directory.CreateDirectory(staging);
        try
        {
            var artifact = new ArtifactSpec("java-package:" + package.RuntimeId, [package.Uri], package.Size, package.Sha256,
                "SHA256", "adoptium-api", "package.zip", ArtifactTrustLevel.PublicDownload);
            var downloadProgress = new CallbackProgress<TransferEvent>(x =>
                progress?.Report(new OperationEvent(operationId, "download-java", 1, OperationState.Running,
                    x.DoneBytes, x.TotalBytes, x.Phase, DateTimeOffset.UtcNow)));
            await _downloader.EnsureAsync(artifact, staging, downloadProgress, ct);
            var archive = Path.Combine(staging, "package.zip");
            var extracted = Path.Combine(staging, "extracted");
            await _extractor.ExtractAsync(archive, extracted, new ArchiveExtractionPolicy(RestoreUnixExecutable: true), ct);
            var executable = FindExecutable(extracted);
            if (executable is null)
                throw new MdclException(MdclError.Of("JAVA_EXECUTABLE_MISSING", "JavaInstall", "JDK 归档中找不到 java 可执行文件。", false));
            var firstProbe = await _probe.ProbeAsync(executable, ct);
            if (firstProbe.Runtime is null || firstProbe.Runtime.MajorVersion != requirement.PreferredMajor)
                throw new MdclException(MdclError.Of("JAVA_INSTALL_PROBE_FAILED", "JavaInstall",
                    $"安装包探测失败：{firstProbe.Status} {firstProbe.Error}".Trim(), false));
            if (firstProbe.Runtime.Architecture != requirement.Architecture)
                throw new MdclException(MdclError.Of("JAVA_ARCH_MISMATCH", "JavaInstall",
                    $"安装包架构 {firstProbe.Runtime.Architecture} 与需要 {requirement.Architecture} 不符。", false));

            var home = firstProbe.Runtime.Home;
            var homeRelative = Path.GetRelativePath(extracted, Path.GetFullPath(home));
            if (Path.IsPathRooted(homeRelative) || homeRelative == ".." || homeRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !Compatible(firstProbe.Runtime, requirement))
                throw new MdclException(MdclError.Of("JAVA_INSTALL_PROBE_FAILED", "JavaInstall", "Java 根目录、架构或模块探测不符合要求。", false));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination))
                throw new MdclException(MdclError.Of("JAVA_RUNTIME_EXISTS", "JavaInstall", "受管 Java 目标已存在。", false, destination));
            Directory.Move(home, destination);
            var finalExecutable = Path.Combine(destination, Path.GetRelativePath(home, executable));
            JavaProbeResult finalProbe;
            try
            {
                finalProbe = await _probe.ProbeAsync(finalExecutable, ct);
                if (finalProbe.Runtime is null || !Compatible(finalProbe.Runtime, requirement))
                    throw new MdclException(MdclError.Of("JAVA_INSTALL_PROBE_FAILED", "JavaInstall", "移动后的 Java 探测失败。", false));
            }
            catch
            {
                // Only this operation's newly published runtime is moved back;
                // the staging finally block can safely discard it and retry.
                Directory.Move(destination, Path.Combine(staging, "failed-runtime"));
                throw;
            }
            progress?.Report(new OperationEvent(operationId, "java-ready", 2, OperationState.Completed,
                null, null, "java-ready", DateTimeOffset.UtcNow));
            return finalProbe.Runtime with
            {
                RuntimeId = "managed:" + package.RuntimeId,
                IsManaged = true
            };
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool Compatible(JavaRuntime runtime, JavaRequirement requirement) =>
        JavaSelectionPolicy.Select(requirement, new MDCL.Core.Instances.JavaPolicy(null, true, requirement.Architecture), [runtime]).Selected is not null;

    private static string? FindExecutable(string root)
    {
        var name = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var direct = Path.Combine(root, "bin", name);
        if (File.Exists(direct)) return direct;
        var mac = Path.Combine(root, "Contents", "Home", "bin", name);
        if (File.Exists(mac)) return mac;
        foreach (var file in Directory.EnumerateFiles(root, name, SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "bin", StringComparison.Ordinal)) return file;
        }
        return null;
    }

    private static string SafeName(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        return new string(chars).Trim('.');
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
