using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Config;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class GrindingM731OverlapContractTests
{
    [Fact]
    public void M731_delay_is_persisted_and_exposed_on_the_configuration_page()
    {
        string model = Read("Infrastructure", "Config", "MotionConfig.cs");
        string json = Read("Config", "motion_settings.json");
        string viewModel = Read("Presentation", "ViewModels", "Config", "ConfigPageViewModel.cs");
        string view = Read("Views", "Config", "ConfigPageView.xaml");

        Assert.Contains("public int M731NotifyDelayMs { get; set; } = 1000;", model, StringComparison.Ordinal);
        Assert.Contains("\"m731NotifyDelayMs\": 1000", json, StringComparison.Ordinal);
        Assert.Contains("public int M731NotifyDelayMs", viewModel, StringComparison.Ordinal);
        Assert.Contains("_cfg.Grinding.M731NotifyDelayMs", viewModel, StringComparison.Ordinal);
        Assert.Contains("ST709取料后M731延时", view, StringComparison.Ordinal);
        Assert.Contains("M731NotifyDelayMs", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_page_updates_m731_delay()
    {
        var config = new MotionConfig();
        var viewModel = new ConfigPageViewModel(config);

        viewModel.M731NotifyDelayMs = 1250;

        Assert.Equal(1250, config.Grinding.M731NotifyDelayMs);
        Assert.Equal(1250, viewModel.M731NotifyDelayMs);
    }

    [Fact]
    public void M731_starts_after_the_configured_delay_while_safe_z_is_in_flight()
    {
        string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");
        string method = ExtractMethod(source, "private async Task ProcessWorkpieceAsync");

        int zStart = method.IndexOf("Task zSafeTask = crane.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);", StringComparison.Ordinal);
        int delay = method.IndexOf("await Task.Delay(_cfg.Grinding.M731NotifyDelayMs, ct);", StringComparison.Ordinal);
        int m731Start = method.IndexOf("Task m731Task = _mc65.WriteMBitInWordAsync(720, 11, true, ct);", StringComparison.Ordinal);
        int join = method.IndexOf("await Task.WhenAll(zSafeTask, m731Task);", StringComparison.Ordinal);

        Assert.True(zStart >= 0 && zStart < delay && delay < m731Start && m731Start < join,
            "必须先启动Z回安全高度，按配置延时启动M731，再等待两项都成功。");
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
