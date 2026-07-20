namespace AutomaticOnlineHostComputer.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Repository_root_contains_main_project()
    {
        string root = RepositoryRoot.Find();
        Assert.True(File.Exists(Path.Combine(root, "AutomaticOnlineHostComputer.csproj")));
    }
}
