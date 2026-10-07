using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Config;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class Line1M721OverlapContractTests
{
    [Fact]
    public void M721_delay_is_persisted_and_exposed_on_the_configuration_page()
    {
        string model = Read("Infrastructure", "Config", "MotionConfig.cs");
        string json = Read("Config", "motion_settings.json");
        string viewModel = Read("Presentation", "ViewModels", "Config", "ConfigPageViewModel.cs");
        string view = Read("Views", "Config", "ConfigPageView.xaml");

        Assert.Contains("public int M721NotifyDelayMs { get; set; } = 1000;", model, StringComparison.Ordinal);
        Assert.Contains("\"m721NotifyDelayMs\": 1000", json, StringComparison.Ordinal);
        Assert.Contains("public int M721NotifyDelayMs", viewModel, StringComparison.Ordinal);
        Assert.Contains("_cfg.Grinding.M721NotifyDelayMs", viewModel, StringComparison.Ordinal);
        Assert.Contains("ST010放料后M721延时", view, StringComparison.Ordinal);
        Assert.Contains("M721NotifyDelayMs", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_page_updates_m721_delay()
    {
        var config = new MotionConfig();
        var viewModel = new ConfigPageViewModel(config);

        viewModel.M721NotifyDelayMs = 1250;

        Assert.Equal(1250, config.Grinding.M721NotifyDelayMs);
        Assert.Equal(1250, viewModel.M721NotifyDelayMs);
    }

    [Fact]
    public void Line1_m721_success_queues_the_workpiece_before_safe_z_completes()
    {
        string source = Read("Service", "FlowEngine", "Line1RearFlowEngine.cs");
        string method = ExtractMethod(source, "private async Task DoUnload");

        int zStart = method.IndexOf("Task st010SafeZTask = cr.MoveAbsoluteAsync(-1, -1, sz, ct: ct);", StringComparison.Ordinal);
        int delay = method.IndexOf("await Task.Delay(_cfg.Grinding.M721NotifyDelayMs, ct);", StringComparison.Ordinal);
        int m721Start = method.IndexOf("Task m721Task = mc65.WriteMBitInWordAsync(720, 1, true, ct);", StringComparison.Ordinal);
        int m721Done = method.IndexOf("await m721Task;", StringComparison.Ordinal);
        int cacheWrite = method.IndexOf("OnGrindingRackPlaced.Invoke(wp);", StringComparison.Ordinal);
        int zDone = method.LastIndexOf("await st010SafeZTask;", StringComparison.Ordinal);

        Assert.True(zStart >= 0 && zStart < delay && delay < m721Start && m721Start < m721Done && m721Done < cacheWrite && cacheWrite < zDone,
            "必须先启动Z回升，延时后写M721；M721成功即写研磨FIFO，不能等待Z安全到位。");
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
