# Grinding Target Parameter Parallel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在研磨天车5持件去目标研磨机时并行执行XY绝对移动和“最终参数→数据传输完成”链，三者成功后才微调，并在该准备窗口异常时自动清目标机参数和上位机输出后暂停。

**Architecture:** 保持`ProcessWorkpieceAsync`的取料、M731、持件状态、后续Z下降和交接流程不变。把目标机准备拆为XY任务与参数链任务，通过`Task.WhenAll`汇合；为参数链开始后的准备/微调窗口增加局部自动清理辅助方法，清理使用独立取消令牌，失败只补充日志而不覆盖原异常，由既有外层异常分支保留`PendingWorkpiece`并暂停。

**Tech Stack:** C#、.NET 8、Task并发、CancellationToken、WPF研磨流程、xUnit源码合同测试、OperationalLog。

---

### Task 1: 建立目标位并行准备的合同测试

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingTargetParameterParallelContractTests.cs`

- [x] **Step 1: 写顺序和异常清理的红灯合同测试**

创建以下测试，从`ProcessWorkpieceAsync`中截取“目标研磨机准备”到第一次`MoveZDownToPlace`之间的代码范围：

```csharp
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

    Assert.True(xyTask >= 0 && parameterTask > xyTask);
    Assert.True(sendParams > parameterTask && dataDone > sendParams);
    Assert.True(join > dataDone && fineTune > join);
}

[Fact]
public void Request_load_and_door_wait_follow_successful_target_fine_tune()
{
    string block = ReadTargetPreparationBlock();
    int fineTune = block.IndexOf("XAbsFineTuneHelper.VerifyAndFineTuneAsync", StringComparison.Ordinal);
    int requestLoad = block.IndexOf("IsRequestLoadAsync", StringComparison.Ordinal);
    int doorOpen = block.IndexOf("IsDoorOpenAsync", StringComparison.Ordinal);

    Assert.True(fineTune >= 0 && requestLoad > fineTune && doorOpen > requestLoad);
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
```

`ReadTargetPreparationBlock`以`// ── ⑦ XY 移到研磨机位置`为起点、`action.BeginStep(FlowActionStep.MoveZDownToPlace`为终点。`ReadFlowSource`读取仓库根目录下`Service/FlowEngine/GrindingFlowEngine.cs`。

- [x] **Step 2: 运行测试确认红灯**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingTargetParameterParallelContractTests --no-restore -clp:ErrorsOnly`

Expected: FAIL，因为现有代码没有`xyMoveTask`、`parameterTransferTask`、`Task.WhenAll`或准备窗口自动清理方法。

### Task 2: 实现并行准备与自动清参数

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:1835-1890`
- Test: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingTargetParameterParallelContractTests.cs`

- [x] **Step 1: 实现局部清理辅助方法**

在`GrindingFlowEngine`中新增：

```csharp
private async Task ClearTargetGrinderPreparationAsync(
    GrinderContext grinder, WorkpieceCache wp, Exception originalException)
{
    const string phase = "自动清理研磨机目标位准备参数";
    Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⚠ {phase}开始: 工件={wp.IdentityText}; 原始异常={originalException.Message}");
    OperationalLog.Warn(phase, "XY/参数链/微调异常，清目标机参数和上位机输出后暂停；Pending保留",
        ("研磨机", grinder.StationCode), ("工件", wp.IdentityText), ("原始异常", originalException.Message),
        ("后续处理", "ClearEmergencyRegistersAsync；Pending保留；由外层异常暂停"));
    try
    {
        await grinder.Svc!.ClearEmergencyRegistersAsync(CancellationToken.None);
        Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ✓ {phase}成功: 参数及上位机输出已清零; Pending保留");
        OperationalLog.Info(phase, "参数及上位机输出已清零，Pending保留，等待外层异常暂停",
            ("研磨机", grinder.StationCode), ("工件", wp.IdentityText));
    }
    catch (Exception clearEx)
    {
        Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⚠ {phase}失败: 原始异常={originalException.Message}; 清理异常={clearEx.Message}; Pending保留");
        OperationalLog.Warn(phase, "自动清理失败，保留原始异常语义并暂停",
            ("研磨机", grinder.StationCode), ("工件", wp.IdentityText),
            ("原始异常", originalException.Message), ("清理异常", clearEx.Message), ("后续处理", "Pending保留；由外层异常暂停"));
    }
}
```

该辅助方法不改`grinder.PendingWorkpiece`、不改FIFO、不写`State`、不吞掉原异常。外层既有`catch (Exception)`会因`holdingWorkpiece || magnetOn`保留Pending并暂停。

- [x] **Step 2: 实现并行汇合和微调清理调用**

在取得`gx/gy/gz`后，替换当前串行XY、参数写入和数据完成信号的代码。增加局部异步参数链，保持参数先于数据完成信号：

```csharp
Task xyMoveTask = crane.MoveAbsoluteAsync(ApplyOffsetX(gx), ApplyOffsetY(gy), -1, ct: ct);
Task parameterTransferTask = SendTargetGrinderParametersAsync();

try
{
    await Task.WhenAll(xyMoveTask, parameterTransferTask);
}
catch (Exception ex)
{
    await ClearTargetGrinderPreparationAsync(grinder, wp, ex);
    throw;
}

async Task SendTargetGrinderParametersAsync()
{
    await grinder.Svc!.SendRollerParamsAsync(wp.Diameter, wp.BoreType, wp.Length, ct);
    await grinder.Svc.SetDataSentDoneAsync(ct);
}
```

在启动两个任务前同步登记`MoveXYToTarget`和`NotifyDownstream`动作步骤；仅在`Task.WhenAll`成功后登记“XY到位、参数与数据完成信号成功”。用如下结构包裹目标位微调；只有微调成功后才继续请求上料和门开：

```csharp
try
{
    await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
        crane, _cfg, _cfg.Grinding.CraneNo, grinder.StationCode, $"研磨天车-{grinder.StationCode}上料放入前",
        reporter: _exceptionReporter,
        failureContext: OperationalEventContextFactory.FineTuneFailure(
            scope: "研磨", engine: "研磨引擎", deviceNo: _cfg.Grinding.CraneNo.ToString(), station: grinder.StationCode,
            actionStage: $"{grinder.StationCode}上料放入前XY微调", workpiece: wp,
            source: OperationalEventContextFactory.ConfirmedLocation("ST709", "本物理周期来源"),
            target: OperationalEventContextFactory.ConfirmedLocation(grinder.StationCode, "已选研磨机目标"),
            owner: "研磨天车", targetZ: EvidenceValue<int>.Unavailable("业务在微调后调用ApplyOffsetZ计算研磨机装料目标"),
            physicalPhase: OperationalEventContextFactory.PlacementBeforeZDown(magnetOn, holdingWorkpiece, physicalTracker, "ST709取料X11=1且研磨机已请求上料", "天车/研磨机上方")),
        actionId: actionId, physicalTracker: physicalTracker, ct: ct);
}
catch (Exception ex)
{
    await ClearTargetGrinderPreparationAsync(grinder, wp, ex);
    throw;
}
```

把`IsRequestLoadAsync`和`IsDoorOpenAsync`等待块移动到微调成功之后。

- [x] **Step 3: 运行合同测试确认绿灯**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingTargetParameterParallelContractTests --no-restore -clp:ErrorsOnly`

Expected: PASS，0 failed。

### Task 3: 更新业务文档并进行回归验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md:研磨上料流程章节`
- Test: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingTargetParameterParallelContractTests.cs`

- [x] **Step 1: 更新研磨天车装入研磨机流程说明**

将目标机段更新为以下事实：

```markdown
1. 持件离开ST709安全Z后，天车去目标研磨机的XY绝对移动与最终直径、孔型、长度参数写入并行开始。
2. 参数写入成功后立即发送数据传输完成3秒信号；XY、参数、数据完成信号均成功后才进行目标X绝对编码器微调。
3. 微调成功后才等待请求上料和门打开，再执行Z下降、锁紧、退磁、Z安全和上料完成。
4. XY、参数链或微调异常时，自动清该目标机的参数和上位机输出；工件Pending保留、研磨引擎暂停、不回FIFO。
5. 微调成功后的物理交接异常维持既有分阶段人工确认语义。
```

- [x] **Step 2: 运行本需求测试集**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~GrindingTargetParameterParallelContractTests|FullyQualifiedName~GrindingM731OverlapContractTests|FullyQualifiedName~GrindingManualSettlementContractTests" --no-restore -clp:ErrorsOnly`

Expected: PASS；若既有M731配置文本契约因用户JSON修改失败，单独记录，不修改配置默认值。

- [x] **Step 3: 构建主项目**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore -clp:ErrorsOnly`

Expected: Build succeeded，0 errors。

- [x] **Step 4: 最终检查**

确认`Task.WhenAll`位于微调前，请求上料/门开位于微调后，目标准备/微调异常均调用清理辅助方法；确认清理辅助方法不清FIFO和Pending。运行`git diff --check`只检查本需求文件；不提交工作区已有的其他用户修改。
