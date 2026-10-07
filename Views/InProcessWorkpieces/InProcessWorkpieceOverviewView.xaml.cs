using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticOnlineHostComputer.Views.InProcessWorkpieces;

/// <summary>
/// 当前程序生命周期内的在制工件只读页面。
/// 定时器仅在页面可见时运行，每次只复制内存快照，不访问设备或数据库。
/// </summary>
public partial class InProcessWorkpieceOverviewView : UserControl
{
    private readonly InProcessWorkpieceOverviewViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;

    public InProcessWorkpieceOverviewView()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<InProcessWorkpieceOverviewViewModel>();
        DataContext = _viewModel;

        // Background优先级避免与操作员交互争用UI；页面隐藏/卸载时立即停止。
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += View_Loaded;
        Unloaded += View_Unloaded;
        IsVisibleChanged += View_IsVisibleChanged;
    }

    private void View_Loaded(object sender, RoutedEventArgs e) => UpdateRefreshState();
    private void View_Unloaded(object sender, RoutedEventArgs e) => _refreshTimer.Stop();
    private void View_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateRefreshState();

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
            _viewModel.Refresh();
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }
}
