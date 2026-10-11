using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.Alarms;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class SafetyAlarmDialogViewModelTests
{
    [Fact]
    public async Task Late_cleanup_updates_popup_but_refresh_cannot_rewrite_failure()
    {
        var action = new FlowActionContext("view-test", "2号线", "4号天车", "P1", "ST606", "ST021", "ST606");
        action.BeginStep(FlowActionStep.MagnetOffSent, new(10, 20, 30), "退磁确认");
        using var context = FlowAlarmPresentationContext.Begin(action,
            new WorkpieceCache { PlateNo = "P1", Sequence = "S2", Length = 1500, Diameter = 240 }, "2号线", 4, "斜床下料");
        FlowAlarmPresentationContext.RecordFailure(new Exception("X7反馈超时"));
        using var model = new SafetyAlarmDialogViewModel(context,
            (_, _) => Task.FromResult(CraneAlarmSnapshot.Unknown("模拟读取失败")), update => update());
        string historical = model.Sections.Historical;
        string failure = model.Sections.Failure;
        action.BeginStep(FlowActionStep.ReturnSafe, new(0, 0, 0), "收尾回升");
        FlowAlarmPresentationContext.RecordCleanup("通信中断，设备清零失败", "Idle", "未通知下游");
        await model.RefreshAsync();
        Assert.Equal(historical, model.Sections.Historical);
        Assert.Equal(failure, model.Sections.Failure);
        Assert.Contains("设备清零失败", model.Sections.Cleanup);
        Assert.Contains("模拟读取失败", model.Sections.Live);
        Assert.Contains("版号：P1", model.CopyText);
        Assert.Contains("序号：S2", model.CopyText);
        context.ObserveFinalState(() => "锁已释放", () => "下游缓存保留");
        context.Dispose();
        Assert.Contains("本动作收尾已结束", model.Sections.Cleanup);
        Assert.Contains("下游缓存保留", model.Sections.Cleanup);
    }

    [Fact]
    public async Task Close_cancels_sampling_and_does_not_change_business_action()
    {
        var action = new FlowActionContext("close-test", "研磨", "5号天车", "", "ST709", "ST701", "FIFO");
        using var context = FlowAlarmPresentationContext.Begin(action, null, "研磨", 5, "研磨机上料");
        var before = action.Snapshot();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new SafetyAlarmDialogViewModel(context, async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(true); throw; }
            return CraneAlarmSnapshot.Unknown("未读取");
        }, update => update());
        var refresh = model.RefreshAsync();
        Assert.False(model.CanRefresh);
        model.Dispose();
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await cancelled.Task);
        FlowAlarmPresentationContext.RecordCleanup("失败", "Idle", "未改动");
        Assert.Equal(before, action.Snapshot() with { HeldLocks = before.HeldLocks });
        Assert.False(model.CanRefresh);
    }

    [Fact]
    public void Source_freeze_and_line2_locks_are_reported_from_real_readonly_state()
    {
        var ledger = new TransferRackWorkpieceLedger();
        var wp = new WorkpieceCache { PlateNo = "P", Sequence = "1" };
        ledger.Enqueue("ST016", wp, out _);
        Assert.True(ledger.TryReserve("ST016", "action", out _, out _));
        Assert.True(ledger.HoldForManualResolution("ST016", "action", "位置未知"));
        using var lease = new EmergencyActionLease(1, "下料", CancellationToken.None);
        lease.MarkHeld("M818"); lease.MarkHeld("M821");
        Assert.Contains("冻结待人工确认", FlowAlarmPresentationContext.DescribeRackCache(ledger, "ST016", "action", wp));
        Assert.Contains("M821", FlowAlarmPresentationContext.DescribeLocks(lease));
        Assert.True(ledger.IsHeldForManualResolution("ST016", out _, out _));
        Assert.True(lease.IsHeld("M818"));
    }
}
