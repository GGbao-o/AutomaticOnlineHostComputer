using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Views.Home.Dialogs;

/// <summary>
/// 主页面“清空状态”窗口。
/// 此窗口只清目标设备中由上位机写入的寄存器，不清任务、缓存、锁或引擎状态。
/// 需要处理软件在途状态或释放锁时，应使用主页面“应急处理”窗口。
/// </summary>
public partial class ClearMachineStatusDialog : Window
{
    private readonly Dictionary<string, string> _stationIps;
    private readonly Func<int, string?> _getBoringClearBlockReason;

    // 即使操作员快速点击不同按钮，也只允许一个设备清理在途。
    // 设备清理会建立连接并连续写多个寄存器，并行执行不仅难以判断日志，也可能与共享通信资源竞争。
    private int _clearInProgress;

    /// <param name="stationIps">站号→IP映射, 从HomeViewModel传入</param>
    /// <param name="getBoringClearBlockReason">按线体查询前端软件在途状态；非空时禁止普通双头镗清零。</param>
    public ClearMachineStatusDialog(
        Dictionary<string, string> stationIps,
        Func<int, string?> getBoringClearBlockReason)
    {
        InitializeComponent();
        _stationIps = stationIps;
        _getBoringClearBlockReason = getBoringClearBlockReason;
    }

    private void Log(string msg, bool isError = false)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        var para = new Paragraph(new Run($"[{time}] {msg}"));
        if (isError) para.Foreground = Brushes.Red;
        TxtLog.Document.Blocks.Add(para);
        TxtLog.ScrollToEnd();
    }

    private string? Ip(string code) => _stationIps.TryGetValue(code, out string? ip) ? ip : null;

    /// <summary>
    /// 单设备清理统一入口：确认目标、阻止并行点击、禁用当前按钮、记录完整结果。
    /// 操作失败不会自动改为“仅清软件”，因为本窗口本来就不管理软件状态。
    /// </summary>
    private async Task RunSingleDeviceClearAsync(
        Button button,
        string code,
        string deviceName,
        string registerText,
        Func<string, Task> clearAction,
        string? additionalWarning = null)
    {
        string? ip = Ip(code);
        if (string.IsNullOrWhiteSpace(ip))
        {
            Log($"✘ {deviceName} ({code}): 未配置IP", true);
            return;
        }

        string prompt =
            $"确认只清空这一台设备？\n\n" +
            $"设备：{deviceName}\n" +
            $"站号：{code}\n" +
            $"IP：{ip}\n" +
            $"清理：{registerText}\n\n" +
            "只清上位机写入的设备数据，不清任务、缓存、锁或引擎状态。\n" +
            "请确认对应引擎已经停止或暂停，且设备没有正在执行的动作。" +
            (string.IsNullOrWhiteSpace(additionalWarning) ? "" : $"\n\n{additionalWarning}");

        if (MessageBox.Show(this, prompt, $"确认清空 {code}",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (Interlocked.CompareExchange(ref _clearInProgress, 1, 0) != 0)
        {
            Log($"⚠ 已有设备正在清空，本次 {code} 操作未执行", true);
            return;
        }

        button.IsEnabled = false;
        try
        {
            Log($"▶ {deviceName} ({code}) {ip} 开始清空：{registerText}");
            await clearAction(ip);
            Log($"✔ {deviceName} ({code}) 清空完成：{registerText}");
        }
        catch (Exception ex)
        {
            Log($"✘ {deviceName} ({code}) 清空失败：{ex.Message}", true);
            MessageBox.Show(this,
                $"{deviceName}（{code}）清空失败。\n\n{ex.Message}\n\n设备数据可能只完成了部分清零，请检查日志和现场状态后再决定是否重试。",
                $"清空 {code} 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.IsEnabled = true;
            Interlocked.Exchange(ref _clearInProgress, 0);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 双头镗：保留原有两个独立按钮和原有清理范围。
    // 不清R6101/R6103/R6107等CNC→上位机输入，也不改变双头镗状态机。
    // ═══════════════════════════════════════════════════════════════

    private async void BtnBoring1_Click(object sender, RoutedEventArgs e)
        => await ClearBoringAsync(BtnBoring1, 1, "ST103", "1号线双头镗");

    private async void BtnBoring2_Click(object sender, RoutedEventArgs e)
        => await ClearBoringAsync(BtnBoring2, 2, "ST402", "2号线双头镗");

    private async Task ClearBoringAsync(Button button, int line, string code, string name)
    {
        string? blockReason = _getBoringClearBlockReason(line);
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            string message = blockReason +
                "\n\n普通清零只会清设备R6102/R6104/R6108，不会恢复_currentWp、_forkPhase或门闩。" +
                "\n有在途工件时禁止单独使用。请进入“应急处理中心 → 前端在途应急”处理。";
            Log($"✘ {name} ({code}) 普通清零已阻止：{blockReason}", true);
            MessageBox.Show(this, message, $"禁止清空 {code}", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await RunSingleDeviceClearAsync(button, code, name,
            "R6102/R6104/R6108→0",
            ip => ClearBoringDeviceAsync(name, ip),
            "特别注意：本按钮只清设备寄存器，软件阶段不会恢复；有在途工件时禁止单独使用。需要处理_currentWp、_forkPhase或门闩时，请使用“前端在途应急”。");
    }

    private static async Task ClearBoringDeviceAsync(string name, string ip)
    {
        using var svc = new BoringModbusService(name, ip);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await svc.ConnectAsync(cts.Token);

        // 服务层统一保持现场既定清理顺序：下料完成→上料完成→数据完成。
        await svc.ClearEmergencyOutputsAsync(cts.Token);
    }

    // ═══════════════════════════════════════════════════════════════
    // 斜床：按钮Tag就是唯一目标站号。ST108~111为锦州Modbus，其余为FANUC。
    // 每次只创建目标设备服务，并调用服务层统一维护的应急寄存器清零。
    // ═══════════════════════════════════════════════════════════════

    private async void SkewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string code) return;

        bool isModbus = code is "ST108" or "ST109" or "ST110" or "ST111";
        string deviceName = isModbus ? "锦州斜床" : "FANUC斜床";
        string registers = isModbus
            ? "10300~10305参数、10370~10374命令→0"
            : "#800/#801/#802/#909/#1101~#1105→0";

        await RunSingleDeviceClearAsync(button, code, deviceName, registers,
            ip => isModbus
                ? ClearModbusSkewDeviceAsync(ip)
                : ClearFanucSkewDeviceAsync(ip));
    }

    private static async Task ClearModbusSkewDeviceAsync(string ip)
    {
        using var svc = new ModbusSkewBedService(ip);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await svc.ConnectAsync(cts.Token);

        // 服务层只写上位机拥有的参数/命令寄存器，所有值均清0；
        // 不再沿用旧页面“10374写1触发停止”的做法。
        await svc.ClearEmergencyRegistersAsync(cts.Token);
    }

    private static async Task ClearFanucSkewDeviceAsync(string ip)
    {
        using var svc = new FanucSkewBedService(ip);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await svc.ConnectAsync(cts.Token);

        // 包含加工参数和全部握手输出，不清#1000~#1014机床输入状态。
        await svc.ClearEmergencyRegistersAsync(cts.Token);
    }

    // ═══════════════════════════════════════════════════════════════
    // 研磨机：ST701/702为新代TypeB，ST703/704为西门子TypeA。
    // ClearEmergencyRegistersAsync同时清握手输出和加工参数，不再只清输出位。
    // ═══════════════════════════════════════════════════════════════

    private async void GrinderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string code) return;

        bool isTypeB = code is "ST701" or "ST702";
        PlcGrinderService.GrinderType type = isTypeB
            ? PlcGrinderService.GrinderType.TypeB
            : PlcGrinderService.GrinderType.TypeA;
        string deviceName = isTypeB ? "新代研磨机" : "西门子研磨机";
        string registers = isTypeB ? "R7311~R7318→0" : "40011~40014→0";

        await RunSingleDeviceClearAsync(button, code, deviceName, registers,
            ip => ClearGrinderDeviceAsync(code, ip, type));
    }

    private static async Task ClearGrinderDeviceAsync(
        string code,
        string ip,
        PlcGrinderService.GrinderType type)
    {
        using var svc = new PlcGrinderService(code, ip, type);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await svc.ConnectAsync(cts.Token);
        await svc.ClearEmergencyRegistersAsync(cts.Token);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
