using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Infrastructure.Data;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Controller;
using AutomaticOnlineHostComputer.Views.Controller.Dialogs;
using System.Windows;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Controller;

public partial class ControllerManagementView : UserControl
{
    private readonly ManagementQueryService _queryService;

    public ControllerManagementView()
    {
        InitializeComponent();
        var connectionString = DbSettingsProvider.GetConnectionString();
        _queryService = new ManagementQueryService(connectionString);
        Loaded += async (_, _) => await LoadGridDataAsync();
    }

    /// <summary>
    /// 异步加载控制器列表。
    /// </summary>
    private async Task LoadGridDataAsync()
    {
        try
        {
            ControllerGrid.ItemsSource = null;
            ControllerGrid.ItemsSource = await _queryService.GetControllerRowsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载控制器数据失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 新增控制器。
    /// </summary>
    private async void AddController_click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddControllerDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            await LoadGridDataAsync();
        }
    }

    /// <summary>
    /// 编辑控制器。
    /// </summary>
    private async void EditController_Click(object sender, RoutedEventArgs e)
    {
        if (ControllerGrid.SelectedItem is not ControllerManagementRowVm row)
        {
            MessageBox.Show("请先选中要编辑的控制器。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new AddControllerDialog(row) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            await LoadGridDataAsync();
        }
    }
}
