using System.Globalization;
using System.Text;

namespace AutomaticOnlineHostComputer.Service.OperationalEvents;

public sealed record OperationalEventDetailSections(
    string Overview,
    string LineDeviceAndStage,
    string WorkpieceAndFlow,
    string PositionAndFineTune,
    string ZMagnetAndX11,
    string StateCacheAndCommitments,
    string PauseLocksAndRecovery,
    string RequiredAndForbiddenActions,
    string FullExceptionChain)
{
    public string FullDetailText => string.Join(
        Environment.NewLine + Environment.NewLine,
        Overview,
        LineDeviceAndStage,
        WorkpieceAndFlow,
        PositionAndFineTune,
        ZMagnetAndX11,
        StateCacheAndCommitments,
        PauseLocksAndRecovery,
        RequiredAndForbiddenActions,
        FullExceptionChain);
}

public sealed class OperationalEventFormatter
{
    private readonly OperationalEventCatalog _catalog;

    public OperationalEventFormatter()
        : this(OperationalEventCatalog.Default)
    {
    }

    public OperationalEventFormatter(OperationalEventCatalog catalog)
    {
        _catalog = catalog ?? OperationalEventCatalog.Default;
    }

    public string FormatFullDetail(OperationalEvent item) => FormatSections(item).FullDetailText;

    public OperationalEventDetailSections FormatSections(OperationalEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        OperationalEventDefinition definition = _catalog.Resolve(item);

        return new OperationalEventDetailSections(
            FormatOverview(item),
            FormatLineDeviceAndStage(item),
            FormatEvidencePair("工件与流向", item, FormatWorkpiece),
            FormatEvidencePair("坐标与微调", item, FormatPosition),
            FormatEvidencePair("Z磁铁与X11", item, FormatMotionAndMagnet),
            FormatEvidencePair("状态缓存与承诺点", item, FormatBusinessState),
            FormatPauseLocksAndRecovery(item),
            FormatGuidance(item, definition),
            FormatEvidencePair("完整异常链", item, FormatException));
    }

    public static string FormatCommand(DeviceCommandEvidence command)
    {
        ArgumentNullException.ThrowIfNull(command);
        string wording = command.State switch
        {
            DeviceCommandState.NotSent => "未发送",
            DeviceCommandState.SentUnconfirmed => "可能已发送但未取得确认",
            DeviceCommandState.Acknowledged => "命令成功返回（仅代表调用返回，不代表X11或物理结果已确认）",
            DeviceCommandState.Failed => "命令已失败",
            DeviceCommandState.Unknown => "未知：没有足够证据判断命令是否发送",
            DeviceCommandState.Unavailable => "不可用：调用点没有命令证据",
            DeviceCommandState.NotApplicable => "不适用：当前动作未进入该命令阶段",
            _ => "未知：未识别命令状态"
        };
        return $"{command.State}/{command.Availability}: {wording}; Reason={Reason(command.Reason)}";
    }

    public static string FormatDiagnostics(OperationalEventDiagnostics diagnostics) =>
        string.Join(
            ", ",
            $"TotalReceived={diagnostics.TotalReceived.ToString(CultureInfo.InvariantCulture)}",
            $"AggregatedCount={diagnostics.AggregatedCount.ToString(CultureInfo.InvariantCulture)}",
            $"EvictedCount={diagnostics.EvictedCount.ToString(CultureInfo.InvariantCulture)}",
            $"ReporterFailureCount={diagnostics.ReporterFailureCount.ToString(CultureInfo.InvariantCulture)}",
            $"LastReporterFailureAtUtc={FormatDate(diagnostics.LastReporterFailureAtUtc)}",
            $"LastReporterFailureReason={NonBlank(diagnostics.LastReporterFailureReason, "无")}",
            $"TransientStateEvictedCount={diagnostics.TransientStateEvictedCount.ToString(CultureInfo.InvariantCulture)}",
            $"StageStateEvictedCount={diagnostics.StageStateEvictedCount.ToString(CultureInfo.InvariantCulture)}");

    internal static string FormatEvidenceValue<T>(EvidenceValue<T> evidence)
    {
        if (evidence is null)
        {
            return "Unavailable/HasValue=False/Value=不可用：证据对象为空/Reason=证据对象为空";
        }

        string reason = Reason(evidence.Reason);
        string value = evidence.HasValue
            ? FormatScalar(evidence.Value)
            : $"{AvailabilityText(evidence.Availability)}：{reason}";
        return $"{evidence.Availability}/HasValue={evidence.HasValue}/Value={value}/Reason={reason}";
    }

    private static string FormatOverview(OperationalEvent item)
    {
        var builder = Section("事件总览");
        Add(builder, "EventId", NonBlank(item.EventId, "未生成"));
        Add(builder, "EventCode", NonBlank(item.EventCode, "未提供"));
        Add(builder, "First ActionId", ActionId(item.FirstActionId));
        Add(builder, "Latest ActionId", ActionId(item.LatestActionId));
        Add(builder, "Title", NonBlank(item.Title, "未提供标题"));
        Add(builder, "Severity", item.Severity.ToString());
        Add(builder, "Category", item.Category.ToString());
        Add(builder, "Source", NonBlank(item.Source, "未提供来源"));
        Add(builder, "首次时间", FormatDate(item.FirstOccurredAtUtc));
        Add(builder, "最近时间", FormatDate(item.LastOccurredAtUtc));
        Add(builder, "累计次数", item.OccurrenceCount.ToString(CultureInfo.InvariantCulture));
        Add(builder, "首次物理结论", FormatPhysicalConclusion(item.FirstPhysicalConclusion));
        Add(builder, "最近物理结论", FormatPhysicalConclusion(item.LatestPhysicalConclusion));
        Add(builder, "首次DetailMessage", NonBlank(item.FirstDetailMessage, "未提供详情"));
        Add(builder, "最近DetailMessage", NonBlank(item.LatestDetailMessage, "未提供详情"));
        Add(builder, "首次Result", NonBlank(item.FirstResult, "未提供结果"));
        Add(builder, "最近Result", NonBlank(item.LatestResult, "未提供结果"));
        Add(builder, "首次BusinessPaused", FormatEvidenceValue(item.FirstBusinessPaused));
        Add(builder, "最近BusinessPaused", FormatEvidenceValue(item.LatestBusinessPaused));
        return builder.ToString().TrimEnd();
    }

    private static string FormatLineDeviceAndStage(OperationalEvent item)
    {
        var builder = Section("线路设备与阶段");
        Add(builder, "Scope", NonBlank(item.Scope, "未知线路"));
        Add(builder, "Engine", NonBlank(item.Engine, "未知引擎"));
        Add(builder, "Source", NonBlank(item.Source, "未知来源"));
        Add(builder, "DeviceType", NonBlank(item.DeviceType, "未知设备类型"));
        Add(builder, "DeviceNo", NonBlank(item.DeviceNo, "未知设备号"));
        Add(builder, "Station", NonBlank(item.Station, "未知工位"));
        Add(builder, "ActionStage", NonBlank(item.ActionStage, "未知动作阶段"));
        return builder.ToString().TrimEnd();
    }

    private static string FormatEvidencePair(
        string title,
        OperationalEvent item,
        Action<StringBuilder, OperationalEvidence> append)
    {
        var builder = Section(title);
        builder.AppendLine("[首次证据]");
        append(builder, item.FirstEvidence);
        builder.AppendLine("[最近证据]");
        append(builder, item.LatestEvidence);
        return builder.ToString().TrimEnd();
    }

    private static void FormatWorkpiece(StringBuilder builder, OperationalEvidence evidence)
    {
        WorkpieceEvidence item = evidence.Workpiece;
        Add(builder, "PlateNo", FormatEvidenceValue(item.PlateNo));
        Add(builder, "Sequence", FormatEvidenceValue(item.Sequence));
        Add(builder, "Diameter", FormatEvidenceValue(item.Diameter));
        Add(builder, "Length", FormatEvidenceValue(item.Length));
        Add(builder, "Source", FormatEvidenceValue(item.Source));
        Add(builder, "Target", FormatEvidenceValue(item.Target));
        Add(builder, "SoftwareOwner", FormatEvidenceValue(item.SoftwareOwner));
        Add(builder, "LastConfirmedLocation", FormatEvidenceValue(item.LastConfirmedLocation));
        Add(builder, "EvidenceSource", NonBlank(item.EvidenceSource, "不可用：未提供证据来源"));
    }

    private static void FormatPosition(StringBuilder builder, OperationalEvidence evidence)
    {
        PositionEvidence item = evidence.Position;
        Add(builder, "DisplayX", FormatEvidenceValue(item.DisplayX));
        Add(builder, "DisplayY", FormatEvidenceValue(item.DisplayY));
        Add(builder, "DisplayZ", FormatEvidenceValue(item.DisplayZ));
        Add(builder, "AbsX", FormatEvidenceValue(item.AbsX));
        Add(builder, "AbsY", FormatEvidenceValue(item.AbsY));
        Add(builder, "TargetX", FormatEvidenceValue(item.TargetX));
        Add(builder, "TargetY", FormatEvidenceValue(item.TargetY));
        Add(builder, "TargetZ", FormatEvidenceValue(item.TargetZ));
        Add(builder, "TargetAbsX", FormatEvidenceValue(item.TargetAbsX));
        Add(builder, "TargetAbsY", FormatEvidenceValue(item.TargetAbsY));
        Add(builder, "LastSentDisplayTargetX", FormatEvidenceValue(item.LastSentDisplayTargetX));
        Add(builder, "LastSentDisplayTargetY", FormatEvidenceValue(item.LastSentDisplayTargetY));
        Add(builder, "XFineTuneCommand", FormatCommand(item.XFineTuneCommand));
        Add(builder, "YFineTuneCommand", FormatCommand(item.YFineTuneCommand));
        Add(builder, "FailureStage", FormatEvidenceValue(item.FailureStage));
        Add(builder, "DeltaX", FormatEvidenceValue(item.DeltaX));
        Add(builder, "DeltaY", FormatEvidenceValue(item.DeltaY));
        Add(builder, "ToleranceX", FormatEvidenceValue(item.ToleranceX));
        Add(builder, "ToleranceY", FormatEvidenceValue(item.ToleranceY));
        Add(builder, "MaximumCorrectionX", FormatEvidenceValue(item.MaximumCorrectionX));
        Add(builder, "MaximumCorrectionY", FormatEvidenceValue(item.MaximumCorrectionY));
        Add(builder, "StableSampleCount", FormatEvidenceValue(item.StableSampleCount));
        Add(builder, "StageReadCount", FormatEvidenceValue(item.StageReadCount));
        Add(builder, "TotalReadCount", FormatEvidenceValue(item.TotalReadCount));
        Add(builder, "FineTuneAttemptCount", FormatEvidenceValue(item.FineTuneAttemptCount));
        Add(builder, "FeedbackRereadCount", FormatEvidenceValue(item.FeedbackRereadCount));
        Add(builder, "CapturedAtUtc", FormatEvidenceValue(item.CapturedAtUtc));
    }

    private static void FormatMotionAndMagnet(StringBuilder builder, OperationalEvidence evidence)
    {
        MotionAndMagnetEvidence item = evidence.MotionAndMagnet;
        Add(builder, "ZDownCommand", FormatCommand(item.ZDownCommand));
        Add(builder, "ZMayStillBeLow", FormatEvidenceValue(item.ZMayStillBeLow));
        Add(builder, "MagnetOnCommand", FormatCommand(item.MagnetOnCommand));
        Add(builder, "MagnetOffCommand", FormatCommand(item.MagnetOffCommand));
        Add(builder, "X11LastValue", FormatEvidenceValue(item.X11LastValue));
        Add(builder, "X11ReadValid", FormatEvidenceValue(item.X11ReadValid));
        Add(builder, "X11Attempts", FormatEvidenceValue(item.X11Attempts));
        Add(builder, "X11ReadAtUtc", FormatEvidenceValue(item.X11ReadAtUtc));
    }

    private static void FormatBusinessState(StringBuilder builder, OperationalEvidence evidence)
    {
        BusinessStateEvidence item = evidence.BusinessState;
        Add(builder, "BusinessState.Availability", item.Availability.ToString());
        Add(builder, "BusinessState.Reason", Reason(item.Reason));
        Add(builder, "LastSuccessfulCheckpoint", FormatEvidenceValue(item.LastSuccessfulCheckpoint));
        Add(builder, "StateBefore", FormatEvidenceValue(item.StateBefore));
        Add(builder, "StateAfter", FormatEvidenceValue(item.StateAfter));
        Add(builder, "CacheBefore", FormatEvidenceValue(item.CacheBefore));
        Add(builder, "CacheAfter", FormatEvidenceValue(item.CacheAfter));
        Add(builder, "OwnerBefore", FormatEvidenceValue(item.OwnerBefore));
        Add(builder, "OwnerAfter", FormatEvidenceValue(item.OwnerAfter));
        Add(builder, "HoldingWorkpiece", FormatEvidenceValue(item.HoldingWorkpiece));
        Add(builder, "Placed", FormatEvidenceValue(item.Placed));
        Add(builder, "CacheNotified", FormatEvidenceValue(item.CacheNotified));
        Add(builder, "PhysicalCommitments", FormatCommitments(item.PhysicalCommitments, item.Availability, item.Reason));
    }

    private static string FormatPauseLocksAndRecovery(OperationalEvent item)
    {
        var builder = Section("暂停锁与恢复");
        builder.AppendLine("[首次证据]");
        Add(builder, "BusinessPaused", FormatEvidenceValue(item.FirstBusinessPaused));
        FormatLocksAndRecovery(builder, item.FirstEvidence);
        builder.AppendLine("[最近证据]");
        Add(builder, "BusinessPaused", FormatEvidenceValue(item.LatestBusinessPaused));
        FormatLocksAndRecovery(builder, item.LatestEvidence);
        return builder.ToString().TrimEnd();
    }

    private static void FormatLocksAndRecovery(StringBuilder builder, OperationalEvidence evidence)
    {
        LockEvidence locks = evidence.Locks;
        Add(builder, "Locks.Availability", locks.Availability.ToString());
        Add(builder, "Locks.Reason", Reason(locks.Reason));
        Add(builder, "Locks", locks.Items.Count == 0
            ? $"{AvailabilityText(locks.Availability)}：{Reason(locks.Reason)}"
            : string.Join(" | ", locks.Items.Select(item =>
                $"Name={NonBlank(item.Name, "未命名锁")}; HeldAtFailure={FormatEvidenceValue(item.HeldAtFailure)}; ReleasedAfterward={FormatEvidenceValue(item.ReleasedAfterward)}; ManualConfirmation={NonBlank(item.ManualConfirmation, "未提供人工确认项")}")));

        RecoveryEvidence recovery = evidence.Recovery;
        Add(builder, "Recovery.Availability", recovery.Availability.ToString());
        Add(builder, "Recovery.Reason", Reason(recovery.Reason));
        Add(builder, "Recovery.Attempted", FormatEvidenceValue(recovery.Attempted));
        Add(builder, "Recovery.Completed", FormatEvidenceValue(recovery.Completed));
        Add(builder, "RecoverySteps", recovery.Steps.Count == 0
            ? $"{AvailabilityText(recovery.Availability)}：{Reason(recovery.Reason)}"
            : string.Join(" | ", recovery.Steps.Select(item =>
                $"Step={NonBlank(item.Step, "未命名步骤")}; State={item.State}; Detail={NonBlank(item.Detail, "未提供步骤结果")}")));
        Add(builder, "PostRecoveryVerification", recovery.PostRecoveryVerification.Count == 0
            ? $"{AvailabilityText(recovery.Availability)}：{Reason(recovery.Reason)}"
            : string.Join(" | ", recovery.PostRecoveryVerification));
        Add(builder, "ResultingBusinessBehavior", NonBlank(
            recovery.ResultingBusinessBehavior,
            $"{AvailabilityText(recovery.Availability)}：{Reason(recovery.Reason)}"));
    }

    private static string FormatGuidance(OperationalEvent item, OperationalEventDefinition definition)
    {
        var builder = Section("必须和禁止操作");
        OperatorGuidance first = item.FirstEvidence.Guidance;
        OperatorGuidance latest = item.LatestEvidence.Guidance;
        IReadOnlyList<string> required = MergeStable(
            latest.RequiredActions,
            first.RequiredActions,
            definition.RequiredActions);
        IReadOnlyList<string> forbidden = MergeStable(
            latest.ForbiddenActions,
            first.ForbiddenActions,
            definition.ForbiddenActions);
        string continueCondition = FirstNonBlank(
            TrustedContinueCondition(latest),
            TrustedContinueCondition(first),
            definition.ContinueCondition);

        Add(builder, "Guidance.Availability", $"First={first.Availability}; Latest={latest.Availability}");
        Add(builder, "Guidance.Reason", $"First={Reason(first.Reason)}; Latest={Reason(latest.Reason)}");
        Add(builder, "First.RequiredActions", JoinNumbered(first.RequiredActions, $"{AvailabilityText(first.Availability)}：{Reason(first.Reason)}"));
        Add(builder, "Latest.RequiredActions", JoinNumbered(latest.RequiredActions, $"{AvailabilityText(latest.Availability)}：{Reason(latest.Reason)}"));
        Add(builder, "First.ForbiddenActions", JoinNumbered(first.ForbiddenActions, $"{AvailabilityText(first.Availability)}：{Reason(first.Reason)}"));
        Add(builder, "Latest.ForbiddenActions", JoinNumbered(latest.ForbiddenActions, $"{AvailabilityText(latest.Availability)}：{Reason(latest.Reason)}"));
        Add(builder, "First.ContinueCondition", NonBlank(first.ContinueCondition, $"{AvailabilityText(first.Availability)}：{Reason(first.Reason)}"));
        Add(builder, "Latest.ContinueCondition", NonBlank(latest.ContinueCondition, $"{AvailabilityText(latest.Availability)}：{Reason(latest.Reason)}"));
        Add(builder, "RequiredActions", JoinNumbered(required, "未提供必须操作"));
        Add(builder, "ForbiddenActions", JoinNumbered(forbidden, "未提供禁止操作"));
        Add(builder, "ContinueCondition", NonBlank(continueCondition, "不允许继续：继续条件未明确"));
        return builder.ToString().TrimEnd();
    }

    private static void FormatException(StringBuilder builder, OperationalEvidence evidence)
    {
        ExceptionEvidence item = evidence.Exception;
        Add(builder, "Exception.Availability", item.Availability.ToString());
        Add(builder, "Exception.Reason", Reason(item.Reason));
        if (item.Availability != EvidenceAvailability.Confirmed &&
            string.IsNullOrWhiteSpace(item.Type) &&
            string.IsNullOrWhiteSpace(item.Message) &&
            string.IsNullOrWhiteSpace(item.InnerExceptionChain) &&
            string.IsNullOrWhiteSpace(item.BusinessContext) &&
            string.IsNullOrWhiteSpace(item.StackTrace))
        {
            const string observation = "不适用：该事件来自状态观察/既有文本";
            Add(builder, "Exception.Type", observation);
            Add(builder, "Exception.Message", observation);
            Add(builder, "Exception.InnerExceptionChain", observation);
            Add(builder, "Exception.BusinessContext", observation);
            Add(builder, "Exception.StackTrace", observation);
            return;
        }

        Add(builder, "Exception.Type", NonBlank(item.Type, "未取得异常类型"));
        Add(builder, "Exception.Message", NonBlank(item.Message, "未取得异常消息"));
        Add(builder, "Exception.InnerExceptionChain", NonBlank(item.InnerExceptionChain, "无InnerException"));
        Add(builder, "Exception.BusinessContext", NonBlank(item.BusinessContext, "未提供关联业务上下文"));
        Add(builder, "Exception.StackTrace", NonBlank(item.StackTrace, "未取得StackTrace"));
    }

    private static string FormatPhysicalConclusion(PhysicalConclusionEvidence evidence) =>
        $"Code={evidence.Code}; Availability={evidence.Availability}; Summary={NonBlank(evidence.Summary, "未提供结论摘要")}; Basis={NonBlank(evidence.Basis, "未提供结论依据")}";

    private static string FormatCommitments(
        IReadOnlyList<PhysicalCommitmentEvidence> items,
        EvidenceAvailability availability,
        string reason)
    {
        if (items.Count > 0)
        {
            return string.Join(" | ", items.Select(item =>
                $"Name={NonBlank(item.Name, "未命名承诺点")}; State={FormatEvidenceValue(item.State)}; Detail={NonBlank(item.Detail, "未提供说明")}"));
        }

        return availability switch
        {
            EvidenceAvailability.Confirmed => "已确认无承诺点",
            EvidenceAvailability.NotApplicable => $"不适用：{Reason(reason)}",
            EvidenceAvailability.Unavailable => $"不可用：{Reason(reason)}",
            EvidenceAvailability.Unknown => $"未知：{Reason(reason)}",
            EvidenceAvailability.Inferred => $"推定无承诺点：{Reason(reason)}",
            _ => $"未知：{Reason(reason)}"
        };
    }

    private static IReadOnlyList<string> MergeStable(params IEnumerable<string>[] groups)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (IEnumerable<string> group in groups)
        {
            foreach (string? raw in group ?? [])
            {
                string value = raw?.Trim() ?? string.Empty;
                if (value.Length > 0 && seen.Add(value))
                {
                    result.Add(value);
                }
            }
        }
        return result;
    }

    private static string JoinNumbered(IReadOnlyList<string> values, string fallback) =>
        values.Count == 0
            ? fallback
            : string.Join(" | ", values.Select((value, index) => $"{index + 1}. {value}"));

    private static string ActionId(string? value) => string.IsNullOrWhiteSpace(value) ? "无动作上下文" : value;

    private static StringBuilder Section(string title) => new StringBuilder().Append("=== ").Append(title).AppendLine(" ===");

    private static void Add(StringBuilder builder, string label, string value) =>
        builder.Append(label).Append(": ").AppendLine(NonBlank(value, "未提供"));

    private static string AvailabilityText(EvidenceAvailability availability) => availability switch
    {
        EvidenceAvailability.Confirmed => "已确认",
        EvidenceAvailability.Inferred => "推定",
        EvidenceAvailability.Unknown => "未知",
        EvidenceAvailability.Unavailable => "不可用",
        EvidenceAvailability.NotApplicable => "不适用",
        _ => "未知"
    };

    private static string Reason(string? value) => NonBlank(value, "未提供原因");

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string TrustedContinueCondition(OperatorGuidance guidance) =>
        guidance.Availability is EvidenceAvailability.Confirmed or EvidenceAvailability.Inferred
            ? guidance.ContinueCondition
            : string.Empty;

    private static string NonBlank(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string FormatDate(DateTime? value) => value.HasValue ? FormatDate(value.Value) : "未取得时间";

    private static string FormatDate(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatScalar<T>(T value) => value switch
    {
        null => "null",
        DateTime date => FormatDate(date),
        DateTimeOffset offset => offset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };
}
