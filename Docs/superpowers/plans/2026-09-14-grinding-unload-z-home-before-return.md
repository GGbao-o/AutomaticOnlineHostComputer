# 研磨下料先 Z 回原点再返回 ST709 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让研磨天车下料到 ST710 后，先成功完成 Z 轴回原点，才并发发送研磨机下料完成信号与空载 XY 回 ST709。

**Architecture:** 仅调整 `UnloadFromGrinderAsync` 收尾段的异步调用顺序：将 `HomeZAsync` 变成必须完成的前置 `await`，保留后续两项的 `Task.WhenAll` 并发。既有异常 catch、研磨状态、Pending 工件和锁释放逻辑不改，由原异常分支处理前置失败或并发失败。

**Tech Stack:** C# / .NET、xUnit、Markdown 业务流程文档。

---

### Task 1: 收尾时序契约测试

**Files:**
- Modify: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingUnloadReturnSt709OverlapContractTests.cs`

- [x] **Step 1: 将现有三项并发契约改为先 Z 回原点的失败测试**

```csharp
int zHome = Index(method, "await crane.HomeZAsync(ct);");
int unloadDone = Index(method, "Task unloadDoneTask = grinder.Svc.SetUnloadDoneAsync(ct);");
int xyReturn = Index(method, "Task st709ReturnXyTask = crane.MoveAbsoluteAsync(ApplyOffsetX(st709X), ApplyOffsetY(st709Y), -1, ct: ct);");
int joined = Index(method, "await Task.WhenAll(unloadDoneTask, st709ReturnXyTask);");

Assert.True(zHome < unloadDone && unloadDone < joined && xyReturn < joined && joined < idle,
    "Z回原点必须成功返回后，才可并发发送下料完成信号和空载XY回ST709；两项成功后才可置Idle。");
Assert.DoesNotContain("Task zHomeTask", method, StringComparison.Ordinal);
```

- [x] **Step 2: 运行该测试并确认现有三项并发实现使其失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingUnloadReturnSt709OverlapContractTests --no-restore --verbosity:minimal`

Expected: FAIL，原因是当前代码尚有 `Task zHomeTask` 且三项 `Task.WhenAll`。

### Task 2: 调整研磨下料收尾顺序

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:2791-2809`

- [x] **Step 1: 先等待 Z 回原点，不成功时不创建后续两项任务**

```csharp
// Z回原点是研磨机释放和空载XY返回的前置条件；失败由既有下料异常分支暂停并保留Pending。
await crane.HomeZAsync(ct);
```

- [x] **Step 2: 在 Z 成功后才开始监控和动作账本步骤，并保留后续两项并发**

```csharp
Task unloadDoneTask = grinder.Svc.SetUnloadDoneAsync(ct);
Task st709ReturnXyTask = crane.MoveAbsoluteAsync(
    ApplyOffsetX(st709X), ApplyOffsetY(st709Y), -1, ct: ct);
await Task.WhenAll(unloadDoneTask, st709ReturnXyTask);
```

- [x] **Step 3: 更新日志、注释与动作确认文案，使其准确说明“先 Z、后两项并发”**

```csharp
Console.WriteLine($"[GrindingEngine] [{grinder.Name}] ⑪ Z回原点成功，开始下料完成(3s长信号)与天车空载回ST709并发... {workpiece.IdentityText}");
```

- [x] **Step 4: 运行时序契约测试并确认通过**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingUnloadReturnSt709OverlapContractTests --no-restore --verbosity:minimal`

Expected: PASS。

### Task 3: 同步核心业务文档与全量验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md:929`

- [x] **Step 1: 将 12.10 第 21 步改为前置 Z 回原点、后续两项并发，并写清前置失败不启动后二项**

```markdown
21. **先让天车5 Z 回原点并成功返回；随后持续3秒的研磨机下料完成信号与天车5空载 XY 回 ST709 研磨上料架3号位并发启动；两项调用均成功后继续。Z 回原点失败时不启动后二项，沿用下料异常暂停并保留 `Unloading` 与 `PendingWorkpiece`。**
```

- [x] **Step 2: 构建主程序并运行目标契约测试**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore --verbosity:minimal`

Expected: Build succeeded，0 errors。

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingUnloadReturnSt709OverlapContractTests --no-restore --verbosity:minimal`

Expected: Passed: 1，Failed: 0。

- [x] **Step 3: 检查本次受控文件的空白错误**

Run: `git diff --check -- Service/FlowEngine/GrindingFlowEngine.cs Docs/全线业务逻辑详解.md AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingUnloadReturnSt709OverlapContractTests.cs`

Expected: 无输出，退出码 0。
