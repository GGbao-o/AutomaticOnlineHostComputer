namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class GrindingUnloadM720WaitContractTests
{
    [Fact]
    public void Held_workpiece_waits_for_m720_instead_of_throwing_or_releasing_crane_lock()
    {
        string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");

        Assert.Contains("WaitForGrindingUnloadRackReadyAsync", source, StringComparison.Ordinal);
        Assert.Contains("while (true)", source, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(_cfg.Grinding.SignalPollIntervalMs, ct)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ST710/M720动作前确认失败", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Unload_rechecks_live_x11_after_safe_z_and_rejects_no_plate_before_m720()
    {
        string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");
        int safeZ = source.IndexOf("研磨机取料后Z安全到位", StringComparison.Ordinal);
        int read = source.IndexOf("await crane.ReadXBitAsync(CraneAddress.D_X11_HasPlate, ct)", StringComparison.Ordinal);
        int rejected = source.IndexOf("if (!heldAfterRise)", StringComparison.Ordinal);
        int waitRack = source.IndexOf("await WaitForGrindingUnloadRackReadyAsync(ct);", StringComparison.Ordinal);
        Assert.True(safeZ >= 0 && safeZ < read && read < rejected && rejected < waitRack);
        Assert.Contains("throw new InvalidOperationException", source[rejected..waitRack]);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepositoryRoot.Find() }.Concat(parts).ToArray()));
}
