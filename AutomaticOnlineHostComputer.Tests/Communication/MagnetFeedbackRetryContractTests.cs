namespace AutomaticOnlineHostComputer.Tests.Communication;

public sealed class MagnetFeedbackRetryContractTests
{
    [Fact]
    public void Magnet_on_and_off_require_configured_feedback_before_success()
    {
        string service = Read("Communication", "DeviceServices", "CraneService.cs");

        Assert.Contains("for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)", service,
            StringComparison.Ordinal);
        Assert.Contains("Addr.D_ManualMagnetOn", service, StringComparison.Ordinal);
        Assert.Contains("Addr.D_ManualMagnetOff", service, StringComparison.Ordinal);
        Assert.Contains("if (x6)", service, StringComparison.Ordinal);
        Assert.Contains("if (x7 && !x6)", service, StringComparison.Ordinal);
        Assert.Contains("new MagnetFeedbackTimeoutException", service, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepositoryRoot.Find() }.Concat(parts).ToArray()));
}
