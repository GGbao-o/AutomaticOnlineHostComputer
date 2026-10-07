# Crane Y Absolute Encoder Fine Tune Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a separately configurable, fail-safe Y absolute-encoder fine tune for cranes 1–5 before existing Z descent steps.

**Architecture:** Retain the existing call sites but generalize the fine-tune helper so every existing pre-descent check runs X first and Y second. Store Y settings in a separate JSON section that defaults to disabled and `-1` targets; extend the existing calibration page to edit and read both axes.

**Tech Stack:** C#/.NET 8 WPF, System.Text.Json configuration, Modbus-backed `CraneService`.

---

### Task 1: Add an independent Y calibration configuration

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs:42-173`
- Modify: `Config/motion_settings.json:235-287`

- [x] **Step 1: Define the Y configuration section**

```csharp
/// <summary>Y绝对编码器下降前微调参数；仅用于1～5号天车。</summary>
public AxisAbsFineTuneSection YAbsFineTune { get; set; } = new() { Enabled = false };
```

Use the same `AxisAbsFineTuneSection` shape as X: `Enabled`, `ToleranceMm`, `MaxAdjustMm`, and per-crane/per-station `StationTargets`, where `-1` skips the axis.

- [x] **Step 2: Add a safe JSON default**

```json
"yAbsFineTune": {
  "enabled": false,
  "toleranceMm": 5,
  "maxAdjustMm": 50,
  "stationTargets": { "1": { "ST713": -1 } }
}
```

Populate every existing X station for cranes 1–5 with `-1`; do not infer field calibration values.

### Task 2: Run X then Y before each existing Z descent

**Files:**
- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs:9-210`

- [x] **Step 1: Generalize the existing helper without changing its call-site contract**

```csharp
await VerifyAxisAndFineTuneAsync(
    crane, cfg, cfg.XAbsFineTune, craneNo, stationCode, context,
    "X", s => s.XPos, s => s.XEncoderAbs,
    (target, token) => crane.MoveAbsoluteAsync(target, -1, -1, ...), ct);
await VerifyAxisAndFineTuneAsync(
    crane, cfg, cfg.YAbsFineTune, craneNo, stationCode, context,
    "Y", s => s.YPos, s => s.YEncoderAbs,
    (target, token) => crane.MoveAbsoluteAsync(-1, target, -1, ...), ct);
```

The Y branch must preserve the existing five-sample stable window, two-attempt cap, post-move feedback-follow check, and exception wording that explicitly says Z descent is prohibited.

- [x] **Step 2: Verify the skip path**

With `yAbsFineTune.enabled=false` or target `-1`, the helper must log the skip and return without issuing a Y movement. With a configured value, Y runs only after X has passed.

### Task 3: Extend the calibration page and validate the build

**Files:**
- Modify: `Presentation/ViewModels/Config/ConfigPageViewModel.cs:25-268,454-473`
- Modify: `Views/Config/ConfigPageView.xaml:256-313`
- Modify: `Docs/整线技术手册.md`

- [x] **Step 1: Expose separate X/Y switches and parameters**

```csharp
public bool YAbsFineTuneEnabled { get => _cfg.YAbsFineTune.Enabled; set { ... } }
public int YAbsToleranceMm { get => _cfg.YAbsFineTune.ToleranceMm; set { ... } }
public int YAbsMaxAdjustMm { get => _cfg.YAbsFineTune.MaxAdjustMm; set { ... } }
```

Make each calibration row hold the X and Y dictionaries, expose `TargetX` and `TargetY`, and add `ReadCurrentYCommand` that writes `status.YEncoderAbs`.

- [x] **Step 2: Make the UI safety state visible**

Add Y target and read buttons beside X, and state that `-1` skips that axis and that Y must remain disabled until on-site calibration is complete.

- [x] **Step 3: Build and inspect the resulting configuration**

Run:

```powershell
dotnet build AutomaticOnlineHostComputer.csproj --no-restore --nologo --consoleloggerparameters:"ErrorsOnly;Summary"
```

Expected: exit code 0. Then parse `Config/motion_settings.json` with PowerShell `ConvertFrom-Json` and confirm `yAbsFineTune` has five crane keys and only `-1` targets.

### Task 4: Use a per-crane Y direction for fixed and dynamic targets

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs`
- Modify: `Config/motion_settings.json`
- Modify: `Service/FlowEngine/XAbsFineTuneHelper.cs`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`
- Modify: `Docs/全线业务逻辑详解.md`

- [x] **Step 1: Add per-crane Y direction configuration**

Add `absolutePerDisplayDirections` with Crane#1/#3/#4=`+1` and Crane#2=`-1`; retain `-1` only for unverified Crane#5. Reject values other than `-1` and `+1` whenever an active Y target uses them.

- [x] **Step 2: Apply the direction to every Y calculation**

Use these two formulas in the common helper:

```text
targetDisplayY = currentDisplayY + direction * (targetAbsY - currentAbsY)
dynamicTargetAbsY = baseTargetAbsY - direction * yOff
```

The existing feedback check already uses `expectedAbsMove = displayMove * direction`; it must receive the same per-crane value.

- [x] **Step 3: Keep the two rear-flow call sites semantic**

Rename the optional helper argument from an already-signed Abs offset to the positive center-to-pick `yOff`. Both rear engines pass the same positive geometric value; the helper owns the direction conversion.

- [x] **Step 4: Verify configuration, formulas and build**

Parse `Config/motion_settings.json`, assert Crane#2=`-1` and Crane#4=`+1`, check representative formulas (`-779 + 359 = -420`, `600 - 414 = 186`), then build the full WPF project with zero errors.
