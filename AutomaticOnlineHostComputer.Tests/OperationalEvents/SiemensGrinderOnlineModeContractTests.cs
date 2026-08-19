namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class SiemensGrinderOnlineModeContractTests
{
    [Fact]
    public void TypeA_online_mode_blocks_only_new_automatic_load_dispatch()
    {
        string address = Read("Communication", "DeviceAddresses", "PlcGrinderAddress.cs");
        string service = Read("Communication", "DeviceServices", "PlcGrinderService.cs");
        string engine = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");

        Assert.Contains("Bit_OnlineMode", address, StringComparison.Ordinal);
        Assert.Contains("= 3", address, StringComparison.Ordinal);
        Assert.Contains("public bool OnlineMode", service, StringComparison.Ordinal);
        Assert.Contains("LastOnlineMode", engine, StringComparison.Ordinal);
        Assert.Contains("g.GrinderType != PlcGrinderService.GrinderType.TypeA || g.LastOnlineMode", engine, StringComparison.Ordinal);

        string prePick = Slice(engine, "// ── ② 检查磨石厚度报警", "// ── ③ 补充长度后写工件参数");
        Assert.Contains("!snapshot.OnlineMode", prePick, StringComparison.Ordinal);
        Assert.Contains("RequeueWorkpiece(wp)", prePick, StringComparison.Ordinal);
        Assert.DoesNotContain("_paused = true", prePick, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeA_cards_display_online_or_standalone_mode()
    {
        string grinderCard = Read("Presentation", "ViewModels", "Home", "GrinderCardViewModel.cs");
        string home = Read("Presentation", "ViewModels", "Home", "HomeViewModel.cs");

        Assert.Contains("单机模式，停止自动分配", grinderCard, StringComparison.Ordinal);
        Assert.Contains("联机", grinderCard, StringComparison.Ordinal);
        Assert.Contains("联机={To01(onlineMode)}", home, StringComparison.Ordinal);
        Assert.Contains("单机模式，停止自动分配", home, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepositoryRoot.Find() }.Concat(parts).ToArray()));

    private static string Slice(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        int endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex, "Expected TypeA pre-pick verification section was not found.");
        return source[startIndex..endIndex];
    }
}
