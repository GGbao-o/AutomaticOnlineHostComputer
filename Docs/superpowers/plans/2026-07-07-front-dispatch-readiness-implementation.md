# Front Dispatch Readiness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在总上料架全局FIFO派发到1/2号线前，增加线路前端准入判断、压力调度和任务栏等待原因显示。

**Architecture:** 新增只读准入评估器 `FrontDispatchReadiness.cs`，1/2号线前端引擎只负责采集自身内存状态与既有设备快照，`HomeViewModel` 只消费准入结果并决定分线。全局分线循环统一使用1秒调度周期；线路前端设备刷新、机械手、货叉、双头镗、前天车和后端动作流程不改。

**Tech Stack:** C# / .NET 8 WPF，现有 `HomeViewModel`、`Line1FrontFlowEngine`、`Line2FrontFlowEngine`、`WorkpieceCache.ReportStage`。

---

### Task 1: 新增准入评估器

**Files:**
- Create: `Service/FlowEngine/FrontDispatchReadiness.cs`

- [x] **Step 1: 定义只读快照、输入和评估器**

```csharp
namespace AutomaticOnlineHostComputer.Service;

public sealed record FrontDispatchReadinessSnapshot(
    int Line,
    bool CanAccept,
    string RejectReason,
    int Pressure,
    DateTime CapturedAtUtc);

internal readonly record struct FrontDispatchReadinessInput(
    int Line,
    bool IsRunning,
    bool IsPaused,
    bool HasCurrentWorkpiece,
    bool ForkPhaseIdle,
    int CachedCount,
    bool ForkSnapshotValid,
    DateTime ForkSnapshotAtUtc,
    bool ForkAtStandby,
    bool ForkHasPlate,
    bool BoringSnapshotValid,
    DateTime BoringSnapshotAtUtc,
    bool BoringRequestData,
    int CraneQueueCount,
    bool FrontCraneActive);
```

- [x] **Step 2: 实现硬性准入和压力公式**

```csharp
internal static class FrontDispatchReadinessEvaluator
{
    internal static FrontDispatchReadinessSnapshot Evaluate(
        FrontDispatchReadinessInput input,
        DateTime nowUtc,
        TimeSpan maxSnapshotAge)
    {
        int pressure = input.CachedCount
            + (input.HasCurrentWorkpiece ? 1 : 0)
            + input.CraneQueueCount
            + (input.FrontCraneActive ? 1 : 0);

        string? reject = null;
        if (!input.IsRunning) reject = "线路引擎未运行";
        else if (input.IsPaused) reject = "线路已暂停";
        else if (input.CachedCount > 0) reject = $"前端缓存未消化({input.CachedCount})";
        else if (input.HasCurrentWorkpiece) reject = "前段_currentWp仍有在途工件";
        else if (!input.ForkPhaseIdle) reject = "货叉/双头镗阶段未回Idle";
        else if (!IsFresh(input.ForkSnapshotValid, input.ForkSnapshotAtUtc, nowUtc, maxSnapshotAge)) reject = "货叉快照无效或过期";
        else if (!input.ForkAtStandby) reject = "货叉不在待机位";
        else if (input.ForkHasPlate) reject = "货叉仍有板";
        else if (!IsFresh(input.BoringSnapshotValid, input.BoringSnapshotAtUtc, nowUtc, maxSnapshotAge)) reject = "双头镗快照无效或过期";
        else if (!input.BoringRequestData) reject = "双头镗R6101未请求数据";

        return reject == null
            ? new FrontDispatchReadinessSnapshot(input.Line, true, "可接板", pressure, nowUtc)
            : new FrontDispatchReadinessSnapshot(input.Line, false, reject, pressure, nowUtc);
    }
}
```

### Task 2: 1/2号线暴露准入快照

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [x] **Step 1: 为 DeviceStatus 增加货叉/双头镗快照时间和货叉待机字段**

新增字段：`ForkSnapshotValid`、`ForkSnapshotAtUtc`、`ForkAtStandby`、`BoringSnapshotAtUtc`。

- [x] **Step 2: 货叉读取成功时更新快照；读取失败时置无效**

在现有 `ReadAllStatusAsync` 成功分支里更新 `ForkSnapshotValid=true`、`ForkSnapshotAtUtc=DateTime.UtcNow`、`ForkAtStandby=fs.AtStandbyPos`、`ForkHasPlate=fs.HasPlate`。失败分支把 `ForkSnapshotValid=false`。

- [x] **Step 3: 双头镗读取成功时更新时间；失败时置无效**

在现有 R6101~R6108 快照成功分支里更新 `BoringSnapshotAtUtc=DateTime.UtcNow`。失败分支保持 `BoringSnapshotValid=false`。

- [x] **Step 4: 增加 `_frontCraneActive` 和 `GetFrontDispatchReadiness()`**

前天车开始处理 `ProcessCraneFrontAsync` 前置 `Interlocked.Exchange(ref _frontCraneActive, 1)`，结束后清零。`GetFrontDispatchReadiness()` 只读内存和快照，不读PLC、不等待动作锁。

### Task 3: 改造全局分线循环

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [x] **Step 1: 增加1秒调度周期常量和延时 helper**

定义 `private static readonly TimeSpan FrontDispatchPollInterval = TimeSpan.FromSeconds(1);`，所有正常等待分支使用 `DelayFrontDispatchPollAsync(ct)`。

- [x] **Step 2: 替换原 `SelectRouteLineBySkewCapacity` 为“能力 + 准入”选择**

保持板长能力、大直径强制1号线、FIFO队头和M800复位门不变。候选线通过 `GetFrontDispatchReadiness()` 过滤，两个都可接时按 `Pressure` 小优先，压力相同用 `_lastFrontDispatchLine` 交替。

- [x] **Step 3: 派发前二次读取准入快照**

真正出队和写线路缓存前，再取一次选中线路快照；若已不可接，任务仍留在全局FIFO队头。

### Task 4: 主页面任务栏显示等待原因

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [x] **Step 1: 在等待M800、等待线路准入、等待复位门时调用 `ReportStage`**

例如 `等待总上料架M800=1（物理板到位）`、`物理板已到位，等待可接线路：1号线=...；2号线=...`。

- [x] **Step 2: 日志和任务栏共用清晰原因**

重复原因允许属性层忽略相同值；生产日志仍按循环计数节流，避免每秒刷屏。

### Task 5: 验证

**Files:**
- No source edits.

- [x] **Step 1: 静态搜索确认边界**

Run: `rg -n "FrontDispatchPollInterval|GetFrontDispatchReadiness|FrontDispatchReadinessEvaluator|Task.Delay\\((300|500|800)" Presentation\\ViewModels\\Home\\HomeViewModel.cs Service\\FlowEngine -S`

- [x] **Step 2: 编译主项目**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: `0 Error(s)`。
