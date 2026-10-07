namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>机械手2/3的人工结案不得退化为原“清位置应急”。</summary>
public sealed class BalancingManualSettlementContractTests
{
    [Fact]
    public void Four_manual_outcomes_preserve_or_clear_balancing_ledger_explicitly()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", "BalancingFlowEngine.cs"));

        Assert.Contains("public string ResolveManualAction", source, StringComparison.Ordinal);
        Assert.Contains("case FlowActionManualResolution.StillAtSource", source, StringComparison.Ordinal);
        Assert.Contains("sourceCacheTransferred && _balWps.ContainsKey(source)", source, StringComparison.Ordinal);
        Assert.Contains("禁止用旧动作工件覆盖", source, StringComparison.Ordinal);
        Assert.Contains("case FlowActionManualResolution.OnCarrier", source, StringComparison.Ordinal);
        Assert.Contains("case FlowActionManualResolution.AtTargetPendingHandoff", source, StringComparison.Ordinal);
        Assert.Contains("case FlowActionManualResolution.RemovedManually", source, StringComparison.Ordinal);
        Assert.Contains("!sourceCacheTransferred", source, StringComparison.Ordinal);
        Assert.Contains("ClearInFlightWorkpiece", source, StringComparison.Ordinal);
        Assert.Contains("source != \"M700\"", source, StringComparison.Ordinal);
        Assert.Contains("动平衡引擎保持暂停", source, StringComparison.Ordinal);
    }
}
