namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <remarks>
/// 核对磁铁反馈异常不会被当作成功持件或自动回来源重试，组件必须保留失败原因给引擎收尾。
/// 放磁检查只读反馈，严重未知不通过强制退磁/重新充磁猜测实物位置。
/// </remarks>
public sealed class MagnetFeedbackSafetyContractTests
{
    [Fact]
    public void Magnet_feedback_timeout_is_an_automatic_safety_pause()
    {
        string resolver = Read("Service", "FlowEngine", "FlowActionExceptionResolver.cs");
        string balancing = Read("Service", "FlowEngine", "BalancingFlowEngine.cs");
        string grinding = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");
        string line1Front = Read("Service", "FlowEngine", "Line1FrontFlowEngine.cs");
        string line2Front = Read("Service", "FlowEngine", "Line2FrontFlowEngine.cs");

        Assert.Contains("exception is MagnetFeedbackTimeoutException", resolver, StringComparison.Ordinal);
        Assert.Contains("PauseLineManual", resolver, StringComparison.Ordinal);
        Assert.Contains("PickupProbeHelper.PickAsync", balancing);
        Assert.Contains("_paused = true;", balancing);
        Assert.Contains("action.Discard(", balancing);
        Assert.Contains("ex is MagnetFeedbackTimeoutException", grinding, StringComparison.Ordinal);
        Assert.Contains("PickupProbeHelper.PickAsync", line1Front);
        Assert.Contains("PauseAndDiscardManipulatorWorkpiece(wp);", line1Front);
        Assert.Contains("PickupProbeHelper.PickAsync", line2Front);
        Assert.Contains("PauseAndDiscardManipulatorWorkpiece(wp);", line2Front);
        Assert.Contains("OnSafetyAlarm?.Invoke", balancing, StringComparison.Ordinal);
        Assert.Contains("OnSafetyAlarm?.Invoke", grinding, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepositoryRoot.Find() }.Concat(parts).ToArray()));
}
