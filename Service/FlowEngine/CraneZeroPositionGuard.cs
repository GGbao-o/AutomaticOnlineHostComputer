using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>天车显示坐标快照。这里只记录PLC返回的X/Y/Z显示值。</summary>
public readonly record struct CranePositionSnapshot(int X, int Y, int Z)
{
    public bool IsAllZero => X == 0 && Y == 0 && Z == 0;
    public override string ToString() => $"X={X} Y={Y} Z={Z}";
}

/// <summary>连续两次读到XYZ全零时，传递给主页面的安全暂停信息。</summary>
public sealed record CraneZeroPositionAlarm(
    int CraneNo,
    string CraneName,
    string Context,
    CranePositionSnapshot First,
    CranePositionSnapshot Second);

/// <summary>
/// 天车断电坐标保护：第一次读到XYZ全零后等待500ms复核，连续两次全零才报警。
/// 读取失败返回null，继续交给现有通信重试/异常流程处理，不能把“读不到”误判成“全零”。
/// </summary>
internal static class CraneZeroPositionGuard
{
    private const int ConfirmDelayMs = 500;

    public static async Task<CraneZeroPositionAlarm?> CheckAsync(
        CraneService crane,
        int craneNo,
        string craneName,
        string context,
        CancellationToken ct)
    {
        var first = await ReadSnapshotAsync(crane, craneName, context, "第一次", ct);
        if (first == null)
        {
            Console.WriteLine($"[天车坐标保护] [{craneName}] {context} 第一次状态读取失败，交由现有通信异常流程处理");
            return null;
        }

        if (!first.Value.IsAllZero)
            return null;

        Console.WriteLine($"[天车坐标保护] [{craneName}] {context} 第一次XYZ全零，等待{ConfirmDelayMs}ms复核");
        await Task.Delay(ConfirmDelayMs, ct);

        var second = await ReadSnapshotAsync(crane, craneName, context, "第二次", ct);
        if (second == null)
        {
            Console.WriteLine($"[天车坐标保护] [{craneName}] {context} 第二次状态读取失败，不按全零报警，交由现有通信异常流程处理");
            return null;
        }

        if (!second.Value.IsAllZero)
        {
            Console.WriteLine($"[天车坐标保护] [{craneName}] {context} 瞬时全零已恢复，第一次[{first}] 第二次[{second}]");
            return null;
        }

        var alarm = new CraneZeroPositionAlarm(craneNo, craneName, context, first.Value, second.Value);
        Console.WriteLine($"[天车坐标保护] ⚠ [{craneName}] {context} 连续两次XYZ全零，疑似断电后坐标丢失；第一次[{first}] 第二次[{second}]");
        return alarm;
    }

    private static async Task<CranePositionSnapshot?> ReadSnapshotAsync(
        CraneService crane, string craneName, string context, string attempt, CancellationToken ct)
    {
        try
        {
            var status = await crane.ReadStatusAsync(ct);
            return status == null
                ? null
                : new CranePositionSnapshot(status.XPos, status.YPos, status.ZPos);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 坐标保护不接管原通信恢复策略；这里只保证读取异常不会遗留派发锁。
            Console.WriteLine($"[天车坐标保护] [{craneName}] {context} {attempt}状态读取异常: {ex.Message}");
            return null;
        }
    }
}
