# Flow Status Reliability Implementation Plan

> **For agentic workers:** Implement each checkbox in order and verify the display-only boundary after every source edit.

**Goal:** Make the full-line status page show complete, current and unambiguous equipment state without changing production behavior.

**Architecture:** Keep `HomeViewModel.StationCards` as the sole UI data source. Add only snapshot-valid metadata to the two front engines, consolidate shared-card presentation in `HomeViewModel`, and use wider wrapping templates for long handshake states in XAML.

**Tech Stack:** C# 12, .NET 8, WPF XAML, existing `ObservableObject` bindings.

---

### Task 1: Persist production-readiness findings

**Files:**
- Create: `Docs/生产上线前待办与优化清单.md`
- Modify: `Docs/README.md`

- [x] Record blocking, onsite-verification and later-optimization items.
- [x] Add the document to the operations and safety navigation.

### Task 2: Make double-ended boring snapshots trustworthy

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [x] Add a UI-only `BoringSnapshotValid` field to both device-status snapshots.
- [x] Set it true only after a complete six-signal read and false on disconnect/read failure.
- [x] Render disconnected and read-failed states separately.

### Task 3: Consolidate shared and balancing card presentation

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [x] Give ST001/ST002 one deterministic display owner while retaining both-line data.
- [x] Centralize ST019/ST020/ST021 source selection and clear stale state after stop.
- [x] Log status synchronization exceptions without touching engine behavior.

### Task 4: Expand long status cards

**Files:**
- Modify: `Views/FlowStatus/FlowStatusView.xaml`

- [x] Add a wrapping detailed-node template.
- [x] Use it for ST001, ST103, ST402, ST020 and ST021.
- [x] Split ST020/ST021 into separate cards and expose full status text in tooltips.
- [x] Add a read-only motion-device strip for all five cranes and three manipulators using existing view models.

### Task 5: Verify

- [x] Parse `FlowStatusView.xaml` as XML.
- [x] Enumerate all station bindings and compare them with card initialization.
- [x] Build `AutomaticOnlineHostComputer.csproj` with zero errors.
- [x] Review the final diff and confirm no command, handshake, lock or state-machine code changed.
