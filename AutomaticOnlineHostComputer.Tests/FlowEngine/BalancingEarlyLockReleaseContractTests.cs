namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 提前放锁的关键业务边界：只能使用既有运动轮询中的实际Y，且来源缓存必须先转为在途工件。
/// </summary>
public sealed class BalancingEarlyLockReleaseContractTests
{
    [Fact]
    public void Configuration_exposes_unrestricted_M2_and_M3_release_thresholds()
    {
        string root = RepositoryRoot.Find();
        string model = File.ReadAllText(Path.Combine(root, "Infrastructure", "Config", "MotionConfig.cs"));
        string viewModel = File.ReadAllText(Path.Combine(root, "Presentation", "ViewModels", "Config", "ConfigPageViewModel.cs"));
        string view = File.ReadAllText(Path.Combine(root, "Views", "Config", "ConfigPageView.xaml"));
        string json = File.ReadAllText(Path.Combine(root, "Config", "motion_settings.json"));

        Assert.Contains("public int M2ReleaseLocksBelowY", model, StringComparison.Ordinal);
        Assert.Contains("public int M3ReleaseLocksBelowY", model, StringComparison.Ordinal);
        Assert.Contains("M2ReleaseLocksBelowY", viewModel, StringComparison.Ordinal);
        Assert.Contains("M3ReleaseLocksBelowY", viewModel, StringComparison.Ordinal);
        Assert.Contains("Binding M2ReleaseLocksBelowY", view, StringComparison.Ordinal);
        Assert.Contains("Binding M3ReleaseLocksBelowY", view, StringComparison.Ordinal);
        Assert.Contains("\"m2ReleaseLocksBelowY\": 0", json, StringComparison.Ordinal);
        Assert.Contains("\"m3ReleaseLocksBelowY\": 0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void M2_and_M3_transfer_source_cache_before_their_actual_y_based_early_release()
    {
        string root = RepositoryRoot.Find();
        string flow = File.ReadAllText(Path.Combine(root, "Service", "FlowEngine", "BalancingFlowEngine.cs"));
        string crane = File.ReadAllText(Path.Combine(root, "Communication", "DeviceServices", "CraneService.cs"));

        Assert.Contains("TransferBalancingCacheToInFlight", flow, StringComparison.Ordinal);
        Assert.Contains("CreateInFlightWorkpiece", flow, StringComparison.Ordinal);
        Assert.Contains("yStatusObserver: ObserveM2TransitY", flow, StringComparison.Ordinal);
        Assert.Contains("yStatusObserver: ObserveM3ReturnY", flow, StringComparison.Ordinal);
        Assert.Contains("status.YPos >= m2ReleaseLocksBelowY", flow, StringComparison.Ordinal);
        Assert.Contains("status.YPos >= m3ReleaseLocksBelowY", flow, StringComparison.Ordinal);
        Assert.Contains("ReleaseM2RackLocks", flow, StringComparison.Ordinal);
        Assert.Contains("ReleaseM3RackLocks", flow, StringComparison.Ordinal);
        Assert.Contains("可能是后天车新写入", flow, StringComparison.Ordinal);
        Assert.True(
            flow.IndexOf("if (touchesM2 && m2SourceCacheTransferred)", StringComparison.Ordinal) <
            flow.IndexOf("else if (touchesM2 && !string.IsNullOrWhiteSpace(m2Source))", StringComparison.Ordinal),
            "M2旧动作已转在途时，必须先走保护来源缓存分支。");
        Assert.True(
            flow.IndexOf("if (touchesM3 && m3SourceCacheTransferred)", StringComparison.Ordinal) <
            flow.IndexOf("else if (touchesM3 && string.Equals(m3Source", StringComparison.Ordinal),
            "M3旧动作已转在途时，必须先走保护来源缓存分支。");
        Assert.Contains("Action<CraneStatus>? yStatusObserver", crane, StringComparison.Ordinal);
        Assert.True(
            flow.IndexOf("TransferBalancingCacheToInFlight(true", StringComparison.Ordinal) <
            flow.IndexOf("yStatusObserver: ObserveM2TransitY", StringComparison.Ordinal),
            "M2必须先把来源缓存转为在途工件，才允许在去ST008途中释放来源锁。");
        Assert.True(
            flow.IndexOf("TransferBalancingCacheToInFlight(false", StringComparison.Ordinal) <
            flow.IndexOf("yStatusObserver: ObserveM3ReturnY", StringComparison.Ordinal),
            "M3必须先把M821缓存转为在途工件，才允许在回安全位途中释放锁。");
    }

    [Fact]
    public void Core_business_document_describes_both_early_release_paths_and_manual_protection()
    {
        string doc = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Docs", "全线业务逻辑详解.md"));

        Assert.Contains("### 11.3 机械手2完整动作", doc, StringComparison.Ordinal);
        Assert.Contains("balancing.m2ReleaseLocksBelowY", doc, StringComparison.Ordinal);
        Assert.Contains("### 11.5 机械手3完整动作", doc, StringComparison.Ordinal);
        Assert.Contains("balancing.m3ReleaseLocksBelowY", doc, StringComparison.Ordinal);
        Assert.Contains("旧M2动作也只处理自己的在途工件", doc, StringComparison.Ordinal);
        Assert.Contains("旧M3动作也只使用自己的在途工件", doc, StringComparison.Ordinal);
        Assert.Contains("拒绝旧工件覆盖", doc, StringComparison.Ordinal);
    }
}
