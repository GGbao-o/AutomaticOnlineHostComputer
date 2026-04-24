using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Infrastructure.Data;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Machine;
using System.Windows;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Machine.Dialogs;

public partial class AddMachineDialog : Window
{
    private readonly ManagementCommandService _commandService;
    private readonly int? _editId;

    public AddMachineDialog()
    {
        InitializeComponent();
        _commandService = new ManagementCommandService(DbSettingsProvider.GetConnectionString());
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
                await _commandService.UpdateMachineAsync(_editId.Value, input);
            }
            else
            {
                await _commandService.AddMachineAsync(input);
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存机器失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private AddMachineInput BuildInput()
    {
        return new AddMachineInput
        {
            LineNo = GetLineNo(LineNoComboBox),
            StationCode = StationCodeTextBox.Text.Trim(),
            Name = RequireText(NameTextBox.Text, "机器名称"),
            TypeName = RequireText(TypeNameTextBox.Text, "机器类型"),
            AreaName = AreaNameTextBox.Text.Trim(),
            MachineNo = ParseInt(MachineNoTextBox.Text, "机号"),
            Ip = IpTextBox.Text.Trim(),
            Port = ParseInt(PortTextBox.Text, "端口"),
            X = ParseInt(XTextBox.Text, "X"),
            Y = ParseInt(YTextBox.Text, "Y"),
            Z = ParseInt(ZTextBox.Text, "Z"),
            XDis = XDisTextBox.Text.Trim(),
            YDis = YDisTextBox.Text.Trim(),
            ZDis = ZDisTextBox.Text.Trim(),
            DisShake = ParseInt(DisShakeTextBox.Text, "抖动"),
            State = GetStateCode(StateComboBox)
        };
    }

    private void FillForm(MachineManagementRowVm row)
    {
        SelectComboByText(LineNoComboBox, row.LineNo == 2 ? "2号线" : "1号线");
        StationCodeTextBox.Text = row.StationCode;
        NameTextBox.Text = row.Name;
        TypeNameTextBox.Text = row.TypeName;
        AreaNameTextBox.Text = row.AreaName;
        MachineNoTextBox.Text = row.MachineNo.ToString();
        IpTextBox.Text = row.Ip;
        PortTextBox.Text = row.Port.ToString();
        XTextBox.Text = row.X.ToString("0");
        YTextBox.Text = row.Y.ToString("0");
        ZTextBox.Text = row.Z.ToString("0");
        XDisTextBox.Text = row.XOffset.ToString("0");
        YDisTextBox.Text = row.YOffset.ToString("0");
        ZDisTextBox.Text = row.ZOffset.ToString("0");
        DisShakeTextBox.Text = row.Shake.ToString("0");
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

    private static int GetLineNo(ComboBox comboBox)
    {
        var text = (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "1号线";
        return text.Contains("2") ? 2 : 1;
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
