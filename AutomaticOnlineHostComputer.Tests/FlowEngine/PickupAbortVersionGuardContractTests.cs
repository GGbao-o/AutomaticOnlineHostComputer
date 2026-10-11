namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 应急取消可能先于SDK返回普通异常。失效动作必须在冻结告警、登记人工任务、
/// 写设备或修改内存之前退出，不能仅靠finally中的清理守卫。
/// </summary>
public sealed class PickupAbortVersionGuardContractTests
{
    [Theory]
    [InlineData("Line1RearFlowEngine.cs", "CraneMotionTimeoutException ex", 2)]
    [InlineData("Line2RearFlowEngine.cs", "CraneMotionTimeoutException ex", 2)]
    [InlineData("Line1RearFlowEngine.cs", "PressureStopException ex", 1)]
    [InlineData("Line2RearFlowEngine.cs", "PressureStopException ex", 1)]
    [InlineData("Line1RearFlowEngine.cs", "LineNotStartedTimeoutException ex", 1)]
    [InlineData("Line2RearFlowEngine.cs", "LineNotStartedTimeoutException ex", 1)]
    public void Rear_failure_handlers_reject_invalidated_action_before_side_effects(
        string fileName, string exception, int expectedHandlers)
    {
        AssertFirstStatement(fileName, exception, expectedHandlers, "if (!operation.IsValid) return;");
    }

    [Theory]
    [InlineData("CraneMotionTimeoutException timeout")]
    [InlineData("PressureStopException pEx")]
    [InlineData("Exception ex")]
    public void Grinding_failure_handlers_reject_old_version_before_registering_manual_task(string exception)
    {
        AssertFirstStatement("GrindingFlowEngine.cs", exception, 2,
            "if (!IsGrindingActionCurrent(actionVersion)) return;", "private async Task ProcessWorkpieceAsync",
            "private async Task WaitForGrinderSignalAsync");
    }

    private static void AssertFirstStatement(string fileName, string exception, int expectedHandlers,
        string guard, string? startMarker = null, string? endMarker = null)
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", fileName));
        int offset = startMarker is null ? 0 : source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"{fileName} 未找到动作方法 {startMarker}");
        if (endMarker is not null)
        {
            int end = source.IndexOf(endMarker, offset, StringComparison.Ordinal);
            Assert.True(end > offset);
            source = source[..end];
        }
        // 只检查主动作的catch；内层参数准备/旁路显示异常使用不同策略。
        string marker = $"\n        catch ({exception})";
        int count = 0;
        while ((offset = source.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            int body = source.IndexOf('{', offset + marker.Length) + 1;
            Assert.True(body > 0);
            Assert.True(source[body..].TrimStart().StartsWith(guard, StringComparison.Ordinal),
                $"{fileName} 的第{count + 1}个 {marker} 必须先拒绝失效动作，再记录告警/人工任务或写状态");
            count++;
            offset = body;
        }
        Assert.Equal(expectedHandlers, count);
    }
}
