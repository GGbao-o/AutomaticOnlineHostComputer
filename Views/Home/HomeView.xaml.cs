using System.Linq;
using System.Windows.Controls;
using System.Windows;
using AutomaticOnlineHostComputer.Views.Home.Dialogs;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticOnlineHostComputer.Views.Home;

public partial class HomeView : UserControl
{
    private readonly HomeViewModel _viewModel;

    public HomeView()
    {
        InitializeComponent();
        
        //从DI容器获取HomeViewModel实例，并绑定到DataContext
        _viewModel = App.Services.GetRequiredService<HomeViewModel>();//从di容器获取
        DataContext = _viewModel;//绑定viewmodel
        
        Loaded += async (_, __) => await _viewModel.LoadAsync();//窗口加载时候触发
    }

    /// <summary>
    /// 添加按钮
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddTaskDialog
        {
            Owner = Window.GetWindow(this)
        };

        var result = dialog.ShowDialog();
        if (result == true && dialog.CreatedTask != null)
        {
            _viewModel.AddTask(dialog.CreatedTask);
        }
    }

    /// <summary>
    /// 切换页面
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void RouteSelect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RouteSelectDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && DataContext is HomeViewModel vm)
        {
            vm.DefaultRouteLine = dialog.SelectedLine;
        }
    }

    /// <summary>
    /// 清空机器状态按钮 — 打开清空状态对话框
    /// <para>⚠ 使用前提: 所有引擎必须已停止(暂停)运行</para>
    /// <para>⚠ 作用: 清零上位机写入的全部机器信号(R6102/R6104/R6108 / 10370~10374 / #1101~#1105 / 研磨机输出)</para>
    /// <para>⚠ 清空后引擎需重新启动才能恢复自动化, 否则状态机检测到信号已归零会自动从Idle重新开始</para>
    /// <para>⚠ 不会清零的设备信号: R6101(CNC请求数据,只读) / #1001~#1005(FANUC只读) / Mxxx(PLC传感器,只读)</para>
    /// </summary>
    private void ClearMachineStatus_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ClearMachineStatusDialog(_viewModel.IpMap) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    /// <summary>
    /// 应急处理中心 — 斜床/动平衡/研磨特殊故障后的软件状态和锁清理。
    /// <para>⚠ 不控制天车动作, 不退磁; 执行前必须人工确认现场安全。</para>
    /// </summary>
    private void EmergencyCenter_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EmergencyCenterDialog(_viewModel) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    /// <summary>
    /// 天车移动按钮 — 打开手动天车移动对话框
    /// <para>⚠ 仅移动X轴, YZ轴保持不动(-1跳过)</para>
    /// <para>⚠ 使用前必须: ①确认天车Z已在安全高度 ②点击「刷新位置」获取当前坐标 ③确认磁铁上无工件</para>
    /// <para>⚠ 不检查 SafetyFlags(共享区域互斥) — 手动移动时操作工需自己确认目标区域无其他天车</para>
    /// <para>⚠ 不检查 X11(磁铁有版) — 如果上次异常后工件还在磁铁上, 移动可能导致掉件</para>
    /// <para>⚠ 速度从 motion_settings.json craneSpeeds 段读取, 与自动化流程一致</para>
    /// </summary>
    private void CraneMove_Click(object sender, RoutedEventArgs e)
    {
        var coords = _viewModel.StationCoords
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var dialog = new CraneMoveDialog(_viewModel.SharedCraneCache, _viewModel.SharedMotionConfig, coords)
            { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }
}
