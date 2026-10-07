# Crane Zero-Position Power-Cycle Guard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Execute this plan task-by-task in the current session. Do not delegate because the user requested direct implementation and the repository has no test project.

**Goal:** Before engine start/resume and before each crane task leaves its queue/cache, detect two consecutive valid XYZ=0 snapshots 500ms apart, pause the corresponding line/grinding engine, preserve the task, and show a Chinese UI warning.

**Architecture:** Add one shared read-only guard in the flow-engine layer. Engines invoke it immediately before dequeue/state mutation and raise a typed alarm callback; `HomeViewModel` owns cross-engine pausing, UI state synchronization, and dispatcher-safe `MessageBox` display. Communication failures (`ReadStatusAsync == null`) retain existing behavior and are not treated as XYZ=0.

**Tech Stack:** C# 12, .NET 8, WPF, async/await, existing `CraneService` and `HomeViewModel`.

---

### Task 1: Shared double-read guard

**Files:**
- Create: `Service/FlowEngine/CraneZeroPositionGuard.cs`

- [ ] Define immutable position/alarm records containing crane number, name, context, and both XYZ snapshots.
- [ ] Implement `CheckAsync`: read once; return healthy on `null` or any non-zero axis; only when the first snapshot is all-zero wait 500ms and read again; return an alarm only when the second valid snapshot is also all-zero.
- [ ] Add Chinese logs for transient first-zero recovery and confirmed two-sample zero detection.

### Task 2: Front-crane task guards

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [ ] Add `Action<CraneZeroPositionAlarm>? OnCraneZeroPositionDetected` to both engines.
- [ ] In each front-crane dispatch worker, run the guard before `TryDequeue`; on confirmed zero set `_paused=true`, invoke the callback, leave the queue unchanged, and exit the worker so its `finally` releases `_craneFrontLock`.
- [ ] Preserve existing workpiece order and all normal movement/handshake code.

### Task 3: Rear-crane task guards

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] Add the same typed alarm callback.
- [ ] Add a helper that checks rear crane #2/#4 after the rear-crane lock is acquired but before bed state is changed, cache is removed, or `DoLoad`/`DoUnload` is started.
- [ ] On confirmed zero set `_paused=true`, invoke the callback, and release only the just-acquired rear/transfer locks owned by the current dispatch attempt.
- [ ] Do not alter shared-area acquisition, PLC handshake, destination selection, or normal motion order.

### Task 4: Grinding-crane task guards

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`

- [ ] Add the typed alarm callback.
- [ ] Under the existing dispatch gate, check crane #5 after `_craneLock` is acquired and before setting `Unloading`, before FIFO dequeue, and before setting `Loading/PendingWorkpiece`.
- [ ] On confirmed zero set `_paused=true`, invoke the callback, release `_craneLock`, and preserve FIFO/grinder state.

### Task 5: Start/resume validation and UI alarm

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] Convert line toggle methods to awaitable validation without blocking the UI thread.
- [ ] Before line 1 start/resume check crane #1 then #2; before line 2 check #3 then #4; before grinding check #5.
- [ ] On confirmed zero: pause both front/rear engines for the matching line (or grinding engine), set the matching `Is...Running=false`, log crane/context/two snapshots, and show one dispatcher-safe Chinese warning popup.
- [ ] Wire all engine alarm callbacks to the same centralized handler.
- [ ] When no alarm is confirmed, retain the existing Start/Resume branches unchanged.

### Task 6: Documentation and verification

**Files:**
- Modify: `Docs/天车通信异常_超时重试与锁恢复分析.md`
- Modify: `Docs/全线业务逻辑详解.md`

- [ ] Document the two-sample rule, crane-to-engine mapping, task-preservation behavior, popup, and the limitation that this pre-dispatch guard does not stop an already-running physical motion.
- [ ] Run `dotnet build AutomaticOnlineHostComputer.csproj --no-restore -clp:ErrorsOnly`; expect exit code 0.
- [ ] Run `git diff --check`; expect exit code 0.
- [ ] Inspect the final diff and confirm no PLC addresses, handshake order, destination rules, or motion order changed.
