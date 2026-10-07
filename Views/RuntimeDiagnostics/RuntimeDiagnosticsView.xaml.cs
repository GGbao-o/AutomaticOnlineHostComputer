using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticOnlineHostComputer.Views.RuntimeDiagnostics;

/// <summary>
/// 当前运行状态的只读诊断页。
/// 页面自己的定时器仅在页面可见时运行；每次只刷新内存快照，不执行设备通信或业务控制。
/// </summary>
public partial class RuntimeDiagnosticsView : UserControl
{
    private readonly RuntimeDiagnosticsViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;

    public RuntimeDiagnosticsView()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<RuntimeDiagnosticsViewModel>();
        DataContext = _viewModel;

        // 使用UI线程定时器，避免额外后台线程；页面隐藏后立即停止，缓存页面不会常驻刷新。
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += RuntimeDiagnosticsView_Loaded;
        Unloaded += RuntimeDiagnosticsView_Unloaded;
        IsVisibleChanged += RuntimeDiagnosticsView_IsVisibleChanged;
    }

    private void RuntimeDiagnosticsView_Loaded(object sender, RoutedEventArgs e) => UpdateRefreshState();

    private void RuntimeDiagnosticsView_Unloaded(object sender, RoutedEventArgs e) => _refreshTimer.Stop();

    private void RuntimeDiagnosticsView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdateRefreshState();

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            _refreshTimer.Stop();
            return;
        }

        _viewModel.Refresh();
    }

    private void UpdateRefreshState()
    {
        if (IsVisible)
        {
            // 打开页面立即显示当前状态，不必先等待1秒。
            _viewModel.Refresh();
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }
}
