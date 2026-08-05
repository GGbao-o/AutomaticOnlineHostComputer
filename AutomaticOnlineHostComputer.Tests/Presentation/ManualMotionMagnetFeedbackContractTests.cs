namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class ManualMotionMagnetFeedbackContractTests
{
    [Fact]
    public void Manual_motion_refresh_reads_both_actual_magnet_feedbacks_and_keeps_unknown_distinct()
    {
        string source = Read("Presentation", "ViewModels", "Home", "CraneManualControlViewModel.cs");

        Assert.Contains("CraneAddress.D_X6_MagnetizeOk", source, StringComparison.Ordinal);
        Assert.Contains("CraneAddress.D_X7_DemagnetizeOk", source, StringComparison.Ordinal);
        Assert.Contains("public bool? IsMagnetizeFeedback", source, StringComparison.Ordinal);
        Assert.Contains("public bool? IsDemagnetizeFeedback", source, StringComparison.Ordinal);
        int reader = source.IndexOf("private async Task<bool?> TryReadMagnetFeedbackAsync", StringComparison.Ordinal);
        int nextMethod = source.IndexOf("    /// <summary>", reader, StringComparison.Ordinal);
        Assert.True(reader > 0 && nextMethod > reader, "磁铁反馈读取器边界无效");
        string readerMethod = source[reader..nextMethod];
        Assert.Contains("ReadXBitAsync(address, ct)", readerMethod, StringComparison.Ordinal);
        Assert.Contains("return null;", readerMethod, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(deviceAtRead, SelectedDevice)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Motion_panel_shows_independent_charge_and_demagnetize_feedback_with_refresh()
    {
        string source = Read("Views", "Motion", "Controls", "ManualMotionControlPanel.xaml");

        Assert.Contains("磁铁实际反馈", source, StringComparison.Ordinal);
        Assert.Contains("ManualControl.MagnetizeFeedbackText", source, StringComparison.Ordinal);
        Assert.Contains("ManualControl.DemagnetizeFeedbackText", source, StringComparison.Ordinal);
        Assert.Contains("刷新磁铁状态", source, StringComparison.Ordinal);
        Assert.Contains("ManualControl.RefreshTargetCommand", source, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));
}
