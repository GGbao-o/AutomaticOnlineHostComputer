# Grinder Unload and Rear Alarm Gates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent new automatic grinding unloads from TypeA standalone machines and new rear-crane skew-bed unloads while manipulator 2 or 3 reports an alarm or cannot be read.

**Architecture:** `GrindingFlowEngine` will reuse its current cycle snapshot (`LastOnlineMode`) to make TypeA online mode a candidate condition, exactly as it already does for loading. A small shared `ManipulatorAlarmDispatchGate` helper will read the two manipulator status snapshots once per rear unload candidate scan; both rear engines call it immediately before adding candidates, so the existing post-lock candidate rebuild is the lock-time recheck.

**Tech Stack:** C#/.NET 8, WPF host process, xUnit source-contract tests, Modbus device services.

---

### Task 1: Guard Siemens grinding unload candidates

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:964-987`
- Modify: `AutomaticOnlineHostComputer.Tests/OperationalEvents/SiemensGrinderOnlineModeContractTests.cs`

- [ ] **Step 1: Add a failing contract assertion for the unload candidate condition**

```csharp
string unloadCandidate = Slice(engine,
    "// ── 检查下料架MC64",
    "if (await _craneLock.WaitAsync(0, ct))");

Assert.Contains("g.GrinderType == PlcGrinderService.GrinderType.TypeA && !g.LastOnlineMode",
    unloadCandidate, StringComparison.Ordinal);
Assert.Contains("continue; // 西门子单机模式仅跳过本轮自动下料", unloadCandidate,
    StringComparison.Ordinal);
Assert.DoesNotContain("_paused = true", unloadCandidate, StringComparison.Ordinal);
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~SiemensGrinderOnlineModeContractTests`

Expected: the new unload-condition assertion fails because `WaitingForUnload` currently does not inspect `LastOnlineMode`.

- [ ] **Step 3: Add the minimal TypeA candidate check**

```csharp
// 西门子单机不接收新的自动下料；保留等待下料状态，恢复联机后由后续轮次重试。
if (g.GrinderType == PlcGrinderService.GrinderType.TypeA && !g.LastOnlineMode)
{
    if (ShouldLogWaiting($"unload-standalone:{g.StationCode}"))
        Console.WriteLine($"[GrindingEngine] [{g.Name}] ⏳ 单机模式(OnlineMode=0)，本轮不派发自动下料");
    continue; // 西门子单机模式仅跳过本轮自动下料
}
```

- [ ] **Step 4: Run the focused test and verify it passes**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~SiemensGrinderOnlineModeContractTests`

Expected: PASS.

### Task 2: Add the shared manipulator-alarm candidate gate

**Files:**
- Create: `Service/FlowEngine/ManipulatorAlarmDispatchGate.cs`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:826-884`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:804-862`
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/ManipulatorAlarmDispatchGateContractTests.cs`

- [ ] **Step 1: Add a failing source-contract test**

```csharp
Assert.Contains("ReadStatusAsync", gate, StringComparison.Ordinal);
Assert.Contains("ServoAlarm == 0", gate, StringComparison.Ordinal);
Assert.Contains("PlcServoAlarm == 0", gate, StringComparison.Ordinal);
Assert.Contains("PlcAlarm == 0", gate, StringComparison.Ordinal);
Assert.Contains("return false;", gate, StringComparison.Ordinal);
Assert.Contains("ManipulatorAlarmDispatchGate.CanDispatchRearSkewUnloadAsync", line1,
    StringComparison.Ordinal);
Assert.Contains("ManipulatorAlarmDispatchGate.CanDispatchRearSkewUnloadAsync", line2,
    StringComparison.Ordinal);
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~ManipulatorAlarmDispatchGateContractTests`

Expected: FAIL because the helper and calls do not yet exist.

- [ ] **Step 3: Implement the helper and candidate-scan calls**

```csharp
// 只允许两台机械手的三类报警均为0；读不到实时状态也不能按“无报警”派发。
bool manipulatorsReady = await ManipulatorAlarmDispatchGate.CanDispatchRearSkewUnloadAsync(
    _manipulatorCache, "1号线", ct);
if (!manipulatorsReady)
    continue;
```

The helper will read each manipulator once using `ReadStatusAsync`, return `false` for a null result, nonzero `ServoAlarm`/`PlcServoAlarm`/`PlcAlarm`, or a non-cancellation exception, and rethrow a requested cancellation. Each engine will cache this result for one `BuildUnloadCandidateScanAsync` call, immediately before candidate insertion. The existing second scan after `_craneRearLock` acquisition performs the required recheck without new lock logic.

- [ ] **Step 4: Run the focused test and verify it passes**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~ManipulatorAlarmDispatchGateContractTests`

Expected: PASS.

### Task 3: Synchronize business documentation and validate the integration

**Files:**
- Modify: `Docs/全线业务逻辑详解.md:556-563,884-892`
- Modify: `Docs/02-通信协议手册/研磨机协议_ModbusTCP.md:43,111-113,161-162`

- [ ] **Step 1: Update the business-flow rules**

```markdown
3. 机械手2、3的 D5011、D5012、D5013 必须均为0；任一状态读取失败或任一报警非0时，本轮不生成下料候选。
```

```markdown
4. 对 ST703/ST704，西门子研磨机联机模式必须实时快照为1；ST701/ST702不适用。单机或状态未知时仅跳过本轮下料。
```

- [ ] **Step 2: Update the protocol wording**

```markdown
联机=1 才参与新的自动上、下料候选；单机=0时停止该机新的自动派发，不影响已派发动作。
```

- [ ] **Step 3: Run focused tests and build**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "FullyQualifiedName~SiemensGrinderOnlineModeContractTests|FullyQualifiedName~ManipulatorAlarmDispatchGateContractTests"`

Expected: PASS.

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: build succeeds with zero errors; pre-existing warnings may remain.

- [ ] **Step 4: Check the changed-file diff without committing**

Run: `git diff --check -- Service/FlowEngine/GrindingFlowEngine.cs Service/FlowEngine/ManipulatorAlarmDispatchGate.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Docs/全线业务逻辑详解.md Docs/02-通信协议手册/研磨机协议_ModbusTCP.md AutomaticOnlineHostComputer.Tests/OperationalEvents/SiemensGrinderOnlineModeContractTests.cs AutomaticOnlineHostComputer.Tests/FlowEngine/ManipulatorAlarmDispatchGateContractTests.cs`

Expected: no whitespace errors. Do not run `git add` or `git commit`.

## Self-review

- Spec coverage: Task 1 covers TypeA online-only unloading; Task 2 covers both rear engines, all three alarm registers, null/error handling, and lock-time recheck; Task 3 covers both mandated documents and verification.
- Placeholder scan: no TBD/TODO or unspecified test steps remain.
- Type consistency: all calls use the existing `ManipulatorConnectionCache`, `ReadStatusAsync`, `CraneStatus.ServoAlarm`, `CraneStatus.PlcServoAlarm`, and `CraneStatus.PlcAlarm` names.
