using System.Diagnostics;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Service;

internal enum PickupFailureReason
{
    MagnetNotReleased, NoPlate, FeedbackUnknown, UnsafeProbe, MagnetFailed, PostMagnetNoPlate
}

// 不继承InvalidOperationException/TimeoutException：研磨重连层不得重放物理取板动作。
internal sealed class PickupFailedException : Exception
{
    public PickupFailureReason Reason { get; }
    public string EventCode => Reason switch
    {
        PickupFailureReason.NoPlate or PickupFailureReason.PostMagnetNoPlate => "CRANE_X11_NOT_CONFIRMED",
        PickupFailureReason.MagnetFailed => "CRANE_MAGNET_ON_RESPONSE_UNKNOWN",
        _ => "ENGINE_FINAL_FAILURE"
    };
    public PickupFailedException(PickupFailureReason reason, string message, Exception? inner = null)
        : base(message, inner) => Reason = reason;
}

internal sealed record PickupProbeOperations(
    Func<CancellationToken, Task> MoveInitial,
    Func<int, CancellationToken, Task> MoveProbe,
    Func<CancellationToken, Task<FlowActionPosition>> ReadPosition,
    Func<int, CancellationToken, Task<bool>> ReadInput,
    Func<CancellationToken, Task> MagnetOn,
    Func<CancellationToken, Task> Stop,
    Func<int, CancellationToken, Task> Delay);

internal readonly record struct PickupProbeResult(int ActualZ, int ProbeCount);

/// <summary>只执行取板物理时序。来源提交、暂停、作废及锁归调用引擎管理。</summary>
internal static class PickupProbeHelper
{
    internal const int MaxPositions = 3;
    internal const int ProbeStepMm = 5;
    internal const int ProbeToleranceMm = 1;

    public static Task<PickupProbeResult> PickAsync(CraneService crane, int initialZ, int stableDelayMs,
        FlowActionContext action, OperationalPhysicalCycleTracker tracker, CancellationToken ct,
        Func<CancellationToken, Task>? moveInitial = null, Action? validateAction = null)
    {
        void Validate(CancellationToken token) { token.ThrowIfCancellationRequested(); validateAction?.Invoke(); }
        return PickAsync(new PickupProbeOperations(
            async token => { Validate(token); await (moveInitial?.Invoke(token) ?? crane.MoveAbsoluteAsync(-1, -1, initialZ, ct: token)); },
            async (z, token) => { Validate(token); await crane.MoveAbsoluteAsync(-1, -1, z, tolerance: ProbeToleranceMm, timeoutMs: 10_000, ct: token); },
            async token => { Validate(token); var s = await crane.ReadStatusAsync(token)
                ?? throw new InvalidOperationException("取板实际XYZ读取为空"); return new(s.XPos, s.YPos, s.ZPos); },
            async (address, token) => { Validate(token); return await crane.ReadXBitAsync(address, token); },
            async token => { Validate(token); await crane.MagnetOnAsync(token); },
            token => crane.EmergencyStopAsync(token), (ms, token) => Task.Delay(ms, token)),
            initialZ, stableDelayMs, action, tracker, ct);
    }

    internal static async Task<PickupProbeResult> PickAsync(PickupProbeOperations io, int initialZ,
        int stableDelayMs, FlowActionContext action, OperationalPhysicalCycleTracker tracker, CancellationToken ct)
    {
        var phase = PickupFailureReason.FeedbackUnknown;
        try
        {
            ct.ThrowIfCancellationRequested();
            action.ResetPickupFeedback();
            // 状态未知不能视为放磁，也不为继续取下一板而自动退磁。
            bool x6 = await io.ReadInput(63494, ct), x7 = await io.ReadInput(63495, ct);
            action.RecordFeedback(FlowActionPosition.Unknown, null, x6, "取板前读取磁铁反馈");
            Record($"放磁检查：X6={(x6 ? 1 : 0)}，X7={(x7 ? 1 : 0)}");
            if (x6 || !x7)
                throw new PickupFailedException(PickupFailureReason.MagnetNotReleased, "取板前未确认放磁(X6必须=0且X7必须=1)，禁止下降");

            phase = PickupFailureReason.UnsafeProbe;
            action.BeginStep(FlowActionStep.MoveZDownToPick, new(null, null, initialZ), "放磁确认后到首次取料位置");
            action.MarkCommandSent();
            tracker.BeginZDown(initialZ);
            await io.MoveInitial(ct);
            var position = await ReadStablePositionAsync(io, null, null, ct);
            int z0 = position.Z!.Value;
            tracker.CompleteZDown();
            action.Confirm(position, $"首次取料实际停稳Z0={z0}，名义Z={initialZ}");
            Record($"首次取料：名义Z={initialZ}，实际Z0={z0}；补偿上限Z={checked(z0 + 10)}");

            tracker.BeginX11Stage("充磁前来源板探测");
            for (int probe = 1; probe <= MaxPositions; probe++)
            {
                if (probe > 1)
                {
                    phase = PickupFailureReason.UnsafeProbe;
                    bool x2 = await io.ReadInput(63490, ct);
                    Record($"第{probe}位置补偿前：X2={(x2 ? 1 : 0)}");
                    if (x2) throw new PickupFailedException(phase, "正常接触停住但X11=0且X2仍压住，禁止继续下探");
                    int target = checked(z0 + (probe - 1) * ProbeStepMm);
                    position = await io.ReadPosition(ct);
                    await CheckUpperBoundAsync(io, position, z0 + 10, ct);
                    action.BeginStep(FlowActionStep.MoveZDownToPick, new(null, null, target), $"充磁前第{probe}位置下探");
                    action.MarkCommandSent();
                    tracker.BeginZDown(target);
                    await io.MoveProbe(target, ct);
                    position = await ReadStablePositionAsync(io, target, z0 + 10, ct);
                    tracker.CompleteZDown();
                    action.Confirm(position, $"第{probe}位置实际到位");
                }

                phase = PickupFailureReason.FeedbackUnknown;
                action.BeginStep(FlowActionStep.ProbeSourcePresence, position, $"充磁前第{probe}/3位置探测（尚未持件）");
                await io.Delay(Math.Max(0, stableDelayMs), ct);
                tracker.BeginX11Attempt();
                bool present;
                try { present = await io.ReadInput(63497, ct); tracker.CompleteX11(present, DateTime.UtcNow, confirmsHolding: false); }
                catch { tracker.FailX11(); throw; }
                action.RecordFeedback(position, present, false, $"充磁前第{probe}/3位置X11={(present ? 1 : 0)}，板仍属来源");
                Record($"充磁前第{probe}/3位置：目标Z={z0 + (probe - 1) * 5}，实际Z={position.Z}，X11={(present ? 1 : 0)}，累计补偿={position.Z - z0}mm");
                if (!present) continue;

                phase = PickupFailureReason.MagnetFailed;
                action.BeginStep(FlowActionStep.MagnetOnSent, position, "来源板探测成功，开始充磁反馈确认");
                action.MarkCommandSent();
                tracker.BeginMagnetOn();
                try { await io.MagnetOn(ct); tracker.CompleteMagnetOn(); }
                catch { tracker.FailMagnetOn(); throw; }
                action.RecordFeedback(position, null, true, "充磁调用成功(X6已确认)，尚待充磁后X11");
                action.Confirm(position, "充磁反馈确认");
                Record("充磁反馈：MagnetOnAsync成功返回(X6已确认)；尚未提交来源取走");

                phase = PickupFailureReason.FeedbackUnknown;
                tracker.BeginX11Stage("充磁后持件确认");
                action.BeginStep(FlowActionStep.ConfirmPickup, position, "充磁后独立确认持件");
                await io.Delay(Math.Max(0, stableDelayMs), ct);
                tracker.BeginX11Attempt();
                bool held;
                try { held = await io.ReadInput(63497, ct); tracker.CompleteX11(held, DateTime.UtcNow); }
                catch { tracker.FailX11(); throw; }
                action.RecordFeedback(position, held, true, $"充磁后X11={(held ? 1 : 0)}");
                Record($"充磁后持件确认：X11={(held ? 1 : 0)}");
                if (!held) throw new PickupFailedException(PickupFailureReason.PostMagnetNoPlate, "充磁后X11=0，持件未确认，禁止再次下探或提交取走");
                action.SetOwnership(FlowWorkpieceOwnership.OnCarrier, "充磁反馈及充磁后X11均确认，载具持件");
                action.Confirm(position, "充磁后X11=1确认持件");
                return new(position.Z!.Value, probe);
            }
            throw new PickupFailedException(PickupFailureReason.NoPlate, "放磁状态三个位置X11均为0（最多额外下降10mm），取板失败，须暂停并作废本板");
        }
        catch (OperationCanceledException) { throw; }
        catch (PickupFailedException ex) { Record($"严重取板失败：{ex.Message}"); throw; }
        catch (Exception ex)
        {
            action.MarkCommandResponseUnknown($"取板{phase}异常：{ex.Message}");
            Record($"严重取板失败：{phase}，反馈/响应未知，{ex.Message}");
            throw new PickupFailedException(phase, $"取板{phase}失败，禁止自动重做本次取板：{ex.Message}", ex);
        }
    }

    private static async Task<FlowActionPosition> ReadStablePositionAsync(PickupProbeOperations io,
        int? targetZ, int? upperZ, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        FlowActionPosition? previous = null;
        while (watch.ElapsedMilliseconds < 10_000)
        {
            var p = await io.ReadPosition(ct);
            if (!p.Z.HasValue) throw new PickupFailedException(PickupFailureReason.FeedbackUnknown, "实际Z未知，禁止探测下探");
            if (upperZ.HasValue) await CheckUpperBoundAsync(io, p, upperZ.Value, ct);
            if (previous?.Z == p.Z && (!targetZ.HasValue || Math.Abs((long)p.Z.Value - targetZ.Value) <= ProbeToleranceMm)) return p;
            previous = p;
            await io.Delay(100, ct);
        }
        // 不留下仍在运行的运动；停止失败单独留证，不吞掉原坐标错误。
        await StopAndRecordAsync(io);
        throw new PickupFailedException(PickupFailureReason.UnsafeProbe, $"下探实际Z未停稳/未到位，目标={targetZ}，最后Z={previous?.Z}");
    }

    private static async Task CheckUpperBoundAsync(PickupProbeOperations io, FlowActionPosition p, int upperZ, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!p.Z.HasValue) throw new PickupFailedException(PickupFailureReason.FeedbackUnknown, "补偿前实际Z未知");
        if (p.Z.Value <= upperZ) return;
        await StopAndRecordAsync(io);
        throw new PickupFailedException(PickupFailureReason.UnsafeProbe, $"实际Z={p.Z}超过本次允许下探上界{upperZ}，已尝试停止");
    }

    private static async Task StopAndRecordAsync(PickupProbeOperations io)
    {
        using var stopCts = new CancellationTokenSource(10_000);
        try { await io.Stop(stopCts.Token); Record("下探验证失败：停止命令成功返回"); }
        catch (Exception ex) { Record($"下探验证失败：停止结果未知：{ex.Message}"); }
    }

    private static void Record(string text)
    {
        Console.WriteLine($"[取板探测] {text}");
        FlowAlarmPresentationContext.RecordPickupProbe(text);
    }
}
