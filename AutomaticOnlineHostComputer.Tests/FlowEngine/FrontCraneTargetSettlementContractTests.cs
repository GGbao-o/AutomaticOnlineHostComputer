namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>前天车目标待交接只能补中转架缓存，绝不能清前端当前板或队列。</summary>
public sealed class FrontCraneTargetSettlementContractTests
{
    [Theory]
    [InlineData("Line1FrontFlowEngine.cs", "1号线前天车取料-打号-中转")]
    [InlineData("Line2FrontFlowEngine.cs", "2号线前天车取料-打号-中转")]
    public void Target_pending_settlement_uses_complete_manual_task_and_only_closes_the_crane_task(string file, string scope)
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", file));

        Assert.Contains("private WorkpieceCache? _frontCraneManualWorkpiece", source, StringComparison.Ordinal);
        Assert.Contains("public string ResolveFrontCraneTargetPending", source, StringComparison.Ordinal);
        Assert.Contains($"action.FlowScope != \"{scope}\"", source, StringComparison.Ordinal);
        Assert.Contains("FlowWorkpieceOwnership.AtTargetPendingHandoff", source, StringComparison.Ordinal);
        Assert.Contains("OnRackPlaced(task.TargetStation, wp.Value)", source, StringComparison.Ordinal);
        Assert.Contains("ClearFrontCraneTask()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_craneQueue.Clear()", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Line1FrontFlowEngine.cs")]
    [InlineData("Line2FrontFlowEngine.cs")]
    public void Pos3_x11_retry_records_the_command_and_uses_a_deeper_pick_target(string file)
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", file));
        Assert.Contains("PickupProbeHelper.PickAsync(crane, f2zOff, pickupStableDelayMs", source);
        Assert.Contains("int f2PickZ = pos3Pickup.ActualZ;", source);
        Assert.DoesNotContain("f2PickZ += 5;", source);
        Assert.DoesNotContain("货叉Pos3 X11重试前退磁", source);
        Assert.Contains("PickupProbeHelper.PickAsync(crane, markerZPickup, pickupStableDelayMs", source);

    }

    [Theory]
    [InlineData("Line1FrontFlowEngine.cs", "1号线机械手1取料送叉")]
    [InlineData("Line2FrontFlowEngine.cs", "2号线机械手1取料送叉")]
    public void Manipulator1_manual_settlement_is_identity_scoped_and_does_not_auto_move(string file, string scope)
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", file));

        Assert.Contains("ResolveManipulator1StillAtSource", source, StringComparison.Ordinal);
        Assert.Contains("ResolveManipulator1OnCarrier", source, StringComparison.Ordinal);
        Assert.Contains("ResolveManipulator1RemovedManually", source, StringComparison.Ordinal);
        Assert.Contains($"action.FlowScope != \"{scope}\"", source, StringComparison.Ordinal);
        Assert.Contains("_manipulatorLock.Wait(0)", source, StringComparison.Ordinal);
        Assert.Contains("queued.IdentityText == action.WorkpieceIdentity", source, StringComparison.Ordinal);
    }
}
