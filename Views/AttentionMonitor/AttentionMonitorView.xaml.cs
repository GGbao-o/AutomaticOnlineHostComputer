using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.AttentionMonitor;

/// <summary>当前生命周期人工关注事件的只读页面。</summary>
public partial class AttentionMonitorView : UserControl
{
    public AttentionMonitorView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<AttentionMonitorViewModel>();
    }
}
