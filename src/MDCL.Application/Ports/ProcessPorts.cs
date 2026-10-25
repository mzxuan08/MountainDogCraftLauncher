using MDCL.Core.Accounts;
using MDCL.Core.Instances;
using MDCL.Core.Launch;

namespace MDCL.Application.Ports;

public interface ILaunchPlanner
{
    Task<LaunchPlan> CreateAsync(LaunchRequest request, CancellationToken ct);
}

public interface IGameProcessService
{
    Task<IGameSession> StartAsync(LaunchPlan plan, AuthSession session, CancellationToken ct);
}
