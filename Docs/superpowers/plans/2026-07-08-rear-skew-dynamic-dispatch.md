# Rear Skew Dynamic Dispatch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the fixed rear-crane “unload first, then load” loop with a rule-based dispatcher that chooses load or unload from explicit candidates.

**Architecture:** Keep normal `DoLoad` and `DoUnload` business steps unchanged. Add a small shared decision helper that only decides `Load`, `Unload`, or `None` from candidate counts, then let each line engine build line-specific load/unload candidates and dispatch exactly one action per rear-crane cycle.

**Tech Stack:** C# / .NET 8 WPF service layer, existing `Line1RearFlowEngine` and `Line2RearFlowEngine`, existing `WorkpieceCache`, `SkewBed` config, MC/FANUC/Modbus services.

---

### Task 1: Add shared rule-based decision helper

**Files:**
- Create: `Service/FlowEngine/RearSkewDispatchPlanner.cs`

- [ ] **Step 1: Create `RearSkewDispatchPlanner`**

Add a helper with no device dependencies. Inputs are already-computed candidate counts.

Important site assumption: transfer racks are stable and have fixed capacity 3, but skew beds may be offline or broken. Therefore rack-pressure thresholds remain absolute (`physicalRackPlateCount >= 3`, `matchableRackPlateCount >= 2`), while unload-pressure must be scaled by the number of usable skew beds in this line.

```csharp
namespace AutomaticOnlineHostComputer.Service;

internal enum RearSkewDispatchAction
{
    None,
    Load,
    Unload
}

internal readonly record struct RearSkewDispatchDecision(
    RearSkewDispatchAction Action,
    string Reason);

internal static class RearSkewDispatchPlanner
{
    public static RearSkewDispatchDecision Decide(
        int loadCandidateCount,
        int unloadCandidateCount,
        int physicalRackPlateCount,
        int matchableRackPlateCount,
        int usableBedCount)
    {
        int highUnloadThreshold = GetHighUnloadThreshold(usableBedCount);

        if (loadCandidateCount <= 0 && unloadCandidateCount <= 0)
            return new RearSkewDispatchDecision(RearSkewDispatchAction.None, "无可上料候选且无可下料候选");

        if (unloadCandidateCount <= 0 && loadCandidateCount > 0)
            return new RearSkewDispatchDecision(RearSkewDispatchAction.Load, "无下料候选但存在上料候选");

        if (loadCandidateCount <= 0 && unloadCandidateCount > 0)
            return new RearSkewDispatchDecision(RearSkewDispatchAction.Unload, "无上料候选但存在下料候选");

        if (physicalRackPlateCount >= 3 && loadCandidateCount > 0)
            return new RearSkewDispatchDecision(RearSkewDispatchAction.Load, "中转架物理满3个且存在上料候选, 强制上料腾位置");

        if (matchableRackPlateCount >= 2 && unloadCandidateCount < highUnloadThreshold)
            return new RearSkewDispatchDecision(RearSkewDispatchAction.Load, $"中转架可匹配板数>=2且下料候选少于高压力阈值{highUnloadThreshold}, 优先上料");

        if (unloadCandidateCount >= highUnloadThreshold && matchableRackPlateCount <= 1)
            return new RearSkewDispatchDecision(RearSkewDispatchAction.Unload, $"下料候选达到高压力阈值{highUnloadThreshold}且中转架可匹配板数<=1, 优先下料");

        return new RearSkewDispatchDecision(RearSkewDispatchAction.Unload, "普通场景默认下料优先");
    }

    private static int GetHighUnloadThreshold(int usableBedCount)
    {
        if (usableBedCount <= 1) return 1;
        if (usableBedCount <= 3) return 2;
        return 3;
    }
}
```

### Task 2: Encapsulate line-specific candidates

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] **Step 1: Add private candidate DTOs in each rear engine**

Add small private classes near `LoadPair`:

```csharp
private sealed class UnloadCandidate
{
    public SkewCtx Bed = null!;
    public WorkpieceCache Wp;
}

private sealed class LoadCandidateScan
{
    public List<LoadPair> Candidates { get; } = new();
    public int PhysicalRackPlateCount { get; set; }
    public int MatchableRackPlateCount { get; set; }
}

private sealed class UnloadCandidateScan
{
    public List<UnloadCandidate> Candidates { get; } = new();
    public int UsableBedCount { get; set; }
    public bool HasActiveUnloading { get; set; }
    public bool HasRearCraneZeroDetected { get; set; }
}
```

- [ ] **Step 2: Refactor current unload scan into `BuildUnloadCandidateScanAsync`**

Move the current step 4 scan into a method. It may still:

- update `Machining -> WaitingUnload` when `RqUnload=1`;
- write completion export before allowing a candidate;
- check destination availability exactly as today;
- skip candidates with missing `bed.Wp`;
- set `HasActiveUnloading` for beds already `Unloading`.

It must not take the rear crane lock or call `DoUnload`.

`UsableBedCount` should count beds that are connected, have fresh signal, and are not in an emergency/recovery-broken condition. This count is only used to scale the high-unload-pressure threshold; broken/offline beds must not make the dispatcher wait for an impossible `unloadCandidateCount >= 3`.

- [ ] **Step 3: Refactor current load scan into `BuildLoadCandidateScan`**

Use existing rack arrival ordering from `GetTransferRackCodesWithPlateSnapshot`.

Rules:

- count all physical rack plates as `PhysicalRackPlateCount`;
- for each rack in arrival order, require software cache;
- if the rack workpiece cannot be processed by any currently ready bed, log and skip it;
- if it can be processed, increment `MatchableRackPlateCount` and add the first compatible bed as a load candidate;
- therefore a first rack plate that is too long for all ready beds does not block a later rack plate that can be processed.

### Task 3: Replace fixed priority with one decision point

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] **Step 1: In the main loop, build both scans first**

Replace the old “step 4 immediately dispatch unload, then step 5 dispatch load” with:

```csharp
var unloadScan = await BuildUnloadCandidateScanAsync(fds, logDetails, ct);
if (unloadScan.HasRearCraneZeroDetected) continue;

var loadScan = BuildLoadCandidateScan(logDetails);

var decision = RearSkewDispatchPlanner.Decide(
    loadScan.Candidates.Count,
    unloadScan.Candidates.Count,
    loadScan.PhysicalRackPlateCount,
    loadScan.MatchableRackPlateCount,
    unloadScan.UsableBedCount);

Console.WriteLine($"│ [后调度] 决策={decision.Action} 原因={decision.Reason} " +
                  $"上料候选={loadScan.Candidates.Count} 下料候选={unloadScan.Candidates.Count} " +
                  $"中转架物理板={loadScan.PhysicalRackPlateCount} 可匹配板={loadScan.MatchableRackPlateCount} " +
                  $"有效斜床={unloadScan.UsableBedCount}");
```

- [ ] **Step 2: Dispatch exactly one selected action**

If decision is `Unload`, take the first unload candidate in existing bed order, acquire rear crane lock, check zero-position guard, set `Bed.St = Unloading`, and call `DoUnload`.

If decision is `Load`, take the first load candidate in existing rack-arrival order, acquire rear crane lock, check zero-position guard, rebuild the load scan after taking the lock, then dispatch the first candidate from the fresh scan.

If rear crane lock is busy, log and do not fall through to the other action in the same cycle.

### Task 4: Documentation and verification

**Files:**
- Modify: `Docs/03-设备交互流程/后天车与斜床交互流程.md`
- Modify: `Docs/03-设备交互流程/前后天车相邻碰撞区锁设计_ZoneMT_ZoneTS.md`

- [ ] **Step 1: Document dynamic dispatch rules**

Add the six decision rules in order, explicitly saying the algorithm preserves:

- front-crane transfer rack arrival order for matchable plates;
- `skewBed.maxWorkpieceLengthMm` capacity checks;
- existing `DoLoad` and `DoUnload` normal business steps.

- [ ] **Step 2: Build**

Run:

```powershell
dotnet build AutomaticOnlineHostComputer.csproj -clp:ErrorsOnly
```

Expected:

```text
0 个错误
```

- [ ] **Step 3: Search for decision helper use**

Run:

```powershell
rg -n "RearSkewDispatchPlanner|BuildLoadCandidateScan|BuildUnloadCandidateScanAsync|后调度" Service\FlowEngine -S
```

Expected: references in both `Line1RearFlowEngine.cs` and `Line2RearFlowEngine.cs`.
