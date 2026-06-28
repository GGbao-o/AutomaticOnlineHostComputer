# Low-Risk Reliability Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Execute each task in order and verify after every behavior-preserving batch.

**Goal:** Improve failure safety, lifecycle management, logging efficiency, configuration durability and maintainability without changing production handshakes or normal action order.

**Architecture:** Keep all existing flow engines and device-service ownership intact. Apply narrow guards and lifecycle controls at existing boundaries, remove unused position persistence, and centralize only address constants and documentation facts.

**Tech Stack:** C# 12, .NET 8 WPF, Modbus TCP, Mitsubishi MC, JSON configuration.

---

### Task 1: Crane safety and polling logs

**Files:**
- Modify: `Communication/DeviceServices/CraneService.cs`
- Modify: `Communication/DeviceServices/ForkService.cs`

- [x] Reject a Z movement when the current Z snapshot cannot be read.
- [x] Throttle successful status snapshots by value change or periodic heartbeat.
- [x] Keep all error and command-write logs unchanged.
- [x] Do not activate the unreachable movement compensation branch.

### Task 2: Remove position persistence

**Files:**
- Modify: `Presentation/ViewModels/Home/CraneCardViewModel.cs`
- Modify: `Presentation/ViewModels/Home/ManipulatorCardViewModel.cs`

- [x] Remove `PositionUpdateService` fields, constructor parameters and update calls.
- [x] Update every construction site to match the new constructors.
- [x] Preserve UI coordinate display and device polling.

### Task 3: Atomic configuration save

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs`

- [x] Serialize to a temporary file in the same directory.
- [x] Deserialize the temporary file as validation.
- [x] Replace the formal file with a backup when it exists; move when it does not.
- [x] Delete a leftover temporary file on failure and rethrow.

### Task 4: Grinder polling lifecycle

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [x] Add a grinder polling CTS and tracked task list.
- [x] Pass cancellation through connection, reads and delays.
- [x] Cancel and await old grinder loops before rebuilding production context.
- [x] Dispose the loop-owned service when the loop exits.

### Task 5: Diagnostics and address constants

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`
- Modify: `Communication/DeviceAddresses/CenteringRackAddress.cs`
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs`

- [x] Log swallowed fork-dispatch/status exceptions without changing fallback state.
- [x] Define M817~M826 constants and their aligned-word bit offsets.
- [x] Replace only the new rear-rack raw bit offsets at focused read/write sites.
- [x] Remove stale comments and malformed XML comments.

### Task 6: Documentation and verification

**Files:**
- Modify: `Docs/02-通信协议手册/研磨机协议_ModbusTCP.md`
- Modify: `Docs/03-设备交互流程/天车_调度逻辑.md`
- Modify: `Docs/天车机械手运动逻辑与安全机制.md`
- Modify: `Docs/整线技术手册.md`
- Modify: `Docs/全线业务逻辑详解.md`

- [x] Correct grinder stuck-timeout behavior to pause and preserve state.
- [x] Remove claims that compensation and Z step-down are active behavior.
- [x] Document that equipment coordinates are displayed but no longer persisted by cards.
- [x] Run stale-description searches, `git diff --check`, and a full Rebuild.
