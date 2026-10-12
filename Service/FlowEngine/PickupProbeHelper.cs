using System.Diagnostics;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 严重取板失败的分类，供调用引擎选择监控事件及弹窗原因。
/// FeedbackUnknown表示读取没有可靠结果，不能把通信失败解释成X11=0后继续下探。
/// </summary>
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

/// <summary>
/// 取板组件的设备访问适配器。生产入口连接真实天车/机械手服务，测试入口注入可控IO。
/// 组件只调用这里提供的物理操作，不接触来源队列、机床参数、调度锁或引擎暂停位。
/// 没有MagnetOff委托：取板前只检查放磁反馈，不自动退磁；有板后才调用MagnetOn。
/// </summary>
internal sealed record PickupProbeOperations(
    Func<CancellationToken, Task> MoveInitial,
    Func<int, CancellationToken, Task> MoveProbe,
    Func<CancellationToken, Task<FlowActionPosition>> ReadPosition,
    Func<int, CancellationToken, Task<bool>> ReadInput,
    Func<CancellationToken, Task> MagnetOn,
    Func<CancellationToken, Task> Stop,
    Func<int, CancellationToken, Task> Delay);

/// <summary>成功持件时的实际Z及来源探测位置数；ProbeCount不包含充磁后那次X11确认。</summary>
internal readonly record struct PickupProbeResult(int ActualZ, int ProbeCount);

/// <summary>
/// 每次取板固定的配置快照。位置数包含首次，累计补偿=(位置数-1)*步长；运行中页面修改不改变本次快照。
/// 校验在首次设备读取/运动前执行，非法值不能静默回退；旧配置由MotionConfig提供3/5默认值。
/// </summary>
internal readonly record struct PickupProbeSettings(int PositionCount, int StepMm)
{
    internal static PickupProbeSettings Default => new(3, 5);
    internal int MaxExtraDescentMm => checked((PositionCount - 1) * StepMm);
    // 到位容差必须小于步长。步长1mm时容差收紧为0，防止Z尚未移动就接受下一探测位置。
    internal int ToleranceMm => StepMm <= 1 ? 0 : 1;

    internal void Validate()
    {
        if (PositionCount < 1 || StepMm < 1)
            throw new PickupFailedException(PickupFailureReason.UnsafeProbe,
                $"取板探测配置无效：位置数={PositionCount}，步长={StepMm}mm；两项必须>=1，禁止取料运动");
        try { _ = MaxExtraDescentMm; }
        catch (OverflowException ex)
        {
            throw new PickupFailedException(PickupFailureReason.UnsafeProbe,
                $"取板探测累计距离溢出：位置数={PositionCount}，步长={StepMm}mm，禁止取料运动", ex);
        }
    }
}

/// <summary>
/// 五台天车及机械手1/2/3共用的取板物理时序：先确认已放磁，再到首次取料位，
/// 按配置的位置数和步长，从实际停稳Z0探测来源板；默认Z0/+5/+10，有板后充磁并独立确认持件。
/// 严重失败只抛出异常，不在此删除来源或写机床信号；暂停、作废、清设备和释放锁由调用引擎收尾。
/// </summary>
internal static class PickupProbeHelper
{
    /// <summary>
    /// 生产适配入口。initialZ已经由业务计算台面Z、直径及偏移，组件不再次叠加偏移。
    /// moveInitial保留特殊首次运动，例如机械手1原来的XYZ复合到ST007；后续补偿只动Z。
    /// validateAction在读输入、读坐标、运动和充磁前检查动作归属；普通暂停不等于版本失效。
    /// </summary>
    public static Task<PickupProbeResult> PickAsync(CraneService crane, int initialZ, int stableDelayMs,
        FlowActionContext action, OperationalPhysicalCycleTracker tracker, CancellationToken ct,
        Func<CancellationToken, Task>? moveInitial = null, Action? validateAction = null,
        PickupProbeSettings? settings = null)
    {
        var options = settings ?? PickupProbeSettings.Default;
        void Validate(CancellationToken token) { token.ThrowIfCancellationRequested(); validateAction?.Invoke(); }
        return PickAsync(new PickupProbeOperations(
            async token => { Validate(token); await (moveInitial?.Invoke(token) ?? crane.MoveAbsoluteAsync(-1, -1, initialZ, ct: token)); },
            // 补偿不能沿用全局±5mm容差；局部容差须严格小于配置步长，且最大1mm。
            async (z, token) => { Validate(token); await crane.MoveAbsoluteAsync(-1, -1, z, tolerance: options.ToleranceMm, timeoutMs: 10_000, ct: token); },
            async token => { Validate(token); var s = await crane.ReadStatusAsync(token)
                ?? throw new InvalidOperationException("取板实际XYZ读取为空"); return new(s.XPos, s.YPos, s.ZPos); },
            async (address, token) => { Validate(token); return await crane.ReadXBitAsync(address, token); },
            async token => { Validate(token); await crane.MagnetOnAsync(token); },
            token => crane.EmergencyStopAsync(token), (ms, token) => Task.Delay(ms, token)),
            initialZ, stableDelayMs, action, tracker, ct, options);
    }

    internal static async Task<PickupProbeResult> PickAsync(PickupProbeOperations io, int initialZ,
        int stableDelayMs, FlowActionContext action, OperationalPhysicalCycleTracker tracker, CancellationToken ct,
        PickupProbeSettings? settings = null)
    {
        var phase = PickupFailureReason.FeedbackUnknown;
        try
        {
            ct.ThrowIfCancellationRequested();
            var options = settings ?? PickupProbeSettings.Default;
            options.Validate(); // 先拒绝非法配置，再读设备、下降；不触发另一套默认动作。
            int maxExtraDescentMm = options.MaxExtraDescentMm;
            Record($"本次配置：最多{options.PositionCount}个位置，步长={options.StepMm}mm，最多额外下降={maxExtraDescentMm}mm");
            action.ResetPickupFeedback();
            // 首次下降前只读X6充磁反馈和X7放磁反馈，必须分别为0、1。
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
            // 首次运动保留底层既有正常接触规则，成功后再取实际停稳Z作为补偿基准。
            // 因正常接触可能提前停住，不能把名义initialZ当作实际Z0，更不能重复加原Z偏移。
            var position = await ReadStablePositionAsync(io, null, null, ct, options.ToleranceMm);
            int z0 = position.Z!.Value;
            int upperZ = checked(z0 + maxExtraDescentMm);
            tracker.CompleteZDown();
            action.Confirm(position, $"首次取料实际停稳Z0={z0}，名义Z={initialZ}");
            Record($"首次取料：名义Z={initialZ}，实际Z0={z0}；补偿上限Z={upperZ}");

            tracker.BeginX11Stage("充磁前来源板探测");
            for (int index = 0; index < options.PositionCount; index++)
            {
                int probe = index + 1;
                if (probe > 1)
                {
                    // 只有上一位置成功读到X11=0才会进入这里；读不到信号已抛异常，不继续补偿。
                    // X2仍压住意味着不允许继续向下压，不能因为没有探测到板而忽略下压保护。
                    phase = PickupFailureReason.UnsafeProbe;
                    bool x2 = await io.ReadInput(63490, ct);
                    Record($"第{probe}位置补偿前：X2={(x2 ? 1 : 0)}");
                    if (x2) throw new PickupFailedException(phase, "正常接触停住但X11=0且X2仍压住，禁止继续下探");
                    int target = checked(z0 + index * options.StepMm);
                    // 目标从实际Z0按本次步长计算（默认+5/+10）；每次运动前后核对本次累计上界。
                    // checked同时避免坐标整型溢出变成错误的运动目标。
                    position = await io.ReadPosition(ct);
                    await CheckUpperBoundAsync(io, position, upperZ, ct);
                    action.BeginStep(FlowActionStep.MoveZDownToPick, new(null, null, target), $"充磁前第{probe}位置下探");
                    action.MarkCommandSent();
                    tracker.BeginZDown(target);
                    await io.MoveProbe(target, ct);
                    position = await ReadStablePositionAsync(io, target, upperZ, ct, options.ToleranceMm);
                    tracker.CompleteZDown();
                    action.Confirm(position, $"第{probe}位置实际到位");
                }

                phase = PickupFailureReason.FeedbackUnknown;
                action.BeginStep(FlowActionStep.ProbeSourcePresence, position, $"充磁前第{probe}/{options.PositionCount}位置探测（尚未持件）");
                await io.Delay(Math.Max(0, stableDelayMs), ct);
                tracker.BeginX11Attempt();
                bool present;
                // 放磁时X11=1仅说明来源板已进入检测范围，尚未证明载具吸住板。
                // confirmsHolding=false防止监控把这一读数误写成“天车已持件”，来源归属也暂不改变。
                try { present = await io.ReadInput(63497, ct); tracker.CompleteX11(present, DateTime.UtcNow, confirmsHolding: false); }
                catch { tracker.FailX11(); throw; }
                action.RecordFeedback(position, present, false, $"充磁前第{probe}/{options.PositionCount}位置X11={(present ? 1 : 0)}，板仍属来源");
                Record($"充磁前第{probe}/{options.PositionCount}位置：目标Z={checked(z0 + index * options.StepMm)}，实际Z={position.Z}，X11={(present ? 1 : 0)}，累计补偿={(long)position.Z!.Value - z0}mm");
                if (!present) continue;

                // 找到板就停止下探，只启动一次业务充磁；底层磁铁服务自身的反馈尝试规则仍保留。
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
                // 充磁成功返回只证明充磁流程反馈满足要求，仍要再读一次X11确认板没有脱离。
                // 这次持件确认不计入配置的来源探测次数，失败后不重新退磁、下探或再次充磁。
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
                // 所有权只在“充磁成功＋充磁后X11=1”后转移；调用方随后才能提交来源取走及通知。
                action.SetOwnership(FlowWorkpieceOwnership.OnCarrier, "充磁反馈及充磁后X11均确认，载具持件");
                action.Confirm(position, "充磁后X11=1确认持件");
                return new(position.Z!.Value, probe);
            }
            throw new PickupFailedException(PickupFailureReason.NoPlate,
                $"放磁状态{options.PositionCount}个位置X11均为0（步长{options.StepMm}mm，最多额外下降{maxExtraDescentMm}mm），取板失败，须暂停并作废本板");
        }
        // 取消保留原异常类型，由普通取消/应急流程决定结案，不能伪装成严重取板失败误删任务。
        catch (OperationCanceledException) { throw; }
        catch (PickupFailedException ex) { Record($"严重取板失败：{ex.Message}"); throw; }
        catch (Exception ex)
        {
            // 普通IO/运动/磁铁错误一律标为响应未知，并保留原异常作为InnerException。
            // 包装类型不进入研磨旧网络重放策略，避免把已发出物理命令的整段取板再执行一次。
            action.MarkCommandResponseUnknown($"取板{phase}异常：{ex.Message}");
            Record($"严重取板失败：{phase}，反馈/响应未知，{ex.Message}");
            throw new PickupFailedException(phase, $"取板{phase}失败，禁止自动重做本次取板：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 两次间隔100ms的实际Z相同才算停稳；补偿还必须满足本次局部容差及累计上界。
    /// 首次调用targetZ为空，允许底层按正常接触规则提前停住；补偿调用必须验证实际到达新位置。
    /// 超时会独立尝试停止并抛严重失败，成功返回的运动调用不能代替这次实际坐标验证。
    /// </summary>
    private static async Task<FlowActionPosition> ReadStablePositionAsync(PickupProbeOperations io,
        int? targetZ, int? upperZ, CancellationToken ct, int toleranceMm)
    {
        var watch = Stopwatch.StartNew();
        FlowActionPosition? previous = null;
        while (watch.ElapsedMilliseconds < 10_000)
        {
            var p = await io.ReadPosition(ct);
            if (!p.Z.HasValue) throw new PickupFailedException(PickupFailureReason.FeedbackUnknown, "实际Z未知，禁止探测下探");
            if (upperZ.HasValue) await CheckUpperBoundAsync(io, p, upperZ.Value, ct);
            if (previous?.Z == p.Z && (!targetZ.HasValue || Math.Abs((long)p.Z.Value - targetZ.Value) <= toleranceMm)) return p;
            previous = p;
            await io.Delay(100, ct);
        }
        // 不留下仍在运行的运动；停止失败单独留证，不吞掉原坐标错误。
        await StopAndRecordAsync(io);
        throw new PickupFailedException(PickupFailureReason.UnsafeProbe, $"下探实际Z未停稳/未到位，目标={targetZ}，最后Z={previous?.Z}");
    }

    /// <summary>未知Z拒绝继续探测；实际Z越过本次配置累计上界时尝试停止，不用运动指令推断实际位置。</summary>
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
        // 原动作可能已经取消，停止尝试使用独立10秒令牌；真实停止结果单独留证，不覆盖原始失败。
        using var stopCts = new CancellationTokenSource(10_000);
        try { await io.Stop(stopCts.Token); Record("下探验证失败：停止命令成功返回"); }
        catch (Exception ex) { Record($"下探验证失败：停止结果未知：{ex.Message}"); }
    }

    private static void Record(string text)
    {
        // 同时保留运行日志和本动作弹窗历史；刷新现场反馈不会覆盖这些按时间排列的探测证据。
        Console.WriteLine($"[取板探测] {text}");
        FlowAlarmPresentationContext.RecordPickupProbe(text);
    }
}
