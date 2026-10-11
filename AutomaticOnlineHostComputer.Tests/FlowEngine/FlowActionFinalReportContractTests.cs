using System.Text.RegularExpressions;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 状态机暂停不是只写日志：最终异常必须带同一动作快照，且目标待交接结论必须先于上报。
/// 这是源码契约，防止以后把 Report 又放回 catch 分支开头而让页面显示过期归属。
/// </summary>
public sealed class FlowActionFinalReportContractTests
{
    [Theory]
    [InlineData("Line1FrontFlowEngine.cs")]
    [InlineData("Line2FrontFlowEngine.cs")]
    public void Front_manipulator_and_crane_final_failures_report_the_action_snapshot_after_final_ownership_is_known(string file)
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", file));

        Assert.Contains("机械手1严重异常作废", source, StringComparison.Ordinal);
        Assert.Contains("actionSnapshot: action.Snapshot()", source, StringComparison.Ordinal);

        int targetOwnership = source.IndexOf("action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff", StringComparison.Ordinal);
        int finalReport = source.LastIndexOf("ActionStage = \"前天车严重异常作废\"", StringComparison.Ordinal);
        Assert.True(targetOwnership >= 0, $"{file}缺少中转架目标待交接结论");
        Assert.True(finalReport > targetOwnership,
            $"{file}必须在中转架目标待交接结论后再生成前天车最终快照，不能展示过期的OnCarrier。");

        Assert.Contains("MarkingHandshakeAsync", source, StringComparison.Ordinal);
        Assert.Contains("FlowActionContext action", source, StringComparison.Ordinal);
        Assert.Contains("action.SetTarget(rackStation", source, StringComparison.Ordinal);
        Assert.Contains("action.SetOwnership(FlowWorkpieceOwnership.AtTargetPendingHandoff", source, StringComparison.Ordinal);
    }
}
