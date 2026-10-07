namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// ST019/M817 分流目的地二次确认契约。
/// M817=有版时不移动天车、不退磁、不暂停，也**不释放** M817/M720 两把分流锁，
/// 保持持件和安全 Z 在锁内按轮询周期无限等待，直到 M817 恢复空闲才进入分流区。
/// 既不允许退回"直接抛异常停线"，也不允许中途让锁（会破坏分流区互斥）。
/// </summary>
public sealed class RearSt019DestinationWaitContractTests
{
    [Fact]
    public void St019_destination_busy_keeps_locks_and_waits_until_empty()
    {
        string source = ReadRear("Line1RearFlowEngine.cs");
        string method = ExtractMethod(source, "private async Task EnsureM817EmptyBeforePlaceAsync(");

        Assert.Contains("while (!await ReadM817EmptyAsync(ct, log: waitCount == 0))", method, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);", method, StringComparison.Ordinal);
        Assert.Contains("保持持件和安全Z持锁等待", method, StringComparison.Ordinal);
        Assert.Contains("结束持锁等待", method, StringComparison.Ordinal);

        // 持锁等待：等待循环里不得释放锁，也不得重新获取锁。
        Assert.DoesNotContain("ReleaseDivertLocks", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TryRelease", method, StringComparison.Ordinal);
        Assert.DoesNotContain("AcquireAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TryReleaseLock", method, StringComparison.Ordinal);

        // 无限等待：不允许超时或直接停线。
        Assert.DoesNotContain("HandshakeTimeoutMs", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeoutException", method, StringComparison.Ordinal);
        Assert.DoesNotContain("禁止放料", method, StringComparison.Ordinal);
    }

    [Fact]
    public void St019_busy_no_longer_throws_and_divert_locks_are_never_released_early()
    {
        string source = ReadRear("Line1RearFlowEngine.cs");

        Assert.Contains("await EnsureM817EmptyBeforePlaceAsync(wp, ct);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await ConfirmM817EmptyBeforePlaceAsync(ct);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("M817二次确认=有版, 禁止放料", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseDivertLocks", source, StringComparison.Ordinal);

        // 目的地二次确认必须在等待机械手2/3离开料架区之前完成；等待期间两把锁一直持有，
        // 因此 gotM817/gotM720 由分支入口置位后不再回写。
        int call = source.IndexOf("await EnsureM817EmptyBeforePlaceAsync(wp, ct);", StringComparison.Ordinal);
        int waitManipulators = source.IndexOf("await WaitManipulatorsClearOfRackAsync(ct);", call, StringComparison.Ordinal);
        Assert.True(call >= 0 && waitManipulators > call, "必须先完成目的地二次确认，再等待机械手2/3离开料架区");

        int branchStart = source.LastIndexOf("bool gotM817 = false;", call, StringComparison.Ordinal);
        Assert.True(branchStart >= 0 && branchStart < call, "未找到分流分支入口");
        Assert.DoesNotContain("gotM817 = operation.IsHeld", source[branchStart..waitManipulators], StringComparison.Ordinal);
        Assert.DoesNotContain("gotM720 = operation.IsHeld", source[branchStart..waitManipulators], StringComparison.Ordinal);
    }

    [Fact]
    public void St019_wait_helper_reads_real_plc_and_treats_short_reply_as_an_error()
    {
        string source = ReadRear("Line1RearFlowEngine.cs");
        string method = ExtractMethod(source, "private async Task<bool> ReadM817EmptyAsync(");

        // M817 = MC63 M800 起始第2个字(M816~M831)的 bit1。
        Assert.Contains("ReadMAlignedWordAsync(800, 2,", method, StringComparison.Ordinal);
        Assert.Contains("(m816 & (1 << 1)) != 0", method, StringComparison.Ordinal);
        // 报文变短必须视为错误并抛出，不能当成"空闲"而误放料。
        Assert.Contains("throw new InvalidOperationException(\"MC63 M817二次确认返回字数不足\")", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Business_doc_Documents_st019_hold_lock_wait_semantics()
    {
        string docs = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Docs", "全线业务逻辑详解.md"));

        Assert.Contains("也不释放 M817、M720 两把分流锁", docs, StringComparison.Ordinal);
        Assert.Contains("保持持件和安全 Z 在锁内按轮询周期无限等待", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("如果不允许也没事 就等", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("不得持锁原地等待", docs, StringComparison.Ordinal);
    }

    private static string ReadRear(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", fileName));

    private static string ExtractMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到方法：{signature}");
        int bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart >= 0, $"方法缺少左大括号：{signature}");
        int depth = 0;
        for (int i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        throw new InvalidDataException($"未找到方法结尾：{signature}");
    }
}
