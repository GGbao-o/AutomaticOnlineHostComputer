using System.Text.Json;
using AutomaticOnlineHostComputer.Infrastructure.Config;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Config;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>验证探测配置的兼容加载、保存、页面共享内存和全部生产入口接入；不连接PLC。</summary>
public sealed class PickupProbeConfigTests
{
    [Fact]
    public void Old_json_without_probe_fields_preserves_three_five_defaults()
    {
        var config = JsonSerializer.Deserialize<MotionConfig>("{\"grinding\":{\"x11StableDelayMs\":500}}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(3, config.Grinding.PickupProbeCount);
        Assert.Equal(5, config.Grinding.PickupProbeStepMm);
        Assert.Equal(500, config.Grinding.X11StableDelayMs);
    }

    [Fact]
    public void New_probe_fields_round_trip_using_the_real_json_naming_policy()
    {
        var config = new MotionConfig();
        config.Grinding.PickupProbeCount = 4;
        config.Grinding.PickupProbeStepMm = 2;
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string json = JsonSerializer.Serialize(config, options);
        Assert.Contains("\"pickupProbeCount\":4", json);
        Assert.Contains("\"pickupProbeStepMm\":2", json);
        var loaded = JsonSerializer.Deserialize<MotionConfig>(json, options)!;
        Assert.Equal(4, loaded.Grinding.PickupProbeCount);
        Assert.Equal(2, loaded.Grinding.PickupProbeStepMm);
    }

    [Fact]
    public void Page_updates_shared_settings_and_notifies_the_derived_distance()
    {
        var config = new MotionConfig();
        var viewModel = new ConfigPageViewModel(config);
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        viewModel.PickupProbeCount = 4;
        viewModel.PickupProbeStepMm = 2;
        Assert.Equal(4, config.Grinding.PickupProbeCount);
        Assert.Equal(2, config.Grinding.PickupProbeStepMm);
        Assert.Contains("6mm", viewModel.PickupProbeSummary);
        Assert.Equal(2, notifications.Count(x => x == nameof(viewModel.PickupProbeSummary)));
        viewModel.PickupProbeCount = 1;
        Assert.Contains("0mm", viewModel.PickupProbeSummary);
        viewModel.PickupProbeStepMm = 0;
        Assert.Contains("无效", viewModel.PickupProbeSummary);
    }

    [Fact]
    public void Every_production_pickup_entry_passes_a_config_snapshot()
    {
        var files = new Dictionary<string, int>
        {
            ["Line1FrontFlowEngine.cs"] = 3, ["Line2FrontFlowEngine.cs"] = 3,
            ["Line1RearFlowEngine.cs"] = 2, ["Line2RearFlowEngine.cs"] = 2,
            ["GrindingFlowEngine.cs"] = 2, ["BalancingFlowEngine.cs"] = 2
        };
        const string snapshot = "settings: new(_cfg.Grinding.PickupProbeCount, _cfg.Grinding.PickupProbeStepMm)";
        foreach (var pair in files)
        {
            string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", pair.Key));
            Assert.Equal(pair.Value, source.Split("PickupProbeHelper.PickAsync(").Length - 1);
            Assert.Equal(pair.Value, source.Split(snapshot).Length - 1);
        }
    }

    [Fact]
    public void Shipped_json_and_page_expose_both_probe_inputs()
    {
        string root = RepositoryRoot.Find();
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Config", "motion_settings.json")));
        var grinding = json.RootElement.GetProperty("grinding");
        Assert.Equal(3, grinding.GetProperty("pickupProbeCount").GetInt32());
        Assert.Equal(5, grinding.GetProperty("pickupProbeStepMm").GetInt32());
        string view = File.ReadAllText(Path.Combine(root, "Views", "Config", "ConfigPageView.xaml"));
        Assert.Contains("{Binding PickupProbeCount,", view);
        Assert.Contains("{Binding PickupProbeStepMm,", view);
        Assert.Contains("{Binding PickupProbeSummary}", view);
    }
}
