using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Infrastructure.Config;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// X绝对编码器下降前微调。
/// 只允许在Z仍处于安全高度、准备下降取/放料前调用；不要在Z已下降或夹持贴近设备时调用。
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
        var fineTune = cfg.XAbsFineTune;
        if (!fineTune.Enabled)
        {
            Console.WriteLine($"[XAbsFineTune] [{context}] 已关闭, 跳过 {stationCode}");
            return;
        }

        if (!fineTune.TryGetTarget(craneNo, stationCode, out int targetAbs) || targetAbs == -1)
        {
            Console.WriteLine($"[XAbsFineTune] [{context}] {stationCode} 未配置XAbsEncoder或为-1, 跳过微调");
            return;
        }

        int tolerance = Math.Max(0, fineTune.ToleranceMm);
        int maxAdjust = Math.Max(tolerance, fineTune.MaxAdjustMm);

        await DelayWithLogAsync(stationCode, context, "微调前沉降", BeforeFineTuneSettleDelayMs, ct);
        CraneStatus status = await ReadStableStatusAsync(crane, stationCode, context, "微调前", ct);

        for (int fineTuneAttempt = 1; fineTuneAttempt <= FineTuneMaxAttempts; fineTuneAttempt++)
        {
            int delta = targetAbs - status.XEncoderAbs;

            if (Math.Abs(delta) <= tolerance)
            {
                Console.WriteLine($"[XAbsFineTune] [{context}] {stationCode} 合格: 显示X={status.XPos}, 当前AbsX={status.XEncoderAbs}, 目标AbsX={targetAbs}, Δ={delta}mm, 容差={tolerance}mm");
                return;
            }

            if (Math.Abs(delta) > maxAdjust)
                throw new InvalidOperationException($"[{context}] {stationCode} X绝对编码器偏差过大: 显示X={status.XPos}, 当前AbsX={status.XEncoderAbs}, 目标AbsX={targetAbs}, Δ={delta}mm > 最大微调{maxAdjust}mm, 禁止Z下降");

            int adjustDisplayX = status.XPos + delta;
            Console.WriteLine($"[XAbsFineTune] [{context}] {stationCode} 需要微调({fineTuneAttempt}/{FineTuneMaxAttempts}): 显示X={status.XPos}, 当前AbsX={status.XEncoderAbs}, 目标AbsX={targetAbs}, Δ={delta}mm → 微调显示X={adjustDisplayX}");

            CraneStatus beforeMoveStatus = status;
            await crane.MoveAbsoluteAsync(adjustDisplayX, -1, -1, tolerance: Math.Max(1, tolerance), timeoutMs: cfg.AbsMove.TimeoutMs, ct: ct);

            int settleDelayMs = ComputeAfterMoveSettleDelayMs(delta);
            await DelayWithLogAsync(stationCode, context, $"微调后{fineTuneAttempt}沉降", settleDelayMs, ct);

            status = await ReadStableStatusAsync(crane, stationCode, context, $"微调后{fineTuneAttempt}", ct);
            status = await EnsureAbsFeedbackFollowedAsync(crane, stationCode, context, fineTuneAttempt, beforeMoveStatus, status, tolerance, ct);
            int afterDelta = targetAbs - status.XEncoderAbs;
            if (Math.Abs(afterDelta) <= tolerance)
            {
                Console.WriteLine($"[XAbsFineTune] [{context}] {stationCode} 微调完成({fineTuneAttempt}/{FineTuneMaxAttempts}): 显示X={status.XPos}, AbsX={status.XEncoderAbs}, Δ={afterDelta}mm");
                return;
            }

            Console.WriteLine($"[XAbsFineTune] [{context}] {stationCode} 微调后仍未合格({fineTuneAttempt}/{FineTuneMaxAttempts}): 显示X={status.XPos}, 当前AbsX={status.XEncoderAbs}, 目标AbsX={targetAbs}, Δ={afterDelta}mm, 容差={tolerance}mm");
        }

        int finalDelta = targetAbs - status.XEncoderAbs;
        throw new InvalidOperationException($"[{context}] {stationCode} X绝对编码器两次微调后仍超差: 显示X={status.XPos}, 当前AbsX={status.XEncoderAbs}, 目标AbsX={targetAbs}, Δ={finalDelta}mm, 容差={tolerance}mm, 禁止Z下降");
    }

    private static async Task<CraneStatus> ReadStableStatusAsync(
        CraneService crane,
        string stationCode,
        string context,
        string phase,
        CancellationToken ct)
    {
        CraneStatus? latest = null;
        var window = new List<CraneStatus>(StableReadRequiredCount);

        for (int attempt = 1; attempt <= StableReadMaxAttempts; attempt++)
        {
            latest = await crane.ReadStatusAsync(ct);
            if (latest == null)
                throw new InvalidOperationException($"[{context}] {stationCode} X绝对编码器{phase}读取状态失败, 禁止Z下降");

            window.Add(latest);
            if (window.Count > StableReadRequiredCount)
                window.RemoveAt(0);

            Console.WriteLine($"[XAbsFineTune] [{context}] {stationCode} {phase}状态采样({attempt}/{StableReadMaxAttempts}): 显示X={latest.XPos}, AbsX={latest.XEncoderAbs}");

            if (window.Count >= StableReadRequiredCount)
            {
                int minX = window.Min(s => s.XPos);
                int maxX = window.Max(s => s.XPos);
                int minAbs = window.Min(s => s.XEncoderAbs);
                int maxAbs = window.Max(s => s.XEncoderAbs);
                int xRange = maxX - minX;
                int absRange = maxAbs - minAbs;

                if (xRange <= StableReadToleranceMm && absRange <= StableReadToleranceMm)
                {
                    Console.WriteLine($"[XAbsFineTune] [{context}] {stationCode} {phase}状态稳定({StableReadRequiredCount}次窗口): 显示X={latest.XPos}, AbsX={latest.XEncoderAbs}, X范围={minX}~{maxX}, Abs范围={minAbs}~{maxAbs}");
                    return latest;
                }

                string reason = xRange <= StableReadToleranceMm
                    ? "显示X已稳但AbsX窗口仍跳动"
                    : "显示X/AbsX窗口未稳定";
                Console.WriteLine($"[XAbsFineTune] [{context}] {stationCode} {phase}状态未稳定({StableReadRequiredCount}次窗口, {reason}): X范围={minX}~{maxX}, Abs范围={minAbs}~{maxAbs}");
            }

            await Task.Delay(StableReadDelayMs, ct);
        }

        throw new InvalidOperationException($"[{context}] {stationCode} X绝对编码器{phase}状态连续读取不稳定: 最近显示X={latest?.XPos}, AbsX={latest?.XEncoderAbs}, 禁止微调/Z下降");
    }

    private static async Task<CraneStatus> EnsureAbsFeedbackFollowedAsync(
        CraneService crane,
        string stationCode,
        string context,
        int fineTuneAttempt,
        CraneStatus beforeMove,
        CraneStatus afterMove,
        int tolerance,
        CancellationToken ct)
    {
        for (int retry = 0; retry <= AbsFeedbackRefreshMaxRetries; retry++)
        {
            if (IsAbsFeedbackFollowed(beforeMove, afterMove, tolerance, out string reason))
                return afterMove;

            if (retry >= AbsFeedbackRefreshMaxRetries)
                throw new InvalidOperationException($"[{context}] {stationCode} X绝对编码器微调后反馈未跟随显示坐标: {reason}, 禁止继续微调/Z下降");

            Console.WriteLine($"[XAbsFineTune] [{context}] {stationCode} 微调后{fineTuneAttempt}反馈疑似未刷新({reason}), 再等{AbsFeedbackRefreshRetryDelayMs}ms后重读({retry + 1}/{AbsFeedbackRefreshMaxRetries})");
            await Task.Delay(AbsFeedbackRefreshRetryDelayMs, ct);
            afterMove = await ReadStableStatusAsync(crane, stationCode, context, $"微调后{fineTuneAttempt}反馈重读{retry + 1}", ct);
        }

        return afterMove;
    }

    private static bool IsAbsFeedbackFollowed(
        CraneStatus beforeMove,
        CraneStatus afterMove,
        int tolerance,
        out string reason)
    {
        int displayMove = afterMove.XPos - beforeMove.XPos;
        int absMove = afterMove.XEncoderAbs - beforeMove.XEncoderAbs;
        int allowedError = Math.Max(tolerance, StableReadToleranceMm);
        int followError = absMove - displayMove;

        if (Math.Abs(displayMove) <= allowedError)
        {
            reason = $"显示X移动量过小: 微调前X={beforeMove.XPos}, 微调后X={afterMove.XPos}, Δ显示={displayMove}mm";
            return false;
        }

        if (Math.Sign(absMove) != Math.Sign(displayMove) || Math.Abs(followError) > allowedError)
        {
            reason = $"显示X {beforeMove.XPos}->{afterMove.XPos}(Δ={displayMove}mm), AbsX {beforeMove.XEncoderAbs}->{afterMove.XEncoderAbs}(Δ={absMove}mm), 跟随误差={followError}mm, 允许={allowedError}mm";
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
        string stationCode,
        string context,
        string phase,
        int delayMs,
        CancellationToken ct)
    {
        Console.WriteLine($"[XAbsFineTune] [{context}] {stationCode} {phase}: 等待PLC/编码器刷新 {delayMs}ms");
        await Task.Delay(delayMs, ct);
    }
}
