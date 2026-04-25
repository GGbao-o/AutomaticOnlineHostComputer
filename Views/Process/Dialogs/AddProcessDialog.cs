using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Process;
using System.Windows;
using AutomaticOnlineHostComputer.Domain.Models;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Views.Process.Dialogs;

/// <summary>
/// 工艺新增/编辑弹窗逻辑。
/// </summary>
public partial class AddProcessDialog : Window
{
    private readonly ManagementQueryService _queryService;
    private readonly ManagementInsertService _insertService;
    private readonly ManagementUpdateService _updateService;
    private readonly ManagementDeleteService _deleteService;

    private readonly int? _editId;

    public AddProcessDialog()
    {
        InitializeComponent();
        _insertService = new ManagementInsertService(DbSettingsProvider.GetConnectionString());
        _updateService = new ManagementUpdateService(DbSettingsProvider.GetConnectionString());
    }

    /// <summary>
    /// 编辑模式构造函数。
    /// </summary>
    public AddProcessDialog(ProcessManagementRowVm row) : this()
    {
        _editId = row.SourceId;
        Title = "编辑工艺";
        FillForm(row);
    }

    /// <summary>
    /// 保存（新增/编辑复用）。
    /// </summary>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var input = new AddProcessInput
            {
                ProcessName = RequireText(ProcessNameTextBox.Text, "工艺名称"),
                StepNo = ParseInt(StepNoTextBox.Text, "工序编号"),
                StepName = RequireText(StepNameTextBox.Text, "工序名称"),
                ExecuteTime = ParseInt(ExecuteTimeTextBox.Text, "执行时间"),
                Device = RequireText(DeviceTextBox.Text, "使用设备"),
                MachineNo = ParseInt(MachineNoTextBox.Text, "机号"),
                SafePosition = SafePositionTextBox.Text.Trim(),
                Enabled = EnabledCheckBox.IsChecked == true,
                ZAxisBackHome = ZHomeCheckBox.IsChecked == true
            };

            if (_editId.HasValue)
            {
                await _updateService.UpdateProcessStepAsync(_editId.Value, input);
            }
            else
            {
                await _insertService.AddProcessStepAsync(input);
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存工艺失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FillForm(ProcessManagementRowVm row)
    {
        ProcessNameTextBox.Text = row.Name;
        StepNoTextBox.Text = row.StepNo.ToString();
        StepNameTextBox.Text = !string.IsNullOrWhiteSpace(row.StepName)
            ? row.StepName
            : (!string.IsNullOrWhiteSpace(row.Step1) ? row.Step1 : row.Step2);
        ExecuteTimeTextBox.Text = row.ExecuteTime.ToString();
        DeviceTextBox.Text = row.Device;
        MachineNoTextBox.Text = row.MachineNo.ToString();
        SafePositionTextBox.Text = row.SafePosition;
        EnabledCheckBox.IsChecked = row.Enabled;
        ZHomeCheckBox.IsChecked = row.ZAxisBackHome;
    }

    private static string RequireText(string? text, string field)
    {
        var value = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{field}不能为空。");
        return value;
    }

    private static int ParseInt(string? text, string field)
    {
        if (!int.TryParse(text, out var value))
            throw new InvalidOperationException($"{field}必须是整数。");
        return value;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
