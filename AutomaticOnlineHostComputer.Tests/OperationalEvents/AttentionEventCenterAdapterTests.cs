using AutomaticOnlineHostComputer.Infrastructure.DependencyInjection;
using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.OperationalEvents;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class AttentionEventCenterAdapterTests
{
    [Theory]
    [InlineData(AttentionEventKind.SafetyAlarm, "LEGACY_SAFETY_ALARM", OperationalEventSeverity.Critical, OperationalEventCategory.Safety)]
    [InlineData(AttentionEventKind.Warning, "LEGACY_WARNING", OperationalEventSeverity.Warning, OperationalEventCategory.Safety)]
    [InlineData(AttentionEventKind.Emergency, "LEGACY_EMERGENCY", OperationalEventSeverity.Critical, OperationalEventCategory.Emergency)]
    public void Record_MapsLegacyFieldsWithoutInventingEvidence(
        AttentionEventKind kind,
        string expectedCode,
        OperationalEventSeverity expectedSeverity,
        OperationalEventCategory expectedCategory)
    {
        DateTime now = new(2026, 7, 20, 3, 4, 5, DateTimeKind.Utc);
        var fixture = CreateFixture(now);

        fixture.Center.Record(kind, "一线/安全", "Line1FrontFlowEngine.Catch", "原始 message  保留", "原始 result");

        OperationalEvent stored = Assert.Single(fixture.Store.Snapshot().Events);
        Assert.Equal(expectedCode, stored.EventCode);
        Assert.Equal(expectedSeverity, stored.Severity);
        Assert.Equal(expectedCategory, stored.Category);
        Assert.Equal("一线/安全", stored.Scope);
        Assert.Equal("Line1FrontFlowEngine.Catch", stored.Source);
        Assert.Equal("原始 message  保留", stored.LatestDetailMessage);
        Assert.Equal("原始 result", stored.LatestResult);
        Assert.Equal(string.Empty, stored.FirstActionId);
        Assert.Equal(string.Empty, stored.LatestActionId);
        Assert.Equal(EvidenceAvailability.Unknown, stored.LatestBusinessPaused.Availability);
        Assert.False(stored.LatestBusinessPaused.HasValue);
        Assert.Equal("Unknown", stored.Engine);
        Assert.Equal("Unknown", stored.DeviceType);
        Assert.Equal("Unknown", stored.DeviceNo);
        Assert.Equal("Unknown", stored.Station);
        Assert.Equal("Unknown", stored.ActionStage);
        Assert.Equal(PhysicalConclusionCode.Unknown, stored.LatestPhysicalConclusion.Code);
        Assert.Equal(EvidenceAvailability.Unknown, stored.LatestPhysicalConclusion.Availability);
        Assert.Equal(EvidenceAvailability.Unavailable, stored.LatestEvidence.Exception.Availability);

        AttentionEvent legacy = Assert.Single(fixture.Center.Snapshot());
        Assert.Equal(kind, legacy.Kind);
        Assert.Equal("一线/安全", legacy.Scope);
        Assert.Equal("Line1FrontFlowEngine.Catch", legacy.Source);
        Assert.Equal("原始 message  保留", legacy.Message);
        Assert.Equal("原始 result", legacy.Result);
        Assert.Equal(now.ToLocalTime(), legacy.OccurredAt);
        Assert.Equal(now.Ticks, legacy.Sequence);
    }

    [Fact]
    public void Record_NullResultRoundTripsAsNullInLegacySnapshot()
    {
        var fixture = CreateFixture(new DateTime(2026, 7, 20, 3, 4, 5, DateTimeKind.Utc));

        fixture.Center.Record(AttentionEventKind.Warning, "scope", "source", "message", null);

        Assert.Equal(string.Empty, Assert.Single(fixture.Store.Snapshot().Events).LatestResult);
        Assert.Null(Assert.Single(fixture.Center.Snapshot()).Result);
    }

    [Fact]
    public void Record_NormalizedMessageAggregatesButDistinctDimensionsDoNot()
    {
        var fixture = CreateFixture(new DateTime(2026, 7, 20, 3, 4, 5, DateTimeKind.Utc));

        fixture.Center.Record(AttentionEventKind.Warning, "scope", "source-a", " motor\t  fault ");
        fixture.Center.Record(AttentionEventKind.Warning, "scope", "source-a", "motor fault");
        fixture.Center.Record(AttentionEventKind.Warning, "scope", "source-b", "motor fault");
        fixture.Center.Record(AttentionEventKind.Warning, "scope", "source-a", "other fault");
        fixture.Center.Record(AttentionEventKind.Emergency, "scope", "source-a", "motor fault");

        OperationalEvent[] events = fixture.Store.Snapshot().Events.ToArray();
        Assert.Equal(4, events.Length);
        OperationalEvent aggregated = Assert.Single(events, item => item.OccurrenceCount == 2);
        Assert.Equal("motor fault", aggregated.LatestDetailMessage);
        Assert.All(events, item =>
        {
            Assert.Equal(string.Empty, item.FirstActionId);
            Assert.Equal(string.Empty, item.LatestActionId);
        });
    }

    [Fact]
    public void Changed_ForwardsStoreNotificationOnceAndIsolatesSubscribers()
    {
        var fixture = CreateFixture(new DateTime(2026, 7, 20, 3, 4, 5, DateTimeKind.Utc));
        int successfulSubscriberCalls = 0;
        fixture.Center.Changed += () => throw new InvalidOperationException("subscriber failure");
        fixture.Center.Changed += () => successfulSubscriberCalls++;

        fixture.Center.Record(AttentionEventKind.SafetyAlarm, "scope", "source", "message");

        Assert.Equal(1, successfulSubscriberCalls);
    }

    [Fact]
    public void Record_SwallowsReporterFailure()
    {
        var store = new OperationalEventStore(OperationalEventMonitorOptions.Default);
        var center = new AttentionEventCenter(store, new ThrowingReporter());

        Exception? exception = Record.Exception(() =>
            center.Record(AttentionEventKind.Emergency, "scope", "source", "message", "result"));

        Assert.Null(exception);
        Assert.Empty(store.Snapshot().Events);
    }

    [Fact]
    public void DependencyInjection_UsesOneStoreAndReporterAndValidatesGraph()
    {
        var services = new ServiceCollection();
        services.AddApplicationServices();

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        OperationalEventStore concreteStore = provider.GetRequiredService<OperationalEventStore>();
        IOperationalEventStore interfaceStore = provider.GetRequiredService<IOperationalEventStore>();
        IOperationalEventReporter reporter1 = provider.GetRequiredService<IOperationalEventReporter>();
        IOperationalEventReporter reporter2 = provider.GetRequiredService<IOperationalEventReporter>();
        AttentionEventCenter center = provider.GetRequiredService<AttentionEventCenter>();

        Assert.Same(concreteStore, interfaceStore);
        Assert.Same(reporter1, reporter2);
        Assert.NotNull(center);
        Assert.NotNull(provider.GetRequiredService<OperationalEventMonitorOptions>());
        Assert.NotNull(provider.GetRequiredService<IOperationalEventClock>());
        Assert.NotNull(provider.GetRequiredService<OperationalEventFormatter>());
    }

    private static Fixture CreateFixture(DateTime now)
    {
        OperationalEventMonitorOptions options = OperationalEventMonitorOptions.Default;
        var store = new OperationalEventStore(options);
        var reporter = new OperationalEventReporter(store, options, new FixedClock(now));
        return new Fixture(store, new AttentionEventCenter(store, reporter));
    }

    private sealed record Fixture(OperationalEventStore Store, AttentionEventCenter Center);

    private sealed class FixedClock(DateTime now) : IOperationalEventClock
    {
        public DateTime UtcNow => now;
    }

    private sealed class ThrowingReporter : IOperationalEventReporter
    {
        public void Report(OperationalEventContext context) => throw new InvalidOperationException("reporter failure");

        public void ObserveTransientFailure(OperationalFailureObservation observation) => throw new NotSupportedException();

        public void ObserveRecovery(OperationalRecoveryObservation observation) => throw new NotSupportedException();

        public void ObserveStage(OperationalStageObservation observation) => throw new NotSupportedException();
    }
}
