using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Infrastructure.Data;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Crane;
using System.Windows;
using System.Windows.Controls;

namespace AutomaticOnlineHostComputer.Views.Crane.Dialogs;

public partial class AddCraneDialog : Window
{
    private readonly ManagementCommandService _commandService;
    private readonly int? _editId;

    /// <summary>
    /// 新增模式。
    /// </summary>
    public AddCraneDialog()
    {
        InitializeComponent();
        _commandService = new ManagementCommandService(DbSettingsProvider.GetConnectionString());
    }

    /// <summary>
    /// 编辑模式（回填数据）。
    /// </summary>
    public AddCraneDialog(CraneManagementRowVm row) : this()
    {
        _editId = row.SourceId;
        Title = "编辑天车";
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
                await _commandService.UpdateCraneAsync(_editId.Value, input);
            }
            else
            {
                await _commandService.AddCraneAsync(input);
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存天车失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 表单转输入模型。
    /// </summary>
    private AddCraneInput BuildInput()
    {
        return new AddCraneInput
        {
            LineNo = GetLineNo(LineNoComboBox),
            CraneNo = ParseInt(CraneNoTextBox.Text, "天车编号"),
            Name = RequireText(CraneNameTextBox.Text, "天车名称"),
            Ip = IpTextBox.Text.Trim(),
            Port = ParseInt(PortTextBox.Text, "端口"),
            EncoderIp = EncoderIpTextBox.Text.Trim(),
            EncoderPort = ParseInt(EncoderPortTextBox.Text, "编码器端口"),
            EncoderNo = ParseInt(EncoderNoTextBox.Text, "编码器机号"),
            OriginX = ParseLong(OriginXTextBox.Text, "原点X"),
            OriginY = ParseLong(OriginYTextBox.Text, "原点Y"),
            OriginZ = ParseLong(OriginZTextBox.Text, "原点Z"),
            Width = ParseLong(WidthTextBox.Text, "天车宽度"),
            WorkSta = GetStateCode(WorkStaComboBox)
        };
    }

    /// <summary>
    /// 编辑模式回填。
    /// </summary>
    private void FillForm(CraneManagementRowVm row)
    {
        SelectComboByText(LineNoComboBox, row.LineNo == 2 ? "2号线" : "1号线");
        CraneNoTextBox.Text = row.CraneNo.ToString();
        CraneNameTextBox.Text = row.Name;
        IpTextBox.Text = row.Ip;
        PortTextBox.Text = row.Port.ToString();
        EncoderIpTextBox.Text = row.EncoderIp;
        EncoderPortTextBox.Text = row.EncoderPort.ToString();
        EncoderNoTextBox.Text = row.EncoderNo.ToString();
        OriginXTextBox.Text = row.X.ToString("0");
        OriginYTextBox.Text = row.Y.ToString("0");
        OriginZTextBox.Text = row.Z.ToString("0");
        WidthTextBox.Text = row.Width.ToString("0");
        SelectComboByText(WorkStaComboBox, row.State);
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

    private static long ParseLong(string? text, string field)
    {
        if (!long.TryParse(text, out var value))
            throw new InvalidOperationException($"{field}必须是整数。");
        return value;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
