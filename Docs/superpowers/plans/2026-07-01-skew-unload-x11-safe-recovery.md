# Skew Unload X11 Safe Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Execute inline in the current session; the user already approved the bounded design.

**Goal:** When rear-crane unloading exhausts all X11 pickup attempts without detecting a plate, demagnetize and raise Z before releasing the crane lock; continue automatically only when both recovery actions succeed.

**Architecture:** Change only the terminal X11-failure branch in both rear engines. Successful recovery keeps the workpiece and `WaitingUnload` for a later retry. Failed demagnetize/Z recovery pauses the matching full line and raises one dispatcher-safe UI alarm through `HomeViewModel`.

**Tech Stack:** C# 12, .NET 8, WPF, existing rear-engine action lease and crane service.

---

### Task 1: Rear-engine recovery branches

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] Add a rear-crane safety-alarm callback to each engine.
- [ ] On the final failed X11 read, attempt `MagnetOffAsync`, then move Z to configured safe height while the rear-crane lock is still held.
- [ ] If both operations succeed, keep `bed.Wp`, set/retain `WaitingUnload`, do not pause, and allow the existing loop to retry.
- [ ] If either operation fails, set `_paused=true`, preserve `bed.Wp/WaitingUnload`, emit detailed logs, and invoke the safety-alarm callback.
- [ ] Correct the terminal error/log text to match the actual three X11 reads.

### Task 2: Main-page alarm

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] Wire both rear-engine callbacks during engine initialization.
- [ ] Immediately pause both front and rear engines for the affected line.
- [ ] Set the matching UI running state to false and show a Chinese error popup on the WPF dispatcher.

### Task 3: Documentation and verification

**Files:**
- Modify: `Docs/03-设备交互流程/后天车与斜床交互流程.md`
- Modify: `Docs/全线业务逻辑详解.md`

- [ ] Document successful automatic safe recovery versus failed recovery requiring manual intervention.
- [ ] Build `AutomaticOnlineHostComputer.csproj` and require zero errors.
- [ ] Run `git diff --check` and inspect the final diff for unintended PLC, handshake, or normal-motion changes.

### Task 4: Operator-visible final alarms

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] Show one warning per skew-bed fault cycle when three X11 reads fail but safe recovery succeeds.
- [ ] Raise operator alerts only from final conditions that pause an engine or require manual confirmation.
- [ ] Keep polling, reconnect and transient read failures log-only.
- [ ] Deduplicate line/balancing/grinding safety popups until the operator starts or resumes that engine.
