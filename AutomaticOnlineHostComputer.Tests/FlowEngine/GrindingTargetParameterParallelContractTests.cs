namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class GrindingTargetParameterParallelContractTests
{
    [Fact]
    public void Target_xy_and_parameter_chain_run_in_parallel_before_fine_tune()
    {
        string block = ReadTargetPreparationBlock();
        int xyTask = block.IndexOf("Task xyMoveTask =", StringComparison.Ordinal);
        int parameterTask = block.IndexOf("Task parameterTransferTask =", StringComparison.Ordinal);
        int sendParams = block.IndexOf("SendRollerParamsAsync", StringComparison.Ordinal);
        int dataDone = block.IndexOf("SetDataSentDoneAsync", StringComparison.Ordinal);
        int join = block.IndexOf("await Task.WhenAll(xyMoveTask, parameterTransferTask);", StringComparison.Ordinal);
        int fineTune = block.IndexOf("XAbsFineTuneHelper.VerifyAndFineTuneAsync", StringComparison.Ordinal);

        Assert.True(xyTask >= 0 && parameterTask > xyTask, "目标XY任务和参数链任务必须在同一准备窗口创建。");
        Assert.True(sendParams >= 0 && dataDone > sendParams, "参数链必须先下发参数，再发送数据传输完成信号。");
        Assert.True(join > parameterTask && fineTune > join, "XY和参数链均成功后才可进行目标位微调。");
    }

    [Fact]
    public void Request_load_and_door_wait_follow_successful_target_fine_tune()
    {
        string block = ReadTargetPreparationBlock();
        int fineTune = block.IndexOf("XAbsFineTuneHelper.VerifyAndFineTuneAsync", StringComparison.Ordinal);
        int requestLoad = block.IndexOf("IsRequestLoadAsync", StringComparison.Ordinal);
        int doorOpen = block.IndexOf("IsDoorOpenAsync", StringComparison.Ordinal);

        Assert.True(fineTune >= 0 && requestLoad > fineTune && doorOpen > requestLoad,
            "请求上料和门开必须在目标位微调成功后才等待。");
    }

    [Fact]
    public void Preparation_or_fine_tune_failure_clears_target_parameters_and_outputs_but_keeps_pending()
    {
        string source = ReadFlowSource();

        Assert.Contains("ClearTargetGrinderPreparationAsync", source, StringComparison.Ordinal);
        Assert.Contains("await grinder.Svc!.ClearEmergencyRegistersAsync(CancellationToken.None)", source, StringComparison.Ordinal);
        Assert.Contains("自动清理研磨机目标位准备参数", source, StringComparison.Ordinal);
        Assert.Contains("Pending保留", source, StringComparison.Ordinal);
    }

    private static string ReadTargetPreparationBlock()
    {
        string source = ReadFlowSource();
        int start = source.IndexOf("// ── ⑦ 目标XY与最终加工参数并行准备", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到研磨天车目标研磨机准备源码起点。");
        int end = source.IndexOf("action.BeginStep(FlowActionStep.MoveZDownToPlace", start, StringComparison.Ordinal);
        Assert.True(end > start, "未找到研磨天车目标研磨机准备源码终点。");
        return source[start..end];
    }

    private static string ReadFlowSource() =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", "GrindingFlowEngine.cs"));
}
