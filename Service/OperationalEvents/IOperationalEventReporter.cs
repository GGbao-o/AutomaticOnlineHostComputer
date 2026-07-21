namespace AutomaticOnlineHostComputer.Service.OperationalEvents;

public interface IOperationalEventReporter
{
    void Report(OperationalEventContext context);

    void ObserveTransientFailure(OperationalFailureObservation observation);

    void ObserveRecovery(OperationalRecoveryObservation observation);

    void ObserveStage(OperationalStageObservation observation);
}

public sealed record OperationalFailureObservation(
    string Key,
    OperationalEventContext Context);

public sealed record OperationalRecoveryObservation(
    string Key,
    OperationalEventContext Context);

public enum OperationalStageObservationState
{
    Active,
    Completed,
    Aborted
}

public sealed record OperationalStageObservation(
    string Key,
    string StageCode,
    string ActionId,
    OperationalStageObservationState State,
    TimeSpan? ThresholdOverride,
    OperationalEventContext Context);

public interface IOperationalEventClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemOperationalEventClock : IOperationalEventClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public enum OperationalEventTrackerKind
{
    TransientFailure,
    Stage
}

public interface IOperationalEventStore
{
    event Action? Changed;

    long CurrentVersion { get; }

    OperationalEventDiagnostics GetDiagnosticsSnapshot();

    void Record(
        OperationalEventContext context,
        DateTime occurredAtUtc,
        long observationSequence,
        string fingerprint);

    void RecordReporterFailure(DateTime occurredAtUtc, string reason);

    void RecordStateEviction(OperationalEventTrackerKind trackerKind, DateTime occurredAtUtc);

    OperationalEventStoreSnapshot Snapshot();
}
