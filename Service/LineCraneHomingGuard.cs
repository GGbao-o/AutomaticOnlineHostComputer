using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>复制一次实时读取的三个原始Word值；读失败保持未知，不能伪装成0或沿用页面旧状态。</summary>
internal sealed record CraneHomingResult(int CraneNo, string Name, short? X, short? Y, short? Z, string? Error)
{
    // 本轮按用户要求严格等于1，不能使用协议旧说明中的“非0即完成”。
    internal bool IsReady => Error == null && X == 1 && Y == 1 && Z == 1;

    internal string Describe()
    {
        string Axis(string axis, int address, short? value)
            => $"  {axis} D{address}={value?.ToString() ?? "未知"}  {(value == 1 ? "✓" : "✗（必须等于1）")}";
        return $"{Name}（{CraneNo}号天车）\n" +
            (Error == null ? "" : $"  读取失败：{Error}\n") +
            $"{Axis("X", 5006, X)}\n{Axis("Y", 5007, Y)}\n{Axis("Z", 5008, Z)}";
    }

    /// <summary>单天车线路使用的启动拒绝报告；只说明当次读值，不触发暂停或工件作废。</summary>
    internal string BuildFailureMessage(string scope)
        => $"{scope}不能启动/恢复：天车回原点完成标志未满足。\n\n{Describe()}\n\n" +
           "要求：D5006、D5007、D5008必须全部等于1。\n" +
           "本次未执行引擎的启动或恢复。请核对设备状态后重新点击启动。";
}

internal sealed record LineHomingResult(int Line, IReadOnlyList<CraneHomingResult> Cranes)
{
    internal bool CanStart => Cranes.Count == 2 && Cranes.All(x => x.IsReady);

    internal string BuildFailureMessage()
    {
        var text = new StringBuilder($"{Line}号线不能启动/恢复：天车回原点完成标志未满足。\n\n");
        foreach (var crane in Cranes) text.AppendLine(crane.Describe()).AppendLine();
        text.Append("要求：本线前后两台天车的D5006、D5007、D5008必须全部等于1。\n" +
            "本次未执行前后端引擎的启动或恢复。请核对设备状态后重新点击启动。");
        return text.ToString();
    }
}

/// <summary>
/// 主页面线路启动/恢复的只读检查：1号线固定天车1/2，2号线固定天车3/4；研磨使用配置编号。
/// 双天车并行读取，研磨只读指定一台；每台独立5秒超时，失败一律拒绝启动。
/// 不检查XYZ显示坐标，不写PLC，不自动回原点，也不接管工件、队列、锁或异常作废。
/// </summary>
internal static class LineCraneHomingGuard
{
    internal static async Task<LineHomingResult> CheckAsync(int line,
        Func<int, CancellationToken, Task<CraneStatus?>> readStatus, CancellationToken ct = default,
        int timeoutMs = 5000)
    {
        var cranes = line switch
        {
            1 => new[] { (No: 1, Name: "1号线前天车"), (No: 2, Name: "1号线后天车") },
            2 => new[] { (No: 3, Name: "2号线前天车"), (No: 4, Name: "2号线后天车") },
            _ => throw new ArgumentOutOfRangeException(nameof(line), "仅支持1/2号线")
        };
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        ct.ThrowIfCancellationRequested();

        var results = await Task.WhenAll(cranes.Select(crane =>
            ReadOneAsync(crane.No, crane.Name, readStatus, ct, timeoutMs)));
        ct.ThrowIfCancellationRequested();
        return new(line, results);
    }

    /// <summary>
    /// 研磨只有一台天车，编号由调用方传入配置值（默认5号），不能复用双天车线路映射。
    /// 与1/2号线共用严格==1、当次快照及超时规则；本方法不写PLC或改变引擎/工件状态。
    /// </summary>
    internal static Task<CraneHomingResult> CheckGrindingAsync(int craneNo,
        Func<int, CancellationToken, Task<CraneStatus?>> readStatus, CancellationToken ct = default,
        int timeoutMs = 5000)
        => ReadOneAsync(craneNo, "研磨天车", readStatus, ct, timeoutMs);

    private static async Task<CraneHomingResult> ReadOneAsync(int craneNo, string craneName,
        Func<int, CancellationToken, Task<CraneStatus?>> readStatus, CancellationToken ct, int timeoutMs)
    {
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        ct.ThrowIfCancellationRequested();
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(timeoutMs);
        try
        {
            // 实际服务读取D5000～D5029；这里只复制D5006/7/8，不使用显示坐标决定准入。
            // WaitAsync同时约束忽略取消的底层读取；迟到的结果不能改变本次已经拒绝的快照。
            var status = await readStatus(craneNo, readCts.Token).WaitAsync(readCts.Token);
            readCts.Token.ThrowIfCancellationRequested();
            if (status == null) return Failed("未取得有效状态快照");
            return new(craneNo, craneName, status.XHomeCompleted, status.YHomeCompleted, status.ZHomeCompleted, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (readCts.IsCancellationRequested)
        {
            return Failed($"读取超时（{timeoutMs}ms），回原点状态无法确认");
        }
        catch (Exception ex) { return Failed($"{ex.GetType().Name}：{ex.Message}"); }

        CraneHomingResult Failed(string reason) => new(craneNo, craneName, null, null, null, reason);
    }
}
