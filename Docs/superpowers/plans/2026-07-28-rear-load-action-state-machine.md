# 后天车上料业务动作状态机 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 1/2 号线后天车上料以单一动作账本处理每一步物理承诺、异常暂停和异常监控详情。

**Architecture:** 扩展现有 `FlowActionContext` 和中转架账本，而不改变 PLC 地址及正常设备动作顺序。后天车上料在每个命令和握手点推进动作账本；统一异常出口将账本快照映射到既有结构化异常系统。第一阶段只改 `DoLoad`，不改其它搬运引擎。

**Tech Stack:** C#、.NET、xUnit、WPF MVVM、既有 `OperationalEventReporter`。

---

### Task 1: 动作账本的可验证事实模型

**Files:**
- Modify: `Service/FlowEngine/FlowActionContext.cs`
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/FlowActionContextTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void SentCommandCannotBecomeWaitRetry()
{
    var action = CreateAction();
    action.BeginStep(FlowActionStep.MoveXYToSource, new(1, 2, 0), "去来源");
    action.MarkCommandSent();
    Assert.Throws<InvalidOperationException>(() => action.WaitRetry("断线"));
}

[Fact]
public void SnapshotKeepsActualIdentityPositionAndHeldLocks()
{
    var action = CreateAction("E26072224/8");
    action.RegisterLock("RearCrane");
    action.RecordFeedback(new(10, 20, 30), x11: true, magnetOn: true, "X11确认");
    var snapshot = action.Snapshot();
    Assert.Equal("E26072224/8", snapshot.WorkpieceIdentity);
    Assert.Contains("RearCrane", snapshot.HeldLocks);
    Assert.Equal(30, snapshot.LastKnownPosition.Z);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests --filter FullyQualifiedName~FlowActionContextTests --no-restore`

Expected: FAIL，缺少快照、锁及传感器事实接口。

- [ ] **Step 3: 实现最小模型**

新增不可变 `FlowActionSnapshot`、`FlowActionPauseDisposition` 和 `RegisterLock/TryReleaseLock/RecordFeedback/Snapshot`。构造函数使用真实工件身份；`PauseManual` 不覆盖已知 `OnCarrier` 或 `AtTargetPendingHandoff` 归属。

- [ ] **Step 4: 运行定向测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests --filter FullyQualifiedName~FlowActionContextTests --no-restore`

Expected: PASS。

### Task 2: 中转架预约在未知异常中保持占用

**Files:**
- Modify: `Service/FlowEngine/TransferRackWorkpieceLedger.cs`
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/TransferRackWorkpieceLedgerTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void UnknownActionReservationCannotBePickedByAnotherOperation()
{
    var ledger = LedgerWith("ST105", Workpiece("E26072224/8"));
    Assert.True(ledger.TryReserve("ST105", "A", out _, out _));
    ledger.HoldForManualResolution("ST105", "A");
    Assert.False(ledger.TryReserve("ST105", "B", out _, out _));
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests --filter FullyQualifiedName~TransferRackWorkpieceLedgerTests --no-restore`

Expected: FAIL，缺少人工确认占用状态。

- [ ] **Step 3: 实现最小账本扩展**

增加预约状态 `Active/ManualResolution` 和 `HoldForManualResolution`；快照显示占用动作号与人工确认原因。只有显式人工处置或已知安全的未取料失败才解除预约。

- [ ] **Step 4: 运行定向测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests --filter FullyQualifiedName~TransferRackWorkpieceLedgerTests --no-restore`

Expected: PASS。

### Task 3: 迁移1号线后天车上料每一步

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/RearLoadX11FailureContractTests.cs`

- [ ] **Step 1: 写失败静态/行为契约**

断言 `DoLoad` 用 `wp.IdentityText` 创建上下文，并在来源XY、微调、取料Z、充磁、每次X11、抬Z、目标XY、目标微调、目标Z、退磁、CNC通知、回位、完成时推进步骤；未知异常调用 `HoldForManualResolution` 而不是 `ReleaseReservation`。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests --filter FullyQualifiedName~RearLoadX11FailureContractTests --no-restore`

Expected: FAIL，当前仅有局部步骤且异常释放预约。

- [ ] **Step 3: 实现迁移**

将 `DoLoad` 创建上下文移到成功预约后；每个物理命令前调用 `BeginStep`，调用开始后 `MarkCommandSent`，反馈后写入真实位置/X11/磁铁。将 `mag`、`holdingWorkpiece` 仅保留为局部补偿辅助，最终异常结论只读 `FlowActionSnapshot`。普通读取失败在未发送命令时标记等待；命令未知、运动超时、非容许下压时保留预约、暂停并创建任务牌；放料后握手失败标记目标待交接。finally 仅结束租约和释放登记锁。

- [ ] **Step 4: 运行定向测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests --filter FullyQualifiedName~RearLoadX11FailureContractTests --no-restore`

Expected: PASS。

### Task 4: 对称迁移2号线后天车上料

**Files:**
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/RearLoadX11FailureContractTests.cs`

- [ ] **Step 1: 扩展失败契约**

为2号线断言与1号线相同的步骤顺序、未知预约保持、任务牌和暂停结论；只允许设备号、工位和路线名称不同。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests --filter FullyQualifiedName~RearLoadX11FailureContractTests --no-restore`

Expected: FAIL，2号线未完整迁移。

- [ ] **Step 3: 对称实现**

把 `Line1RearFlowEngine.DoLoad` 已验证的账本推进和统一异常结论逐点移植到 `Line2RearFlowEngine.DoLoad`，保留2号线已有的斜床工位、坐标和协议差异，不复制1号线站号常量。

- [ ] **Step 4: 运行定向测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests --filter FullyQualifiedName~RearLoadX11FailureContractTests --no-restore`

Expected: PASS。

### Task 5: 异常监控显示动作账本

**Files:**
- Modify: `Service/OperationalEvents/OperationalEventContext.cs`
- Modify: `Service/OperationalEvents/OperationalEventFormatter.cs`
- Modify: `Service/FlowEngine/OperationalEventContextFactory.cs`
- Modify: `Presentation/ViewModels/Home/OperationalEventRowViewModel.cs`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/FlowActionSnapshotEventTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void FormatterShowsActionStepOwnershipAndManualInstruction()
{
    var text = Format(EventWithActionSnapshot(
        step: "XY到中转架", ownership: "来源位已占用",
        pause: "人工确认：板仍在ST105/天车持件/已在目标/人工移走"));
    Assert.Contains("步骤：XY到中转架", text);
    Assert.Contains("来源位已占用", text);
    Assert.Contains("人工确认", text);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests --filter FullyQualifiedName~FlowActionSnapshotEventTests --no-restore`

Expected: FAIL，事件上下文没有动作账本字段。

- [ ] **Step 3: 实现旁路映射**

在结构化事件上下文增加可空动作快照；工厂将后天车异常的动作账本复制进事件，不读取设备、不影响异常判断。格式化器和右侧详情把步骤、命令状态、归属、位置、锁和人工指引展示为一组只读证据。

- [ ] **Step 4: 运行定向测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests --filter FullyQualifiedName~FlowActionSnapshotEventTests --no-restore`

Expected: PASS。

### Task 6: 文档、全量回归与复审

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/异常监控页面使用手册.md`
- Modify: `Docs/03-设备交互流程/后天车与斜床交互流程.md`

- [ ] **Step 1: 文档与代码同步**

明确“第一阶段仅后天车上料已完整迁移”；列出动作账本的每一步、预约/提交/人工占用、catch/finally职责和异常页面字段。删除把全线旧 catch 路径描述为已迁移的表述。

- [ ] **Step 2: 运行定向与全量测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests --no-restore`

Expected: PASS，且包含本计划新增的动作账本测试。

- [ ] **Step 3: 构建生产工程**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore --verbosity minimal`

Expected: 0 error。

- [ ] **Step 4: 执行一致性复审**

Run: `git diff --check`

Expected: 无空白错误；人工复核两线 `DoLoad` 的所有 `MoveAbsoluteAsync`、`MagnetOnAsync`、`MagnetOffAsync`、`ReadXBitAsync` 和握手调用均有对应账本步骤。
