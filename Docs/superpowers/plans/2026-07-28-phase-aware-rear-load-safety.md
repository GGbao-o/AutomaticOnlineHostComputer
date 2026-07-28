# 阶段感知后天车上料安全改造 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 先将1/2号线后天车“中转架取料→斜床上料”改造成阶段感知动作：来源缓存预约而非提前删除，异常按物理事实统一暂停，动作退出后释放软件锁，并提供可恢复的人工对账状态。

**Architecture:** 新增独立的动作上下文与中转架缓存预约模型；后天车上料流程在每个物理命令前后更新上下文。局部 catch 只完成已证实安全的步骤补偿，统一 catch 只给动作下结论，finally 只完成取消、版本和租约锁收尾。1/2号线采用相同实现结构，保留各自的工位、斜床、PLC 与 Zone 差异。

**Tech Stack:** C# / .NET 8 WPF，`SemaphoreSlim`，`EmergencyActionLease`，现有 xUnit 静态契约测试，`Docs/全线业务逻辑详解.md`。

---

## 范围和后续拆分

本计划只实施公共基础与 **1/2号线后天车上料**。后天车下料、前天车、机械手1、M2/M3、研磨天车必须各自使用后续计划迁移；不得在本次顺带重写它们。

## 文件结构

- 新建 `Service/FlowEngine/FlowActionContext.cs`：动作阶段、命令确定性、工件归属、统一终止结论和不可变诊断快照。
- 新建 `Service/FlowEngine/TransferRackWorkpieceLedger.cs`：中转架工件、到达序号与来源预约的线程安全账本。
- 修改 `Service/FlowEngine/EmergencyActionLease.cs`：删除“运动超时永久保锁”语义；保留动作版本、取消与幂等持锁记录。
- 修改 `Service/FlowEngine/Line1RearFlowEngine.cs`：使用账本和动作上下文改造 `DoLoad`、应急信息和应急对账入口。
- 修改 `Service/FlowEngine/Line2RearFlowEngine.cs`：与1号线对称改造。
- 修改 `Service/FlowEngine/FlowStatusDisplaySnapshots.cs`：中转架快照展示“已预约/需人工确认”的软件状态，不把预约误报为空缓存。
- 新建 `AutomaticOnlineHostComputer.Tests/OperationalEvents/PhaseAwareRearLoadContractTests.cs`：验证新模型的静态行为契约。
- 修改 `AutomaticOnlineHostComputer.Tests/OperationalEvents/CraneMotionTimeoutContractTests.cs`：将“超时保锁”旧契约替换为“暂停后释放动作锁、保留账本”。
- 修改 `Docs/全线业务逻辑详解.md`：后天车上料的正常阶段、账本转移、异常矩阵和人工对账选项。

### Task 1: 动作上下文的纯模型与契约测试

**Files:**
- Create: `Service/FlowEngine/FlowActionContext.cs`
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/PhaseAwareRearLoadContractTests.cs`

- [ ] **Step 1: 写失败契约，锁定模型的阶段和结论名称**

```csharp
[Fact]
public void Flow_action_context_declares_phase_command_and_workpiece_facts()
{
    string source = Read("Service", "FlowEngine", "FlowActionContext.cs");
    Assert.Contains("enum FlowActionStep", source);
    Assert.Contains("PreCheck", source);
    Assert.Contains("MoveXYToSource", source);
    Assert.Contains("MagnetOnSent", source);
    Assert.Contains("TargetPendingHandoff", source);
    Assert.Contains("enum FlowCommandState", source);
    Assert.Contains("NotSent", source);
    Assert.Contains("SentAwaitingEvidence", source);
    Assert.Contains("ResponseUnknown", source);
    Assert.Contains("enum FlowWorkpieceOwnership", source);
    Assert.Contains("ReservedAtSource", source);
    Assert.Contains("OnCarrier", source);
    Assert.Contains("AtTargetPendingHandoff", source);
}

[Fact]
public void Flow_action_context_requires_manual_pause_for_unknown_physical_command()
{
    string source = Read("Service", "FlowEngine", "FlowActionContext.cs");
    Assert.Contains("PauseManual", source);
    Assert.Contains("MarkCommandResponseUnknown", source);
    Assert.Contains("LastKnownPosition", source);
    Assert.Contains("SourceCacheKey", source);
}
```

- [ ] **Step 2: 运行失败测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~PhaseAwareRearLoadContractTests`

Expected: FAIL；新文件和断言中的模型尚不存在。

- [ ] **Step 3: 实现最小且独立的动作事实模型**

```csharp
internal enum FlowActionStep
{
    PreCheck, SourceReserved, MoveXYToSource, FineTuneSource,
    MoveZDownToPick, MagnetOnSent, ConfirmPickup, HoldingWorkpiece,
    MoveZSafeWithWorkpiece, MoveXYToTarget, MoveZDownToPlace,
    MagnetOffSent, ConfirmPlaced, TargetPendingHandoff,
    NotifyDownstream, ReturnSafe, Completed
}

internal enum FlowCommandState { NotSent, SentAwaitingEvidence, Confirmed, ResponseUnknown }
internal enum FlowWorkpieceOwnership { ReservedAtSource, OnCarrier, AtTargetPendingHandoff, Completed, ManualConfirmation }
internal enum FlowActionDisposition { WaitRetry, PauseManual, Completed }

internal sealed class FlowActionContext
{
    // 保存OperationId、来源缓存键、来源/目标、工件、最后实际坐标、Step、CommandState、Ownership和Disposition。
    // BeginStep、MarkCommandSent、Confirm、MarkCommandResponseUnknown、PauseManual只能单调推进事实，不能回写为更安全的状态。
}
```

`FlowActionContext` 不持有设备连接、不发送命令、不释放锁；它只是每个动作的事实账本。

- [ ] **Step 4: 运行模型测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~PhaseAwareRearLoadContractTests`

Expected: PASS。

- [ ] **Step 5: 提交纯模型**

```bash
git add Service/FlowEngine/FlowActionContext.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/PhaseAwareRearLoadContractTests.cs
git commit -m "feat: add phase-aware flow action context"
```

### Task 2: 中转架缓存账本加入“预约而非提前删除”

**Files:**
- Create: `Service/FlowEngine/TransferRackWorkpieceLedger.cs`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:75-80, 180-198, 519-582, 1271-1310`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:74-79, 174-190, 504-566, 1242-1276`
- Modify: `Service/FlowEngine/FlowStatusDisplaySnapshots.cs:78-92`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/PhaseAwareRearLoadContractTests.cs`

- [ ] **Step 1: 写失败契约，禁止上料动作起点直接删除来源缓存**

```csharp
[Fact]
public void Rear_load_reserves_source_before_pick_and_commits_only_after_x11_confirmation()
{
    foreach (string file in new[] { "Line1RearFlowEngine.cs", "Line2RearFlowEngine.cs" })
    {
        string source = Read("Service", "FlowEngine", file);
        Assert.Contains("TryReserve", source);
        Assert.Contains("CommitPickup", source);
        Assert.Contains("ReleaseReservation", source);
        Assert.DoesNotContain("TryRemoveRackWorkpieceLocked(rs, out wp, out rackArrivalSeq)", source);
    }
}
```

- [ ] **Step 2: 运行失败测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~PhaseAwareRearLoadContractTests`

Expected: FAIL；当前 `DoLoad` 在开头调用 `TryRemoveRackWorkpieceLocked`。

- [ ] **Step 3: 实现线程安全中转架账本**

`TransferRackWorkpieceLedger` 在单一私有锁内维护工件、到达序号和预约：

```csharp
internal bool TryReserve(string rackCode, string operationId, out WorkpieceCache wp, out long arrivalSeq);
internal bool CommitPickup(string rackCode, string operationId, out WorkpieceCache wp, out long arrivalSeq);
internal bool ReleaseReservation(string rackCode, string operationId);
internal bool TryGet(string rackCode, out WorkpieceCache wp);
internal bool IsReserved(string rackCode, out string operationId);
internal void Enqueue(string rackCode, WorkpieceCache wp);
```

`TryReserve` 不删除工件；`CommitPickup` 仅在 X11 已确认吸住后删除工件和到达序号；`ReleaseReservation` 只解除同一 `OperationId` 的预约。将两条后端原 `_wps/_wpArrivalSeqs` 的所有读写改为调用账本，保留原有到达序号排序和人工写缓存保护。

扩展 `TransferRackSnapshot`，新增 `bool Reserved` 与 `string ReservationText`；快照必须继续显示预约中的工件身份，不能让 UI 显示“物理有板但软件为空”。

- [ ] **Step 4: 在上料动作中接入预约与确认取走**

在 `DoLoad` 开头调用 `TryReserve(rs, operationId, out wp, out rackArrivalSeq)`；X11=1 后立即调用 `CommitPickup(rs, operationId, out _, out _)`。任何 X11 未确认、动作前失败或动作取消都仅调用 `ReleaseReservation`，不再恢复一个此前已删除的缓存。

- [ ] **Step 5: 运行测试和构建**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "FullyQualifiedName~PhaseAwareRearLoadContractTests|FullyQualifiedName~RearLoadX11"`

Expected: PASS。

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: 0 errors。

- [ ] **Step 6: 提交账本改造**

```bash
git add Service/FlowEngine/TransferRackWorkpieceLedger.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Service/FlowEngine/FlowStatusDisplaySnapshots.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/PhaseAwareRearLoadContractTests.cs
git commit -m "feat: reserve transfer rack workpieces during rear load"
```

### Task 3: 后天车上料改为阶段感知异常结论

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:1271-1815`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:1242-1784`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/PhaseAwareRearLoadContractTests.cs`

- [ ] **Step 1: 写失败契约，锁定三个层次的职责**

```csharp
[Fact]
public void Rear_load_has_step_context_and_single_manual_pause_path()
{
    foreach (string file in new[] { "Line1RearFlowEngine.cs", "Line2RearFlowEngine.cs" })
    {
        string source = Read("Service", "FlowEngine", file);
        Assert.Contains("new FlowActionContext", source);
        Assert.Contains("context.BeginStep(FlowActionStep.MoveXYToSource", source);
        Assert.Contains("context.BeginStep(FlowActionStep.MagnetOnSent", source);
        Assert.Contains("context.BeginStep(FlowActionStep.NotifyDownstream", source);
        Assert.Contains("HandleRearLoadFailure", source);
        Assert.Contains("FinalizeRearLoadAction", source);
    }
}
```

- [ ] **Step 2: 运行失败测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~PhaseAwareRearLoadContractTests`

Expected: FAIL；现有流程仅有 `LoadHandshakeState` 和分散 catch。

- [ ] **Step 3: 在两条线的 `DoLoad` 中逐命令更新上下文**

在每一个命令前后采用固定模式：

```csharp
context.BeginStep(FlowActionStep.MoveXYToSource, targetX, targetY, null);
context.MarkCommandSent();
await cr.MoveAbsoluteAsync(targetX, targetY, -1, ct: operation.Token);
context.Confirm(lastKnownPosition: await cr.ReadStatusAsync(operation.Token));
```

至少覆盖：来源 XY、来源微调、取料 Z、充磁、X11、持件安全 Z、目标 XY、目标微调、放料 Z、退磁、目标安全 Z、CNC 上料通知、回零。所有后续调用使用 `operation.Token`；每次 await 后调用 `operation.ThrowIfInvalid()`，防止应急取消后的旧任务继续写命令。

- [ ] **Step 4: 引入统一失败处理和纯 finally 收尾**

新增两条线各自的私有方法：

```csharp
private void HandleRearLoadFailure(SkewCtx bed, FlowActionContext context,
    EmergencyActionLease operation, Exception exception, string rackCode);

private void FinalizeRearLoadAction(SkewCtx bed, FlowActionContext context,
    EmergencyActionLease operation);
```

`HandleRearLoadFailure` 只能按 `context.CommandState`、`context.Ownership` 和 `context.Step` 设置 `WaitRetry` / `PauseManual`、状态、任务牌、报警和人工说明。它不移动、不充退磁、不写 CNC。

`finally` 只调用 `FinalizeRearLoadAction`：完成旧动作、清 busy、按 `operation.TryRelease` 释放本动作登记的 `RearCrane`、`ZoneMT`、`ZoneTS` 及分流锁。删除 `RequiresManualSafetyRecovery` 分支中的长期保锁逻辑；不在 finally 中恢复缓存、清 `bed.Wp`、做 X 退避或写设备命令。

- [ ] **Step 5: 处理局部补偿的唯一例外**

将 Z 下降的 `PressureStopException` 处理集中为“仅向下接近时读取坐标”。实际 Z 在目标 `±_cfg.AbsMove.Tolerance` 内时标记本步骤确认；否则 `context.MarkCommandResponseUnknown` 并抛给统一失败处理。XY、Z 上升、回零出现下压不得继续。

三次 X11=0 的局部补偿必须仅在退磁成功、Z 安全确认后解除来源预约；动作仍调用统一失败处理暂停，不进入下一轮自动取料。充磁/退磁/PLC/CNC 响应未知直接进入统一暂停，不执行相反命令猜测现场结果。

- [ ] **Step 6: 运行定向测试和构建**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "FullyQualifiedName~PhaseAwareRearLoadContractTests|FullyQualifiedName~RearLoadX11|FullyQualifiedName~CraneMotionTimeout"`

Expected: PASS。

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: 0 errors。

- [ ] **Step 7: 提交阶段感知上料流程**

```bash
git add Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/PhaseAwareRearLoadContractTests.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/CraneMotionTimeoutContractTests.cs
git commit -m "feat: make rear load failures phase-aware"
```

### Task 4: 超时锁语义、人工恢复与页面证据

**Files:**
- Modify: `Service/FlowEngine/EmergencyActionLease.cs:33-83`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:1004-1116`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:984-1096`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/CraneMotionTimeoutContractTests.cs`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/PhaseAwareRearLoadContractTests.cs`

- [ ] **Step 1: 替换旧超时保锁契约**

```csharp
[Fact]
public void Rear_crane_timeout_pauses_and_releases_action_locks_but_keeps_manual_ledger_state()
{
    string lease = Read("Service", "FlowEngine", "EmergencyActionLease.cs");
    Assert.DoesNotContain("HoldForManualSafetyRecovery", lease);
    foreach (string file in new[] { "Line1RearFlowEngine.cs", "Line2RearFlowEngine.cs" })
    {
        string source = Read("Service", "FlowEngine", file);
        Assert.Contains("PauseManual", source);
        Assert.Contains("FinalizeRearLoadAction", source);
        Assert.Contains("TryRelease(\"RearCrane\"", source);
        Assert.DoesNotContain("当前安全占用", source);
    }
}
```

- [ ] **Step 2: 运行失败测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "FullyQualifiedName~CraneMotionTimeoutContractTests|FullyQualifiedName~PhaseAwareRearLoadContractTests"`

Expected: FAIL；当前 `EmergencyActionLease` 仍有 `HoldForManualSafetyRecovery`。

- [ ] **Step 3: 实现“释放锁、保留账本”的超时语义**

从 `EmergencyActionLease` 删除 `RequiresManualSafetyRecovery`、原因字段和 `HoldForManualSafetyRecovery`；不改变其取消、版本、完成和 `TryRelease` 幂等语义。

后天车运动超时统一标记 `context.PauseManual`，任务牌显示最后 XYZ、目标、未到位轴和“软件锁已释放，工件账本待人工对账”。动作 finally 释放登记锁。不能再由斜床应急依赖“锁仍被占用”判断是否可恢复。

- [ ] **Step 4: 让斜床应急按工件结果对账，而不是无条件清空**

将现有“清斜床”操作拆成内部四个明确结果：

```csharp
internal enum RearLoadManualResolution
{
    StillAtSource,
    ConfirmedOnCarrier,
    ConfirmedAtBed,
    RemovedOrScrapped
}
```

`StillAtSource` 解除来源预约、清 `bed.Wp` 和任务牌；`ConfirmedOnCarrier` 保留人工任务并拒绝恢复引擎；`ConfirmedAtBed` 保留/补齐 `bed.Wp` 并设对应斜床等待人工继续处理；`RemovedOrScrapped` 记录工件作废后才清身份。设备侧寄存器清零仍在人工确认后执行；失败时不能隐式清软件账本。

- [ ] **Step 5: 运行定向测试和完整构建**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "FullyQualifiedName~CraneMotionTimeoutContractTests|FullyQualifiedName~PhaseAwareRearLoadContractTests|FullyQualifiedName~RearLoadX11"`

Expected: PASS。

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: 0 errors。

- [ ] **Step 6: 提交超时和人工对账修改**

```bash
git add Service/FlowEngine/EmergencyActionLease.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/CraneMotionTimeoutContractTests.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/PhaseAwareRearLoadContractTests.cs
git commit -m "feat: reconcile rear load timeout workpieces manually"
```

### Task 5: 业务主文档、全量验证与迁移边界

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/superpowers/plans/2026-07-28-phase-aware-rear-load-safety.md`

- [ ] **Step 1: 更新后天车上料业务主文档**

在“后天车上料”章节增加：来源预约→X11确认后移除来源缓存→目标待交接→斜床持件的账本表；补充动作前等待、允许下压、必须暂停、finally 释放锁、四种人工对账结果。删除“运动超时保留区域锁直到斜床应急”的旧描述。

- [ ] **Step 2: 添加文档契约检查**

```csharp
[Fact]
public void Business_document_describes_rear_load_reservation_and_manual_reconciliation()
{
    string doc = Read("Docs", "全线业务逻辑详解.md");
    Assert.Contains("来源预约", doc);
    Assert.Contains("X11=1", doc);
    Assert.Contains("StillAtSource", doc);
    Assert.Contains("软件锁已释放", doc);
}
```

- [ ] **Step 3: 执行全量验证**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore`

Expected: all tests pass。

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: 0 errors。

Run: `git diff --check`

Expected: no whitespace errors。

- [ ] **Step 4: 提交文档与验证完成状态**

```bash
git add Docs/全线业务逻辑详解.md Docs/superpowers/plans/2026-07-28-phase-aware-rear-load-safety.md AutomaticOnlineHostComputer.Tests/OperationalEvents/PhaseAwareRearLoadContractTests.cs
git commit -m "docs: describe phase-aware rear load recovery"
```

## 计划自审

- 设计中的阶段上下文、命令确定性、来源预约、局部/统一/finally职责、暂停释放锁、人工四种结果、后天车上料和文档均有对应任务。
- 本计划不迁移后天车下料、前天车、机械手、动平衡或研磨，避免把多个独立风险域混入首批改造。
- 后续迁移必须复用 `FlowActionContext` 与账本规则，并另建计划和回归矩阵。
