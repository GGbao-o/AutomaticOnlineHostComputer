using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.FlowStatus;

/// <summary>
/// 全流程状态页。
/// 只承载原主页面第四部分的 StationCards 状态图，不新建刷新逻辑。
/// </summary>
public partial class FlowStatusView : UserControl
{
    public FlowStatusView()
    {
        InitializeComponent();

        // StationCards 仍由 HomeViewModel 的刷新循环维护，这里只负责展示。
        DataContext = App.Services.GetRequiredService<HomeViewModel>();
    }
}
