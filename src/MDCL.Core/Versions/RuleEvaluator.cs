namespace MDCL.Core.Versions;

public sealed record RuleContext(LauncherOperatingSystem OS, CpuArchitecture Architecture, IReadOnlyDictionary<string, bool> Features)
{
    public static RuleContext For(TargetPlatform target, IReadOnlyDictionary<string, bool>? features = null) =>
        new(target.OS, target.GameArchitecture, features ?? new Dictionary<string, bool>());

    public string OsName => OS == LauncherOperatingSystem.MacOS ? "osx" : "windows";
    public bool MatchesArch(string arch) => Architecture switch
    {
        CpuArchitecture.X64 => arch is "x86_64" or "x64" or "amd64",
        CpuArchitecture.Arm64 => arch is "arm64" or "aarch64",
        _ => false
    };
}

public static class RuleEvaluator
{
    public static bool IsAllowed(IReadOnlyList<VersionRule> rules, RuleContext context, bool defaultWhenEmpty = true)
    {
        if (rules.Count == 0) return defaultWhenEmpty;
        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule, context)) continue;
            allowed = rule.IsAllow;
        }
        return allowed;
    }

    public static bool Matches(VersionRule rule, RuleContext context)
    {
        if (rule.Os is not null)
        {
            if (rule.Os.TryGetValue("name", out var osName) && !string.Equals(osName, context.OsName, StringComparison.Ordinal)) return false;
            if (rule.Os.TryGetValue("arch", out var arch) && !context.MatchesArch(arch)) return false;
        }
        if (rule.Features is not null)
        {
            foreach (var feature in rule.Features)
            {
                if (!context.Features.TryGetValue(feature.Key, out var actual) || actual != feature.Value) return false;
            }
        }
        return true;
    }
}
