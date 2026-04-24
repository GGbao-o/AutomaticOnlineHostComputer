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

    public MachineManagementView()
    {
        InitializeComponent();
        var connectionString = DbSettingsProvider.GetConnectionString();
        _queryService = new ManagementQueryService(connectionString);
        Loaded += async (_, _) => await LoadGridDataAsync();
    }

    /// <summary>
    /// 异步加载机器列表。
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
}
