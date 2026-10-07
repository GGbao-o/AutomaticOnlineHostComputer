# Skew Load Command Clear Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clear only a selected skew bed's stale upper-computer command outputs before the rear crane picks the selected transfer-rack workpiece.

**Architecture:** Add narrow command-output clearing methods to the existing Modbus and FANUC skew-bed services. Both rear engines call the relevant method after temporarily reserving the selected workpiece cache but before any crane movement; a failed clear restores that cache and releases the rear-crane dispatch lock.

**Tech Stack:** C#/.NET 8, Modbus TCP, FANUC FOCAS, existing rear flow engines.

---

### Task 1: Add protocol-scoped command clear methods

**Files:**
- Modify: `Communication/DeviceServices/ModbusSkewBedService.cs:177-195`
- Modify: `Communication/DeviceServices/FanucSkewBedService.cs:555-582`

- [x] **Step 1: Clear only Modbus output registers 10370–10374**

```csharp
public Task ClearUpperComputerCommandOutputsAsync(CancellationToken ct = default)
    => SafeCallAsync(async token =>
    {
        await _client.WriteAsync(ModbusSkewBedAddress.MachiningMode, 0, token);
        await _client.WriteAsync(ModbusSkewBedAddress.TailstockClampCmd, 0, token);
        await _client.WriteAsync(ModbusSkewBedAddress.RemoteStart, 0, token);
        await _client.WriteAsync(ModbusSkewBedAddress.TailstockOpenCmd, 0, token);
        await _client.WriteAsync(ModbusSkewBedAddress.TailstockStopCmd, 0, token);
    }, "上料前清零10370~10374", ct);
```

- [x] **Step 2: Clear only FANUC output macros #1101–#1105**

```csharp
public Task ClearUpperComputerCommandOutputsAsync(CancellationToken ct = default)
    => SafeCallAsync(() =>
    {
        foreach (int address in new[] { 1101, 1102, 1103, 1104, 1105 })
            _sdk.SetMacro(address, 0.0);
    }, "FANUC上料前清零#1101~#1105", ct);
```

### Task 2: Gate both rear-engine loads before crane pickup

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:1240-1275`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:1216-1251`

- [x] **Step 1: Add a per-engine service adapter**

```csharp
private static async Task ClearSkewLoadCommandOutputsAsync(SkewCtx bed, CancellationToken ct)
{
    if (bed.F != null) { await bed.F.ClearUpperComputerCommandOutputsAsync(ct); return; }
    if (bed.M != null) { await bed.M.ClearUpperComputerCommandOutputsAsync(ct); return; }
    throw new InvalidOperationException($"{bed.Code}斜床通信服务未注入，无法清理上料前命令输出");
}
```

- [x] **Step 2: Restore cache on clear failure before any crane command**

```csharp
try { await ClearSkewLoadCommandOutputsAsync(bed, ct); }
catch (Exception ex)
{
    lock (_wpLock) RestoreRackWorkpieceLocked(rs, wp, rackArrivalSeq);
    _craneRearLock.Release();
    bed.St = SkewState.Idle;
    Console.WriteLine($"[上料] {bed.Code} 上料前命令清理失败，未取板：{ex.Message}");
    return;
}
```

Place this after length validation and before `BeginSkewOperation`, zone-lock acquisition, crane connection, or any movement.

### Task 3: Document and verify

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`

- [x] **Step 1: Document the pre-pick gate**

State the exact protocol ranges, that parameters are preserved, and that clear failure leaves the board cache in place and sends no crane command.

- [x] **Step 2: Build and statically inspect both engines**

Run:

```powershell
dotnet build AutomaticOnlineHostComputer.csproj --no-restore --nologo --consoleloggerparameters:"ErrorsOnly;Summary"
```

Expected: exit code 0. Then search for both `ClearSkewLoadCommandOutputsAsync` call sites and confirm they occur before the first `CraneService` creation in each `DoLoad`.
