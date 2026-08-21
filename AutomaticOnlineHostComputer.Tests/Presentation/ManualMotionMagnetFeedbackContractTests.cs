namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class ManualMotionMagnetFeedbackContractTests
{
    [Fact]
    public void Manual_motion_refresh_reads_actual_device_feedbacks_and_keeps_unknown_distinct()
    {
        string source = Read("Presentation", "ViewModels", "Home", "CraneManualControlViewModel.cs");

        Assert.Contains("CraneAddress.D_X6_MagnetizeOk", source, StringComparison.Ordinal);
        Assert.Contains("CraneAddress.D_X7_DemagnetizeOk", source, StringComparison.Ordinal);
        Assert.Contains("CraneAddress.D_X11_HasPlate", source, StringComparison.Ordinal);
        Assert.Contains("CraneAddress.D_X2_MagnetLimit", source, StringComparison.Ordinal);
        Assert.Contains("public bool? IsMagnetizeFeedback", source, StringComparison.Ordinal);
        Assert.Contains("public bool? IsDemagnetizeFeedback", source, StringComparison.Ordinal);
        Assert.Contains("public bool? IsPlateFeedback", source, StringComparison.Ordinal);
        Assert.Contains("public bool? IsMagnetPressureFeedback", source, StringComparison.Ordinal);
        Assert.Contains("true => \"有板（X11=1）\"", source, StringComparison.Ordinal);
        Assert.Contains("false => \"无板（X11=0）\"", source, StringComparison.Ordinal);
        Assert.Contains("true => \"下压触发（X2=1）\"", source, StringComparison.Ordinal);
        Assert.Contains("false => \"下压未触发（X2=0）\"", source, StringComparison.Ordinal);
        int reader = source.IndexOf("private async Task<bool?> TryReadXFeedbackAsync", StringComparison.Ordinal);
        int nextMethod = source.IndexOf("    /// <summary>", reader, StringComparison.Ordinal);
        Assert.True(reader > 0 && nextMethod > reader, "磁铁反馈读取器边界无效");
        string readerMethod = source[reader..nextMethod];
        Assert.Contains("ReadXBitAsync(address, ct)", readerMethod, StringComparison.Ordinal);
        Assert.Contains("return null;", readerMethod, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(deviceAtRead, SelectedDevice)", source, StringComparison.Ordinal);
        Assert.Contains("IsPlateFeedback = plateFeedback;", source, StringComparison.Ordinal);
        Assert.Contains("IsMagnetPressureFeedback = magnetPressureFeedback;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Motion_panel_shows_independent_actual_feedbacks_with_refresh()
    {
        string source = Read("Views", "Motion", "Controls", "ManualMotionControlPanel.xaml");

        Assert.Contains("设备实际反馈", source, StringComparison.Ordinal);
        Assert.Contains("ManualControl.MagnetizeFeedbackText", source, StringComparison.Ordinal);
        Assert.Contains("ManualControl.DemagnetizeFeedbackText", source, StringComparison.Ordinal);
        Assert.Contains("ManualControl.PlateFeedbackText", source, StringComparison.Ordinal);
        Assert.Contains("ManualControl.MagnetPressureFeedbackText", source, StringComparison.Ordinal);
        Assert.Contains("#FEE2E2", source, StringComparison.Ordinal);
        Assert.Contains("刷新设备状态", source, StringComparison.Ordinal);
        Assert.Contains("ManualControl.RefreshTargetCommand", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Drain_tray_controls_and_door_feedbacks_are_crane_only()
    {
        string addresses = Read("Communication", "DeviceAddresses", "CraneAddress.cs");
        string viewModel = Read("Presentation", "ViewModels", "Home", "CraneManualControlViewModel.cs");
        string xaml = Read("Views", "Motion", "Controls", "ManualMotionControlPanel.xaml");

        Assert.Contains("D_X0_DrainOpenOk    = 63488", addresses, StringComparison.Ordinal);
        Assert.Contains("D_X1_DrainCloseOk   = 63489", addresses, StringComparison.Ordinal);

        Assert.Contains("public bool IsCraneOnly", viewModel, StringComparison.Ordinal);
        Assert.Contains("public bool? IsDrainOpenFeedback", viewModel, StringComparison.Ordinal);
        Assert.Contains("public bool? IsDrainClosedFeedback", viewModel, StringComparison.Ordinal);
        Assert.Contains("CraneAddress.D_X0_DrainOpenOk", viewModel, StringComparison.Ordinal);
        Assert.Contains("CraneAddress.D_X1_DrainCloseOk", viewModel, StringComparison.Ordinal);
        Assert.Contains("bool isCraneAtRead =", viewModel, StringComparison.Ordinal);
        Assert.Contains("if (isCraneAtRead)", viewModel, StringComparison.Ordinal);
        Assert.Contains("true => \"门开到位（X0=1）\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("false => \"门开未到位（X0=0）\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("true => \"门关到位（X1=1）\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("false => \"门关未到位（X1=0）\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsDrainOpenFeedback = drainOpenFeedback;", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsDrainClosedFeedback = drainClosedFeedback;", viewModel, StringComparison.Ordinal);

        Assert.Contains("ManualControl.DrainOpenFeedbackText", xaml, StringComparison.Ordinal);
        Assert.Contains("ManualControl.DrainClosedFeedbackText", xaml, StringComparison.Ordinal);
        Assert.Contains("ManualControl.DrainOpenCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ManualControl.DrainCloseCommand", xaml, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(xaml, "ManualControl.IsCraneOnly") >= 4,
            "两个接液盘按钮和两个门反馈标签都必须绑定天车专属可见性");
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int start = 0;
        while ((start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));
}
