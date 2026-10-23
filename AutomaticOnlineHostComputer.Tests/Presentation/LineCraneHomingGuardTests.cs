using AutomaticOnlineHostComputer.Communication.DeviceServices;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

/// <summary>注入当次读取结果验证启动门禁，无PLC连接、运动或业务状态操作。</summary>
public sealed class LineCraneHomingGuardTests
{
    [Theory]
    [InlineData(1, 1, 2)]
    [InlineData(2, 3, 4)]
    public async Task Correct_two_cranes_with_six_ones_allow_start_even_at_zero_display_positions(int line, int front, int rear)
    {
        var reads = new List<int>();
        var result = await LineCraneHomingGuard.CheckAsync(line, (no, _) =>
        {
            reads.Add(no);
            return Task.FromResult<CraneStatus?>(Ready()); // XYZ显示坐标保持默认0。
        });
        Assert.Equal(new[] { front, rear }, reads);
        Assert.True(result.CanStart);
        Assert.All(result.Cranes, x => Assert.True(x.IsReady));
    }

    public static IEnumerable<object[]> NonOneFlags()
    {
        foreach (int line in new[] { 1, 2 })
        foreach (int offset in new[] { 0, 1 })
        foreach (int axis in new[] { 0, 1, 2 })
        foreach (short value in new short[] { 0, 2, -1 })
            yield return new object[] { line, (line - 1) * 2 + 1 + offset, axis, value };
    }

    [Theory]
    [MemberData(nameof(NonOneFlags))]
    public async Task Any_of_six_flags_not_exactly_one_blocks_start(int line, int failedCrane, int axis, short value)
    {
        var result = await LineCraneHomingGuard.CheckAsync(line, (no, _) =>
        {
            var status = Ready();
            status.XPos = 1000; status.YPos = 2000; status.ZPos = 100; // 坐标非零也不能绕过标志检查。
            if (no == failedCrane)
            {
                if (axis == 0) status.XHomeCompleted = value;
                else if (axis == 1) status.YHomeCompleted = value;
                else status.ZHomeCompleted = value;
            }
            return Task.FromResult<CraneStatus?>(status);
        });
        Assert.False(result.CanStart);
        var failure = Assert.Single(result.Cranes.Where(x => !x.IsReady));
        Assert.Equal(failedCrane, failure.CraneNo);
        string report = result.BuildFailureMessage();
        Assert.Contains($"{line}号线不能启动/恢复", report);
        Assert.Contains($"D{5006 + axis}={value}", report);
        Assert.Contains("必须等于1", report);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_failed_front_read_blocks_start_and_still_checks_rear(bool throws)
    {
        var reads = new List<int>();
        var result = await LineCraneHomingGuard.CheckAsync(1, (no, _) =>
        {
            reads.Add(no);
            if (no == 1)
            {
                if (throws) throw new IOException("设备连接失败");
                return Task.FromResult<CraneStatus?>(null);
            }
            return Task.FromResult<CraneStatus?>(Ready());
        });
        Assert.Equal(new[] { 1, 2 }, reads);
        Assert.False(result.CanStart);
        Assert.True(result.Cranes[1].IsReady);
        Assert.Null(result.Cranes[0].X);
        Assert.Contains("读取失败", result.BuildFailureMessage());
        Assert.Contains("D5006=未知", result.BuildFailureMessage());
        Assert.DoesNotContain("D5006=0", result.BuildFailureMessage());
    }

    [Fact]
    public async Task Both_cranes_start_reading_before_either_is_required_to_finish()
    {
        int started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await LineCraneHomingGuard.CheckAsync(2, async (_, ct) =>
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.SetResult();
            await bothStarted.Task.WaitAsync(ct);
            return Ready();
        });
        Assert.True(result.CanStart);
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task One_crane_timeout_is_reported_without_losing_the_other_result()
    {
        var result = await LineCraneHomingGuard.CheckAsync(1, async (no, ct) =>
        {
            if (no == 1) await Task.Delay(Timeout.Infinite, ct);
            return Ready();
        }, timeoutMs: 50);
        Assert.False(result.CanStart);
        Assert.Contains("超时", result.Cranes[0].Error);
        Assert.True(result.Cranes[1].IsReady);
    }

    [Fact]
    public async Task Each_click_reads_again_and_the_old_result_is_an_immutable_snapshot()
    {
        var sharedStatus = Ready();
        int reads = 0;
        Task<CraneStatus?> Read(int _, CancellationToken ct) { reads++; return Task.FromResult<CraneStatus?>(sharedStatus); }
        var first = await LineCraneHomingGuard.CheckAsync(1, Read);
        sharedStatus.YHomeCompleted = 0;
        var second = await LineCraneHomingGuard.CheckAsync(1, Read);
        Assert.True(first.CanStart);
        Assert.False(second.CanStart);
        Assert.Equal(4, reads);
    }

    [Fact]
    public async Task Failure_report_includes_both_roles_and_all_three_addresses_for_each()
    {
        var result = await LineCraneHomingGuard.CheckAsync(2, (no, _) => Task.FromResult<CraneStatus?>(new()
        {
            XHomeCompleted = (short)(no == 3 ? 0 : 1),
            YHomeCompleted = (short)(no == 4 ? 2 : 1), ZHomeCompleted = 1
        }));
        string report = result.BuildFailureMessage();
        Assert.Contains("2号线前天车（3号天车）", report);
        Assert.Contains("2号线后天车（4号天车）", report);
        foreach (string address in new[] { "D5006", "D5007", "D5008" })
            Assert.Equal(2, report.Split(address + "=").Length - 1);
        Assert.Contains("D5006=0", report);
        Assert.Contains("D5007=2", report);
    }

    [Fact]
    public async Task External_cancellation_never_returns_permission_to_start()
    {
        using var cts = new CancellationTokenSource();
        Task<LineHomingResult> check = LineCraneHomingGuard.CheckAsync(1, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ready();
        }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Unsupported_line_is_rejected_before_device_access(int line)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LineCraneHomingGuard.CheckAsync(line,
            (_, _) => throw new Exception("不应访问设备")));
    }

    private static CraneStatus Ready() => new() { XHomeCompleted = 1, YHomeCompleted = 1, ZHomeCompleted = 1 };
}
