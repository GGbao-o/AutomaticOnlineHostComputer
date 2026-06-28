using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Views.Home.Dialogs;

public partial class ClearMachineStatusDialog : Window
{
    private readonly Dictionary<string, string> _stationIps;

    /// <param name="stationIps">站号→IP映射, 从HomeViewModel传入</param>
    public ClearMachineStatusDialog(Dictionary<string, string> stationIps)
    {
        InitializeComponent();
        _stationIps = stationIps;
    }

    private void Log(string msg, bool isError = false)
    {
        var time = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{time}] {msg}";
        var para = new Paragraph(new Run(line));
        if (isError) para.Foreground = Brushes.Red;
        TxtLog.Document.Blocks.Add(para);
        TxtLog.ScrollToEnd();
    }

    private string? Ip(string code) => _stationIps.TryGetValue(code, out var ip) ? ip : null;

    // ═══════════════════════════════════════════════════════════════
    //  双头镗 清空 R6102/R6104/R6108（上位机写入的握手寄存器）
    //
    //  ⚠ 注意事项:
    //    1. 不清 R6101 — R6101 是 CNC→上位机 的只读请求信号, 上位机不应改写
    //    2. 不清 R2041/R2043/R2044/R2045/R2047 — 加工参数, 下次加工前会被覆盖
    //    3. 必须在引擎停止(暂停)状态下使用 — 引擎正在握手中会信号冲突, 导致状态机异常
    //    4. 清空后双头镗侧握手信号归零, 下个工件从 ① 等 R6101=1 重新开始
    //    5. 正常流程只写1不清零；这里写0仅用于人工异常清理
    // ═══════════════════════════════════════════════════════════════

    private async void BtnBoring1_Click(object sender, RoutedEventArgs e)
        => await ClearBoringAsync("ST103", "1号线双头镗");

    private async void BtnBoring2_Click(object sender, RoutedEventArgs e)
        => await ClearBoringAsync("ST402", "2号线双头镗");

    private async Task ClearBoringAsync(string code, string name)
    {
        try
        {
            var ip = Ip(code);
            if (string.IsNullOrWhiteSpace(ip)) { Log($"✘ {name} ({code}): 未配置IP", true); return; }

            Log($"▶ {name} ({code}) {ip} 开始清空 R6102/R6104/R6108 ...");
            using var svc = new BoringModbusService(name, ip);
            using var cts = new CancellationTokenSource(5000);
            await svc.ConnectAsync(cts.Token);

            // 人工清理: 只清上位机写入的三个完成位。
            await svc.WriteRAsync(6108, 0, cts.Token);
            await svc.WriteRAsync(6104, 0, cts.Token);
            await svc.WriteRAsync(6102, 0, cts.Token);

            Log($"✔ {name} ({code}) R6102/R6104/R6108 → 0");
        }
        catch (Exception ex) { Log($"✘ {name} ({code}): {ex.Message}", true); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  锦州斜床 ST108~ST111 清空 10370~10374（上位机写入的全部寄存器）
    //
    //  ⚠ 注意事项:
    //    1. 10370(加工方式) → 0
    //    2. 10371(尾座夹紧) → 0,  10372(远程启动) → 0
    //    3. 10373(尾座张开) → 0,  10374(停止尾座) → 1(脉冲, PLC自动复位)
    //    4. 清空后斜床回到初始状态, 引擎会检测 Idle 并在下轮重新触发上料
    //    5. 必须在引擎停止时使用
    // ═══════════════════════════════════════════════════════════════

    private async void BtnSkewModbus_Click(object sender, RoutedEventArgs e)
        => await ClearModbusSkewAsync();

    private async Task ClearModbusSkewAsync()
    {
        var codes = new[] { "ST108", "ST109", "ST111", "ST110" };
        foreach (var code in codes)
        {
            var ip = Ip(code);
            if (string.IsNullOrWhiteSpace(ip)) { Log($"  {code}: 无IP,跳过"); continue; }

            try
            {
                Log($"▶ {code} {ip} ...");
                using var svc = new ModbusSkewBedService(ip);
                using var cts = new CancellationTokenSource(5000);
                await svc.ConnectAsync(cts.Token);

                await svc.ClearMachiningModeAsync(cts.Token);      // 10370=0
                await svc.ClearTailstockClampAsync(cts.Token);     // 10371=0
                await svc.ClearRemoteStartAsync(cts.Token);        // 10372=0
                await svc.ClearTailstockOpenAsync(cts.Token);      // 10373=0
                await svc.StopTailstockAsync(cts.Token);           // 10374=1→PLC自动复位, 这里写1触发停止(不写0)

                Log($"  ✔ {code} 10370~10374 已清空");
            }
            catch (Exception ex) { Log($"  ✘ {code}: {ex.Message}", true); }
        }
        Log("✔ 锦州斜床处理完成");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ST112 + 2号线 FANUC 斜床 清空 #1101~#1105（上位机写入的全部宏变量）
    //
    //  ⚠ 注意事项:
    //    1. FANUC宏变量编号: #1101(上料到位) #1102(上料完成) #1103(下料到位) #1104(下料完成) #1105(预留)
    //    2. 使用 SafeSetMacro 而非直接写, 内部有 -8 自动重连和 sem 锁保护
    //    3. Clear-Before-Set 顺序: 从后往前 (#1105→#1101)
    //    4. 不清 #1001~#1005(CNC→上位机只读信号)
    // ═══════════════════════════════════════════════════════════════

    private async void BtnSkewST112_Click(object sender, RoutedEventArgs e)
        => await ClearFanucSkewAsync(new[] { "ST112" }, "ST112 FANUC");

    // ═══════════════════════════════════════════════════════════════
    //  2号线 FANUC ST606~ST610
    // ═══════════════════════════════════════════════════════════════

    private async void BtnSkewLine2_Click(object sender, RoutedEventArgs e)
        => await ClearFanucSkewAsync(new[] { "ST606", "ST607", "ST608", "ST609", "ST610" },
            "2号线FANUC斜床");

    private async Task ClearFanucSkewAsync(string[] codes, string name)
    {
        foreach (var code in codes)
        {
            var ip = Ip(code);
            if (string.IsNullOrWhiteSpace(ip)) { Log($"  {code}: 无IP,跳过"); continue; }

            try
            {
                Log($"▶ {code} {ip} ...");
                using var svc = new FanucSkewBedService(ip);
                using var cts = new CancellationTokenSource(5000);
                await svc.ConnectAsync(cts.Token);

                // 级联清零: 从后往前
                svc.SafeSetMacro(1105, 0);
                svc.SafeSetMacro(1104, 0);
                svc.SafeSetMacro(1103, 0);
                svc.SafeSetMacro(1102, 0);
                svc.SafeSetMacro(1101, 0);

                Log($"  ✔ {code} #1101~#1105 → 0");
            }
            catch (Exception ex) { Log($"  ✘ {code}: {ex.Message}", true); }
        }
        Log($"✔ {name}处理完成");
    }

    // ═══════════════════════════════════════════════════════════════
    //  研磨机 清空所有输出信号
    //
    //  ⚠ 注意事项:
    //    1. TypeB(新代) ST701/ST702 和 TypeA(西门子) ST703/ST704 使用不同的PLC协议
    //    2. ClearAllOutputsAsync 内部根据类型发送不同的清空指令
    //    3. 4台研磨机互不干扰, 可独立清空
    // ═══════════════════════════════════════════════════════════════

    private async void BtnGrinderB_Click(object sender, RoutedEventArgs e)
        => await ClearGrinderAsync(new[] { "ST701", "ST702" }, PlcGrinderService.GrinderType.TypeB, "新代研磨机");

    // ═══════════════════════════════════════════════════════════════
    //  研磨机 TypeA 西门子
    // ═══════════════════════════════════════════════════════════════

    private async void BtnGrinderA_Click(object sender, RoutedEventArgs e)
        => await ClearGrinderAsync(new[] { "ST703", "ST704" }, PlcGrinderService.GrinderType.TypeA, "西门子研磨机");

    private async Task ClearGrinderAsync(string[] codes, PlcGrinderService.GrinderType type, string name)
    {
        foreach (var code in codes)
        {
            var ip = Ip(code);
            if (string.IsNullOrWhiteSpace(ip)) { Log($"  {code}: 无IP,跳过"); continue; }

            try
            {
                Log($"▶ {code} {ip} ({name}) ...");
                using var svc = new PlcGrinderService(code, ip, type);
                using var cts = new CancellationTokenSource(5000);
                await svc.ConnectAsync(cts.Token);
                await svc.ClearAllOutputsAsync(cts.Token);
                Log($"  ✔ {code} 输出信号已清空");
            }
            catch (Exception ex) { Log($"  ✘ {code}: {ex.Message}", true); }
        }
        Log($"✔ {name}处理完成");
    }

    // ═══════════════════════════════════════════════════════════════
    //  一键全清 — 依次清空全部设备, 每步独立 try-catch 互不影响
    //
    //  ⚠ 使用前必须:
    //    1. 确认所有引擎已停止(暂停)
    //    2. 确认所有设备已停止运行(无加工中工件)
    //    3. 确认无天车/机械手正在移动
    //
    //  ⚠ 清空顺序:
    //    双头镗 → 锦州斜床 → FANUC斜床 → 研磨机
    //    先清 CNC 侧(握手信号), 再清 PLC 侧(动作信号)
    //
    //  ⚠ 清空后所有上位机→设备信号归零, 引擎需重新启动才能恢复自动化
    // ═══════════════════════════════════════════════════════════════

    private async void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("确认清空全部机器状态？\n\n这将清零所有上位机写入的信号。\n\n⚠ 请确认:\n① 所有引擎已停止\n② 所有设备已停止运行\n③ 无天车/机械手正在移动",
            "确认一键全清", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        BtnClearAll.IsEnabled = false;
        Log("══════ 开始一键全清 ══════");

        await ClearBoringAsync("ST103", "1号线双头镗");
        await ClearBoringAsync("ST402", "2号线双头镗");
        await ClearModbusSkewAsync();
        await ClearFanucSkewAsync(new[] { "ST112" }, "ST112 FANUC");
        await ClearFanucSkewAsync(new[] { "ST606", "ST607", "ST608", "ST609", "ST610" }, "2号线FANUC");
        await ClearGrinderAsync(new[] { "ST701", "ST702" }, PlcGrinderService.GrinderType.TypeB, "新代研磨机");
        await ClearGrinderAsync(new[] { "ST703", "ST704" }, PlcGrinderService.GrinderType.TypeA, "西门子研磨机");

        Log("══════ 一键全清完成 ══════");
        BtnClearAll.IsEnabled = true;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
