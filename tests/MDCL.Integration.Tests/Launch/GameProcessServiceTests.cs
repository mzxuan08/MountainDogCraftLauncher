using System.Text;
using MDCL.Core;
using MDCL.Core.Accounts;
using MDCL.Core.Instances;
using MDCL.Core.Launch;
using MDCL.Infrastructure.Processes;
using Xunit;

namespace MDCL.Integration.Tests.Launch;

[Collection("ProcessEnvironment")]
public sealed class GameProcessServiceTests
{
    [Fact]
    public async Task ArgumentsArriveExactlyWithoutShellAndSecretsStayBound()
    {
        using var temp = new TestDirectory();
        var helper = HelperPath();
        var plan = Plan(helper, temp.Path, ["jvm-中文 & 空格", "-Dprop=a b"],
            ["--username", "${auth_player_name}", "--uuid", "${auth_uuid}", "--accessToken", "${auth_access_token}", "--utf8"]);
        var session = new AuthSession(new AccountProfile(AccountId.New(), "玩家 名", AccountKind.Microsoft, "abcdef"),
            "secret-token-value", DateTimeOffset.UtcNow.AddHours(1));
        await using var process = await new GameProcessService(Path.Combine(temp.Path, "temp")).StartAsync(plan, session, CancellationToken.None);
        var lines = await ReadAllAsync(process, TimeSpan.FromSeconds(15));
        var exit = await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(5));
        var arguments = lines.Where(x => x.Stream == "stdout" && x.Text.StartsWith("ARG[", StringComparison.Ordinal))
            .Select(x => Encoding.UTF8.GetString(Convert.FromBase64String(x.Text[(x.Text.IndexOf('=') + 1)..]))).ToArray();
        Assert.Contains("jvm-中文 & 空格", arguments);
        Assert.Contains("玩家 名", arguments);
        Assert.Contains("abcdef", arguments);
        Assert.Contains("secret-token-value", arguments);
        Assert.Contains("--utf8", arguments);
        Assert.Equal(GameSessionState.Exited, exit.State);
        Assert.Contains(lines, x => (x.Text.Contains("中文🙂", StringComparison.Ordinal) || x.Text.Contains("结束", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ShellEnvironmentJavaOptions_AreRemoved()
    {
        using var temp = new TestDirectory();
        var previous = Environment.GetEnvironmentVariable("JAVA_TOOL_OPTIONS");
        Environment.SetEnvironmentVariable("JAVA_TOOL_OPTIONS", "-Dshould-not-leak=1");
        try
        {
            await using var process = await new GameProcessService(Path.Combine(temp.Path, "temp")).StartAsync(
                Plan(HelperPath(), temp.Path, [], []), LocalSession(), CancellationToken.None);
            var lines = await ReadAllAsync(process, TimeSpan.FromSeconds(15));
            Assert.Contains(lines, x => x.Text == "ENV_JAVA_TOOL_OPTIONS=<null>");
        }
        finally { Environment.SetEnvironmentVariable("JAVA_TOOL_OPTIONS", previous); }
    }

    [Fact]
    public async Task LargeStdoutAndStderr_DoNotDeadlock()
    {
        using var temp = new TestDirectory();
        await using var process = await new GameProcessService(Path.Combine(temp.Path, "temp")).StartAsync(
            Plan(HelperPath(), temp.Path, [], ["--flood"]), LocalSession(), CancellationToken.None);
        var lines = await ReadAllAsync(process, TimeSpan.FromSeconds(20));
        var exit = await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(GameSessionState.Exited, exit.State);
        Assert.True(lines.Count > 100);
        Assert.Contains(lines, x => x.Stream == "stdout" && x.Text.StartsWith("OUT-", StringComparison.Ordinal));
        Assert.Contains(lines, x => x.Stream == "stderr" && x.Text.StartsWith("ERR-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonZeroExit_IsCrashedAndNotMainMenuClaim()
    {
        using var temp = new TestDirectory();
        await using var process = await new GameProcessService(Path.Combine(temp.Path, "temp")).StartAsync(
            Plan(HelperPath(), temp.Path, [], ["--exit", "7"]), LocalSession(), CancellationToken.None);
        await ReadAllAsync(process, TimeSpan.FromSeconds(15));
        var exit = await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, exit.ExitCode);
        Assert.Equal(GameSessionState.Crashed, exit.State);
        Assert.Equal("GAME_PROCESS_NONZERO_EXIT", exit.ErrorCode);
    }

    [Fact]
    public async Task Stop_OnlyTargetsOwnProcessAndReportsUserStop()
    {
        using var temp = new TestDirectory();
        await using var process = await new GameProcessService(Path.Combine(temp.Path, "temp")).StartAsync(
            Plan(HelperPath(), temp.Path, [], ["--sleep"]), LocalSession(), CancellationToken.None);
        await Task.Delay(300);
        await process.StopAsync(CancellationToken.None);
        var exit = await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(exit.StoppedByUser);
        Assert.Equal(GameSessionState.Exited, exit.State);
    }

    private static AuthSession LocalSession() => new(
        new AccountProfile(AccountId.New(), "Local", AccountKind.Local, "uuid"), "", DateTimeOffset.MaxValue);

    private static LaunchPlan Plan(string exe, string root, string[] jvm, string[] game) => new(
        InstanceId.New(), exe, root, jvm, "example.Main", game, [], Path.Combine(root, "natives"),
        [], [], 17, [], Path.Combine(root, "assets"), "5", "release", ";");

    private static async Task<List<GameLogLine>> ReadAllAsync(IGameSession session, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var lines = new List<GameLogLine>();
        await foreach (var line in session.ReadLogsAsync(cts.Token)) lines.Add(line);
        return lines;
    }

    private static string HelperPath()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var configuration = new DirectoryInfo(baseDirectory).Parent!.Name;
        var executable = OperatingSystem.IsWindows() ? "ArgumentEcho.exe" : "ArgumentEcho";
        return Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "Fixtures", "ArgumentEcho", "bin", configuration, "net10.0", executable));
    }
}
