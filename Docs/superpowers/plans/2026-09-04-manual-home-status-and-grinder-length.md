# 手动回原点状态与研磨长度准入 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在手动运动刷新中展示 PLC 的 X/Y/Z 回原点只读状态，并在研磨上料派发前以 mm 为单位阻止超过目标研磨机最大长度的工件出队或移动。

**Architecture:** 手动页面继续复用 `CraneService.ReadStatusAsync` 对 D5000~D5029 的单次批量读取，仅把 D5006~D5008 映射为新的只读回原点显示字段。研磨引擎在取得派发门闩后窥视 FIFO 队头，必要时先只读 MC65 D200 补足长度，再用配置的 ST701~ST704 最大长度筛选 Idle 研磨机；不匹配时不改变 FIFO、Pending、PLC 或天车状态。

**Tech Stack:** C#/.NET、WPF/XAML、xUnit、JSON 配置、Modbus TCP。

---

### Task 1: 将 D5006~D5008 定义为只读回原点状态

**Files:**
- Modify: `Communication/DeviceAddresses/CraneAddress.cs`
- Modify: `Communication/DeviceServices/CraneService.cs`
- Modify: `Presentation/ViewModels/Home/CraneManualControlViewModel.cs`
- Modify: `Views/Motion/Controls/ManualMotionControlPanel.xaml`
- Test: `AutomaticOnlineHostComputer.Tests/Presentation/ManualMotionHomeStatusContractTests.cs`

- [ ] **Step 1: 写入失败契约测试**

```csharp
Assert.Contains("XHomeCompleted", craneStatus, StringComparison.Ordinal);
Assert.Contains("YHomeCompleted", craneStatus, StringComparison.Ordinal);
Assert.Contains("ZHomeCompleted", craneStatus, StringComparison.Ordinal);
Assert.Contains("D5006", manualVm, StringComparison.Ordinal);
Assert.Contains("X轴已回原点", xaml, StringComparison.Ordinal);
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~ManualMotionHomeStatusContractTests`

Expected: FAIL，新的状态字段和页面绑定尚不存在。

- [ ] **Step 3: 实现单次读取与页面显示**

```csharp
// CraneStatus: D5006~D5008 仍来自 ReadStatusAsync 的同一批 r[6]~r[8]。
public short XHomeCompleted { get; set; } // D5006
public short YHomeCompleted { get; set; } // D5007
public short ZHomeCompleted { get; set; } // D5008

// ViewModel: 每次刷新前置 null；状态读取成功后按非零/零映射 true/false。
IsXHomeCompleted = status.XHomeCompleted != 0;
IsYHomeCompleted = status.YHomeCompleted != 0;
IsZHomeCompleted = status.ZHomeCompleted != 0;
```

XAML 只显示 `X轴已回原点（D5006=1）`、`Y轴已回原点（D5007=1）`、`Z轴已回原点（D5008=1）` 及对应的未回原点/读取未知文字；不增加读、写或业务判断。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~ManualMotionHomeStatusContractTests`

Expected: PASS。

### Task 2: 增加研磨机最大加工长度（mm）配置与页面编辑

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs`
- Modify: `Config/motion_settings.json`
- Modify: `Presentation/ViewModels/Config/ConfigPageViewModel.cs`
- Modify: `Views/Config/ConfigPageView.xaml`
- Test: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingMaximumLengthDispatchContractTests.cs`

- [ ] **Step 1: 写入失败契约测试**

```csharp
Assert.Contains("MaxWorkpieceLengthMm", model, StringComparison.Ordinal);
Assert.Contains("ST701", json, StringComparison.Ordinal);
Assert.Contains("GrindingLengthRows", viewModel, StringComparison.Ordinal);
Assert.Contains("研磨机最大加工长度", view, StringComparison.Ordinal);
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingMaximumLengthDispatchContractTests`

Expected: FAIL，研磨配置和页面行模型尚不存在。

- [ ] **Step 3: 实现安全配置语义**

```csharp
public Dictionary<string, int> MaxWorkpieceLengthMm { get; set; } = new(StringComparer.OrdinalIgnoreCase)
{
    ["ST701"] = 0, ["ST702"] = 0, ["ST703"] = 0, ["ST704"] = 0,
};

public bool CanProcessLength(string stationCode, double workpieceLength)
    => workpieceLength > 0
       && MaxWorkpieceLengthMm.TryGetValue(stationCode, out int max)
       && max > 0
       && workpieceLength <= max;
```

在“斜床参数”标签页的斜床表格下增加 ST701~ST704 的“研磨机最大加工长度 (mm)”表格。`0`/缺失显示可编辑，但派发时视为不可加工。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingMaximumLengthDispatchContractTests`

Expected: PASS。

### Task 3: 在 FIFO 出队和天车动作前按长度筛选研磨机

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`
- Modify: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingMaximumLengthDispatchContractTests.cs`

- [ ] **Step 1: 扩展契约测试覆盖顺序**

```csharp
int peek = method.IndexOf("TryPeekCache", StringComparison.Ordinal);
int lengthMatch = method.IndexOf("CanProcessLength", StringComparison.Ordinal);
int zeroGuard = method.IndexOf("EnsureGrindingCranePositionReadyAsync", StringComparison.Ordinal);
int dequeue = method.IndexOf("TryDequeueCache", StringComparison.Ordinal);
Assert.True(peek < lengthMatch && lengthMatch < zeroGuard && zeroGuard < dequeue);
```

- [ ] **Step 2: 实现仅窥视及 D200 长度解析**

```csharp
private bool TryPeekCache(out WorkpieceCache wp) { /* lock(_cacheLock), 不移除队头 */ }

// Length > 0: 直接使用 FIFO 长度。
// Length <= 0: 在 MC65/M730 已确认后只读 D200；无效时 return false，绝不出队。
// 解析后的本地 WorkpieceCache 仅在最终成功出队后写入 Pending 和后台动作。
```

在持有 `_grindingDispatchGate` 后重新读取队头、解析长度、调用 `FindReadyGrinder(workpieceLength)`。候选必须同时满足现有 Idle/请求数据/连接/Pending/磨石/联机条件及 `CanProcessLength`。若没有候选，仅输出节流日志，释放锁并保留 FIFO。

- [ ] **Step 3: 在最终派发点二次复查**

```csharp
if (ready.State != GrinderState.Idle ||
    !ready.LastRequestData ||
    !ready.Svc!.IsConnected ||
    ready.PendingWorkpiece != null ||
    !CanProcessLength(ready.StationCode, resolvedWp.Length))
    return false;
```

二次复查必须位于 `TryDequeueCache` 前；不匹配、D200 无效或配置缺失时不得设 `Loading`、`PendingWorkpiece`、动作牌、写 PLC 或移动天车。

- [ ] **Step 4: 运行针对性测试和编译**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingMaximumLengthDispatchContractTests`

Expected: PASS。

Run: `dotnet build AutomaticOnlineHostComputer-chengdu/AutomaticOnlineHostComputer-chengdu.csproj`

Expected: Build succeeded。
