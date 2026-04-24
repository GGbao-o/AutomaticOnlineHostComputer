using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Infrastructure.Data;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using AutomaticOnlineHostComputer.Views.Machine.Dialogs;
using System.Windows;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Machine;

public partial class MachineManagementView : UserControl
{
    private readonly ManagementQueryService _queryService;
    private readonly ManagementCommandService _commandService;

    public MachineManagementView()
    {
        InitializeComponent();

        var connectionString = DbSettingsProvider.GetConnectionString();
        _queryService = new ManagementQueryService(connectionString);
        _commandService = new ManagementCommandService(connectionString);

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

    /// <summary>
    /// 新增机器。
    /// </summary>
    private async void AddMachine_click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddMachineDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            await LoadGridDataAsync();
        }
    }

    /// <summary>
    /// 编辑机器。
    /// </summary>
    private async void EditMachine_Click(object sender, RoutedEventArgs e)
    {
        if (MachineGrid.SelectedItem is not MachineManagementRowVm row)
        {
            MessageBox.Show("请先选中要编辑的机器。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new AddMachineDialog(row) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            await LoadGridDataAsync();
        }
    }

    /// <summary>
    /// 删除机器（带确认）。
    /// </summary>
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
            await _commandService.DeleteMachineAsync(row.SourceId);
            await LoadGridDataAsync();
            MessageBox.Show("删除成功。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除机器失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
