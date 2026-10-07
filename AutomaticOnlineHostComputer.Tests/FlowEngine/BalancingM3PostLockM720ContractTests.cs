namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 机械手3等待M821/M720位置锁后必须使用新鲜M720值再决定是否开始取料。
/// 尚未取料时M720=0应正常放弃；通信状态未知才重试并最终暂停。
/// </summary>
public sealed class BalancingM3PostLockM720ContractTests
{
    [Fact]
    public void M3_rechecks_M720_after_both_locks_before_any_source_work()
    {
        string source = ReadFlowSource();
        int flowStart = source.IndexOf("private async Task M3Flow", StringComparison.Ordinal);
        int flowEnd = source.IndexOf("private async Task ConfigureManipulatorAbsSpeedAsync", flowStart, StringComparison.Ordinal);

        Assert.True(flowStart >= 0 && flowEnd > flowStart, "未找到完整M3Flow源码范围。");
        string flow = source[flowStart..flowEnd];

        int m720LockAcquired = flow.IndexOf("MarkM3Lock(\"M720\", true)", StringComparison.Ordinal);
        int postLockCheck = flow.IndexOf("await ConfirmM720AfterM3LocksAsync(ct)", StringComparison.Ordinal);
        int sourceWork = flow.IndexOf("string pickReg", StringComparison.Ordinal);

        Assert.True(m720LockAcquired >= 0, "M3必须登记已经取得M720锁。");
        Assert.True(postLockCheck > m720LockAcquired, "M720复查必须发生在M821/M720两把锁取得之后。");
        Assert.True(sourceWork > postLockCheck, "M720复查必须发生在M825、D100、缓存和任何来源动作之前。");
        Assert.Contains("if (!await ConfirmM720AfterM3LocksAsync(ct))", flow, StringComparison.Ordinal);
        Assert.Contains("return;", flow[postLockCheck..sourceWork], StringComparison.Ordinal);
        Assert.True(
            flow.IndexOf("if (gotM720)", StringComparison.Ordinal) <
            flow.IndexOf("if (gotM821)", StringComparison.Ordinal),
            "M3资源清理必须按后拿先放顺序先释放M720、再释放M821。");
        Assert.Contains("ReleaseM3RackLocks(\"finally兜底\")", flow, StringComparison.Ordinal);
        Assert.Contains("ClearM3Busy();", flow, StringComparison.Ordinal);
    }

    [Fact]
    public void M3_post_lock_check_retries_three_times_logs_and_pauses_only_when_state_stays_unknown()
    {
        string source = ReadFlowSource();
        int start = source.IndexOf("private async Task<bool> ConfirmM720AfterM3LocksAsync", StringComparison.Ordinal);
        int end = source.IndexOf("private async Task ConfirmM720EmptyBeforePlaceAsync", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "未找到独立的M3锁后M720复查方法。");
        string method = source[start..end];

        Assert.Contains("const int maxAttempts = 3;", method, StringComparison.Ordinal);
        Assert.Contains("const int retryDelayMs = 500;", method, StringComparison.Ordinal);
        Assert.Contains("const string mc65Ip = \"192.168.2.65\";", method, StringComparison.Ordinal);
        Assert.Contains("const int mc65Port = 9000;", method, StringComparison.Ordinal);
        Assert.Contains("attempt <= maxAttempts", method, StringComparison.Ordinal);
        Assert.Contains("await _mcCache.GetOrCreateAsync(mc65Ip, mc65Port, ct)", method, StringComparison.Ordinal);
        Assert.Contains("await _mcCache.InvalidateAsync(mc65Ip, mc65Port)", method, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex) when (ex is not OperationCanceledException)", method, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(retryDelayMs, ct);", method, StringComparison.Ordinal);
        Assert.Contains("锁后复查M720读取失败", method, StringComparison.Ordinal);
        Assert.Contains("锁后复查M720=1", method, StringComparison.Ordinal);
        Assert.Contains("锁后复查M720=0", method, StringComparison.Ordinal);
        Assert.Contains("_paused = true;", method, StringComparison.Ordinal);
        Assert.Contains("OnSafetyAlarm?.Invoke(message);", method, StringComparison.Ordinal);
    }

    [Fact]
    public void M3_keeps_the_existing_held_workpiece_M720_wait_separate()
    {
        string source = ReadFlowSource();
        int start = source.IndexOf("private async Task ConfirmM720EmptyBeforePlaceAsync", StringComparison.Ordinal);
        int end = source.IndexOf("private async Task ConfirmMc63HandshakeBitAsync", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "未找到持件后的M720等待方法。");
        string method = source[start..end];

        Assert.Contains("while (true)", method, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);", method, StringComparison.Ordinal);
        Assert.Contains("工件状态", method, StringComparison.Ordinal);
        Assert.Contains("保持持件", method, StringComparison.Ordinal);
        Assert.DoesNotContain("maxAttempts", method, StringComparison.Ordinal);
    }

    private static string ReadFlowSource() =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", "BalancingFlowEngine.cs"));
}
