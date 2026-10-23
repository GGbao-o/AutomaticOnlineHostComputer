namespace AutomaticOnlineHostComputer.Tests.Presentation;

/// <summary>检查新启动门禁接入与四个引擎旧全零保护删除，保留研磨独立规则。</summary>
public sealed class LineHomingEntryContractTests
{
    [Theory]
    [InlineData("Line1FrontFlowEngine.cs")]
    [InlineData("Line2FrontFlowEngine.cs")]
    [InlineData("Line1RearFlowEngine.cs")]
    [InlineData("Line2RearFlowEngine.cs")]
    public void Line_engine_no_longer_blocks_dispatch_due_to_all_zero_display_positions(string file)
    {
        string source = Read("Service", "FlowEngine", file);
        Assert.DoesNotContain("CraneZeroPositionGuard", source);
        Assert.DoesNotContain("OnCraneZeroPositionDetected", source);
        Assert.DoesNotContain("EnsureRearCranePositionReadyAsync", source);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Homing_check_precedes_every_start_resume_and_running_flag(int line)
    {
        string source = Read("Presentation", "ViewModels", "Home", "HomeViewModel.cs");
        int start = source.IndexOf($"private async Task Line{line}ToggleAsync()", StringComparison.Ordinal);
        int gate = source.IndexOf($"EnsureLineCranesHomedForStartAsync({line})", start, StringComparison.Ordinal);
        int resume = source.IndexOf($"_line{line}Engine.Resume()", start, StringComparison.Ordinal);
        int engineStart = source.IndexOf($"_line{line}Engine.Start()", start, StringComparison.Ordinal);
        int running = source.IndexOf($"IsLine{line}Running = true;", start, StringComparison.Ordinal);
        Assert.True(gate > start && resume > gate && engineStart > gate && running > gate);
        Assert.Contains("return;", source[gate..resume]);
    }

    [Fact]
    public void Grinding_keeps_its_original_zero_position_guard()
    {
        Assert.Contains("CraneZeroPositionGuard.CheckAsync", Read("Service", "FlowEngine", "GrindingFlowEngine.cs"));
        string source = Read("Presentation", "ViewModels", "Home", "HomeViewModel.cs");
        int start = source.IndexOf("private async Task GrindingToggleAsync()", StringComparison.Ordinal);
        int gate = source.IndexOf("EnsureCranePositionsReadyForStartAsync", start, StringComparison.Ordinal);
        int resume = source.IndexOf("_grindingEngine.Resume()", start, StringComparison.Ordinal);
        Assert.True(gate > start && resume > gate);
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(path)));
}
