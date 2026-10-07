namespace AutomaticOnlineHostComputer.Tests.Communication;

public sealed class MagnetFeedbackConfigurationContractTests
{
    [Fact]
    public void Magnet_feedback_uses_one_configurable_three_attempt_policy()
    {
        string config = Read("Infrastructure", "Config", "MotionConfig.cs");
        string json = Read("Config", "motion_settings.json");
        string service = Read("Communication", "DeviceServices", "CraneService.cs");

        Assert.Contains("MagnetFeedbackMaxAttempts", config, StringComparison.Ordinal);
        Assert.Contains("MagnetFeedbackTimeoutMs", config, StringComparison.Ordinal);
        Assert.Contains("\"magnetFeedbackMaxAttempts\": 3", json, StringComparison.Ordinal);
        Assert.Contains("\"magnetFeedbackTimeoutMs\": 3000", json, StringComparison.Ordinal);
        Assert.Contains("MagnetFeedbackTimeoutException", service, StringComparison.Ordinal);
        Assert.Contains("magnetFeedbackOptionsProvider", service, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepositoryRoot.Find() }.Concat(parts).ToArray()));
}
