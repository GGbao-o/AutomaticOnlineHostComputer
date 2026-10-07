using System;
using System.IO;
using Xunit;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 守住后天车下料人工结案的关键顺序：目标缓存/PLC握手成功前，不能清斜床工件或动作快照。
/// 这是源码契约测试；现场命令仍由集成测试/设备联调验证。
/// </summary>
public sealed class RearUnloadManualSettlementContractTests
{
    [Theory]
    [InlineData("Line1RearFlowEngine.cs", "ST019", "M817", "ST010", "M721")]
    [InlineData("Line2RearFlowEngine.cs", "ST020", "M818", "ST021", "M821")]
    public void TargetPendingSettlement_CompletesTargetEvidenceBeforeClearingBedLedger(
        string fileName, string firstStation, string firstCache, string secondStation, string secondSignal)
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Service", "FlowEngine", fileName));

        Assert.Contains("ResolveManualUnloadTargetPendingAsync", source, StringComparison.Ordinal);
        Assert.Contains(firstStation, source, StringComparison.Ordinal);
        Assert.Contains(firstCache, source, StringComparison.Ordinal);
        Assert.Contains(secondStation, source, StringComparison.Ordinal);
        Assert.Contains(secondSignal, source, StringComparison.Ordinal);

        int targetWork = source.IndexOf("OnBalancingRackPlaced", source.IndexOf("ResolveManualUnloadTargetPendingAsync", StringComparison.Ordinal), StringComparison.Ordinal);
        int clearBed = source.IndexOf("bed.Wp = null", source.IndexOf("ResolveManualUnloadTargetPendingAsync", StringComparison.Ordinal), StringComparison.Ordinal);
        int removeSnapshot = source.IndexOf("FlowActionManualRegistry.Remove(operationId)", source.IndexOf("ResolveManualUnloadTargetPendingAsync", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(targetWork >= 0 && clearBed > targetWork, "必须先补目标缓存/通知，后清斜床工件账本。");
        Assert.True(removeSnapshot > clearBed, "必须在斜床账本成功结案后才移除待人工确认快照。");
    }

    [Theory]
    [InlineData("Line1RearFlowEngine.cs", "1号线后天车上料")]
    [InlineData("Line2RearFlowEngine.cs", "2号线后天车上料")]
    public void LoadTargetPendingSettlement_UsesTheTargetBedIdentityAfterX11HasConsumedTheSource(string fileName, string scope)
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Service", "FlowEngine", fileName));
        int start = source.IndexOf("ResolveManualLoadTargetPending", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string method = source[start..];

        Assert.Contains($"action.FlowScope != \"{scope}\"", method, StringComparison.Ordinal);
        Assert.Contains("action.Ownership != FlowWorkpieceOwnership.AtTargetPendingHandoff", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveManualReservation", method[..method.IndexOf("    /// <summary>", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("bed.Wp is not { } wp", method, StringComparison.Ordinal);
        Assert.Contains("wp.IdentityText != action.WorkpieceIdentity", method, StringComparison.Ordinal);
        Assert.Contains("bed.St = SkewState.Loading", method, StringComparison.Ordinal);
        Assert.Contains("不自动补发CNC握手", method, StringComparison.Ordinal);
    }
}
