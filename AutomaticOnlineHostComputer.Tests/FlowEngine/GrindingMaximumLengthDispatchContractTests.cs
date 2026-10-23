namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class GrindingMaximumLengthDispatchContractTests
{
    [Fact]
    public void Grinding_maximum_length_is_configured_in_mm_and_exposed_on_configuration_page()
    {
        string model = Read("Infrastructure", "Config", "MotionConfig.cs");
        string json = Read("Config", "motion_settings.json");
        string viewModel = Read("Presentation", "ViewModels", "Config", "ConfigPageViewModel.cs");
        string view = Read("Views", "Config", "ConfigPageView.xaml");

        Assert.Contains("MaxWorkpieceLengthMm", model, StringComparison.Ordinal);
        Assert.Contains("CanProcessLength", model, StringComparison.Ordinal);
        Assert.Contains("\"maxWorkpieceLengthMm\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ST701\"", json, StringComparison.Ordinal);
        Assert.Contains("GrindingLengthRows", viewModel, StringComparison.Ordinal);
        Assert.Contains("研磨机最大加工长度 (mm)", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispatch_resolves_head_length_and_matches_before_crane_prepare_or_dequeue()
    {
        string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");
        string method = ExtractMethod(source, "private async Task<bool> TryDispatchLoadAsync");

        int peek = method.IndexOf("TryPeekCache", StringComparison.Ordinal);
        int resolveLength = method.IndexOf("ResolveDispatchWorkpieceLengthAsync", StringComparison.Ordinal);
        int match = method.IndexOf("FindReadyGrinder(dispatchWp.Length)", StringComparison.Ordinal);
        int finalCheck = method.IndexOf("IsReadyGrinderForLength(ready, dispatchWp.Length)", StringComparison.Ordinal);
        int dequeue = method.IndexOf("TryDequeueCache", StringComparison.Ordinal);
        int pending = method.IndexOf("ready.PendingWorkpiece = wp", StringComparison.Ordinal);

        Assert.True(peek >= 0 && peek < resolveLength && resolveLength < match && match < finalCheck && finalCheck < dequeue,
            "必须先窥视FIFO、补足/确认长度并选择长度匹配机台，最终复核机台准入后才能出队；启动时的回原点门禁不在派发中重复执行。");
        Assert.DoesNotContain("EnsureGrindingCranePositionReadyAsync", method, StringComparison.Ordinal);
        Assert.True(dequeue < pending, "Pending只能在长度匹配并成功出队之后写入。");
        Assert.Contains("_cfg.Grinding.CanProcessLength", source, StringComparison.Ordinal);
        Assert.Contains("M700来源且FIFO长度为0", source, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));

    private static string ExtractMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到方法: {signature}");
        int nextSummary = source.IndexOf("    /// <summary>", start + signature.Length, StringComparison.Ordinal);
        return nextSummary >= 0 ? source[start..nextSummary] : source[start..];
    }
}
