namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 主页面“恢复内存状态”只能放开已确认的候选门槛；不能变成清PLC、清恢复标记或清动作账本的旁路入口。
/// </summary>
public sealed class HomeMemoryStateResetContractTests
{
    [Theory]
    [InlineData("Line1RearFlowEngine.cs")]
    [InlineData("Line2RearFlowEngine.cs")]
    public void Skew_memory_reset_only_sets_idle_and_clears_workpiece(string fileName)
    {
        string method = ExtractMethod(Read("Service", "FlowEngine", fileName), "public string ResetSkewMemoryState");

        Assert.Contains("bed.St = SkewState.Idle", method, StringComparison.Ordinal);
        Assert.Contains("bed.Wp = null", method, StringComparison.Ordinal);
        Assert.DoesNotContain("CompletionExported =", method, StringComparison.Ordinal);
        Assert.DoesNotContain("WpRecoveryNeeded =", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalFresh =", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveOperation", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Grinder_memory_reset_only_sets_idle_and_clears_pending_workpiece()
    {
        string method = ExtractMethod(Read("Service", "FlowEngine", "GrindingFlowEngine.cs"), "public string ResetGrindingMemoryState");

        Assert.Contains("grinder.State = GrinderState.Idle", method, StringComparison.Ordinal);
        Assert.Contains("grinder.PendingWorkpiece = null", method, StringComparison.Ordinal);
        Assert.DoesNotContain("WpRecoveryNeeded =", method, StringComparison.Ordinal);
        Assert.DoesNotContain("LastRequestData =", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalScanVersion", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_page_routes_memory_reset_to_a_dedicated_device_dialog()
    {
        string view = Read("Views", "Home", "HomeView.xaml");
        string codeBehind = Read("Views", "Home", "HomeView.xaml.cs");
        string viewModel = Read("Presentation", "ViewModels", "Home", "HomeViewModel.cs");
        string dialogXaml = Read("Views", "Home", "Dialogs", "MemoryStateResetDialog.xaml");
        string dialogCode = Read("Views", "Home", "Dialogs", "MemoryStateResetDialog.xaml.cs");

        Assert.Contains("恢复内存状态", view, StringComparison.Ordinal);
        Assert.Contains("ResetMemoryState_Click", view, StringComparison.Ordinal);
        Assert.Contains("new MemoryStateResetDialog", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ResetSkewMemoryState", viewModel, StringComparison.Ordinal);
        Assert.Contains("ResetGrindingMemoryState", viewModel, StringComparison.Ordinal);
        Assert.Contains("St→Idle、Wp→null", dialogCode, StringComparison.Ordinal);
        Assert.Contains("State→Idle、PendingWorkpiece→null", dialogCode, StringComparison.Ordinal);

        foreach (string code in new[] { "ST108", "ST109", "ST111", "ST110", "ST112", "ST606", "ST607", "ST608", "ST609", "ST610", "ST701", "ST702", "ST703", "ST704" })
            Assert.Contains(code, dialogXaml, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到方法: {signature}");

        int nextSummary = source.IndexOf("    /// <summary>", start + signature.Length, StringComparison.Ordinal);
        return nextSummary >= 0 ? source[start..nextSummary] : source[start..];
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));
}
