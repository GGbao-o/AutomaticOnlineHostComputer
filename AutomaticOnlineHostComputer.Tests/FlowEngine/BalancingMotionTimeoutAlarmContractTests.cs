using System.Text.RegularExpressions;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class BalancingMotionTimeoutAlarmContractTests
{
    [Fact]
    public void M2_and_m3_motion_timeouts_pause_and_forward_the_original_timeout_message()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", "BalancingFlowEngine.cs"));
        foreach (int no in new[] { 2, 3 })
        {
            int start = source.IndexOf($"private async Task M{no}Flow(");
            int end = source.IndexOf("\n    private ", start + 10);
            string method = source[start..end];
            int failure = method.IndexOf("\n        catch (Exception ex)");
            string severe = method[failure..];
            Assert.Contains("_paused = true;", severe);
            Assert.Contains("FlowAlarmPresentationContext.RecordFailure(ex);", severe);
            Assert.Contains("{ex.Message}", severe);
            Assert.Contains("action.Discard(", severe);
            Assert.Contains("OnSafetyAlarm?.Invoke", severe);
        }

    }
}
