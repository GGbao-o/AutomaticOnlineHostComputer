namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class BalancingManipulatorSpeedContractTests
{
    [Fact]
    public void Each_M2_and_M3_transfer_configures_speed_once_before_its_first_physical_move()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "Service", "FlowEngine", "BalancingFlowEngine.cs"));

        const string m2Configure = "await ConfigureManipulatorAbsSpeedAsync(_m2!, 2, \"M2\", ct);";
        const string m3Configure = "await ConfigureManipulatorAbsSpeedAsync(_m3!, 3, \"M3\", ct);";

        Assert.Contains("private async Task ConfigureManipulatorAbsSpeedAsync(", source, StringComparison.Ordinal);
        Assert.Contains(m2Configure, source, StringComparison.Ordinal);
        Assert.Contains(m3Configure, source, StringComparison.Ordinal);
        Assert.DoesNotContain("await _m2.SetAbsSpeedAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await _m3.SetAbsSpeedAsync(", source, StringComparison.Ordinal);

        Assert.True(source.IndexOf(m2Configure, StringComparison.Ordinal) <
                    source.IndexOf("await _m2.MoveAbsoluteAsync(-1, pickY, -1, ct: ct);", StringComparison.Ordinal));
        Assert.True(source.IndexOf(m3Configure, StringComparison.Ordinal) <
                    source.IndexOf("await _m3.MoveAbsoluteAsync(-1, pickY, -1, ct: ct);", StringComparison.Ordinal));
    }

    [Fact]
    public void Business_docs_describe_per_task_speed_and_the_complete_M2_M3_handoffs()
    {
        string root = RepositoryRoot.Find();
        string businessDocs = File.ReadAllText(Path.Combine(root, "Docs", "全线业务逻辑详解.md"));
        string overviewDocs = File.ReadAllText(Path.Combine(root, "Docs", "03-设备交互流程", "整线引擎流程总览_按代码.md"));

        Assert.Contains("本趟任务设置速度", businessDocs, StringComparison.Ordinal);
        Assert.Contains("初始化和暂停恢复本身不写", businessDocs, StringComparison.Ordinal);
        Assert.Contains("M817/ST019` 或 `M818/ST020`", businessDocs, StringComparison.Ordinal);
        Assert.Contains("M700/ST009` 或 `M821/ST021`", businessDocs, StringComparison.Ordinal);
        Assert.Contains("完整写一次三轴绝对速度", overviewDocs, StringComparison.Ordinal);
        Assert.Contains("M721=1、通知研磨FIFO", overviewDocs, StringComparison.Ordinal);
    }
}
