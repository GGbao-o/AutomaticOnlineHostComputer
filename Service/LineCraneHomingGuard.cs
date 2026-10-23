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
/// 仅供主页面1/2号线启动/恢复使用的只读检查：1号线固定天车1/2，2号线固定天车3/4。
/// 两台并行读取，各自独立5秒超时；一台失败也收集另一台结果，失败一律拒绝启动。
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

        async Task<CraneHomingResult> ReadOneAsync((int No, string Name) crane)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(timeoutMs);
            try
            {
                // 实际服务读取D5000～D5029；这里只复制D5006/7/8，不使用显示坐标决定准入。
                var status = await readStatus(crane.No, readCts.Token).WaitAsync(readCts.Token);
                readCts.Token.ThrowIfCancellationRequested();
                if (status == null) return Failed("未取得有效状态快照");
                return new(crane.No, crane.Name, status.XHomeCompleted, status.YHomeCompleted, status.ZHomeCompleted, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (readCts.IsCancellationRequested)
            {
                return Failed($"读取超时（{timeoutMs}ms），回原点状态无法确认");
            }
            catch (Exception ex) { return Failed($"{ex.GetType().Name}：{ex.Message}"); }

            CraneHomingResult Failed(string reason) => new(crane.No, crane.Name, null, null, null, reason);
        }

        var results = await Task.WhenAll(cranes.Select(ReadOneAsync));
        ct.ThrowIfCancellationRequested();
        return new(line, results);
    }
}
