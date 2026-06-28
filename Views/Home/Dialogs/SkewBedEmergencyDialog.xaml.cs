using System;
using System.Windows;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

namespace AutomaticOnlineHostComputer.Views.Home.Dialogs;

public partial class SkewBedEmergencyDialog : Window
{
    private readonly HomeViewModel _viewModel;

    private static readonly string[] Line1Beds = { "ST108", "ST109", "ST111", "ST110", "ST112" };
    private static readonly string[] Line2Beds = { "ST606", "ST607", "ST608", "ST609", "ST610" };

    public SkewBedEmergencyDialog(HomeViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        LineBox.ItemsSource = new[] { "1号线", "2号线" };
        LineBox.SelectedIndex = 0;
        RefreshBeds();
        RefreshInfo();
    }

    private int SelectedLine => LineBox.SelectedIndex == 1 ? 2 : 1;
    private string SelectedBed => BedBox.SelectedItem?.ToString() ?? (SelectedLine == 1 ? "ST108" : "ST606");

    private void LineBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RefreshBeds();
        RefreshInfo();
    }

    private void RefreshBeds()
    {
        BedBox.ItemsSource = SelectedLine == 1 ? Line1Beds : Line2Beds;
        BedBox.SelectedIndex = 0;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshInfo();

    private void RefreshInfo()
    {
        InfoBox.Text = _viewModel.GetSkewEmergencyInfo(SelectedLine, SelectedBed);
    }

    private async void EmergencyClear_Click(object sender, RoutedEventArgs e)
    {
        string prompt =
            "请确认现场已人工处理完成：\n\n" +
            "1. 天车/工件位置已确认安全。\n" +
            "2. 磁铁状态已由人工确认，不由本按钮控制。\n" +
            "3. 允许清机床参数/握手寄存器。\n" +
            "4. 允许清掉该斜床上位机缓存，并释放后天车/中转架相关软件锁。\n\n" +
            $"确认作废 {SelectedLine}号线 {SelectedBed} 吗？";

        if (MessageBox.Show(this, prompt, "确认斜床应急作废", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var result = await _viewModel.EmergencyClearSkewBedAsync(SelectedLine, SelectedBed, skipDeviceClear: false);
        if (!result.StartsWith("设备侧清零失败:", StringComparison.Ordinal))
        {
            InfoBox.Text = result;
            return;
        }

        string softwareOnlyPrompt =
            result + "\n\n" +
            "是否仅清上位机软件状态？\n\n" +
            "继续后必须人工确认机床参数/握手信号已经在设备侧清除，或该设备当天已放弃使用。";
        if (MessageBox.Show(this, softwareOnlyPrompt, "设备侧清零失败", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            InfoBox.Text = result + Environment.NewLine + "用户取消，仅保留原软件状态。";
            return;
        }

        InfoBox.Text = await _viewModel.EmergencyClearSkewBedAsync(SelectedLine, SelectedBed, skipDeviceClear: true);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
