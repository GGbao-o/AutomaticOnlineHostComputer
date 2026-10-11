namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 异常自动作废清零的源码契约。
/// <para>
/// 现场模型：异常了这块板会被人工抽走，设备侧随即变空，下一轮派发拿别的板。
/// 所以引擎只清<b>这台斜床 / 这台研磨机</b>的 PLC 下发数据与内存工件数据并回空闲，<b>来源作废由调用方核对预约及到达代次</b>。
/// </para>
/// </summary>
/// <remarks>
/// 严重主动作失败应按归属触发本机清理并软件Idle，普通取消及失效版本不能套用同一作废。
/// 源码契约核对清理调用、参数范围及内存结案位置；不保证PLC多寄存器写原子成功。
/// </remarks>
public sealed class AbortClearOnExceptionContractTests
{
    private const string Line1 = "Line1RearFlowEngine.cs";
    private const string Line2 = "Line2RearFlowEngine.cs";
    private const string Grinding = "GrindingFlowEngine.cs";

    [Theory]
    [InlineData(Line1, "await ClearSkewOnAbortAsync(bed, isLoad: true);", "await ClearSkewOnAbortAsync(bed, isLoad: false);")]
    [InlineData(Line2, "await ClearSkewOnAbortAsync(bed, isLoad: true);", "await ClearSkewOnAbortAsync(bed, isLoad: false);")]
    [InlineData(Grinding, "await ClearGrinderOnAbortAsync(grinder, isLoad: true);", "await ClearGrinderOnAbortAsync(grinder, isLoad: false);")]
    public void Abort_clear_runs_in_finally_only_for_current_serious_failed_action(
        string file, string loadCall, string unloadCall)
    {
        string source = Read(file);

        foreach (string call in new[] { loadCall, unloadCall })
        {
            int at = source.IndexOf(call, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{file} 缺少调用 {call}");

            string guard = file == Grinding
                ? "if (discardWorkpieceOnAbort && IsGrindingActionCurrent(actionVersion))"
                : "if (discardOnFailure && operation.IsValid && ReferenceEquals(bed.ActiveOperation, operation))";
            int guardAt = source.LastIndexOf(guard, at, StringComparison.Ordinal);
            Assert.True(guardAt >= 0 && at - guardAt < 1000, $"{file}清理须由本动作严重异常及版本共同守卫");

            // 必须在当前动作finally内，允许其中等待并收束已经启动的Z任务。
            // 注意：helper 的说明注释里也写了"finally"这个词，所以必须匹配行首缩进的 finally 关键字，
            // 不能用裸的 "finally" 去 LastIndexOf —— 否则会命中注释、把断言判错。
            int finallyAt = source.LastIndexOf("\n        finally", guardAt, StringComparison.Ordinal);
            Assert.True(finallyAt >= 0, $"{file} 的 {call} 前方未找到行首 finally 关键字");
            Assert.DoesNotContain("\n        catch", source[finallyAt..at], StringComparison.Ordinal);
        }

        // 上料与下料必须分别传 true / false，配反会让日志与账本快照清错方向。
        Assert.True(source.IndexOf(loadCall, StringComparison.Ordinal) < source.IndexOf(unloadCall, StringComparison.Ordinal),
            $"{file} 上料调用（isLoad: true）必须出现在下料调用（isLoad: false）之前");
    }

    [Theory]
    [InlineData(Line1, "private async Task ClearSkewOnAbortAsync", "private const string AbortClearInstruction", true)]
    [InlineData(Line2, "private async Task ClearSkewOnAbortAsync", "private const string AbortClearInstruction", true)]
    [InlineData(Grinding, "private async Task ClearGrinderOnAbortAsync", "目标研磨机上料准备阶段", false)]
    public void Abort_clear_helper_is_safe_and_complete(string file, string startAnchor, string endAnchor, bool clearsCraneTask)
    {
        string helper = ExtractHelper(Read(file), file, startAnchor, endAnchor);

        // ① 绝不允许出现运动或磁铁命令：清零只改软件与设备寄存器，天车保持原地不动。
        Assert.DoesNotContain("MoveAbsoluteAsync", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("MagnetOnAsync", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("MagnetOffAsync", helper, StringComparison.Ordinal);

        // ② 绝不碰中转架账本：来源预约与来源冻结仍由既有入口处理。
        Assert.DoesNotContain("_rackLedger", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveManualReservation", helper, StringComparison.Ordinal);

        // ③ 不得用 IsFinalized 作判据。
        Assert.DoesNotContain("IsFinalized", helper, StringComparison.Ordinal);

        // ④ 清设备下发数据必须传 CancellationToken.None：本方法在 finally 执行，
        //    动作的 ct 可能已被应急取消，沿用它会抛出并掩盖真正的原始异常。
        Assert.Contains("new CancellationTokenSource(10_000)", helper);
        Assert.Contains("cleanupCts.Token", helper);
        Assert.DoesNotContain("ClearSkewDeviceEmergencyRegistersAsync(bed, ct)", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearEmergencyRegistersAsync(ct)", helper, StringComparison.Ordinal);

        // ⑤ 清内存工件数据并回空闲。
        bool clearsWorkpiece = helper.Contains("bed.Wp = null;", StringComparison.Ordinal)
            || helper.Contains("grinder.PendingWorkpiece = null;", StringComparison.Ordinal);
        Assert.True(clearsWorkpiece, $"{file} 的作废 helper 未清内存工件数据");
        bool backToIdle = helper.Contains("bed.St = SkewState.Idle;", StringComparison.Ordinal)
            || helper.Contains("grinder.State = GrinderState.Idle;", StringComparison.Ordinal);
        Assert.True(backToIdle, $"{file} 的作废 helper 未把设备状态回空闲");

        // PollVersion 是斜床自己的轮询版本号；只有斜床 helper 需要推进它让展示快照立即刷新。
        if (helper.Contains("bed.St", StringComparison.Ordinal))
            Assert.Contains("PollVersion", helper, StringComparison.Ordinal);

        if (clearsCraneTask)
            Assert.Contains("ClearRearCraneTask();", helper, StringComparison.Ordinal);

        // 本次活动快照由调用方按actionId结案，不能清同机台其他动作。
        Assert.DoesNotContain("FlowActionManualRegistry.Snapshot()", helper);
        Assert.Contains("action.Discard(", Read(file));

        // ⑦ 日志/告警必须同时说清"已自动作废"和下一步动作。
        Assert.Contains("本轮已自动作废", helper, StringComparison.Ordinal);
        Assert.Contains("请把本块板抽走", helper, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Line1)]
    [InlineData(Line2)]
    [InlineData(Grinding)]
    public void Abort_clear_replaces_the_obsolete_manual_recovery_wording(string file)
    {
        string source = Read(file);

        // 清零之后不再需要人工走应急恢复，旧口径必须清除，否则操作员会被误导。
        Assert.DoesNotContain("斜床应急恢复", source, StringComparison.Ordinal);
        Assert.DoesNotContain("禁止直接点击启动", source, StringComparison.Ordinal);
        Assert.DoesNotContain("工件状态未自动清除", source, StringComparison.Ordinal);
    }

    private static string ExtractHelper(string source, string file, string startAnchor, string endAnchor)
    {
        int start = source.IndexOf(startAnchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{file} 未找到作废 helper 定义 {startAnchor}");

        int end = source.IndexOf(endAnchor, start, StringComparison.Ordinal);
        Assert.True(end > start, $"{file} 未找到作废 helper 的结束边界 {endAnchor}");
        return source[start..end];
    }

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", fileName));
}
