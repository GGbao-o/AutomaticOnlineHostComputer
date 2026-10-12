namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 检查两线ZoneMT准入的控制流顺序：全满必须先释放再锁外读取；暂停保留锁/任务无限等待。
/// 属于源码契约，不模拟PLC或机械并发；恢复后不二次读架是用户明确选择的本轮规则。
/// </summary>
public sealed class FrontCraneZoneMtAdmissionContractTests
{
    [Theory]
    [InlineData("Line1FrontFlowEngine.cs", "Station1HasPlate", "Station2HasPlate", "Station3HasPlate")]
    [InlineData("Line2FrontFlowEngine.cs", "Station4HasPlate", "Station5HasPlate", "Station6HasPlate")]
    public void Full_racks_release_before_polling_outside_then_retry_acquisition(
        string file, string first, string second, string third)
    {
        string gate = Gate(file);
        Assert.Contains($"if (lockedRackStatus.{first} && lockedRackStatus.{second} && lockedRackStatus.{third})", gate);
        int release = gate.IndexOf("_safety.MarkerTransferCollisionLock.Release();", StringComparison.Ordinal);
        int reset = gate.IndexOf("zoneMtLocked = false;", release, StringComparison.Ordinal);
        int ledger = gate.IndexOf("action.TryReleaseLock(\"ZoneMT\");", reset, StringComparison.Ordinal);
        int outsideRead = gate.IndexOf("var outsideRackStatus = await _rackSvc.ReadAllStatusAsync(ct);", ledger, StringComparison.Ordinal);
        int retry = gate.IndexOf("continue;", outsideRead, StringComparison.Ordinal);
        Assert.True(release >= 0 && reset > release && ledger > reset && outsideRead > ledger && retry > outsideRead);
        Assert.Contains($"!outsideRackStatus.{first} || !outsideRackStatus.{second} || !outsideRackStatus.{third}", gate);
        Assert.Contains("while (true)", gate[..release]);
    }

    [Theory]
    [InlineData("Line1FrontFlowEngine.cs")]
    [InlineData("Line2FrontFlowEngine.cs")]
    public void Pause_wait_keeps_the_board_and_lock_without_timeout_or_resume_reread(string file)
    {
        string gate = Gate(file);
        string pause = gate[gate.IndexOf("if (_paused)", StringComparison.Ordinal)..];
        Assert.Contains("while (_paused)", pause);
        Assert.Contains("await Task.Delay(500, ct);", pause);
        Assert.DoesNotContain("Release(", pause);
        Assert.DoesNotContain("ReadAllStatusAsync", pause);
        Assert.DoesNotContain("Timeout", pause);
        Assert.DoesNotContain("TimeSpan", pause);
        Assert.DoesNotContain("Discard(", gate);
        Assert.DoesNotContain("EnqueueFrontCrane", gate);
        Assert.DoesNotContain("MagnetOff", gate);
    }

    [Theory]
    [InlineData("Line1FrontFlowEngine.cs")]
    [InlineData("Line2FrontFlowEngine.cs")]
    public void Acquisition_and_reads_are_inside_original_cleanup_scope_before_marking(string file)
    {
        string source = Read(file);
        int marker = source.IndexOf("// ZoneMT准入：", StringComparison.Ordinal);
        int flags = source.IndexOf("bool zoneMtLocked = false;", marker, StringComparison.Ordinal);
        int guardedTry = source.IndexOf("try", flags, StringComparison.Ordinal);
        int acquisition = source.IndexOf("MarkerTransferCollisionLock.WaitAsync", guardedTry, StringComparison.Ordinal);
        int held = source.IndexOf("zoneMtLocked = true;", acquisition, StringComparison.Ordinal);
        int register = source.IndexOf("action.RegisterLock(\"ZoneMT\");", held, StringComparison.Ordinal);
        int read = source.IndexOf("var lockedRackStatus = await _rackSvc!.ReadAllStatusAsync(ct);", register, StringComparison.Ordinal);
        int marking = source.IndexOf("await MarkingHandshakeAsync", read, StringComparison.Ordinal);
        Assert.True(marker >= 0 && flags > marker && guardedTry > flags && acquisition > guardedTry);
        Assert.True(held > acquisition && register > held && read > register && marking > read);
        Assert.Contains("if (zoneMtLocked)", source[marking..]);
    }

    private static string Gate(string file)
    {
        string source = Read(file);
        int start = source.IndexOf("// ZoneMT准入：", StringComparison.Ordinal);
        Assert.True(start >= 0, "缺少ZoneMT拿锁后准入循环");
        int end = source.IndexOf("await MarkingHandshakeAsync", start, StringComparison.Ordinal);
        return source[start..end];
    }

    private static string Read(string file) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", file));
}
