namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class AttentionMonitorViewModelContractTests
{
    [Fact]
    public void Selected_detail_notifications_use_the_single_inherited_observable_event()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "Presentation", "ViewModels", "Home", "AttentionMonitorViewModel.cs"));

        Assert.Contains("OnPropertyChanged(nameof(SelectedDetailTitle));", source, StringComparison.Ordinal);
        Assert.Contains("OnPropertyChanged(nameof(SelectedDetailText));", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public event PropertyChangedEventHandler? PropertyChanged;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private void OnPropertyChanged([CallerMemberName]", source, StringComparison.Ordinal);
    }
}
