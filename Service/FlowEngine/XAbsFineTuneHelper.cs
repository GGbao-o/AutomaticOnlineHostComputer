using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Infrastructure.Config;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// X/Y绝对编码器下降前同步微调。
/// 只允许在Z仍处于安全高度、准备下降取/放料前调用；任何参与校验的轴未通过都会阻止后续Z下降。
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

    private sealed record AxisFineTune(
        string Name,
        MotionConfig.AxisAbsFineTuneSection Settings,
        Func<CraneStatus, int> DisplayPosition,
        Func<CraneStatus, int> AbsoluteEncoder,
        int AbsolutePerDisplayDirection);

    private sealed record ActiveAxisFineTune(AxisFineTune Axis, int TargetAbs)
    {
        public string Name => Axis.Name;
        public int Tolerance => Math.Max(0, Axis.Settings.ToleranceMm);
        public int MaxAdjust => Math.Max(Tolerance, Axis.Settings.MaxAdjustMm);
        public int DisplayPosition(CraneStatus status) => Axis.DisplayPosition(status);
        public int AbsoluteEncoder(CraneStatus status) => Axis.AbsoluteEncoder(status);
        public int AbsolutePerDisplayDirection => Axis.AbsolutePerDisplayDirection;
    }

    private sealed record AxisCorrection(ActiveAxisFineTune Axis, int Delta)
    {
        public bool NeedsCorrection => Math.Abs(Delta) > Axis.Tolerance;
    }

    public static async Task VerifyAndFineTuneAsync(
        CraneService crane,
        MotionConfig cfg,
        int craneNo,
        string stationCode,
        string context,
        CancellationToken ct)
    {
        var axes = new[]
        {
            new AxisFineTune("X", cfg.XAbsFineTune, status => status.XPos, status => status.XEncoderAbs, +1),
            new AxisFineTune("Y", cfg.YAbsFineTune, status => status.YPos, status => status.YEncoderAbs, -1)
        };
        List<ActiveAxisFineTune> activeAxes = ResolveActiveAxes(axes, craneNo, stationCode, context);
        if (activeAxes.Count == 0)
            return;

        await DelayWithLogAsync("[XYAbsFineTune]", stationCode, context, "微调前沉降", BeforeFineTuneSettleDelayMs, ct);
        CraneStatus status = await ReadStableStatusAsync(crane, stationCode, context, "微调前", activeAxes, ct);

        for (int fineTuneAttempt = 1; fineTuneAttempt <= FineTuneMaxAttempts; fineTuneAttempt++)
        {
            List<AxisCorrection> corrections = BuildCorrections(activeAxes, status);
            if (corrections.All(correction => !correction.NeedsCorrection))
            {
                LogQualifiedAxes(corrections, status, stationCode, context);
                return;
            }

            foreach (AxisCorrection correction in corrections.Where(correction => correction.NeedsCorrection))
            {
                if (Math.Abs(correction.Delta) > correction.Axis.MaxAdjust)
                {
                    throw new InvalidOperationException(
                        $"[{context}] {stationCode} {correction.Axis.Name}绝对编码器偏差过大: " +
                        $"显示{correction.Axis.Name}={correction.Axis.DisplayPosition(status)}, " +
                        $"当前Abs{correction.Axis.Name}={correction.Axis.AbsoluteEncoder(status)}, " +
                        $"目标Abs{correction.Axis.Name}={correction.Axis.TargetAbs}, Δ={correction.Delta}mm > " +
                        $"最大微调{correction.Axis.MaxAdjust}mm, 禁止Z下降");
                }
            }

            CraneStatus beforeMoveStatus = status;
            List<AxisCorrection> movingAxes = corrections.Where(correction => correction.NeedsCorrection).ToList();
            int targetX = -1;
            int targetY = -1;
            foreach (AxisCorrection correction in movingAxes)
            {
                int deltaDisplay = correction.Delta * correction.Axis.AbsolutePerDisplayDirection;
                int adjustDisplay = correction.Axis.DisplayPosition(status) + deltaDisplay;
                Console.WriteLine(
                    $"[{correction.Axis.Name}AbsFineTune] [{context}] {stationCode} 需要同步微调({fineTuneAttempt}/{FineTuneMaxAttempts}): " +
                    $"显示{correction.Axis.Name}={correction.Axis.DisplayPosition(status)}, " +
                    $"当前Abs{correction.Axis.Name}={correction.Axis.AbsoluteEncoder(status)}, " +
                    $"目标Abs{correction.Axis.Name}={correction.Axis.TargetAbs}, ΔAbs={correction.Delta}mm, " +
                    $"Δ显示={deltaDisplay}mm → 微调显示{correction.Axis.Name}={adjustDisplay}");

                if (correction.Axis.Name == "X") targetX = adjustDisplay;
                else targetY = adjustDisplay;
            }

            int moveTolerance = Math.Max(1, movingAxes.Max(correction => correction.Axis.Tolerance));
            Console.WriteLine($"[XYAbsFineTune] [{context}] {stationCode} 发起一次XY微调: X={(targetX == -1 ? "保持" : targetX)}, Y={(targetY == -1 ? "保持" : targetY)}");
            await crane.MoveAbsoluteAsync(targetX, targetY, -1, tolerance: moveTolerance, timeoutMs: cfg.AbsMove.TimeoutMs, ct: ct);

            int maxDelta = movingAxes.Max(correction => Math.Abs(correction.Delta));
            int settleDelayMs = ComputeAfterMoveSettleDelayMs(maxDelta);
            await DelayWithLogAsync("[XYAbsFineTune]", stationCode, context, $"微调后{fineTuneAttempt}共同沉降", settleDelayMs, ct);
            status = await ReadStableStatusAsync(crane, stationCode, context, $"微调后{fineTuneAttempt}", activeAxes, ct);

            foreach (AxisCorrection correction in movingAxes)
            {
                status = await EnsureAbsFeedbackFollowedAsync(
                    crane, stationCode, context, fineTuneAttempt, correction.Axis, beforeMoveStatus, status, activeAxes, ct);
            }

            LogCurrentCorrections(BuildCorrections(activeAxes, status), stationCode, context, fineTuneAttempt);
        }

        List<AxisCorrection> finalCorrections = BuildCorrections(activeAxes, status);
        string remaining = string.Join("；", finalCorrections
            .Where(correction => correction.NeedsCorrection)
            .Select(correction =>
                $"{correction.Axis.Name}: 显示{correction.Axis.Name}={correction.Axis.DisplayPosition(status)}, " +
                $"当前Abs{correction.Axis.Name}={correction.Axis.AbsoluteEncoder(status)}, " +
                $"目标Abs{correction.Axis.Name}={correction.Axis.TargetAbs}, Δ={correction.Delta}mm, 容差={correction.Axis.Tolerance}mm"));
        throw new InvalidOperationException($"[{context}] {stationCode} XY绝对编码器两次微调后仍超差: {remaining}, 禁止Z下降");
    }

    private static List<ActiveAxisFineTune> ResolveActiveAxes(
        IEnumerable<AxisFineTune> axes,
        int craneNo,
        string stationCode,
        string context)
    {
        var activeAxes = new List<ActiveAxisFineTune>();
        foreach (AxisFineTune axis in axes)
        {
            string prefix = $"[{axis.Name}AbsFineTune]";
            if (!axis.Settings.Enabled)
            {
                Console.WriteLine($"{prefix} [{context}] 已关闭, 跳过 {stationCode}");
                continue;
            }

            if (!axis.Settings.TryGetTarget(craneNo, stationCode, out int targetAbs) || targetAbs == -1)
            {
                Console.WriteLine($"{prefix} [{context}] {stationCode} 未配置{axis.Name}AbsEncoder或为-1, 跳过微调");
                continue;
            }

            activeAxes.Add(new ActiveAxisFineTune(axis, targetAbs));
        }

        return activeAxes;
    }

    private static List<AxisCorrection> BuildCorrections(IEnumerable<ActiveAxisFineTune> activeAxes, CraneStatus status)
        => activeAxes.Select(axis => new AxisCorrection(axis, axis.TargetAbs - axis.AbsoluteEncoder(status))).ToList();

    private static void LogQualifiedAxes(IEnumerable<AxisCorrection> corrections, CraneStatus status, string stationCode, string context)
    {
        foreach (AxisCorrection correction in corrections)
        {
            Console.WriteLine(
                $"[{correction.Axis.Name}AbsFineTune] [{context}] {stationCode} 合格: " +
                $"显示{correction.Axis.Name}={correction.Axis.DisplayPosition(status)}, " +
                $"当前Abs{correction.Axis.Name}={correction.Axis.AbsoluteEncoder(status)}, " +
                $"目标Abs{correction.Axis.Name}={correction.Axis.TargetAbs}, Δ={correction.Delta}mm, 容差={correction.Axis.Tolerance}mm");
        }
    }

    private static void LogCurrentCorrections(IEnumerable<AxisCorrection> corrections, string stationCode, string context, int fineTuneAttempt)
    {
        foreach (AxisCorrection correction in corrections)
        {
            string result = correction.NeedsCorrection ? "微调后仍未合格" : "微调完成";
            Console.WriteLine(
                $"[{correction.Axis.Name}AbsFineTune] [{context}] {stationCode} {result}({fineTuneAttempt}/{FineTuneMaxAttempts}): " +
                $"Δ={correction.Delta}mm, 容差={correction.Axis.Tolerance}mm");
        }
    }

    private static async Task<CraneStatus> ReadStableStatusAsync(
        CraneService crane,
        string stationCode,
        string context,
        string phase,
        IReadOnlyList<ActiveAxisFineTune> activeAxes,
        CancellationToken ct)
    {
        CraneStatus? latest = null;
        var window = new List<CraneStatus>(StableReadRequiredCount);

        for (int attempt = 1; attempt <= StableReadMaxAttempts; attempt++)
        {
            latest = await crane.ReadStatusAsync(ct);
            if (latest == null)
                throw new InvalidOperationException($"[{context}] {stationCode} XY绝对编码器{phase}读取状态失败, 禁止Z下降");

            window.Add(latest);
            if (window.Count > StableReadRequiredCount)
                window.RemoveAt(0);

            Console.WriteLine($"[XYAbsFineTune] [{context}] {stationCode} {phase}状态采样({attempt}/{StableReadMaxAttempts}): {FormatAxisValues(latest, activeAxes)}");

            if (window.Count >= StableReadRequiredCount)
            {
                var unstableRanges = new List<string>();
                foreach (ActiveAxisFineTune axis in activeAxes)
                {
                    int minDisplay = window.Min(axis.DisplayPosition);
                    int maxDisplay = window.Max(axis.DisplayPosition);
                    int minAbs = window.Min(axis.AbsoluteEncoder);
                    int maxAbs = window.Max(axis.AbsoluteEncoder);
                    if (maxDisplay - minDisplay > StableReadToleranceMm || maxAbs - minAbs > StableReadToleranceMm)
                    {
                        unstableRanges.Add(
                            $"{axis.Name}范围={minDisplay}~{maxDisplay}, Abs范围={minAbs}~{maxAbs}");
                    }
                }

                if (unstableRanges.Count == 0)
                {
                    Console.WriteLine($"[XYAbsFineTune] [{context}] {stationCode} {phase}状态稳定({StableReadRequiredCount}次窗口): {FormatAxisValues(latest, activeAxes)}");
                    return latest;
                }

                Console.WriteLine($"[XYAbsFineTune] [{context}] {stationCode} {phase}状态未稳定({StableReadRequiredCount}次窗口): {string.Join("；", unstableRanges)}");
            }

            await Task.Delay(StableReadDelayMs, ct);
        }

        throw new InvalidOperationException($"[{context}] {stationCode} XY绝对编码器{phase}连续读取不稳定: 最近{FormatAxisValues(latest!, activeAxes)}, 禁止微调/Z下降");
    }

    private static async Task<CraneStatus> EnsureAbsFeedbackFollowedAsync(
        CraneService crane,
        string stationCode,
        string context,
        int fineTuneAttempt,
        ActiveAxisFineTune axis,
        CraneStatus beforeMove,
        CraneStatus afterMove,
        IReadOnlyList<ActiveAxisFineTune> activeAxes,
        CancellationToken ct)
    {
        for (int retry = 0; retry <= AbsFeedbackRefreshMaxRetries; retry++)
        {
            if (IsAbsFeedbackFollowed(beforeMove, afterMove, axis, out string reason))
                return afterMove;

            if (retry >= AbsFeedbackRefreshMaxRetries)
                throw new InvalidOperationException($"[{context}] {stationCode} {axis.Name}绝对编码器微调后反馈未跟随显示坐标: {reason}, 禁止继续微调/Z下降");

            Console.WriteLine($"[{axis.Name}AbsFineTune] [{context}] {stationCode} 微调后{fineTuneAttempt}反馈疑似未刷新({reason}), 再等{AbsFeedbackRefreshRetryDelayMs}ms后共同重读({retry + 1}/{AbsFeedbackRefreshMaxRetries})");
            await Task.Delay(AbsFeedbackRefreshRetryDelayMs, ct);
            afterMove = await ReadStableStatusAsync(crane, stationCode, context, $"微调后{fineTuneAttempt}反馈重读{retry + 1}", activeAxes, ct);
        }

        return afterMove;
    }

    private static bool IsAbsFeedbackFollowed(
        CraneStatus beforeMove,
        CraneStatus afterMove,
        ActiveAxisFineTune axis,
        out string reason)
    {
        int displayMove = axis.DisplayPosition(afterMove) - axis.DisplayPosition(beforeMove);
        int absMove = axis.AbsoluteEncoder(afterMove) - axis.AbsoluteEncoder(beforeMove);
        int allowedError = Math.Max(axis.Tolerance, StableReadToleranceMm);
        int expectedAbsMove = displayMove * axis.AbsolutePerDisplayDirection;
        int followError = absMove - expectedAbsMove;

        if (Math.Abs(displayMove) <= allowedError)
        {
            reason = $"显示{axis.Name}移动量过小: 微调前{axis.Name}={axis.DisplayPosition(beforeMove)}, 微调后{axis.Name}={axis.DisplayPosition(afterMove)}, Δ显示={displayMove}mm";
            return false;
        }

        if (Math.Abs(followError) > allowedError)
        {
            reason = $"显示{axis.Name} {axis.DisplayPosition(beforeMove)}->{axis.DisplayPosition(afterMove)}(Δ={displayMove}mm), " +
                     $"Abs{axis.Name} {axis.AbsoluteEncoder(beforeMove)}->{axis.AbsoluteEncoder(afterMove)}(Δ={absMove}mm), " +
                     $"预期Abs位移={expectedAbsMove}mm, 跟随误差={followError}mm, 允许={allowedError}mm";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static string FormatAxisValues(CraneStatus status, IEnumerable<ActiveAxisFineTune> activeAxes)
        => string.Join(", ", activeAxes.Select(axis => $"显示{axis.Name}={axis.DisplayPosition(status)}, Abs{axis.Name}={axis.AbsoluteEncoder(status)}"));

    private static int ComputeAfterMoveSettleDelayMs(int maxDelta)
    {
        int delay = maxDelta * AfterMoveSettleDelayPerMm;
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
