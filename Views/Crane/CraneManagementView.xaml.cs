using AutomaticOnlineHostComputer.Presentation.ViewModels.Crane;
using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Views.Crane.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Crane;

public partial class CraneManagementView : UserControl
{
    private readonly ManagementQueryService _queryService;
    private readonly ManagementDeleteService _deleteService;

    public CraneManagementView()
    {
        InitializeComponent();
        // 从全局 DI 容器获取服务，不再手动 new + 读取配置文件
        _queryService  = App.Services.GetRequiredService<ManagementQueryService>();
        _deleteService = App.Services.GetRequiredService<ManagementDeleteService>();
        Loaded += async (_, _) => await LoadGridDataAsync();
    }

    /// <summary>
    /// 异步加载天车列表。
    /// </summary>
    private async Task LoadGridDataAsync()
    {
        try
        {
            CraneGrid.ItemsSource = null;
            CraneGrid.ItemsSource = await _queryService.GetCraneRowsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载天车数据失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 新增天车。
    /// </summary>
    private async void AddCrane_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddCraneDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            await LoadGridDataAsync();
        }
    }

    /// <summary>
    /// 编辑天车。
    /// </summary>
    private async void EditCrane_Click(object sender, RoutedEventArgs e)
    {
        if (CraneGrid.SelectedItem is not CraneManagementRowVm row)
        {
            MessageBox.Show("请先选中要编辑的天车。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new AddCraneDialog(row) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            await LoadGridDataAsync();
        }
    }

    /// <summary>
    /// 删除天车（带确认）。
    /// </summary>
    private async void DeleteCrane_Click(object sender, RoutedEventArgs e)
    {
        if (CraneGrid.SelectedItem is not CraneManagementRowVm row)
        {
            MessageBox.Show("请先选中要删除的天车。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show($"确认删除天车【{row.Name}】吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _deleteService.DeleteCraneAsync(row.SourceId);
            await LoadGridDataAsync();
            MessageBox.Show("删除成功。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除天车失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
