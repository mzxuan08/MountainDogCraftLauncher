using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using MDCL.Application.Ports;
using MDCL.Core.Accounts;
using MDCL.Core.Errors;
using MDCL.Core.Instances;
using MDCL.Core.Launch;
using MDCL.Infrastructure.Diagnostics;

namespace MDCL.Infrastructure.Processes;

public sealed class GameProcessService : IGameProcessService
{
    private readonly string _temporaryRoot;
    public GameProcessService(string temporaryRoot) => _temporaryRoot = Path.GetFullPath(temporaryRoot);

    public async Task<IGameSession> StartAsync(LaunchPlan plan, AuthSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sessionId = Guid.NewGuid();
        var working = Path.Combine(_temporaryRoot, sessionId.ToString("N"));
        var arguments = BindArguments(plan, session);
        string? argFile = null;
        if (arguments.Sum(x => x.Length + 1) > 30000)
        {
            if (plan.JavaMajorVersion < 9)
                throw new MdclException(MdclError.Of("PROCESS_COMMAND_TOO_LONG", "Launch",
                    "Java 8 不支持 argfile，命令过长。", false));
            argFile = await JavaArgumentFileWriter.WriteAsync(working, arguments, ct);
            arguments = ["@" + argFile];
        }
        Directory.CreateDirectory(working);

        var start = new ProcessStartInfo
        {
            FileName = plan.JavaExecutable,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH" })
            start.Environment.Remove(name);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Process did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            if (argFile is not null) TryDelete(argFile);
            try { Directory.Delete(working, true); } catch (IOException) { }
            throw new MdclException(MdclError.Of("LAUNCH_PROCESS_START_FAILED", "Launch", "游戏进程启动失败。", false, ex.Message), ex);
        }
        var sessionObject = new GameSession(sessionId, plan, session, process, working, argFile);
        sessionObject.StartPumps();
        return sessionObject;
    }

    internal static IReadOnlyList<string> BindArguments(LaunchPlan plan, AuthSession session)
    {
        var uuid = session.Profile.MinecraftUuid ?? "";
        var token = string.IsNullOrEmpty(session.AccessToken) ? "0" : session.AccessToken;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = session.Profile.DisplayName,
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = token,
            ["auth_session"] = token,
            ["user_type"] = session.Profile.Kind == AccountKind.Microsoft ? "msa" : "legacy",
            ["user_properties"] = "{}",
            ["auth_xuid"] = "0"
        };
        var arguments = new List<string>();
        foreach (var argument in plan.JvmArguments) arguments.Add(Replace(argument, values));
        arguments.Add(plan.MainClass);
        foreach (var argument in plan.GameArguments) arguments.Add(Replace(argument, values));
        return arguments;
    }

    private static string Replace(string value, IReadOnlyDictionary<string, string> values)
    {
        var result = value;
        foreach (var pair in values) result = result.Replace("${" + pair.Key + "}", pair.Value, StringComparison.Ordinal);
        return result;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    private sealed class GameSession : IGameSession
    {
        private readonly LaunchPlan _plan;
        private readonly AuthSession _session;
        private readonly SensitiveDataRedactor _redactor = new();
        private readonly Process _process;
        private readonly string _workingDirectory;
        private readonly string? _argFile;
        private readonly Channel<GameLogLine> _logs = Channel.CreateBounded<GameLogLine>(
            new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = false });
        private readonly TaskCompletionSource<GameExitResult> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _lifetime = new();
        private int _stopped;
        private int _state = (int)GameSessionState.ProcessStarted;
        private Task _stdoutPump = Task.CompletedTask;
        private Task _stderrPump = Task.CompletedTask;

        public GameSession(Guid sessionId, LaunchPlan plan, AuthSession session, Process process, string workingDirectory, string? argFile)
        {
            SessionId = sessionId;
            _plan = plan;
            _session = session;
            _process = process;
            _workingDirectory = workingDirectory;
            _argFile = argFile;
            ProcessId = process.Id;
            ProcessStartedAt = process.StartTime.ToUniversalTime();
            ProcessIdAtStart = process.StartTime;
        }

        public Guid SessionId { get; }
        public InstanceId InstanceId => _plan.InstanceId;
        public int ProcessId { get; }
        public DateTimeOffset ProcessStartedAt { get; }
        public DateTimeOffset ProcessIdAtStart { get; }
        public GameSessionState State => (GameSessionState)Volatile.Read(ref _state);
        public Task<GameExitResult> ExitTask => _exit.Task;

        public void StartPumps()
        {
            _stdoutPump = PumpAsync(_process.StandardOutput, "stdout");
            _stderrPump = PumpAsync(_process.StandardError, "stderr");
            _ = MonitorAsync();
        }

        private async Task PumpAsync(StreamReader reader, string stream)
        {
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(_lifetime.Token);
                    if (line is null) break;
                    if (!string.IsNullOrEmpty(_session.AccessToken))
                        line = line.Replace(_session.AccessToken, "<redacted>", StringComparison.Ordinal);
                    line = _redactor.RedactPath(_redactor.Redact(line), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    _logs.Writer.TryWrite(new GameLogLine(DateTimeOffset.UtcNow, stream, line));
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally { }
        }

        private async Task MonitorAsync()
        {
            try
            {
                Volatile.Write(ref _state, (int)GameSessionState.Running);
                await _process.WaitForExitAsync();
                await Task.WhenAll(_stdoutPump, _stderrPump);
                _logs.Writer.TryComplete();
                var stopped = Volatile.Read(ref _stopped) != 0;
                var state = stopped ? GameSessionState.Exited
                    : _process.ExitCode == 0 ? GameSessionState.Exited
                    : GameSessionState.Crashed;
                Volatile.Write(ref _state, (int)state);
                _exit.TrySetResult(new GameExitResult(_process.ExitCode, state, DateTimeOffset.UtcNow, stopped,
                    state == GameSessionState.Crashed ? "GAME_PROCESS_NONZERO_EXIT" : null));
            }
            catch (Exception)
            {
                _logs.Writer.TryComplete();
                Volatile.Write(ref _state, (int)GameSessionState.Unknown);
                _exit.TrySetResult(new GameExitResult(null, GameSessionState.Unknown, DateTimeOffset.UtcNow,
                    Volatile.Read(ref _stopped) != 0, "GAME_PROCESS_MONITOR_FAILED"));
            }
            finally
            {
                if (_argFile is not null) TryDelete(_argFile);
                try { if (Directory.Exists(_workingDirectory)) Directory.Delete(_workingDirectory, true); } catch (IOException) { }
                _lifetime.Cancel();
            }
        }

        public async IAsyncEnumerable<GameLogLine> ReadLogsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var line in _logs.Reader.ReadAllAsync(ct)) yield return line;
        }

        public async Task StopAsync(CancellationToken ct)
        {
            if (!_process.HasExited)
            {
                Interlocked.Exchange(ref _stopped, 1);
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await _process.WaitForExitAsync(ct);
            }
            await _exit.Task.WaitAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            _lifetime.Cancel();
            _process.Dispose();
            if (_argFile is not null) TryDelete(_argFile);
            try { if (Directory.Exists(_workingDirectory)) Directory.Delete(_workingDirectory, true); } catch (IOException) { }
            await Task.CompletedTask;
        }
    }
}
