# Front Crane Exception Phase Preservation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure line 1 and line 2 front-crane/marker exceptions pause safely and release all locks without resetting or clearing the concurrently active fork–boring state.

**Architecture:** Keep ownership boundaries strict: `ProcessCraneFrontAsync` owns crane-task error reporting and pause decisions, while the fork state machine exclusively owns `_forkPhase` and its handshake latches. Preserve existing `finally` blocks for `_transferRackLock`, `SharedAreaLock`, and `_craneFrontLock`.

**Tech Stack:** C# 12, .NET 8 WPF, `SemaphoreSlim`, existing flow-engine logging and safety-alarm callbacks.

---

### Task 1: Protect existing worktree changes

**Files:**
- Inspect: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Inspect: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [ ] **Step 1: Capture the existing diff around front-crane exception handling**

Run:

```powershell
git diff -- Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs
```

Expected: Existing user changes are visible and retained; only the targeted exception blocks will be patched.

- [ ] **Step 2: Locate every front-crane assignment that can reset fork state**

Run:

```powershell
rg -n "_forkPhase = ForkBoringPhase.Idle|_boringUnloadDoneSent = false|_m912Sent = false|_skipBoringTriggered = false" Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs
```

Expected: Results include legitimate normal state-machine reset sites and the two exceptional front-crane branches per line. Only the exceptional branches are in scope.

### Task 2: Preserve line 1 fork–boring state on front-crane exceptions

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs` in `ProcessCraneFrontAsync` catch branches

- [ ] **Step 1: Remove cross-domain fork reset assignments from both pausing branches**

Delete these assignments only from the `TransferRackCacheException` and `magnetOn` branches:

```csharp
_forkPhase = ForkBoringPhase.Idle;
_boringUnloadDoneSent = false;
_m912Sent = false;
_skipBoringTriggered = false;
```

- [ ] **Step 2: Add explicit preservation diagnostics**

Use the current phase in both console and popup text:

```csharp
Console.WriteLine($"[Line1Front] [前天车] 货叉—双头镗阶段保持={_forkPhase}，相关门闩未清除；人工确认后恢复原阶段");
```

The popup must state that the line is paused, the fork phase is preserved, locks are released by `finally`, and the operator must confirm the old workpiece and crane safe position before resuming.

- [ ] **Step 3: Confirm lock handling remains unchanged**

Verify these existing releases remain present:

```csharp
if (transferRackLocked) _transferRackLock.Release();
_safety.SharedAreaLock.Release();
_craneFrontLock.Release();
```

### Task 3: Preserve line 2 fork–boring state on front-crane exceptions

**Files:**
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs` in `ProcessCraneFrontAsync` catch branches

- [ ] **Step 1: Remove cross-domain fork reset assignments from both pausing branches**

Delete these assignments only from the `TransferRackCacheException` and `magnetOn` branches:

```csharp
_forkPhase = ForkBoringPhase.Idle;
_boringUnloadDoneSent = false;
_m912Sent = false;
_skipBoringTriggered = false;
```

- [ ] **Step 2: Add line 2 preservation diagnostics**

```csharp
Console.WriteLine($"[Line2Front] [前天车] 货叉—双头镗阶段保持={_forkPhase}，相关门闩未清除；人工确认后恢复原阶段");
```

The popup must carry the same recovery requirements as line 1.

- [ ] **Step 3: Confirm lock handling remains unchanged**

Verify `_transferRackLock`, `SharedAreaLock`, and `_craneFrontLock` are still released by their existing `finally` blocks.

### Task 4: Update exception-recovery documentation

**Files:**
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`

- [ ] **Step 1: Add the confirmed concurrency failure scenario**

Document this sequence explicitly:

```text
上一块工件在前天车/打号机流程中发生异常
→ 下一块工件已进入货叉—双头镗并拥有独立阶段
→ 前天车异常只暂停线体和释放自己的锁
→ 不得重置下一块工件的货叉阶段或门闩
→ 人工处理旧工件及天车安全位置后恢复原阶段
```

- [ ] **Step 2: Document lock and resume semantics**

State that the transfer-rack lock, shared-area lock, and front-crane lock are released; `Resume()` clears only `_paused`; a preserved `WaitingMachine` stage resumes polling `R6107` and can then issue `M913`.

### Task 5: Static verification and build

**Files:**
- Verify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Verify: `Service/FlowEngine/Line2FrontFlowEngine.cs`
- Verify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`

- [ ] **Step 1: Inspect the targeted diff**

Run:

```powershell
git diff -- Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md
```

Expected: Only exception handling, comments, diagnostics, and documentation change; normal coordinates, signals, sequence, and timeout values remain unchanged.

- [ ] **Step 2: Verify exceptional branches no longer reset fork state**

Run focused source inspection around both `ProcessCraneFrontAsync` catch blocks. Expected: no `_forkPhase=Idle` or handshake-latch clear in either pausing branch; normal return-to-standby reset sites remain intact.

- [ ] **Step 3: Build the application**

Run:

```powershell
dotnet build AutomaticOnlineHostComputer.csproj --no-restore
```

Expected: Build succeeds with zero errors. Existing unrelated warnings may remain and must be reported separately.

