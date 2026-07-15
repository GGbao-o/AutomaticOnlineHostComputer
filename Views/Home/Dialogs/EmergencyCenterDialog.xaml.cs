using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Views.Home.Dialogs;

public partial class EmergencyCenterDialog : Window
{
    private readonly HomeViewModel _viewModel;
    private readonly AttentionEventCenter _attentionEvents;
    // 页面级统一操作门：任一应急执行期间，其他应急按钮都不得再次进入底层。
    // 该门闩只防UI重复调用，不改变各引擎自己的应急、锁和超时规则。
    private int _emergencyActionInProgress;
    private static readonly string[] Line1Beds = { "ST108", "ST109", "ST111", "ST110", "ST112" };
    private static readonly string[] Line2Beds = { "ST606", "ST607", "ST608", "ST609", "ST610" };

    public EmergencyCenterDialog(HomeViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _attentionEvents = App.Services.GetRequiredService<AttentionEventCenter>();
        ResetSkewBeds();
        RefreshAll();
        Loaded += async (_, _) => await RefreshFrontInfoAsync();
    }

    private int SelectedFrontLine => int.Parse(((ComboBoxItem)FrontLineBox.SelectedItem).Tag?.ToString() ?? "1");
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

    private async Task RefreshFrontInfoAsync()
    {
        try
        {
            FrontInfoBox.Text = await _viewModel.GetFrontEmergencyInfoAsync(SelectedFrontLine);
        }
        catch (Exception ex)
        {
            ShowEmergencyException("前端实时诊断异常", ex, text => FrontInfoBox.Text = text);
        }
    }

    private async Task AppendFrontInfoAsync(int line)
    {
        try
        {
            FrontInfoBox.Text += "\n\n" + await _viewModel.GetFrontEmergencyInfoAsync(line);
            FrontInfoBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            FrontInfoBox.Text += $"\n\n刷新诊断失败: {ex.GetType().Name} - {ex.Message}";
        }
    }

    private async void Front_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await RefreshFrontInfoAsync();
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
    private async void RefreshFront_Click(object sender, RoutedEventArgs e) => await RefreshFrontInfoAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private bool TryBeginEmergencyAction()
    {
        if (Interlocked.CompareExchange(ref _emergencyActionInProgress, 1, 0) != 0)
        {
            MessageBox.Show(this, "另一项应急操作正在执行，请等待当前操作完成。", "操作进行中",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        SetEmergencyControlsEnabled(false);
        return true;
    }

    private void EndEmergencyAction()
    {
        SetEmergencyControlsEnabled(true);
        Interlocked.Exchange(ref _emergencyActionInProgress, 0);
    }

    private void SetEmergencyControlsEnabled(bool enabled)
    {
        ContinueFrontButton.IsEnabled = enabled;
        DiscardFrontButton.IsEnabled = enabled;
        ClearSkewButton.IsEnabled = enabled;
        ClearBalanceButton.IsEnabled = enabled;
        ClearGrindingButton.IsEnabled = enabled;
        FrontLineBox.IsEnabled = enabled;
        SkewLineBox.IsEnabled = enabled;
        SkewBedBox.IsEnabled = enabled;
        BalancePositionBox.IsEnabled = enabled;
        GrindingTargetBox.IsEnabled = enabled;
    }

    private async void ContinueFront_Click(object sender, RoutedEventArgs e)
    {
        int line = SelectedFrontLine;
        if (MessageBox.Show(this,
                $"确认允许{line}号线货叉继续当前板？\n\n" +
                "仅适用于：板已经稳定放在货叉待机位，但机械手安全退出或M801异常导致门闩未打开。\n\n" +
                "请人工确认：\n1. 板稳定在货叉，机械手不持件；\n2. 机械手Z已回0并在本线安全Y；\n3. 货叉、机械手、前天车均未运动，现场无人处于危险区。\n\n" +
                "系统还会实时复核M900/M901/M902、机械手Y/Z/持件状态并补发M801。不会自动移动设备，也不会自动恢复线体。",
                "确认前端当前板继续", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (!TryBeginEmergencyAction()) return;
        try
        {
            string result = await _viewModel.EmergencyContinueFrontPlateAsync(line);
            RecordEmergency($"{line}号线", "前端在途继续", result);
            FrontInfoBox.Text = result;
            ShowEmergencyFailureIfNeeded("前端当前板继续失败", result);
            await AppendFrontInfoAsync(line);
        }
        catch (Exception ex)
        {
            RecordEmergencyException($"{line}号线", "前端在途继续", ex);
            ShowEmergencyException("前端当前板继续异常", ex, text => FrontInfoBox.Text = text);
        }
        finally { EndEmergencyAction(); }
    }

    private async void DiscardFront_Click(object sender, RoutedEventArgs e)
    {
        int line = SelectedFrontLine;
        if (MessageBox.Show(this,
                $"确认丢弃{line}号线前端当前工件并回Idle？\n\n" +
                "请先人工确认工件已经移走或确定报废，机械手/货叉/双头镗/前天车均停止，磁铁和现场人员安全。\n\n" +
                "系统会先清货叉M911~M914和双头镗R6108/R6104/R6102，全部成功后才清_currentWp、阶段和门闩。不会清缓存FIFO或前天车队列，也不会自动恢复线体。",
                "确认丢弃前端当前工件", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (!TryBeginEmergencyAction()) return;
        try
        {
            string result = await _viewModel.EmergencyDiscardFrontCurrentAsync(
                line, skipDeviceClear: false);
            RecordEmergency($"{line}号线", "前端当前工件丢弃", result);
            FrontInfoBox.Text = result;
            if (await HandleDeviceClearFailureAsync(result,
                    () => _viewModel.EmergencyDiscardFrontCurrentAsync(
                        line, skipDeviceClear: true),
                    text => FrontInfoBox.Text = text,
                    $"{line}号线", "前端当前工件丢弃"))
            {
                await AppendFrontInfoAsync(line);
                return;
            }

            ShowEmergencyFailureIfNeeded("前端当前工件丢弃失败", result);
            await AppendFrontInfoAsync(line);
        }
        catch (Exception ex)
        {
            RecordEmergencyException($"{line}号线", "前端当前工件丢弃", ex);
            ShowEmergencyException("前端当前工件丢弃异常", ex, text => FrontInfoBox.Text = text);
        }
        finally { EndEmergencyAction(); }
    }

    private async void ClearSkew_Click(object sender, RoutedEventArgs e)
    {
        int line = SelectedSkewLine;
        string bed = SelectedSkewBed;
        if (MessageBox.Show(this,
                $"确认执行斜床应急？\n\n线体: {line}号线\n斜床: {bed}\n\n请确认已人工处理磁铁、工件、天车安全位置。系统会先取消并等待旧动作退出，最多6秒；超时不会释放锁或清缓存。",
                "确认斜床应急", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (!TryBeginEmergencyAction()) return;
        try
        {
            var result = await _viewModel.EmergencyClearSkewBedAsync(line, bed, skipDeviceClear: false);
            RecordEmergency($"{line}号线", $"{bed}斜床应急", result);
            if (await HandleDeviceClearFailureAsync(result,
                    () => _viewModel.EmergencyClearSkewBedAsync(line, bed, skipDeviceClear: true),
                    text => SkewInfoBox.Text = text,
                    $"{line}号线", $"{bed}斜床应急"))
                return;

            SkewInfoBox.Text = result;
            ShowEmergencyFailureIfNeeded("斜床应急失败", result);
        }
        catch (Exception ex)
        {
            RecordEmergencyException($"{line}号线", $"{bed}斜床应急", ex);
            ShowEmergencyException("斜床应急异常", ex, text => SkewInfoBox.Text = text);
        }
        finally { EndEmergencyAction(); }
    }

    private async void ClearBalance_Click(object sender, RoutedEventArgs e)
    {
        string position = SelectedBalancePosition;
        if (MessageBox.Show(this,
                $"确认执行动平衡应急？\n\n位置: {position}\n\n系统会先取消并等待旧动作退出，最多6秒；退出后才释放残留锁并清实际来源缓存。不会写PLC信号，也不会控制机械手/磁铁。",
                "确认动平衡应急", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (!TryBeginEmergencyAction()) return;
        try
        {
            var result = await _viewModel.EmergencyClearBalancingPositionAsync(position);
            RecordEmergency("动平衡", $"{position}动平衡应急", result);
            BalanceInfoBox.Text = result;
            ShowEmergencyFailureIfNeeded("动平衡应急失败", result);
        }
        catch (Exception ex)
        {
            RecordEmergencyException("动平衡", $"{position}动平衡应急", ex);
            ShowEmergencyException("动平衡应急异常", ex, text => BalanceInfoBox.Text = text);
        }
        finally { EndEmergencyAction(); }
    }

    private async void ClearGrinding_Click(object sender, RoutedEventArgs e)
    {
        string target = SelectedGrindingTarget;
        if (MessageBox.Show(this,
                $"确认执行研磨应急？\n\n目标: {target}\n\nST701~ST704会先暂停新派发并取消目标旧动作，最多等待6秒确认退出；超时不会清状态或释放锁。随后尝试清PLC输出/参数，成功后再清Pending/状态。ST709只清上位机FIFO队头。",
                "确认研磨应急", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        // 保存点击应急前的运行状态。第一次设备清零失败后引擎会保持暂停，
        // 二次“仅清软件”仍使用这个原始状态，成功后才能按原状态恢复派发。
        bool resumeAfterClear = _viewModel.IsGrindingRunning;
        if (!TryBeginEmergencyAction()) return;
        try
        {
            var result = await _viewModel.EmergencyClearGrindingAsync(
                target, skipDeviceClear: false, resumeAfterClear);
            RecordEmergency("研磨", $"{target}研磨应急", result);
            if (await HandleDeviceClearFailureAsync(result,
                    () => _viewModel.EmergencyClearGrindingAsync(
                        target, skipDeviceClear: true, resumeAfterClear),
                    text => GrindingInfoBox.Text = text,
                    "研磨", $"{target}研磨应急"))
                return;

            GrindingInfoBox.Text = result;
            ShowEmergencyFailureIfNeeded("研磨应急失败", result);
        }
        catch (Exception ex)
        {
            RecordEmergencyException("研磨", $"{target}研磨应急", ex);
            ShowEmergencyException("研磨应急异常", ex, text => GrindingInfoBox.Text = text);
        }
        finally { EndEmergencyAction(); }
    }

    private async Task<bool> HandleDeviceClearFailureAsync(string result, Func<Task<string>> softwareOnlyAction,
        Action<string> setText, string scope, string source)
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
        RecordEmergency(scope, source, softwareOnlyResult, softwareOnly: true);
        Console.WriteLine($"[EmergencyCenter] 用户确认仅清软件: {softwareOnlyResult.Replace(Environment.NewLine, " ")}");
        setText(softwareOnlyResult);
        ShowEmergencyFailureIfNeeded("仅清软件应急失败", softwareOnlyResult);
        return true;
    }

    private void ShowEmergencyFailureIfNeeded(string title, string result)
    {
        // 应急接口目前返回可读文本而不是结构化结果；统一兜底常见失败关键词，
        // 防止新增异常分支只显示在信息框、现场没有醒目的弹窗提示。
        bool failed = IsEmergencyFailure(result);
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

    private static bool IsEmergencyFailure(string result)
        => result.Contains("失败", StringComparison.Ordinal)
           || result.Contains("拒绝", StringComparison.Ordinal)
           || result.Contains("禁止", StringComparison.Ordinal)
           || result.Contains("未找到", StringComparison.Ordinal)
           || result.Contains("超时", StringComparison.Ordinal)
           || result.Contains("旧动作6秒内未退出", StringComparison.Ordinal)
           || result.StartsWith("未知", StringComparison.Ordinal)
           || result.StartsWith("无效", StringComparison.Ordinal);

    private void RecordEmergency(string scope, string source, string message, bool softwareOnly = false)
    {
        bool failed = IsEmergencyFailure(message);
        string result = softwareOnly
            ? failed ? "仅清软件失败" : "仅清软件"
            : failed ? "失败" : "成功";
        _attentionEvents.Record(AttentionEventKind.Emergency, scope, source, message, result);
    }

    private void RecordEmergencyException(string scope, string source, Exception ex)
        => _attentionEvents.Record(AttentionEventKind.Emergency, scope, source,
            $"{ex.GetType().Name} - {ex.Message}", "失败");
}
