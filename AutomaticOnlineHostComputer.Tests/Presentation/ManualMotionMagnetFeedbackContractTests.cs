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

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));
}
