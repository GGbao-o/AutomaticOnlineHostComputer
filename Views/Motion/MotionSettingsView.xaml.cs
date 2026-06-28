using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Motion;

/// <summary>
/// 运动参数设置页。
/// 当前只迁移主页面原有手动运动控制，不新建控制 ViewModel，避免重复连接设备。
/// </summary>
public partial class MotionSettingsView : UserControl
{
    public MotionSettingsView()
    {
        InitializeComponent();

        // 复用主页面同一个 HomeViewModel/ManualControl，页面切换时手动控制状态保持一致。
        DataContext = App.Services.GetRequiredService<HomeViewModel>();
    }
}
