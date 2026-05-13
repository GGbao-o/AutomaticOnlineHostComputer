using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using System.Windows;
using System.Windows.Controls;
using AutomaticOnlineHostComputer.Domain.Models;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Views.Machine.Dialogs;

public partial class AddMachineDialog : Window
{
    private readonly ManagementInsertService _insertService;
    private readonly ManagementUpdateService _updateService;

    private readonly int? _editId;

    public AddMachineDialog()
    {
        InitializeComponent();
        var connectionString = DbSettingsProvider.GetConnectionString();
        _insertService = new ManagementInsertService(connectionString);
        _updateService = new ManagementUpdateService(connectionString);
    }

    /// <summary>
    /// 编辑模式构造函数。
    /// </summary>
    public AddMachineDialog(MachineManagementRowVm row) : this()
    {
        _editId = row.SourceId;
        Title = "编辑机器";
        FillForm(row);
    }

    /// <summary>
    /// 保存（新增/编辑复用）。
    /// </summary>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var input = BuildInput();
            if (_editId.HasValue)
            {
                await _updateService.UpdateMachineAsync(_editId.Value, input);
            }
            else
            {
                await _insertService.AddMachineAsync(input);
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存机器失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 将界面字段转换为机器输入模型。
    /// </summary>
    private AddMachineInput BuildInput()
    {
        return new AddMachineInput
        {
            LineNo = GetLineNo(LineNoComboBox),
            StationCode = string.Empty,
            Name = RequireText(NameTextBox.Text, "机器名称"),
            TypeName = GetComboText(TypeNameComboBox),
            AreaName = string.Empty,
            MachineNo = 0,
            X = ParseInt(XTextBox.Text, "X坐标"),
            Y = ParseInt(YTextBox.Text, "Y坐标"),
            Z = ParseInt(ZTextBox.Text, "Z坐标"),
            SafeZDown = ParseInt(DownZTextBox.Text, "下降Z坐标"),
            SafeZUp = ParseInt(UpZTextBox.Text, "上升Z坐标"),
            AbsolutePos = ParseInt(AbsolutePosTextBox.Text, "绝对坐标"),
            XDis = XDisTextBox.Text.Trim(),
            YDis = YDisTextBox.Text.Trim(),
            ZDis = ZDisTextBox.Text.Trim(),
            DisShake = ParseInt(DisShakeTextBox.Text, "抖动距离"),
            ProcessRange = WorkRangeTextBox.Text.Trim(),
            CurrentX = ParseNullableInt(CurrentXTextBox.Text),
            CurrentY = ParseNullableInt(CurrentYTextBox.Text),
            CurrentZ = ParseNullableInt(CurrentZTextBox.Text),
            Ip = RequireText(IpTextBox.Text, "IP地址"),
            Port = ParseInt(PortTextBox.Text, "端口"),
            State = 1
        };
    }

    /// <summary>
    /// 编辑时回填页面。
    /// </summary>
    private void FillForm(MachineManagementRowVm row)
    {
        SelectComboByText(LineNoComboBox, row.LineNo == 2 ? "二号线" : "一号线");
        NameTextBox.Text = row.Name;
        SelectComboByText(TypeNameComboBox, row.TypeName);
        XTextBox.Text = row.X.ToString("0");
        YTextBox.Text = row.Y.ToString("0");
        ZTextBox.Text = row.Z.ToString("0");
        DownZTextBox.Text = row.SafeZDown.ToString();
        UpZTextBox.Text = row.SafeZUp.ToString();
        AbsolutePosTextBox.Text = row.AbsolutePos.ToString();
        XDisTextBox.Text = row.XOffset.ToString("0");
        YDisTextBox.Text = row.YOffset.ToString("0");
        ZDisTextBox.Text = row.ZOffset.ToString("0");
        DisShakeTextBox.Text = row.Shake.ToString("0");
        WorkRangeTextBox.Text = row.ProcessRange;
        CurrentXTextBox.Text = row.CurrentX?.ToString() ?? "";
        CurrentYTextBox.Text = row.CurrentY?.ToString() ?? "";
        CurrentZTextBox.Text = row.CurrentZ?.ToString() ?? "";
        IpTextBox.Text = row.Ip ?? string.Empty;
        PortTextBox.Text = row.Port.ToString();
    }

    private static string GetComboText(ComboBox comboBox)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
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

    private static int GetLineNo(ComboBox comboBox)
    {
        var text = GetComboText(comboBox);
        return text.Contains("二") || text.Contains("2") ? 2 : 1;
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

    private static int? ParseNullableInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text, out var v) ? v : null;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
