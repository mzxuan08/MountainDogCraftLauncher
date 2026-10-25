using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Core.Java;
using MDCL.Core.Launch;
using MDCL.Core.Versions;

namespace MDCL.Application.Launch;

public sealed class LaunchPlanBuilder
{
    public LaunchPlan Build(
        InstanceManifest instance,
        ResolvedVersion version,
        JavaRuntime java,
        string sharedRoot,
        string instanceRoot,
        string nativesDirectory,
        TargetPlatform target)
    {
        if (java.Architecture != version.JavaRequirement.Architecture)
            throw new MdclException(MdclError.Of("NATIVE_ARCH_MISMATCH", "LaunchPrecheck",
                $"Java 架构 {java.Architecture} 与游戏要求 {version.JavaRequirement.Architecture} 不符。", false));
        if (java.MajorVersion < version.JavaRequirement.MinimumMajor)
            throw new MdclException(MdclError.Of("JAVA_NO_COMPATIBLE_RUNTIME", "LaunchPrecheck",
                $"Java {java.MajorVersion} 低于要求 {version.JavaRequirement.MinimumMajor}。", false));
        if (!File.Exists(java.Executable))
            throw new MdclException(MdclError.Of("JAVA_EXECUTABLE_MISSING", "LaunchPrecheck", "Java 可执行文件不存在。", false));

        var shared = Path.GetFullPath(sharedRoot);
        var instanceRootFull = Path.GetFullPath(instanceRoot);
        var workingDirectory = Path.GetFullPath(Path.Combine(instanceRootFull, instance.GameDirectory));
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(nativesDirectory);
        var classPath = version.Libraries
            .Where(x => x.ArtifactUri is not null)
            .Select(x => Path.Combine(shared, "libraries", x.ClassPathRelativePath))
            .Append(Path.Combine(shared, "versions", SafeComponent(instance.BaseVersion), "client.jar"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var missing = classPath.Where(x => !File.Exists(x)).ToArray();
        if (missing.Length > 0)
            throw new MdclException(MdclError.Of("LAUNCH_DEPENDENCY_MISSING", "LaunchPrecheck",
                "启动依赖缺失。", true, string.Join("; ", missing.Take(5))));

        var separator = target.OS == LauncherOperatingSystem.Windows ? ";" : ":";
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["natives_directory"] = Path.GetFullPath(nativesDirectory),
            ["classpath"] = string.Join(separator, classPath),
            ["classpath_separator"] = separator,
            ["library_directory"] = Path.Combine(shared, "libraries"),
            ["launcher_name"] = "MDCL",
            ["launcher_version"] = "0.1.0-alpha",
            ["game_directory"] = workingDirectory,
            ["assets_root"] = Path.Combine(shared, "assets"),
            ["assets_index_name"] = version.AssetIndex?.Id ?? "",
            ["version_name"] = version.VersionId,
            ["version_type"] = "release",
            ["user_properties"] = "{}"
        };
        var jvm = version.JvmArguments.Select(x => Replace(x, placeholders)).ToList();
        if (!jvm.Any(x => x.StartsWith("-Djava.library.path=", StringComparison.Ordinal)))
        {
            jvm.Insert(0, "-Djava.library.path=" + placeholders["natives_directory"]);
        }
        if (!jvm.Any(x => x == "-cp" || x == "-classpath" || x.StartsWith("-cp=", StringComparison.Ordinal)))
        {
            jvm.Insert(0, "-cp");
            jvm.Insert(1, placeholders["classpath"]);
        }
        if (!jvm.Any(x => x.StartsWith("-Xmx", StringComparison.Ordinal)))
            jvm.Insert(0, $"-Xmx{Math.Clamp(instance.MemoryMiB, 512, 131072)}M");
        if (version.Logging is { } logging)
        {
            var logPath = Path.Combine(shared, "logging", SafeComponent(logging.FileId));
            if (File.Exists(logPath))
            {
                var argument = logging.Argument.Replace("${path}", logPath, StringComparison.Ordinal);
                if (!jvm.Contains(argument, StringComparer.Ordinal)) jvm.Add(argument);
            }
        }
        jvm.AddRange(instance.ExtraJvmArgs);
        var game = version.GameArguments.Select(x => Replace(x, placeholders)).ToArray();
        var nativeJars = version.NativeLibraries
            .Where(x => x.Native?.Uri is not null)
            .Select(x => Path.Combine(shared, "libraries", x.Native!.RelativePath))
            .ToArray();
        if (version.JavaRequirement.Architecture != target.GameArchitecture)
            throw new MdclException(MdclError.Of("NATIVE_ARCH_MISMATCH", "LaunchPrecheck",
                "目标游戏架构与解析要求不一致。", false));

        return new LaunchPlan(instance.InstanceId, Path.GetFullPath(java.Executable), workingDirectory, jvm,
            version.MainClass, game, classPath, Path.GetFullPath(nativesDirectory),
            version.Libraries.Select(x => x.Sha1).Where(x => x is not null).Cast<string>().ToArray(),
            [$"java={java.MajorVersion}/{java.Architecture}", $"classpath={classPath.Length}", $"dependencies-missing=0"],
            java.MajorVersion, nativeJars, Path.Combine(shared, "assets"), version.AssetIndex?.Id ?? "",
            "release", separator);
    }

    private static string Replace(string value, IReadOnlyDictionary<string, string> placeholders)
    {
        var result = value;
        foreach (var pair in placeholders) result = result.Replace("${" + pair.Key + "}", pair.Value, StringComparison.Ordinal);
        return result;
    }

    private static string SafeComponent(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        return new string(chars).Trim('.');
    }
}
