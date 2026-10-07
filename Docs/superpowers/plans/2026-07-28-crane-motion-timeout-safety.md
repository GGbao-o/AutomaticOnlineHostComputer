# Crane Motion Timeout Safety Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every automatic crane motion timeout a 120-second, evidence-rich safety pause that preserves physical-zone ownership and requires an explicit human recovery decision.

**Architecture:** `CraneService` remains device-only: it enforces per-stage timing, stops the servo safely, captures final status, and throws `CraneMotionTimeoutException`. Each owning flow engine catches that exception before its generic catch, pauses through existing callbacks, preserves workpiece state, and retains any collision-zone ownership until emergency recovery.

**Tech Stack:** .NET 8, C#, WPF, xUnit, Modbus TCP, existing `SafetyFlags`, `AttentionEventCenter`, JSON `MotionConfig`.

---

### Task 1: Motion timeout contract and 120-second configuration

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs:55-66`
- Modify: `Config/motion_settings.json:2-20` and each `craneSpeeds.*`
- Modify: `Communication/DeviceServices/CraneService.cs:9-17,759-997`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/CraneMotionTimeoutContractTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Motion_timeout_has_axis_and_final_position_evidence()
{
    string source = Read("Communication", "DeviceServices", "CraneService.cs");
    Assert.Contains("public sealed class CraneMotionTimeoutException", source);
    Assert.Contains("LastKnownStatus", source);
    Assert.Contains("UnreachedAxes", source);
    Assert.Contains("TriggerManualEmergencyStopAsync", source);
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~CraneMotionTimeoutContractTests`

Expected: FAIL because the timeout data contract does not exist.

- [ ] **Step 3: Implement the minimum communication contract**

```csharp
public sealed class CraneMotionTimeoutException : TimeoutException
{
    public CraneStatus? LastKnownStatus { get; }
    public IReadOnlyList<string> UnreachedAxes { get; }
    public bool EmergencyStopCommandSucceeded { get; }
}
```

Add `XyTimeoutMs=120000` and `ZTimeoutMs=120000` to `AbsMoveSection` and source JSON. Replace mixed `timeoutMs / 2` logic with a named 120-second budget per safe movement stage. On timeout reset D4520/D4521/D4522, pulse D4518 using `CancellationToken.None`, read final status once, and throw the dedicated exception. Do not pause an engine or change caches/locks here.

- [ ] **Step 4: Re-run the test**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~CraneMotionTimeoutContractTests`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -- Infrastructure/Config/MotionConfig.cs Config/motion_settings.json Communication/DeviceServices/CraneService.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/CraneMotionTimeoutContractTests.cs
git commit -m "feat: report and stop crane motion timeouts"
```

### Task 2: Rear-crane timeout safety hold and source reservation

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:1238-1818`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:1238-1818`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:1886-1920,2051-2080`
- Modify: `Views/Home/Dialogs/SkewBedEmergencyDialog.xaml.cs:46-76`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/RearCraneMotionTimeoutSafetyContractTests.cs`

- [ ] **Step 1: Write the failing symmetric contract**

```csharp
[Theory]
[InlineData("Line1RearFlowEngine.cs")]
[InlineData("Line2RearFlowEngine.cs")]
public void Rear_load_timeout_pauses_without_auto_cache_restore(string file)
{
    string method = ExtractMethod(ReadFlow(file), "private async Task DoLoad");
    Assert.Contains("catch (CraneMotionTimeoutException timeout)", method);
    Assert.Contains("CreateRearCraneMotionSafetyHold", method);
    Assert.Contains("OnRearCraneSafetyAlarm?.Invoke", method);
}
```

- [ ] **Step 2: Run the contract**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~RearCraneMotionTimeoutSafetyContractTests`

Expected: FAIL because a dedicated safety branch does not exist.

- [ ] **Step 3: Implement rear timeout state**

Reserve M817/M818/M821 before motion rather than deleting it. After X11=1, transition to explicit rear-crane in-transit state. In `DoLoad` and `DoUnload`, catch `CraneMotionTimeoutException` before generic exceptions; preserve workpiece and phase, set `_paused=true`, and hand ZoneMT/ZoneTS ownership to a safety-hold record. Do not retry, requeue, discard, move Z, or release uncertain collision zones. Emergency recovery must offer only source-present, crane-holding, and manually-removed outcomes.

- [ ] **Step 4: Verify rear timeout and existing rear regressions**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "FullyQualifiedName~RearCraneMotionTimeoutSafetyContractTests|FullyQualifiedName~RearLoadX11FailureContractTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -- Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Presentation/ViewModels/Home/HomeViewModel.cs Views/Home/Dialogs/SkewBedEmergencyDialog.xaml.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/RearCraneMotionTimeoutSafetyContractTests.cs
git commit -m "feat: pause rear crane on position timeout"
```

### Task 3: Front and grinding crane owner branches

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs:2096-2867`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs:2096-2867`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:1420-2261`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:1886-2005,2477-2489`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/CraneFlowMotionTimeoutSafetyContractTests.cs`

- [ ] **Step 1: Write failing owner contract**

```csharp
[Theory]
[InlineData("Line1FrontFlowEngine.cs")]
[InlineData("Line2FrontFlowEngine.cs")]
[InlineData("GrindingFlowEngine.cs")]
public void Crane_owner_handles_motion_timeout_before_generic_catch(string file)
{
    string source = ReadFlow(file);
    Assert.Contains("catch (CraneMotionTimeoutException timeout)", source);
    Assert.Contains("安全占用", source);
    Assert.Contains("OnSafetyAlarm", source);
}
```

- [ ] **Step 2: Run the contract**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~CraneFlowMotionTimeoutSafetyContractTests`

Expected: FAIL because generic catches currently own timeout behavior.

- [ ] **Step 3: Implement minimal safe pauses**

At each outer front/grinding crane workpiece action, catch the dedicated exception before generic catch. Preserve current workpiece and phase, retain its collision-zone safety hold, pause the owning engine, and invoke the existing HomeViewModel safety callback. Never requeue FIFO, clear pending workpiece, send handshake completion, or launch another crane operation while held.

- [ ] **Step 4: Re-run focused regressions**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "FullyQualifiedName~CraneFlowMotionTimeoutSafetyContractTests|FullyQualifiedName~GrindingStoneAlarmDispatchContractTests|FullyQualifiedName~LegacyAlarmOrderingContractTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -- Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs Presentation/ViewModels/Home/HomeViewModel.cs AutomaticOnlineHostComputer.Tests/OperationalEvents/CraneFlowMotionTimeoutSafetyContractTests.cs
git commit -m "feat: pause crane owners on position timeout"
```

### Task 4: Emergency evidence, documentation, and full validation

**Files:**
- Modify: `Presentation/ViewModels/Home/CraneManualControlViewModel.cs:717-744`
- Modify: `Service/FlowEngine/OperationalEventContextFactory.cs`
- Modify: `Service/OperationalEventCatalog.cs`
- Modify: `Views/Home/Dialogs/EmergencyCenterDialog.xaml`
- Modify: `Views/Home/Dialogs/EmergencyCenterDialog.xaml.cs`
- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/02-通信协议手册/天车交互协议.md`
- Modify: `Docs/02-通信协议手册/天车手动控制协议_完整寄存器.md`
- Modify: `Docs/03-设备交互流程/天车_调度逻辑.md`
- Modify: `Docs/03-设备交互流程/后天车与斜床交互流程.md`
- Modify: `Docs/03-设备交互流程/整线引擎流程总览_按代码.md`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/CraneMotionTimeoutEventContractTests.cs`

- [ ] **Step 1: Write failing event contract**

```csharp
[Fact]
public void Timeout_event_exposes_target_last_position_axes_and_manual_guidance()
{
    string factory = Read("Service", "FlowEngine", "OperationalEventContextFactory.cs");
    Assert.Contains("CRANE_MOTION_TIMEOUT", factory);
}
```

- [ ] **Step 2: Run the event test**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~CraneMotionTimeoutEventContractTests`

Expected: FAIL because the event is absent.

- [ ] **Step 3: Implement event, manual display, and source-derived docs**

Use `CRANE_MOTION_TIMEOUT` to record target/last XYZ, unreached axes, stop result, workpiece, lock hold and recovery instructions. Manual panel displays the exception but never clears an engine hold. Emergency Center reads the evidence and offers only the three approved human outcomes. Update every listed document with ±5mm, 120-second stages, D4518, safety holds, cache ownership and no-auto-retry rules.

- [ ] **Step 4: Run all verification**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore`

Expected: all tests pass.

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: build succeeds with no new warnings or errors.

Run: `git diff --check -- Communication/DeviceServices/CraneService.cs Infrastructure/Config/MotionConfig.cs Config/motion_settings.json Service/FlowEngine Presentation Docs AutomaticOnlineHostComputer.Tests`

Expected: no whitespace errors.

- [ ] **Step 5: Commit docs and final integration**

```powershell
git add -- Presentation/ViewModels/Home/CraneManualControlViewModel.cs Service/FlowEngine/OperationalEventContextFactory.cs Service/OperationalEventCatalog.cs Views/Home/Dialogs/EmergencyCenterDialog.xaml Views/Home/Dialogs/EmergencyCenterDialog.xaml.cs Docs AutomaticOnlineHostComputer.Tests/OperationalEvents/CraneMotionTimeoutEventContractTests.cs
git commit -m "docs: document crane timeout safety recovery"
```

