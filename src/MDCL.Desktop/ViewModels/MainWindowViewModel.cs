using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MDCL.Core.Accounts;
using MDCL.Core.Instances;
using MDCL.Core.Installation;
using MDCL.Core.Launch;
using MDCL.Infrastructure.Launcher;

namespace MDCL.Desktop.ViewModels;

public sealed record InstanceRow(InstanceManifest Manifest, bool IsInstalled = false)
{
    public string Display
    {
        get
        {
            var loader = Manifest.Loader.Kind == LoaderKind.Vanilla
                ? "原版"
                : $"{Manifest.Loader.Kind} {Manifest.Loader.Version}";
            return $"{Manifest.DisplayName}  ·  {Manifest.BaseVersion}  ·  {loader}{(IsInstalled ? "" : "  ·  未安装")}";
        }
    }
    public InstanceId Id => Manifest.InstanceId;
}

public sealed record AccountRow(AccountProfile Profile)
{
    public string Display => $"{Profile.DisplayName}  ·  {(Profile.Kind == AccountKind.Local ? "本地身份" : "微软账号")}";
    public AccountId Id => Profile.Id;
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly LauncherWorkflow _workflow;
    private IGameSession? _session;
    private DateTimeOffset _lastProgress = DateTimeOffset.MinValue;
    private bool _initialized;

    [ObservableProperty] private string _status = "正在初始化…";
    [ObservableProperty] private string _newInstanceName = "我的原版";
    [ObservableProperty] private string _newVersion = "1.20.1";
    [ObservableProperty] private string _localDisplayName = "本地玩家";
    [ObservableProperty] private string _fabricVersion = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isGameRunning;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private InstanceRow? _selectedInstance;
    [ObservableProperty] private AccountRow? _selectedAccount;

    public MainWindowViewModel(LauncherWorkflow workflow) => _workflow = workflow;

    public string Title => "MDCL";
    public string Subtitle => "Minecraft Java Edition · Windows / macOS";
    public string[] Navigation { get; } = ["实例", "安装", "账号", "任务", "设置"];
    public ObservableCollection<InstanceRow> Instances { get; } = [];
    public ObservableCollection<AccountRow> Accounts { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await RunAsync(async () =>
        {
            await _workflow.InitializeAsync(CancellationToken.None);
            if ((await _workflow.Accounts.ListAsync(CancellationToken.None)).Count == 0)
                await _workflow.Accounts.AddLocalAsync("本地玩家", CancellationToken.None);
            await RefreshCoreAsync();
            Status = "就绪。创建实例后点击“安装”，完成后点击“启动”。";
        });
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(RefreshCoreAsync);

    private async Task RefreshCoreAsync()
    {
        var selectedId = SelectedInstance?.Id;
        var selectedAccountId = SelectedAccount?.Id;
        Instances.Clear();
        foreach (var instance in await _workflow.ListInstancesAsync(CancellationToken.None))
        {
            var instanceDirectory = Path.Combine(_workflow.Paths.InstancesRoot, instance.InstanceId.ToString());
            Instances.Add(new InstanceRow(instance, File.Exists(Path.Combine(instanceDirectory, "resolved-version.json"))));
        }
        Accounts.Clear();
        foreach (var account in await _workflow.Accounts.ListAsync(CancellationToken.None)) Accounts.Add(new AccountRow(account));
        SelectedInstance = Instances.FirstOrDefault(x => x.Id == selectedId) ?? Instances.FirstOrDefault();
        SelectedAccount = Accounts.FirstOrDefault(x => x.Id == selectedAccountId) ?? Accounts.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        await RunAsync(async () =>
        {
            var created = await _workflow.CreateInstanceAsync(NewInstanceName, NewVersion, CancellationToken.None);
            await RefreshCoreAsync();
            SelectedInstance = Instances.FirstOrDefault(x => x.Id == created.InstanceId);
            Status = $"已创建实例“{created.DisplayName}”，尚未安装。";
        });
    }

    private bool CanCreate() => !IsBusy && !string.IsNullOrWhiteSpace(NewInstanceName) && !string.IsNullOrWhiteSpace(NewVersion);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var instance = SelectedInstance;
        if (instance is null) return;
        await RunAsync(async () =>
        {
            var progress = new Progress<OperationEvent>(evt =>
            {
                if (evt.Sequence % 20 != 0 && evt.MessageCode is "download" or "cache-hit") return;
                ProgressText = evt.TotalBytes is > 0
                    ? $"{evt.StepId}: {FormatBytes(evt.DoneBytes ?? 0)} / {FormatBytes(evt.TotalBytes.Value)}"
                    : $"{evt.StepId}: {evt.MessageCode}";
            });
            Status = "正在解析并安装原版文件…";
            await _workflow.InstallAsync(instance.Id, progress, CancellationToken.None);
            await RefreshCoreAsync();
            SelectedInstance = Instances.FirstOrDefault(x => x.Id == instance.Id);
            ProgressText = "";
            Status = $"实例“{instance.Manifest.DisplayName}”安装完成。";
        });
    }

    private bool CanInstall() => !IsBusy && SelectedInstance is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallFabricAsync()
    {
        var instance = SelectedInstance;
        if (instance is null) return;
        await RunAsync(async () =>
        {
            var progress = new Progress<OperationEvent>(evt =>
            {
                if (evt.MessageCode is "download" or "cache-hit" && evt.Sequence % 10 != 0) return;
                ProgressText = evt.TotalBytes is > 0
                    ? $"{evt.StepId}: {FormatBytes(evt.DoneBytes ?? 0)} / {FormatBytes(evt.TotalBytes.Value)}"
                    : $"{evt.StepId}: {evt.MessageCode}";
            });
            Status = "正在解析并安装 Fabric…";
            var outcome = await _workflow.InstallFabricAsync(instance.Id, FabricVersion, progress, CancellationToken.None);
            await RefreshCoreAsync();
            SelectedInstance = Instances.FirstOrDefault(x => x.Id == instance.Id);
            ProgressText = "";
            Status = $"Fabric {outcome.Manifest.Loader.Version} 已安装到“{instance.Manifest.DisplayName}”。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        var instance = SelectedInstance;
        var account = SelectedAccount;
        if (instance is null || account is null) return;
        await RunAsync(async () =>
        {
            Status = "正在准备 Java、natives 与启动参数…";
            var outcome = await _workflow.LaunchAsync(instance.Id, account.Id, null, CancellationToken.None);
            _session = outcome.Session;
            IsGameRunning = true;
            Status = $"游戏已启动（PID {outcome.Session.ProcessId}，Java {outcome.Java.MajorVersion}）。等待日志与退出。";
            _ = MonitorSessionAsync(outcome.Session);
        });
    }

    private bool CanLaunch() => !IsBusy && !IsGameRunning && SelectedInstance is not null && SelectedAccount is not null;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        var session = _session;
        if (session is null) return;
        Status = "正在停止当前游戏进程…";
        await session.StopAsync(CancellationToken.None);
    }

    private bool CanStop() => _session is not null && IsGameRunning;

    [RelayCommand(CanExecute = nameof(CanAddLocal))]
    private async Task AddLocalAsync()
    {
        await RunAsync(async () =>
        {
            await _workflow.Accounts.AddLocalAsync(LocalDisplayName, CancellationToken.None);
            await RefreshCoreAsync();
            Status = "已添加本地身份；它不会获得微软/Minecraft 正版会话。";
        });
    }

    private bool CanAddLocal() => !IsBusy && !string.IsNullOrWhiteSpace(LocalDisplayName);

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginMicrosoftAsync()
    {
        await RunAsync(async () =>
        {
            Status = "正在打开系统浏览器进行微软登录…";
            var profile = await _workflow.Accounts.LoginMicrosoftAsync(null, CancellationToken.None);
            await RefreshCoreAsync();
            SelectedAccount = Accounts.FirstOrDefault(x => x.Id == profile.Id);
            Status = $"微软账号“{profile.DisplayName}”已添加。";
        });
    }

    private bool CanLogin() => !IsBusy;

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        catch (Exception ex)
        {
            Status = ex.Message;
            AddLog("错误: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private async Task MonitorSessionAsync(IGameSession session)
    {
        try
        {
            await foreach (var line in session.ReadLogsAsync(CancellationToken.None))
                AddLog($"[{line.Stream}] {line.Text}");
            var exit = await session.ExitTask;
            AddLog($"进程退出：code={exit.ExitCode}, state={exit.State}, userStop={exit.StoppedByUser}");
        }
        catch (Exception ex) { AddLog("日志读取失败: " + ex.Message); }
        finally
        {
            _session = null;
            IsGameRunning = false;
            Status = "游戏已退出。可再次启动同一实例。";
            NotifyCommands();
        }
    }

    private void AddLog(string line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddLog(line));
            return;
        }
        Logs.Add(line);
        while (Logs.Count > 1000) Logs.RemoveAt(0);
    }

    private void NotifyCommands()
    {
        CreateCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        InstallFabricCommand.NotifyCanExecuteChanged();
        LaunchCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        AddLocalCommand.NotifyCanExecuteChanged();
        LoginMicrosoftCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnIsGameRunningChanged(bool value) => NotifyCommands();
    partial void OnSelectedInstanceChanged(InstanceRow? value) => NotifyCommands();
    partial void OnSelectedAccountChanged(AccountRow? value) => NotifyCommands();
    partial void OnNewInstanceNameChanged(string value) => CreateCommand.NotifyCanExecuteChanged();
    partial void OnNewVersionChanged(string value) => CreateCommand.NotifyCanExecuteChanged();
    partial void OnLocalDisplayNameChanged(string value) => AddLocalCommand.NotifyCanExecuteChanged();

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.0} GiB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.0} MiB",
        >= 1024 => $"{bytes / 1024d:0.0} KiB",
        _ => $"{bytes} B"
    };
}
