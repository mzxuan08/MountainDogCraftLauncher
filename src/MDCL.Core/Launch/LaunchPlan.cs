using MDCL.Core.Accounts;
using MDCL.Core.Instances;

namespace MDCL.Core.Launch;

public sealed record LaunchPlan(
    InstanceId InstanceId,
    string JavaExecutable,
    string WorkingDirectory,
    IReadOnlyList<string> JvmArguments,
    string MainClass,
    IReadOnlyList<string> GameArguments,
    IReadOnlyList<string> ClassPath,
    string NativeDirectory,
    IReadOnlyList<string> DependencyHashes,
    IReadOnlyList<string> PrecheckEvidence,
    int JavaMajorVersion,
    IReadOnlyList<string> NativeJars,
    string AssetRoot,
    string AssetIndexId,
    string VersionType,
    string ClassPathSeparator);

public sealed record LaunchRequest(InstanceId InstanceId, bool Offline = false);

public enum GameSessionState { Preparing, ProcessStarted, Running, Exited, StartFailed, Crashed, Unknown }

public sealed record GameLogLine(DateTimeOffset Timestamp, string Stream, string Text);

public sealed record GameExitResult(int? ExitCode, GameSessionState State, DateTimeOffset ExitedAt, bool StoppedByUser, string? ErrorCode);

public interface IGameSession : IAsyncDisposable
{
    Guid SessionId { get; }
    InstanceId InstanceId { get; }
    int ProcessId { get; }
    DateTimeOffset ProcessStartedAt { get; }
    GameSessionState State { get; }
    IAsyncEnumerable<GameLogLine> ReadLogsAsync(CancellationToken ct);
    Task<GameExitResult> ExitTask { get; }
    Task StopAsync(CancellationToken ct);
}

public static class LaunchArgumentRedactor
{
    public static IReadOnlyList<string> Redact(IEnumerable<string> arguments, AuthSession session)
    {
        var token = session.AccessToken;
        return arguments.Select(argument =>
            !string.IsNullOrEmpty(token) && argument.Contains(token, StringComparison.Ordinal)
                ? argument.Replace(token, "<redacted>", StringComparison.Ordinal)
                : argument).ToArray();
    }
}
