using AutomaticOnlineHostComputer.Presentation.ViewModels.Controller;
using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Views.Controller.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Controller;

/*
 * 改造说明：与 MachineManagementView 一致——
 * 服务从 App.Services DI 容器获取，不再手动 new + 读取配置文件。
 */

public partial class ControllerManagementView : UserControl
{
    private readonly ManagementQueryService _queryService;
    private readonly ManagementDeleteService _deleteService;

    public ControllerManagementView()
    {
        InitializeComponent();

        _queryService  = App.Services.GetRequiredService<ManagementQueryService>();
        _deleteService = App.Services.GetRequiredService<ManagementDeleteService>();

        Loaded += async (_, _) => await LoadGridDataAsync();
    }

    /// <summary>异步加载控制器列表。</summary>
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

    /// <summary>新增控制器。</summary>
    private async void AddController_click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddControllerDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await LoadGridDataAsync();
    }

    /// <summary>编辑控制器。</summary>
    private async void EditController_Click(object sender, RoutedEventArgs e)
    {
        if (ControllerGrid.SelectedItem is not ControllerManagementRowVm row)
        {
            MessageBox.Show("请先选中要编辑的控制器。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new AddControllerDialog(row) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await LoadGridDataAsync();
    }

    /// <summary>删除控制器（带确认）。</summary>
    private async void DeleteController_Click(object sender, RoutedEventArgs e)
    {
        if (ControllerGrid.SelectedItem is not ControllerManagementRowVm row)
        {
            MessageBox.Show("请先选中要删除的控制器。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show($"确认删除控制器【{row.Name}】吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _deleteService.DeleteControllerAsync(row.SourceId);
            await LoadGridDataAsync();
            MessageBox.Show("删除成功。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除控制器失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
