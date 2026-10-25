using MDCL.Application.Ports;
using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Core.Installation;
using MDCL.Core.Java;
using MDCL.Core.Versions;
using MDCL.Infrastructure.Java;
using MDCL.Infrastructure.Launcher;
using Xunit;

namespace MDCL.Integration.Tests.Java;

public sealed class BoundRuntimeWorkflowTests
{
    [Fact]
    public async Task MissingExplicitBinding_NeverInstallsReplacementSilently()
    {
        var installer = new UnexpectedInstaller();
        var workflow = new LauncherWorkflow(null!,null!,null!,null!,null!,null!,null!,null!,
            new EmptyDiscovery(), new JavaSelector(), installer,null!,null!,null!);
        var instance = new InstanceManifest { InstanceId=InstanceId.New(), DisplayName="bound", BaseVersion="1.20.1", GameDirectory=".minecraft",
            JavaPolicy=new JavaPolicy("missing-runtime",true,CpuArchitecture.X64) };
        var version = new ResolvedVersion("1.20.1","Main",[],[],[],[],null,null,null,
            new JavaRequirement(17,17,null,CpuArchitecture.X64,[],[]),[],"hash");
        var error = await Assert.ThrowsAsync<MdclException>(() => workflow.EnsureJavaAsync(instance,version,CancellationToken.None));
        Assert.Equal("JAVA_BOUND_RUNTIME_MISSING",error.Error.ErrorCode);
        Assert.Equal(0,installer.Calls);
    }
    private sealed class EmptyDiscovery : IJavaDiscovery
    { public Task<IReadOnlyList<JavaProbeResult>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<JavaProbeResult>>([]); }
    private sealed class UnexpectedInstaller : IJavaRuntimeInstaller
    {
        public int Calls {get;private set;}
        public Task<JavaRuntime> InstallAsync(JavaRequirement requirement,TargetPlatform target,IProgress<OperationEvent>? progress,CancellationToken ct)
        { Calls++; throw new InvalidOperationException("Explicit binding must prevent automatic installation."); }
    }
}
