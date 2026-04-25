using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Controller;
using System.Windows;
using System.Windows.Controls;
using AutomaticOnlineHostComputer.Domain.Models;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Views.Controller.Dialogs;

/// <summary>
/// 控制器新增/编辑弹窗逻辑。
/// </summary>
public partial class AddControllerDialog : Window
{
    private readonly ManagementInsertService _insertService;
    private readonly ManagementUpdateService _updateService;

    private readonly int? _editId;

    public AddControllerDialog()
    {
        InitializeComponent();
        var connectionString = DbSettingsProvider.GetConnectionString();
        //用连接字符串创建“新增服务”实例，后续用于插入数据。
        _insertService = new ManagementInsertService(connectionString);
        _updateService = new ManagementUpdateService(connectionString);
    }

    /// <summary>
    /// 编辑模式构造函数。
    /// </summary>
    public AddControllerDialog(ControllerManagementRowVm row) : this()
    {
        _editId = row.SourceId;
        Title = "编辑控制器";
        FillForm(row);
    }

    /// <summary>
    /// 保存（新增/编辑复用）。
    /// </summary>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var input = new AddControllerInput
            {
                Name = RequireText(NameTextBox.Text, "名称"),
                TypeName = RequireText(TypeTextBox.Text, "类型"),
                Ip = IpTextBox.Text.Trim(),
                Port = ParseInt(PortTextBox.Text, "端口"),
                DeviceNo = ParseInt(DeviceNoTextBox.Text, "设备号"),
                State = GetStateCode(StateComboBox)
            };
            
            if (_editId.HasValue)
            {
                await _updateService.UpdateControllerAsync(_editId.Value, input);
            }
            else
            {
                await _insertService.AddControllerAsync(input);
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存控制器失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FillForm(ControllerManagementRowVm row)
    {
        NameTextBox.Text = row.Name;
        TypeTextBox.Text = row.Type;
        IpTextBox.Text = row.Ip;
        PortTextBox.Text = row.Port.ToString();
        DeviceNoTextBox.Text = row.DeviceNo.ToString();
        SelectComboByText(StateComboBox, row.State);
    }

    private static void SelectComboByText(ComboBox comboBox, string text)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem cbItem && string.Equals(cbItem.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = cbItem;
                break;
            }
        }
    }

    private static int GetStateCode(ComboBox comboBox)
    {
        var text = (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "启用";
        return text switch
        {
            "启用" => 1,
            "检修" => 2,
            _ => 0
        };
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
