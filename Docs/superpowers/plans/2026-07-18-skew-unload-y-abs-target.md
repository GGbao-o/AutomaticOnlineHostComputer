# Dynamic Y Absolute Target for Skew Unload Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Use a workpiece-specific Y absolute-encoder target when rear cranes pick a part from a skew bed for unloading.

**Architecture:** Keep each station configuration as the Y absolute-encoder value at its fixed database coordinate. Rear-crane skew-unload callers pass the positive geometric `yOff`; the shared helper converts it with a per-crane Y direction. Fixed-coordinate callers retain the zero default.

**Tech Stack:** C#, .NET, WPF upper-computer service layer, `dotnet build`.

---

## File structure

- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs` — applies the optional Y absolute target offset and reports base/dynamic targets.
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs` — passes the computed `yOff` at 1-line rear skew-unload verification.
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs` — passes the computed `yOff` at 2-line rear skew-unload verification.
- Modify: `Docs/全线业务逻辑详解.md` — documents the dynamic skew-unload Y target equation.

### Task 1: Represent base and dynamic Y targets in the helper

**Files:**
- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs`

- [x] **Step 1: Add optional `int yTargetAbsOffsetMm = 0` after the cancellation token in `VerifyAndFineTuneAsync`.**

```csharp
public static async Task VerifyAndFineTuneAsync(
    CraneService crane, MotionConfig cfg, int craneNo, string stationCode,
    string context, CancellationToken ct, int yTargetAbsOffsetMm = 0)
```

- [x] **Step 2: Preserve the configured target as `BaseTargetAbs` and compute Y `TargetAbs` as `BaseTargetAbs + yTargetAbsOffsetMm`.**

```csharp
int baseTargetAbs = axis.Settings.StationTargets[craneNo][stationCode];
int targetAbs = axis.Name == "Y" ? baseTargetAbs + yTargetAbsOffsetMm : baseTargetAbs;
activeAxes.Add(new ActiveAxisFineTune(axis, baseTargetAbs, targetAbs));
```

- [x] **Step 3: Include base Y target, dynamic offset, and dynamic target in correction and over-limit logs.**

```text
基准AbsY=-779, 动态偏移=+170mm, 动态目标AbsY=-609
```

### Task 2: Supply the per-workpiece offset only for skew unloading

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:1742`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:1701`

- [x] **Step 1: Pass the existing `yOff` value by name after each dynamic `yPick` movement.**

```csharp
await XAbsFineTuneHelper.VerifyAndFineTuneAsync(
    cr, _cfg, CraneRearNo, bed.Code,
    $"1号线后天车-{bed.Code}下料取料前", ct,
    yTargetAbsOffsetMm: yOff);
```

- [x] **Step 2: Leave every fixed-coordinate verification call unchanged so its optional offset remains zero.**

### Task 3: Document and verify the calculation

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`

- [x] **Step 1: Record `动态目标AbsY = 基准AbsY + yOff` for rear-crane skew unloading and the ST112 `-779 + 170 = -609` example.**

- [x] **Step 2: Build the primary project.**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore --nologo --consoleloggerparameters:"ErrorsOnly;Summary"`

Expected: exit code `0`.

- [x] **Step 3: Verify source expressions and patch whitespace.**

Run: `git diff --check` and `rg -n "yTargetAbsOffsetMm|baseTargetAbs|动态目标Abs" Service/FlowEngine/XAbsFineTuneHelper.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs`

Expected: `yOff` appears only at the two rear skew-unload callers; X uses its unchanged configured target.

- [ ] **Step 4: Commit only these helper, rear-engine, and documentation files.**

```bash
git add -- Service/FlowEngine/XAbsFineTuneHelper.cs Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Docs/全线业务逻辑详解.md Docs/superpowers/specs/2026-07-18-skew-unload-y-abs-target-design.md Docs/superpowers/plans/2026-07-18-skew-unload-y-abs-target.md
git commit -m "fix: use dynamic y encoder target for skew unload"
```

### Revision 2026-07-20: Per-crane direction

- [x] Rename the helper argument to `yCenterToPickOffsetMm` so callers pass an unsigned geometric offset rather than an already-signed Abs offset.
- [x] Read Crane#1/#3/#4=`+1` and Crane#2=`-1` from `yAbsFineTune.absolutePerDisplayDirections`; Crane#5 remains unverified at `-1`.
- [x] Replace the old global addition with `targetAbsY = baseTargetAbsY - direction * yCenterToPickOffsetMm`.
- [x] Reuse the same direction for display correction and feedback-follow validation.
