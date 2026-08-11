using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Config;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class X11StableDelayConfigContractTests
{
    [Fact]
    public void Global_x11_stable_delay_is_exposed_on_configuration_page()
    {
        string model = Read("Infrastructure", "Config", "MotionConfig.cs");
        string json = Read("Config", "motion_settings.json");
        string viewModel = Read("Presentation", "ViewModels", "Config", "ConfigPageViewModel.cs");
        string view = Read("Views", "Config", "ConfigPageView.xaml");

        Assert.Contains("public int X11StableDelayMs { get; set; } = 3000;", model, StringComparison.Ordinal);
        Assert.Contains("\"x11StableDelayMs\": 3000", json, StringComparison.Ordinal);
        Assert.Contains("public int X11StableDelayMs", viewModel, StringComparison.Ordinal);
        Assert.Contains("_cfg.Grinding.X11StableDelayMs", viewModel, StringComparison.Ordinal);
        Assert.Contains("充磁后X11稳定等待", view, StringComparison.Ordinal);
        Assert.Contains("X11StableDelayMs", view, StringComparison.Ordinal);
        Assert.Contains("全线共用", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_page_updates_the_shared_x11_stable_delay_value()
    {
        var config = new MotionConfig();
        var viewModel = new ConfigPageViewModel(config);

        viewModel.X11StableDelayMs = 1800;

        Assert.Equal(1800, config.Grinding.X11StableDelayMs);
        Assert.Equal(1800, viewModel.X11StableDelayMs);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));
}
