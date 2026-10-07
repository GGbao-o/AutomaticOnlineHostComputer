# Crane Z-Zero Guard Before Pickup XY Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure four specified crane pickup entry paths prove Z is within 0±5mm before their first XY movement.

**Architecture:** Add one reusable guard method to `CraneService`; it performs live read, conditional Z return, and post-move verification. Add exactly four calls in the two front engines and grinding engine, leaving all other horizontal movement paths unchanged.

**Tech Stack:** C# 12, .NET 8 WPF, async/await, existing Modbus crane service.

---

### Task 1: Add the reusable CraneService Z-zero guard

**Files:**
- Modify: `Communication/DeviceServices/CraneService.cs` near `MoveAbsoluteAsync`

- [ ] **Step 1: Add `EnsureZAtZeroAsync`**

```csharp
public async Task EnsureZAtZeroAsync(int tolerance = 5, CancellationToken ct = default)
{
    if (tolerance < 0)
        throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Z零位容差不能小于0");

    var status = await ReadStatusAsync(ct);
    if (status == null)
        throw new InvalidOperationException($"[CraneService] [{_name}] 无法读取当前Z，禁止XY横移");

    if (Math.Abs(status.ZPos) <= tolerance)
    {
        Console.WriteLine($"[CraneService] [{_name}] Z零位检查通过: Z={status.ZPos}, 容差=±{tolerance}mm");
        return;
    }

    Console.WriteLine($"[CraneService] [{_name}] Z={status.ZPos}超出零位容差±{tolerance}mm，先回Z=0再允许XY");
    await MoveAbsoluteAsync(-1, -1, 0, tolerance: tolerance, ct: ct);

    var verified = await ReadStatusAsync(ct);
    if (verified == null)
        throw new InvalidOperationException($"[CraneService] [{_name}] Z回零后状态读取失败，禁止XY横移");
    if (Math.Abs(verified.ZPos) > tolerance)
        throw new InvalidOperationException($"[CraneService] [{_name}] Z回零复核失败: Z={verified.ZPos}, 容差=±{tolerance}mm，禁止XY横移");

    Console.WriteLine($"[CraneService] [{_name}] Z回零复核通过: Z={verified.ZPos}, 容差=±{tolerance}mm");
}
```

- [ ] **Step 2: Compile after the service change**

Run: `dotnet build AutomaticOnlineHostComputer.csproj -p:BaseOutputPath=.tmp/build-zguard/service/ -p:BaseIntermediateOutputPath=.tmp/obj-zguard/service/`

Expected: build completes with 0 errors. Existing warnings are allowed.

### Task 2: Guard both front-crane Pos3 pickup entry moves

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs` immediately before the first XY move to ST713/Pos3
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs` immediately before the first XY move to its Pos3 station

- [ ] **Step 1: Add the line 1 call**

```csharp
Console.WriteLine("[Line1Front] [前天车] 去货叉Pos3前确认Z=0±5mm");
await crane.EnsureZAtZeroAsync(5, ct);
await crane.MoveAbsoluteAsync(f2xOff, f2yOff, -1, ct: ct);
```

- [ ] **Step 2: Add the line 2 call**

```csharp
Console.WriteLine("[Line2Front] [前天车] 去货叉Pos3前确认Z=0±5mm");
await crane.EnsureZAtZeroAsync(5, ct);
await crane.MoveAbsoluteAsync(f2xOff, f2yOff, -1, ct: ct);
```

- [ ] **Step 3: Verify both calls occur before XY and before Z descent**

Run: `rg -n -C 4 "去货叉Pos3前确认Z=0|EnsureZAtZeroAsync|MoveAbsoluteAsync\(f2xOff" Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs`

Expected: each engine contains one guard immediately before its original XY move.

### Task 3: Guard both grinding-crane pickup entry moves

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs` before XY to ST709 in `ProcessWorkpieceAsync`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs` before XY to the grinder in `ProcessUnloadAsync`

- [ ] **Step 1: Add ST709 pickup guard**

```csharp
Console.WriteLine($"[GrindingEngine] [{craneName}] 去ST709取料前确认Z=0±5mm");
await crane.EnsureZAtZeroAsync(5, ct);
await crane.MoveAbsoluteAsync(ApplyOffsetX(rackX), ApplyOffsetY(rackY), -1, ct: ct);
```

- [ ] **Step 2: Add grinder unload pickup guard**

```csharp
Console.WriteLine($"[GrindingEngine] [{craneName}] 去{grinder.StationCode}下料取板前确认Z=0±5mm");
await crane.EnsureZAtZeroAsync(5, ct);
await CraneOpAsync(crane,
    c => crane.MoveAbsoluteAsync(ApplyOffsetX(gx), ApplyOffsetY(gy), -1, ct: c),
    "XY去研磨机取料位", ct);
```

- [ ] **Step 3: Verify only the two grinding pickup entries use the guard**

Run: `rg -n -C 4 "EnsureZAtZeroAsync|去ST709取料前确认Z|下料取板前确认Z" Service/FlowEngine/GrindingFlowEngine.cs`

Expected: exactly two grinding calls, before the existing XY commands.

### Task 4: Validate scope and compile the complete project

**Files:**
- Verify: `Communication/DeviceServices/CraneService.cs`
- Verify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Verify: `Service/FlowEngine/Line2FrontFlowEngine.cs`
- Verify: `Service/FlowEngine/GrindingFlowEngine.cs`

- [ ] **Step 1: Confirm the exact call count**

Run: `rg -n "EnsureZAtZeroAsync\(" Communication/DeviceServices/CraneService.cs Service/FlowEngine`

Expected: one method declaration and exactly four production call sites.

- [ ] **Step 2: Confirm unrelated exceptional horizontal paths were not changed**

Run: `git diff -- Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs`

Expected: no new diff from this task in either rear engine.

- [ ] **Step 3: Check patch formatting**

Run: `git diff --check`

Expected: no output and exit code 0.

- [ ] **Step 4: Build with isolated output paths**

Run: `dotnet build AutomaticOnlineHostComputer.csproj -p:BaseOutputPath=.tmp/build-zguard/final/ -p:BaseIntermediateOutputPath=.tmp/obj-zguard/final/`

Expected: 0 errors. Existing warnings are allowed.

- [ ] **Step 5: Review the final diff**

Run: `git diff -- Communication/DeviceServices/CraneService.cs Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs`

Expected: only one reusable method and four pre-XY calls; no coordinate, signal, timeout, lock, magnet, X11, or downstream sequence changes.
