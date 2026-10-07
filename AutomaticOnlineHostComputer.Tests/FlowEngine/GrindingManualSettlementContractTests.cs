namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>研磨人工结案必须实际转换Pending/FIFO，而不是只删除动作快照。</summary>
public sealed class GrindingManualSettlementContractTests
{
    [Fact]
    public void Four_manual_outcomes_have_explicit_safe_ledger_conversions()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", "GrindingFlowEngine.cs"));

        Assert.Contains("public string ResolveManualAction", source, StringComparison.Ordinal);
        Assert.Contains("case FlowActionManualResolution.StillAtSource when isLoad", source, StringComparison.Ordinal);
        Assert.Contains("RequeueWorkpiece(wp.Value)", source, StringComparison.Ordinal);
        Assert.Contains("case FlowActionManualResolution.OnCarrier", source, StringComparison.Ordinal);
        Assert.Contains("case FlowActionManualResolution.AtTargetPendingHandoff", source, StringComparison.Ordinal);
        Assert.Contains("case FlowActionManualResolution.RemovedManually", source, StringComparison.Ordinal);
        Assert.Contains("FlowActionManualRegistry.Remove(operationId)", source, StringComparison.Ordinal);
        Assert.Contains("研磨引擎保持暂停", source, StringComparison.Ordinal);
    }
}
