using AutomaticOnlineHostComputer.Service;
using Settings = AutomaticOnlineHostComputer.Infrastructure.Config.MotionConfig.RackSignalConfirmationSection;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class RackSignalConfirmationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Opposite_value_restarts_the_streak_instead_of_accumulating_successes(bool expected)
    {
        var values = new Queue<bool>(new[] { expected, expected, !expected, expected, expected, expected });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.True(await RackSignalConfirmation.CheckAsync(_ => Task.FromResult(values.Dequeue()), expected,
            "测试料架", new Settings { SampleIntervalMs = 20 }, timeout.Token, waitUntilConfirmed: true));
        Assert.Empty(values);
    }

    [Theory]
    [InlineData(1, 811, "ST105")]
    [InlineData(2, 814, "ST016")]
    public async Task Rotating_empty_positions_cannot_combine_to_authorize_placement(int line, int firstAddress, string firstStation)
    {
        int firstBit = firstAddress - 800;
        int full = 7 << firstBit;
        int FirstEmpty() => full & ~(1 << firstBit);
        var values = new Queue<int>(new[] { FirstEmpty(), full & ~(1 << (firstBit + 1)), full & ~(1 << (firstBit + 2)) });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var settings = new Settings { SampleIntervalMs = 20 };
        Task<int> Read(CancellationToken _) => Task.FromResult(values.Dequeue());
        Assert.Null(await RackSignalConfirmation.FindEmptyTransferRackAsync(Read, line, "锁后", settings, timeout.Token, singleWindow: true));
        Assert.Empty(values);

        // Both first and second stations are stable in the next window. Physical priority wins.
        int firstTwoEmpty = full & ~(3 << firstBit);
        for (int i = 0; i < settings.ConsecutiveReads; i++) values.Enqueue(firstTwoEmpty);
        Assert.Equal(firstStation, await RackSignalConfirmation.FindEmptyTransferRackAsync(Read, line, "最终选位", settings, timeout.Token, stopWhenFull: true));
        Assert.Empty(values);
    }

    [Fact]
    public void Rereading_candidate_state_never_ticks_the_counter_and_failure_clears_permission()
    {
        var confirmation = new RackSignalConfirmation();
        var settings = new Settings();
        Assert.False(confirmation.Observe("测试料架", true, true, settings));
        for (int i = 0; i < 5; i++) Assert.False(confirmation.IsConfirmed(settings));
        Assert.False(confirmation.Observe("测试料架", true, true, settings, usesExplicitDelay: true));
        Assert.True(confirmation.Observe("测试料架", true, true, settings, usesExplicitDelay: true));
        confirmation.Reset();
        Assert.False(confirmation.IsConfirmed(settings));
    }

    [Fact]
    public async Task Lock_free_precheck_starts_over_after_a_failed_sample()
    {
        int attempts = 0;
        int mask = (1 << (812 - 800)) | (1 << (813 - 800));
        Task<int> Read(CancellationToken _) =>
            ++attempts == 3 ? Task.FromException<int>(new IOException("MC63断线")) : Task.FromResult(mask);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal("ST105", await RackSignalConfirmation.FindEmptyTransferRackAsync(Read, 1, "锁外",
            new Settings { SampleIntervalMs = 20 }, timeout.Token, retryReadFailures: true));
        Assert.Equal(6, attempts);
    }

    [Fact]
    public async Task Unknown_report_and_cancellation_stop_the_action_confirmation()
    {
        var settings = new Settings { SampleIntervalMs = 20 };
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => RackSignalConfirmation.CheckAsync(_ =>
            ++attempts == 2 ? Task.FromException<bool>(new IOException("短报文")) : Task.FromResult(true),
            true, "测试料架", settings, ct.Token, waitUntilConfirmed: true));
        Assert.Equal(2, attempts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RackSignalConfirmation.CheckAsync(_ =>
        {
            ct.Cancel();
            return Task.FromResult(true);
        }, true, "测试料架", settings, ct.Token, waitUntilConfirmed: true));
    }
}
