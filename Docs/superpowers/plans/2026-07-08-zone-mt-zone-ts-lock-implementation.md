# ZoneMT / ZoneTS Collision Lock Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the coarse `SharedAreaLock` workflow with `ZoneMT` and `ZoneTS` collision locks for marker/transfer/skew1 interactions, matching the confirmed site rule that transfer-rack lock release happens once Z is back to 0/safe height.

**Architecture:** `SafetyFlags` exposes two collision locks: marker-transfer (`ZoneMT`) and transfer-skew1 (`ZoneTS`). Front engines hold `ZoneMT` for marker work, then acquire `ZoneTS` for transfer-rack placement and release both after Z returns safe. Rear engines acquire both zones for transfer-rack pickup, release both for normal skew beds after Z is safe, or release `ZoneMT` and keep `ZoneTS` for ST108/ST606 until skew1 retreat completes.

**Tech Stack:** C#/.NET 8 WPF service layer, existing `SemaphoreSlim`, `EmergencyActionLease`, `CraneService`, `dotnet build`.

---

### Task 1: Replace shared collision lock definitions

**Files:**
- Modify: `Service/FlowEngine/SafetyFlags.cs`

- [ ] Add `MarkerTransferCollisionLock` and `TransferSkew1CollisionLock`.
- [ ] Keep `SharedAreaLock` unavailable to new code; all flow-engine references must move to Zone locks.
- [ ] Dispose both new locks.

### Task 2: Update front crane lock flow for both lines

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [ ] Replace pre-marker `SharedAreaLock` with `ZoneMT`.
- [ ] Remove front `_transferRackLock` acquisition from the normal marker/transfer flow.
- [ ] After marker pickup, acquire `ZoneTS` before transfer-rack placement.
- [ ] In `PlaceOnTransferRackAsync`, make the second check use only transfer-rack physical plate signals; do not reject placement because software cache already exists.
- [ ] After transfer-rack placement and Z safe, release `ZoneTS` then `ZoneMT`; then continue home movement.
- [ ] Update safety-alarm text from “中转架锁/共享区锁” to “ZoneMT/ZoneTS碰撞区锁”.

### Task 3: Update rear load dispatch and transfer-rack pickup

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] Remove main-loop `_transferRackLock` gating for load dispatch.
- [ ] Let `DoLoad` acquire `ZoneMT → ZoneTS` before reading/removing transfer-rack cache.
- [ ] Under Zone locks, re-run `FindLoadPair` or equivalent cache/physical validation; rear pickup must require both physical plate signal and software cache.
- [ ] For normal beds, after X11 confirms pickup and Z safe, release `ZoneTS` and `ZoneMT`.
- [ ] For ST108/ST606, after X11 confirms pickup and Z safe, release `ZoneMT` only, keep `ZoneTS` through skew1 load handshake and X+1000 retreat.
- [ ] Update comments and console logs to explain the site rule.

### Task 4: Update rear unload for ST108/ST606

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] Replace ST108/ST606 unload `SharedArea` acquisition with `ZoneTS`.
- [ ] Keep current X+1000 retreat logic for ST108/ST606 before normal `ZoneTS` release.
- [ ] Update finally cleanup and alarm text to say `ZoneTS` instead of shared area.

### Task 5: Update skew emergency diagnostics and release

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] Emergency info displays `ZoneMT` and `ZoneTS` lock status.
- [ ] `ActiveOperationText` shows held `ZoneMT`/`ZoneTS`.
- [ ] Emergency release attempts release only locks recorded by `EmergencyActionLease`.
- [ ] Remove `TransferRack` release from skew emergency unless a legacy holder remains during transition.

### Task 6: Update docs affected by implementation

**Files:**
- Modify: `Docs/03-设备交互流程/前后天车相邻碰撞区锁设计_ZoneMT_ZoneTS.md`
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`
- Modify: `Docs/引擎异常暂停_缓存锁与恢复流程详解.md`

- [ ] Synchronize final code behavior: Z safe releases transfer-rack zone locks, ST108/ST606 retains only `ZoneTS`, emergency releases recorded zone locks.

### Task 7: Verify

**Commands:**
- [ ] Run `dotnet build AutomaticOnlineHostComputer.csproj -clp:ErrorsOnly`.
- [ ] Run `rg -n "SharedAreaLock|SharedArea|共享区锁|中转架锁已释放|_transferRackLock" Service/FlowEngine -S` and inspect remaining references.
- [ ] Run `rg -n "ZoneMT|ZoneTS|MarkerTransferCollisionLock|TransferSkew1CollisionLock" Service/FlowEngine -S` to verify both lines use the new names.
