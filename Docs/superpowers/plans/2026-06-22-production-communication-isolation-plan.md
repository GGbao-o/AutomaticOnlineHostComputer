# Production Communication Isolation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent a disconnected Syntec/MC device or idle polling loop from blocking both production lines while preserving all existing PLC addresses, handshakes, caches, and motion order.

**Architecture:** Isolate each synchronous Syntec SDK client on one dedicated long-lived worker and expose bounded async calls. Keep MC63 reconnection outside the shared manipulator lock, avoid lock acquisition by lines without queued work, and guarantee grinder wait branches are rate-limited.

**Tech Stack:** .NET 8, WPF, C#, Syntec OpenCNC SDK, Mitsubishi MC, Modbus TCP.

---

### Task 1: Isolate Syntec SDK calls

**Files:**
- Modify: `Communication/Clients/SyntecCncClient.cs`
- Modify: `Communication/DeviceServices/SyntecBoringService.cs`

- [x] Replace per-call `Task.Run` with one dedicated worker per client.
- [x] Add bounded caller waits and reject new calls after a worker hard timeout.
- [x] Keep SDK calls serialized and mark the client disconnected after communication failures.

### Task 2: Reduce shared manipulator lock scope

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [x] Skip the shared lock when the line has no cached workpiece.
- [x] Check boring readiness before acquiring the shared manipulator lock.
- [x] On MC63 read failure, release the lock and let the existing bounded snapshot reconnect path recover outside the lock.

### Task 3: Rate-limit grinder waiting paths

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`

- [x] Add the configured poll delay before `continue` when no grinder is ready.
- [x] Add the configured poll delay before `continue` when ST709/M730 has no plate.

### Task 4: Verification

- [x] Build `AutomaticOnlineHostComputer.csproj` and require zero errors.
- [x] Inspect all four changed paths for stale tasks, double release, duplicate reconnect, and altered business conditions.
- [x] Report source file count and added/deleted line totals.
