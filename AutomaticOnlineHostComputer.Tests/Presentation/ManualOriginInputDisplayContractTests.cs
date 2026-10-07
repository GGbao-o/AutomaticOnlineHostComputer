namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class ManualOriginInputDisplayContractTests
{
    [Fact]
    public void Manual_refresh_exposes_three_read_only_origin_inputs()
    {
        string addresses = Read("Communication", "DeviceAddresses", "CraneAddress.cs");
        string viewModel = Read("Presentation", "ViewModels", "Home", "CraneManualControlViewModel.cs");
        string view = Read("Views", "Motion", "Controls", "ManualMotionControlPanel.xaml");

        Assert.Contains("D_X3_XHome          = 63491", addresses, StringComparison.Ordinal);
        Assert.Contains("D_X4_YHome          = 63492", addresses, StringComparison.Ordinal);
        Assert.Contains("D_X5_ZHome          = 63493", addresses, StringComparison.Ordinal);
        Assert.Contains("IsXOriginInput", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsYOriginInput", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsZOriginInput", viewModel, StringComparison.Ordinal);
        Assert.Contains("CraneAddress.D_X3_XHome", viewModel, StringComparison.Ordinal);
        Assert.Contains("CraneAddress.D_X4_YHome", viewModel, StringComparison.Ordinal);
        Assert.Contains("CraneAddress.D_X5_ZHome", viewModel, StringComparison.Ordinal);
        Assert.Contains("ManualControl.XOriginInputText", view, StringComparison.Ordinal);
        Assert.Contains("ManualControl.YOriginInputText", view, StringComparison.Ordinal);
        Assert.Contains("ManualControl.ZOriginInputText", view, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepositoryRoot.Find() }.Concat(parts).ToArray()));
}
