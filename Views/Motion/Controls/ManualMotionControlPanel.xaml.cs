using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Motion.Controls;

/// <summary>
/// 手动运动控制面板。
/// 只承载界面布局，DataContext 由外层页面传入，确保始终复用 HomeViewModel.ManualControl。
/// </summary>
public partial class ManualMotionControlPanel : UserControl
{
    public ManualMotionControlPanel()
    {
        InitializeComponent();
    }
}
