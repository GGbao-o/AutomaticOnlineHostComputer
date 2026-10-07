namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class M2RearDispatchSafetyContractTests
{
    [Fact]
    public void M817_dispatch_requires_its_cache_and_checks_both_rear_cranes_for_the_route()
    {
        string source = ReadFlowSource("BalancingFlowEngine.cs");

        Assert.Contains("bool m817Cached = HasBalWp(\"M817\");", source, StringComparison.Ordinal);
        Assert.Contains("(_m817 && m817Cached && !m818Cached)", source, StringComparison.Ordinal);
        Assert.Contains("await AreBothRearCranesAtSafeXAsync(ct)", source, StringComparison.Ordinal);
        Assert.Contains("await IsCraneAtSafeXAsync(2, ct)", source, StringComparison.Ordinal);
        Assert.Contains("await IsCraneAtSafeXAsync(4, ct)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void M3_dispatch_checks_both_rear_cranes_regardless_of_source()
    {
        string source = ReadFlowSource("BalancingFlowEngine.cs");
        int start = source.IndexOf("// ── ⑥ 机械手3触发条件", StringComparison.Ordinal);
        int end = source.IndexOf("await Task.Delay(_cfg.Grinding.PollIntervalMs", start, StringComparison.Ordinal);
        string m3Block = source[start..end];

        Assert.Contains("await AreBothRearCranesAtSafeXAsync(ct)", m3Block, StringComparison.Ordinal);
        Assert.DoesNotContain("await IsCraneAtSafeXAsync(4, ct)", m3Block, StringComparison.Ordinal);
        Assert.DoesNotContain("!useM700 && !await", m3Block, StringComparison.Ordinal);
    }

    [Fact]
    public void Rear_unload_waits_for_both_manipulators_below_config_threshold_without_timeout()
    {
        foreach (string file in new[] { "Line1RearFlowEngine.cs", "Line2RearFlowEngine.cs" })
        {
            string source = ReadFlowSource(file);
            int start = source.IndexOf("private async Task WaitManipulatorsClearOfRackAsync", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{file} 缺少 WaitManipulatorsClearOfRackAsync");
            string method = source[start..];
            Assert.Contains("_cfg.Balancing.M2ReleaseLocksBelowY", method, StringComparison.Ordinal);
            Assert.Contains("_cfg.Balancing.M3ReleaseLocksBelowY", method, StringComparison.Ordinal);
            Assert.Contains("while (true)", method, StringComparison.Ordinal);
            Assert.DoesNotContain("HandshakeTimeoutMs", method, StringComparison.Ordinal);
            Assert.DoesNotContain("TimeoutException", method, StringComparison.Ordinal);
        }

        string line1 = ReadFlowSource("Line1RearFlowEngine.cs");
        int confirm720 = line1.IndexOf("await WaitForM720CanPlaceBeforePlaceAsync(wp, ct);", StringComparison.Ordinal);
        int wait720 = line1.IndexOf("await WaitManipulatorsClearOfRackAsync(ct);", confirm720, StringComparison.Ordinal);
        int flag = line1.IndexOf("if (_st010PlacementFlags.BeginCrane2Placement())", StringComparison.Ordinal);
        Assert.True(confirm720 >= 0 && wait720 > confirm720 && flag > wait720,
            "ST010短板分支必须先等机械手2/3离开料架区，再置位Crane2St010Placing并发送XY。");

        string line2 = ReadFlowSource("Line2RearFlowEngine.cs");
        // ST020/ST021 的目的地二次确认已由"抛异常"改为"持锁等待"；
        // 契约不变：目的地确认（含持锁等待）必须先完成，之后才等待机械手2/3离开料架区。
        int confirm818 = line2.IndexOf("await EnsureM818CanPlaceBeforePlaceAsync(wp, ct);", StringComparison.Ordinal);
        int wait818 = line2.IndexOf("await WaitManipulatorsClearOfRackAsync(ct);", confirm818, StringComparison.Ordinal);
        int confirm820 = line2.IndexOf("await EnsureM820CanPlaceBeforePlaceAsync(wp, ct);", StringComparison.Ordinal);
        int wait820 = line2.IndexOf("await WaitManipulatorsClearOfRackAsync(ct);", confirm820, StringComparison.Ordinal);
        Assert.True(confirm818 >= 0 && wait818 > confirm818, "ST020分支必须在M818确认后等待机械手2/3。");
        Assert.True(confirm820 >= 0 && wait820 > confirm820, "ST021分支必须在M820确认后等待机械手2/3。");
    }

    [Fact]
    public void M2_and_M3_recheck_both_rear_cranes_right_after_acquiring_locks()
    {
        string source = ReadFlowSource("BalancingFlowEngine.cs");

        int m2Start = source.IndexOf("private async Task M2Flow", StringComparison.Ordinal);
        int m3Start = source.IndexOf("private async Task M3Flow", StringComparison.Ordinal);
        Assert.True(m2Start >= 0 && m3Start > m2Start, "未找到 M2Flow/M3Flow");
        string m2 = source[m2Start..m3Start];
        string m3 = source[m3Start..];

        Assert.Contains("await AreBothRearCranesAtSafeXAsync(ct)", m2, StringComparison.Ordinal);
        Assert.Contains("ReleaseM2RackLocks(\"锁后天车复核未通过\")", m2, StringComparison.Ordinal);

        Assert.Contains("await AreBothRearCranesAtSafeXAsync(ct)", m3, StringComparison.Ordinal);
        Assert.Contains("ReleaseM3RackLocks(\"锁后天车复核未通过\")", m3, StringComparison.Ordinal);
    }

    [Fact]
    public void Rear_crane_waits_when_M720_is_temporarily_not_ready_but_keeps_read_failures_as_exceptions()
    {
        string source = ReadFlowSource("Line1RearFlowEngine.cs");

        Assert.Contains("await WaitForM720CanPlaceBeforePlaceAsync(wp, ct);", source, StringComparison.Ordinal);
        Assert.Contains("while (!await ReadM720CanPlaceBeforePlaceAsync(ct))", source, StringComparison.Ordinal);
        Assert.Contains("M720二次确认=不可放料，保持持板等待", source, StringComparison.Ordinal);
        Assert.DoesNotContain("M720二次确认=不可放料, 禁止放料", source, StringComparison.Ordinal);
    }

    [Fact]
    public void M3_waits_with_the_workpiece_and_locks_when_M720_is_temporarily_not_ready()
    {
        string source = ReadFlowSource("BalancingFlowEngine.cs");
        int start = source.IndexOf("private async Task ConfirmM720EmptyBeforePlaceAsync", StringComparison.Ordinal);
        int end = source.IndexOf("private async Task ConfirmMc63HandshakeBitAsync", start, StringComparison.Ordinal);
        string method = source[start..end];

        Assert.Contains("while (true)", method, StringComparison.Ordinal);
        Assert.Contains("M720二次确认=不可放料，保持持板等待", method, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);", method, StringComparison.Ordinal);
        Assert.Contains("M720恢复可放料，结束持板等待，继续放料", method, StringComparison.Ordinal);
        Assert.DoesNotContain("M720二次确认=不可放料, 禁止M3放料", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Business_docs_describe_the_new_M817_and_M720_safety_rules()
    {
        string docs = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Docs", "全线业务逻辑详解.md"));

        Assert.Contains("`_m817=1` 和 `_balWps[\"M817\"]` 缓存存在", docs, StringComparison.Ordinal);
        Assert.Contains("2号和4号后天车", docs, StringComparison.Ordinal);
        Assert.Contains("目标暂时不可放料", docs, StringComparison.Ordinal);
        Assert.Contains("抱板后 M720=0", docs, StringComparison.Ordinal);
        Assert.Contains("保持持件、安全 Z、`M821 + M720` 位置锁和 M3Busy", docs, StringComparison.Ordinal);
    }

    private static string ReadFlowSource(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", fileName));
}
