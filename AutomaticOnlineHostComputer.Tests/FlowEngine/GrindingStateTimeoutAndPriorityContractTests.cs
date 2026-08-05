namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class GrindingStateTimeoutAndPriorityContractTests
{
    [Fact]
    public void Grinding_state_timeouts_are_independently_configured_in_minutes()
    {
        string config = Read("Infrastructure", "Config", "MotionConfig.cs");
        string settings = Read("Config", "motion_settings.json");
        string engine = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");

        Assert.Contains("public int LoadingTimeoutMinutes { get; set; } = 10;", config, StringComparison.Ordinal);
        Assert.Contains("public int WaitingForUnloadTimeoutMinutes { get; set; } = 20;", config, StringComparison.Ordinal);
        Assert.Contains("public int UnloadingTimeoutMinutes { get; set; } = 10;", config, StringComparison.Ordinal);
        Assert.Contains("\"loadingTimeoutMinutes\": 10", settings, StringComparison.Ordinal);
        Assert.Contains("\"waitingForUnloadTimeoutMinutes\": 20", settings, StringComparison.Ordinal);
        Assert.Contains("\"unloadingTimeoutMinutes\": 10", settings, StringComparison.Ordinal);
        Assert.Contains("GetStateTimeout", engine, StringComparison.Ordinal);
        Assert.Contains("configuredMinutes > 0 ? configuredMinutes : defaultMinutes", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("GrindingStuckTimeoutMs", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_page_exposes_three_grinding_state_timeout_inputs()
    {
        string viewModel = Read("Presentation", "ViewModels", "Config", "ConfigPageViewModel.cs");
        string view = Read("Views", "Config", "ConfigPageView.xaml");

        Assert.Contains("public int GrindingLoadingTimeoutMinutes", viewModel, StringComparison.Ordinal);
        Assert.Contains("public int GrindingWaitingForUnloadTimeoutMinutes", viewModel, StringComparison.Ordinal);
        Assert.Contains("public int GrindingUnloadingTimeoutMinutes", viewModel, StringComparison.Ordinal);
        Assert.Contains("研磨状态超时（分钟）", view, StringComparison.Ordinal);
        Assert.Contains("GrindingLoadingTimeoutMinutes", view, StringComparison.Ordinal);
        Assert.Contains("GrindingWaitingForUnloadTimeoutMinutes", view, StringComparison.Ordinal);
        Assert.Contains("GrindingUnloadingTimeoutMinutes", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Grinding_dispatch_starts_unload_only_when_load_cannot_start()
    {
        string engine = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");

        Assert.Contains("private async Task<bool> TryDispatchLoadAsync", engine, StringComparison.Ordinal);
        Assert.Contains("bool loadDispatched = await TryDispatchLoadAsync(ct);", engine, StringComparison.Ordinal);
        Assert.Contains("if (!loadDispatched)", engine, StringComparison.Ordinal);
        int loadDispatch = engine.IndexOf("bool loadDispatched = await TryDispatchLoadAsync(ct);", StringComparison.Ordinal);
        int unloadDispatch = engine.IndexOf("if (g.State == GrinderState.WaitingForUnload)", loadDispatch, StringComparison.Ordinal);
        Assert.True(loadDispatch >= 0 && unloadDispatch > loadDispatch,
            "必须先尝试上料，再进入等待下料研磨机的派发分支");
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));
}
