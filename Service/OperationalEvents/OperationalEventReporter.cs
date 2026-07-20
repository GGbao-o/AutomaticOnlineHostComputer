using System.Text;

namespace AutomaticOnlineHostComputer.Service.OperationalEvents;

public sealed class OperationalEventReporter : IOperationalEventReporter
{
    private readonly IOperationalEventStore _store;
    private readonly OperationalEventMonitorOptions _options;
    private readonly IOperationalEventClock _clock;
    private readonly object _stateGate = new();
    private readonly Dictionary<string, FailureCycle> _failureCycles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StageCycle> _stageCycles = new(StringComparer.Ordinal);
    private readonly int _trackerCapacity;
    private long _observationSequence;

    public OperationalEventReporter(
        IOperationalEventStore store,
        OperationalEventMonitorOptions? options = null,
        IOperationalEventClock? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? OperationalEventMonitorOptions.Default;
        _clock = clock ?? new SystemOperationalEventClock();
        _trackerCapacity = _options.Capacity;
    }

    public void Report(OperationalEventContext context)
    {
        long sequence = Interlocked.Increment(ref _observationSequence);
        try
        {
            RecordCore(context, sequence);
        }
        catch (Exception exception)
        {
            RecordFailureSafely(exception);
        }
    }

    public void ObserveTransientFailure(OperationalFailureObservation observation)
    {
        long sequence = Interlocked.Increment(ref _observationSequence);
        try
        {
            ArgumentNullException.ThrowIfNull(observation);
            ArgumentNullException.ThrowIfNull(observation.Context);
            DateTime occurredAtUtc = ResolveOccurredAt(observation.Context);
            OperationalEventContext frozenObservationContext = NormalizeAndFreeze(observation.Context);
            string stateKey = BuildFailureStateKey(observation.Key, frozenObservationContext);
            OperationalEventContext? contextToRecord = null;
            bool stateEvicted = false;

            lock (_stateGate)
            {
                if (!_failureCycles.TryGetValue(stateKey, out FailureCycle? cycle))
                {
                    cycle = new FailureCycle(
                        occurredAtUtc,
                        sequence,
                        0,
                        false,
                        sequence,
                        occurredAtUtc,
                        sequence,
                        frozenObservationContext);
                }

                int count = checked(cycle.Count + 1);
                bool replaceFirst = IsEarlier(
                    occurredAtUtc,
                    sequence,
                    cycle.FirstFailureAtUtc,
                    cycle.FirstSequence);
                bool replaceLatest = IsLater(
                    occurredAtUtc,
                    sequence,
                    cycle.LatestOccurredAtUtc,
                    cycle.LatestSequence);
                DateTime firstFailureAtUtc = replaceFirst ? occurredAtUtc : cycle.FirstFailureAtUtc;
                long firstSequence = replaceFirst ? sequence : cycle.FirstSequence;
                DateTime latestOccurredAtUtc = replaceLatest ? occurredAtUtc : cycle.LatestOccurredAtUtc;
                long latestSequence = replaceLatest ? sequence : cycle.LatestSequence;
                OperationalEventContext latestContext = replaceLatest
                    ? frozenObservationContext
                    : cycle.LatestContext;
                bool durationReached = latestOccurredAtUtc >= firstFailureAtUtc &&
                    latestOccurredAtUtc - firstFailureAtUtc >= _options.TransientFailureDuration;
                bool thresholdReached = count >= _options.TransientFailureCountThreshold || durationReached;
                var updated = cycle with
                {
                    FirstFailureAtUtc = firstFailureAtUtc,
                    FirstSequence = firstSequence,
                    Count = count,
                    Reported = cycle.Reported || thresholdReached,
                    LastTouchedSequence = Math.Max(cycle.LastTouchedSequence, sequence),
                    LatestOccurredAtUtc = latestOccurredAtUtc,
                    LatestSequence = latestSequence,
                    LatestContext = latestContext
                };
                _failureCycles[stateKey] = updated;
                if (thresholdReached)
                {
                    bool firstThresholdEmission = !cycle.Reported;
                    OperationalEventContext eventContext = firstThresholdEmission
                        ? updated.LatestContext
                        : frozenObservationContext;
                    DateTime eventOccurredAtUtc = firstThresholdEmission
                        ? updated.LatestOccurredAtUtc
                        : occurredAtUtc;
                    contextToRecord = WithFailureCycle(
                        eventContext,
                        updated.FirstFailureAtUtc,
                        eventOccurredAtUtc,
                        count,
                        "连续失败达到记录阈值");
                    occurredAtUtc = eventOccurredAtUtc;
                    sequence = firstThresholdEmission ? updated.LatestSequence : sequence;
                }

                stateEvicted = TrimOldest(_failureCycles, stateKey);
            }

            if (stateEvicted)
            {
                RecordStateEvictionSafely(OperationalEventTrackerKind.TransientFailure, occurredAtUtc);
            }

            if (contextToRecord is not null)
            {
                RecordCore(contextToRecord, sequence, occurredAtUtc);
            }
        }
        catch (Exception exception)
        {
            RecordFailureSafely(exception);
        }
    }

    public void ObserveRecovery(OperationalRecoveryObservation observation)
    {
        long sequence = Interlocked.Increment(ref _observationSequence);
        try
        {
            ArgumentNullException.ThrowIfNull(observation);
            ArgumentNullException.ThrowIfNull(observation.Context);
            DateTime occurredAtUtc = ResolveOccurredAt(observation.Context);
            string stateKey = BuildFailureStateKey(observation.Key, observation.Context);
            FailureCycle? removedCycle;

            lock (_stateGate)
            {
                _failureCycles.Remove(stateKey, out removedCycle);
            }

            if (removedCycle is { Reported: true })
            {
                OperationalEventContext recovery = WithFailureCycle(
                    observation.Context,
                    removedCycle.FirstFailureAtUtc,
                    occurredAtUtc,
                    removedCycle.Count,
                    "连续失败周期已恢复");
                RecordCore(recovery, sequence, occurredAtUtc);
            }
        }
        catch (Exception exception)
        {
            RecordFailureSafely(exception);
        }
    }

    public void ObserveStage(OperationalStageObservation observation)
    {
        long sequence = Interlocked.Increment(ref _observationSequence);
        try
        {
            ArgumentNullException.ThrowIfNull(observation);
            ArgumentNullException.ThrowIfNull(observation.Context);
            DateTime occurredAtUtc = ResolveOccurredAt(observation.Context);
            string stateKey = BuildStageStateKey(observation);
            OperationalEventContext? contextToRecord = null;
            bool stateEvicted = false;

            lock (_stateGate)
            {
                switch (observation.State)
                {
                    case OperationalStageObservationState.Active:
                    {
                        TimeSpan observedThreshold = observation.ThresholdOverride is { } value && value > TimeSpan.Zero
                            ? value
                            : _options.GetStageThreshold(observation.StageCode);
                        if (!_stageCycles.TryGetValue(stateKey, out StageCycle? cycle))
                        {
                            cycle = new StageCycle(occurredAtUtc, sequence, observedThreshold, false, sequence);
                        }

                        bool replaceStart = IsEarlier(
                            occurredAtUtc,
                            sequence,
                            cycle.StartedAtUtc,
                            cycle.StartSequence);
                        DateTime startedAtUtc = replaceStart ? occurredAtUtc : cycle.StartedAtUtc;
                        long startSequence = replaceStart ? sequence : cycle.StartSequence;
                        TimeSpan threshold = replaceStart ? observedThreshold : cycle.Threshold;
                        bool thresholdReached = occurredAtUtc >= startedAtUtc &&
                            occurredAtUtc - startedAtUtc >= threshold;
                        bool reportNow = thresholdReached && !cycle.Reported;
                        _stageCycles[stateKey] = cycle with
                        {
                            StartedAtUtc = startedAtUtc,
                            StartSequence = startSequence,
                            Threshold = threshold,
                            Reported = cycle.Reported || thresholdReached,
                            LastTouchedSequence = Math.Max(cycle.LastTouchedSequence, sequence)
                        };
                        if (reportNow)
                        {
                            contextToRecord = WithElapsed(observation.Context, startedAtUtc, occurredAtUtc, "阶段滞留");
                        }

                        stateEvicted = TrimOldest(_stageCycles, stateKey);
                        break;
                    }
                    case OperationalStageObservationState.Completed:
                    {
                        if (_stageCycles.Remove(stateKey, out StageCycle? completed) && completed.Reported)
                        {
                            contextToRecord = WithElapsed(observation.Context, completed.StartedAtUtc, occurredAtUtc, "阶段已恢复");
                        }

                        break;
                    }
                    case OperationalStageObservationState.Aborted:
                    {
                        if (_stageCycles.Remove(stateKey, out StageCycle? aborted) && aborted.Reported)
                        {
                            contextToRecord = WithElapsed(observation.Context, aborted.StartedAtUtc, occurredAtUtc, "阶段跟踪已中止");
                        }

                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(observation.State));
                }
            }

            if (stateEvicted)
            {
                RecordStateEvictionSafely(OperationalEventTrackerKind.Stage, occurredAtUtc);
            }

            if (contextToRecord is not null)
            {
                RecordCore(contextToRecord, sequence, occurredAtUtc);
            }
        }
        catch (Exception exception)
        {
            RecordFailureSafely(exception);
        }
    }

    private void RecordCore(
        OperationalEventContext context,
        long sequence,
        DateTime? knownOccurredAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        DateTime occurredAtUtc = knownOccurredAtUtc ?? ResolveOccurredAt(context);
        OperationalEventContext normalized = NormalizeAndFreeze(context);
        string fingerprint = BuildFingerprint(normalized);
        _store.Record(normalized, occurredAtUtc, sequence, fingerprint);
    }

    private DateTime ResolveOccurredAt(OperationalEventContext context) =>
        context.OccurredAtUtc ?? _clock.UtcNow;

    private static OperationalEventContext NormalizeAndFreeze(OperationalEventContext context)
    {
        OperationalEvidence source = context.Evidence ?? OperationalEvidence.Unavailable("调用点没有提供证据对象");
        ExceptionEvidence exceptionEvidence = context.CapturedException is null
            ? source.Exception with { }
            : NormalizeException(context.CapturedException, source.Exception.BusinessContext);
        OperationalEvidence evidence = FreezeEvidence(source with { Exception = exceptionEvidence });
        return context with
        {
            CapturedException = null,
            EventCode = context.EventCode ?? string.Empty,
            Scope = context.Scope ?? string.Empty,
            Engine = context.Engine ?? string.Empty,
            Source = context.Source ?? string.Empty,
            DeviceType = context.DeviceType ?? string.Empty,
            DeviceNo = context.DeviceNo ?? string.Empty,
            Station = context.Station ?? string.Empty,
            ActionStage = context.ActionStage ?? string.Empty,
            Title = context.Title ?? string.Empty,
            ActionId = context.ActionId ?? string.Empty,
            CorrelationKey = context.CorrelationKey ?? string.Empty,
            DetailMessage = context.DetailMessage ?? string.Empty,
            Result = context.Result ?? string.Empty,
            PhysicalConclusion = context.PhysicalConclusion ?? new PhysicalConclusionEvidence(
                PhysicalConclusionCode.Unknown,
                EvidenceAvailability.Unknown,
                "物理结论未知",
                "调用点没有提供物理结论"),
            BusinessPaused = context.BusinessPaused ?? EvidenceValue<bool>.Unknown("调用点没有提供业务暂停状态"),
            Evidence = evidence
        };
    }

    private static ExceptionEvidence NormalizeException(Exception exception, string businessContext)
    {
        string type = SafeRead(() => exception.GetType().FullName ?? exception.GetType().Name);
        string message = SafeRead(() => exception.Message);
        string stackTrace = SafeRead(() => exception.StackTrace ?? string.Empty);
        var inner = new StringBuilder();
        Exception? current = exception.InnerException;
        int depth = 0;
        while (current is not null && depth++ < 32)
        {
            if (inner.Length > 0)
            {
                inner.Append(" -> ");
            }

            inner.Append(SafeRead(() => current.GetType().FullName ?? current.GetType().Name));
            inner.Append(": ");
            inner.Append(SafeRead(() => current.Message));
            current = current.InnerException;
        }

        if (current is not null)
        {
            inner.Append(" -> [inner exception chain truncated]");
        }

        return new ExceptionEvidence(
            type,
            message,
            inner.ToString(),
            businessContext ?? string.Empty,
            stackTrace)
        {
            Availability = EvidenceAvailability.Confirmed,
            Reason = "由Reporter从调用点捕获的异常对象规范化"
        };
    }

    private static string SafeRead(Func<string> reader)
    {
        try
        {
            return reader() ?? string.Empty;
        }
        catch (Exception exception)
        {
            return $"[读取失败: {exception.GetType().Name}]";
        }
    }

    private string BuildFingerprint(OperationalEventContext context)
    {
        string workpieceIdentity = WorkpieceIdentity(context.Evidence.Workpiece);
        string actionCorrelation = string.Empty;
        bool unknownWorkpiece = string.IsNullOrEmpty(workpieceIdentity);
        if (context.IndependentAction || unknownWorkpiece)
        {
            if (!string.IsNullOrWhiteSpace(context.ActionId))
            {
                actionCorrelation = context.ActionId;
            }
            else if (!string.IsNullOrWhiteSpace(context.CorrelationKey))
            {
                actionCorrelation = context.CorrelationKey;
            }
            else
            {
                actionCorrelation = $"nonce:{Guid.NewGuid():N}";
            }
        }

        return Encode(
            context.EventCode,
            context.Scope,
            context.Engine,
            context.Source,
            context.Category.ToString(),
            context.DeviceType,
            context.DeviceNo,
            context.Station,
            context.ActionStage,
            context.Title,
            workpieceIdentity,
            context.CorrelationKey,
            actionCorrelation,
            context.PhysicalConclusion.Code.ToString(),
            context.Severity.ToString());
    }

    private static string WorkpieceIdentity(WorkpieceEvidence workpiece)
    {
        if (!workpiece.PlateNo.HasValue || !workpiece.Sequence.HasValue ||
            string.IsNullOrWhiteSpace(workpiece.PlateNo.Value) ||
            string.IsNullOrWhiteSpace(workpiece.Sequence.Value))
        {
            return string.Empty;
        }

        return Encode(workpiece.PlateNo.Value, workpiece.Sequence.Value);
    }

    private static string BuildFailureStateKey(string key, OperationalEventContext context) =>
        Encode(
            key,
            context.Scope,
            context.Engine,
            context.DeviceType,
            context.DeviceNo,
            context.Station,
            context.CorrelationKey,
            context.IndependentAction ? context.ActionId : string.Empty);

    private static string BuildStageStateKey(OperationalStageObservation observation) =>
        Encode(
            observation.Key,
            observation.StageCode,
            observation.ActionId,
            observation.Context.Scope,
            observation.Context.Engine,
            observation.Context.DeviceType,
            observation.Context.DeviceNo,
            observation.Context.Station);

    private static string Encode(params string?[] values)
    {
        var builder = new StringBuilder();
        foreach (string? raw in values)
        {
            string value = raw ?? string.Empty;
            builder.Append(value.Length);
            builder.Append(':');
            builder.Append(value);
            builder.Append(';');
        }

        return builder.ToString();
    }

    private static OperationalEventContext WithElapsed(
        OperationalEventContext context,
        DateTime startedAtUtc,
        DateTime endedAtUtc,
        string outcome)
    {
        TimeSpan elapsed = endedAtUtc >= startedAtUtc ? endedAtUtc - startedAtUtc : TimeSpan.Zero;
        string elapsedText = $"总滞留时长={elapsed:c}";
        string detail = string.IsNullOrWhiteSpace(context.DetailMessage)
            ? elapsedText
            : $"{context.DetailMessage}; {elapsedText}";
        string result = string.IsNullOrWhiteSpace(context.Result)
            ? outcome
            : $"{context.Result}; {outcome}";
        return context with { DetailMessage = detail, Result = result };
    }

    private static OperationalEventContext WithFailureCycle(
        OperationalEventContext context,
        DateTime startedAtUtc,
        DateTime endedAtUtc,
        int failureCount,
        string outcome)
    {
        TimeSpan elapsed = endedAtUtc >= startedAtUtc ? endedAtUtc - startedAtUtc : TimeSpan.Zero;
        string cycleText = $"连续失败次数={failureCount}; 周期时长={elapsed:c}";
        string detail = string.IsNullOrWhiteSpace(context.DetailMessage)
            ? cycleText
            : $"{context.DetailMessage}; {cycleText}";
        string result = string.IsNullOrWhiteSpace(context.Result)
            ? outcome
            : $"{context.Result}; {outcome}";
        return context with { DetailMessage = detail, Result = result };
    }

    private bool TrimOldest<TCycle>(Dictionary<string, TCycle> cycles, string justTouchedKey)
        where TCycle : ITrackerCycle
    {
        if (cycles.Count <= _trackerCapacity)
        {
            return false;
        }

        string? oldestKey = null;
        long oldestSequence = long.MaxValue;
        foreach ((string key, TCycle cycle) in cycles)
        {
            if (key == justTouchedKey && cycles.Count > 1)
            {
                continue;
            }

            if (cycle.LastTouchedSequence < oldestSequence)
            {
                oldestSequence = cycle.LastTouchedSequence;
                oldestKey = key;
            }
        }

        if (oldestKey is null)
        {
            return false;
        }

        cycles.Remove(oldestKey);
        return true;
    }

    private void RecordFailureSafely(Exception exception)
    {
        try
        {
            DateTime occurredAtUtc = _clock.UtcNow;
            _store.RecordReporterFailure(
                occurredAtUtc,
                $"{exception.GetType().FullName}: {SafeRead(() => exception.Message)}");
        }
        catch
        {
            // Monitoring must never affect production behavior, including diagnostic failures.
        }
    }

    private void RecordStateEvictionSafely(
        OperationalEventTrackerKind trackerKind,
        DateTime occurredAtUtc)
    {
        try
        {
            _store.RecordStateEviction(trackerKind, occurredAtUtc);
        }
        catch (Exception exception)
        {
            try
            {
                _store.RecordReporterFailure(
                    occurredAtUtc,
                    $"{exception.GetType().FullName}: {SafeRead(() => exception.Message)}");
            }
            catch
            {
                // Diagnostic failure is isolated from the formal event write that follows.
            }
        }
    }

    private static bool IsEarlier(
        DateTime candidateTime,
        long candidateSequence,
        DateTime currentTime,
        long currentSequence) =>
        candidateTime < currentTime ||
        (candidateTime == currentTime && candidateSequence < currentSequence);

    private static bool IsLater(
        DateTime candidateTime,
        long candidateSequence,
        DateTime currentTime,
        long currentSequence) =>
        candidateTime > currentTime ||
        (candidateTime == currentTime && candidateSequence > currentSequence);

    private static OperationalEvidence FreezeEvidence(OperationalEvidence evidence)
    {
        BusinessStateEvidence business = evidence.BusinessState;
        LockEvidence locks = evidence.Locks;
        RecoveryEvidence recovery = evidence.Recovery;
        OperatorGuidance guidance = evidence.Guidance;
        return evidence with
        {
            BusinessState = new BusinessStateEvidence(
                business.Availability,
                business.Reason,
                business.LastSuccessfulCheckpoint,
                business.StateBefore,
                business.StateAfter,
                business.CacheBefore,
                business.CacheAfter,
                business.OwnerBefore,
                business.OwnerAfter,
                business.HoldingWorkpiece,
                business.Placed,
                business.CacheNotified,
                business.PhysicalCommitments.Select(item => item with { State = item.State with { } }).ToArray()),
            Locks = new LockEvidence(
                locks.Availability,
                locks.Reason,
                locks.Items.Select(item => item with
                {
                    HeldAtFailure = item.HeldAtFailure with { },
                    ReleasedAfterward = item.ReleasedAfterward with { }
                }).ToArray()),
            Recovery = new RecoveryEvidence(
                recovery.Availability,
                recovery.Reason,
                recovery.Attempted with { },
                recovery.Completed with { },
                recovery.Steps.Select(item => item with { }).ToArray(),
                recovery.PostRecoveryVerification.ToArray(),
                recovery.ResultingBusinessBehavior),
            Guidance = new OperatorGuidance(
                guidance.Availability,
                guidance.Reason,
                guidance.RequiredActions.ToArray(),
                guidance.ForbiddenActions.ToArray(),
                guidance.ContinueCondition),
            Exception = evidence.Exception with { }
        };
    }

    private interface ITrackerCycle
    {
        long LastTouchedSequence { get; }
    }

    private sealed record FailureCycle(
        DateTime FirstFailureAtUtc,
        long FirstSequence,
        int Count,
        bool Reported,
        long LastTouchedSequence,
        DateTime LatestOccurredAtUtc,
        long LatestSequence,
        OperationalEventContext LatestContext) : ITrackerCycle;

    private sealed record StageCycle(
        DateTime StartedAtUtc,
        long StartSequence,
        TimeSpan Threshold,
        bool Reported,
        long LastTouchedSequence) : ITrackerCycle;
}
