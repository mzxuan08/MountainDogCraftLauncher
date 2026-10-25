using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MDCL.Core.Accounts;
using MDCL.Core.Instances;
using MDCL.Core.Installation;
using MDCL.Core.Launch;
using MDCL.Infrastructure.Installers;
using MDCL.Infrastructure.Launcher;
using MDCL.Infrastructure.Metadata;
using MDCL.Infrastructure.Mods;
using MDCL.Infrastructure.Diagnostics;

namespace MDCL.Desktop.ViewModels;

public sealed record InstanceRow(InstanceManifest Manifest, bool IsInstalled = false)
{
    public string LoaderDisplay => Manifest.Loader.Kind == LoaderKind.Vanilla
        ? "原版"
        : $"{Manifest.Loader.Kind} {Manifest.Loader.Version}";
    public string InstallState => IsInstalled ? "已安装" : "未安装";
    public string Details => $"{Manifest.BaseVersion}  ·  {LoaderDisplay}  ·  {InstallState}";
    public string Display => $"{Manifest.DisplayName}  ·  {Details}";
    public InstanceId Id => Manifest.InstanceId;
}

public sealed record ModrinthProjectRow(ModrinthProject Project)
{
    public string Display => $"{Project.Title}  ·  {Project.Downloads:N0} 下载";
}

public sealed record LocalModRow(LocalModInfo Info)
{
    public string Display => $"{Info.Name}  ·  {Info.Version}  ·  {Info.Loader}  ·  {(Info.Enabled ? "已启用" : "已禁用")}";
    public string FileName => Info.FileName;
    public bool Enabled => Info.Enabled;
}

public sealed record AccountRow(AccountProfile Profile)
{
    public string Display => $"{Profile.DisplayName}  ·  {(Profile.Kind == AccountKind.Local ? "本地身份" : "微软账号")}";
    public AccountId Id => Profile.Id;
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly LauncherWorkflow _workflow;
    private readonly ModrinthProvider _modrinth;
    private readonly ModrinthInstallService _modrinthInstaller;
    private readonly LocalModService _mods;
    private readonly MrpackImportService _mrpackImport;
    private IGameSession? _session;
    private DateTimeOffset _lastProgress = DateTimeOffset.MinValue;
    private bool _initialized;
    private readonly SensitiveDataRedactor _redactor = new();
    private CancellationTokenSource? _operationCancellation;
    private TaskCompletionSource? _operationCompletion;
    private long _modGeneration;

    [ObservableProperty] private string _status = "正在初始化…";
    [ObservableProperty] private string _newInstanceName = "我的原版";
    [ObservableProperty] private string _newVersion = "1.20.1";
    [ObservableProperty] private string _localDisplayName = "本地玩家";
    [ObservableProperty] private string _fabricVersion = "";
    [ObservableProperty] private string _forgeVersion = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isGameRunning;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private InstanceRow? _selectedInstance;
    [ObservableProperty] private AccountRow? _selectedAccount;
    [ObservableProperty] private string _modrinthQuery = "";
    [ObservableProperty] private ModrinthProjectRow? _selectedModrinthProject;
    [ObservableProperty] private LocalModRow? _selectedLocalMod;
    [ObservableProperty] private string _mrpackPath = "";
    [ObservableProperty] private string _mrpackInstanceName = "导入的整合包";
    [ObservableProperty] private int _selectedTabIndex;

    public MainWindowViewModel(LauncherWorkflow workflow, ModrinthProvider modrinth,
        ModrinthInstallService modrinthInstaller, LocalModService mods, MrpackImportService mrpackImport)
    {
        _workflow = workflow;
        _modrinth = modrinth;
        _modrinthInstaller = modrinthInstaller;
        _mods = mods;
        _mrpackImport = mrpackImport;
    }

    public string Title => "MDCL";
    public string Subtitle => "Minecraft Java Edition · Windows / macOS";
    public string RunState => IsGameRunning ? "运行中" : "未运行";
    public bool HasActiveWork => IsBusy || IsGameRunning;
    public string[] Navigation { get; } = ["实例", "安装", "账号", "任务", "设置"];
    public ObservableCollection<InstanceRow> Instances { get; } = [];
    public ObservableCollection<AccountRow> Accounts { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<ModrinthProjectRow> ModrinthProjects { get; } = [];
    public ObservableCollection<LocalModRow> LocalMods { get; } = [];

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await RunAsync(async ct =>
        {
            await _workflow.InitializeAsync(ct);
            if ((await _workflow.Accounts.ListAsync(ct)).Count == 0)
                await _workflow.Accounts.AddLocalAsync("本地玩家", ct);
            await RefreshCoreAsync(ct);
            Status = "就绪。创建实例后点击“安装”，完成后点击“启动”。";
        });
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(RefreshCoreAsync);

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var selectedId = SelectedInstance?.Id;
        var selectedAccountId = SelectedAccount?.Id;
        Instances.Clear();
        foreach (var instance in await _workflow.ListInstanceStatusesAsync(ct))
        {
            Instances.Add(new InstanceRow(instance.Manifest, instance.IsInstalled));
        }
        Accounts.Clear();
        foreach (var account in await _workflow.Accounts.ListAsync(ct)) Accounts.Add(new AccountRow(account));
        SelectedInstance = Instances.FirstOrDefault(x => x.Id == selectedId) ?? Instances.FirstOrDefault();
        SelectedAccount = Accounts.FirstOrDefault(x => x.Id == selectedAccountId) ?? Accounts.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        await RunAsync(async ct =>
        {
            var created = await _workflow.CreateInstanceAsync(NewInstanceName, NewVersion, ct);
            await RefreshCoreAsync(ct);
            SelectedInstance = Instances.FirstOrDefault(x => x.Id == created.InstanceId);
            Status = $"已创建实例“{created.DisplayName}”，尚未安装。";
        });
    }

    private bool CanCreate() => !IsBusy && !string.IsNullOrWhiteSpace(NewInstanceName) && !string.IsNullOrWhiteSpace(NewVersion);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        var instance = SelectedInstance;
        if (instance is null) { Status = "请先在左侧选择实例，或先创建一个实例。"; return; }
        await RunAsync(async ct =>
        {
            var progress = new Progress<OperationEvent>(evt =>
            {
                if (evt.Sequence % 20 != 0 && evt.MessageCode is "download" or "cache-hit") return;
                ProgressText = evt.TotalBytes is > 0
                    ? $"{evt.StepId}: {FormatBytes(evt.DoneBytes ?? 0)} / {FormatBytes(evt.TotalBytes.Value)}"
                    : $"{evt.StepId}: {evt.MessageCode}";
            });
            Status = "正在解析并安装原版文件…";
            await _workflow.InstallAsync(instance.Id, progress, ct);
            await RefreshCoreAsync(ct);
            SelectedInstance = Instances.FirstOrDefault(x => x.Id == instance.Id);
            ProgressText = "";
            Status = $"实例“{instance.Manifest.DisplayName}”安装完成。";
        }, cancellationToken);
    }

    private bool CanInstall() => !IsBusy && !IsGameRunning && SelectedInstance is not null;
    private bool CanLaunch() => CanInstall() && SelectedInstance!.IsInstalled && SelectedAccount is not null;
    private bool CanStop() => IsGameRunning;
    private bool CanCancel() => IsBusy && _operationCancellation is { IsCancellationRequested: false };

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        Status = "正在取消，等待当前提交步骤结束…";
        _operationCancellation?.Cancel();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public async Task ShutdownAsync()
    {
        _operationCancellation?.Cancel();
        if (_operationCompletion is { } operation) await operation.Task;
        if (_session is { } session) await session.StopAsync(CancellationToken.None);
    }

    [RelayCommand] private void OpenLoaders() => SelectedTabIndex = 1;
    [RelayCommand] private void OpenMods() => SelectedTabIndex = 2;
    [RelayCommand] private void OpenPacks() => SelectedTabIndex = 3;
    [RelayCommand] private void OpenAccounts() => SelectedTabIndex = 4;

    [RelayCommand(CanExecute = nameof(CanImportMrpack))]
    private async Task ImportMrpackAsync()
    {
        await RunAsync(async ct =>
        {
            Status = "正在解析并导入 mrpack…";
            var outcome = await _mrpackImport.ImportAsync(MrpackPath, MrpackInstanceName, null, ct);
            await RefreshCoreAsync(ct);
            SelectedInstance = Instances.FirstOrDefault(x => x.Id == outcome.Instance.InstanceId);
            Status = $"mrpack“{outcome.Preview.Name}”已导入为新实例。";
        });
    }

    private bool CanImportMrpack() => !IsBusy && !string.IsNullOrWhiteSpace(MrpackPath) &&
        !string.IsNullOrWhiteSpace(MrpackInstanceName) ;

    [RelayCommand(CanExecute = nameof(CanManageMods))]
    private Task RefreshModsAsync() => RunAsync(ct => RefreshModsCoreAsync(ct));

    private async Task RefreshModsCoreAsync(CancellationToken ct)
    {
        var generation = Interlocked.Increment(ref _modGeneration);
        var instance = SelectedInstance;
        if (instance is null) { LocalMods.Clear(); return; }
        var mods = await _mods.ListAsync(instance.Manifest, ct);
        if (generation != _modGeneration || instance.Id != SelectedInstance?.Id) return;
        LocalMods.Clear();
        foreach (var mod in mods) LocalMods.Add(new LocalModRow(mod));
        SelectedLocalMod = LocalMods.FirstOrDefault();
        Status = LocalMods.Count == 0 ? "当前实例没有本地模组。" : $"当前实例有 {LocalMods.Count} 个本地模组。";
    }

    private async Task RefreshSelectionModsAsync()
    {
        try { await RefreshModsCoreAsync(CancellationToken.None); }
        catch (Exception ex) { AddLog("模组列表读取失败: " + ex.Message); }
    }

    private bool CanManageMods() => !IsBusy && !IsGameRunning && SelectedInstance is not null;

    [RelayCommand(CanExecute = nameof(CanToggleMod))]
    private async Task ToggleModAsync()
    {
        var mod = SelectedLocalMod;
        var instance = SelectedInstance;
        if (mod is null || instance is null) return;
        await RunAsync(async ct =>
        {
            await _mods.SetEnabledAsync(instance.Manifest, mod.FileName, !mod.Enabled, ct);
            await RefreshModsCoreAsync(ct);
            Status = mod.Enabled ? $"已禁用模组 {mod.Info.Name}。" : $"已启用模组 {mod.Info.Name}。";
        });
    }

    private bool CanToggleMod() => !IsBusy && !IsGameRunning && SelectedLocalMod is not null;

    [RelayCommand(CanExecute = nameof(CanSearchModrinth))]
    private async Task SearchModrinthAsync()
    {
        await RunAsync(async ct =>
        {
            ModrinthProjects.Clear();
            SelectedModrinthProject = null;
            Status = "正在搜索 Modrinth…";
            var projects = await _modrinth.SearchAsync(ModrinthQuery, 20, ct);
            foreach (var project in projects) ModrinthProjects.Add(new ModrinthProjectRow(project));
            Status = ModrinthProjects.Count == 0 ? "Modrinth 没有匹配项目。" : $"找到 {ModrinthProjects.Count} 个 Modrinth 项目。";
        });
    }

    private bool CanSearchModrinth() => !IsBusy && !string.IsNullOrWhiteSpace(ModrinthQuery);

    [RelayCommand(CanExecute = nameof(CanInstallModrinth))]
    private async Task InstallModrinthAsync()
    {
        var project = SelectedModrinthProject;
        var instance = SelectedInstance;
        if (project is null || instance is null) return;
        await RunAsync(async ct =>
        {
            var loader = instance.Manifest.Loader.Kind switch
            {
                LoaderKind.Fabric => "fabric",
                LoaderKind.Forge => "forge",
                LoaderKind.NeoForge => "neoforge",
                _ => null
            };
            if (loader is null)
                throw new InvalidOperationException("原版实例没有模组加载器，不能安装 Modrinth 模组。");
            Status = $"正在读取 {project.Project.Title} 的兼容版本…";
            var versions = await _modrinth.GetVersionsAsync(project.Project.Id, instance.Manifest.BaseVersion, loader, ct);
            var version = versions.FirstOrDefault(x => string.Equals(x.VersionType, "release", StringComparison.OrdinalIgnoreCase)) ?? versions.FirstOrDefault();
            if (version is null) throw new InvalidOperationException("没有匹配当前游戏版本和加载器的文件。");
            var receipt = await _modrinthInstaller.InstallPrimaryFileAsync(instance.Id, version, null, ct);
            Status = $"已安装 {project.Project.Title} {version.VersionNumber} 到实例。";
            AddLog($"Modrinth: {receipt.Path}");
        });
    }

    private bool CanInstallModrinth() => !IsBusy && !IsGameRunning && SelectedInstance is not null && SelectedModrinthProject is not null;

    [RelayCommand(CanExecute = nameof(CanInstallForge))]
    private Task InstallForgeAsync() => InstallForgeCoreAsync(LoaderKind.Forge);

    [RelayCommand(CanExecute = nameof(CanInstallForge))]
    private Task InstallNeoForgeAsync() => InstallForgeCoreAsync(LoaderKind.NeoForge);

    private async Task InstallForgeCoreAsync(LoaderKind kind)
    {
        var instance = SelectedInstance;
        if (instance is null) return;
        await RunAsync(async ct =>
        {
            Status = $"正在安装 {kind} {ForgeVersion}…";
            var outcome = await _workflow.InstallForgeAsync(instance.Id, kind, ForgeVersion.Trim(), null, ct);
            await RefreshCoreAsync(ct);
            SelectedInstance = Instances.FirstOrDefault(x => x.Id == instance.Id);
            Status = $"{kind} {outcome.Manifest.Loader.Version} 已安装到“{instance.Manifest.DisplayName}”。";
        });
    }

    private bool CanInstallForge() => !IsBusy && !IsGameRunning && SelectedInstance is not null && !string.IsNullOrWhiteSpace(ForgeVersion);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallFabricAsync()
    {
        var instance = SelectedInstance;
        if (instance is null) { Status = "请先在左侧选择实例，或先创建一个实例。"; return; }
        await RunAsync(async ct =>
        {
            var progress = new Progress<OperationEvent>(evt =>
            {
                if (evt.MessageCode is "download" or "cache-hit" && evt.Sequence % 10 != 0) return;
                ProgressText = evt.TotalBytes is > 0
                    ? $"{evt.StepId}: {FormatBytes(evt.DoneBytes ?? 0)} / {FormatBytes(evt.TotalBytes.Value)}"
                    : $"{evt.StepId}: {evt.MessageCode}";
            });
            Status = "正在解析并安装 Fabric…";
            var outcome = await _workflow.InstallFabricAsync(instance.Id, FabricVersion, progress, ct);
            await RefreshCoreAsync(ct);
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
        if (instance is null) { Status = "请先在左侧选择实例。"; return; }
        if (account is null) { Status = "请先在“账号”页选择或添加一个账号。"; return; }
        await RunAsync(async ct =>
        {
            Status = "正在准备 Java、natives 与启动参数…";
            var outcome = await _workflow.LaunchAsync(instance.Id, account.Id, null, ct);
            _session = outcome.Session;
            IsGameRunning = true;
            Status = $"游戏已启动（PID {outcome.Session.ProcessId}，Java {outcome.Java.MajorVersion}）。等待日志与退出。";
            _ = MonitorSessionAsync(outcome.Session);
        });
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        var session = _session;
        if (session is null) { Status = "当前没有运行中的游戏进程。"; return; }
        Status = "正在停止当前游戏进程…";
        await session.StopAsync(CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanAddLocal))]
    private async Task AddLocalAsync()
    {
        await RunAsync(async ct =>
        {
            await _workflow.Accounts.AddLocalAsync(LocalDisplayName, ct);
            await RefreshCoreAsync(ct);
            Status = "已添加本地身份；它不会获得微软/Minecraft 正版会话。";
        });
    }

    private bool CanAddLocal() => !IsBusy && !string.IsNullOrWhiteSpace(LocalDisplayName);

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginMicrosoftAsync()
    {
        await RunAsync(async ct =>
        {
            Status = "正在打开系统浏览器进行微软登录…";
            var profile = await _workflow.Accounts.LoginMicrosoftAsync(null, ct);
            await RefreshCoreAsync(ct);
            SelectedAccount = Accounts.FirstOrDefault(x => x.Id == profile.Id);
            Status = $"微软账号“{profile.DisplayName}”已添加。";
        });
    }

    private bool CanLogin() => !IsBusy;

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operationCancellation = operation;
        _operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancelCommand.NotifyCanExecuteChanged();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status = "操作已取消；可重新安装以校验并恢复。"; }
        catch (Exception ex)
        {
            Status = _redactor.Redact(ex.Message);
            AddLog("错误: " + ex);
        }
        finally
        {
            _operationCancellation = null;
            IsBusy = false;
            _operationCompletion?.TrySetResult();
            _operationCompletion = null;
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
            await session.DisposeAsync();
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
        Logs.Add(_redactor.RedactPath(_redactor.Redact(line), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        while (Logs.Count > 1000) Logs.RemoveAt(0);
    }

    private void NotifyCommands()
    {
        CreateCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        InstallFabricCommand.NotifyCanExecuteChanged();
        InstallForgeCommand.NotifyCanExecuteChanged();
        InstallNeoForgeCommand.NotifyCanExecuteChanged();
        LaunchCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        AddLocalCommand.NotifyCanExecuteChanged();
        LoginMicrosoftCommand.NotifyCanExecuteChanged();
        SearchModrinthCommand.NotifyCanExecuteChanged();
        InstallModrinthCommand.NotifyCanExecuteChanged();
        RefreshModsCommand.NotifyCanExecuteChanged();
        ToggleModCommand.NotifyCanExecuteChanged();
        ImportMrpackCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnIsGameRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(RunState));
        NotifyCommands();
    }
    partial void OnSelectedInstanceChanged(InstanceRow? value)
    {
        NotifyCommands();
        _ = RefreshSelectionModsAsync();
    }

    partial void OnSelectedLocalModChanged(LocalModRow? value) => ToggleModCommand.NotifyCanExecuteChanged();
    partial void OnSelectedAccountChanged(AccountRow? value) => NotifyCommands();
    partial void OnSelectedModrinthProjectChanged(ModrinthProjectRow? value) => InstallModrinthCommand.NotifyCanExecuteChanged();
    partial void OnModrinthQueryChanged(string value) => SearchModrinthCommand.NotifyCanExecuteChanged();
    partial void OnMrpackPathChanged(string value) => ImportMrpackCommand.NotifyCanExecuteChanged();
    partial void OnMrpackInstanceNameChanged(string value) => ImportMrpackCommand.NotifyCanExecuteChanged();
    partial void OnNewInstanceNameChanged(string value) => CreateCommand.NotifyCanExecuteChanged();
    partial void OnNewVersionChanged(string value) => CreateCommand.NotifyCanExecuteChanged();
    partial void OnLocalDisplayNameChanged(string value) => AddLocalCommand.NotifyCanExecuteChanged();
    partial void OnForgeVersionChanged(string value)
    {
        InstallForgeCommand.NotifyCanExecuteChanged();
        InstallNeoForgeCommand.NotifyCanExecuteChanged();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.0} GiB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.0} MiB",
        >= 1024 => $"{bytes / 1024d:0.0} KiB",
        _ => $"{bytes} B"
    };
}
