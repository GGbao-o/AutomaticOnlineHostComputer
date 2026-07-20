namespace AutomaticOnlineHostComputer.Service.OperationalEvents;

public sealed class OperationalEventStore : IOperationalEventStore
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly TimeSpan _aggregationWindow;
    private readonly LinkedList<StoredEvent> _events = new();
    private readonly Dictionary<string, LinkedListNode<StoredEvent>> _currentByFingerprint =
        new(StringComparer.Ordinal);

    private long _version;
    private long _totalReceived;
    private long _aggregatedCount;
    private long _evictedCount;
    private long _reporterFailureCount;
    private long _transientStateEvictedCount;
    private long _stageStateEvictedCount;
    private DateTime? _lastReporterFailureAtUtc;
    private string _lastReporterFailureReason = string.Empty;

    public OperationalEventStore(OperationalEventMonitorOptions? options = null)
    {
        OperationalEventMonitorOptions selected = options ?? OperationalEventMonitorOptions.Default;
        _capacity = selected.Capacity;
        _aggregationWindow = selected.AggregationWindow;
    }

    public event Action? Changed;

    public void Record(
        OperationalEventContext context,
        DateTime occurredAtUtc,
        long observationSequence,
        string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Evidence);

        OperationalEventContext frozen = FreezeContext(context);
        string normalizedFingerprint = fingerprint ?? string.Empty;
        Action? changed;

        lock (_gate)
        {
            _totalReceived++;
            if (_currentByFingerprint.TryGetValue(normalizedFingerprint, out LinkedListNode<StoredEvent>? node) &&
                IsWithinAggregationWindow(node.Value.Event, occurredAtUtc))
            {
                node.Value = Aggregate(node.Value, frozen, occurredAtUtc, observationSequence);
                _events.Remove(node);
                _events.AddLast(node);
                _aggregatedCount++;
            }
            else
            {
                OperationalEvent item = CreateEvent(frozen, occurredAtUtc);
                var stored = new StoredEvent(
                    item,
                    normalizedFingerprint,
                    observationSequence,
                    observationSequence);
                LinkedListNode<StoredEvent> newNode = _events.AddLast(stored);
                if (node is null || occurredAtUtc >= node.Value.Event.LastOccurredAtUtc)
                {
                    _currentByFingerprint[normalizedFingerprint] = newNode;
                }
            }

            while (_events.Count > _capacity)
            {
                LinkedListNode<StoredEvent> oldest = _events.First!;
                _events.RemoveFirst();
                if (_currentByFingerprint.TryGetValue(oldest.Value.Fingerprint, out LinkedListNode<StoredEvent>? indexed) &&
                    ReferenceEquals(indexed, oldest))
                {
                    _currentByFingerprint.Remove(oldest.Value.Fingerprint);
                }

                _evictedCount++;
            }

            _version++;
            changed = Changed;
        }

        PublishChanged(changed);
    }

    public void RecordReporterFailure(DateTime occurredAtUtc, string reason)
    {
        Action? changed;
        lock (_gate)
        {
            _reporterFailureCount++;
            _lastReporterFailureAtUtc = occurredAtUtc;
            _lastReporterFailureReason = reason ?? string.Empty;
            _version++;
            changed = Changed;
        }

        PublishChanged(changed);
    }

    public void RecordStateEviction(OperationalEventTrackerKind trackerKind, DateTime occurredAtUtc)
    {
        Action? changed;
        lock (_gate)
        {
            if (trackerKind == OperationalEventTrackerKind.Stage)
            {
                _stageStateEvictedCount++;
            }
            else
            {
                _transientStateEvictedCount++;
            }

            _version++;
            changed = Changed;
        }

        PublishChanged(changed);
    }

    public OperationalEventStoreSnapshot Snapshot()
    {
        lock (_gate)
        {
            OperationalEvent[] events = _events
                .Select(stored => (Event: FreezeEvent(stored.Event), stored.LatestSequence))
                .OrderByDescending(entry => entry.Event.LastOccurredAtUtc)
                .ThenByDescending(entry => entry.LatestSequence)
                .Select(entry => entry.Event)
                .ToArray();
            var diagnostics = new OperationalEventDiagnostics(
                _totalReceived,
                _aggregatedCount,
                _evictedCount,
                _reporterFailureCount,
                _lastReporterFailureAtUtc,
                _lastReporterFailureReason,
                _transientStateEvictedCount,
                _stageStateEvictedCount);
            return new OperationalEventStoreSnapshot(_version, events, diagnostics);
        }
    }

    private bool IsWithinAggregationWindow(OperationalEvent current, DateTime occurredAtUtc)
    {
        TimeSpan distance = occurredAtUtc >= current.LastOccurredAtUtc
            ? occurredAtUtc - current.LastOccurredAtUtc
            : current.LastOccurredAtUtc - occurredAtUtc;
        return distance <= _aggregationWindow;
    }

    private static OperationalEvent CreateEvent(OperationalEventContext context, DateTime occurredAtUtc) =>
        new(
            $"OE-{Guid.NewGuid():N}",
            context.ActionId ?? string.Empty,
            context.ActionId ?? string.Empty,
            context.EventCode ?? string.Empty,
            context.Severity,
            context.Category,
            occurredAtUtc,
            occurredAtUtc,
            1,
            context.Scope ?? string.Empty,
            context.Engine ?? string.Empty,
            context.Source ?? string.Empty,
            context.DeviceType ?? string.Empty,
            context.DeviceNo ?? string.Empty,
            context.Station ?? string.Empty,
            context.ActionStage ?? string.Empty,
            context.Title ?? string.Empty,
            context.PhysicalConclusion,
            context.PhysicalConclusion,
            context.BusinessPaused,
            context.BusinessPaused,
            context.DetailMessage ?? string.Empty,
            context.DetailMessage ?? string.Empty,
            context.Result ?? string.Empty,
            context.Result ?? string.Empty,
            context.Evidence,
            context.Evidence);

    private static StoredEvent Aggregate(
        StoredEvent stored,
        OperationalEventContext incoming,
        DateTime occurredAtUtc,
        long observationSequence)
    {
        OperationalEvent current = stored.Event;
        bool replaceFirst = occurredAtUtc < current.FirstOccurredAtUtc ||
            (occurredAtUtc == current.FirstOccurredAtUtc && observationSequence < stored.FirstSequence);
        bool replaceLatest = occurredAtUtc > current.LastOccurredAtUtc ||
            (occurredAtUtc == current.LastOccurredAtUtc && observationSequence > stored.LatestSequence);

        OperationalEvent updated = current with
        {
            FirstOccurredAtUtc = replaceFirst ? occurredAtUtc : current.FirstOccurredAtUtc,
            LastOccurredAtUtc = replaceLatest ? occurredAtUtc : current.LastOccurredAtUtc,
            OccurrenceCount = current.OccurrenceCount + 1,
            FirstActionId = replaceFirst ? incoming.ActionId ?? string.Empty : current.FirstActionId,
            LatestActionId = replaceLatest ? incoming.ActionId ?? string.Empty : current.LatestActionId,
            FirstPhysicalConclusion = replaceFirst ? incoming.PhysicalConclusion : current.FirstPhysicalConclusion,
            LatestPhysicalConclusion = replaceLatest ? incoming.PhysicalConclusion : current.LatestPhysicalConclusion,
            FirstBusinessPaused = replaceFirst ? incoming.BusinessPaused : current.FirstBusinessPaused,
            LatestBusinessPaused = replaceLatest ? incoming.BusinessPaused : current.LatestBusinessPaused,
            FirstDetailMessage = replaceFirst ? incoming.DetailMessage ?? string.Empty : current.FirstDetailMessage,
            LatestDetailMessage = replaceLatest ? incoming.DetailMessage ?? string.Empty : current.LatestDetailMessage,
            FirstResult = replaceFirst ? incoming.Result ?? string.Empty : current.FirstResult,
            LatestResult = replaceLatest ? incoming.Result ?? string.Empty : current.LatestResult,
            FirstEvidence = replaceFirst ? incoming.Evidence : current.FirstEvidence,
            LatestEvidence = replaceLatest ? incoming.Evidence : current.LatestEvidence
        };

        return stored with
        {
            Event = updated,
            FirstSequence = replaceFirst ? observationSequence : stored.FirstSequence,
            LatestSequence = replaceLatest ? observationSequence : stored.LatestSequence
        };
    }

    private static OperationalEventContext FreezeContext(OperationalEventContext context) => context with
    {
        CapturedException = null,
        BusinessPaused = context.BusinessPaused with { },
        PhysicalConclusion = context.PhysicalConclusion with { },
        Evidence = FreezeEvidence(context.Evidence)
    };

    private static OperationalEvent FreezeEvent(OperationalEvent item) => item with
    {
        FirstBusinessPaused = item.FirstBusinessPaused with { },
        LatestBusinessPaused = item.LatestBusinessPaused with { },
        FirstPhysicalConclusion = item.FirstPhysicalConclusion with { },
        LatestPhysicalConclusion = item.LatestPhysicalConclusion with { },
        FirstEvidence = FreezeEvidence(item.FirstEvidence),
        LatestEvidence = FreezeEvidence(item.LatestEvidence)
    };

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

    private static void PublishChanged(Action? changed)
    {
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
                // A read-only observer cannot affect reporting or other observers.
            }
        }
    }

    private sealed record StoredEvent(
        OperationalEvent Event,
        string Fingerprint,
        long FirstSequence,
        long LatestSequence);
}
