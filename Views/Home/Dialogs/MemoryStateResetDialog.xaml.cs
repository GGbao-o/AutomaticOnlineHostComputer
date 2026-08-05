using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AutomaticOnlineHostComputer.Views.Home.Dialogs;

/// <summary>
/// 主页面“恢复内存状态”窗口。
/// 只改已确认会阻塞候选派发的上位机内存字段；不执行设备清零或应急结案。
/// </summary>
public partial class MemoryStateResetDialog : Window
{
    private readonly Func<int, string, string> _resetSkew;
    private readonly Func<string, string> _resetGrinding;
    private int _resetInProgress;

    public MemoryStateResetDialog(
        Func<int, string, string> resetSkew,
        Func<string, string> resetGrinding)
    {
        InitializeComponent();
        _resetSkew = resetSkew;
        _resetGrinding = resetGrinding;
    }

    private void Log(string message, bool isError = false)
    {
        var paragraph = new Paragraph(new Run($"[{DateTime.Now:HH:mm:ss}] {message}"));
        if (isError) paragraph.Foreground = Brushes.Red;
        TxtLog.Document.Blocks.Add(paragraph);
        TxtLog.ScrollToEnd();
    }

    private void Line1SkewButton_Click(object sender, RoutedEventArgs e)
        => ResetSkew(sender as Button, 1);

    private void Line2SkewButton_Click(object sender, RoutedEventArgs e)
        => ResetSkew(sender as Button, 2);

    private void ResetSkew(Button? button, int line)
    {
        if (button?.Tag is not string stationCode) return;

        const string changes = "St→Idle、Wp→null";
        string prompt =
            $"确认复位 {line}号线 {stationCode} 的上位机内存？\n\n" +
            $"将清除：{changes}。\n" +
            "不会写PLC/CNC，不会移动设备，不会清信号、快照、账本或锁。\n\n" +
            "仅在PLC已正常请求数据且后天车未派发时使用。";
        if (MessageBox.Show(this, prompt, $"确认恢复 {stationCode}",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        RunReset(button, $"{line}号线 {stationCode}", () => _resetSkew(line, stationCode));
    }

    private void GrinderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string stationCode) return;

        const string changes = "State→Idle、PendingWorkpiece→null";
        string prompt =
            $"确认复位 {stationCode} 的上位机内存？\n\n" +
            $"将清除：{changes}。\n" +
            "不会写PLC，不会移动设备，不会清信号、恢复标记、快照、账本或锁。\n\n" +
            "仅在PLC已正常请求数据且研磨天车未派发时使用。";
        if (MessageBox.Show(this, prompt, $"确认恢复 {stationCode}",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        RunReset(button, stationCode, () => _resetGrinding(stationCode));
    }

    private void RunReset(Button button, string target, Func<string> resetAction)
    {
        if (Interlocked.CompareExchange(ref _resetInProgress, 1, 0) != 0)
        {
            Log($"⚠ 已有内存复位正在执行，本次 {target} 未执行", true);
            return;
        }

        button.IsEnabled = false;
        try
        {
            string result = resetAction();
            Log($"✔ {result}");
        }
        catch (Exception ex)
        {
            Log($"✘ {target} 内存复位失败：{ex.Message}", true);
            MessageBox.Show(this, ex.Message, $"恢复 {target} 失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.IsEnabled = true;
            Interlocked.Exchange(ref _resetInProgress, 0);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
