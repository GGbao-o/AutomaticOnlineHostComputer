# Grinding M731 Overlap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Start the ST709 post-pick Z lift, then write MC65 M731 after a configurable delay while Z is still returning, and continue only after both operations succeed.

**Architecture:** Add a grinding-only delay field to `MotionConfig`, persist it in the site JSON, and expose it on the configuration page. In `ProcessWorkpieceAsync`, create the Z-motion task first, await the configured delay, create the M731 task, then await both tasks before recording the safe-Z and M731 success checkpoints. Existing outer exception handling remains responsible for pausing and retaining `PendingWorkpiece` after either operation fails.

**Tech Stack:** C#/.NET, WPF binding, JSON configuration, xUnit source-contract tests.

---

### Task 1: Cover the configured concurrent handoff contract

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingM731OverlapContractTests.cs`
- Test: `AutomaticOnlineHostComputer.Tests/FlowEngine/GrindingM731OverlapContractTests.cs`

- [x] **Step 1: Write the failing test**

```csharp
[Fact]
public void M731_is_started_after_the_configured_delay_while_safe_z_is_in_flight()
{
    string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");
    string method = ExtractMethod(source, "private async Task ProcessWorkpieceAsync");

    int zStart = method.IndexOf("Task zSafeTask = crane.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);", StringComparison.Ordinal);
    int delay = method.IndexOf("await Task.Delay(_cfg.Grinding.M731NotifyDelayMs, ct);", StringComparison.Ordinal);
    int m731Start = method.IndexOf("Task m731Task = _mc65.WriteMBitInWordAsync(720, 11, true, ct);", StringComparison.Ordinal);
    int join = method.IndexOf("await Task.WhenAll(zSafeTask, m731Task);", StringComparison.Ordinal);

    Assert.True(zStart >= 0 && zStart < delay && delay < m731Start && m731Start < join);
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingM731OverlapContractTests --no-restore`

Expected: FAIL because the configuration field and concurrent task sequence do not yet exist.

### Task 2: Add the operator-controlled delay setting

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs:255-278`
- Modify: `Config/motion_settings.json:375-388`
- Modify: `Presentation/ViewModels/Config/ConfigPageViewModel.cs:110-121`
- Modify: `Views/Config/ConfigPageView.xaml:442-462`

- [x] **Step 1: Add the defaulted persisted setting**

```csharp
/// <summary>ST709取料后：启动Z回安全高度后，延时多久写M731释放传送带（ms）。</summary>
public int M731NotifyDelayMs { get; set; } = 1000;
```

Add the matching JSON property below `x11StableDelayMs`:

```json
"m731NotifyDelayMs": 1000,
```

- [x] **Step 2: Expose the setting in the configuration page**

```csharp
public int M731NotifyDelayMs
{
    get => _cfg.Grinding.M731NotifyDelayMs;
    set { _cfg.Grinding.M731NotifyDelayMs = value; OnPropertyChanged(); }
}
```

Bind it with a labeled textbox in the grinding configuration area:

```xml
<TextBlock Text="ST709取料后M731延时" Style="{StaticResource ParamLabelStyle}" />
<TextBox Text="{Binding M731NotifyDelayMs, UpdateSourceTrigger=PropertyChanged}" Style="{StaticResource ParamTextBoxStyle}" Width="80" />
<TextBlock Text="ms" Style="{StaticResource ParamLabelStyle}" />
```

- [x] **Step 3: Run the focused contract test**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingM731OverlapContractTests --no-restore`

Expected: still FAIL because `ProcessWorkpieceAsync` has not changed.

### Task 3: Overlap the Z lift and M731 notification

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:1769-1797`

- [x] **Step 1: Start Z first, then M731 after the configured delay, and await both**

Replace the sequential Z-motion/M731 block with the following task ordering, preserving the existing action and physical-tracker checkpoints only after `Task.WhenAll` succeeds:

```csharp
Task zSafeTask = crane.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);
await Task.Delay(_cfg.Grinding.M731NotifyDelayMs, ct);
Task m731Task = _mc65.WriteMBitInWordAsync(720, 11, true, ct);
await Task.WhenAll(zSafeTask, m731Task);
```

Keep the existing `InvalidOperationException` messages for an unavailable MC65 or M731 write failure. Do not move to the grinder until both tasks complete successfully; any exception must continue to reach the existing catch blocks, which retain `grinder.PendingWorkpiece` and pause the engine rather than requeueing the workpiece.

- [x] **Step 2: Run the focused test**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~GrindingM731OverlapContractTests --no-restore`

Expected: PASS.

- [x] **Step 3: Build and run the full suite**

Run: `dotnet build AutomaticOnlineHostComputer.sln --no-restore`

Expected: build succeeds.

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-restore`

Expected: the new test passes; report any existing unrelated failures separately.
