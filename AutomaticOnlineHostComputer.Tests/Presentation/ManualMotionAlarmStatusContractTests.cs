namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class ManualMotionAlarmStatusContractTests
{
    [Fact]
    public void Manual_control_alarm_display_is_read_only_and_aggregates_three_alarm_words()
    {
        string viewModel = Read("Presentation", "ViewModels", "Home", "CraneManualControlViewModel.cs");
        string xaml = Read("Views", "Motion", "Controls", "ManualMotionControlPanel.xaml");

        Assert.Contains("public bool? IsAlarmFeedback", viewModel, StringComparison.Ordinal);
        Assert.Contains("public short? ServoAlarmValue", viewModel, StringComparison.Ordinal);
        Assert.Contains("public short? PlcServoAlarmValue", viewModel, StringComparison.Ordinal);
        Assert.Contains("public short? PlcAlarmValue", viewModel, StringComparison.Ordinal);
        Assert.Contains("status.ServoAlarm != 0 || status.PlcServoAlarm != 0 || status.PlcAlarm != 0", viewModel, StringComparison.Ordinal);
        Assert.Contains("D5011={ServoAlarmValue}", viewModel, StringComparison.Ordinal);
        Assert.Contains("D5012={PlcServoAlarmValue}", viewModel, StringComparison.Ordinal);
        Assert.Contains("D5013={PlcAlarmValue}", viewModel, StringComparison.Ordinal);

        Assert.Contains("ManualControl.AlarmFeedbackText", xaml, StringComparison.Ordinal);
        Assert.Contains("ManualControl.IsAlarmFeedback", xaml, StringComparison.Ordinal);
        Assert.Contains("#FEE2E2", xaml, StringComparison.Ordinal);
        Assert.Contains("#DCFCE7", xaml, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));
}
