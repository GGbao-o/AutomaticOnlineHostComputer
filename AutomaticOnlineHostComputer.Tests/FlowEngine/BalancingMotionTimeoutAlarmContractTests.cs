using System.Text.RegularExpressions;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <remarks>
/// 核对M2/M3运动失败进入严重暂停/告警路径，及后端接口保持一致。
/// 动平衡没有机床Idle枚举；本趟Busy、显示和在途按动作版本结束，实际机械手位置另行确认。
/// </remarks>
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
