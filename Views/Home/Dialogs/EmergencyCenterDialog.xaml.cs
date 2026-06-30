using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

namespace AutomaticOnlineHostComputer.Views.Home.Dialogs;

public partial class EmergencyCenterDialog : Window
{
    private readonly HomeViewModel _viewModel;
    private static readonly string[] Line1Beds = { "ST108", "ST109", "ST111", "ST110", "ST112" };
    private static readonly string[] Line2Beds = { "ST606", "ST607", "ST608", "ST609", "ST610" };

    public EmergencyCenterDialog(HomeViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        ResetSkewBeds();
        RefreshAll();
    }

    private int SelectedSkewLine => int.Parse(((ComboBoxItem)SkewLineBox.SelectedItem).Tag?.ToString() ?? "1");
    private string SelectedSkewBed => SkewBedBox.SelectedItem?.ToString() ?? "";
    private string SelectedBalancePosition => ((ComboBoxItem)BalancePositionBox.SelectedItem).Tag?.ToString() ?? "";
    private string SelectedGrindingTarget => ((ComboBoxItem)GrindingTargetBox.SelectedItem).Tag?.ToString() ?? "";

    private void ResetSkewBeds()
    {
        SkewBedBox.ItemsSource = SelectedSkewLine == 1 ? Line1Beds : Line2Beds;
        SkewBedBox.SelectedIndex = 0;
    }

    private void RefreshAll()
    {
        RefreshSkewInfo();
        RefreshBalanceInfo();
        RefreshGrindingInfo();
    }

    private void RefreshSkewInfo()
    {
        if (!string.IsNullOrWhiteSpace(SelectedSkewBed))
            SkewInfoBox.Text = _viewModel.GetSkewEmergencyInfo(SelectedSkewLine, SelectedSkewBed);
    }

    private void RefreshBalanceInfo()
    {
        if (!string.IsNullOrWhiteSpace(SelectedBalancePosition))
            BalanceInfoBox.Text = _viewModel.GetBalancingEmergencyInfo(SelectedBalancePosition);
    }

    private void RefreshGrindingInfo()
    {
        if (!string.IsNullOrWhiteSpace(SelectedGrindingTarget))
            GrindingInfoBox.Text = _viewModel.GetGrindingEmergencyInfo(SelectedGrindingTarget);
    }

    private void Skew_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender == SkewLineBox) ResetSkewBeds();
        RefreshSkewInfo();
    }

    private void Balance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) RefreshBalanceInfo();
    }

    private void Grinding_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) RefreshGrindingInfo();
    }

    private void RefreshSkew_Click(object sender, RoutedEventArgs e) => RefreshSkewInfo();
    private void RefreshBalance_Click(object sender, RoutedEventArgs e) => RefreshBalanceInfo();
    private void RefreshGrinding_Click(object sender, RoutedEventArgs e) => RefreshGrindingInfo();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void ClearSkew_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                $"确认执行斜床应急？\n\n线体: {SelectedSkewLine}号线\n斜床: {SelectedSkewBed}\n\n请确认已人工处理磁铁、工件、天车安全位置。系统会先取消并等待旧动作退出，最多6秒；超时不会释放锁或清缓存。",
                "确认斜床应急", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var result = await _viewModel.EmergencyClearSkewBedAsync(SelectedSkewLine, SelectedSkewBed, skipDeviceClear: false);
            if (await HandleDeviceClearFailureAsync(result, () => _viewModel.EmergencyClearSkewBedAsync(SelectedSkewLine, SelectedSkewBed, skipDeviceClear: true), text => SkewInfoBox.Text = text))
                return;

            SkewInfoBox.Text = result;
            ShowEmergencyFailureIfNeeded("斜床应急失败", result);
        }
        catch (Exception ex)
        {
            ShowEmergencyException("斜床应急异常", ex, text => SkewInfoBox.Text = text);
        }
    }

    private async void ClearBalance_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                $"确认执行动平衡应急？\n\n位置: {SelectedBalancePosition}\n\n系统会先取消并等待旧动作退出，最多6秒；退出后才释放残留锁并清实际来源缓存。不会写PLC信号，也不会控制机械手/磁铁。",
                "确认动平衡应急", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var result = await _viewModel.EmergencyClearBalancingPositionAsync(SelectedBalancePosition);
            BalanceInfoBox.Text = result;
            ShowEmergencyFailureIfNeeded("动平衡应急失败", result);
        }
        catch (Exception ex)
        {
            ShowEmergencyException("动平衡应急异常", ex, text => BalanceInfoBox.Text = text);
        }
    }

    private async void ClearGrinding_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                $"确认执行研磨应急？\n\n目标: {SelectedGrindingTarget}\n\nST701~ST704会先暂停新派发并取消目标旧动作，最多等待6秒确认退出；超时不会清状态或释放锁。随后尝试清PLC输出/参数，成功后再清Pending/状态。ST709只清上位机FIFO队头。",
                "确认研磨应急", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        // 保存点击应急前的运行状态。第一次设备清零失败后引擎会保持暂停，
        // 二次“仅清软件”仍使用这个原始状态，成功后才能按原状态恢复派发。
        bool resumeAfterClear = _viewModel.IsGrindingRunning;
        try
        {
            var result = await _viewModel.EmergencyClearGrindingAsync(
                SelectedGrindingTarget, skipDeviceClear: false, resumeAfterClear);
            if (await HandleDeviceClearFailureAsync(result,
                    () => _viewModel.EmergencyClearGrindingAsync(
                        SelectedGrindingTarget, skipDeviceClear: true, resumeAfterClear),
                    text => GrindingInfoBox.Text = text))
                return;

            GrindingInfoBox.Text = result;
            ShowEmergencyFailureIfNeeded("研磨应急失败", result);
        }
        catch (Exception ex)
        {
            ShowEmergencyException("研磨应急异常", ex, text => GrindingInfoBox.Text = text);
        }
    }

    private async Task<bool> HandleDeviceClearFailureAsync(string result, Func<Task<string>> softwareOnlyAction, Action<string> setText)
    {
        if (!result.StartsWith("设备侧清零失败:", StringComparison.Ordinal))
            return false;

        Console.WriteLine($"[EmergencyCenter] 设备侧清零失败: {result.Replace(Environment.NewLine, " ")}");
        var ask = MessageBox.Show(this,
            result + "\n\n是否仅清上位机软件状态？\n继续后必须人工确认设备参数/握手信号已清除。",
            "设备侧清零失败", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ask != MessageBoxResult.Yes)
        {
            Console.WriteLine("[EmergencyCenter] 用户取消仅清软件，应急目标保持原软件状态/暂停状态");
            setText(result + "\n用户取消，仅保留原软件状态。");
            return true;
        }

        var softwareOnlyResult = await softwareOnlyAction();
        Console.WriteLine($"[EmergencyCenter] 用户确认仅清软件: {softwareOnlyResult.Replace(Environment.NewLine, " ")}");
        setText(softwareOnlyResult);
        ShowEmergencyFailureIfNeeded("仅清软件应急失败", softwareOnlyResult);
        return true;
    }

    private void ShowEmergencyFailureIfNeeded(string title, string result)
    {
        // 应急接口目前返回可读文本而不是结构化结果；统一兜底常见失败关键词，
        // 防止新增异常分支只显示在信息框、现场没有醒目的弹窗提示。
        bool failed = result.Contains("失败", StringComparison.Ordinal)
                      || result.Contains("未找到", StringComparison.Ordinal)
                      || result.Contains("超时", StringComparison.Ordinal)
                      || result.Contains("旧动作6秒内未退出", StringComparison.Ordinal)
                      || result.StartsWith("未知", StringComparison.Ordinal)
                      || result.StartsWith("无效", StringComparison.Ordinal);
        if (!failed) return;

        Console.WriteLine($"[EmergencyCenter] {title}: {result.Replace(Environment.NewLine, " ")}");
        MessageBox.Show(this, result, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ShowEmergencyException(string title, Exception ex, Action<string> setText)
    {
        string message = $"{title}: {ex.GetType().Name} - {ex.Message}";
        Console.WriteLine($"[EmergencyCenter] {message}");
        setText(message);
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
