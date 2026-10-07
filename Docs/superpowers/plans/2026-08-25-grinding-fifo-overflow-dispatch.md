# 研磨上料 FIFO 超限派发保护 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 当研磨上料 FIFO 超过可配置上限时，在天车5上料动作启动前安全软暂停研磨引擎并提示人工处理。

**Architecture:** 将容量判断置于 `GrindingFlowEngine.TryDispatchLoadAsync` 的 `_craneLock` 与 `_grindingDispatchGate` 双重保护内。该分支在 FIFO 出队与动作创建前锁存 `_paused` 并发送 `OnSafetyAlarm`，由既有 `finally` 释放天车锁；配置模型和配置页共同维护默认上限 2。

**Tech Stack:** C#/.NET 8、WPF、xUnit 源码契约测试、JSON 配置。

---

### Task 1: 增加可配置 FIFO 上限

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs`
- Modify: `Config/motion_settings.json`
- Modify: `Presentation/ViewModels/Config/ConfigPageViewModel.cs`
- Modify: `Views/Config/ConfigPageView.xaml`
- Test: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingFifoOverflowDispatchContractTests.cs`

- [x] **Step 1: 写入失败的配置契约测试**

```csharp
Assert.Contains("public int MaxFifoCount { get; set; } = 2;", config);
Assert.Contains("\"maxFifoCount\": 2", json);
Assert.Contains("public int GrindingMaxFifoCount", viewModel);
Assert.Contains("Text=\"{Binding GrindingMaxFifoCount", xaml);
```

- [x] **Step 2: 运行测试，确认当前缺少配置字段**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingFifoOverflowDispatchContractTests --no-restore`

- [x] **Step 3: 添加最小配置绑定**

```csharp
public int MaxFifoCount { get; set; } = 2;
public int GrindingMaxFifoCount
{
    get => _cfg.Grinding.MaxFifoCount;
    set { _cfg.Grinding.MaxFifoCount = value; OnPropertyChanged(); }
}
```

- [x] **Step 4: 运行配置契约测试，确认通过**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingFifoOverflowDispatchContractTests --no-restore`

### Task 2: 在安全派发门内拦截 FIFO 超限

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`
- Test: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingFifoOverflowDispatchContractTests.cs`

- [x] **Step 1: 扩展契约测试，约束超限检查顺序和安全副作用**

```csharp
Assert.True(craneLock < dispatchGate && dispatchGate < overflowGuard && overflowGuard < dequeue);
Assert.Contains("_paused = true;", guardedScope);
Assert.Contains("OnSafetyAlarm?.Invoke", guardedScope);
Assert.DoesNotContain("ProcessWorkpieceAsync", guardedScope);
```

- [x] **Step 2: 在 `TryDispatchLoadAsync` 中添加守卫**

```csharp
int maxFifoCount = Math.Max(1, _cfg.Grinding.MaxFifoCount);
int cachedCount = CachedCount;
if (cachedCount > maxFifoCount)
{
    _paused = true;
    OnSafetyAlarm?.Invoke(/* 包含当前数量、上限、ST709应急逐笔清队头说明 */);
    return false;
}
```

- [x] **Step 3: 运行契约测试，确认锁释放仍由 finally 处理**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingFifoOverflowDispatchContractTests --no-restore`

### Task 3: 同步业务文档和验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/03-设备交互流程/整线引擎流程总览_按代码.md`

- [x] **Step 1: 文档化上料派发前的超限软暂停、未出队与应急逐笔清理规则**

- [x] **Step 2: 运行目标测试和主项目构建**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~GrindingFifoOverflowDispatchContractTests|FullyQualifiedName~GrindingStateTimeoutAndPriorityContractTests" --no-restore`

Run: `dotnet build AutomaticOnlineHostComputer.sln --no-restore`
