namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 2号线 ST020/M818 与 ST021/M820 分流目的地二次确认契约。
/// 与 1号线 ST019/M817 同样的语义：目的地未允许放料时不移动天车、不退磁、不暂停，
/// 也**不释放** M818/M821 两把分流锁，保持持件和安全 Z 在锁内按轮询周期无限等待。
/// 既不允许退回"直接抛异常停线"，也不允许中途让锁。
/// </summary>
public sealed class RearSt020St021DestinationWaitContractTests
{
    [Theory]
    [InlineData("EnsureM818CanPlaceBeforePlaceAsync", "ReadM818CanPlaceAsync", "M818")]
    [InlineData("EnsureM820CanPlaceBeforePlaceAsync", "ReadM820CanPlaceAsync", "M820")]
    public void St020_and_st021_keep_divert_locks_and_wait_until_allowed(string ensureMethod, string readMethod, string signal)
    {
        string source = ReadRear("Line2RearFlowEngine.cs");
        string method = ExtractMethod(source, $"private async Task {ensureMethod}(");

        Assert.Contains($"while (!await {readMethod}(ct, log: waitCount == 0))", method, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(_cfg.Grinding.PollIntervalMs, ct);", method, StringComparison.Ordinal);
        Assert.Contains("保持持件和安全Z持锁等待", method, StringComparison.Ordinal);
        Assert.Contains("结束持锁等待", method, StringComparison.Ordinal);

        // 持锁等待：等待循环里不得释放锁，也不得重新获取锁。
        Assert.DoesNotContain("ReleaseDivertLocks", method, StringComparison.Ordinal);
        Assert.DoesNotContain("AcquireDivertLocksAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TryRelease", method, StringComparison.Ordinal);
        Assert.DoesNotContain("AcquireAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TryReleaseLock", method, StringComparison.Ordinal);

        // 无限等待：不允许超时或直接停线。
        Assert.DoesNotContain("HandshakeTimeoutMs", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeoutException", method, StringComparison.Ordinal);
        Assert.DoesNotContain($"throw new InvalidOperationException(\"{signal}=", method, StringComparison.Ordinal);
    }

    [Fact]
    public void St020_and_st021_no_longer_throw_and_divert_locks_are_never_released_early()
    {
        string source = ReadRear("Line2RearFlowEngine.cs");

        Assert.DoesNotContain("await ConfirmM818CanPlaceBeforePlaceAsync(ct);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await ConfirmM820CanPlaceBeforePlaceAsync(ct);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("M818=0, ST020未允许后天车放料", source, StringComparison.Ordinal);
        Assert.DoesNotContain("M820=0, ST021未允许后天车放料", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseDivertLocks", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AcquireDivertLocksAsync", source, StringComparison.Ordinal);

        foreach (string call in new[]
                 {
                     "await EnsureM818CanPlaceBeforePlaceAsync(wp, ct);",
                     "await EnsureM820CanPlaceBeforePlaceAsync(wp, ct);"
                 })
        {
            int index = source.IndexOf(call, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Line2RearFlowEngine 缺少调用：{call}");
            int waitManipulators = source.IndexOf("await WaitManipulatorsClearOfRackAsync(ct);", index, StringComparison.Ordinal);
            Assert.True(waitManipulators > index, $"必须先完成目的地二次确认，再等待机械手2/3离开料架区（{call}）");

            int branchStart = source.LastIndexOf("bool gotM818 = false;", index, StringComparison.Ordinal);
            Assert.True(branchStart >= 0 && branchStart < index, "未找到分流分支入口");
            Assert.DoesNotContain("gotM818 = operation.IsHeld", source[branchStart..waitManipulators], StringComparison.Ordinal);
            Assert.DoesNotContain("gotM821 = operation.IsHeld", source[branchStart..waitManipulators], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void St020_st021_wait_helper_reads_real_plc_and_treats_short_reply_as_an_error()
    {
        string source = ReadRear("Line2RearFlowEngine.cs");
        string method = ExtractMethod(source, "private async Task<bool> ReadRearHandshakeBitAsync(");

        Assert.Contains("ReadMAlignedWordAsync(800, 2,", method, StringComparison.Ordinal);
        Assert.Contains("(m816 & (1 << bitOffset)) != 0", method, StringComparison.Ordinal);
        Assert.Contains("throw new InvalidOperationException($\"MC63 {signalName}二次确认返回字数不足\")", method, StringComparison.Ordinal);

        // M818/M820 的 bit 偏移必须来自地址常量，不允许硬编码。
        Assert.Contains("ReadRearHandshakeBitAsync(RackAddr.Bit_ST020_CanPlace, \"M818\", ct, log)", source, StringComparison.Ordinal);
        Assert.Contains("ReadRearHandshakeBitAsync(RackAddr.Bit_ST021_CanPlace, \"M820\", ct, log)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Business_doc_Documents_st020_and_st021_hold_lock_wait_semantics()
    {
        string docs = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Docs", "全线业务逻辑详解.md"));

        Assert.Contains("也不释放 M818、M821 两把分流锁", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("2. 锁内再次确认M818=1", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("2. 锁内再次确认M820=1", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("互等死锁", docs, StringComparison.Ordinal);
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
