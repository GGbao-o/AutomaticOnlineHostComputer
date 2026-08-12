namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class MainWindowCloseConfirmationContractTests
{
    [Fact]
    public void Main_window_closing_requires_an_explicit_yes_confirmation()
    {
        string window = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "MainWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "MainWindow.xaml.cs"));

        Assert.Contains("Closing=\"Window_Closing\"", window, StringComparison.Ordinal);
        Assert.Contains("MessageBoxButton.YesNo", codeBehind, StringComparison.Ordinal);
        Assert.Contains("MessageBoxResult.No", codeBehind, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = result != MessageBoxResult.Yes", codeBehind, StringComparison.Ordinal);
    }
}
