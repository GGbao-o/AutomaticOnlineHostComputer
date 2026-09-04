namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class LineStartLedgerGateContractTests
{
    [Fact]
    public void Line_start_and_resume_keep_crane_zero_guard_but_do_not_consult_manual_action_ledger()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "Presentation", "ViewModels", "Home", "HomeViewModel.cs"));

        int line1Start = source.IndexOf("private async Task Line1ToggleAsync()", StringComparison.Ordinal);
        int line2Start = source.IndexOf("private async Task Line2ToggleAsync()", StringComparison.Ordinal);
        int coordinateGuard = source.IndexOf("private async Task<bool> EnsureCranePositionsReadyForStartAsync", StringComparison.Ordinal);
        Assert.True(line1Start >= 0 && line2Start > line1Start && coordinateGuard > line2Start);

        string line1 = source[line1Start..line2Start];
        string line2 = source[line2Start..coordinateGuard];

        Assert.Contains("EnsureCranePositionsReadyForStartAsync", line1, StringComparison.Ordinal);
        Assert.Contains("EnsureCranePositionsReadyForStartAsync", line2, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureNoPendingManualActionsForLine", line1, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureNoPendingManualActionsForLine", line2, StringComparison.Ordinal);
    }
}
