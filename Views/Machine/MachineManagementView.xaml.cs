using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Views.Machine.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Machine;

/*
 * ╔══════════════════════════════════════════════════════════════════════════╗
     * ║                     MachineManagementView（改造说明）                    ║
 * ╠══════════════════════════════════════════════════════════════════════════╣
 * ║  改造前：                                                                ║
 * ║    var connectionString = DbSettingsProvider.GetConnectionString();      ║
 * ║    _queryService  = new ManagementQueryService(connectionString);        ║
 * ║    _deleteService = new ManagementDeleteService(connectionString);       ║
 * ║                                                                          ║
 * ║  问题：每个 View 都重复读取配置文件、手动 new 服务实例，                  ║
 * ║         违反"单一职责"原则，且无法替换为测试桩（Mock）。                  ║
 * ║                                                                          ║
 * ║  改造后：                                                                ║
 * ║    _queryService  = App.Services.GetRequiredService<ManagementQueryService>();  ║
 * ║    _deleteService = App.Services.GetRequiredService<ManagementDeleteService>(); ║
 * ║                                                                          ║
 * ║  优点：                                                                  ║
 * ║    • 服务在 App.OnStartup 统一注册，全程序只有一份实例（Singleton）       ║
 * ║    • View 只关心"用服务"，不关心"怎么创建服务"                           ║
 * ║    • 连接字符串修改只需改 ServiceCollectionExtensions，其余代码不动      ║
 * ╚══════════════════════════════════════════════════════════════════════════╝
 */

public partial class MachineManagementView : UserControl
{
    // ── 字段从 DI 容器解析，不再手动 new ────────────────────────────────────
    private readonly ManagementQueryService _queryService;
    private readonly ManagementDeleteService _deleteService;

    public MachineManagementView()
    {
        InitializeComponent();

        // GetRequiredService<T>：从全局 DI 容器解析已注册的 Singleton 服务。
        // 若服务未注册则抛出 InvalidOperationException，便于启动时发现配置问题。
        _queryService  = App.Services.GetRequiredService<ManagementQueryService>();
        _deleteService = App.Services.GetRequiredService<ManagementDeleteService>();

        Loaded += async (_, _) => await LoadGridDataAsync();
    }

    /// <summary>
    /// 异步加载机器管理列表数据。
    /// </summary>
    private async Task LoadGridDataAsync()
    {
        try
        {
            MachineGrid.ItemsSource = null;
            MachineGrid.ItemsSource = await _queryService.GetMachineRowsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载机器数据失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>新增机器。</summary>
    private async void AddMachine_click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddMachineDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await LoadGridDataAsync();
    }

    /// <summary>编辑机器。</summary>
    private async void EditMachine_Click(object sender, RoutedEventArgs e)
    {
        if (MachineGrid.SelectedItem is not MachineManagementRowVm row)
        {
            MessageBox.Show("请先选中要编辑的机器。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new AddMachineDialog(row) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await LoadGridDataAsync();
    }

    /// <summary>删除机器（带确认）。</summary>
    private async void DeleteMachine_Click(object sender, RoutedEventArgs e)
    {
        if (MachineGrid.SelectedItem is not MachineManagementRowVm row)
        {
            MessageBox.Show("请先选中要删除的机器。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show($"确认删除机器【{row.Name}】吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _deleteService.DeleteMachineAsync(row.SourceId);
            await LoadGridDataAsync();
            MessageBox.Show("删除成功。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除机器失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
