# MC64 Unload Rack M720-Only Handshake Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the MC64 grinding unload rack use only M720 as its placement permission and only M721 as the host completion signal, removing MC64 M730 gating and MC64 D200 writes.

**Architecture:** Keep the existing two-stage safety check: one M720 check before dispatch and another immediately before moving to ST710. Keep the existing physical order `demagnetize -> raise Z safe -> write M721 -> notify grinder unload complete`. Restrict all changes to MC64 at `192.168.2.64:9000`; MC65 M730/M731/D200 remain unchanged.

**Tech Stack:** C# 12, .NET 8 WPF, Mitsubishi MC 1E communication, Markdown documentation.

---

### Task 1: Change the MC64 unload handshake

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`

- [x] Change the periodic MC64 connection description from `M720/M730 + M721/D200` to `M720 + M721`.
- [x] In the main-loop unload precheck, derive readiness only from M720 bit 0 while retaining connection-failure invalidation and conservative waiting.
- [x] In `ConfirmGrindingUnloadRackReadyAsync`, retain the aligned M720-word read but return readiness from M720 bit 0 only.
- [x] Keep the second check immediately before the ST710 move.
- [x] After demagnetizing and raising Z safe, write only M721 bit 1 and set `unloadRackNotified=true` after that write succeeds.
- [x] Remove the MC64 D200 diameter write and update exception/log text to mention only M721.
- [x] Do not change MC65 M730/M731/D200, grinder motion, or grinder PLC handshake order.

### Task 2: Align the ST710 status cards

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [x] In both MC64 card-refresh paths, read the existing aligned M720 word once.
- [x] Display M720 bit 0 as placement permission and M721 bit 1 as placement completion.
- [x] Stop reading and displaying MC64 M730 and MC64 D200.
- [x] Leave the MC65 ST709 card and its M730/D200 reads unchanged.

### Task 3: Update active protocol and business documentation

**Files:**
- Modify: `Docs/02-通信协议手册/货叉及辅助PLC协议_三菱MC.md`
- Modify: `Docs/02-通信协议手册/通信矩阵总览.md`
- Modify: `Docs/03-设备交互流程/整线引擎流程总览_按代码.md`
- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/全线设备运动前置条件检查表.md`
- Modify: `Docs/整线技术手册.md`
- Modify: `Docs/日志审查与故障定位手册.md`
- Modify: `Docs/引擎异常暂停_缓存锁与恢复流程详解.md`

- [x] Define MC64 M720 as the sole placement permission and M721 as the sole host completion write.
- [x] Remove MC64 M730 and MC64 D200 from ST710 conditions, diagnostics, and recovery wording.
- [x] Preserve every MC65 M730/M731/D200 description.

### Task 4: Verify scope and compilation

**Files:**
- Verify: all modified files above

- [x] Search for remaining MC64/ST710 text that requires M730 or writes D200.
- [x] Confirm remaining M730/D200 references belong to MC65 or generic protocol examples.
- [x] Run `dotnet build AutomaticOnlineHostComputer.csproj` and require exit code 0.
- [x] Review the final diff to confirm no PLC addresses, movement order, grinder handshake, or unrelated business conditions changed.
