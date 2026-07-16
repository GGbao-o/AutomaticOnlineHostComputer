using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Infrastructure.Config;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// X/Y绝对编码器下降前微调。
/// 只允许在Z仍处于安全高度、准备下降取/放料前调用；任何一轴未通过校验都会阻止后续Z下降。
/// </summary>
internal static class XAbsFineTuneHelper
{
    private const int StableReadMaxAttempts = 20;
    private const int StableReadRequiredCount = 5;
    private const int StableReadDelayMs = 200;
    private const int StableReadToleranceMm = 1;
    private const int FineTuneMaxAttempts = 2;
    private const int BeforeFineTuneSettleDelayMs = 500;
    private const int AfterMoveMinSettleDelayMs = 600;
    private const int AfterMoveMaxSettleDelayMs = 2000;
    private const int AfterMoveSettleDelayPerMm = 30;
    private const int AbsFeedbackRefreshMaxRetries = 3;
    private const int AbsFeedbackRefreshRetryDelayMs = 1000;

    public static async Task VerifyAndFineTuneAsync(
        CraneService crane,
        MotionConfig cfg,
        int craneNo,
        string stationCode,
        string context,
        CancellationToken ct)
    {
        // 顺序不能互换：X通过后才检查Y；两轴均通过后调用点才可继续原有Z下降。
        await VerifyAxisAndFineTuneAsync(
            crane, cfg, cfg.XAbsFineTune, craneNo, stationCode, context,
            "X", status => status.XPos, status => status.XEncoderAbs,
            (target, token) => crane.MoveAbsoluteAsync(target, -1, -1,
                tolerance: Math.Max(1, cfg.XAbsFineTune.ToleranceMm), timeoutMs: cfg.AbsMove.TimeoutMs, ct: token),
            ct);

        await VerifyAxisAndFineTuneAsync(
            crane, cfg, cfg.YAbsFineTune, craneNo, stationCode, context,
            "Y", status => status.YPos, status => status.YEncoderAbs,
            (target, token) => crane.MoveAbsoluteAsync(-1, target, -1,
                tolerance: Math.Max(1, cfg.YAbsFineTune.ToleranceMm), timeoutMs: cfg.AbsMove.TimeoutMs, ct: token),
            ct);
    }

    private static async Task VerifyAxisAndFineTuneAsync(
        CraneService crane,
        MotionConfig cfg,
        MotionConfig.AxisAbsFineTuneSection fineTune,
        int craneNo,
        string stationCode,
        string context,
        string axis,
        Func<CraneStatus, int> displayPosition,
        Func<CraneStatus, int> absoluteEncoder,
        Func<int, CancellationToken, Task> moveAxisAsync,
        CancellationToken ct)
    {
        string prefix = $"[{axis}AbsFineTune]";
        if (!fineTune.Enabled)
        {
            Console.WriteLine($"{prefix} [{context}] 已关闭, 跳过 {stationCode}");
            return;
        }

        if (!fineTune.TryGetTarget(craneNo, stationCode, out int targetAbs) || targetAbs == -1)
        {
            Console.WriteLine($"{prefix} [{context}] {stationCode} 未配置{axis}AbsEncoder或为-1, 跳过微调");
            return;
        }

        int tolerance = Math.Max(0, fineTune.ToleranceMm);
        int maxAdjust = Math.Max(tolerance, fineTune.MaxAdjustMm);

        await DelayWithLogAsync(prefix, stationCode, context, "微调前沉降", BeforeFineTuneSettleDelayMs, ct);
        CraneStatus status = await ReadStableStatusAsync(crane, stationCode, context, "微调前", axis, displayPosition, absoluteEncoder, ct);

        for (int fineTuneAttempt = 1; fineTuneAttempt <= FineTuneMaxAttempts; fineTuneAttempt++)
        {
            int delta = targetAbs - absoluteEncoder(status);
            int display = displayPosition(status);

            if (Math.Abs(delta) <= tolerance)
            {
                Console.WriteLine($"{prefix} [{context}] {stationCode} 合格: 显示{axis}={display}, 当前Abs{axis}={absoluteEncoder(status)}, 目标Abs{axis}={targetAbs}, Δ={delta}mm, 容差={tolerance}mm");
                return;
            }

            if (Math.Abs(delta) > maxAdjust)
                throw new InvalidOperationException($"[{context}] {stationCode} {axis}绝对编码器偏差过大: 显示{axis}={display}, 当前Abs{axis}={absoluteEncoder(status)}, 目标Abs{axis}={targetAbs}, Δ={delta}mm > 最大微调{maxAdjust}mm, 禁止Z下降");

            int adjustDisplay = display + delta;
            Console.WriteLine($"{prefix} [{context}] {stationCode} 需要微调({fineTuneAttempt}/{FineTuneMaxAttempts}): 显示{axis}={display}, 当前Abs{axis}={absoluteEncoder(status)}, 目标Abs{axis}={targetAbs}, Δ={delta}mm → 微调显示{axis}={adjustDisplay}");

            CraneStatus beforeMoveStatus = status;
            await moveAxisAsync(adjustDisplay, ct);

            int settleDelayMs = ComputeAfterMoveSettleDelayMs(delta);
            await DelayWithLogAsync(prefix, stationCode, context, $"微调后{fineTuneAttempt}沉降", settleDelayMs, ct);

            status = await ReadStableStatusAsync(crane, stationCode, context, $"微调后{fineTuneAttempt}", axis, displayPosition, absoluteEncoder, ct);
            status = await EnsureAbsFeedbackFollowedAsync(
                crane, stationCode, context, fineTuneAttempt, axis, displayPosition, absoluteEncoder,
                beforeMoveStatus, status, tolerance, ct);
            int afterDelta = targetAbs - absoluteEncoder(status);
            if (Math.Abs(afterDelta) <= tolerance)
            {
                Console.WriteLine($"{prefix} [{context}] {stationCode} 微调完成({fineTuneAttempt}/{FineTuneMaxAttempts}): 显示{axis}={displayPosition(status)}, Abs{axis}={absoluteEncoder(status)}, Δ={afterDelta}mm");
                return;
            }

            Console.WriteLine($"{prefix} [{context}] {stationCode} 微调后仍未合格({fineTuneAttempt}/{FineTuneMaxAttempts}): 显示{axis}={displayPosition(status)}, 当前Abs{axis}={absoluteEncoder(status)}, 目标Abs{axis}={targetAbs}, Δ={afterDelta}mm, 容差={tolerance}mm");
        }

        int finalDelta = targetAbs - absoluteEncoder(status);
        throw new InvalidOperationException($"[{context}] {stationCode} {axis}绝对编码器两次微调后仍超差: 显示{axis}={displayPosition(status)}, 当前Abs{axis}={absoluteEncoder(status)}, 目标Abs{axis}={targetAbs}, Δ={finalDelta}mm, 容差={tolerance}mm, 禁止Z下降");
    }

    private static async Task<CraneStatus> ReadStableStatusAsync(
        CraneService crane,
        string stationCode,
        string context,
        string phase,
        string axis,
        Func<CraneStatus, int> displayPosition,
        Func<CraneStatus, int> absoluteEncoder,
        CancellationToken ct)
    {
        CraneStatus? latest = null;
        var window = new List<CraneStatus>(StableReadRequiredCount);
        string prefix = $"[{axis}AbsFineTune]";

        for (int attempt = 1; attempt <= StableReadMaxAttempts; attempt++)
        {
            latest = await crane.ReadStatusAsync(ct);
            if (latest == null)
                throw new InvalidOperationException($"[{context}] {stationCode} {axis}绝对编码器{phase}读取状态失败, 禁止Z下降");

            window.Add(latest);
            if (window.Count > StableReadRequiredCount)
                window.RemoveAt(0);

            Console.WriteLine($"{prefix} [{context}] {stationCode} {phase}状态采样({attempt}/{StableReadMaxAttempts}): 显示{axis}={displayPosition(latest)}, Abs{axis}={absoluteEncoder(latest)}");

            if (window.Count >= StableReadRequiredCount)
            {
                int minDisplay = window.Min(displayPosition);
                int maxDisplay = window.Max(displayPosition);
                int minAbs = window.Min(absoluteEncoder);
                int maxAbs = window.Max(absoluteEncoder);
                int displayRange = maxDisplay - minDisplay;
                int absRange = maxAbs - minAbs;

                if (displayRange <= StableReadToleranceMm && absRange <= StableReadToleranceMm)
                {
                    Console.WriteLine($"{prefix} [{context}] {stationCode} {phase}状态稳定({StableReadRequiredCount}次窗口): 显示{axis}={displayPosition(latest)}, Abs{axis}={absoluteEncoder(latest)}, {axis}范围={minDisplay}~{maxDisplay}, Abs范围={minAbs}~{maxAbs}");
                    return latest;
                }

                string reason = displayRange <= StableReadToleranceMm
                    ? $"显示{axis}已稳但Abs{axis}窗口仍跳动"
                    : $"显示{axis}/Abs{axis}窗口未稳定";
                Console.WriteLine($"{prefix} [{context}] {stationCode} {phase}状态未稳定({StableReadRequiredCount}次窗口, {reason}): {axis}范围={minDisplay}~{maxDisplay}, Abs范围={minAbs}~{maxAbs}");
            }

            await Task.Delay(StableReadDelayMs, ct);
        }

        throw new InvalidOperationException($"[{context}] {stationCode} {axis}绝对编码器{phase}状态连续读取不稳定: 最近显示{axis}={(latest is null ? null : displayPosition(latest))}, Abs{axis}={(latest is null ? null : absoluteEncoder(latest))}, 禁止微调/Z下降");
    }

    private static async Task<CraneStatus> EnsureAbsFeedbackFollowedAsync(
        CraneService crane,
        string stationCode,
        string context,
        int fineTuneAttempt,
        string axis,
        Func<CraneStatus, int> displayPosition,
        Func<CraneStatus, int> absoluteEncoder,
        CraneStatus beforeMove,
        CraneStatus afterMove,
        int tolerance,
        CancellationToken ct)
    {
        for (int retry = 0; retry <= AbsFeedbackRefreshMaxRetries; retry++)
        {
            if (IsAbsFeedbackFollowed(beforeMove, afterMove, axis, displayPosition, absoluteEncoder, tolerance, out string reason))
                return afterMove;

            if (retry >= AbsFeedbackRefreshMaxRetries)
                throw new InvalidOperationException($"[{context}] {stationCode} {axis}绝对编码器微调后反馈未跟随显示坐标: {reason}, 禁止继续微调/Z下降");

            Console.WriteLine($"[{axis}AbsFineTune] [{context}] {stationCode} 微调后{fineTuneAttempt}反馈疑似未刷新({reason}), 再等{AbsFeedbackRefreshRetryDelayMs}ms后重读({retry + 1}/{AbsFeedbackRefreshMaxRetries})");
            await Task.Delay(AbsFeedbackRefreshRetryDelayMs, ct);
            afterMove = await ReadStableStatusAsync(crane, stationCode, context, $"微调后{fineTuneAttempt}反馈重读{retry + 1}", axis, displayPosition, absoluteEncoder, ct);
        }

        return afterMove;
    }

    private static bool IsAbsFeedbackFollowed(
        CraneStatus beforeMove,
        CraneStatus afterMove,
        string axis,
        Func<CraneStatus, int> displayPosition,
        Func<CraneStatus, int> absoluteEncoder,
        int tolerance,
        out string reason)
    {
        int displayMove = displayPosition(afterMove) - displayPosition(beforeMove);
        int absMove = absoluteEncoder(afterMove) - absoluteEncoder(beforeMove);
        int allowedError = Math.Max(tolerance, StableReadToleranceMm);
        int followError = absMove - displayMove;

        if (Math.Abs(displayMove) <= allowedError)
        {
            reason = $"显示{axis}移动量过小: 微调前{axis}={displayPosition(beforeMove)}, 微调后{axis}={displayPosition(afterMove)}, Δ显示={displayMove}mm";
            return false;
        }

        if (Math.Sign(absMove) != Math.Sign(displayMove) || Math.Abs(followError) > allowedError)
        {
            reason = $"显示{axis} {displayPosition(beforeMove)}->{displayPosition(afterMove)}(Δ={displayMove}mm), Abs{axis} {absoluteEncoder(beforeMove)}->{absoluteEncoder(afterMove)}(Δ={absMove}mm), 跟随误差={followError}mm, 允许={allowedError}mm";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static int ComputeAfterMoveSettleDelayMs(int delta)
    {
        int delay = Math.Abs(delta) * AfterMoveSettleDelayPerMm;
        return Math.Clamp(delay, AfterMoveMinSettleDelayMs, AfterMoveMaxSettleDelayMs);
    }

    private static async Task DelayWithLogAsync(
        string prefix,
        string stationCode,
        string context,
        string phase,
        int delayMs,
        CancellationToken ct)
    {
        Console.WriteLine($"{prefix} [{context}] {stationCode} {phase}: 等待PLC/编码器刷新 {delayMs}ms");
        await Task.Delay(delayMs, ct);
    }
}
