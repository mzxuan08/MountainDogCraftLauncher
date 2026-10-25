using System.Text;
using MDCL.Application.Launch;
using MDCL.Core;
using MDCL.Core.Accounts;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Core.Java;
using MDCL.Core.Versions;
using Xunit;

namespace MDCL.Integration.Tests.Launch;

public sealed class LaunchPlanBuilderTests
{
    [Fact]
    public void BuildsClasspathMemoryAndLeavesAuthForExecutionBoundary()
    {
        using var temp = new TestDirectory();
        var shared = Path.Combine(temp.Path, "shared");
        var instance = Path.Combine(temp.Path, "instance");
        var javaPath = Path.Combine(temp.Path, "java.exe");
        Directory.CreateDirectory(Path.Combine(shared, "libraries", "g", "a", "1"));
        Directory.CreateDirectory(Path.Combine(shared, "versions", "1.20.1"));
        Directory.CreateDirectory(Path.Combine(shared, "logging"));
        Directory.CreateDirectory(instance);
        File.WriteAllText(Path.Combine(javaPath), "");
        File.WriteAllText(Path.Combine(shared, "libraries", "g", "a", "1", "a.jar"), "jar");
        File.WriteAllText(Path.Combine(shared, "versions", "1.20.1", "client.jar"), "client");
        File.WriteAllText(Path.Combine(shared, "logging", "client.xml"), "log");
        var manifest = new InstanceManifest
        {
            InstanceId = InstanceId.New(), DisplayName = "test", BaseVersion = "1.20.1",
            GameDirectory = ".minecraft", MemoryMiB = 2048, CreatedAt = DateTimeOffset.UtcNow
        };
        var resolved = new ResolvedVersion("1.20.1", "example.Main",
            ["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"],
            ["--username", "${auth_player_name}", "--version", "${version_name}"],
            [new ResolvedLibrary("g:a:1", "g/a/1/a.jar", new Uri("https://example.invalid/a"), new string('a', 40), 1, null, [])],
            [],
            new ResolvedAssetIndex("5", null, null, null, null),
            new ResolvedLogging("-Dlog4j.configurationFile=${path}", "client.xml", new Uri("https://example.invalid/log"), null, null, "log4j2-xml"),
            new ClientDownload(new Uri("https://example.invalid/client"), null, null),
            new JavaRequirement(17, 17, null, CpuArchitecture.X64, [], []),
            [], "hash");
        var java = new JavaRuntime("j17", Path.GetDirectoryName(javaPath)!, javaPath, 17, "test",
            CpuArchitecture.X64, "ok", DateTimeOffset.UtcNow);
        var plan = new LaunchPlanBuilder().Build(manifest, resolved, java, shared, instance,
            Path.Combine(temp.Path, "natives"), new TargetPlatform(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64));
        Assert.Contains("-Xmx2048M", plan.JvmArguments);
        Assert.Contains("g/a/1/a.jar", plan.ClassPath[0], StringComparison.Ordinal);
        Assert.Contains(plan.ClassPath, x => x.EndsWith("client.jar", StringComparison.Ordinal));
        Assert.Contains("${auth_player_name}", plan.GameArguments);
        Assert.Equal(".minecraft", Path.GetFileName(plan.WorkingDirectory));
        Assert.Contains(plan.JvmArguments, x => x.StartsWith("-Dlog4j.configurationFile=", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDependency_ReturnsPrecheckError()
    {
        using var temp = new TestDirectory();
        var manifest = new InstanceManifest
        {
            InstanceId = InstanceId.New(), DisplayName = "test", BaseVersion = "1.20.1",
            GameDirectory = ".minecraft", CreatedAt = DateTimeOffset.UtcNow
        };
        var resolved = new ResolvedVersion("1.20.1", "example.Main", [], [], [], [], null, null, null,
            new JavaRequirement(17, 17, null, CpuArchitecture.X64, [], []), [], "hash");
        var javaPath = Path.Combine(temp.Path, "java.exe");
        File.WriteAllText(javaPath, "");
        var java = new JavaRuntime("j17", temp.Path, javaPath, 17, "test",
            CpuArchitecture.X64, "ok", DateTimeOffset.UtcNow);
        var error = Assert.Throws<MdclException>(() => new LaunchPlanBuilder().Build(manifest, resolved, java,
            Path.Combine(temp.Path, "shared"), Path.Combine(temp.Path, "instance"), Path.Combine(temp.Path, "natives"),
            new TargetPlatform(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64)));
        Assert.Equal("LAUNCH_DEPENDENCY_MISSING", error.Error.ErrorCode);
    }
}
