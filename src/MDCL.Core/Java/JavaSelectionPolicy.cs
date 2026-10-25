using MDCL.Core.Errors;
using MDCL.Core.Instances;

namespace MDCL.Core.Java;

public static class JavaRequirementFactory
{
    public static JavaRequirement Create(
        int preferredMajor,
        CpuArchitecture architecture,
        IEnumerable<JavaConstraint> constraints,
        IEnumerable<string>? requiredModules = null)
    {
        var all = constraints.ToArray();
        var minimum = all.Where(x => x.MinimumMajor.HasValue).Select(x => x.MinimumMajor!.Value).DefaultIfEmpty(0).Max();
        var maximum = all.Where(x => x.MaximumMajor.HasValue).Select(x => x.MaximumMajor!.Value).DefaultIfEmpty(0).Min();
        var hasMaximum = all.Any(x => x.MaximumMajor.HasValue);
        if (hasMaximum && minimum > maximum)
            throw new MdclException(MdclError.Of("JAVA_CONSTRAINT_CONFLICT", "JavaSelection",
                $"Java 约束无交集：最低 {minimum}，最高 {maximum}。", false,
                string.Join("; ", all.Select(x => $"{x.Source}:{x.MinimumMajor}-{x.MaximumMajor}"))));
        if (preferredMajor < minimum || (hasMaximum && preferredMajor > maximum))
            throw new MdclException(MdclError.Of("JAVA_CONSTRAINT_CONFLICT", "JavaSelection",
                $"首选 Java {preferredMajor} 不在约束交集 {minimum}-{(hasMaximum ? maximum : "∞")} 内。", false));
        return new JavaRequirement(preferredMajor, minimum, hasMaximum ? maximum : null, architecture,
            (requiredModules ?? []).Distinct(StringComparer.Ordinal).ToArray(), all);
    }
}

public static class JavaSelectionPolicy
{
    public static JavaSelection Select(JavaRequirement requirement, JavaPolicy policy, IReadOnlyList<JavaRuntime> candidates)
    {
        var rejections = new List<JavaCandidateRejection>();
        var valid = new List<JavaRuntime>();
        foreach (var candidate in candidates)
        {
            var reason = Validate(candidate, requirement);
            if (reason is null) valid.Add(candidate);
            else rejections.Add(new JavaCandidateRejection(candidate, reason));
        }

        if (!string.IsNullOrWhiteSpace(policy.BoundRuntimeId))
        {
            var bound = candidates.FirstOrDefault(x => string.Equals(x.RuntimeId, policy.BoundRuntimeId, StringComparison.Ordinal));
            if (bound is null)
                return new JavaSelection(null, rejections, MdclError.Of("JAVA_BOUND_RUNTIME_MISSING", "JavaSelection",
                    $"实例绑定的 Java {policy.BoundRuntimeId} 不存在。", false, policy.BoundRuntimeId));
            var boundReason = Validate(bound, requirement);
            if (boundReason is not null)
                return new JavaSelection(null, rejections, MdclError.Of("JAVA_BOUND_RUNTIME_INVALID", "JavaSelection",
                    $"实例绑定的 Java 不兼容：{boundReason}", false, policy.BoundRuntimeId));
            return new JavaSelection(bound, rejections, null);
        }

        if (valid.Count == 0)
            return new JavaSelection(null, rejections, MdclError.Of("JAVA_NO_COMPATIBLE_RUNTIME", "JavaSelection",
                "没有满足版本要求、架构与模块约束的 Java。", true,
                $"preferred={requirement.PreferredMajor}", $"arch={requirement.Architecture}"));

        var selected = valid
            .OrderBy(x => x.MajorVersion == requirement.PreferredMajor ? 0 : 1)
            .ThenBy(x => policy.PreferManaged && x.IsManaged ? 0 : 1)
            .ThenBy(x => x.IsManaged ? 0 : 1)
            .ThenBy(x => Math.Abs(x.MajorVersion - requirement.PreferredMajor))
            .ThenBy(x => x.Home, StringComparer.Ordinal)
            .First();
        return new JavaSelection(selected, rejections, null);
    }

    private static string? Validate(JavaRuntime candidate, JavaRequirement requirement)
    {
        if (!string.Equals(candidate.ProbeStatus, "ok", StringComparison.OrdinalIgnoreCase))
            return "探测未通过：" + candidate.ProbeStatus;
        if (candidate.Architecture != requirement.Architecture)
            return $"架构不符：候选 {candidate.Architecture}，需要 {requirement.Architecture}";
        if (candidate.MajorVersion < requirement.MinimumMajor)
            return $"主版本过低：{candidate.MajorVersion} < {requirement.MinimumMajor}";
        if (requirement.MaximumMajor is { } max && candidate.MajorVersion > max)
            return $"主版本过高：{candidate.MajorVersion} > {max}";
        if (requirement.RequiredModules.Count > 0)
        {
            var modules = candidate.Modules ?? [];
            var missing = requirement.RequiredModules.Where(x => !modules.Contains(x, StringComparer.Ordinal)).ToArray();
            if (missing.Length > 0) return "缺少模块：" + string.Join(", ", missing);
        }
        return null;
    }
}
