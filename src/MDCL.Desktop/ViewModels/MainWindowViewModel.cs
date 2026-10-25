using CommunityToolkit.Mvvm.ComponentModel;

namespace MDCL.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _status = "桌面壳已就绪；启动器功能尚未验收。";

    public string Title => "MDCL";
    public string Subtitle => "Minecraft Java Edition · Windows / macOS";
    public string[] Navigation { get; } = ["实例", "安装", "账号", "任务", "设置"];
}
