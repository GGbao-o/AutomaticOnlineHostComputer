# Y Absolute Encoder Reverse Direction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct Y-axis absolute-encoder fine tuning so the displayed Y coordinate moves in the inverse direction while X behavior stays unchanged.

**Architecture:** `XAbsFineTuneHelper` already models X and Y as axis descriptors. Add a descriptor-level direction factor so movement-target calculation and post-move follow verification use the same mapping. The existing shared stable sampling, safety limits, and one-call XY motion command remain unchanged.

**Tech Stack:** C#, .NET, WPF upper-computer service layer, `dotnet build`.

---

## File structure

- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs` — maps per-axis display movement to absolute-encoder movement.
- Modify: `Docs/全线业务逻辑详解.md` — records the X/Y direction distinction for on-site diagnosis.
- Create: `Docs/superpowers/specs/2026-07-18-y-abs-direction-design.md` — records the confirmed calculation and scope.
- Create: `Docs/superpowers/plans/2026-07-18-y-abs-direction.md` — this implementation record.

### Task 1: Make movement and feedback direction-aware

**Files:**
- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs`

- [x] **Step 1: Add an absolute-per-display direction to the axis descriptor.**

```csharp
private sealed record AxisFineTune(
    string Name,
    MotionConfig.AxisAbsFineTuneSection Settings,
    Func<CraneStatus, int> DisplayPosition,
    Func<CraneStatus, int> AbsoluteEncoder,
    int AbsolutePerDisplayDirection);

new AxisFineTune("X", config.XAbsFineTune, status => status.XPos, status => status.XEncoderAbs, +1);
new AxisFineTune("Y", config.YAbsFineTune, status => status.YPos, status => status.YEncoderAbs, -1);
```

- [x] **Step 2: Convert absolute correction delta to display correction delta before composing `MoveAbsoluteAsync`.**

```csharp
int deltaDisplay = correction.Delta * correction.Axis.AbsolutePerDisplayDirection;
int targetDisplay = correction.Axis.DisplayPosition(status) + deltaDisplay;
```

- [x] **Step 3: Validate feedback using the same direction factor.**

```csharp
int expectedAbsMove = displayMove * axis.AbsolutePerDisplayDirection;
int followError = absMove - expectedAbsMove;
if (Math.Abs(followError) > allowedError)
{
    reason = $"显示{axis.Name}与Abs{axis.Name}的位移关系不符";
    return false;
}
```

### Task 2: Document the operational mapping

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`

- [x] **Step 1: Add the confirmed equations and ST105 example.**

```text
X: TargetDisplay = CurrentDisplay + (TargetAbs - CurrentAbs)
Y: TargetDisplay = CurrentDisplay - (TargetAbs - CurrentAbs)
```

- [x] **Step 2: State that feedback expects X absolute movement to match display movement and Y absolute movement to be its inverse.**

### Task 3: Verify and commit the focused change

**Files:**
- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs`
- Modify: `Docs/全线业务逻辑详解.md`

- [x] **Step 1: Build the main project.**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore --nologo --consoleloggerparameters:"ErrorsOnly;Summary"`

Expected: exit code `0`; warnings may remain from the existing project.

- [x] **Step 2: Check the patch for whitespace errors and inspect the direction expressions.**

Run: `git diff --check` and `rg -n "AbsolutePerDisplayDirection|deltaDisplay|expectedAbsMove" Service/FlowEngine/XAbsFineTuneHelper.cs`

Expected: no `git diff --check` output; X is `+1`, Y is `-1`, and both movement and feedback use the same factor.

- [x] **Step 3: Commit only the helper and documentation files for this formula correction.**

```bash
git add -- Service/FlowEngine/XAbsFineTuneHelper.cs Docs/全线业务逻辑详解.md Docs/superpowers/specs/2026-07-18-y-abs-direction-design.md Docs/superpowers/plans/2026-07-18-y-abs-direction.md
git commit -m "fix: reverse y encoder fine tune direction"
```
