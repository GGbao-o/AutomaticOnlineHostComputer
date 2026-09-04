# Remove Line Start Ledger Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let line 1 and line 2 start or resume without inspecting pending manual action ledger snapshots, while retaining the crane XYZ-zero safety guard.

**Architecture:** Keep the existing `Line1ToggleAsync` and `Line2ToggleAsync` start/resume flows unchanged except for removing their call to `EnsureNoPendingManualActionsForLine`. A source-contract test protects the resulting invariant for both lines and verifies the independent coordinate guard is retained.

**Tech Stack:** C# 12, WPF, xUnit 2.9.3, .NET 8.

---

### Task 1: Add the line-start contract test

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/Presentation/LineStartLedgerGateContractTests.cs`
- Test: `AutomaticOnlineHostComputer.Tests/Presentation/LineStartLedgerGateContractTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class LineStartLedgerGateContractTests
{
    [Fact]
    public void Line_start_and_resume_keep_crane_zero_guard_but_do_not_consult_manual_action_ledger()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "Presentation", "ViewModels", "Home", "HomeViewModel.cs"));

        int line1Start = source.IndexOf("private async Task Line1ToggleAsync()", StringComparison.Ordinal);
        int line2Start = source.IndexOf("private async Task Line2ToggleAsync()", StringComparison.Ordinal);
        int coordinateGuard = source.IndexOf("private async Task<bool> EnsureCranePositionsReadyForStartAsync", StringComparison.Ordinal);
        Assert.True(line1Start >= 0 && line2Start > line1Start && coordinateGuard > line2Start);

        string line1 = source[line1Start..line2Start];
        string line2 = source[line2Start..coordinateGuard];

        Assert.Contains("EnsureCranePositionsReadyForStartAsync", line1, StringComparison.Ordinal);
        Assert.Contains("EnsureCranePositionsReadyForStartAsync", line2, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureNoPendingManualActionsForLine", line1, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureNoPendingManualActionsForLine", line2, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the focused test and confirm it fails before implementation**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~LineStartLedgerGateContractTests`

Expected: FAIL because both toggle methods still call `EnsureNoPendingManualActionsForLine`.

### Task 2: Remove only the manual-action startup gate

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:1766-1770`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:1804-1808`

- [ ] **Step 1: Remove the 1号线 gate after the retained coordinate guard**

Delete exactly:

```csharp
if (!EnsureNoPendingManualActionsForLine(1))
    return;
```

- [ ] **Step 2: Remove the 2号线 gate after the retained coordinate guard**

Delete exactly:

```csharp
if (!EnsureNoPendingManualActionsForLine(2))
    return;
```

- [ ] **Step 3: Run the focused test and confirm it passes**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~LineStartLedgerGateContractTests`

Expected: PASS.

- [ ] **Step 4: Build the application**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: build succeeds with no errors.

- [ ] **Step 5: Commit**

```bash
git add -- Presentation/ViewModels/Home/HomeViewModel.cs AutomaticOnlineHostComputer.Tests/Presentation/LineStartLedgerGateContractTests.cs Docs/superpowers/plans/2026-09-04-remove-line-start-ledger-gate.md
git commit -m "fix: remove line start action ledger gate"
```
