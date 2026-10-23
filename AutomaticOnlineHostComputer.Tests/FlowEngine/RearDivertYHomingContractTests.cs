namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>确保新条件接在原持锁等待内，并覆盖两条线长短板的四个分流目标。</summary>
public sealed class RearDivertYHomingContractTests
{
    [Theory]
    [InlineData("Line1RearFlowEngine.cs")]
    [InlineData("Line2RearFlowEngine.cs")]
    public void Each_poll_reads_y_and_d5007_from_the_same_snapshot_and_requires_both_manipulators(string file)
    {
        string wait = WaitMethod(Read(file));
        Assert.Contains("short? m2YHome = null, m3YHome = null;", wait);
        Assert.Contains("m2Y = s?.YPos;", wait);
        Assert.Contains("m2YHome = s?.YHomeCompleted;", wait);
        Assert.Contains("m3Y = s?.YPos;", wait);
        Assert.Contains("m3YHome = s?.YHomeCompleted;", wait);
        Assert.Contains("GetOrCreateService(2).ReadStatusAsync(ct)", wait);
        Assert.Contains("GetOrCreateService(3).ReadStatusAsync(ct)", wait);
        Assert.Equal(2, wait.Split("ReadStatusAsync(ct)").Length - 1);
        Assert.Contains("ManipulatorRackPositionGate.IsClear(m2Y, m2YHome, m2Below)", wait);
        Assert.Contains("ManipulatorRackPositionGate.IsClear(m3Y, m3YHome, m3Below)", wait);
        Assert.Contains("if (m2Clear && m3Clear)", wait);
        Assert.True(wait.IndexOf("short? m2YHome", StringComparison.Ordinal) > wait.IndexOf("while (true)", StringComparison.Ordinal));
        Assert.Contains("D5007={m2HomeText}", wait);
        Assert.Contains("D5007={m3HomeText}", wait);
    }

    [Theory]
    [InlineData("Line1RearFlowEngine.cs")]
    [InlineData("Line2RearFlowEngine.cs")]
    public void Unmet_flags_keep_the_original_cancellable_hold_lock_wait_without_side_effects(string file)
    {
        string wait = WaitMethod(Read(file));
        Assert.Contains("while (true)", wait);
        Assert.Contains("ct.ThrowIfCancellationRequested();", wait);
        Assert.Contains("await Task.Delay(pollMs, ct);", wait);
        Assert.Contains("_cfg.Balancing.M2ReleaseLocksBelowY", wait);
        Assert.Contains("_cfg.Balancing.M3ReleaseLocksBelowY", wait);
        Assert.Equal(2, wait.Split("catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }").Length - 1);
        foreach (string forbidden in new[] { "TimeoutException", "LineStartWaitTimeoutMs", "_paused =", ".Release(", "AcquireAsync", "TryReleaseLock", "MagnetOnAsync", "MagnetOffAsync", "MoveAbsoluteAsync", "WriteAsync", "ClearSkew" })
            Assert.DoesNotContain(forbidden, wait);
    }

    [Theory]
    [InlineData("Line1RearFlowEngine.cs", "EnsureM817EmptyBeforePlaceAsync")]
    [InlineData("Line1RearFlowEngine.cs", "WaitForM720CanPlaceBeforePlaceAsync")]
    [InlineData("Line2RearFlowEngine.cs", "EnsureM818CanPlaceBeforePlaceAsync")]
    [InlineData("Line2RearFlowEngine.cs", "EnsureM820CanPlaceBeforePlaceAsync")]
    public void Every_destination_checks_manipulators_after_its_locked_destination_confirmation(string file, string confirm)
    {
        string source = Read(file);
        int destination = source.IndexOf($"await {confirm}(wp, ct);", StringComparison.Ordinal);
        Assert.True(destination >= 0);
        int wait = source.IndexOf("await WaitManipulatorsClearOfRackAsync(ct);", destination, StringComparison.Ordinal);
        Assert.True(wait > destination);
        string between = source[destination..wait];
        Assert.DoesNotContain(".Release(", between);
        Assert.DoesNotContain("MoveAbsoluteAsync", between);
        Assert.DoesNotContain("MagnetOffAsync", between);
        if (confirm == "WaitForM720CanPlaceBeforePlaceAsync")
            Assert.True(source.IndexOf("_st010PlacementFlags.BeginCrane2Placement()", destination, StringComparison.Ordinal) > wait);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", file));

    private static string WaitMethod(string source)
    {
        int start = source.IndexOf("private async Task WaitManipulatorsClearOfRackAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = source.IndexOf("public void Dispose()", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }
}
