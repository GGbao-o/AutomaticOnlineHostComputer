using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class OperationalEventReporterTests
{
    [Fact]
    public void Fingerprint_separates_workpiece_severity_and_physical_conclusion()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        var reporter = new OperationalEventReporter(store, TestEventFactory.Options(), new FakeClock());

        reporter.Report(TestEventFactory.Context(plateNo: "P1", sequence: "1"));
        reporter.Report(TestEventFactory.Context(plateNo: "P2", sequence: "1"));
        reporter.Report(TestEventFactory.Context(plateNo: "P1", sequence: "1", severity: OperationalEventSeverity.Warning));
        reporter.Report(TestEventFactory.Context(plateNo: "P1", sequence: "1", conclusion: PhysicalConclusionCode.CommandResultUnknown));

        Assert.Equal(4, store.Snapshot().Events.Count);
    }

    [Fact]
    public void Fingerprint_separates_source_category_and_title_even_with_same_correlation_key()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        var reporter = new OperationalEventReporter(store, TestEventFactory.Options(), new FakeClock());
        OperationalEventContext baseline = TestEventFactory.Context(
            plateNo: "", sequence: "", actionId: "", correlationKey: "legacy-key");

        reporter.Report(baseline with { Source = "source-a", Title = "title-a" });
        reporter.Report(baseline with { Source = "source-b", Title = "title-a" });
        reporter.Report(baseline with { Source = "source-a", Title = "title-b" });
        reporter.Report(baseline with
        {
            Source = "source-a",
            Title = "title-a",
            Category = OperationalEventCategory.Safety
        });

        Assert.Equal(4, store.Snapshot().Events.Count);
    }

    [Fact]
    public void Fingerprint_length_prefix_prevents_separator_collisions()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        var reporter = new OperationalEventReporter(store, TestEventFactory.Options(), new FakeClock());
        OperationalEventContext baseline = TestEventFactory.Context();
        reporter.Report(baseline with { EventCode = "a\u001fb", Scope = "c" });
        reporter.Report(baseline with { EventCode = "a", Scope = "b\u001fc" });
        Assert.Equal(2, store.Snapshot().Events.Count);
    }

    [Fact]
    public void Unknown_workpiece_correlation_rules_are_truthful()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        var reporter = new OperationalEventReporter(store, TestEventFactory.Options(), new FakeClock());

        reporter.Report(TestEventFactory.Context(plateNo: "", sequence: "", actionId: "A"));
        reporter.Report(TestEventFactory.Context(plateNo: "", sequence: "", actionId: "B"));
        reporter.Report(TestEventFactory.Context(plateNo: "", sequence: "", actionId: "A"));
        reporter.Report(TestEventFactory.Context(plateNo: "", sequence: "", actionId: ""));
        reporter.Report(TestEventFactory.Context(plateNo: "", sequence: "", actionId: ""));

        OperationalEventStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(4, snapshot.Events.Count);
        Assert.Contains(snapshot.Events, item => item.FirstActionId == "A" && item.OccurrenceCount == 2);
    }

    [Fact]
    public void Legacy_correlation_key_aggregates_without_inventing_action_id()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        var reporter = new OperationalEventReporter(store, TestEventFactory.Options(), new FakeClock());

        reporter.Report(TestEventFactory.Context(plateNo: "", sequence: "", actionId: "", correlationKey: "legacy-1"));
        reporter.Report(TestEventFactory.Context(plateNo: "", sequence: "", actionId: "", correlationKey: "legacy-1"));
        reporter.Report(TestEventFactory.Context(plateNo: "", sequence: "", actionId: "", correlationKey: "legacy-2"));

        OperationalEventStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(2, snapshot.Events.Count);
        OperationalEvent aggregated = Assert.Single(snapshot.Events, item => item.OccurrenceCount == 2);
        Assert.Equal(string.Empty, aggregated.FirstActionId);
        Assert.Equal(string.Empty, aggregated.LatestActionId);
    }

    [Fact]
    public void Real_action_id_has_priority_over_correlation_key()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        var reporter = new OperationalEventReporter(store, TestEventFactory.Options(), new FakeClock());

        reporter.Report(TestEventFactory.Context(
            plateNo: "", sequence: "", actionId: "action-a", correlationKey: "shared-legacy-key"));
        reporter.Report(TestEventFactory.Context(
            plateNo: "", sequence: "", actionId: "action-b", correlationKey: "shared-legacy-key"));

        Assert.Equal(2, store.Snapshot().Events.Count);
    }

    [Fact]
    public void Known_workpiece_independent_actions_use_action_id()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        var reporter = new OperationalEventReporter(store, TestEventFactory.Options(), new FakeClock());
        reporter.Report(TestEventFactory.Context(actionId: "A", independentAction: true));
        reporter.Report(TestEventFactory.Context(actionId: "B", independentAction: true));
        Assert.Equal(2, store.Snapshot().Events.Count);
    }

    [Fact]
    public void Known_workpiece_non_independent_actions_aggregate_but_correlation_key_still_separates()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        var reporter = new OperationalEventReporter(store, TestEventFactory.Options(), new FakeClock());
        reporter.Report(TestEventFactory.Context(actionId: "A"));
        reporter.Report(TestEventFactory.Context(actionId: "B"));
        reporter.Report(TestEventFactory.Context(actionId: "C", correlationKey: "separate"));

        OperationalEventStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(2, snapshot.Events.Count);
        Assert.Contains(snapshot.Events, item => item.OccurrenceCount == 2);
    }

    [Fact]
    public void Transient_failures_report_at_count_or_duration_threshold_and_recover_once()
    {
        var clock = new FakeClock();
        OperationalEventMonitorOptions options = TestEventFactory.Options(failureCount: 3, failureSeconds: 30);
        var store = new OperationalEventStore(options);
        var reporter = new OperationalEventReporter(store, options, clock);
        var failure = new OperationalFailureObservation("MC65", TestEventFactory.Context(eventCode: "COMM_FAILURE"));
        var recovery = new OperationalRecoveryObservation("MC65", TestEventFactory.Context(eventCode: "COMM_RECOVERED"));

        reporter.ObserveTransientFailure(failure);
        reporter.ObserveTransientFailure(failure);
        Assert.Empty(store.Snapshot().Events);
        reporter.ObserveTransientFailure(failure);
        Assert.Single(store.Snapshot().Events);
        reporter.ObserveTransientFailure(failure);
        Assert.Equal(2, Assert.Single(store.Snapshot().Events, item => item.EventCode == "COMM_FAILURE").OccurrenceCount);
        reporter.ObserveRecovery(recovery);
        reporter.ObserveRecovery(recovery);
        Assert.Equal(2, store.Snapshot().Events.Count);

        reporter.ObserveTransientFailure(new("MC66", TestEventFactory.Context(eventCode: "DURATION_FAILURE")));
        clock.Advance(TimeSpan.FromSeconds(30));
        reporter.ObserveTransientFailure(new("MC66", TestEventFactory.Context(eventCode: "DURATION_FAILURE")));
        Assert.Equal(3, store.Snapshot().Events.Count);
    }

    [Fact]
    public void Recovery_before_failure_threshold_only_clears_cycle()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        var reporter = new OperationalEventReporter(store, TestEventFactory.Options(), new FakeClock());
        reporter.ObserveTransientFailure(new("MC65", TestEventFactory.Context(eventCode: "FAIL")));
        reporter.ObserveRecovery(new("MC65", TestEventFactory.Context(eventCode: "RECOVER")));
        Assert.Empty(store.Snapshot().Events);
    }

    [Fact]
    public void Recovery_store_failure_does_not_resurrect_old_failure_cycle()
    {
        OperationalEventMonitorOptions options = TestEventFactory.Options(failureCount: 2);
        var store = new RecoveryThrowingStore(options);
        var reporter = new OperationalEventReporter(store, options, new FakeClock());
        var failure = new OperationalFailureObservation("MC65", TestEventFactory.Context(eventCode: "FAIL"));

        reporter.ObserveTransientFailure(failure);
        reporter.ObserveTransientFailure(failure);
        reporter.ObserveRecovery(new("MC65", TestEventFactory.Context(eventCode: "RECOVER")));
        reporter.ObserveTransientFailure(failure);

        OperationalEventStoreSnapshot snapshot = store.Snapshot();
        Assert.Single(snapshot.Events);
        Assert.Equal("FAIL", snapshot.Events[0].EventCode);
        Assert.Equal(1, snapshot.Diagnostics.ReporterFailureCount);
    }

    [Fact]
    public void Failure_key_includes_device_dimensions()
    {
        OperationalEventMonitorOptions options = TestEventFactory.Options(failureCount: 2);
        var store = new OperationalEventStore(options);
        var reporter = new OperationalEventReporter(store, options, new FakeClock());
        OperationalEventContext firstDevice = TestEventFactory.Context(eventCode: "FAIL");
        OperationalEventContext secondDevice = firstDevice with { DeviceNo = "66" };
        reporter.ObserveTransientFailure(new("read", firstDevice));
        reporter.ObserveTransientFailure(new("read", secondDevice));
        Assert.Empty(store.Snapshot().Events);
        reporter.ObserveTransientFailure(new("read", firstDevice));
        Assert.Single(store.Snapshot().Events);
    }

    [Fact]
    public void Stage_stall_reports_once_then_completed_recovers_and_aborted_never_recovers()
    {
        var clock = new FakeClock();
        OperationalEventMonitorOptions options = TestEventFactory.Options(stages: new Dictionary<string, TimeSpan>
        {
            ["TEST_STAGE"] = TimeSpan.FromSeconds(10)
        });
        var store = new OperationalEventStore(options);
        var reporter = new OperationalEventReporter(store, options, clock);

        reporter.ObserveStage(Stage(OperationalStageObservationState.Active, "A", "STALL-A"));
        clock.Advance(TimeSpan.FromSeconds(10));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Active, "A", "STALL-A"));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Active, "A", "STALL-A"));
        Assert.Single(store.Snapshot().Events);
        reporter.ObserveStage(Stage(OperationalStageObservationState.Completed, "A", "RECOVER-A"));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Completed, "A", "RECOVER-A"));
        Assert.Equal(2, store.Snapshot().Events.Count);

        reporter.ObserveStage(Stage(OperationalStageObservationState.Active, "B", "STALL-B"));
        clock.Advance(TimeSpan.FromSeconds(10));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Active, "B", "STALL-B"));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Aborted, "B", "ABORT-B"));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Completed, "B", "RECOVER-B"));
        Assert.Equal(4, store.Snapshot().Events.Count);
        Assert.Contains(store.Snapshot().Events, item => item.EventCode == "ABORT-B" && item.LatestResult.Contains("中止"));
        Assert.DoesNotContain(store.Snapshot().Events, item => item.EventCode == "RECOVER-B");
    }

    [Fact]
    public void Stage_completed_or_aborted_before_threshold_closes_without_event()
    {
        var store = new OperationalEventStore(TestEventFactory.Options());
        var reporter = new OperationalEventReporter(store, TestEventFactory.Options(), new FakeClock());
        reporter.ObserveStage(Stage(OperationalStageObservationState.Active, "A", "STALL"));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Completed, "A", "RECOVER"));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Active, "B", "STALL"));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Aborted, "B", "ABORT"));
        Assert.Empty(store.Snapshot().Events);
    }

    [Fact]
    public void Failure_and_stage_tracker_evictions_are_bounded_and_diagnosed()
    {
        OperationalEventMonitorOptions options = TestEventFactory.Options(
            capacity: 1,
            failureCount: 99,
            stages: new Dictionary<string, TimeSpan> { ["TEST_STAGE"] = TimeSpan.FromHours(1) });
        var store = new OperationalEventStore(options);
        var reporter = new OperationalEventReporter(store, options, new FakeClock());

        reporter.ObserveTransientFailure(new("failure-a", TestEventFactory.Context()));
        reporter.ObserveTransientFailure(new("failure-b", TestEventFactory.Context()));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Active, "stage-a", "A"));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Active, "stage-b", "B"));

        OperationalEventStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(1, snapshot.Diagnostics.TransientStateEvictedCount);
        Assert.Equal(1, snapshot.Diagnostics.StageStateEvictedCount);
        Assert.Equal(2, snapshot.Version);
        Assert.Empty(snapshot.Events);
    }

    [Fact]
    public async Task Interleaved_failure_observations_keep_monotonic_touch_and_latest_context()
    {
        var clock = new InterleavingClock();
        OperationalEventMonitorOptions options = TestEventFactory.Options(failureCount: 2);
        var store = new OperationalEventStore(options);
        var reporter = new OperationalEventReporter(store, options, clock);

        Task first = Task.Run(() => reporter.ObserveTransientFailure(new(
            "MC65",
            TestEventFactory.Context(eventCode: "FAIL", detail: "sequence-one"))));
        Assert.True(clock.WaitUntilFirstCallBlocks());
        Task second = Task.Run(() => reporter.ObserveTransientFailure(new(
            "MC65",
            TestEventFactory.Context(eventCode: "FAIL", detail: "sequence-two"))));
        await second;
        clock.ReleaseFirstCall();
        await first;

        OperationalEvent item = Assert.Single(store.Snapshot().Events);
        Assert.Contains("sequence-two", item.LatestDetailMessage);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(2), item.LastOccurredAtUtc);
        Assert.Equal(2L, GetTrackerSequence(reporter, "_failureCycles", "LastTouchedSequence"));
    }

    [Fact]
    public async Task Interleaved_stage_observations_keep_monotonic_touch_and_earliest_start()
    {
        var clock = new InterleavingClock();
        OperationalEventMonitorOptions options = TestEventFactory.Options(
            stages: new Dictionary<string, TimeSpan> { ["TEST_STAGE"] = TimeSpan.FromMinutes(1) });
        var reporter = new OperationalEventReporter(new OperationalEventStore(options), options, clock);

        Task first = Task.Run(() => reporter.ObserveStage(Stage(
            OperationalStageObservationState.Active, "same-stage", "STALL")));
        Assert.True(clock.WaitUntilFirstCallBlocks());
        Task second = Task.Run(() => reporter.ObserveStage(Stage(
            OperationalStageObservationState.Active, "same-stage", "STALL")));
        await second;
        clock.ReleaseFirstCall();
        await first;

        Assert.Equal(2L, GetTrackerSequence(reporter, "_stageCycles", "LastTouchedSequence"));
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1),
            GetTrackerValue<DateTime>(reporter, "_stageCycles", "StartedAtUtc"));
    }

    [Fact]
    public void State_eviction_diagnostic_failure_does_not_skip_threshold_event()
    {
        OperationalEventMonitorOptions options = TestEventFactory.Options(capacity: 1, failureCount: 1);
        var store = new EvictionThrowingStore();
        var reporter = new OperationalEventReporter(store, options, new FakeClock());

        reporter.ObserveTransientFailure(new("first", TestEventFactory.Context(eventCode: "FIRST")));
        reporter.ObserveTransientFailure(new("second", TestEventFactory.Context(eventCode: "SECOND")));

        Assert.Equal(new[] { "FIRST", "SECOND" }, store.RecordedEventCodes);
        Assert.Equal(1, store.ReporterFailureCount);
    }

    [Fact]
    public void Stage_eviction_diagnostic_failure_does_not_skip_stall_event()
    {
        OperationalEventMonitorOptions options = TestEventFactory.Options(
            capacity: 1,
            stages: new Dictionary<string, TimeSpan> { ["TEST_STAGE"] = TimeSpan.FromTicks(1) });
        var store = new EvictionThrowingStore();
        var clock = new FakeClock();
        var reporter = new OperationalEventReporter(store, options, clock);

        reporter.ObserveStage(Stage(OperationalStageObservationState.Active, "first", "FIRST"));
        clock.Advance(TimeSpan.FromTicks(1));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Active, "second", "SECOND"));
        clock.Advance(TimeSpan.FromTicks(1));
        reporter.ObserveStage(Stage(OperationalStageObservationState.Active, "second", "SECOND"));

        Assert.Equal(new[] { "SECOND" }, store.RecordedEventCodes);
        Assert.Equal(1, store.ReporterFailureCount);
    }

    [Fact]
    public void All_four_entrypoints_and_secondary_failure_reporting_never_throw()
    {
        var reporter = new OperationalEventReporter(new ThrowingStore(), TestEventFactory.Options(), new ThrowingClock());

        reporter.Report(null!);
        reporter.ObserveTransientFailure(null!);
        reporter.ObserveRecovery(null!);
        reporter.ObserveStage(null!);
    }

    [Fact]
    public void Reporter_normalizes_exception_without_retaining_exception_reference()
    {
        var store = new CapturingStore();
        var reporter = new OperationalEventReporter(store, TestEventFactory.Options(), new FakeClock());
        var inner = new InvalidOperationException("inner");
        var exception = new ApplicationException("outer", inner);

        reporter.Report(TestEventFactory.Context(exception: exception));

        Assert.NotNull(store.Context);
        Assert.Null(store.Context!.CapturedException);
        Assert.Contains(nameof(ApplicationException), store.Context.Evidence.Exception.Type);
        Assert.Contains("outer", store.Context.Evidence.Exception.Message);
        Assert.Contains("inner", store.Context.Evidence.Exception.InnerExceptionChain);
    }

    [Fact]
    public void Null_extreme_fields_and_exception_property_failures_do_not_escape()
    {
        var store = new CapturingStore();
        var reporter = new OperationalEventReporter(store, TestEventFactory.Options(), new FakeClock());
        OperationalEventContext context = TestEventFactory.Context() with
        {
            EventCode = null!,
            Scope = null!,
            Engine = null!,
            DeviceType = null!,
            DeviceNo = null!,
            Station = null!,
            ActionStage = null!,
            Title = null!,
            PhysicalConclusion = null!,
            BusinessPaused = null!,
            Evidence = null!,
            CapturedException = new ThrowingMessageException()
        };

        reporter.Report(context);

        Assert.NotNull(store.Context);
        Assert.Equal(string.Empty, store.Context!.EventCode);
        Assert.Equal(EvidenceAvailability.Unknown, store.Context.BusinessPaused.Availability);
        Assert.Contains("读取失败", store.Context.Evidence.Exception.Message);
    }

    private static OperationalStageObservation Stage(
        OperationalStageObservationState state,
        string key,
        string eventCode) => new(
            key,
            "TEST_STAGE",
            key,
            state,
            null,
            TestEventFactory.Context(eventCode: eventCode, actionId: key));

    private static long GetTrackerSequence(
        OperationalEventReporter reporter,
        string dictionaryField,
        string propertyName) =>
        GetTrackerValue<long>(reporter, dictionaryField, propertyName);

    private static T GetTrackerValue<T>(
        OperationalEventReporter reporter,
        string dictionaryField,
        string propertyName)
    {
        object dictionary = typeof(OperationalEventReporter)
            .GetField(dictionaryField, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(reporter)!;
        object cycle = ((System.Collections.IEnumerable)dictionary)
            .Cast<object>()
            .Select(entry => entry.GetType().GetProperty("Value")!.GetValue(entry)!)
            .Single();
        return (T)cycle.GetType().GetProperty(propertyName)!.GetValue(cycle)!;
    }

    private sealed class FakeClock : IOperationalEventClock
    {
        public DateTime UtcNow { get; private set; } = DateTime.UnixEpoch;
        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed class InterleavingClock : IOperationalEventClock
    {
        private readonly ManualResetEventSlim _firstBlocked = new(false);
        private readonly ManualResetEventSlim _releaseFirst = new(false);
        private int _calls;

        public DateTime UtcNow
        {
            get
            {
                int call = Interlocked.Increment(ref _calls);
                if (call == 1)
                {
                    _firstBlocked.Set();
                    if (!_releaseFirst.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("test did not release first clock call");
                    }

                    return DateTime.UnixEpoch.AddSeconds(1);
                }

                return DateTime.UnixEpoch.AddSeconds(2);
            }
        }

        public bool WaitUntilFirstCallBlocks() => _firstBlocked.Wait(TimeSpan.FromSeconds(5));

        public void ReleaseFirstCall() => _releaseFirst.Set();
    }

    private sealed class ThrowingClock : IOperationalEventClock
    {
        public DateTime UtcNow => throw new InvalidOperationException("clock failed");
    }

    private sealed class ThrowingStore : IOperationalEventStore
    {
        public event Action? Changed { add { } remove { } }
        public void Record(OperationalEventContext context, DateTime occurredAtUtc, long observationSequence, string fingerprint) =>
            throw new InvalidOperationException("record failed");
        public void RecordReporterFailure(DateTime occurredAtUtc, string reason) =>
            throw new InvalidOperationException("diagnostic failed");
        public void RecordStateEviction(OperationalEventTrackerKind trackerKind, DateTime occurredAtUtc) =>
            throw new InvalidOperationException("diagnostic failed");
        public OperationalEventStoreSnapshot Snapshot() => throw new InvalidOperationException();
    }

    private sealed class CapturingStore : IOperationalEventStore
    {
        public OperationalEventContext? Context { get; private set; }
        public event Action? Changed { add { } remove { } }
        public void Record(OperationalEventContext context, DateTime occurredAtUtc, long observationSequence, string fingerprint) => Context = context;
        public void RecordReporterFailure(DateTime occurredAtUtc, string reason) { }
        public void RecordStateEviction(OperationalEventTrackerKind trackerKind, DateTime occurredAtUtc) { }
        public OperationalEventStoreSnapshot Snapshot() => throw new NotSupportedException();
    }

    private sealed class RecoveryThrowingStore : IOperationalEventStore
    {
        private readonly OperationalEventStore _inner;

        public RecoveryThrowingStore(OperationalEventMonitorOptions options) =>
            _inner = new OperationalEventStore(options);

        public event Action? Changed
        {
            add => _inner.Changed += value;
            remove => _inner.Changed -= value;
        }

        public void Record(OperationalEventContext context, DateTime occurredAtUtc, long observationSequence, string fingerprint)
        {
            if (context.EventCode == "RECOVER")
            {
                throw new InvalidOperationException("simulated recovery write failure");
            }

            _inner.Record(context, occurredAtUtc, observationSequence, fingerprint);
        }

        public void RecordReporterFailure(DateTime occurredAtUtc, string reason) =>
            _inner.RecordReporterFailure(occurredAtUtc, reason);

        public void RecordStateEviction(OperationalEventTrackerKind trackerKind, DateTime occurredAtUtc) =>
            _inner.RecordStateEviction(trackerKind, occurredAtUtc);

        public OperationalEventStoreSnapshot Snapshot() => _inner.Snapshot();
    }

    private sealed class EvictionThrowingStore : IOperationalEventStore
    {
        private readonly List<string> _eventCodes = new();

        public IReadOnlyList<string> RecordedEventCodes => _eventCodes;

        public int ReporterFailureCount { get; private set; }

        public event Action? Changed { add { } remove { } }

        public void Record(OperationalEventContext context, DateTime occurredAtUtc, long observationSequence, string fingerprint) =>
            _eventCodes.Add(context.EventCode);

        public void RecordReporterFailure(DateTime occurredAtUtc, string reason) => ReporterFailureCount++;

        public void RecordStateEviction(OperationalEventTrackerKind trackerKind, DateTime occurredAtUtc) =>
            throw new InvalidOperationException("state eviction diagnostic failed");

        public OperationalEventStoreSnapshot Snapshot() => throw new NotSupportedException();
    }

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("message getter failed");
    }
}
