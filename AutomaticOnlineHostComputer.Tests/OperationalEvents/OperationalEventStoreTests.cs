using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class OperationalEventStoreTests
{
    [Fact]
    public void Aggregation_window_is_rolling_and_boundary_is_inclusive()
    {
        OperationalEventMonitorOptions options = TestEventFactory.Options(capacity: 10, aggregationSeconds: 600);
        var store = new OperationalEventStore(options);
        DateTime start = DateTime.UnixEpoch;

        store.Record(TestEventFactory.Context(detail: "first"), start, 1, "same");
        store.Record(TestEventFactory.Context(detail: "boundary"), start.AddMinutes(10), 2, "same");
        store.Record(TestEventFactory.Context(detail: "new-window"), start.AddMinutes(20).AddTicks(1), 3, "same");

        OperationalEventStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(2, snapshot.Events.Count);
        Assert.Contains(snapshot.Events, item => item.OccurrenceCount == 2 && item.LatestDetailMessage == "boundary");
        Assert.Contains(snapshot.Events, item => item.OccurrenceCount == 1 && item.LatestDetailMessage == "new-window");
    }

    [Fact]
    public void Out_of_order_observations_keep_first_and_latest_groups_consistent()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        DateTime later = DateTime.UnixEpoch.AddMinutes(5);
        OperationalEventContext latest = TestEventFactory.Context(
            actionId: "latest", detail: "latest-detail", result: "latest-result",
            paused: EvidenceValue<bool>.Confirmed(true, "latest"));
        OperationalEventContext first = TestEventFactory.Context(
            actionId: "first", detail: "first-detail", result: "first-result",
            paused: EvidenceValue<bool>.Confirmed(false, "first"));

        store.Record(latest, later, 1, "same");
        store.Record(first, later.AddMinutes(-1), 2, "same");

        OperationalEvent item = Assert.Single(store.Snapshot().Events);
        Assert.Equal("first", item.FirstActionId);
        Assert.Equal("first-detail", item.FirstDetailMessage);
        Assert.False(item.FirstBusinessPaused.Value);
        Assert.Equal("latest", item.LatestActionId);
        Assert.Equal("latest-detail", item.LatestDetailMessage);
        Assert.True(item.LatestBusinessPaused.Value);
        Assert.Equal(later.AddMinutes(-1), item.FirstOccurredAtUtc);
        Assert.Equal(later, item.LastOccurredAtUtc);
    }

    [Fact]
    public void Equal_timestamps_use_observation_sequence_for_first_and_latest()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        DateTime when = DateTime.UnixEpoch;
        store.Record(TestEventFactory.Context(actionId: "seq-2", detail: "second"), when, 2, "same");
        store.Record(TestEventFactory.Context(actionId: "seq-1", detail: "first"), when, 1, "same");

        OperationalEvent item = Assert.Single(store.Snapshot().Events);
        Assert.Equal("seq-1", item.FirstActionId);
        Assert.Equal("first", item.FirstDetailMessage);
        Assert.Equal("seq-2", item.LatestActionId);
        Assert.Equal("second", item.LatestDetailMessage);
    }

    [Fact]
    public void Capacity_evicts_least_recently_updated_and_old_index_cannot_remove_new_window()
    {
        var store = new OperationalEventStore(TestEventFactory.Options(capacity: 2, aggregationSeconds: 10));
        DateTime start = DateTime.UnixEpoch;
        store.Record(TestEventFactory.Context(eventCode: "A", detail: "old-a"), start, 1, "A");
        store.Record(TestEventFactory.Context(eventCode: "B"), start.AddSeconds(1), 2, "B");
        store.Record(TestEventFactory.Context(eventCode: "A", detail: "new-a"), start.AddSeconds(20), 3, "A");
        store.Record(TestEventFactory.Context(eventCode: "C"), start.AddSeconds(21), 4, "C");
        store.Record(TestEventFactory.Context(eventCode: "A", detail: "aggregate-new-a"), start.AddSeconds(22), 5, "A");

        OperationalEventStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(2, snapshot.Events.Count);
        Assert.Contains(snapshot.Events, item => item.EventCode == "A" && item.OccurrenceCount == 2);
        Assert.Contains(snapshot.Events, item => item.EventCode == "C");
        Assert.Equal(2, snapshot.Diagnostics.EvictedCount);
    }

    [Fact]
    public void Capacity_is_hard_capped_at_five_thousand_items()
    {
        var store = new OperationalEventStore(TestEventFactory.Options(capacity: 5000));
        for (int index = 0; index < 5001; index++)
        {
            store.Record(
                TestEventFactory.Context(eventCode: $"E-{index}"),
                DateTime.UnixEpoch.AddTicks(index),
                index + 1L,
                $"fingerprint-{index}");
        }

        OperationalEventStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(5000, snapshot.Events.Count);
        Assert.Equal(1, snapshot.Diagnostics.EvictedCount);
        Assert.DoesNotContain(snapshot.Events, item => item.EventCode == "E-0");
    }

    [Fact]
    public void Clock_rollback_outside_window_does_not_extend_current_window()
    {
        var store = new OperationalEventStore(TestEventFactory.Options(aggregationSeconds: 10));
        DateTime current = DateTime.UnixEpoch.AddMinutes(1);
        store.Record(TestEventFactory.Context(detail: "current"), current, 1, "same");
        store.Record(TestEventFactory.Context(detail: "old"), current.AddSeconds(-11), 2, "same");
        store.Record(TestEventFactory.Context(detail: "next"), current.AddSeconds(1), 3, "same");

        OperationalEventStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(2, snapshot.Events.Count);
        Assert.Contains(snapshot.Events, item => item.OccurrenceCount == 1 && item.LatestDetailMessage == "old");
        Assert.Contains(snapshot.Events, item => item.OccurrenceCount == 2 && item.FirstDetailMessage == "current" && item.LatestDetailMessage == "next");
    }

    [Fact]
    public void Diagnostics_versions_and_changed_are_precise_and_subscribers_are_isolated()
    {
        var store = new OperationalEventStore(TestEventFactory.Options(capacity: 1));
        int goodSubscriberCalls = 0;
        store.Changed += () => throw new InvalidOperationException("subscriber failure");
        store.Changed += () => goodSubscriberCalls++;

        store.Record(TestEventFactory.Context(eventCode: "A"), DateTime.UnixEpoch, 1, "A");
        store.Record(TestEventFactory.Context(eventCode: "A"), DateTime.UnixEpoch.AddSeconds(1), 2, "A");
        store.Record(TestEventFactory.Context(eventCode: "B"), DateTime.UnixEpoch.AddSeconds(2), 3, "B");
        store.RecordReporterFailure(DateTime.UnixEpoch.AddSeconds(3), "report failure");

        OperationalEventStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(4, snapshot.Version);
        Assert.Equal(3, snapshot.Diagnostics.TotalReceived);
        Assert.Equal(1, snapshot.Diagnostics.AggregatedCount);
        Assert.Equal(1, snapshot.Diagnostics.EvictedCount);
        Assert.Equal(1, snapshot.Diagnostics.ReporterFailureCount);
        Assert.Equal(4, goodSubscriberCalls);
    }

    [Fact]
    public void Concurrent_records_preserve_total_occurrences()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        Parallel.For(0, 500, index =>
            store.Record(TestEventFactory.Context(detail: index.ToString()), DateTime.UnixEpoch, index + 1L, "same"));

        OperationalEventStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(500, snapshot.Diagnostics.TotalReceived);
        Assert.Equal(499, snapshot.Diagnostics.AggregatedCount);
        Assert.Equal(500, Assert.Single(snapshot.Events).OccurrenceCount);
    }

    [Fact]
    public void Store_and_snapshots_deep_freeze_nested_collections()
    {
        var commitments = new List<PhysicalCommitmentEvidence>
        {
            new("持件", EvidenceValue<bool>.Confirmed(true, "local"), "before")
        };
        OperationalEventContext context = TestEventFactory.Context(commitments: commitments);
        var store = new OperationalEventStore(TestEventFactory.Options());
        store.Record(context, DateTime.UnixEpoch, 1, "same");
        commitments.Clear();

        OperationalEventStoreSnapshot first = store.Snapshot();
        Assert.Single(first.Events[0].LatestEvidence.BusinessState.PhysicalCommitments);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PhysicalCommitmentEvidence>)first.Events[0].LatestEvidence.BusinessState.PhysicalCommitments).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<LockItemEvidence>)first.Events[0].LatestEvidence.Locks.Items).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<RecoveryStepEvidence>)first.Events[0].LatestEvidence.Recovery.Steps).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)first.Events[0].LatestEvidence.Recovery.PostRecoveryVerification).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)first.Events[0].LatestEvidence.Guidance.RequiredActions).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)first.Events[0].LatestEvidence.Guidance.ForbiddenActions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<OperationalEvent>)first.Events).Clear());
        Assert.Single(store.Snapshot().Events[0].LatestEvidence.BusinessState.PhysicalCommitments);
    }

    [Fact]
    public void Snapshot_source_keeps_deep_copy_and_sort_outside_store_lock()
    {
        string sourcePath = Path.Combine(
            RepositoryRoot.Find(),
            "Service",
            "OperationalEvents",
            "OperationalEventStore.cs");
        string source = File.ReadAllText(sourcePath);
        int captureStart = source.IndexOf("private SnapshotCapture CaptureSnapshot()", StringComparison.Ordinal);
        int freezeStart = source.IndexOf("private static OperationalEvent[] FreezeAndSort", StringComparison.Ordinal);

        Assert.True(captureStart >= 0);
        Assert.True(freezeStart > captureStart);
        string captureMethod = source[captureStart..freezeStart];
        Assert.Contains("lock (_gate)", captureMethod);
        Assert.DoesNotContain("FreezeEvent", captureMethod);
        Assert.DoesNotContain("OrderBy", captureMethod);
    }
}

internal static class TestEventFactory
{
    public static OperationalEventMonitorOptions Options(
        int capacity = 5000,
        int aggregationSeconds = 600,
        int failureCount = 3,
        int failureSeconds = 30,
        IReadOnlyDictionary<string, TimeSpan>? stages = null) =>
        OperationalEventMonitorOptions.Create(
            capacity,
            TimeSpan.FromSeconds(aggregationSeconds),
            failureCount,
            TimeSpan.FromSeconds(failureSeconds),
            stages ?? new Dictionary<string, TimeSpan> { ["TEST_STAGE"] = TimeSpan.FromSeconds(10) });

    public static OperationalEventContext Context(
        string eventCode = "TEST_EVENT",
        string actionId = "ACT-1",
        string correlationKey = "",
        string plateNo = "P-1",
        string sequence = "001",
        string detail = "detail",
        string result = "result",
        EvidenceValue<bool>? paused = null,
        bool independentAction = false,
        PhysicalConclusionCode conclusion = PhysicalConclusionCode.Unknown,
        OperationalEventSeverity severity = OperationalEventSeverity.Error,
        IReadOnlyList<PhysicalCommitmentEvidence>? commitments = null,
        Exception? exception = null,
        DateTime? occurredAt = null)
    {
        string reason = "test";
        OperationalEvidence unavailable = OperationalEvidence.Unavailable(reason);
        WorkpieceEvidence workpiece = unavailable.Workpiece with
        {
            PlateNo = string.IsNullOrEmpty(plateNo)
                ? EvidenceValue<string>.Unknown("unknown")
                : EvidenceValue<string>.Confirmed(plateNo, reason),
            Sequence = string.IsNullOrEmpty(sequence)
                ? EvidenceValue<string>.Unknown("unknown")
                : EvidenceValue<string>.Confirmed(sequence, reason)
        };
        BusinessStateEvidence business = new(
            unavailable.BusinessState.Availability,
            unavailable.BusinessState.Reason,
            unavailable.BusinessState.LastSuccessfulCheckpoint,
            unavailable.BusinessState.StateBefore,
            unavailable.BusinessState.StateAfter,
            unavailable.BusinessState.CacheBefore,
            unavailable.BusinessState.CacheAfter,
            unavailable.BusinessState.OwnerBefore,
            unavailable.BusinessState.OwnerAfter,
            unavailable.BusinessState.HoldingWorkpiece,
            unavailable.BusinessState.Placed,
            unavailable.BusinessState.CacheNotified,
            commitments ?? unavailable.BusinessState.PhysicalCommitments);

        return new OperationalEventContext
        {
            EventCode = eventCode,
            Severity = severity,
            Category = OperationalEventCategory.FinalFailure,
            Scope = "line-1",
            Engine = "engine-1",
            DeviceType = "PLC",
            DeviceNo = "65",
            Station = "ST1",
            ActionStage = "stage",
            Title = eventCode,
            ActionId = actionId,
            CorrelationKey = correlationKey,
            IndependentAction = independentAction,
            DetailMessage = detail,
            Result = result,
            CapturedException = exception,
            PhysicalConclusion = new(conclusion, EvidenceAvailability.Unknown, conclusion.ToString(), reason),
            BusinessPaused = paused ?? EvidenceValue<bool>.Unknown("not captured"),
            Evidence = unavailable with { Workpiece = workpiece, BusinessState = business },
            OccurredAtUtc = occurredAt
        };
    }
}
