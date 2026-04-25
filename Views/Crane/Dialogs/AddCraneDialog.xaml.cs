using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Crane;
using System.Windows;
using System.Windows.Controls;
using AutomaticOnlineHostComputer.Domain.Models;
using AutomaticOnlineHostComputer.Service;
namespace AutomaticOnlineHostComputer.Views.Crane.Dialogs;

public partial class AddCraneDialog : Window
{
    private readonly ManagementInsertService _insertService;
    private readonly ManagementUpdateService _updateService;

    private readonly int? _editId;

    public AddCraneDialog()
    {
        InitializeComponent();
        var connectionString = DbSettingsProvider.GetConnectionString();
        _insertService = new ManagementInsertService(connectionString);
        _updateService = new ManagementUpdateService(connectionString);
    }

    public AddCraneDialog(CraneManagementRowVm row) : this()
    {
        _editId = row.SourceId;
        Title = "编辑天车";
        FillForm(row);
    }
/// <summary>
/// 保存按钮
/// </summary>
/// <param name="sender"></param>
/// <param name="e"></param>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var input = BuildInput();
            if (_editId.HasValue)
            {
                await _updateService.UpdateCraneAsync(_editId.Value, input);
            }
            else
            {
                await _insertService.AddCraneAsync(input);
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存天车失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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
            AbsXOffset = ParseInt(AbsXOffsetTextBox.Text, "绝对位置偏移"),
            RatioX = ParseDouble(RatioXTextBox.Text, "X轴减速比"),
            RatioY = ParseDouble(RatioYTextBox.Text, "Y轴减速比"),
            RatioZ = ParseDouble(RatioZTextBox.Text, "Z轴减速比"),
            StartX = ParseLong(StartXTextBox.Text, "X轴起始位置"),
            EndX = ParseLong(EndXTextBox.Text, "X轴结束位置"),
            LimitZP = ParseLong(LimitZPTextBox.Text, "Z轴正限位"),
            LimitZN = ParseLong(LimitZNTextBox.Text, "Z轴负限位"),
            PulseX = ParseDouble(XPulseTextBox.Text, "X轴螺距"),
            WorkSta = GetStateCode()
        };
    }

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

        AbsXOffsetTextBox.Text = row.AbsXOffset.ToString();
        RatioXTextBox.Text = row.RatioX.ToString("0.###");
        RatioYTextBox.Text = row.RatioY.ToString("0.###");
        RatioZTextBox.Text = row.RatioZ.ToString("0.###");
        StartXTextBox.Text = row.StartX.ToString();
        EndXTextBox.Text = row.EndX.ToString();
        LimitZPTextBox.Text = row.LimitZP.ToString();
        LimitZNTextBox.Text = row.LimitZN.ToString();
        XPulseTextBox.Text = row.PulseX.ToString("0.###");

        SetStateByText(row.State);
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

    private int GetStateCode()
    {
        if (RepairRadio.IsChecked == true) return 2;
        if (DisableRadio.IsChecked == true) return 0;
        return 1;
    }

    private void SetStateByText(string stateText)
    {
        if (stateText == "检修")
        {
            RepairRadio.IsChecked = true;
        }
        else if (stateText == "停用")
        {
            DisableRadio.IsChecked = true;
        }
        else
        {
            EnableRadio.IsChecked = true;
        }
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

    private static double ParseDouble(string? text, string field)
    {
        if (!double.TryParse(text, out var value))
            throw new InvalidOperationException($"{field}必须是数字。");
        return value;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
