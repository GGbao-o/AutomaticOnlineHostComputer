using System.Windows;
using System.Windows.Controls;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticOnlineHostComputer.Views.AttentionMonitor;

/// <summary>生产异常结构化观察页面 — 只读展示，不执行设备控制。</summary>
public partial class AttentionMonitorView : UserControl
{
    private AttentionMonitorViewModel ViewModel => (AttentionMonitorViewModel)DataContext;

    public AttentionMonitorView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<AttentionMonitorViewModel>();
        Loaded += (_, _) => ViewModel?.EnterPage();
        Unloaded += (_, _) => ViewModel?.LeavePage();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e) => ViewModel?.CopyDetail();
    private async void ExportFiltered_Click(object sender, RoutedEventArgs e) { if (ViewModel != null) await ViewModel.ExportFilteredAsync(); }
    private async void ExportAll_Click(object sender, RoutedEventArgs e) { if (ViewModel != null) await ViewModel.ExportAllAsync(); }
}
