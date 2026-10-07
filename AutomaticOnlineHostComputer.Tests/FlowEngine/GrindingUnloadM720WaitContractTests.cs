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

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepositoryRoot.Find() }.Concat(parts).ToArray()));
}
