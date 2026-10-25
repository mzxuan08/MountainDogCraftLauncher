using MDCL.Application.Ports;
using MDCL.Core.Java;

namespace MDCL.Infrastructure.Java;

public sealed class JavaSelector : IJavaSelector
{
    public Task<JavaSelection> SelectAsync(
        JavaRequirement requirement, MDCL.Core.Instances.JavaPolicy policy,
        IReadOnlyList<JavaRuntime> candidates, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var selection = JavaSelectionPolicy.Select(requirement, policy, candidates);
        return Task.FromResult(selection);
    }
}
