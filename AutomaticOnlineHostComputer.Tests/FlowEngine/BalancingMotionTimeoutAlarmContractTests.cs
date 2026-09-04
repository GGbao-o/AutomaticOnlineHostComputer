using System.Text.RegularExpressions;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class BalancingMotionTimeoutAlarmContractTests
{
    [Fact]
    public void M2_and_m3_motion_timeouts_pause_and_forward_the_original_timeout_message()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", "BalancingFlowEngine.cs"));
        const string pattern = "if \\(ex is CraneMotionTimeoutException timeout\\)[\\s\\S]{0,300}?_paused = true;[\\s\\S]{0,300}?OnSafetyAlarm\\?\\.Invoke\\(timeout.Message\\);";

        Assert.Equal(2, Regex.Matches(source, pattern).Count);
    }
}
