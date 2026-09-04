# Manual Alarm Display and Balancing Timeout Alarm Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only manual-page alarm indicator and make M2/M3 motion timeouts pause balancing with the original timeout shown in the existing safety popup.

**Architecture:** `CraneManualControlViewModel` derives a nullable alarm state and raw-value text from its existing `CraneStatus` batch; XAML only renders it. `BalancingFlowEngine` recognizes `CraneMotionTimeoutException` inside each existing generic catch and uses the existing `OnSafetyAlarm` path, leaving ledger, business state, locks, and start gates unchanged.

**Tech Stack:** C#/.NET WPF, CommunityToolkit MVVM bindings, xUnit source-contract tests.

---

### Task 1: Write and run failing contracts

**Files:**

- Create: `AutomaticOnlineHostComputer.Tests/Presentation/ManualMotionAlarmStatusContractTests.cs`
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/BalancingMotionTimeoutAlarmContractTests.cs`

- [x] **Step 1: Require all three words in the manual alarm state**

```csharp
Assert.Contains("IsAlarmFeedback", source, StringComparison.Ordinal);
Assert.Contains("status.ServoAlarm != 0 || status.PlcServoAlarm != 0 || status.PlcAlarm != 0", source, StringComparison.Ordinal);
Assert.Contains("D5011={ServoAlarmValue}", source, StringComparison.Ordinal);
```

- [x] **Step 2: Require M2 and M3 to forward a timeout message after setting pause**

```csharp
Assert.Equal(2, Regex.Matches(source,
    "catch \\(CraneMotionTimeoutException timeout\\)[\\s\\S]{0,500}?_paused = true;[\\s\\S]{0,500}?OnSafetyAlarm\\?\\.Invoke\\(timeout.Message\\);").Count);
```

- [x] **Step 3: Run the new tests before source changes**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~ManualMotionAlarmStatusContractTests|FullyQualifiedName~BalancingMotionTimeoutAlarmContractTests" --no-restore`

Expected: FAIL because neither contract exists yet.

### Task 2: Add the manual read-only alarm display

**Files:**

- Modify: `Presentation/ViewModels/Home/CraneManualControlViewModel.cs`
- Modify: `Views/Motion/Controls/ManualMotionControlPanel.xaml`

- [x] **Step 1: Add state, raw values, and binding notifications**

```csharp
public bool? IsAlarmFeedback { get; private set; }
public string AlarmFeedbackText => IsAlarmFeedback switch
{
    true => $"报警（D5011={ServoAlarmValue}，D5012={PlcServoAlarmValue}，D5013={PlcAlarmValue}）",
    false => $"报警正常（D5011={ServoAlarmValue}，D5012={PlcServoAlarmValue}，D5013={PlcAlarmValue}）",
    null => "报警状态未知"
};
```

- [x] **Step 2: Reset on selection/refresh and populate from the existing status read**

```csharp
ServoAlarmValue = status.ServoAlarm;
PlcServoAlarmValue = status.PlcServoAlarm;
PlcAlarmValue = status.PlcAlarm;
IsAlarmFeedback = status.ServoAlarm != 0 || status.PlcServoAlarm != 0 || status.PlcAlarm != 0;
```

- [x] **Step 3: Bind a non-interactive normal/alarm/unknown badge in the existing status panel**

The badge has no command, PLC write, acknowledgement, or interlock.

### Task 3: Pause and display every M2/M3 movement timeout

**Files:**

- Modify: `Service/FlowEngine/BalancingFlowEngine.cs`

- [x] **Step 1: At the start of each existing generic action catch, add this timeout recognition**

```csharp
if (ex is CraneMotionTimeoutException timeout)
{
    _paused = true;
    OnSafetyAlarm?.Invoke(timeout.Message);
}
```

- [x] **Step 2: Leave the existing generic catch and finally unchanged**

This preserves exception reporting, action snapshots, existing lock release and Busy cleanup. It adds no ledger action, cache mutation, source completion, or line-start condition.

### Task 4: Verify, document, and commit

**Files:**

- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/superpowers/plans/2026-09-04-manual-alarm-display-and-balancing-timeout-alarm.md`

- [ ] **Step 1: Run focused contracts**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~ManualMotionAlarmStatusContractTests|FullyQualifiedName~BalancingMotionTimeoutAlarmContractTests" --no-restore`

Expected: PASS with both test classes passing.

- [ ] **Step 2: Build production**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: build succeeds with zero errors.

- [ ] **Step 3: Record the display-only and timeout-only behavior in the core business document**

- [ ] **Step 4: Run `git diff --check`, stage only task files, and commit `fix: show manual alarms and pause balancing timeouts`**

## Self-review

- Tasks 1–2 meet the display-only requirement from D5011/D5012/D5013.
- Tasks 1 and 3 make only M2/M3 motion timeout pause-and-popup behavior change.
- No task changes action ledger, source closure, locks, manual settlement, or line-start logic.
