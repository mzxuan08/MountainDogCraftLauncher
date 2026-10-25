using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Core.Java;
using Xunit;

namespace MDCL.Core.Tests.Java;

public sealed class JavaSelectionTests
{
    [Fact]
    public void Constraints_IntersectAndConflictExplicitly()
    {
        var merged = JavaRequirementFactory.Create(17, CpuArchitecture.X64,
            [new JavaConstraint(17, null, "game"), new JavaConstraint(null, 21, "loader")]);
        Assert.Equal(17, merged.MinimumMajor);
        Assert.Equal(21, merged.MaximumMajor);
        var error = Assert.Throws<MdclException>(() => JavaRequirementFactory.Create(25, CpuArchitecture.X64,
            [new JavaConstraint(21, 21, "loader"), new JavaConstraint(25, null, "game")]));
        Assert.Equal("JAVA_CONSTRAINT_CONFLICT", error.Error.ErrorCode);
    }

    [Fact]
    public void PreferredButIncompatibleDirectoryJava_DoesNotBeatValidCandidate()
    {
        var requirement = JavaRequirementFactory.Create(17, CpuArchitecture.X64, [new JavaConstraint(17, null, "game")]);
        var policy = new JavaPolicy(null, true, CpuArchitecture.X64);
        var candidates = new[]
        {
            Runtime("old21", 21, CpuArchitecture.X64, "/preferred/old21", true),
            Runtime("managed17", 17, CpuArchitecture.X64, "/managed/17", true),
            Runtime("external17", 17, CpuArchitecture.X64, "/external/17", false)
        };
        var result = JavaSelectionPolicy.Select(requirement, policy, candidates);
        Assert.NotNull(result.Selected);
        Assert.Equal("managed17", result.Selected.RuntimeId);
    }

    [Fact]
    public void InvalidBoundRuntime_DoesNotSilentlyFallBack()
    {
        var requirement = JavaRequirementFactory.Create(17, CpuArchitecture.X64, [new JavaConstraint(17, null, "game")]);
        var policy = new JavaPolicy("missing", true, CpuArchitecture.X64);
        var result = JavaSelectionPolicy.Select(requirement, policy, [Runtime("valid17", 17, CpuArchitecture.X64, "/j17", true)]);
        Assert.Null(result.Selected);
        Assert.Equal("JAVA_BOUND_RUNTIME_MISSING", result.Error?.ErrorCode);
    }

    [Fact]
    public void ArchitectureAndProbeFailures_AreExplainedAndFiltered()
    {
        var requirement = JavaRequirementFactory.Create(17, CpuArchitecture.Arm64, [new JavaConstraint(17, null, "game")]);
        var policy = new JavaPolicy(null, true, CpuArchitecture.Arm64);
        var candidates = new[]
        {
            Runtime("x64", 17, CpuArchitecture.X64, "/x64", true),
            Runtime("broken", 17, CpuArchitecture.Arm64, "/broken", true) with { ProbeStatus = "timeout" },
            Runtime("arm", 17, CpuArchitecture.Arm64, "/arm", true)
        };
        var result = JavaSelectionPolicy.Select(requirement, policy, candidates);
        Assert.Equal("arm", result.Selected?.RuntimeId);
        Assert.Equal(2, result.Rejections.Count);
        Assert.Contains(result.Rejections, x => x.Reason.Contains("架构", StringComparison.Ordinal));
        Assert.Contains(result.Rejections, x => x.Reason.Contains("timeout", StringComparison.Ordinal));
    }

    private static JavaRuntime Runtime(string id, int major, CpuArchitecture arch, string home, bool managed) =>
        new(id, home, Path.Combine(home, "bin", "java"), major, "test", arch, "ok", DateTimeOffset.UtcNow, managed);
}
