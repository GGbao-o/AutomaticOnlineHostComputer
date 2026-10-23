using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

/// <summary>只验证启动前只读门禁；不连接PLC，不执行回原点、运动或工件清理。</summary>
public sealed class GrindingCraneHomingGuardTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public async Task Three_ones_allow_zero_display_positions_and_read_only_the_configured_crane(int configuredNo)
    {
        var reads = new List<int>();
        var result = await LineCraneHomingGuard.CheckGrindingAsync(configuredNo, (no, _) =>
        {
            reads.Add(no);
            return Task.FromResult<CraneStatus?>(Ready()); // XYZ保持默认0，不能据此拒绝。
        });

        Assert.Equal(new[] { configuredNo }, reads);
        Assert.Equal(configuredNo, result.CraneNo);
        Assert.Equal("研磨天车", result.Name);
        Assert.True(result.IsReady);
    }

    public static IEnumerable<object[]> NonOneFlags()
    {
        foreach (int axis in new[] { 0, 1, 2 })
        foreach (short value in new short[] { 0, 2, -1 })
            yield return new object[] { axis, value };
    }

    [Theory]
    [MemberData(nameof(NonOneFlags))]
    public async Task Each_non_one_flag_blocks_even_when_display_positions_are_nonzero(int axis, short value)
    {
        var status = Ready();
        status.XPos = 1000; status.YPos = 2000; status.ZPos = 100;
        if (axis == 0) status.XHomeCompleted = value;
        else if (axis == 1) status.YHomeCompleted = value;
        else status.ZHomeCompleted = value;
        var result = await LineCraneHomingGuard.CheckGrindingAsync(5,
            (_, _) => Task.FromResult<CraneStatus?>(status));

        Assert.False(result.IsReady);
        string report = result.BuildFailureMessage("研磨线路");
        Assert.Contains("研磨线路不能启动/恢复", report);
        Assert.Contains("研磨天车（5号天车）", report);
        Assert.Contains($"D{5006 + axis}={value}", report);
        Assert.Contains("必须等于1", report);
        foreach (string address in new[] { "D5006", "D5007", "D5008" })
            Assert.Contains(address + "=", report);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_failed_read_blocks_with_unknown_values_and_the_reason(bool throws)
    {
        var result = await LineCraneHomingGuard.CheckGrindingAsync(5, (_, _) =>
        {
            if (throws) throw new IOException("连接失败");
            return Task.FromResult<CraneStatus?>(null);
        });

        Assert.False(result.IsReady);
        string report = result.BuildFailureMessage("研磨线路");
        Assert.Contains("读取失败", report);
        Assert.Contains(throws ? "连接失败" : "未取得有效状态快照", report);
        foreach (string address in new[] { "D5006", "D5007", "D5008" })
            Assert.Contains(address + "=未知", report);
        Assert.DoesNotContain("D5006=0", report);
    }

    [Fact]
    public async Task Timeout_blocks_without_waiting_forever_for_a_read_that_ignores_cancellation()
    {
        var lateRead = new TaskCompletionSource<CraneStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await LineCraneHomingGuard.CheckGrindingAsync(5, (_, _) => lateRead.Task, timeoutMs: 50);
        lateRead.SetResult(Ready()); // 迟到的成功响应不能修改已经拒绝的本次结果。
        Assert.False(result.IsReady);
        Assert.Null(result.X);
        Assert.Contains("超时", result.Error);
    }

    [Fact]
    public async Task Every_start_attempt_reads_again_and_preserves_the_previous_snapshot()
    {
        var status = Ready();
        int reads = 0;
        Task<CraneStatus?> Read(int _, CancellationToken ct)
        {
            reads++;
            return Task.FromResult<CraneStatus?>(status);
        }
        var first = await LineCraneHomingGuard.CheckGrindingAsync(5, Read);
        status.ZHomeCompleted = 0;
        var second = await LineCraneHomingGuard.CheckGrindingAsync(5, Read);
        Assert.True(first.IsReady);
        Assert.False(second.IsReady);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task External_cancellation_cannot_become_permission_to_start()
    {
        using var cts = new CancellationTokenSource();
        var check = LineCraneHomingGuard.CheckGrindingAsync(5, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ready();
        }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    private static CraneStatus Ready() => new() { XHomeCompleted = 1, YHomeCompleted = 1, ZHomeCompleted = 1 };
}
