# Crane XY Absolute Encoder Synchronized Fine Tune Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace sequential X-then-Y absolute-encoder correction with synchronized XY sampling and one-command XY correction for cranes 1–5.

**Architecture:** Keep every existing `VerifyAndFineTuneAsync` call site and configuration shape. Refactor `XAbsFineTuneHelper` to operate on enabled/configured axis descriptors: one shared stable `CraneStatus` window validates all participating axes, then a single `CraneService.MoveAbsoluteAsync` carries the X and/or Y correction. Per-axis feedback-follow validation remains independent after the shared movement.

**Tech Stack:** C#/.NET 8 WPF, `CraneService`, existing Modbus crane status feedback, `MotionConfig`.

---

### Task 1: Replace sequential axis operations with an XY operation model

**Files:**
- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs:27-252`

- [x] **Step 1: Define an axis descriptor and resolve participating axes**

```csharp
private sealed record AxisFineTune(
    string Name,
    MotionConfig.AxisAbsFineTuneSection Settings,
    Func<CraneStatus, int> DisplayPosition,
    Func<CraneStatus, int> AbsoluteEncoder);
```

Create X and Y descriptors at the start of `VerifyAndFineTuneAsync`. Resolve a target only when that axis is enabled and `TryGetTarget(...)` returns a value other than `-1`; log the existing skip reason for disabled or `-1` axes. If neither axis participates, return without reading status or moving.

- [x] **Step 2: Read one shared five-sample stable window**

Replace the single-axis `ReadStableStatusAsync` call with a method that accepts the participating descriptors. On each `ReadStatusAsync` sample, retain the last five statuses; only return when every participating descriptor has display and absolute ranges within `StableReadToleranceMm`. Preserve the 20-attempt cap and 200 ms cadence. Include X and Y values in one sampling log line.

- [x] **Step 3: Fail before movement on any axis over its own correction limit**

From the shared stable status calculate `delta = targetAbs - AbsEncoder(status)` per participating axis. Return only when all deltas are within their own tolerance. Before issuing a movement, inspect every noncompliant delta and throw the existing Z-descent-prohibited exception style when any `Abs(delta)` exceeds that axis `MaxAdjustMm` (with tolerance as a lower bound).

### Task 2: Issue one XY command and retain per-axis safety checks

**Files:**
- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs:27-252`

- [x] **Step 1: Construct the shared movement command**

```csharp
int targetX = xNeedsCorrection ? status.XPos + xDelta : -1;
int targetY = yNeedsCorrection ? status.YPos + yDelta : -1;
await crane.MoveAbsoluteAsync(targetX, targetY, -1,
    tolerance: Math.Max(1, Math.Max(xTolerance, yTolerance)),
    timeoutMs: cfg.AbsMove.TimeoutMs, ct: ct);
```

Only put a target into an axis slot when that axis is noncompliant; if both are noncompliant this is one XY command, and if exactly one is noncompliant the other is `-1`.

- [x] **Step 2: Revalidate after the shared movement**

Use `max(Abs(xDelta), Abs(yDelta))` across the axes moved in that command to calculate the existing post-move settle delay. Re-read the shared stable window, then independently call feedback-follow validation for each axis that moved. Repeat from delta calculation until all axes pass or two movement attempts have been used; on exhaustion throw an exception listing every remaining noncompliant axis and explicitly prohibit Z descent.

- [x] **Step 3: Preserve disabled and single-axis behavior**

Confirm that an enabled/configured X with Y disabled or `-1` produces the same X-only command behavior as before, and that the inverse produces Y-only. Do not modify `MotionConfig`, JSON defaults, UI code, or flow-engine call sites.

### Task 3: Document and verify the refactor

**Files:**
- Modify: `Docs/全线业务逻辑详解.md:1022-1040`
- Modify: `Docs/superpowers/plans/2026-07-17-crane-xy-abs-fine-tune.md`

- [x] **Step 1: Update operational documentation**

Replace the wording that describes “X then Y” with: shared XY stable sampling; a single XY movement only when both axes need correction; `-1` for an already-qualified axis; common recheck after each movement; and Z descent only after all participating axes qualify.

- [x] **Step 2: Compile and inspect the safety-sensitive code paths**

Run:

```powershell
dotnet build AutomaticOnlineHostComputer.csproj --no-restore --nologo --consoleloggerparameters:"ErrorsOnly;Summary"
git diff --check
```

Expected: build exit code 0; `git diff --check` exit code 0. Inspect the helper source to confirm there is exactly one `MoveAbsoluteAsync` call in the correction loop, it receives both X and Y targets, and every exception path returns/throws before the calling flow can descend Z.

- [x] **Step 3: Mark completed plan steps and commit only implementation-owned files**

Stage `Service/FlowEngine/XAbsFineTuneHelper.cs`, `Docs/全线业务逻辑详解.md`, and this plan only. Do not stage unrelated existing workspace changes. Commit with:

```powershell
git commit -m "feat: synchronize crane xy encoder fine tune"
```
