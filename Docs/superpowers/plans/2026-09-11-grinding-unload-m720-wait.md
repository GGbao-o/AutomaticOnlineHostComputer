# 研磨下料持件后 M720 等待 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让研磨天车已持件且 Z 安全回升后，在 ST710 的 M720 暂时为0时原地等待而非暂停。

**Architecture:** 保持候选派发前的 M720 检查不变，仅将下料动作第⑦步前的二次检查改为专用等待方法。该方法只把有效读取的 M720=0 视为暂不可放料；MC64 未连接、读取异常和取消仍走现有异常暂停路径。等待期间保留当前动作、工件、`Unloading` 和 `_craneLock`，因此不可能派发第二趟研磨天车动作。

**Tech Stack:** C# / .NET 8、Mitsubishi MC 通讯、xUnit 源码契约测试。

---

### Task 1: 约束“持件后二次 M720”为等待语义

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingUnloadM720WaitContractTests.cs`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:1244-1273,2638-2642`

- [ ] **Step 1: 写失败的源码契约测试**

```csharp
[Fact]
public void Held_workpiece_waits_for_m720_instead_of_throwing_or_releasing_crane_lock()
{
    string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");

    Assert.Contains("WaitForGrindingUnloadRackReadyAsync", source, StringComparison.Ordinal);
    Assert.Contains("while (true)", source, StringComparison.Ordinal);
    Assert.Contains("await Task.Delay(_cfg.Grinding.SignalPollIntervalMs, ct)", source, StringComparison.Ordinal);
    Assert.DoesNotContain("ST710/M720动作前确认失败", source, StringComparison.Ordinal);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~GrindingUnloadM720WaitContractTests`

Expected: FAIL，因为等待方法尚不存在，动作前仍抛出异常。

- [ ] **Step 3: 实现最小等待方法并替换二次检查**

```csharp
private async Task WaitForGrindingUnloadRackReadyAsync(CancellationToken ct)
{
    int attempts = 0;
    while (true)
    {
        if (_mc64?.IsConnected != true)
            throw new InvalidOperationException("MC64未连接, ST710/M720状态未知");

        var result = await _mc64.ReadMAlignedWordAsync(720, 1, ct);
        int raw = result.IntValues.Length > 0 ? result.IntValues[0] : 0;
        if ((raw & 1) != 0)
            return;

        attempts++;
        if (attempts == 1 || attempts % 10 == 0)
            Console.WriteLine($"[GrindingEngine] 持件等待ST710/M720允许放料 raw=0x{raw:X4}");
        await Task.Delay(_cfg.Grinding.SignalPollIntervalMs, ct);
    }
}
```

在第⑦步调用该方法。读取 catch 中使 MC64 缓存失效后 rethrow，保留状态未知异常语义；不得在方法内写 M721、移动天车、释放 `_craneLock` 或改变研磨机状态。

- [ ] **Step 4: 运行聚焦测试确认通过**

Run: `dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~GrindingUnloadM720WaitContractTests`

Expected: PASS。

### Task 2: 同步核心流程文档并完整核验

**Files:**
- Modify: `Docs/全线业务逻辑详解.md:894-903`
- Modify: `Docs/02-通信协议手册/货叉及辅助PLC协议_三菱MC.md:285-292`

- [ ] **Step 1: 更新持件后二次确认的等待边界**

在研磨下料步骤中明确：候选派发前 M720=0 只跳过本轮；已持件、Z 安全后 M720=0 原地安全等待，不暂停、不释放天车锁；通信未知或取消仍暂停。

- [ ] **Step 2: 更新 MC64 协议说明**

将“去 ST710 前再检查一次”补充为“已持件后的二次检查为 M720=0 时等待，读取失败才进入异常暂停”。

- [ ] **Step 3: 执行验证**

Run: `dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-build --filter FullyQualifiedName~GrindingUnloadM720WaitContractTests --verbosity:minimal`

Expected: 1/1 PASS。

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore --verbosity:minimal`

Expected: 0 errors。

Run: `git diff --check -- Service/FlowEngine/GrindingFlowEngine.cs Docs/全线业务逻辑详解.md "Docs/02-通信协议手册/货叉及辅助PLC协议_三菱MC.md" AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingUnloadM720WaitContractTests.cs`

Expected: 当前任务新增的差异无空白错误。已有不相关脏改动不清理、不提交。

## 自检

- Spec coverage：Task 1 覆盖 M720=0 等待、通信未知/取消异常与锁保持；Task 2 覆盖核心流程和 MC64 协议文档。
- Placeholder scan：没有 TBD、TODO 或未定义的实施步骤。
- Type consistency：使用现有 `_mc64`、`ReadMAlignedWordAsync`、`_cfg.Grinding.SignalPollIntervalMs` 和既有外层 finally；不新增业务状态或锁。
- Git：用户要求不提交，本计划不执行 `git add` 或 `git commit`。
