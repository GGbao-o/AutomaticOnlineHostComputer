using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.OperationalEvents;
using AutomaticOnlineHostComputer.Infrastructure.Config;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 用可注入IO验证取板物理时序：1/2/3位置成功、三处无板、反馈未知、充磁后确认失败、
/// 实际Z0及补偿上界、下压阻止和取消路径。断言充磁前仍属于来源，不能提前产生持件证据。
/// 假设备不连接PLC；这些测试不能证明现场检测距离、真实运动超调或磁铁吸附可靠性。
/// </summary>
public class PickupProbeHelperTests
{
    // 配置次数包括首次位置；非默认步长必须同时改变运动目标、循环次数和实际Z上界。
    [Theory]
    [InlineData(4, 2)]
    [InlineData(3, 8)]
    public async Task Configured_positions_and_step_drive_the_entire_probe(int count, int step)
    {
        var fake = new PickupFake(100, Enumerable.Repeat(false, count - 1).Concat(new[] { true, true }).ToArray())
            { Settings = new(count, step) };
        var result = await fake.Pick();
        Assert.Equal(Enumerable.Range(0, count).Select(n => 100 + n * step), fake.Moves);
        Assert.Equal(count, result.ProbeCount);
        Assert.Equal(100 + (count - 1) * step, result.ActualZ);
        Assert.Equal(1, fake.MagnetCalls);
    }

    [Fact]
    public async Task One_position_disables_compensation_and_fails_without_magnet()
    {
        var fake = new PickupFake(100, false) { Settings = new(1, 5) };
        var error = await Assert.ThrowsAsync<PickupFailedException>(() => fake.Pick());
        Assert.Equal(PickupFailureReason.NoPlate, error.Reason);
        Assert.Contains("1个位置", error.Message);
        Assert.Contains("0mm", error.Message);
        Assert.Equal(new[] { 100 }, fake.Moves);
        Assert.Equal(0, fake.MagnetCalls);
    }

    [Fact]
    public async Task Configured_failure_has_no_extra_position_and_reports_actual_limit()
    {
        var fake = new PickupFake(100, false, false, false, false) { Settings = new(4, 2) };
        var error = await Assert.ThrowsAsync<PickupFailedException>(() => fake.Pick());
        Assert.Equal(new[] { 100, 102, 104, 106 }, fake.Moves);
        Assert.Equal(0, fake.MagnetCalls);
        Assert.Contains("4个位置", error.Message);
        Assert.Contains("6mm", error.Message);
    }

    // 非法参数不能自动回退为另一套物理动作；必须在任何输入读取、下降及充磁之前拒绝。
    [Theory]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(3, 0)]
    [InlineData(3, -1)]
    [InlineData(3, int.MaxValue)]
    public async Task Invalid_or_overflowing_settings_fail_before_device_io(int count, int step)
    {
        var fake = new PickupFake(100, true, true) { Settings = new(count, step) };
        var error = await Assert.ThrowsAsync<PickupFailedException>(() => fake.Pick());
        Assert.Equal(PickupFailureReason.UnsafeProbe, error.Reason);
        Assert.Empty(fake.Moves);
        Assert.Equal(0, fake.InputReads);
        Assert.Equal(0, fake.MagnetCalls);
    }

    [Fact]
    public async Task One_mm_step_is_not_accepted_before_actual_motion()
    {
        var fake = new PickupFake(100, false, true, true)
            { Settings = new(2, 1), ProbePositionLagReads = 2 };
        var result = await fake.Pick();
        Assert.Equal(101, result.ActualZ);
        Assert.True(fake.ReadsSinceProbeAtSecondX11 >= 4);
    }

    [Fact]
    public async Task Editing_shared_config_during_pickup_does_not_change_its_snapshot()
    {
        var config = new MotionConfig();
        var fake = new PickupFake(100, false, false, true, true)
        {
            Settings = new(config.Grinding.PickupProbeCount, config.Grinding.PickupProbeStepMm),
            OnFirstX11 = () => { config.Grinding.PickupProbeCount = 1; config.Grinding.PickupProbeStepMm = 20; }
        };
        await fake.Pick();
        Assert.Equal(new[] { 100, 105, 110 }, fake.Moves);
        Assert.Equal(1, config.Grinding.PickupProbeCount);
        Assert.Equal(20, config.Grinding.PickupProbeStepMm);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Presence_is_probed_without_magnet_then_confirmed_after_magnet(int successAt)
    {
        var fake = new PickupFake(100, Enumerable.Repeat(false, successAt - 1).Concat(new[] { true, true }).ToArray());
        var result = await fake.Pick();
        Assert.Equal(successAt, result.ProbeCount);
        Assert.Equal(100 + (successAt - 1) * 5, result.ActualZ);
        Assert.Equal(Enumerable.Range(0, successAt).Select(n => 100 + n * 5), fake.Moves);
        Assert.Equal(1, fake.MagnetCalls);
        Assert.Equal(successAt + 1, fake.X11Reads);
        Assert.Equal(FlowWorkpieceOwnership.OnCarrier, fake.Action.Ownership);
        Assert.All(fake.OwnershipAtReads.Take(successAt), value => Assert.Equal(FlowWorkpieceOwnership.ReservedAtSource, value));
        Assert.Equal(FlowWorkpieceOwnership.ReservedAtSource, fake.OwnershipAtMagnet);
        Assert.NotEqual(EvidenceAvailability.Confirmed, fake.HoldingAtMagnet!.Availability);
        Assert.True(fake.Tracker.Snapshot().HoldingWorkpiece.Value);
    }

    [Fact]
    public async Task Three_zero_probes_fail_without_magnet_or_fourth_move()
    {
        var fake = new PickupFake(100, false, false, false);
        var error = await Assert.ThrowsAsync<PickupFailedException>(() => fake.Pick());
        Assert.Equal(PickupFailureReason.NoPlate, error.Reason);
        Assert.Equal(new[] { 100, 105, 110 }, fake.Moves);
        Assert.Equal(0, fake.MagnetCalls);
        Assert.Equal(FlowWorkpieceOwnership.ReservedAtSource, fake.Action.Ownership);
        Assert.NotEqual(EvidenceAvailability.Confirmed, fake.Tracker.Snapshot().HoldingWorkpiece.Availability);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Unknown_input_is_never_zero_and_stops_probing(int read)
    {
        var fake = new PickupFake(100, false, false, false) { FailX11Read = read };
        var error = await Assert.ThrowsAsync<PickupFailedException>(() => fake.Pick());
        Assert.Equal(PickupFailureReason.FeedbackUnknown, error.Reason);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(read, fake.Moves.Count);
        Assert.Equal(0, fake.MagnetCalls);
    }

    [Fact]
    public async Task Post_magnet_zero_is_fatal_and_does_not_restart_probing()
    {
        var fake = new PickupFake(100, true, false);
        var error = await Assert.ThrowsAsync<PickupFailedException>(() => fake.Pick());
        Assert.Equal(PickupFailureReason.PostMagnetNoPlate, error.Reason);
        Assert.Equal(new[] { 100 }, fake.Moves);
        Assert.Equal(1, fake.MagnetCalls);
        Assert.Equal(FlowWorkpieceOwnership.ReservedAtSource, fake.Action.Ownership);
    }

    [Fact]
    public async Task Post_magnet_read_failure_is_fatal_and_does_not_confirm_ownership()
    {
        var fake = new PickupFake(100, true) { FailX11Read = 2 };
        await Assert.ThrowsAsync<PickupFailedException>(() => fake.Pick());
        Assert.Single(fake.Moves);
        Assert.Equal(1, fake.MagnetCalls);
        Assert.Equal(FlowWorkpieceOwnership.ReservedAtSource, fake.Action.Ownership);
    }

    [Fact]
    public async Task Magnet_response_unknown_is_not_replayed()
    {
        var fake = new PickupFake(100, true) { FailMagnet = true };
        var error = await Assert.ThrowsAsync<PickupFailedException>(() => fake.Pick());
        Assert.Equal(PickupFailureReason.MagnetFailed, error.Reason);
        Assert.Single(fake.Moves);
        Assert.Equal(1, fake.MagnetCalls);
        Assert.Equal(FlowWorkpieceOwnership.ReservedAtSource, fake.Action.Ownership);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Release_feedback_must_be_confirmed_before_any_descent(bool x6, bool x7)
    {
        var fake = new PickupFake(100, true, true) { X6 = x6, X7 = x7 };
        var error = await Assert.ThrowsAsync<PickupFailedException>(() => fake.Pick());
        Assert.Equal(PickupFailureReason.MagnetNotReleased, error.Reason);
        Assert.Empty(fake.Moves);
        Assert.Equal(0, fake.X11Reads);
    }

    [Fact]
    public async Task Still_pressed_pressure_blocks_next_probe_even_after_normal_initial_contact()
    {
        var fake = new PickupFake(100, false) { X2 = true };
        var error = await Assert.ThrowsAsync<PickupFailedException>(() => fake.Pick());
        Assert.Equal(PickupFailureReason.UnsafeProbe, error.Reason);
        Assert.Single(fake.Moves);
        Assert.Equal(0, fake.MagnetCalls);
    }

    [Fact]
    public async Task Compensations_use_actual_stopped_Z_and_do_not_repeat_nominal_offset()
    {
        var fake = new PickupFake(110, false, false, true, true) { FirstActualZ = 97 };
        var result = await fake.Pick();
        Assert.Equal(new[] { 110, 102, 107 }, fake.Moves);
        Assert.Equal(107, result.ActualZ);
    }

    [Fact]
    public async Task Actual_overtravel_stops_and_never_magnetizes()
    {
        var fake = new PickupFake(100, false) { ProbeOvertravel = true };
        var error = await Assert.ThrowsAsync<PickupFailedException>(() => fake.Pick());
        Assert.Equal(PickupFailureReason.UnsafeProbe, error.Reason);
        Assert.Equal(1, fake.Stops);
        Assert.Equal(0, fake.MagnetCalls);
        Assert.Equal(1, fake.X11Reads);
    }

    [Fact]
    public async Task Probe_does_not_accept_global_five_mm_tolerance_without_actual_motion()
    {
        var fake = new PickupFake(100, false, true, true) { ProbePositionLagReads = 2 };
        await fake.Pick();
        Assert.True(fake.ReadsSinceProbeAtSecondX11 >= 4);
    }

    [Fact]
    public async Task Cancel_is_not_converted_to_discard_failure()
    {
        var fake = new PickupFake(100, false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.Pick(cts.Token));
        Assert.Empty(fake.Moves);
    }

    [Fact]
    public void Discard_removes_manual_resume_record_but_alarm_keeps_frozen_board_and_step()
    {
        var action = new FlowActionContext(Guid.NewGuid().ToString(), "研磨", "5号天车", "A/1", "ST709", "ST708", "FIFO");
        using var alarm = FlowAlarmPresentationContext.Begin(action, new WorkpieceCache { PlateNo = "A", Sequence = "1" }, "研磨", 5, "上料");
        action.BeginStep(FlowActionStep.MoveZDownToPick);
        action.MarkCommandSent();
        FlowAlarmPresentationContext.RecordPickupProbe("位置1：X11=0");
        FlowAlarmPresentationContext.RecordFailure(new Exception("取板失败"));
        action.PauseForManualResolution("取板失败");
        action.Discard("本板已作废");
        Assert.False(FlowActionManualRegistry.TryGet(action.OperationId, out _));
        Assert.Equal(FlowActionDisposition.Discarded, action.Disposition);
        Assert.Equal("A", alarm.Snapshot().Workpiece!.PlateNo);
        Assert.Equal(FlowActionStep.MoveZDownToPick, alarm.Snapshot().FailureAction!.Step);
        Assert.Contains("X11=0", alarm.Snapshot().AlarmMessages);
    }

    // 假设备保存运动目标、X11读取及充磁时的归属，模拟有效0、有效1或通信失败。
    // 三种结果分开，避免测试把“读不到”伪装成0；位置滞后/越界用来核对实际到位验证。
    private sealed class PickupFake
    {
        private readonly int _initial;
        private readonly Queue<bool> _values;
        private int _z, _probeTarget, _readsSinceProbe;
        public PickupFake(int initial, params bool[] values) { _initial = initial; _values = new(values); }
        public FlowActionContext Action { get; } = new(Guid.NewGuid().ToString(), "测试", "载具", "A/1", "来源", "目标", "缓存");
        public OperationalPhysicalCycleTracker Tracker { get; } = new("测试取板");
        public EvidenceValue<bool>? HoldingAtMagnet;
        public List<int> Moves { get; } = new();
        public List<FlowWorkpieceOwnership> OwnershipAtReads { get; } = new();
        public FlowWorkpieceOwnership OwnershipAtMagnet;
        public int MagnetCalls, X11Reads, Stops, FailX11Read, ProbePositionLagReads, ReadsSinceProbeAtSecondX11;
        public int? FirstActualZ;
        public bool X6, X7 = true, X2, FailMagnet, ProbeOvertravel;
        public PickupProbeSettings? Settings;
        public System.Action? OnFirstX11;
        public int InputReads;
        public Task<PickupProbeResult> Pick(CancellationToken ct = default) => PickupProbeHelper.PickAsync(new(
            token => { Moves.Add(_initial); _z = FirstActualZ ?? _initial; return Task.CompletedTask; },
            (z, token) => { Moves.Add(z); _probeTarget = z; _readsSinceProbe = 0;
                if (ProbePositionLagReads == 0) _z = ProbeOvertravel ? z + 20 : z; return Task.CompletedTask; },
            token => { if (_probeTarget != 0 && ++_readsSinceProbe > ProbePositionLagReads) _z = ProbeOvertravel ? _probeTarget + 20 : _probeTarget;
                return Task.FromResult(new FlowActionPosition(1, 2, _z)); },
            (address, token) =>
            {
                InputReads++;
                if (address == 63494) return Task.FromResult(X6);
                if (address == 63495) return Task.FromResult(X7);
                if (address == 63490) return Task.FromResult(X2);
                OwnershipAtReads.Add(Action.Ownership);
                if (++X11Reads == FailX11Read) throw new IOException("通信失败");
                if (X11Reads == 1) OnFirstX11?.Invoke();
                if (X11Reads == 2) ReadsSinceProbeAtSecondX11 = _readsSinceProbe;
                return Task.FromResult(_values.Dequeue());
            },
            token => { MagnetCalls++; OwnershipAtMagnet = Action.Ownership; HoldingAtMagnet = Tracker.Snapshot().HoldingWorkpiece;
                return FailMagnet ? Task.FromException(new IOException("充磁响应丢失")) : Task.CompletedTask; },
            token => { Stops++; return Task.CompletedTask; }, (ms, token) => Task.CompletedTask),
            _initial, 0, Action, Tracker, ct, Settings);
    }
}
