# Rear Unload On-Carrier Emergency Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A successful skew-bed emergency clears the exact matching rear-crane unload `PauseOnCarrier` snapshot so the line can resume without weakening target-position safety.

**Architecture:** Add an unload-specific helper beside the existing rear-load emergency helper in each rear engine. The helper filters the manual-action registry by the line's rear-unload scope, the selected bed as `Source`, and `PauseOnCarrier`; the existing emergency invokes it only after device and in-memory bed cleanup succeed. A mirrored source-contract test prevents accidentally expanding the predicate to `PauseAtTargetPendingHandoff`.

**Tech Stack:** C# / .NET 8, xUnit source-contract tests, `FlowActionManualRegistry`.

---

### Task 1: Add the focused source-contract test

**Files:**

- Modify: `AutomaticOnlineHostComputer.Tests/FlowEngine/CraneExceptionSafetyContractTests.cs:94-106`

- [ ] **Step 1: Add a two-line theory proving the narrow unload settlement**

```csharp
[Theory]
[InlineData("Line1RearFlowEngine.cs", "1号线后天车下料")]
[InlineData("Line2RearFlowEngine.cs", "2号线后天车下料")]
public void Rear_unload_on_carrier_emergency_settles_only_the_selected_source_bed(
    string file, string scope)
{
    string source = Read("Service", "FlowEngine", file);
    int settlement = source.IndexOf("private static string SettleManualRearUnloadOnCarrierForEmergency", StringComparison.Ordinal);
    int settlementEnd = source.IndexOf("    /// <summary>", settlement, StringComparison.Ordinal);
    Assert.True(settlement > 0 && settlementEnd > settlement, $"{file}必须有后天车下料持件应急结算器");
    string method = source[settlement..settlementEnd];

    Assert.Contains($"const string flowScope = \"{scope}\"", method, StringComparison.Ordinal);
    Assert.Contains("item.Source == bed.Code", method, StringComparison.Ordinal);
    Assert.Contains("item.Disposition == FlowActionDisposition.PauseOnCarrier", method, StringComparison.Ordinal);
    Assert.DoesNotContain("PauseAtTargetPendingHandoff", method, StringComparison.Ordinal);
    Assert.DoesNotContain("PauseSourceReserved", method, StringComparison.Ordinal);

    int cleared = source.IndexOf("MarkSkewManualCleared(bed);", StringComparison.Ordinal);
    int invoked = source.IndexOf("logs.Add(SettleManualRearUnloadOnCarrierForEmergency(bed));", StringComparison.Ordinal);
    Assert.True(invoked > cleared, $"{file}必须在斜床软件状态清空后再清下料持件快照");
}
```

- [ ] **Step 2: Run the focused test to prove the contract is initially missing**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~CraneExceptionSafetyContractTests`

Expected: FAIL because `SettleManualRearUnloadOnCarrierForEmergency` does not exist yet.

### Task 2: Settle matching rear-unload on-carrier snapshots after a successful emergency

**Files:**

- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:1128-1148,1233-1237`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:1090-1110,1195-1199`

- [ ] **Step 1: Add the Line 1 helper and call it after `MarkSkewManualCleared`**

```csharp
private static string SettleManualRearUnloadOnCarrierForEmergency(SkewCtx bed)
{
    // 下料目标待交接表示工件可能已在 M817/M720，必须走目标位专用结案器。
    const string flowScope = "1号线后天车下料";
    FlowActionSnapshot[] snapshots = FlowActionManualRegistry.Snapshot()
        .Where(item => item.FlowScope == flowScope &&
                       item.Source == bed.Code &&
                       item.Disposition == FlowActionDisposition.PauseOnCarrier)
        .ToArray();
    foreach (FlowActionSnapshot snapshot in snapshots)
        FlowActionManualRegistry.Remove(snapshot.OperationId);
    return snapshots.Length == 0
        ? "动作账本=无后天车下料持件快照"
        : $"动作账本=已结清{snapshots.Length}条后天车下料持件快照(工件已人工移走/放弃)";
}

// Existing successful-emergency tail, immediately after MarkSkewManualCleared(bed):
logs.Add(SettleManualRearUnloadOnCarrierForEmergency(bed));
```

- [ ] **Step 2: Add the matching Line 2 helper and call it after `MarkSkewManualCleared`**

```csharp
private static string SettleManualRearUnloadOnCarrierForEmergency(SkewCtx bed)
{
    // 下料目标待交接表示工件可能已在 M818/M821，必须走目标位专用结案器。
    const string flowScope = "2号线后天车下料";
    FlowActionSnapshot[] snapshots = FlowActionManualRegistry.Snapshot()
        .Where(item => item.FlowScope == flowScope &&
                       item.Source == bed.Code &&
                       item.Disposition == FlowActionDisposition.PauseOnCarrier)
        .ToArray();
    foreach (FlowActionSnapshot snapshot in snapshots)
        FlowActionManualRegistry.Remove(snapshot.OperationId);
    return snapshots.Length == 0
        ? "动作账本=无后天车下料持件快照"
        : $"动作账本=已结清{snapshots.Length}条后天车下料持件快照(工件已人工移走/放弃)";
}

// Existing successful-emergency tail, immediately after MarkSkewManualCleared(bed):
logs.Add(SettleManualRearUnloadOnCarrierForEmergency(bed));
```

### Task 3: Verify and review the bounded change

**Files:**

- Modify: `AutomaticOnlineHostComputer.Tests/FlowEngine/CraneExceptionSafetyContractTests.cs`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] **Step 1: Run the focused safety contract**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~CraneExceptionSafetyContractTests`

Expected: PASS; both rear engines have an exact unload-source-bed `PauseOnCarrier` settlement and no unload target-pending settlement.

- [ ] **Step 2: Build the application**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: Build succeeded with 0 errors.

- [ ] **Step 3: Review the diff scope**

Run: `git diff --check -- AutomaticOnlineHostComputer.Tests/FlowEngine/CraneExceptionSafetyContractTests.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs`

Expected: no output; the production diff contains only the two unload settlement helpers and their successful-emergency calls.
