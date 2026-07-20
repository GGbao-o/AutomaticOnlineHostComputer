using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>需要人工关注的事件类别；不用于普通轮询或重连日志。</summary>
public enum AttentionEventKind
{
    SafetyAlarm,
    Warning,
    Emergency
}

/// <summary>当前程序生命周期内的一条人工关注事件。</summary>
public sealed record AttentionEvent(
    long Sequence,
    DateTime OccurredAt,
    AttentionEventKind Kind,
    string Scope,
    string Source,
    string Message,
    string? Result)
{
    public string KindText => Kind switch
    {
        AttentionEventKind.SafetyAlarm => "红色安全异常",
        AttentionEventKind.Warning => "黄色警告",
        AttentionEventKind.Emergency => "应急操作",
        _ => "未知"
    };

    public string ResultText => string.IsNullOrWhiteSpace(Result) ? "--" : Result;
}

/// <summary>
/// 仅在内存保存当前程序生命周期内的人工关注事件。
/// 事件中心永远不能反向影响设备控制或生产流程。
/// </summary>
public sealed class AttentionEventCenter
{
    public const int Capacity = 500;

    private const string Unknown = "Unknown";
    private const string UnavailableReason = "旧Attention事件入口没有提供该项证据";

    private readonly IOperationalEventStore _store;
    private readonly IOperationalEventReporter _reporter;

    public AttentionEventCenter(
        IOperationalEventStore store,
        IOperationalEventReporter reporter)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _store.Changed += ForwardChanged;
    }

    /// <summary>仅用于通知 UI 刷新；订阅方异常由 Record 兜底吞掉。</summary>
    public event Action? Changed;

    public void Record(AttentionEventKind kind, string scope, string source,
        string message, string? result = null)
    {
        try
        {
            LegacyEventMapping mapping = Map(kind);
            string safeScope = scope ?? string.Empty;
            string safeSource = source ?? string.Empty;
            string safeMessage = message ?? string.Empty;
            _reporter.Report(new OperationalEventContext
            {
                EventCode = mapping.EventCode,
                Severity = mapping.Severity,
                Category = mapping.Category,
                Scope = safeScope,
                Engine = Unknown,
                DeviceType = Unknown,
                DeviceNo = Unknown,
                Station = Unknown,
                ActionStage = Unknown,
                Title = mapping.Title,
                Source = safeSource,
                ActionId = string.Empty,
                CorrelationKey = BuildCorrelationKey(kind, safeScope, safeSource, safeMessage),
                IndependentAction = false,
                DetailMessage = safeMessage,
                Result = result ?? string.Empty,
                CapturedException = null,
                PhysicalConclusion = new PhysicalConclusionEvidence(
                    PhysicalConclusionCode.Unknown,
                    EvidenceAvailability.Unknown,
                    "旧Attention事件未提供物理结论",
                    UnavailableReason),
                BusinessPaused = EvidenceValue<bool>.Unknown("旧Attention事件入口没有暂停参数"),
                Evidence = OperationalEvidence.Unavailable(UnavailableReason)
            });
        }
        catch
        {
            // 监控故障不得影响安全告警、暂停、弹窗或任何设备动作。
        }
    }

    public IReadOnlyList<AttentionEvent> Snapshot()
    {
        try
        {
            return _store.Snapshot().Events
                .Where(IsLegacyEvent)
                .Take(Capacity)
                .Select(MapBack)
                .ToArray();
        }
        catch
        {
            return Array.Empty<AttentionEvent>();
        }
    }

    private static LegacyEventMapping Map(AttentionEventKind kind) => kind switch
    {
        AttentionEventKind.SafetyAlarm => new(
            "LEGACY_SAFETY_ALARM",
            "既有安全异常",
            OperationalEventSeverity.Critical,
            OperationalEventCategory.Safety),
        AttentionEventKind.Warning => new(
            "LEGACY_WARNING",
            "既有黄色警告",
            OperationalEventSeverity.Warning,
            OperationalEventCategory.Safety),
        AttentionEventKind.Emergency => new(
            "LEGACY_EMERGENCY",
            "应急操作结果",
            OperationalEventSeverity.Critical,
            OperationalEventCategory.Emergency),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知Attention事件类别")
    };

    private static bool IsLegacyEvent(OperationalEvent item) => item.EventCode is
        "LEGACY_SAFETY_ALARM" or
        "LEGACY_WARNING" or
        "LEGACY_EMERGENCY";

    private static AttentionEvent MapBack(OperationalEvent item)
    {
        AttentionEventKind kind = item.EventCode switch
        {
            "LEGACY_SAFETY_ALARM" => AttentionEventKind.SafetyAlarm,
            "LEGACY_WARNING" => AttentionEventKind.Warning,
            "LEGACY_EMERGENCY" => AttentionEventKind.Emergency,
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.EventCode, "未知legacy事件代码")
        };

        return new AttentionEvent(
            item.LastOccurredAtUtc.Ticks,
            item.LastOccurredAtUtc.ToLocalTime(),
            kind,
            item.Scope,
            item.Source,
            item.LatestDetailMessage,
            string.IsNullOrEmpty(item.LatestResult) ? null : item.LatestResult);
    }

    private static string BuildCorrelationKey(
        AttentionEventKind kind,
        string scope,
        string source,
        string message) =>
        Encode(kind.ToString(), scope, source, NormalizeMessage(message));

    private static string NormalizeMessage(string message)
    {
        var normalized = new StringBuilder(message.Length);
        bool pendingSpace = false;
        foreach (char character in message.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }

            normalized.Append(character);
        }

        return normalized.ToString();
    }

    private static string Encode(params string[] values)
    {
        var encoded = new StringBuilder("legacy:");
        foreach (string value in values)
        {
            encoded.Append(value.Length);
            encoded.Append(':');
            encoded.Append(value);
            encoded.Append(';');
        }

        return encoded.ToString();
    }

    private void ForwardChanged()
    {
        Action? changed = Changed;
        if (changed is null)
        {
            return;
        }

        foreach (Action subscriber in changed.GetInvocationList().Cast<Action>())
        {
            try
            {
                subscriber();
            }
            catch
            {
                // 一个只读订阅者的故障不能影响其他订阅者或事件上报。
            }
        }
    }

    private sealed record LegacyEventMapping(
        string EventCode,
        string Title,
        OperationalEventSeverity Severity,
        OperationalEventCategory Category);
}
