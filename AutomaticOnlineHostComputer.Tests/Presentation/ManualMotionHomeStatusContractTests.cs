namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class ManualMotionHomeStatusContractTests
{
    [Fact]
    public void Refresh_reuses_the_status_snapshot_to_show_three_read_only_home_flags()
    {
        string service = Read("Communication", "DeviceServices", "CraneService.cs");
        string viewModel = Read("Presentation", "ViewModels", "Home", "CraneManualControlViewModel.cs");
        string xaml = Read("Views", "Motion", "Controls", "ManualMotionControlPanel.xaml");

        Assert.Contains("XHomeCompleted", service, StringComparison.Ordinal);
        Assert.Contains("YHomeCompleted", service, StringComparison.Ordinal);
        Assert.Contains("ZHomeCompleted", service, StringComparison.Ordinal);
        Assert.Contains("XHomeCompleted", service, StringComparison.Ordinal);
        Assert.Contains("YHomeCompleted", service, StringComparison.Ordinal);
        Assert.Contains("ZHomeCompleted", service, StringComparison.Ordinal);
        Assert.Contains("// D5006", service, StringComparison.Ordinal);
        Assert.Contains("// D5007", service, StringComparison.Ordinal);
        Assert.Contains("// D5008", service, StringComparison.Ordinal);

        Assert.Contains("public bool? IsXHomeCompleted", viewModel, StringComparison.Ordinal);
        Assert.Contains("public bool? IsYHomeCompleted", viewModel, StringComparison.Ordinal);
        Assert.Contains("public bool? IsZHomeCompleted", viewModel, StringComparison.Ordinal);
        Assert.Contains("status.XHomeCompleted != 0", viewModel, StringComparison.Ordinal);
        Assert.Contains("status.YHomeCompleted != 0", viewModel, StringComparison.Ordinal);
        Assert.Contains("status.ZHomeCompleted != 0", viewModel, StringComparison.Ordinal);

        Assert.Contains("X轴已回原点", viewModel, StringComparison.Ordinal);
        Assert.Contains("Y轴已回原点", viewModel, StringComparison.Ordinal);
        Assert.Contains("Z轴已回原点", viewModel, StringComparison.Ordinal);
        Assert.Contains("ManualControl.XHomeCompletedText", xaml, StringComparison.Ordinal);
        Assert.Contains("ManualControl.YHomeCompletedText", xaml, StringComparison.Ordinal);
        Assert.Contains("ManualControl.ZHomeCompletedText", xaml, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));
}
