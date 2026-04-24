using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Infrastructure.Data;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Process;
using AutomaticOnlineHostComputer.Views.Process.Dialogs;
using System.Windows;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Process;

public partial class ProcessManagementView : UserControl
{
    private readonly ManagementQueryService _queryService;

    public ProcessManagementView()
    {
        InitializeComponent();
        var connectionString = DbSettingsProvider.GetConnectionString();
        _queryService = new ManagementQueryService(connectionString);
        Loaded += async (_, _) => await LoadGridDataAsync();
    }

    /// <summary>
    /// 异步加载工艺列表。
    /// </summary>
    private async Task LoadGridDataAsync()
    {
        try
        {
            ProcessGrid.ItemsSource = null;
            ProcessGrid.ItemsSource = await _queryService.GetProcessRowsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载工艺数据失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 新增工艺。
    /// </summary>
    private async void AddProcess_click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddProcessDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            await LoadGridDataAsync();
        }
    }

    /// <summary>
    /// 编辑工艺。
    /// </summary>
    private async void EditProcess_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessManagementRowVm row)
        {
            MessageBox.Show("请先选中要编辑的工艺。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new AddProcessDialog(row) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            await LoadGridDataAsync();
        }
    }
}
