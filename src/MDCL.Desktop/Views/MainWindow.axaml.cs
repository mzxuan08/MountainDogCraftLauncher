using Avalonia.Controls;
using Avalonia;
using MDCL.Desktop.ViewModels;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MDCL.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _promptOpen;
    public MainWindow()
    {
        InitializeComponent();
        Closing += HandleClosing;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Control source && (source is Button or ListBox ||
            source.FindAncestorOfType<Button>() is not null || source.FindAncestorOfType<ListBox>() is not null)) return;
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else BeginMoveDrag(e);
    }

    private async void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || DataContext is not MainWindowViewModel vm || !vm.HasActiveWork) return;
        e.Cancel = true;
        if (_promptOpen) return;
        _promptOpen = true;
        try
        {
            var dialog = new Window { Title = "MDCL 仍有任务运行", Width = 450, Height = 215,
                CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = "选择继续监控，或结束当前游戏和任务后退出。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var keep = new Button { Content = "继续监控（最小化）" };
            var stop = new Button { Content = "结束当前游戏和任务后退出" };
            var back = new Button { Content = "返回" };
            keep.Click += (_, _) => dialog.Close("keep");
            stop.Click += (_, _) => dialog.Close("stop");
            back.Click += (_, _) => dialog.Close("back");
            panel.Children.Add(keep); panel.Children.Add(stop); panel.Children.Add(back);
            dialog.Content = panel;
            var choice = await dialog.ShowDialog<string?>(this);
            if (choice == "keep") WindowState = WindowState.Minimized;
            else if (choice == "stop")
            {
                await vm.ShutdownAsync();
                _allowClose = true;
                Close();
            }
        }
        catch (Exception) { vm.Status = "退出尚未完成，请停止当前任务后重试。"; }
        finally { _promptOpen = false; }
    }
}
