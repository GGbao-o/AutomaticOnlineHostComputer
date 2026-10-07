# Grinder Stone Alarm Single-Machine Dispatch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a TypeA grinder stone-thickness alarm block only new work assigned to that grinder while keeping the grinding engine and other grinders running.

**Architecture:** `GrindingFlowEngine` owns the dispatch eligibility snapshot in `GrinderContext`; its TypeA scan commits the two stone signals and derives a per-machine dispatch block. The pre-pick fresh PLC read remains a race guard. The existing polling path drives both the device overview and flow-status cards, so it will promote the alarm to the primary state without creating a second connection or PLC write path.

**Tech Stack:** C#/.NET 8 WPF, xUnit source-contract tests, Modbus TypeA snapshots, Markdown operation documents.

---

### Task 1: Lock the new behavior with a regression contract

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/OperationalEvents/GrindingStoneAlarmDispatchContractTests.cs`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/GrindingStoneAlarmDispatchContractTests.cs`

- [x] **Step 1: Write the failing test**

```csharp
[Fact]
public void TypeA_stone_alarm_blocks_only_the_affected_grinder_without_pausing_the_engine()
{
    string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");

    Assert.Contains("LastGrindStone1Alarm", source, StringComparison.Ordinal);
    Assert.Contains("LastGrindStone2Alarm", source, StringComparison.Ordinal);
    Assert.Contains("&& !g.HasGrindStoneAlarm", source, StringComparison.Ordinal);
    Assert.Contains("TryCommitGrinderSignals(g, scanVersion, s.RawDI, s.ReqData, s.ReqUnload ? 1 : 0, s.Busy, s.Door, s.GrindStone1Alarm, s.GrindStone2Alarm)", source, StringComparison.Ordinal);

    string prePick = Slice(source, "// ── ② 检查磨石厚度报警", "// ── ③ 补充长度后写工件参数");
    Assert.DoesNotContain("_paused = true", prePick, StringComparison.Ordinal);
    Assert.DoesNotContain("OnSafetyAlarm?.Invoke", prePick, StringComparison.Ordinal);
    Assert.Contains("RequeueWorkpiece(wp)", prePick, StringComparison.Ordinal);
}
```

- [x] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingStoneAlarmDispatchContractTests --no-restore`

Expected: failure because the context, scan commit and selection predicate do not yet include stone alarms, and the pre-pick branch still pauses.

- [x] **Step 3: Implement the minimal engine state and selection change**

```csharp
public bool LastGrindStone1Alarm { get; set; }
public bool LastGrindStone2Alarm { get; set; }
public bool HasGrindStoneAlarm => LastGrindStone1Alarm || LastGrindStone2Alarm;

return _grinders.FirstOrDefault(g =>
    g.State == GrinderState.Idle && g.LastRequestData && g.Svc != null &&
    g.Svc.IsConnected && g.PendingWorkpiece == null && !g.HasGrindStoneAlarm);
```

Extend `TryCommitGrinderSignals` so TypeA passes its two snapshot bits and TypeB passes `false, false`. Log only transition and one-minute ongoing heartbeats. In the existing pre-pick fresh check, update the two context bits, return the workpiece to FIFO and clear `PendingWorkpiece`, but do not set `_paused` or invoke `OnSafetyAlarm`.

- [x] **Step 4: Run the focused test and verify it passes**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingStoneAlarmDispatchContractTests --no-restore`

Expected: 1 passed, 0 failed.

### Task 2: Promote the alarm in both existing status-card paths

**Files:**
- Modify: `Presentation/ViewModels/Home/GrinderCardViewModel.cs:112-145`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:673-684,2869-2890`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/GrindingStoneAlarmDispatchContractTests.cs`

- [x] **Step 1: Extend the failing contract for the UI paths**

```csharp
Assert.Contains("⚠ 磨石{stoneNo}厚度报警 · 已停止自动分配", grinderCard, StringComparison.Ordinal);
Assert.Contains("BuildGrinderOverviewState(fault, stone1 || stone2", home, StringComparison.Ordinal);
Assert.Contains("stationCard.ConnectedBrush = fault || stone1 || stone2", home, StringComparison.Ordinal);
```

- [x] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingStoneAlarmDispatchContractTests --no-restore`

Expected: failure because stone bits are currently only appended to detailed signal text.

- [x] **Step 3: Implement one shared TypeA display rule per path**

```csharp
bool stoneAlarm = stone1 || stone2;
int stoneNo = stone1 ? 1 : 2;
Line1 = stoneAlarm
    ? $"已连接 | ⚠ 磨石{stoneNo}厚度报警 · 已停止自动分配"
    : fault ? "已连接 | ⚠ 故障" : "已连接，就绪";
```

Make `BuildGrinderOverviewState` accept `stoneAlarm` and return the same alert text before normal request/load/unload states. In the TypeA polling branch, pass `stone1 || stone2` and color both the status and connected marker red. Keep raw bits in `Status2` and keep TypeB behavior unchanged.

- [x] **Step 4: Run the focused test and verify it passes**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingStoneAlarmDispatchContractTests --no-restore`

Expected: all contract assertions pass.

### Task 3: Make the operational documents match the new policy

**Files:**
- Modify: `Docs/全线业务逻辑详解.md:906-910`
- Modify: `Docs/02-通信协议手册/研磨机协议_ModbusTCP.md:41,111,159`
- Modify: `Docs/整线技术手册.md:809`
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md:429,567`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/GrindingStoneAlarmDispatchContractTests.cs`

- [x] **Step 1: Extend the failing document assertions**

```csharp
Assert.Contains("不暂停研磨引擎", businessDocs, StringComparison.Ordinal);
Assert.Contains("停止自动分配", businessDocs, StringComparison.Ordinal);
Assert.DoesNotContain("有报警 → 暂停引擎提示更换磨石", businessDocs, StringComparison.Ordinal);
```

- [x] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingStoneAlarmDispatchContractTests --no-restore`

Expected: failure because all four operation documents still describe a whole grinding-engine pause.

- [x] **Step 3: Update the policy and physical boundary in every listed document**

State that TypeA `40001-1/2` is a per-machine maintenance signal: it blocks only new automatic assignments to the alarmed machine, returns a pre-pick dequeued workpiece to FIFO, continues dispatching other grinders, and automatically recovers assignment eligibility after both bits clear. Explicitly preserve full-engine pause for physical safety / unknown-result cases.

- [x] **Step 4: Run the focused test and verify it passes**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingStoneAlarmDispatchContractTests --no-restore`

Expected: all contract assertions pass.

### Task 4: Run full regression and compile verification

**Files:**
- Verify: `AutomaticOnlineHostComputer.Tests/`
- Verify: `AutomaticOnlineHostComputer.csproj`

- [x] **Step 1: Run all tests**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore`

Expected: 0 failed.

- [x] **Step 2: Build the production project**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore --verbosity minimal`

Expected: 0 errors.

- [x] **Step 3: Inspect the scoped diff**

Run: `git diff --check -- Service/FlowEngine/GrindingFlowEngine.cs Presentation/ViewModels/Home/GrinderCardViewModel.cs Presentation/ViewModels/Home/HomeViewModel.cs Docs/全线业务逻辑详解.md Docs/02-通信协议手册/研磨机协议_ModbusTCP.md Docs/整线技术手册.md Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`

Expected: no whitespace errors.
