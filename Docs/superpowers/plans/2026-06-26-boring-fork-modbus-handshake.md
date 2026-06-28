# Boring Fork Modbus Handshake Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the old Syntec SDK and R6105/R6109/M903/M915 boring handshake with the new ModbusTCP R-area protocol and updated fork actions for both front-line engines.

**Architecture:** Add a focused Modbus address/service pair for the double-head boring machine, update both front-line fork state machines to use R6101/R6102/R6103/R6104/R6107/R6108 only, keep SkipBoring gated by boring connectivity and R6101, and refresh UI/docs to show the new signals. The fork service remains Mitsubishi MC but drops the obsolete boring-position and M915 command from active logic.

**Tech Stack:** C#, WPF/MVVM, Mitsubishi MC client, Modbus TCP client, markdown documentation.

---

### Task 1: Add New Boring Modbus Address And Service

**Files:**
- Create: `Communication/DeviceAddresses/BoringModbusAddress.cs`
- Create: `Communication/DeviceServices/BoringModbusService.cs`
- Modify: `Communication/DeviceServices/SyntecBoringService.cs`

- [ ] Add R-to-Modbus address constants for R2041/R2043/R2044/R2045/R2046/R2047/R2048 and R6101/R6102/R6103/R6104/R6107/R6108.
- [ ] Implement Modbus-backed reads/writes: length as float, diameter/plug/bore values as integers multiplied by 100, and handshake done bits written as 1 only.
- [ ] Keep a compatibility wrapper or remove old Syntec references from callers so no front-line logic depends on Syntec SDK for boring.

### Task 2: Update Fork Service To New M900-M914 Protocol

**Files:**
- Modify: `Communication/DeviceAddresses/ForkAddress.cs`
- Modify: `Communication/DeviceServices/ForkService.cs`

- [ ] Treat M900/M901/M902 as the only active fork feedback signals.
- [ ] Treat M911/M912/M913/M914 as the only active fork command signals.
- [ ] Remove M903/M915 from logs, status text, command masks, and public command methods used by the engines.

### Task 3: Rewrite Line 1 Front Fork/Boring State Machine

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`

- [ ] Replace `SyntecBoringService` with `BoringModbusService`.
- [ ] Keep manipulator-to-fork dispatch blocked unless boring is connected and `R6101=1`, including SkipBoring jobs.
- [ ] Normal flow: R6101 -> write params -> R6102 -> R6103 -> M912 -> wait M901/M900=0 -> R6104 -> wait R6107 -> M913 -> wait M902/M900=1 -> R6108 -> crane queue.
- [ ] SkipBoring flow: M914 -> wait M902/M900=1 -> crane queue, no R writes except the precondition read.
- [ ] Remove R6105/R6106/R6109/R6110 and M903/M915 branches.

### Task 4: Mirror The Same State Machine In Line 2 Front

**Files:**
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [ ] Apply the same service, condition, and state machine changes as Line 1.
- [ ] Keep line-specific station IDs, cache names, logs, and crane queues unchanged.

### Task 5: Update UI Status

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`
- Modify: `Views/Home/Dialogs/ClearMachineStatusDialog.xaml.cs`

- [ ] Boring cards show R6101/R6103/R6107 and optionally R6102/R6104/R6108 readback.
- [ ] Fork cards show M900/M901/M902 and active M911/M912/M913/M914 commands.
- [ ] Emergency clear dialog no longer references R6105/R6106/R6109/R6110 as active process signals.

### Task 6: Update Documentation

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/货叉与双头镗交互流程.md`
- Modify: `Docs/02-通信协议手册/双头镗协议_Syntec.md`
- Modify: `Docs/02-通信协议手册/货叉及辅助PLC协议_三菱MC.md`
- Modify: other docs found by searching old R6105/R6109/M915/M903 protocol references.

- [ ] Document Modbus address formula `R*2+1`.
- [ ] Document the parameter scaling rules and explicitly mark R2046/R2048 as defined but not written.
- [ ] Document the new normal and SkipBoring flows, including SkipBoring requiring boring connection and R6101=1 before manipulator dispatch.

### Task 7: Verify

**Files:**
- Run project build and targeted searches.

- [ ] Run `dotnet build AUtomaticOnlineHostComputer-chengdu/AUtomaticOnlineHostComputer-chengdu.csproj`.
- [ ] Search for active old protocol references in code: `R6105`, `R6106`, `R6109`, `R6110`, `M915`, `AtBoringPos`, `GoBoringToPos3`.
- [ ] Search docs for old protocol references and leave only historical/deprecated mentions if any remain.
