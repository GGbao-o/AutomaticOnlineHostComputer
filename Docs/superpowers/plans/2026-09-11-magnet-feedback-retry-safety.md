# Magnet Feedback Retry Safety Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Require three configurable feedback-confirmed magnetization/demagnetization attempts, and make every automatic-flow final magnet feedback failure pause safely while leaving manual commands independent.

**Architecture:** `CraneService` owns the PLC-level retry loop and raises a typed `MagnetFeedbackTimeoutException` only after valid feedback has remained unsatisfied for all configured attempts. Both connection caches provide the live `MotionConfig.Safety` values. Existing front/rear flow action resolution already pauses after a sent physical command; balancing and grinding receive explicit typed-exception branches so a failed pre-X11 magnetization cannot be mistaken for an unpicked workpiece and requeued.

**Tech Stack:** C#/.NET 8, Modbus TCP, WPF host process, xUnit source-contract tests.

---

### Task 1: Add the shared magnet-feedback configuration and service exception

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs:92-102`
- Modify: `Config/motion_settings.json:safety`
- Modify: `Communication/DeviceServices/CraneService.cs:11-55,76-105`
- Modify: `Communication/DeviceServices/CraneConnectionCache.cs:107-110`
- Modify: `Communication/DeviceServices/ManipulatorConnectionCache.cs:92-95`
- Create: `AutomaticOnlineHostComputer.Tests/Communication/MagnetFeedbackConfigurationContractTests.cs`

- [ ] **Step 1: Write the failing configuration contract**

```csharp
Assert.Contains("MagnetFeedbackMaxAttempts", config, StringComparison.Ordinal);
Assert.Contains("MagnetFeedbackTimeoutMs", config, StringComparison.Ordinal);
Assert.Contains("\"magnetFeedbackMaxAttempts\": 3", json, StringComparison.Ordinal);
Assert.Contains("\"magnetFeedbackTimeoutMs\": 3000", json, StringComparison.Ordinal);
Assert.Contains("MagnetFeedbackTimeoutException", service, StringComparison.Ordinal);
Assert.Contains("magnetFeedbackOptionsProvider", service, StringComparison.Ordinal);
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~MagnetFeedbackConfigurationContractTests`

Expected: FAIL because the three-attempt configuration and typed exception do not yet exist.

- [ ] **Step 3: Add the configuration, validated provider, and typed exception**

```csharp
public int MagnetFeedbackMaxAttempts { get; set; } = 3;
public int MagnetFeedbackTimeoutMs { get; set; } = 3_000;
```

```csharp
public sealed class MagnetFeedbackTimeoutException : TimeoutException
{
    public bool IsMagnetizing { get; }
    public int Attempts { get; }
    public bool? LastX6 { get; }
    public bool? LastX7 { get; }
}
```

Pass a lambda from both connection caches that returns the current safety values. In `CraneService`, clamp invalid values to `3` and `3000` at each operation start so reloaded configuration is honored without recreating a connection.

- [ ] **Step 4: Run the focused test and verify it passes**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~MagnetFeedbackConfigurationContractTests`

Expected: PASS.

### Task 2: Make charging and demagnetizing use the same feedback-confirmed retry semantics

**Files:**
- Modify: `Communication/DeviceServices/CraneService.cs:476-539`
- Create: `AutomaticOnlineHostComputer.Tests/Communication/MagnetFeedbackRetryContractTests.cs`

- [ ] **Step 1: Write the failing retry-semantics contract**

```csharp
Assert.Contains("for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)", service,
    StringComparison.Ordinal);
Assert.Contains("Addr.D_ManualMagnetOn", service, StringComparison.Ordinal);
Assert.Contains("Addr.D_ManualMagnetOff", service, StringComparison.Ordinal);
Assert.Contains("if (x6)", service, StringComparison.Ordinal);
Assert.Contains("if (x7 && !x6)", service, StringComparison.Ordinal);
Assert.Contains("finally", service, StringComparison.Ordinal);
Assert.Contains("new MagnetFeedbackTimeoutException", service, StringComparison.Ordinal);
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~MagnetFeedbackRetryContractTests`

Expected: FAIL because charging has one non-throwing attempt and demagnetizing has a hard-coded ten attempts.

- [ ] **Step 3: Implement the two retry loops**

```csharp
for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)
{
    try
    {
        await WriteRegAsync(triggerAddress, 2, triggerText, ct);
        // Poll only to options.TimeoutMs. A ReadXBitAsync exception leaves feedback unknown and escapes.
        if (feedbackReached) return;
    }
    finally
    {
        await WriteRegAsync(triggerAddress, 0, stopText, ct);
    }
}

throw new MagnetFeedbackTimeoutException(_name, isMagnetizing, options.MaxAttempts, lastX6, lastX7);
```

Use `X6=1` for charging and `X7=1 && X6=0` for demagnetizing. Only a completed polling interval with valid but unsatisfied feedback advances to the next attempt. A read/write/cancellation exception is not retried as though feedback were zero.

- [ ] **Step 4: Run the focused test and verify it passes**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~MagnetFeedbackRetryContractTests`

Expected: PASS.

### Task 3: Route automatic magnet-feedback failure into the safety pause path

**Files:**
- Modify: `Service/FlowEngine/FlowActionExceptionResolver.cs:18-38`
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs:1681-1742,2356-2426`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:2234-2288,2826-2860`
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs:2650-2710`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs:2650-2710`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:3210-3275`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:3072-3135`
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/MagnetFeedbackSafetyContractTests.cs`

- [ ] **Step 1: Write the failing automatic-safety contract**

```csharp
Assert.Contains("exception is MagnetFeedbackTimeoutException", resolver, StringComparison.Ordinal);
Assert.Contains("PauseLineManual", resolver, StringComparison.Ordinal);
Assert.Contains("ex is MagnetFeedbackTimeoutException", balancing, StringComparison.Ordinal);
Assert.Contains("ex is MagnetFeedbackTimeoutException", grinding, StringComparison.Ordinal);
Assert.Contains("OnSafetyAlarm?.Invoke", balancing, StringComparison.Ordinal);
Assert.Contains("OnSafetyAlarm?.Invoke", grinding, StringComparison.Ordinal);
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~MagnetFeedbackSafetyContractTests`

Expected: FAIL because the typed magnet feedback failure does not yet exist or have explicit automatic-flow treatment.

- [ ] **Step 3: Implement explicit pause classification and engine handling**

```csharp
if (exception is MagnetFeedbackTimeoutException)
{
    action.MarkCommandResponseUnknown(exception.Message);
    action.PauseForManualResolution(exception.Message);
    return FlowActionExceptionConclusion.PauseLineManual;
}
```

For balancing M2/M3 and grinding load/unload top-level catches, add a typed-exception first branch that sets `_paused = true`, retains the active task/workpiece context, invokes the existing safety callback with `ex.Message`, and prevents the existing “not magnetized → requeue/retry” branch. Do not add locks or alter each action's existing `finally`; it remains responsible for software lock cleanup. Front and rear engines use their existing resolver-driven pause plus safety callbacks; add explicit source comments only where needed to document this guarantee.

- [ ] **Step 4: Run the focused test and verify it passes**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~MagnetFeedbackSafetyContractTests`

Expected: PASS.

### Task 4: Synchronize the operating documentation and verify

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/02-通信协议手册/研磨机协议_ModbusTCP.md`
- Modify: `Docs/superpowers/plans/2026-09-11-magnet-feedback-retry-safety.md`

- [ ] **Step 1: Document the shared magnet action contract**

```markdown
充磁和退磁均按 `safety.magnetFeedbackMaxAttempts` 与
`safety.magnetFeedbackTimeoutMs` 执行；充磁要求 X6=1，退磁要求 X7=1且X6=0。
自动流程三次未确认或反馈读取未知时必须暂停、弹窗并由finally释放软件锁；手动页仅显示失败。
```

- [ ] **Step 2: Run focused tests**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "FullyQualifiedName~MagnetFeedbackConfigurationContractTests|FullyQualifiedName~MagnetFeedbackRetryContractTests|FullyQualifiedName~MagnetFeedbackSafetyContractTests"`

Expected: PASS.

- [ ] **Step 3: Build the application**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: zero errors.

- [ ] **Step 4: Check formatting without committing**

Run: `git diff --check -- Infrastructure/Config/MotionConfig.cs Config/motion_settings.json Communication/DeviceServices/CraneService.cs Communication/DeviceServices/CraneConnectionCache.cs Communication/DeviceServices/ManipulatorConnectionCache.cs Service/FlowEngine/FlowActionExceptionResolver.cs Service/FlowEngine/BalancingFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Docs/全线业务逻辑详解.md Docs/02-通信协议手册/研磨机协议_ModbusTCP.md`

Expected: no whitespace errors in the current task's hunks. Do not run `git add` or `git commit`.

## Self-review

- Spec coverage: Tasks 1–2 cover one configurable three-attempt implementation for both magnet operations; Task 3 covers automatic action exit/pause/lock cleanup and manual isolation; Task 4 covers documentation and verification.
- Placeholder scan: no TBD/TODO entries or unspecified verification steps.
- Type consistency: all planned callers use the existing `CraneService`, `MotionConfig.Safety`, `FlowActionExceptionResolver`, and `OnSafetyAlarm`/rear safety callback APIs.
