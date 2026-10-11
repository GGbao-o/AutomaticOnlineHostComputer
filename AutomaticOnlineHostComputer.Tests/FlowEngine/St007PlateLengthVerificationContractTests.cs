namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 机械手1 取料板长校验（ST007 D100 ↔ 任务板长）的源码契约。
/// 两条前端引擎必须同构：判定早于任何物理命令；不一致时丢弃任务身份而**不回退本线缓存**；
/// 弹窗必须带齐人工处置所需的信息。
/// </summary>
public sealed class St007PlateLengthVerificationContractTests
{
    private const string Line1File = "Line1FrontFlowEngine.cs";
    private const string Line2File = "Line2FrontFlowEngine.cs";

    [Theory]
    [InlineData(Line1File)]
    [InlineData(Line2File)]
    public void Pickup_verifies_plate_length_before_any_physical_command(string fileName)
    {
        string source = ReadFront(fileName);

        // 判定必须在 D100 读取之后、action.MarkCommandSent() 之前。
        // 只有在此之前抛出，外层收尾才会按"尚未下发物理命令"处理，从而允许丢弃任务身份。
        int readD100 = source.IndexOf("ReadPlateLengthAsync(ct)", StringComparison.Ordinal);
        int verdict = source.IndexOf(
            "PlateLengthVerification.Evaluate(plateLen, wp.Length, plateLengthToleranceMm)", StringComparison.Ordinal);
        int throwMismatch = source.IndexOf("throw new PlateLengthMismatchException(", StringComparison.Ordinal);
        int firstMarkSent = source.IndexOf("action.MarkCommandSent();", verdict, StringComparison.Ordinal);
        int firstPhysicalWrite = source.IndexOf("SetAbsSpeedAsync(", verdict, StringComparison.Ordinal);

        Assert.True(readD100 >= 0, $"{fileName} 未读取 D100");
        Assert.True(verdict > readD100, $"{fileName} 板长判定必须在 D100 读取之后");
        Assert.True(throwMismatch > verdict, $"{fileName} 不一致时必须抛 PlateLengthMismatchException");
        Assert.True(throwMismatch < firstMarkSent,
            $"{fileName} 板长判定必须早于 action.MarkCommandSent()，否则会被误判为命令响应未知");
        Assert.True(throwMismatch < firstPhysicalWrite,
            $"{fileName} 板长判定必须早于第一条物理写命令 SetAbsSpeedAsync");

        // 容差来自配置，不得硬编码。
        Assert.Contains("_cfg.Safety.PlateLengthToleranceMm", source, StringComparison.Ordinal);
        // 校验不可用时只记日志放行，也必须留下痕迹。
        Assert.Contains("板长校验不可用", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Line1File)]
    [InlineData(Line2File)]
    public void Mismatch_discards_the_task_identity_and_never_requeues_it(string fileName)
    {
        string source = ReadFront(fileName);
        string mismatchBranch = ExtractMismatchBranch(source, fileName);

        // 共用身份限定的暂停作废入口：清匹配current及来源残留，保留其他任务。
        // helper的真实状态转换由PausedWorkpieceDiscardTests覆盖。
        Assert.Contains("PauseAndDiscardManipulatorWorkpiece(wp);", mismatchBranch, StringComparison.Ordinal);
        // … 但绝不整条清空缓存（会丢掉缓存里其它任务）…
        Assert.DoesNotContain("ClearCache()", mismatchBranch, StringComparison.Ordinal);
        // … 也绝不回退本线缓存。若回退，本线缓存长期非空会让"两线前端缓存都空才派发"闸门
        // 永久拦住全局派发，整条派发链停摆。
        Assert.DoesNotContain("RequeueFrontCache", mismatchBranch, StringComparison.Ordinal);
        Assert.Contains("_paused = true;", source[source.IndexOf("private void PauseAndDiscardManipulatorWorkpiece", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Line1File)]
    [InlineData(Line2File)]
    public void Mismatch_alarm_carries_everything_the_operator_needs(string fileName)
    {
        string source = ReadFront(fileName);
        string mismatchBranch = ExtractMismatchBranch(source, fileName);

        Assert.Contains("OnSafetyAlarm?.Invoke(", mismatchBranch, StringComparison.Ordinal);
        Assert.Contains("实测板长(D100)", mismatchBranch, StringComparison.Ordinal);
        Assert.Contains("任务板长(ERP)", mismatchBranch, StringComparison.Ordinal);
        Assert.Contains("（容差 ", mismatchBranch, StringComparison.Ordinal);
        Assert.Contains("来源工位：ST007", mismatchBranch, StringComparison.Ordinal);
        // 必须明确"实物板仍在 ST007"，否则现场会以为系统已经处理掉了这块板。
        Assert.Contains("实物板仍在ST007未被处理", mismatchBranch, StringComparison.Ordinal);
        // 换板必须等到日志确认 M800 复位，否则复位门永久关闭（除重启无法恢复）。
        Assert.Contains("已观察到上一块M800复位为0", mismatchBranch, StringComparison.Ordinal);
        // 工件身份必须出现在弹窗里，便于按版号/序号重导 ERP 任务。
        Assert.Contains("{wp.IdentityText}", mismatchBranch, StringComparison.Ordinal);
    }

    /// <summary>截取"板长不一致"分支本体：从分支条件到通用回退分支的注释之前。</summary>
    private static string ExtractMismatchBranch(string source, string fileName)
    {
        int start = source.IndexOf("else if (ex is PlateLengthMismatchException", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{fileName} 缺少板长不一致的专用 catch 分支");

        int end = source.IndexOf("// 尚未确认从总上料架吸起", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{fileName} 未找到通用回退分支的边界注释");

        return source[start..end];
    }

    private static string ReadFront(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", fileName));
}
