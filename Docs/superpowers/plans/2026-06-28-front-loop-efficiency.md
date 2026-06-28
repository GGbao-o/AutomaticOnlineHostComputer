# Front Loop Efficiency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Execute each checkbox in order and verify each behavior-preserving batch.

**Goal:** Reduce duplicate front-loop communication and isolate blocking marker-share probes without changing production behavior.

**Architecture:** Keep both front engines and all existing device ownership intact. Reuse only same-cycle observation snapshots, add a dedicated marker-share monitor, and preserve every action-time safety confirmation.

**Tech Stack:** C# 12, .NET 8 WPF, Mitsubishi MC, Modbus TCP, Windows UNC shares.

---

### Task 1: Reuse the M816 word already read

**Files:** `Communication/DeviceServices/CenteringRackService.cs`, both front engines.

- [x] Add `RawM816Word` populated from `raw2`.
- [x] Replace the four adjacent `ReadM816Async` calls with the current `CenteringRackStatus` value.
- [x] Keep independent reads in unrelated action-time confirmation paths.

### Task 2: Same-cycle front snapshots

**Files:** both front engines.

- [x] Read ordinary fork status once per cycle and reuse it for dispatch, state machine and UI.
- [x] Keep `ConfirmForkReadyForManipulatorAsync` as a direct read.
- [x] Read the boring signal block once per cycle for R6101, R6107 and UI.
- [x] Keep R6103 polling direct and invalidate fork observations after motion commands.

### Task 3: Marker share monitor

**Files:** create `Communication/DeviceServices/MarkerShareMonitor.cs`; modify both front engines.

- [x] Use one background thread per share; never overlap probes.
- [x] Store success, capture time and error; stale success becomes disconnected.
- [x] Start/stop with the engine and keep pre-mark direct checks unchanged.

### Task 4: Logging and cycle diagnostics

**Files:** rack service, both front engines, grinding engine.

- [x] Throttle unchanged successful/waiting states by value change or 10-second heartbeat.
- [x] Preserve action and exception logs.
- [x] Add throttled IO-cycle duration warnings excluding normal delays.

### Task 5: Crane mapping compatibility

**File:** `Communication/DeviceServices/CraneConnectionCache.cs`.

- [x] Prefer `TypeName=天车` plus exact name.
- [x] Keep name-only legacy fallback with a warning.
- [x] Warn on duplicate exact mappings.

### Task 6: Documentation and verification

**Files:** `Docs/整线技术手册.md` and implementation records.

- [x] Document same-cycle snapshots, marker probe isolation and slow-cycle diagnostics.
- [x] Run focused static scans and full Rebuild.
- [x] Confirm source/docs whitespace checks excluding generated build output.
