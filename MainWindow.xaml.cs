using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AutomaticOnlineHostComputer.Infrastructure.Navigation;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticOnlineHostComputer;

public partial class MainWindow : Window
{
    private readonly CachedNavigationService _navigationService;
    private readonly OperationalEventNavigationState _navState;

    public MainWindow()
    {
        InitializeComponent();

        _navigationService = App.Services.GetRequiredService<CachedNavigationService>();
        _navState = App.Services.GetRequiredService<OperationalEventNavigationState>();
        _navState.PropertyChanged += OnNavStateChanged;

        Console.WriteLine("MainWindow: 显示默认页面...");
        ContentHost.Content = _navigationService.GetOrCreatePage("main");
    }

    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is null) return;
        ContentHost.Content = _navigationService.GetOrCreatePage(menuItem.Tag.ToString());
    }

    /// <summary>
    /// 所有常规窗口关闭入口（标题栏、Alt+F4、任务栏）均需人工确认，防止误触终止运行中的自动任务。
    /// 选择“否”时取消关闭，选择“是”时继续 WPF 原有退出和资源释放流程。
    /// </summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        MessageBoxResult result = MessageBox.Show(
            "确定要关闭程序吗？\n正在运行的自动任务将停止。",
            "确认关闭程序",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        e.Cancel = result != MessageBoxResult.Yes;
    }

    private void OnNavStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OperationalEventNavigationState.UnseenDisplayText)) return;
        try
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                string badge = _navState.UnseenDisplayText;
                AttentionMonitorMenuItem.Header = string.IsNullOrWhiteSpace(badge)
                    ? "异常监控"
                    : $"异常监控 ({badge})";
            }));
        }
        catch { }
    }
}
