# Boring ERP Taper and Corner Parameters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the fixed R2046/R2048 values with validated ERP/manual task values carried through the existing workpiece pipeline.

**Architecture:** Add `InnerTaper` and `CornerSize` as typed task/workpiece fields, populate them from strict 13-column ERP input or the manual dialog, and pass them through both front-line engines to `BoringModbusService`. Keep every existing dispatch, motion, lock, SkipBoring and R610 handshake branch unchanged.

**Tech Stack:** C# 12, .NET 8 WPF, Modbus TCP, Markdown.

---

### Task 1: Extend task input models and ERP parsing

**Files:**
- Modify: `Presentation/ViewModels/Home/TaskRowViewModel.cs`
- Modify: `Presentation/ViewModels/Home/ErpTaskImportParser.cs`
- Modify: `Presentation/ViewModels/Home/AddTaskDialogViewModel.cs`

- [ ] Add `double InnerTaper` and `double CornerSize` to `TaskRowViewModel`.
- [ ] Change `ErpTaskImportParser` to require exactly 13 columns, parse indexes 11 and 12 with invariant culture, and reject values outside `(0, 655.35]`.
- [ ] Populate the two task-row fields from ERP.
- [ ] Add manual input properties defaulting to 10 and 8.
- [ ] Validate both manual values with the same `(0, 655.35]` range and copy them in `ToTaskRow()`.

### Task 2: Add manual and main-page display fields

**Files:**
- Modify: `Views/Home/Dialogs/AddTaskDialog.xaml`
- Modify: `Views/Home/HomeView.xaml`

- [ ] Add editable “内孔锥度” and “圆角大小” rows to the add-task dialog and increase its usable height/rows without changing its commands.
- [ ] Add read-only task-grid columns bound to `InnerTaper` and `CornerSize`.

### Task 3: Carry values through the workpiece pipeline

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] Add `InnerTaper` and `CornerSize` to `WorkpieceCache`.
- [ ] Copy both fields in `HomeViewModel.BuildWorkpieceCache`.
- [ ] Preserve both fields in any explicit `WorkpieceCache` copy that starts from an existing workpiece.
- [ ] Set existing built-in/manual test workpieces that can reach the boring flow to 10 and 8.

### Task 4: Replace the fixed boring values

**Files:**
- Modify: `Communication/DeviceServices/BoringModbusService.cs`
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [ ] Remove `DefaultInnerTaper` and `DefaultCornerSize`.
- [ ] Extend `SendMachiningParamsAsync` with `innerTaper` and `cornerSize` before the cancellation token.
- [ ] Validate and scale both values to 16-bit register integers using `Math.Round(value * 100, AwayFromZero)`.
- [ ] Write the scaled values to the existing R2046 and R2048 addresses.
- [ ] Pass the workpiece fields from both front-line engines and log original/scaled values.
- [ ] Leave SkipBoring and every R610/fork state transition unchanged.

### Task 5: Update active documentation

**Files:**
- Modify: `Docs/02-通信协议手册/双头镗协议_Syntec.md`
- Modify: `Docs/03-设备交互流程/货叉与双头镗交互流程.md`
- Modify: `Docs/全线设备运动前置条件检查表.md`
- Modify: `Docs/整线技术手册.md`
- Modify: `Docs/全线业务逻辑详解.md`

- [ ] Document the strict 13-column ERP format and validation.
- [ ] Replace “R2046固定1000/R2048固定800” with task value ×100.
- [ ] State that manual defaults are 10/8 but editable, and SkipBoring does not write these values.

### Task 6: Verify behavior and scope

**Files:**
- Verify all modified files above.

- [ ] Search for active fixed `R2046=1000`, `R2048=800`, `DefaultInnerTaper` and `DefaultCornerSize` references; require none outside historical plans/specs.
- [ ] Search both line engines to confirm the new fields are passed to the boring service.
- [ ] Search MC65/MC64 and rear/balancing/grinding logic to confirm no unrelated business conditions changed.
- [ ] Run `dotnet build AutomaticOnlineHostComputer.csproj` and require exit code 0.
- [ ] Run `git diff --check` and inspect the final diff.

