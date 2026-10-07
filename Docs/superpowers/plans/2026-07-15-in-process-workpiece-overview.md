# In-Process Workpiece Overview Implementation Plan

> **For agentic workers:** Execute inline in the current session. Subagent execution is disabled for this repository task, and the user explicitly requested no Git commit.

**Goal:** Add a visible-only, read-only page that aggregates every current in-memory workpiece identity and highlights rack identity mismatches or multiple software records without changing production control.

**Architecture:** Immutable snapshot records isolate the UI from engines. Front and balancing engines expose synchronous memory-copy getters; `HomeViewModel` combines those with existing rear/grinding snapshots, then a page ViewModel filters the resulting fixed-size list. M2/M3 receive versioned display-only fields that are never read by control logic.

**Tech Stack:** C# 10, .NET 8 WPF, existing `ObservableObject`, `ICollectionView`, `DispatcherTimer`, existing flow-engine snapshot types.

---

### Task 1: Immutable overview snapshot model

**Files:**
- Create: `Service/FlowEngine/InProcessWorkpieceSnapshots.cs`

- [ ] Define `InProcessWorkpieceKind` with `GlobalQueue`, `FrontCache`, `FrontCurrent`, `FrontCraneQueue`, `CraneTask`, `TransferRack`, `ProcessStation`, `BalancingCache`, `BalancingInTransit`, `GrindingCache`, and `SourceFailure`.
- [ ] Define `InProcessWorkpieceStatus` with `Normal`, `Handoff`, `SignalMismatch`, `EvidenceUnavailable`, and `ManualConfirmation`.
- [ ] Define immutable `InProcessWorkpieceSnapshot` containing kind, filter scope, flow scope, location, stage, `WorkpieceDisplaySnapshot?`, evidence text, status, detail, and captured time. Add `IdentityText`, `IdentityKey`, `HasKnownIdentity`, `DiameterText`, `LengthText`, and `StatusText` display properties.
- [ ] Define immutable `InProcessWorkpieceOverviewSnapshot` containing the item array and captured time.
- [ ] Add XML comments explicitly prohibiting device connections, callbacks, locks, and control commands in these records.
- [ ] Run `dotnet build AutomaticOnlineHostComputer.csproj --no-restore` and require zero errors.

### Task 2: 1/2 line front read-only workpiece snapshots

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [ ] Add `GetInProcessWorkpieceSnapshots()` to each engine.
- [ ] Copy `_cachedList` under `_cacheLock`, `_currentWp` under `_wpLock`, `_craneQueue` via `ToArray()`, and `_frontCraneTask` under its existing lock. Never hold two locks simultaneously.
- [ ] Map cache rows to `FrontCache`, current workpiece to `FrontCurrent`, queued crane rows to `FrontCraneQueue`, and an active crane task with a workpiece to `CraneTask`.
- [ ] Add a private pure formatter for `_forkPhase`; it may read existing phase flags but must not write any field or call a service.
- [ ] Confirm with `rg` that the new method contains no `await`, protocol service, enqueue/dequeue, start/pause/resume, or register write.
- [ ] Build and require zero errors.

### Task 3: Versioned M2/M3 display-only identity

**Files:**
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs`

- [ ] Under `_emergencyLock`, add `_m2DisplayWorkpiece`, `_m2DisplayStage`, `_m2DisplayVersion`, `_m3DisplayWorkpiece`, `_m3DisplayStage`, and `_m3DisplayVersion`, with comments that control code must never read them.
- [ ] Add `SetM2Display(version, workpiece, stage)`, `SetM3Display(...)`, `ClearM2Display(version)`, and `ClearM3Display(version)`. Set methods update only when the action version is current; clear methods clear only the same display version.
- [ ] In M2, set display identity immediately after `trackedWorkpiece` is confirmed; update after X11 confirms pickup and after placing on M710; clear in `finally` before `FinishM2Action`.
- [ ] In M3, create an unknown-identity display workpiece for M700 using its D100 diameter, or use the M821 cached workpiece; update after X11, M720 placement, and grinding-cache notification; clear in `finally` before `FinishM3Action`.
- [ ] Add `GetInProcessWorkpieceSnapshots()` that separately copies `_balWps` under `_balWpsLock` and the two display contexts under `_emergencyLock`, never nesting those locks.
- [ ] Verify no existing condition, state transition, lock decision, PLC write, or exception branch reads a display field.
- [ ] Build and require zero errors.

### Task 4: HomeViewModel aggregation

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] Add `GetInProcessWorkpieceOverviewSnapshot()`.
- [ ] Copy `_frontDispatchQueue` under `_frontDispatchLock` and add `GlobalQueue` rows.
- [ ] Add front snapshots from both engines, then combine existing rear transfer-rack, skew-station and crane-task snapshots.
- [ ] Add balancing snapshots, grinding cache, grinder-station, and grinding-crane snapshots.
- [ ] For rack physical signal available/identity mismatch, generate `SignalMismatch`; for unavailable physical signal use `EvidenceUnavailable`; for manual station recovery use `ManualConfirmation`.
- [ ] Group only nonblank `PlateNo + Sequence`. If a key has more than one row, change only `Normal` rows to `Handoff` and append a yellow multi-record explanation; preserve mismatch/manual statuses.
- [ ] Catch each source independently and emit one `SourceFailure` row without Console logging.
- [ ] Build and statically inspect that aggregation calls only snapshot getters and memory fields.

### Task 5: Page ViewModel, UI and visible-only refresh

**Files:**
- Create: `Presentation/ViewModels/Home/InProcessWorkpieceOverviewViewModel.cs`
- Create: `Views/InProcessWorkpieces/InProcessWorkpieceOverviewView.xaml`
- Create: `Views/InProcessWorkpieces/InProcessWorkpieceOverviewView.xaml.cs`
- Modify: `Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`

- [ ] ViewModel exposes an observable source list, filtered `ICollectionView`, scope/status options, search text, seven KPI counts, and last-refresh text.
- [ ] `Refresh()` calls only `HomeViewModel.GetInProcessWorkpieceOverviewSnapshot()`, replaces the fixed-size collection on the UI thread, calculates counts, and refreshes the view.
- [ ] XAML follows the existing runtime-diagnostics style, with KPI cards, three filters, a read-only virtualized DataGrid, yellow handoff/evidence rows and red mismatch/manual rows.
- [ ] Code-behind owns a one-second `DispatcherTimer`; it refreshes immediately on show, starts only while visible, and stops on hide/unload.
- [ ] Register `InProcessWorkpieceOverviewViewModel` as a singleton with a comment that it owns no device connection.
- [ ] Build and require XAML compilation with zero errors.

### Task 6: Navigation

**Files:**
- Modify: `Infrastructure/Navigation/CachedNavigationService.cs`
- Modify: `MainWindow.xaml`

- [ ] Add the `inProcessWorkpieces` cached page mapping.
- [ ] Insert “在制工件” between “运行诊断” and “异常监控”.
- [ ] Verify both files contain `flowStatus`, `runtimeDiagnostics`, `inProcessWorkpieces`, `attentionMonitor` in the same order.

### Task 7: Documentation

**Files:**
- Modify: `Docs/整线技术手册.md`
- Modify: `Docs/日志审查与故障定位手册.md`
- Modify: `Docs/03-设备交互流程/整线引擎流程总览_按代码.md`

- [ ] Document page fields, lifecycle-only memory scope, multiple-record warning semantics, unknown identity boundary, and visible-only one-second refresh.
- [ ] Document the fault workflow: open the page before emergency action, record all software positions, compare physical rack signals, then use logs/field confirmation.
- [ ] Document identity storage locations at global FIFO, front cache/current/crane queue, rear racks/beds/crane, balancing cache/display context, and grinding cache/machines/crane.
- [ ] State in all three documents that snapshots never participate in control or add device communication.

### Task 8: Final safety review

**Files:** Review all files above.

- [ ] Run a fresh full build with summary and compare the warning count with the current baseline of 660.
- [ ] Scan new model/ViewModel/View files for protocol clients, `ConnectAsync`, device reads/writes, database calls, emergency/control commands, and background task creation.
- [ ] Inspect the exact M2/M3 diff and confirm display fields appear only in set/update/clear/snapshot code.
- [ ] Inspect navigation order, timer start/stop, and documentation wording.
- [ ] Review the production diff for any modified condition, lock acquisition/release, state transition, timeout, handshake address, or command write.
- [ ] Leave all changes uncommitted as requested.
