namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class PhaseAwareRearLoadContractTests
{
    [Fact]
    public void Flow_action_context_declares_phase_command_and_workpiece_facts()
    {
        string source = Read("Service", "FlowEngine", "FlowActionContext.cs");

        Assert.Contains("enum FlowActionStep", source, StringComparison.Ordinal);
        Assert.Contains("PreCheck", source, StringComparison.Ordinal);
        Assert.Contains("MoveXYToSource", source, StringComparison.Ordinal);
        Assert.Contains("MagnetOnSent", source, StringComparison.Ordinal);
        Assert.Contains("TargetPendingHandoff", source, StringComparison.Ordinal);
        Assert.Contains("enum FlowCommandState", source, StringComparison.Ordinal);
        Assert.Contains("NotSent", source, StringComparison.Ordinal);
        Assert.Contains("SentAwaitingEvidence", source, StringComparison.Ordinal);
        Assert.Contains("ResponseUnknown", source, StringComparison.Ordinal);
        Assert.Contains("enum FlowWorkpieceOwnership", source, StringComparison.Ordinal);
        Assert.Contains("ReservedAtSource", source, StringComparison.Ordinal);
        Assert.Contains("OnCarrier", source, StringComparison.Ordinal);
        Assert.Contains("AtTargetPendingHandoff", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Flow_action_context_requires_manual_pause_for_unknown_physical_command()
    {
        string source = Read("Service", "FlowEngine", "FlowActionContext.cs");

        Assert.Contains("PauseManual", source, StringComparison.Ordinal);
        Assert.Contains("MarkCommandResponseUnknown", source, StringComparison.Ordinal);
        Assert.Contains("LastKnownPosition", source, StringComparison.Ordinal);
        Assert.Contains("SourceCacheKey", source, StringComparison.Ordinal);
        Assert.Contains("已发送物理命令的动作不能降级为自动等待重试", source, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([RepositoryRoot.Find(), .. parts]));
}
