using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Infrastructure.Data;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Crane;
using AutomaticOnlineHostComputer.Views.Crane.Dialogs;
using System.Windows;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Crane;

public partial class CraneManagementView : UserControl
{
    private readonly ManagementQueryService _queryService;
    private readonly ManagementCommandService _commandService;

    public CraneManagementView()
    {
        InitializeComponent();
        var connectionString = DbSettingsProvider.GetConnectionString();
        _queryService = new ManagementQueryService(connectionString);
        _commandService = new ManagementCommandService(connectionString);
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
            await _commandService.DeleteCraneAsync(row.SourceId);
            await LoadGridDataAsync();
            MessageBox.Show("删除成功。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除天车失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
