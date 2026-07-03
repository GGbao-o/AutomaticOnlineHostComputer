using System.Linq;
using System.Windows.Controls;
using System.Windows;
using AutomaticOnlineHostComputer.Views.Home.Dialogs;
using AutomaticOnlineHostComputer.Views.Config;
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
    /// 清空机器状态按钮 — 打开单设备清空窗口。
    /// <para>⚠ 斜床和研磨机均按站号独立操作，点击哪个只清哪个设备，不提供批量全清。</para>
    /// <para>⚠ 只清上位机写入的参数和握手输出，不清任务、软件缓存、锁或引擎状态。</para>
    /// <para>⚠ 使用前必须停止/暂停对应引擎并确认设备无动作；需要释放锁或清软件状态时使用“应急处理”。</para>
    /// <para>⚠ 不清设备→上位机请求/反馈：R6101、#1001~#1005、研磨机输入和PLC传感器等。</para>
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
    /// 运动参数配置 — 修改天车/机械手速度、斜床参数、机械手坐标、编码器标定等。
    /// <para>修改即时生效（引擎共享同一配置实例），保存按钮持久化到 JSON 文件，无需重编译。</para>
    /// </summary>
    private void ConfigPage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfigPageView(_viewModel.CreateConfigPageViewModel())
            { Owner = Window.GetWindow(this) };
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
