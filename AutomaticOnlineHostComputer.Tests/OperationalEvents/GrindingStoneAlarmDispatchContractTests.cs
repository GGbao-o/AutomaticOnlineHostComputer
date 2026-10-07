namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class GrindingStoneAlarmDispatchContractTests
{
    [Fact]
    public void TypeA_stone_alarm_blocks_only_the_affected_grinder_without_pausing_the_engine()
    {
        string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");

        Assert.Contains("LastGrindStone1Alarm", source, StringComparison.Ordinal);
        Assert.Contains("LastGrindStone2Alarm", source, StringComparison.Ordinal);
        Assert.Contains("HasGrindStoneAlarm", source, StringComparison.Ordinal);
        Assert.Contains("&& !g.HasGrindStoneAlarm", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.GrindStone1Alarm, snapshot.GrindStone2Alarm", source, StringComparison.Ordinal);

        string prePick = Slice(source, "// ── ② 检查磨石厚度报警", "// ── ③ 补充长度后写工件参数");
        Assert.DoesNotContain("_paused = true", prePick, StringComparison.Ordinal);
        Assert.DoesNotContain("OnSafetyAlarm?.Invoke", prePick, StringComparison.Ordinal);
        Assert.Contains("RequeueWorkpiece(wp)", prePick, StringComparison.Ordinal);
        Assert.Contains("ReadTypeAStatusSnapshotAsync", prePick, StringComparison.Ordinal);
    }

    [Fact]
    public void Device_overview_and_flow_status_promote_the_stone_alarm_to_the_primary_red_state()
    {
        string grinderCard = Read("Presentation", "ViewModels", "Home", "GrinderCardViewModel.cs");
        string home = Read("Presentation", "ViewModels", "Home", "HomeViewModel.cs");

        Assert.Contains("⚠ 磨石{stoneNo}厚度报警 · 已停止自动分配", grinderCard, StringComparison.Ordinal);
        Assert.Contains("stone1 || stone2", home, StringComparison.Ordinal);
        Assert.Contains("⚠ 磨石{stoneNo}厚度报警 · 已停止自动分配", home, StringComparison.Ordinal);
        Assert.Contains("stationCard.ConnectedBrush = fault || stone1 || stone2", home, StringComparison.Ordinal);
    }

    [Fact]
    public void Operations_docs_describe_single_machine_dispatch_block_not_engine_pause()
    {
        string root = RepositoryRoot.Find();
        string businessDocs = File.ReadAllText(Path.Combine(root, "Docs", "全线业务逻辑详解.md"));
        string protocolDocs = File.ReadAllText(Path.Combine(root, "Docs", "02-通信协议手册", "研磨机协议_ModbusTCP.md"));

        Assert.Contains("不暂停研磨引擎", businessDocs, StringComparison.Ordinal);
        Assert.Contains("停止自动分配", businessDocs, StringComparison.Ordinal);
        Assert.Contains("不暂停研磨引擎", protocolDocs, StringComparison.Ordinal);
        Assert.DoesNotContain("有报警 → 暂停引擎提示更换磨石", businessDocs, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepositoryRoot.Find() }.Concat(parts).ToArray()));

    private static string Slice(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        int endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex, "Expected grinding stone pre-pick section was not found.");
        return source[startIndex..endIndex];
    }
}
