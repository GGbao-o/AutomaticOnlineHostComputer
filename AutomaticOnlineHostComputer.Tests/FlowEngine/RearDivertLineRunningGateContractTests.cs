using AutomaticOnlineHostComputer.Infrastructure.Config;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 后天车下料分流前"本线已启动"门禁的源码契约。
/// 核心不变量：检查点必须在分流区 try 内、拿两把分流锁之前、任何 XY 移动之前；
/// 超时必须作废本轮分流（清斜床 PLC 下发数据 + 内存回空闲）并保持天车持件不动。
/// </summary>
public sealed class RearDivertLineRunningGateContractTests
{
    private const string Line1File = "Line1RearFlowEngine.cs";
    private const string Line2File = "Line2RearFlowEngine.cs";

    [Theory]
    [InlineData(Line1File, "bool gotM817 = false;", "AcquireAsync(\"M817\"", "持件XY到ST019/M817")]
    [InlineData(Line2File, "bool gotM818 = false;", "AcquireAsync(\"M818\"", "持件XY到ST020/M818")]
    public void Divert_gate_runs_inside_try_and_before_locks_and_before_any_xy_move(
        string fileName, string flagAnchor, string firstLockAcquire, string firstDivertXy)
    {
        string source = ReadRear(fileName);

        int ensure = source.IndexOf("await EnsureLineRunningBeforeDivertAsync(bed, ct);", StringComparison.Ordinal);
        int flag = source.IndexOf(flagAnchor, StringComparison.Ordinal);
        int firstLock = source.IndexOf(firstLockAcquire, StringComparison.Ordinal);
        int firstXy = source.IndexOf(firstDivertXy, StringComparison.Ordinal);

        Assert.True(flag >= 0, $"{fileName} 未找到分流锁标志声明 {flagAnchor}");
        Assert.True(ensure > flag, $"{fileName} 分流门禁必须在分流锁标志声明之后（即分流区内）");
        Assert.True(firstLock > ensure, $"{fileName} 分流门禁必须早于第一把分流锁 AcquireAsync，否则等待期间会占着共享锁");
        Assert.True(firstXy > ensure, $"{fileName} 分流门禁必须早于第一次分流 XY 移动");

        // 门禁必须在 try 之内：否则抛出后外层 finally 不执行，后天车锁无法释放。
        string between = source[flag..firstLock];
        const string ensureCall = "await EnsureLineRunningBeforeDivertAsync(bed, ct);";
        Assert.Contains(ensureCall, between, StringComparison.Ordinal);

        int tryRelative = between.IndexOf("try", StringComparison.Ordinal);
        int ensureRelative = between.IndexOf(ensureCall, StringComparison.Ordinal);
        Assert.True(tryRelative >= 0, $"{fileName} 分流锁标志与门禁之间没有 try");
        Assert.True(ensureRelative > tryRelative, $"{fileName} 分流门禁必须在 try 之后");
        // try 与门禁之间不得出现 catch —— 保证门禁确实在 try 块体内，而不是落在 try 之后。
        Assert.DoesNotContain("catch", between[tryRelative..ensureRelative], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Line1File)]
    [InlineData(Line2File)]
    public void Gate_waits_on_the_engine_pause_flag_and_is_bounded_by_config(string fileName)
    {
        string source = ReadRear(fileName);
        string helper = ExtractHelper(source, fileName);

        // 判据是引擎自身的暂停位：与主页"暂停N号线"天然同步，无需跨层回调。
        Assert.Contains("if (!_paused) return;", helper, StringComparison.Ordinal);
        Assert.Contains("while (_paused)", helper, StringComparison.Ordinal);
        // 上限来自配置，不得硬编码。
        Assert.Contains("_cfg.Grinding.LineStartWaitTimeoutMs", helper, StringComparison.Ordinal);
        Assert.Contains("_cfg.Grinding.PollIntervalMs", helper, StringComparison.Ordinal);
        Assert.Contains("throw new LineNotStartedTimeoutException(", helper, StringComparison.Ordinal);
        // 等待期间不得移动天车、不得退磁。
        Assert.DoesNotContain("MoveAbsoluteAsync", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("MagnetOffAsync", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("MagnetOnAsync", helper, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Line1File)]
    [InlineData(Line2File)]
    public void Timeout_aborts_the_divert_and_keeps_the_crane_holding(string fileName)
    {
        string branch = ExtractTimeoutBranch(ReadRear(fileName), fileName);

        // 暂停本引擎并留下动作账本结论。
        Assert.Contains("_paused = true;", branch, StringComparison.Ordinal);
        Assert.Contains("action.PauseForManualResolution(ex.Message);", branch, StringComparison.Ordinal);
        // 清该斜床 PLC 下发数据。
        Assert.Contains("ClearSkewDeviceEmergencyRegistersAsync(bed, ct)", branch, StringComparison.Ordinal);
        // 内存状态回空闲：斜床回 Idle、清工件与相关标志。
        Assert.Contains("bed.St = SkewState.Idle;", branch, StringComparison.Ordinal);
        Assert.Contains("bed.Wp = null;", branch, StringComparison.Ordinal);
        Assert.Contains("ClearRearCraneTask();", branch, StringComparison.Ordinal);
        // 天车不动：本分支不得有任何运动或磁铁命令。
        Assert.DoesNotContain("MoveAbsoluteAsync", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("MagnetOffAsync", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("MagnetOnAsync", branch, StringComparison.Ordinal);
        // 必须自己报警，且说清"人工放磁取件后启动线路继续"。
        Assert.Contains("OnRearCraneSafetyAlarm?.Invoke(", branch, StringComparison.Ordinal);
        Assert.Contains("请人工放磁取下工件并把天车移回安全位置，然后启动线路继续", branch, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Line1File)]
    [InlineData(Line2File)]
    public void Timeout_does_not_set_pendingSafetyAlarm_so_the_recovery_tail_is_not_appended(string fileName)
    {
        string branch = ExtractTimeoutBranch(ReadRear(fileName), fileName);

        // 外层 finally 在 pendingSafetyAlarm 非空时会追加"请先人工处理现场…执行斜床应急恢复"，
        // 而本场景斜床已回空闲、操作员应直接启动线路，所以本分支不得设置它，否则会出现两条告警且口径矛盾。
        Assert.DoesNotContain("pendingSafetyAlarm =", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("斜床应急恢复", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void Motion_config_defaults_the_line_start_wait_timeout_to_120_seconds()
    {
        Assert.Equal(120_000, new MotionConfig().Grinding.LineStartWaitTimeoutMs);
    }

    /// <summary>截取分流门禁 helper 本体：从其方法签名到下一个方法签名之前。</summary>
    private static string ExtractHelper(string source, string fileName)
    {
        // helper 定义在调用点之后，因此按文件名顺序向后定位，不能从调用点向前找。
        int start = source.IndexOf("private async Task EnsureLineRunningBeforeDivertAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{fileName} 未找到分流门禁 helper 定义");

        int end = source.IndexOf("private async Task WaitManipulatorsClearOfRackAsync", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{fileName} 未找到分流门禁 helper 的结束边界");
        return source[start..end];
    }

    /// <summary>截取"线路未启动超时"catch 分支本体：从分支条件到下一个 catch 之前。</summary>
    private static string ExtractTimeoutBranch(string source, string fileName)
    {
        string marker = "catch (LineNotStartedTimeoutException ex)";
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{fileName} 缺少线路未启动超时的专用 catch 分支");

        int end = source.IndexOf("catch (Exception ex)", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{fileName} 未找到超时分支的边界");
        return source[start..end];
    }

    private static string ReadRear(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", fileName));
}
