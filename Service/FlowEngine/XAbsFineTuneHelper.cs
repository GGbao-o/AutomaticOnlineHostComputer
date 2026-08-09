using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Infrastructure.Logging;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// X/Y绝对编码器下降前同步微调。
/// 只允许在Z仍处于安全高度、准备下降取/放料前调用；任何参与校验的轴未通过都会阻止后续Z下降。
/// </summary>
internal static class XAbsFineTuneHelper
{
    private const int StableReadMaxAttempts = 20;
    private const int FineTuneMaxAttempts = 2;
    private const int AfterMoveMaxSettleDelayMs = 2000;
    private const int AfterMoveSettleDelayPerMm = 30;
    private const int AbsFeedbackFollowMinimumToleranceMm = 1;
    private const int AbsFeedbackRefreshMaxRetries = 3;
    private const int AbsFeedbackRefreshRetryDelayMs = 1000;

    internal enum FineTuneDelayKind
    {
        Phase,
        StableRead,
        FeedbackRefresh
    }

    /// <summary>
    /// 仅隔离微调算法执行边界，便于用确定性状态序列验证失败证据；不得承载业务判断。
    /// </summary>
    internal interface IFineTuneExecution
    {
        Task<CraneStatus?> ReadStatusAsync(CancellationToken ct);

        Task MoveAbsoluteAsync(
            int x,
            int y,
            int z,
            int tolerance,
            int timeoutMs,
            CancellationToken ct);

        Task DelayAsync(FineTuneDelayKind kind, int delayMs, CancellationToken ct);
    }

    private sealed class CraneFineTuneExecution : IFineTuneExecution
    {
        private readonly CraneService _crane;

        public CraneFineTuneExecution(CraneService crane) => _crane = crane;

        public Task<CraneStatus?> ReadStatusAsync(CancellationToken ct) =>
            _crane.ReadStatusAsync(ct);

        public Task MoveAbsoluteAsync(
            int x,
            int y,
            int z,
            int tolerance,
            int timeoutMs,
            CancellationToken ct) =>
            _crane.MoveAbsoluteAsync(x, y, z, tolerance: tolerance, timeoutMs: timeoutMs, ct: ct);

        public Task DelayAsync(FineTuneDelayKind kind, int delayMs, CancellationToken ct) => kind switch
        {
            FineTuneDelayKind.Phase => Task.Delay(delayMs, ct),
            FineTuneDelayKind.StableRead => Task.Delay(delayMs, ct),
            FineTuneDelayKind.FeedbackRefresh => Task.Delay(delayMs, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知微调延时类型")
        };
    }

    private sealed record AxisFineTune(
        string Name,
        MotionConfig.AxisAbsFineTuneSection Settings,
        Func<CraneStatus, int> DisplayPosition,
        Func<CraneStatus, int> AbsoluteEncoder,
        int DefaultAbsolutePerDisplayDirection);

    private sealed record ActiveAxisFineTune(
        AxisFineTune Axis,
        int BaseTargetAbs,
        int TargetAbs,
        int AbsolutePerDisplayDirection)
    {
        public string Name => Axis.Name;
        public int Tolerance => Math.Max(0, Axis.Settings.ToleranceMm);
        public int MaxAdjust => Math.Max(Tolerance, Axis.Settings.MaxAdjustMm);
        public int DisplayPosition(CraneStatus status) => Axis.DisplayPosition(status);
        public int AbsoluteEncoder(CraneStatus status) => Axis.AbsoluteEncoder(status);
        public int TargetAbsOffset => TargetAbs - BaseTargetAbs;
        public string TargetDescription => Name == "Y"
            ? TargetAbsOffset != 0
                ? $"基准AbsY={BaseTargetAbs}, 动态偏移={TargetAbsOffset:+#;-#;0}mm, " +
                  $"动态目标AbsY={TargetAbs}, Y方向={AbsolutePerDisplayDirection:+#;-#}"
                : $"目标AbsY={TargetAbs}, Y方向={AbsolutePerDisplayDirection:+#;-#}"
            : $"目标Abs{Name}={TargetAbs}";
    }

    private sealed record AxisCorrection(ActiveAxisFineTune Axis, int Delta)
    {
        public bool NeedsCorrection => Math.Abs(Delta) > Axis.Tolerance;
    }

    private sealed class FineTuneEvidenceAccumulator
    {
        private CraneStatus? _latestXY;
        private CraneStatus? _latestZ;
        private DateTime? _latestXYAtUtc;
        private DateTime? _latestZAtUtc;
        private IReadOnlyList<ActiveAxisFineTune> _activeAxes = Array.Empty<ActiveAxisFineTune>();
        private string _failureStage = "初始化";
        private int _stableSampleCount;
        private int _stageReadCount;
        private int _totalReadCount;
        private int _fineTuneAttemptCount;
        private int _feedbackRereadCount;
        private int? _lastSentX;
        private int? _lastSentY;
        private DeviceCommandEvidence _xCommand = NotSent("X轴尚未要求微调");
        private DeviceCommandEvidence _yCommand = NotSent("Y轴尚未要求微调");
        private EvidenceValue<string> _lastCheckpoint;

        private FineTuneEvidenceAccumulator(OperationalEventContext context)
        {
            _lastCheckpoint = context.Evidence.BusinessState.LastSuccessfulCheckpoint;
        }

        public static FineTuneEvidenceAccumulator? TryCreate(OperationalEventContext context)
        {
            try { return new FineTuneEvidenceAccumulator(context); }
            catch { return null; }
        }

        public void SetActiveAxes(IReadOnlyList<ActiveAxisFineTune> axes) => Try(() => _activeAxes = axes);

        public void BeginStage(string stage) => Try(() =>
        {
            _failureStage = stage;
            _stageReadCount = 0;
            _stableSampleCount = 0;
        });

        public void BeginReadAttempt() => Try(() =>
        {
            _stageReadCount++;
            _totalReadCount++;
        });

        public void Capture(CraneStatus? status, int stableSampleCount) => Try(() =>
        {
            if (status != null)
            {
                DateTime capturedAtUtc = DateTime.UtcNow;
                _latestXY = status;
                _latestZ = status;
                _latestXYAtUtc = capturedAtUtc;
                _latestZAtUtc = capturedAtUtc;
            }
            _stableSampleCount = stableSampleCount;
        });

        public void FineTuneAttempt(int attempt) => Try(() =>
        {
            _fineTuneAttemptCount = attempt;
            _failureStage = $"第{attempt}次微调计算";
        });

        public void CommandPrepared(int targetX, int targetY, IReadOnlyList<AxisCorrection> movingAxes) => Try(() =>
        {
            bool movingX = movingAxes.Any(item => item.Axis.Name == "X");
            bool movingY = movingAxes.Any(item => item.Axis.Name == "Y");
            if (movingX)
            {
                _lastSentX = targetX;
                _xCommand = SentUnconfirmed("XY微调命令调用已开始，返回结果尚未取得");
            }
            if (movingY)
            {
                _lastSentY = targetY;
                _yCommand = SentUnconfirmed("XY微调命令调用已开始，返回结果尚未取得");
            }
            _latestXY = null;
            _latestXYAtUtc = null;
            _failureStage = $"第{_fineTuneAttemptCount}次XY微调命令";
        });

        public void CommandAcknowledged(IReadOnlyList<AxisCorrection> movingAxes) => Try(() =>
        {
            if (movingAxes.Any(item => item.Axis.Name == "X"))
                _xCommand = Acknowledged("MoveAbsoluteAsync成功返回");
            if (movingAxes.Any(item => item.Axis.Name == "Y"))
                _yCommand = Acknowledged("MoveAbsoluteAsync成功返回");
            Checkpoint($"第{_fineTuneAttemptCount}次XY微调命令成功返回");
        });

        public void FeedbackReread(int retry) => Try(() =>
        {
            _feedbackRereadCount++;
            _failureStage = $"第{_fineTuneAttemptCount}次微调反馈重读{retry}";
        });

        public void FailureStage(string stage) => Try(() => _failureStage = stage);

        public void Checkpoint(string checkpoint) => Try(() =>
            _lastCheckpoint = EvidenceValue<string>.Confirmed(checkpoint, "Helper中既有调用成功返回"));

        public PositionEvidence ToPositionEvidence(PositionEvidence original)
        {
            ActiveAxisFineTune? x = _activeAxes.FirstOrDefault(item => item.Name == "X");
            ActiveAxisFineTune? y = _activeAxes.FirstOrDefault(item => item.Name == "Y");
            AxisCorrection? xCorrection = _latestXY != null && x != null ? new AxisCorrection(x, x.TargetAbs - x.AbsoluteEncoder(_latestXY)) : null;
            AxisCorrection? yCorrection = _latestXY != null && y != null ? new AxisCorrection(y, y.TargetAbs - y.AbsoluteEncoder(_latestXY)) : null;
            string unavailable = _latestZ == null ? "状态读取尚未成功" : "XY命令已开始，命令前XY/Abs证据已失效且尚无命令后成功读取";

            return original with
            {
                DisplayX = Snapshot(_latestXY?.XPos, unavailable),
                DisplayY = Snapshot(_latestXY?.YPos, unavailable),
                DisplayZ = Snapshot(_latestZ?.ZPos, "状态读取尚未成功"),
                AbsX = Snapshot(_latestXY?.XEncoderAbs, unavailable),
                AbsY = Snapshot(_latestXY?.YEncoderAbs, unavailable),
                TargetX = Optional(_lastSentX, "按已有显示坐标与Abs偏差计算的X目标", "尚未计算X显示目标"),
                TargetY = Optional(_lastSentY, "按已有显示坐标与Abs偏差计算的Y目标", "尚未计算Y显示目标"),
                TargetZ = original.TargetZ,
                TargetAbsX = AxisValue(x, item => item.TargetAbs, "X轴未启用或没有目标配置"),
                TargetAbsY = AxisValue(y, item => item.TargetAbs, "Y轴未启用或没有目标配置"),
                LastSentDisplayTargetX = Optional(_lastSentX, "最后一次MoveAbsoluteAsync的X参数", "本次未发送X微调目标"),
                LastSentDisplayTargetY = Optional(_lastSentY, "最后一次MoveAbsoluteAsync的Y参数", "本次未发送Y微调目标"),
                XFineTuneCommand = _xCommand,
                YFineTuneCommand = _yCommand,
                FailureStage = EvidenceValue<string>.Confirmed(_failureStage, "异常捕获时Helper阶段"),
                DeltaX = Correction(xCorrection, "X偏差尚不可计算"),
                DeltaY = Correction(yCorrection, "Y偏差尚不可计算"),
                ToleranceX = AxisValue(x, item => item.Tolerance, "X轴未启用或没有目标配置"),
                ToleranceY = AxisValue(y, item => item.Tolerance, "Y轴未启用或没有目标配置"),
                MaximumCorrectionX = AxisValue(x, item => item.MaxAdjust, "X轴未启用或没有目标配置"),
                MaximumCorrectionY = AxisValue(y, item => item.MaxAdjust, "Y轴未启用或没有目标配置"),
                StableSampleCount = EvidenceValue<int>.Confirmed(_stableSampleCount, "当前稳定读取窗口已有样本数"),
                StageReadCount = EvidenceValue<int>.Confirmed(_stageReadCount, "当前读取阶段尝试次数"),
                TotalReadCount = EvidenceValue<int>.Confirmed(_totalReadCount, "本次微调累计读取尝试次数"),
                FineTuneAttemptCount = EvidenceValue<int>.Confirmed(_fineTuneAttemptCount, "已开始的微调次数"),
                FeedbackRereadCount = EvidenceValue<int>.Confirmed(_feedbackRereadCount, "反馈重读累计次数"),
                CapturedAtUtc = CaptureTime()
            };
        }

        public BusinessStateEvidence ToBusinessState(BusinessStateEvidence original) => new(
            original.Availability,
            original.Reason,
            _lastCheckpoint,
            original.StateBefore,
            original.StateAfter,
            original.CacheBefore,
            original.CacheAfter,
            original.OwnerBefore,
            original.OwnerAfter,
            original.HoldingWorkpiece,
            original.Placed,
            original.CacheNotified,
            original.PhysicalCommitments);

        public EvidenceValue<bool> ZMayStillBeLow(
            OperationalEventContextFactory.FineTunePhysicalTracker? tracker,
            EvidenceValue<bool> original)
        {
            if (_latestZ == null)
                return original;
            OperationalEventContextFactory.FineTunePhysicalSnapshot snapshot = tracker?.Snapshot()
                ?? new OperationalEventContextFactory.FineTunePhysicalSnapshot(
                    EvidenceValue<int>.Unknown("没有物理跟踪器"), EvidenceValue<bool>.Unknown("没有物理跟踪器"),
                    EvidenceValue<int>.Unknown("没有物理跟踪器"), EvidenceValue<DateTime>.Unknown("没有物理跟踪器"),
                    EvidenceValue<bool>.Unknown("没有物理跟踪器"), EvidenceValue<int>.Unknown("没有物理跟踪器"),
                    EvidenceValue<int>.Unknown("没有物理跟踪器"));
            if (!snapshot.SafeZTarget.HasValue || !snapshot.SafeZTolerance.HasValue)
                return EvidenceValue<bool>.Unknown($"最新显示Z={_latestZ.ZPos}，但调用点没有可复用的本阶段安全Z目标/容差");
            int safeZ = snapshot.SafeZTarget.Value;
            int toleranceMm = snapshot.SafeZTolerance.Value;
            bool awayFromSafe = Math.Abs(_latestZ.ZPos - safeZ) > toleranceMm;
            return awayFromSafe
                ? EvidenceValue<bool>.Inferred(true, $"最新显示Z={_latestZ.ZPos}，偏离本阶段安全目标{safeZ}±{toleranceMm}")
                : EvidenceValue<bool>.Confirmed(false, $"最新显示Z={_latestZ.ZPos}位于本阶段安全目标{safeZ}±{toleranceMm}");
        }

        public PhysicalConclusionEvidence PhysicalConclusion()
        {
            bool sentUnknown = _xCommand.State == DeviceCommandState.SentUnconfirmed ||
                               _yCommand.State == DeviceCommandState.SentUnconfirmed;
            if (sentUnknown)
                return new PhysicalConclusionEvidence(
                    PhysicalConclusionCode.CommandResultUnknown,
                    EvidenceAvailability.Unknown,
                    "Z下降未发送；XY微调命令结果未知",
                    "至少一个轴的XY微调命令调用已开始但未取得成功返回");

            bool acknowledged = _xCommand.State == DeviceCommandState.Acknowledged ||
                                _yCommand.State == DeviceCommandState.Acknowledged;
            if (acknowledged)
                return new PhysicalConclusionEvidence(
                    PhysicalConclusionCode.ManualConfirmationRequired,
                    EvidenceAvailability.Inferred,
                    "Z下降未发送；XY已移动但最终微调验证失败",
                    _latestXY == null
                        ? "XY命令成功返回，但尚无命令后状态读取"
                        : "XY命令成功返回且已有命令后状态读取，但未通过最终容差/反馈验证");

            return new PhysicalConclusionEvidence(
                PhysicalConclusionCode.CommandNotSent,
                EvidenceAvailability.Confirmed,
                "XY微调未发起，Z下降未发送",
                "异常发生在XY微调命令调用开始之前");
        }

        private static EvidenceValue<int> Snapshot(int? value, string reason) =>
            value.HasValue ? EvidenceValue<int>.Confirmed(value.Value, "Helper已有CraneStatus快照") : EvidenceValue<int>.Unavailable(reason);

        private static EvidenceValue<int> Optional(int? value, string reason, string missing) =>
            value.HasValue ? EvidenceValue<int>.Confirmed(value.Value, reason) : EvidenceValue<int>.Unavailable(missing);

        private static EvidenceValue<int> AxisValue(ActiveAxisFineTune? axis, Func<ActiveAxisFineTune, int> selector, string missing) =>
            axis != null ? EvidenceValue<int>.Confirmed(selector(axis), "微调配置和调用参数") : EvidenceValue<int>.NotApplicable(missing);

        private static EvidenceValue<int> Correction(AxisCorrection? correction, string missing) =>
            correction != null ? EvidenceValue<int>.Confirmed(correction.Delta, "目标Abs减当前Abs") : EvidenceValue<int>.Unavailable(missing);

        private static DeviceCommandEvidence NotSent(string reason) =>
            new(DeviceCommandState.NotSent, EvidenceAvailability.Confirmed, reason);

        private static DeviceCommandEvidence SentUnconfirmed(string reason) =>
            new(DeviceCommandState.SentUnconfirmed, EvidenceAvailability.Unknown, reason);

        private static DeviceCommandEvidence Acknowledged(string reason) =>
            new(DeviceCommandState.Acknowledged, EvidenceAvailability.Confirmed, reason);

        private EvidenceValue<DateTime> CaptureTime()
        {
            DateTime? capturedAtUtc = _latestXYAtUtc ?? _latestZAtUtc;
            return capturedAtUtc.HasValue
                ? EvidenceValue<DateTime>.Confirmed(capturedAtUtc.Value, "上位机成功接收CraneStatus结果的时间")
                : EvidenceValue<DateTime>.Unavailable("尚无成功CraneStatus读取");
        }

        private static void Try(Action update)
        {
            try { update(); }
            catch { }
        }
    }

    public static async Task VerifyAndFineTuneAsync(
        CraneService crane,
        MotionConfig cfg,
        int craneNo,
        string stationCode,
        string context,
        IOperationalEventReporter reporter,
        OperationalEventContext failureContext,
        string actionId,
        OperationalEventContextFactory.FineTunePhysicalTracker? physicalTracker,
        CancellationToken ct,
        int yCenterToPickOffsetMm = 0)
    {
        await VerifyAndFineTuneAsync(
            new CraneFineTuneExecution(crane),
            cfg,
            craneNo,
            stationCode,
            context,
            reporter,
            failureContext,
            actionId,
            physicalTracker,
            ct,
            yCenterToPickOffsetMm);
    }

    internal static async Task VerifyAndFineTuneAsync(
        IFineTuneExecution execution,
        MotionConfig cfg,
        int craneNo,
        string stationCode,
        string context,
        IOperationalEventReporter reporter,
        OperationalEventContext failureContext,
        string actionId,
        OperationalEventContextFactory.FineTunePhysicalTracker? physicalTracker,
        CancellationToken ct,
        int yCenterToPickOffsetMm = 0)
    {
        FineTuneEvidenceAccumulator? evidence = FineTuneEvidenceAccumulator.TryCreate(failureContext);
        try
        {
            if (yCenterToPickOffsetMm < 0)
                throw new ArgumentOutOfRangeException(nameof(yCenterToPickOffsetMm), "斜床中心到取料位的Y偏移量不能为负数");

            var axes = new[]
            {
                new AxisFineTune("X", cfg.XAbsFineTune, status => status.XPos, status => status.XEncoderAbs, +1),
                new AxisFineTune("Y", cfg.YAbsFineTune, status => status.YPos, status => status.YEncoderAbs, -1)
            };
            List<ActiveAxisFineTune> activeAxes = ResolveActiveAxes(
                axes, craneNo, stationCode, context, yCenterToPickOffsetMm);
            evidence?.SetActiveAxes(activeAxes);
            if (activeAxes.Count == 0)
                return;

            MotionConfig.FineTuneVerificationValues verification = cfg.AbsFineTuneVerification.GetValidated();
            OperationalLog.Info("绝对值微调开始", "XY到位后开始核对绝对编码器",
                ("工位", stationCode), ("微调前等待", $"{verification.BeforeReadSettleDelayMs} ms"),
                ("稳定采样次数", verification.StableSampleCount), ("采样间隔", $"{verification.StableSampleIntervalMs} ms"),
                ("允许波动", $"{verification.StableRangeMm} mm"));
            evidence?.BeginStage("微调前沉降");
            await DelayWithLogAsync(execution, "[XYAbsFineTune]", stationCode, context, "微调前沉降", verification.BeforeReadSettleDelayMs, ct);
            evidence?.Checkpoint("微调前沉降完成");
            CraneStatus status = await ReadStableStatusAsync(execution, stationCode, context, "微调前", activeAxes, verification, evidence, ct);
            evidence?.Checkpoint("微调前稳定状态读取完成");

            for (int fineTuneAttempt = 1; fineTuneAttempt <= FineTuneMaxAttempts; fineTuneAttempt++)
            {
                evidence?.FineTuneAttempt(fineTuneAttempt);
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
                        evidence?.FailureStage($"第{fineTuneAttempt}次{correction.Axis.Name}偏差超过最大允许修正");
                        throw new InvalidOperationException(
                            $"[{context}] {stationCode} {correction.Axis.Name}绝对编码器偏差过大: " +
                            $"显示{correction.Axis.Name}={correction.Axis.DisplayPosition(status)}, " +
                            $"当前Abs{correction.Axis.Name}={correction.Axis.AbsoluteEncoder(status)}, " +
                            $"{correction.Axis.TargetDescription}, Δ={correction.Delta}mm > " +
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
                        $"{correction.Axis.TargetDescription}, ΔAbs={correction.Delta}mm, " +
                        $"Δ显示={deltaDisplay}mm → 微调显示{correction.Axis.Name}={adjustDisplay}");

                    if (correction.Axis.Name == "X") targetX = adjustDisplay;
                    else targetY = adjustDisplay;
                }

                int moveTolerance = Math.Max(1, movingAxes.Max(correction => correction.Axis.Tolerance));
                OperationalLog.Info("绝对值微调命令已发送", "已发送XY联合微调命令",
                    ("工位", stationCode), ("目标显示X", targetX == -1 ? "保持当前值" : $"{targetX} mm"),
                    ("目标显示Y", targetY == -1 ? "保持当前值" : $"{targetY} mm"),
                    ("本次微调", $"{fineTuneAttempt}/{FineTuneMaxAttempts}"));
                evidence?.CommandPrepared(targetX, targetY, movingAxes);
                await execution.MoveAbsoluteAsync(targetX, targetY, -1, moveTolerance,
                    cfg.GetCraneSpeed(craneNo).XyTimeoutMs, ct);
                evidence?.CommandAcknowledged(movingAxes);

                int maxDelta = movingAxes.Max(correction => Math.Abs(correction.Delta));
                int settleDelayMs = ComputeAfterMoveSettleDelayMs(maxDelta, verification.AfterMoveMinSettleDelayMs);
                evidence?.BeginStage($"微调后{fineTuneAttempt}共同沉降");
                await DelayWithLogAsync(execution, "[XYAbsFineTune]", stationCode, context, $"微调后{fineTuneAttempt}共同沉降", settleDelayMs, ct);
                evidence?.Checkpoint($"微调后{fineTuneAttempt}共同沉降完成");
                status = await ReadStableStatusAsync(execution, stationCode, context, $"微调后{fineTuneAttempt}", activeAxes, verification, evidence, ct);
                evidence?.Checkpoint($"微调后{fineTuneAttempt}稳定状态读取完成");

                foreach (AxisCorrection correction in movingAxes)
                {
                    status = await EnsureAbsFeedbackFollowedAsync(
                        execution, stationCode, context, fineTuneAttempt, correction.Axis, beforeMoveStatus, status, activeAxes, verification, evidence, ct);
                }

                LogCurrentCorrections(BuildCorrections(activeAxes, status), stationCode, context, fineTuneAttempt);
            }

            List<AxisCorrection> finalCorrections = BuildCorrections(activeAxes, status);
            evidence?.FailureStage("第二次微调后最终复核仍超差");
            string remaining = string.Join("；", finalCorrections
                .Where(correction => correction.NeedsCorrection)
                .Select(correction =>
                    $"{correction.Axis.Name}: 显示{correction.Axis.Name}={correction.Axis.DisplayPosition(status)}, " +
                    $"当前Abs{correction.Axis.Name}={correction.Axis.AbsoluteEncoder(status)}, " +
                    $"{correction.Axis.TargetDescription}, Δ={correction.Delta}mm, 容差={correction.Axis.Tolerance}mm"));
            throw new InvalidOperationException($"[{context}] {stationCode} XY绝对编码器两次微调后仍超差: {remaining}, 禁止Z下降");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                OperationalEvidence original = failureContext.Evidence;
                PositionEvidence position = evidence?.ToPositionEvidence(original.Position) ?? original.Position;
                BusinessStateEvidence businessState = evidence?.ToBusinessState(original.BusinessState) ?? original.BusinessState;
                PhysicalConclusionEvidence physicalConclusion = evidence?.PhysicalConclusion() ?? failureContext.PhysicalConclusion;
                reporter.Report(failureContext with
                {
                    EventCode = "CRANE_XY_FINE_TUNE_FAILED",
                    ActionId = actionId,
                    CorrelationKey = $"xy-fine-tune:{failureContext.Scope}:{failureContext.Engine}:{failureContext.DeviceNo}:{stationCode}:{failureContext.ActionStage}",
                    CapturedException = ex,
                    DetailMessage = $"{context}；失败阶段={(position.FailureStage.HasValue ? position.FailureStage.Value : position.FailureStage.Reason)}；{ex.Message}",
                    PhysicalConclusion = physicalConclusion,
                    Evidence = original with
                    {
                        Position = position,
                        MotionAndMagnet = original.MotionAndMagnet with
                        {
                            ZMayStillBeLow = evidence?.ZMayStillBeLow(physicalTracker, original.MotionAndMagnet.ZMayStillBeLow)
                                ?? original.MotionAndMagnet.ZMayStillBeLow
                        },
                        BusinessState = businessState
                    }
                });
            }
            catch { }
            throw;
        }
    }

    private static List<ActiveAxisFineTune> ResolveActiveAxes(
        IEnumerable<AxisFineTune> axes,
        int craneNo,
        string stationCode,
        string context,
        int yCenterToPickOffsetMm)
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

            if (!axis.Settings.TryGetTarget(craneNo, stationCode, out int baseTargetAbs) || baseTargetAbs == -1)
            {
                Console.WriteLine($"{prefix} [{context}] {stationCode} 未配置{axis.Name}AbsEncoder或为-1, 跳过微调");
                continue;
            }

            int direction = axis.Name == "Y"
                ? axis.Settings.GetAbsolutePerDisplayDirection(craneNo, GetDefaultYDirection(craneNo))
                : axis.DefaultAbsolutePerDisplayDirection;
            int targetAbs = axis.Name == "Y"
                ? checked(baseTargetAbs - direction * yCenterToPickOffsetMm)
                : baseTargetAbs;
            activeAxes.Add(new ActiveAxisFineTune(axis, baseTargetAbs, targetAbs, direction));
        }

        return activeAxes;
    }

    private static List<AxisCorrection> BuildCorrections(IEnumerable<ActiveAxisFineTune> activeAxes, CraneStatus status)
        => activeAxes.Select(axis => new AxisCorrection(axis, axis.TargetAbs - axis.AbsoluteEncoder(status))).ToList();

    /// <summary>
    /// 兼容未包含 absolutePerDisplayDirections 的旧运行目录配置。
    /// 现场已确认 Crane#1/#3/#4 同向、Crane#2 反向；Crane#5 未复核，保持历史反向行为。
    /// </summary>
    private static int GetDefaultYDirection(int craneNo)
        => craneNo is 1 or 3 or 4 ? +1 : -1;

    private static void LogQualifiedAxes(IEnumerable<AxisCorrection> corrections, CraneStatus status, string stationCode, string context)
    {
        foreach (AxisCorrection correction in corrections)
        {
            Console.WriteLine(
                $"[{correction.Axis.Name}AbsFineTune] [{context}] {stationCode} 合格: " +
                $"显示{correction.Axis.Name}={correction.Axis.DisplayPosition(status)}, " +
                $"当前Abs{correction.Axis.Name}={correction.Axis.AbsoluteEncoder(status)}, " +
                $"{correction.Axis.TargetDescription}, Δ={correction.Delta}mm, 容差={correction.Axis.Tolerance}mm");
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
        IFineTuneExecution execution,
        string stationCode,
        string context,
        string phase,
        IReadOnlyList<ActiveAxisFineTune> activeAxes,
        MotionConfig.FineTuneVerificationValues verification,
        FineTuneEvidenceAccumulator? evidence,
        CancellationToken ct)
    {
        CraneStatus? latest = null;
        var window = new List<CraneStatus>(verification.StableSampleCount);
        evidence?.BeginStage(phase);

        for (int attempt = 1; attempt <= StableReadMaxAttempts; attempt++)
        {
            evidence?.BeginReadAttempt();
            latest = await execution.ReadStatusAsync(ct);
            evidence?.Capture(latest, window.Count);
            if (latest == null)
                throw new InvalidOperationException($"[{context}] {stationCode} XY绝对编码器{phase}读取状态失败, 禁止Z下降");

            window.Add(latest);
            if (window.Count > verification.StableSampleCount)
                window.RemoveAt(0);
            evidence?.Capture(latest, window.Count);

            Console.WriteLine($"[XYAbsFineTune] [{context}] {stationCode} {phase}状态采样({attempt}/{StableReadMaxAttempts}): {FormatAxisValues(latest, activeAxes)}");

            if (window.Count >= verification.StableSampleCount)
            {
                var unstableRanges = new List<string>();
                foreach (ActiveAxisFineTune axis in activeAxes)
                {
                    int minDisplay = window.Min(axis.DisplayPosition);
                    int maxDisplay = window.Max(axis.DisplayPosition);
                    int minAbs = window.Min(axis.AbsoluteEncoder);
                    int maxAbs = window.Max(axis.AbsoluteEncoder);
                    if (maxDisplay - minDisplay > verification.StableRangeMm || maxAbs - minAbs > verification.StableRangeMm)
                    {
                        unstableRanges.Add(
                            $"{axis.Name}范围={minDisplay}~{maxDisplay}, Abs范围={minAbs}~{maxAbs}");
                    }
                }

                if (unstableRanges.Count == 0)
                {
                    OperationalLog.Info("绝对值读数稳定", "连续采样已满足稳定条件，可继续后续安全校验",
                        ("工位", stationCode), ("阶段", phase), ("稳定采样次数", verification.StableSampleCount),
                        ("允许波动", $"{verification.StableRangeMm} mm"), ("当前读数", FormatAxisValues(latest, activeAxes)));
                    evidence?.Checkpoint($"{phase}状态稳定({verification.StableSampleCount}次窗口)");
                    return latest;
                }

                Console.WriteLine($"[XYAbsFineTune] [{context}] {stationCode} {phase}状态未稳定({verification.StableSampleCount}次窗口): {string.Join("；", unstableRanges)}");
            }

            await execution.DelayAsync(FineTuneDelayKind.StableRead, verification.StableSampleIntervalMs, ct);
        }

        throw new InvalidOperationException($"[{context}] {stationCode} XY绝对编码器{phase}连续读取不稳定: 最近{FormatAxisValues(latest!, activeAxes)}, 禁止微调/Z下降");
    }

    private static async Task<CraneStatus> EnsureAbsFeedbackFollowedAsync(
        IFineTuneExecution execution,
        string stationCode,
        string context,
        int fineTuneAttempt,
        ActiveAxisFineTune axis,
        CraneStatus beforeMove,
        CraneStatus afterMove,
        IReadOnlyList<ActiveAxisFineTune> activeAxes,
        MotionConfig.FineTuneVerificationValues verification,
        FineTuneEvidenceAccumulator? evidence,
        CancellationToken ct)
    {
        for (int retry = 0; retry <= AbsFeedbackRefreshMaxRetries; retry++)
        {
            if (IsAbsFeedbackFollowed(beforeMove, afterMove, axis, out string reason))
            {
                evidence?.Checkpoint($"第{fineTuneAttempt}次{axis.Name}绝对编码器反馈已跟随");
                return afterMove;
            }

            if (retry >= AbsFeedbackRefreshMaxRetries)
            {
                evidence?.FailureStage($"第{fineTuneAttempt}次{axis.Name}绝对编码器反馈最终未跟随");
                OperationalLog.Error("绝对值微调反馈未跟随", "微调后坐标未发生预期变化，已禁止继续微调和Z下降",
                    ("工位", stationCode), ("轴", axis.Name), ("失败原因", reason), ("保护结果", "禁止继续微调/Z下降"));
                throw new InvalidOperationException($"[{context}] {stationCode} {axis.Name}绝对编码器微调后反馈未跟随显示坐标: {reason}, 禁止继续微调/Z下降");
            }

            Console.WriteLine($"[{axis.Name}AbsFineTune] [{context}] {stationCode} 微调后{fineTuneAttempt}反馈疑似未刷新({reason}), 再等{AbsFeedbackRefreshRetryDelayMs}ms后共同重读({retry + 1}/{AbsFeedbackRefreshMaxRetries})");
            await execution.DelayAsync(FineTuneDelayKind.FeedbackRefresh, AbsFeedbackRefreshRetryDelayMs, ct);
            evidence?.FeedbackReread(retry + 1);
            afterMove = await ReadStableStatusAsync(execution, stationCode, context, $"微调后{fineTuneAttempt}反馈重读{retry + 1}", activeAxes, verification, evidence, ct);
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
        int allowedError = Math.Max(axis.Tolerance, AbsFeedbackFollowMinimumToleranceMm);
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

    private static int ComputeAfterMoveSettleDelayMs(int maxDelta, int afterMoveMinSettleDelayMs)
    {
        int delay = maxDelta * AfterMoveSettleDelayPerMm;
        return Math.Clamp(delay, afterMoveMinSettleDelayMs, AfterMoveMaxSettleDelayMs);
    }

    private static async Task DelayWithLogAsync(
        IFineTuneExecution execution,
        string prefix,
        string stationCode,
        string context,
        string phase,
        int delayMs,
        CancellationToken ct)
    {
        Console.WriteLine($"{prefix} [{context}] {stationCode} {phase}: 等待PLC/编码器刷新 {delayMs}ms");
        await execution.DelayAsync(FineTuneDelayKind.Phase, delayMs, ct);
    }
}
