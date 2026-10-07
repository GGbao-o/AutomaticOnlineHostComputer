using AutomaticOnlineHostComputer.Infrastructure.Config;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class AbsFineTuneVerificationConfigContractTests
{
    [Fact]
    public void Shared_fine_tune_verification_defaults_and_json_are_exposed()
    {
        string model = Read("Infrastructure", "Config", "MotionConfig.cs");
        string json = Read("Config", "motion_settings.json");

        Assert.Contains("public AbsFineTuneVerificationSection AbsFineTuneVerification { get; set; } = new();", model, StringComparison.Ordinal);
        Assert.Contains("public int BeforeReadSettleDelayMs { get; set; } = 1200;", model, StringComparison.Ordinal);
        Assert.Contains("public int StableSampleCount { get; set; } = 3;", model, StringComparison.Ordinal);
        Assert.Contains("public int StableSampleIntervalMs { get; set; } = 200;", model, StringComparison.Ordinal);
        Assert.Contains("public int StableRangeMm { get; set; } = 1;", model, StringComparison.Ordinal);
        Assert.Contains("public int AfterMoveMinSettleDelayMs { get; set; } = 1200;", model, StringComparison.Ordinal);
        Assert.Contains("\"absFineTuneVerification\"", json, StringComparison.Ordinal);
        Assert.Contains("\"beforeReadSettleDelayMs\": 1200", json, StringComparison.Ordinal);
        Assert.Contains("\"stableSampleCount\": 3", json, StringComparison.Ordinal);
        Assert.Contains("\"stableSampleIntervalMs\": 200", json, StringComparison.Ordinal);
        Assert.Contains("\"stableRangeMm\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"afterMoveMinSettleDelayMs\": 1200", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_page_binds_all_shared_verification_inputs()
    {
        string viewModel = Read("Presentation", "ViewModels", "Config", "ConfigPageViewModel.cs");
        string view = Read("Views", "Config", "ConfigPageView.xaml");

        Assert.Contains("public int AbsFineTuneBeforeReadSettleDelayMs", viewModel, StringComparison.Ordinal);
        Assert.Contains("public int AbsFineTuneStableSampleCount", viewModel, StringComparison.Ordinal);
        Assert.Contains("public int AbsFineTuneStableSampleIntervalMs", viewModel, StringComparison.Ordinal);
        Assert.Contains("public int AbsFineTuneStableRangeMm", viewModel, StringComparison.Ordinal);
        Assert.Contains("public int AbsFineTuneAfterMoveMinSettleDelayMs", viewModel, StringComparison.Ordinal);
        Assert.Contains("微调读取验证（所有天车共用）", view, StringComparison.Ordinal);
        Assert.Contains("AbsFineTuneBeforeReadSettleDelayMs", view, StringComparison.Ordinal);
        Assert.Contains("AbsFineTuneStableSampleCount", view, StringComparison.Ordinal);
        Assert.Contains("AbsFineTuneStableSampleIntervalMs", view, StringComparison.Ordinal);
        Assert.Contains("AbsFineTuneStableRangeMm", view, StringComparison.Ordinal);
        Assert.Contains("AbsFineTuneAfterMoveMinSettleDelayMs", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_manual_values_fall_back_to_the_confirmed_safe_defaults()
    {
        var config = new MotionConfig();
        config.AbsFineTuneVerification.BeforeReadSettleDelayMs = 0;
        config.AbsFineTuneVerification.StableSampleCount = 1;
        config.AbsFineTuneVerification.StableSampleIntervalMs = 0;
        config.AbsFineTuneVerification.StableRangeMm = -1;
        config.AbsFineTuneVerification.AfterMoveMinSettleDelayMs = 3000;

        MotionConfig.FineTuneVerificationValues values = config.AbsFineTuneVerification.GetValidated();

        Assert.Equal(1200, values.BeforeReadSettleDelayMs);
        Assert.Equal(3, values.StableSampleCount);
        Assert.Equal(200, values.StableSampleIntervalMs);
        Assert.Equal(1, values.StableRangeMm);
        Assert.Equal(1200, values.AfterMoveMinSettleDelayMs);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));
}
