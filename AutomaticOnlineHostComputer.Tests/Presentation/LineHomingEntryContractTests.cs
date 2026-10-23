namespace AutomaticOnlineHostComputer.Tests.Presentation;

/// <summary>检查1/2号线及研磨启动门禁接入，并验证各引擎删除旧显示坐标全零保护。</summary>
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
    public void Grinding_homing_check_precedes_start_resume_and_uses_the_configured_crane()
    {
        string source = Read("Presentation", "ViewModels", "Home", "HomeViewModel.cs");
        int start = source.IndexOf("private async Task GrindingToggleAsync()", StringComparison.Ordinal);
        int gate = source.IndexOf("EnsureGrindingCraneHomedForStartAsync()", start, StringComparison.Ordinal);
        int resume = source.IndexOf("_grindingEngine.Resume()", start, StringComparison.Ordinal);
        int engineStart = source.IndexOf("_grindingEngine.Start()", start, StringComparison.Ordinal);
        int running = source.IndexOf("IsGrindingRunning = true;", start, StringComparison.Ordinal);
        Assert.True(gate > start && resume > gate && engineStart > gate && running > gate);
        Assert.Contains("return;", source[gate..resume]);
        Assert.Contains("CheckGrindingAsync(_cfg.Grinding.CraneNo", source);
        Assert.DoesNotContain("EnsureCranePositionsReadyForStartAsync", source);
        Assert.DoesNotContain("HandleCraneZeroPositionAlarm", source);
    }

    [Fact]
    public void Grinding_dispatch_no_longer_checks_zero_coordinates_and_retains_its_other_safety_pause()
    {
        string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");
        Assert.DoesNotContain("CraneZeroPositionGuard", source);
        Assert.DoesNotContain("OnCraneZeroPositionDetected", source);
        Assert.DoesNotContain("EnsureGrindingCranePositionReadyAsync", source);
        // 此公共软暂停方法仍被严重安全回调使用，不能随着全零门禁一起删除。
        Assert.Contains("public void PauseForCraneZeroPosition()", source);
        Assert.Contains("TryDequeueCacheIfHeadMatches", source);
        Assert.Contains("_grindingDispatchGate.Release()", source);
    }

    [Fact]
    public void Grinding_failed_start_gate_is_read_only_and_does_not_pause_or_discard()
    {
        string source = Read("Presentation", "ViewModels", "Home", "HomeViewModel.cs");
        int start = source.IndexOf("private async Task<bool> EnsureGrindingCraneHomedForStartAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = source.IndexOf("private bool EnsureNoPendingManualActionsForLine", start, StringComparison.Ordinal);
        string gate = source[start..end];
        Assert.Contains(".ReadStatusAsync(ct)", gate);
        Assert.Contains("if (result.IsReady) return true;", gate);
        Assert.Contains("MessageBox.Show(", gate);
        Assert.Contains("return false;", gate);
        foreach (string sideEffect in new[] { ".Start(", ".Resume(", ".Pause", ".WriteAsync(", ".WriteCoil", "Discard", "ClearCache", "PendingWorkpiece" })
            Assert.DoesNotContain(sideEffect, gate);
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(path)));
}
