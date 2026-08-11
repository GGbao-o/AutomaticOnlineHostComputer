using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class RearSkewDispatchPlannerTests
{
    [Fact]
    public void Load_decision_yields_to_immediately_dispatchable_unload_when_transfer_path_is_busy()
    {
        var baseline = new RearSkewDispatchDecision(RearSkewDispatchAction.Load, "base-load", 3);

        RearSkewDispatchDecision result = RearSkewDispatchPlanner.ApplyLoadPathAvailability(
            baseline,
            loadPathImmediatelyAvailable: false,
            immediatelyDispatchableUnloadCount: 1,
            loadPathBlockReason: "ZoneMT=busy, ZoneTS=free");

        Assert.Equal(RearSkewDispatchAction.Unload, result.Action);
        Assert.Equal(3, result.HighUnloadThreshold);
        Assert.Contains("ZoneMT=busy", result.Reason, StringComparison.Ordinal);
        Assert.NotEqual(baseline.Reason, result.Reason);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Load_decision_is_kept_when_path_is_available_or_no_immediate_unload_exists(
        bool loadPathImmediatelyAvailable,
        int immediatelyDispatchableUnloadCount)
    {
        var baseline = new RearSkewDispatchDecision(RearSkewDispatchAction.Load, "base-load", 3);

        RearSkewDispatchDecision result = RearSkewDispatchPlanner.ApplyLoadPathAvailability(
            baseline,
            loadPathImmediatelyAvailable,
            immediatelyDispatchableUnloadCount,
            "ZoneMT=busy");

        Assert.Equal(baseline, result);
    }

    [Fact]
    public void Unload_decision_is_never_rewritten_by_load_path_availability()
    {
        var baseline = new RearSkewDispatchDecision(RearSkewDispatchAction.Unload, "base-unload", 2);

        RearSkewDispatchDecision result = RearSkewDispatchPlanner.ApplyLoadPathAvailability(
            baseline,
            loadPathImmediatelyAvailable: false,
            immediatelyDispatchableUnloadCount: 1,
            loadPathBlockReason: "ZoneMT=busy");

        Assert.Equal(baseline, result);
    }
}
