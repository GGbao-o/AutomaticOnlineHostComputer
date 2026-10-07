namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>锁定斜床下料候选的机械手2/3报警门禁，避免后续改动绕过实时安全状态。</summary>
public sealed class ManipulatorAlarmDispatchGateContractTests
{
    [Fact]
    public void Rear_unload_candidate_requires_both_manipulators_to_be_alarm_free()
    {
        string gate = Read("Service", "FlowEngine", "ManipulatorAlarmDispatchGate.cs");
        string line1 = Read("Service", "FlowEngine", "Line1RearFlowEngine.cs");
        string line2 = Read("Service", "FlowEngine", "Line2RearFlowEngine.cs");

        Assert.Contains("ReadStatusAsync", gate, StringComparison.Ordinal);
        Assert.Contains("ServoAlarm == 0", gate, StringComparison.Ordinal);
        Assert.Contains("PlcServoAlarm == 0", gate, StringComparison.Ordinal);
        Assert.Contains("PlcAlarm == 0", gate, StringComparison.Ordinal);
        Assert.Contains("return false;", gate, StringComparison.Ordinal);
        Assert.Contains("ManipulatorAlarmDispatchGate.CanDispatchRearSkewUnloadAsync", line1,
            StringComparison.Ordinal);
        Assert.Contains("ManipulatorAlarmDispatchGate.CanDispatchRearSkewUnloadAsync", line2,
            StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepositoryRoot.Find() }.Concat(parts).ToArray()));
}
