using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Tests.Communication;

/// <summary>验证手动JOG的OFF收尾；注入IO，不移动真实设备。</summary>
public sealed class ManualJogPulseTests
{
    [Theory]
    [InlineData(70)] [InlineData(71)] [InlineData(72)]
    [InlineData(73)] [InlineData(74)] [InlineData(75)]
    public async Task Every_coil_runs_on_delay_off_then_confirmed_read(int coil)
    {
        var calls = new List<string>();
        await ManualJogPulse.RunAsync(new(
            (on, _) => { calls.Add(on ? "ON" : "OFF"); return Task.CompletedTask; },
            _ => { calls.Add("READ"); return Task.FromResult(false); },
            (ms, _) => { calls.Add($"DELAY:{ms}"); return Task.CompletedTask; }), coil, 1000, CancellationToken.None);
        Assert.Equal(new[] { "ON", "DELAY:1000", "OFF", "READ" }, calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public async Task Invalid_duration_is_rejected_before_any_io(int duration)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ManualJogPulse.RunAsync(NoIo(), 70, duration, CancellationToken.None));
    }

    [Theory]
    [InlineData(69)] [InlineData(76)] [InlineData(-1)]
    public async Task Non_jog_coil_is_rejected_before_any_io(int coil)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ManualJogPulse.RunAsync(NoIo(), coil, 1000, CancellationToken.None));
    }

    [Fact]
    public async Task On_response_failure_still_writes_off_and_preserves_original_error()
    {
        int onCount = 0, offCount = 0, reads = 0;
        var failure = new IOException("ON可能已到PLC，但响应失败");
        var error = await Assert.ThrowsAsync<IOException>(() => ManualJogPulse.RunAsync(new(
            (on, _) => { if (on) { onCount++; throw failure; } offCount++; return Task.CompletedTask; },
            _ => { reads++; return Task.FromResult(false); },
            (_, _) => throw new Exception("ON未确认不能开始正常保持")), 70, 1000, CancellationToken.None));
        Assert.Same(failure, error);
        Assert.Equal(1, onCount);
        Assert.Equal(1, offCount);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Wait_cancellation_does_not_cancel_the_independent_off_and_read()
    {
        using var cts = new CancellationTokenSource();
        bool off = false, read = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ManualJogPulse.RunAsync(new(
            (on, token) =>
            {
                if (!on) { Assert.NotEqual(cts.Token, token); Assert.False(token.IsCancellationRequested); off = true; }
                return Task.CompletedTask;
            },
            token => { Assert.False(token.IsCancellationRequested); read = true; return Task.FromResult(false); },
            (_, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }), 72, 1000, cts.Token));
        Assert.True(off);
        Assert.True(read);
    }

    [Fact]
    public async Task Cancellation_during_on_attempt_also_resets_with_a_fresh_token()
    {
        using var cts = new CancellationTokenSource();
        bool off = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ManualJogPulse.RunAsync(new(
            (on, token) =>
            {
                if (on) { cts.Cancel(); token.ThrowIfCancellationRequested(); }
                else { Assert.False(token.IsCancellationRequested); off = true; }
                return Task.CompletedTask;
            }, _ => Task.FromResult(false), (_, _) => Task.CompletedTask), 70, 1000, cts.Token));
        Assert.True(off);
    }

    [Fact]
    public async Task Wait_exception_also_runs_the_reset()
    {
        var failure = new InvalidOperationException("等待阶段异常");
        int offCount = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ManualJogPulse.RunAsync(new(
            (on, _) => { if (!on) offCount++; return Task.CompletedTask; }, _ => Task.FromResult(false),
            (_, _) => throw failure), 74, 1000, CancellationToken.None));
        Assert.Same(failure, error);
        Assert.Equal(1, offCount);
    }

    [Fact]
    public async Task Off_failures_are_retried_without_repeating_on()
    {
        int onCount = 0, offCount = 0;
        await ManualJogPulse.RunAsync(new(
            (on, _) =>
            {
                if (on) onCount++;
                else if (++offCount < 3) throw new IOException("OFF暂时失败");
                return Task.CompletedTask;
            }, _ => Task.FromResult(false), (_, _) => Task.CompletedTask), 70, 1000, CancellationToken.None);
        Assert.Equal(1, onCount);
        Assert.Equal(3, offCount);
    }

    [Fact]
    public async Task Readback_one_is_not_accepted_as_reset_success()
    {
        int offCount = 0, reads = 0;
        await ManualJogPulse.RunAsync(new(
            (on, _) => { if (!on) offCount++; return Task.CompletedTask; },
            _ => Task.FromResult(++reads < 3), (_, _) => Task.CompletedTask), 71, 1000, CancellationToken.None);
        Assert.Equal(3, offCount);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task Repeated_unknown_readback_reports_unconfirmed_reset()
    {
        int offCount = 0;
        var error = await Assert.ThrowsAsync<ManualJogResetException>(() => ManualJogPulse.RunAsync(new(
            (on, _) => { if (!on) offCount++; return Task.CompletedTask; },
            _ => throw new IOException("回读失败"), (_, _) => Task.CompletedTask), 75, 1000, CancellationToken.None));
        Assert.Equal(3, offCount);
        Assert.Contains("M75", error.Message);
        Assert.Contains("未确认", error.Message);
    }

    [Fact]
    public async Task Reset_failure_keeps_the_original_on_failure_as_evidence()
    {
        var original = new IOException("原ON异常");
        var error = await Assert.ThrowsAsync<ManualJogResetException>(() => ManualJogPulse.RunAsync(new(
            (on, _) => throw (on ? original : new IOException("OFF失败")),
            _ => Task.FromResult(false), (_, _) => Task.CompletedTask), 70, 1000, CancellationToken.None));
        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Contains(original, aggregate.InnerExceptions);
    }

    [Fact]
    public async Task Retry_reset_has_no_on_or_holding_delay()
    {
        int offCount = 0;
        await ManualJogPulse.ResetAsync(new(
            (on, _) => { Assert.False(on); offCount++; return Task.CompletedTask; },
            _ => Task.FromResult(false), (_, _) => throw new Exception("复位不保持运动")), 73);
        Assert.Equal(1, offCount);
    }

    [Fact]
    public async Task Duration_longer_than_existing_five_second_timeout_is_kept()
    {
        int observed = 0;
        await ManualJogPulse.RunAsync(new((_, _) => Task.CompletedTask, _ => Task.FromResult(false),
            (ms, _) => { observed = ms; return Task.CompletedTask; }), 70, 12000, CancellationToken.None);
        Assert.Equal(12000, observed);
    }

    [Fact]
    public async Task Cancellation_before_any_on_does_not_start_io()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ManualJogPulse.RunAsync(NoIo(), 70, 1000, cts.Token));
    }

    private static ManualJogOperations NoIo() => new(
        (_, _) => throw new Exception("不应写入"), _ => throw new Exception("不应读取"), (_, _) => throw new Exception("不应等待"));
}
