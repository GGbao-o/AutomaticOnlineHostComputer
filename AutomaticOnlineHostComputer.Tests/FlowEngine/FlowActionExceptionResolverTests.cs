using System.IO;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class FlowActionExceptionResolverTests
{
    [Fact]
    public void Sent_command_response_unknown_requires_manual_pause_and_persists_snapshot()
    {
        const string operationId = "exception-resolver-sent";
        FlowActionManualRegistry.Remove(operationId);
        var action = CreateAction(operationId);
        action.BeginStep(FlowActionStep.MagnetOnSent, new FlowActionPosition(10, 20, 30), "充磁命令");
        action.MarkCommandSent();

        FlowActionExceptionConclusion result = FlowActionExceptionResolver.Resolve(action,
            new IOException("PLC响应丢失"));

        Assert.Equal(FlowActionExceptionConclusion.PauseLineManual, result);
        Assert.Equal(FlowCommandState.ResponseUnknown, action.CommandState);
        Assert.True(action.ManualConfirmationRequired);
        Assert.True(FlowActionManualRegistry.TryGet(operationId, out FlowActionSnapshot snapshot));
        Assert.Equal(FlowActionDisposition.PauseSourceReserved, snapshot.Disposition);
        FlowActionManualRegistry.Remove(operationId);
    }

    [Fact]
    public void Unsent_exception_is_the_only_case_that_can_wait_retry()
    {
        const string operationId = "exception-resolver-unsent";
        FlowActionManualRegistry.Remove(operationId);
        var action = CreateAction(operationId);

        FlowActionExceptionConclusion result = FlowActionExceptionResolver.Resolve(action,
            new IOException("发送前连接失败"));

        Assert.Equal(FlowActionExceptionConclusion.WaitRetry, result);
        Assert.Equal(FlowActionDisposition.WaitRetry, action.Disposition);
        Assert.False(action.ManualConfirmationRequired);
        Assert.False(FlowActionManualRegistry.TryGet(operationId, out _));
    }

    private static FlowActionContext CreateAction(string operationId) =>
        new(operationId, "1号线前天车取料-打号-中转", "前天车1", "E26072224/8", "ST105", "ST107", "ST105");
}
