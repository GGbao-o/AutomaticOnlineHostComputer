# Crane Y Encoder Signed INT Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Interpret D5020 and D5021 as signed PLC INT16 values so Y absolute-encoder calibration and fine tuning use real negative values.

**Architecture:** Change only the raw `CraneStatus` construction in `CraneService.ReadStatusAsync`. The public properties remain C# `int`; existing calibration and fine-tune consumers continue using them without special wraparound logic.

**Tech Stack:** C#/.NET 8, Modbus `int[]` register response, WPF calibration UI.

---

### Task 1: Correct the Y encoder register conversions

**Files:**
- Modify: `Communication/DeviceServices/CraneService.cs:137-142,1110-1113`

- [x] **Step 1: Preserve the signed 16-bit PLC representation**

```csharp
YEncoderAbs  = (short)r[20],   // D5020（INT16，自动提升为C# int）
YEncoderZero = (short)r[21],   // D5021（INT16，自动提升为C# int）
YPos         = (ushort)r[22],  // D5022显示坐标保持现有无符号语义
```

Update the two `CraneStatus` comments from `ushort→int` to `INT16→int`. Do not change X, Z, address constants, property types, or the Y calibration/fine-tune algorithm.

- [x] **Step 2: Verify all consumers share the corrected value**

Run:

```powershell
rg -n 'YEncoderAbs|YEncoderZero|YPos' Communication/DeviceServices/CraneService.cs Presentation/ViewModels/Config/ConfigPageViewModel.cs Service/FlowEngine/XAbsFineTuneHelper.cs
```

Confirm the calibration page reads `status.YEncoderAbs` and the fine-tune helper uses the same field.

- [x] **Step 3: Build and check the implementation diff**

Run:

```powershell
dotnet build AutomaticOnlineHostComputer.csproj --no-restore --nologo --consoleloggerparameters:"ErrorsOnly;Summary"
git diff --check
```

Expected: build exit code 0 and no diff-check errors. Confirm source has `(short)r[20]`, `(short)r[21]`, and `(ushort)r[22]`.

- [x] **Step 4: Commit only the signed-Y implementation files**

Stage `Communication/DeviceServices/CraneService.cs`, this plan, and the matching design specification only; do not stage unrelated workspace changes. Commit with:

```powershell
git commit -m "fix: read crane y encoder as signed int"
```
