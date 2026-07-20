namespace AutomaticOnlineHostComputer.Service.OperationalEvents;

public sealed record OperationalEventContext
{
    public required string EventCode { get; init; }

    public required OperationalEventSeverity Severity { get; init; }

    public required OperationalEventCategory Category { get; init; }

    public required string Scope { get; init; }

    public required string Engine { get; init; }

    public required string DeviceType { get; init; }

    public required string DeviceNo { get; init; }

    public required string Station { get; init; }

    public required string ActionStage { get; init; }

    public required string Title { get; init; }

    public string Source { get; init; } = string.Empty;

    public string ActionId { get; init; } = string.Empty;

    public string CorrelationKey { get; init; } = string.Empty;

    public bool IndependentAction { get; init; }

    public string DetailMessage { get; init; } = string.Empty;

    public string Result { get; init; } = string.Empty;

    public Exception? CapturedException { get; init; }

    public PhysicalConclusionEvidence PhysicalConclusion { get; init; } = new(
        PhysicalConclusionCode.Unknown,
        EvidenceAvailability.Unknown,
        "物理结论未知",
        "调用点没有确认依据");

    public EvidenceValue<bool> BusinessPaused { get; init; } =
        EvidenceValue<bool>.Unknown("调用点没有提供业务暂停状态");

    public required OperationalEvidence Evidence { get; init; }

    public DateTime? OccurredAtUtc { get; init; }
}

public sealed record OperationalEvent(
    string EventId,
    string FirstActionId,
    string LatestActionId,
    string EventCode,
    OperationalEventSeverity Severity,
    OperationalEventCategory Category,
    DateTime FirstOccurredAtUtc,
    DateTime LastOccurredAtUtc,
    long OccurrenceCount,
    string Scope,
    string Engine,
    string Source,
    string DeviceType,
    string DeviceNo,
    string Station,
    string ActionStage,
    string Title,
    PhysicalConclusionEvidence FirstPhysicalConclusion,
    PhysicalConclusionEvidence LatestPhysicalConclusion,
    EvidenceValue<bool> FirstBusinessPaused,
    EvidenceValue<bool> LatestBusinessPaused,
    string FirstDetailMessage,
    string LatestDetailMessage,
    string FirstResult,
    string LatestResult,
    OperationalEvidence FirstEvidence,
    OperationalEvidence LatestEvidence);

public sealed record OperationalEventDiagnostics(
    long TotalReceived,
    long AggregatedCount,
    long EvictedCount,
    long ReporterFailureCount,
    DateTime? LastReporterFailureAtUtc,
    string LastReporterFailureReason,
    long TransientStateEvictedCount = 0,
    long StageStateEvictedCount = 0);

public sealed record OperationalEventStoreSnapshot
{
    public OperationalEventStoreSnapshot(
        long version,
        IEnumerable<OperationalEvent>? events,
        OperationalEventDiagnostics diagnostics)
    {
        Version = version;
        Events = EvidenceCollection.Freeze(events);
        Diagnostics = diagnostics;
    }

    public long Version { get; }

    public IReadOnlyList<OperationalEvent> Events { get; }

    public OperationalEventDiagnostics Diagnostics { get; }
}
