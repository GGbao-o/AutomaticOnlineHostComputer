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

    [Fact]
    public void Transfer_rack_ledger_reserves_before_pick_and_removes_only_after_pick_confirmation()
    {
        string source = Read("Service", "FlowEngine", "TransferRackWorkpieceLedger.cs");

        Assert.Contains("TryReserve", source, StringComparison.Ordinal);
        Assert.Contains("CommitPickup", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseReservation", source, StringComparison.Ordinal);
        Assert.Contains("_workpieces.Remove(rackCode, out workpiece)", source, StringComparison.Ordinal);
        Assert.Contains("_reservations[rackCode] = reservation ?? Reservation.Active(operationId)", source, StringComparison.Ordinal);
        Assert.Contains("Reservation.Active(operationId)", source, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([RepositoryRoot.Find(), .. parts]));
}
