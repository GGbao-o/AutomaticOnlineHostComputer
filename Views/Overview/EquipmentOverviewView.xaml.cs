using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Overview;

/// <summary>
/// 设备总览页。
/// 只承载原主页面第三部分的显示和测试按钮，继续复用同一个 HomeViewModel，避免重复连接设备。
/// </summary>
public partial class EquipmentOverviewView : UserControl
{
    public EquipmentOverviewView()
    {
        InitializeComponent();

        // 第三部分的卡片命令、研磨参数测试命令都在 HomeViewModel 中，拆页后仍复用同一份实例。
        DataContext = App.Services.GetRequiredService<HomeViewModel>();
    }
}
