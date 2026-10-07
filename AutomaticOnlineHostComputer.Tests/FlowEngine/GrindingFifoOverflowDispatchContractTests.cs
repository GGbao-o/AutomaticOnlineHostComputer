using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Config;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class GrindingFifoOverflowDispatchContractTests
{
    [Fact]
    public void Max_fifo_count_is_persisted_and_exposed_on_the_configuration_page()
    {
        string model = Read("Infrastructure", "Config", "MotionConfig.cs");
        string json = Read("Config", "motion_settings.json");
        string viewModel = Read("Presentation", "ViewModels", "Config", "ConfigPageViewModel.cs");
        string view = Read("Views", "Config", "ConfigPageView.xaml");

        Assert.Contains("public int MaxFifoCount { get; set; } = 2;", model, StringComparison.Ordinal);
        Assert.Contains("\"maxFifoCount\":", json, StringComparison.Ordinal);
        Assert.Contains("public int GrindingMaxFifoCount", viewModel, StringComparison.Ordinal);
        Assert.Contains("_cfg.Grinding.MaxFifoCount", viewModel, StringComparison.Ordinal);
        Assert.Contains("研磨上料FIFO最大缓存数", view, StringComparison.Ordinal);
        Assert.Contains("GrindingMaxFifoCount", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_page_updates_max_fifo_count()
    {
        var config = new MotionConfig();
        var viewModel = new ConfigPageViewModel(config);

        viewModel.GrindingMaxFifoCount = 3;

        Assert.Equal(3, config.Grinding.MaxFifoCount);
        Assert.Equal(3, viewModel.GrindingMaxFifoCount);
    }

    [Fact]
    public void Overflow_pauses_before_dequeue_and_before_starting_a_crane_action()
    {
        string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");
        string method = ExtractMethod(source, "private async Task<bool> TryDispatchLoadAsync");

        int craneLock = method.IndexOf("await _craneLock.WaitAsync(0, ct)", StringComparison.Ordinal);
        int dispatchGate = method.IndexOf("await _grindingDispatchGate.WaitAsync(ct)", StringComparison.Ordinal);
        int pausedCheck = method.IndexOf("if (_paused)", dispatchGate, StringComparison.Ordinal);
        int overflowGuard = method.IndexOf("if (cachedCount > maxFifoCount)", StringComparison.Ordinal);
        int pause = method.IndexOf("_paused = true;", overflowGuard, StringComparison.Ordinal);
        int alarm = method.IndexOf("OnSafetyAlarm?.Invoke", overflowGuard, StringComparison.Ordinal);
        int dequeue = method.IndexOf("TryDequeueCache", StringComparison.Ordinal);
        int actionStart = method.IndexOf("ProcessWorkpieceAsync", StringComparison.Ordinal);

        Assert.True(craneLock >= 0 && craneLock < dispatchGate && dispatchGate < pausedCheck && pausedCheck < overflowGuard,
            "FIFO超限检查必须在成功取得天车锁和派发门闩、完成暂停二次检查后执行。");
        Assert.True(overflowGuard < pause && pause < alarm && alarm < dequeue && dequeue < actionStart,
            "超限时必须先软暂停并告警，随后不得出队或启动天车动作。");
        Assert.Contains("每次只清除一个FIFO队头数据", method[overflowGuard..dequeue], StringComparison.Ordinal);
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
