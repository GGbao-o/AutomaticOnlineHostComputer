using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.Alarms;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class FrontCraneAlarmPresentationTests
{
    [Theory]
    [InlineData("1号线", 1, "ST713", "ST107", "ST105")]
    [InlineData("2号线", 3, "ST714", "ST502", "ST016")]
    public async Task Marker_pickup_freezes_actual_phase_source_without_changing_business_action(
        string scope, int crane, string fork, string marker, string rack)
    {
        var action = Action(fork);
        action.BeginStep(FlowActionStep.MagnetOnSent, new(10, 20, 30), "打号机充磁取回");
        var original = action.Snapshot();
        using var context = FlowAlarmPresentationContext.BeginFront(action, Workpiece(), scope, crane);
        FlowAlarmPresentationContext.RecordTransferPhase(marker, "中转架（尚未选择）", "打号机取回");
        FlowAlarmPresentationContext.RecordFailure(new Exception("充磁反馈失败"));
        var failed = context.Snapshot();
        FlowAlarmPresentationContext.RecordTransferPhase(marker, rack, "中转架放料");
        Assert.Equal(marker, failed.FailureAction!.Source);
        Assert.Contains("尚未选择", failed.FailureAction.Target);
        Assert.Equal("打号机取回", context.Snapshot().TransferPhase);
        Assert.Equal(failed.OccurredAtUtc, context.Snapshot().OccurredAtUtc);
        Assert.Equal(original, action.Snapshot() with { HeldLocks = original.HeldLocks });
        string text = SafetyAlarmFormatter.Format(failed, null);
        Assert.Contains($"{crane}号天车", text);
        Assert.Contains("版号：P-FRONT", text);
        Assert.Contains("序号：002", text);
        Assert.Contains("尚未选择", text);
        int requestedCrane = 0;
        using var model = new SafetyAlarmDialogViewModel(context, (number, _) =>
        {
            requestedCrane = number;
            return Task.FromResult(CraneAlarmSnapshot.Unknown("模拟现场读取失败"));
        }, update => update());
        string failureText = model.Sections.Failure;
        string routeText = model.Sections.Workpiece;
        await model.RefreshAsync();
        Assert.Equal(crane, requestedCrane);
        Assert.Equal(failureText, model.Sections.Failure);
        Assert.Equal(routeText, model.Sections.Workpiece);
    }

    [Fact]
    public void File_handshake_failure_displays_file_step_despite_parallel_z_business_step()
    {
        var action = Action("ST713");
        action.BeginStep(FlowActionStep.ReturnSafe, new(1, 2, 0), "打号机放料后Z回升");
        using var context = FlowAlarmPresentationContext.BeginFront(action, Workpiece(), "1号线", 1);
        FlowAlarmPresentationContext.RecordTransferPhase("ST107", "ST107", "打号机文件握手", "等待打号机B文件完成回执");
        FlowAlarmPresentationContext.RecordFailure(new TimeoutException("1小时未生成B.txt"));
        string text = SafetyAlarmFormatter.Format(context.Snapshot(), null);
        Assert.Contains("失败步骤：等待打号机B文件完成回执", text);
        Assert.Equal(FlowActionStep.ReturnSafe, action.Step);
        Assert.Contains("来源：ST107", text);
    }

    [Theory]
    [InlineData("中转架缓存交接", "通知后端写入ST016中转架缓存")]
    [InlineData("货叉Pos3取料后回升与货叉回待机", "Z回安全高度与货叉回待机（并行，具体失败见原因）")]
    public void Display_step_reports_untracked_or_parallel_work_without_relabeling_business_motion(string phase, string step)
    {
        var action = Action("ST714");
        action.BeginStep(FlowActionStep.ReturnSafe, new(100, 200, 0), "原业务登记的Z回升");
        using var context = FlowAlarmPresentationContext.BeginFront(action, Workpiece(), "2号线", 3);
        FlowAlarmPresentationContext.RecordTransferPhase("ST502", "ST016", phase, step);
        FlowAlarmPresentationContext.RecordFailure(new Exception("模拟失败"));
        var sections = SafetyAlarmFormatter.Sections(context.Snapshot(), null);
        Assert.Contains($"失败步骤：{step}", sections.Failure);
        Assert.Contains("最后业务步骤说明：原业务登记的Z回升", sections.Failure);
        Assert.Contains("业务账本最后登记运动目标：X=100 Y=200 Z=0", sections.Historical);
        Assert.Equal(FlowActionStep.ReturnSafe, action.Step);
        Assert.Equal("ST714", action.Source);
    }

    [Fact]
    public void Outer_dispatch_release_updates_existing_failure_after_action_scope_exits()
    {
        using var dispatch = FrontCraneAlarmDispatchScope.Begin();
        var action = Action("ST713");
        var context = FlowAlarmPresentationContext.BeginFront(action, Workpiece(), "1号线", 1);
        context.ObserveFinalState(() => "本动作ZoneMT/ZoneTS登记已释放", () => "中转架缓存通知未确认",
            () => "货叉阶段=WaitingMachine；当前工件=下一块板，保持原处理");
        FlowAlarmPresentationContext.RecordFailure(new Exception("中转架交接异常"));
        using var model = new SafetyAlarmDialogViewModel(context, (_, _) => Task.FromResult(CraneAlarmSnapshot.Unknown("模拟读取")), update => update());
        int updates = 0;
        context.Changed += () => updates++;
        context.Dispose();
        Assert.False(context.Snapshot().CleanupFinished);
        Assert.Contains("等待调度层释放", context.Snapshot().LockCleanup);
        Assert.Contains("收尾进行中", model.Sections.Cleanup);
        int before = updates;
        FrontCraneAlarmDispatchScope.RecordReleased();
        var final = context.Snapshot();
        Assert.True(final.CleanupFinished);
        Assert.Contains("前天车调度锁已释放", final.LockCleanup);
        Assert.Contains("下一块板", final.MemoryCleanup);
        Assert.DoesNotContain("已清零", final.DeviceCleanup);
        Assert.True(updates > before);
        Assert.Contains("本动作收尾已结束", model.Sections.Cleanup);
        Assert.Contains("前天车调度锁已释放", model.Sections.Cleanup);
    }

    [Fact]
    public void Missing_release_record_is_unknown_and_never_reported_as_success()
    {
        var dispatch = FrontCraneAlarmDispatchScope.Begin();
        var context = FlowAlarmPresentationContext.BeginFront(Action("ST714"), Workpiece(), "2号线", 3);
        FlowAlarmPresentationContext.RecordFailure(new Exception("运动失败"));
        context.Dispose();
        dispatch.Dispose();
        Assert.True(context.Snapshot().CleanupFinished);
        Assert.Contains("未取得成功释放记录", context.Snapshot().LockCleanup);
        Assert.DoesNotContain("前天车调度锁已释放", context.Snapshot().LockCleanup);
    }

    [Fact]
    public void New_front_context_does_not_replace_outer_rear_context_or_operate_a_lock()
    {
        using var outer = FlowAlarmPresentationContext.Begin(Action("ST016"), null, "2号线", 4, "斜床上料");
        using (var dispatch = FrontCraneAlarmDispatchScope.Begin())
        {
            using var inner = FlowAlarmPresentationContext.BeginFront(Action("ST714"), Workpiece(), "2号线", 3);
            inner.Changed += () => throw new Exception("窗口已关闭");
            FlowAlarmPresentationContext.RecordFailure(new Exception("测试异常"));
            FrontCraneAlarmDispatchScope.RecordReleased();
        }
        Assert.Same(outer, FlowAlarmPresentationContext.Current);
        Assert.Null(outer.Snapshot().FailureAction);
    }

    [Fact]
    public async Task Concurrent_lines_keep_their_failure_and_dispatch_results_separate_across_awaits()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        async Task<SafetyAlarmDetails> Run(int line, int crane, string marker)
        {
            using var dispatch = FrontCraneAlarmDispatchScope.Begin();
            FlowAlarmPresentationContext context;
            async Task FailAction()
            {
                using var actionContext = FlowAlarmPresentationContext.BeginFront(Action("货叉"), Workpiece(), $"{line}号线", crane);
                context = actionContext;
                FlowAlarmPresentationContext.RecordTransferPhase(marker, "中转架（尚未选择）", "打号机取回");
                if (Interlocked.Increment(ref started) == 2) bothStarted.SetResult();
                await bothStarted.Task;
                FlowAlarmPresentationContext.RecordFailure(new Exception($"线路{line}模拟异常"));
                Assert.Same(actionContext, FlowAlarmPresentationContext.Current);
            }
            context = null!;
            await FailAction();
            Assert.Null(FlowAlarmPresentationContext.Current);
            Assert.False(context.Snapshot().CleanupFinished);
            if (line == 1) FrontCraneAlarmDispatchScope.RecordReleased();
            dispatch.Dispose();
            return context.Snapshot();
        }
        var results = await Task.WhenAll(Task.Run(() => Run(1, 1, "ST107")), Task.Run(() => Run(2, 3, "ST502")));
        Assert.Equal("ST107", results[0].FailureAction!.Source);
        Assert.Equal("ST502", results[1].FailureAction!.Source);
        Assert.Equal("线路1模拟异常", results[0].Reason);
        Assert.Equal("线路2模拟异常", results[1].Reason);
        Assert.Contains("前天车调度锁已释放", results[0].LockCleanup);
        Assert.Contains("未取得成功释放记录", results[1].LockCleanup);
        Assert.All(results, result => Assert.True(result.CleanupFinished));
    }

    private static FlowActionContext Action(string source) => new(Guid.NewGuid().ToString(), "前天车", "前天车", "P-FRONT/002", source, "中转架", "前天车队列");
    private static WorkpieceCache Workpiece() => new() { PlateNo = "P-FRONT", Sequence = "002", Length = 1200.5, Diameter = 250, BoreType = 2 };
}
