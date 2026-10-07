# Line2 Manipulator Two-Stage Return Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make 2号线机械手1 match 1号线 behavior: after placing a plate on the fork, move to the line-specific fork-release safe Y, write M801 to release the fork, then keep the shared manipulator lock and return to the pickup standby Y before releasing the lock.

**Architecture:** Reuse the existing configuration fields `Manipulator1Line2SafeY` and `Manipulator1PickupStandbyY`. Copy the established 1号线 second-stage helper into `Line2FrontFlowEngine` with Line2-specific logs and `GetManipulator1SafeYForLine(2)`, then call it after M801 succeeds and `_manipulatorClearedFork=true`. Update the production config default to make Line2 release Y different from pickup standby Y.

**Tech Stack:** C#/.NET 8 WPF application, existing `MotionConfig`, JSON motion configuration, Markdown docs.

---

### Task 1: Update Line2 manipulator behavior

**Files:**
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [ ] **Step 1: Replace the post-M801 completion block**

Change the block after `_manipulatorClearedFork = true;` so it mirrors Line1. The new block must keep `_manipulatorLock` held while the helper moves to `Manipulator1PickupStandbyY`.

```csharp
// 门闩必须放在M801成功之后；主循环可能与本方法并行，提前置位会让货叉在M801失败时误启动。
_manipulatorClearedFork = true;
Console.WriteLine("[Line2Front] [机械手1]   货叉放行门闩=ON，双头镗可开始交互；机械手继续回取板待机点");

// 同1号线：货叉放行后仍保持两线共享机械手锁，先回取板待机点，再释放锁。
// 这样当2号线货叉释放安全点与取板待机点不同，例如2000→1000时，
// 货叉可以先启动，机械手继续预定位，不会停在释放安全点。
if (!await MoveManipulator1ToPickupStandbyAfterForkReleaseAsync(wp, ct))
    return; // 货叉门闩保持开启，但共享机械手位置不确定；告警已同步暂停两条线。

Console.WriteLine($"[Line2Front] [机械手1] ═══ 取料送叉完成 {wp.IdentityText} ═══");
```

- [ ] **Step 2: Add the Line2 second-stage helper**

Insert this helper after `ProcessManipulatorPickupAsync` and before `DispatchForkAsync`.

```csharp
/// <summary>
/// 2号线货叉已安全放行后，把机械手1预定位到总上料架附近，缩短下一次取板空行程。
/// 调用期间仍持有两线共享的_manipulatorLock；货叉依靠独立门闩和状态机并行工作。
/// </summary>
private async Task<bool> MoveManipulator1ToPickupStandbyAfterForkReleaseAsync(
    WorkpieceCache wp, CancellationToken ct)
{
    int targetY = _cfg.SkewBed.Manipulator1PickupStandbyY;
    int releaseY = _cfg.SkewBed.GetManipulator1SafeYForLine(2);
    if (Math.Abs(targetY - releaseY) <= MotionConfig.SkewBedSection.Manipulator1SafeYTolerance)
    {
        Console.WriteLine($"[Line2Front] [机械手1] ⑤ 取板待机Y={targetY}与货叉释放点相同，无需第二段移动");
        return true;
    }

    try
    {
        Console.WriteLine($"[Line2Front] [机械手1] ⑤ 货叉已开始独立工作，保持共享机械手锁并预定位 Y:{releaseY}→{targetY}");
        await _manipulator1!.MoveAbsoluteAsync(-1, targetY, -1, ct: ct);

        var status = await _manipulator1.ReadStatusAsync(ct);
        if (status == null || Math.Abs(status.YPos - targetY) > MotionConfig.SkewBedSection.Manipulator1SafeYTolerance)
            throw new InvalidOperationException(status == null
                ? $"回取板待机Y={targetY}后状态读取为空"
                : $"回取板待机Y未到位，实际Y={status.YPos}，目标Y={targetY}");

        Console.WriteLine($"[Line2Front] [机械手1]   取板待机Y={status.YPos}到位 ✓，下一块板可从近端取料");
        return true;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception moveEx)
    {
        int? actualY = null;
        string readDetail;
        try
        {
            var status = await _manipulator1!.ReadStatusAsync(ct);
            actualY = status?.YPos;
            readDetail = status == null ? "实际Y读取为空" : $"实际Y={status.YPos}";
        }
        catch (Exception readEx)
        {
            readDetail = $"实际Y读取失败: {readEx.Message}";
        }

        if (actualY.HasValue && _cfg.SkewBed.IsManipulator1AtAnySafeY(actualY.Value))
        {
            Console.WriteLine($"[Line2Front] [机械手1] ⚠ 回取板待机点失败，但仍在已确认安全端点({readDetail})，货叉继续工作。异常：{moveEx.Message}");
            return true;
        }

        _paused = true;
        string alarm = $"共享机械手1在2号线货叉已放行后回取板待机Y={targetY}时异常，{readDetail}。" +
                       $"工件{wp.IdentityText}已在货叉且货叉状态不回退；机械手位置不确定，1/2号线新派发必须暂停。异常：{moveEx.Message}";
        Console.WriteLine($"[Line2Front] [机械手1] ❌ {alarm}");
        OnSharedManipulatorSafetyAlarm?.Invoke(alarm);
        return false;
    }
}
```

### Task 2: Update motion configuration

**Files:**
- Modify: `Config/motion_settings.json`

- [ ] **Step 1: Set the Line2 fork-release safe Y to 2000**

Change:

```json
"manipulator1Line2SafeY": 1000,
"manipulator1PickupStandbyY": 1000
```

to:

```json
"manipulator1Line2SafeY": 2000,
"manipulator1PickupStandbyY": 1000
```

This means Line2 releases the fork at Y=2000, then keeps the manipulator lock and returns to pickup standby Y=1000.

### Task 3: Update documentation

**Files:**
- Modify: `Docs/异常Catch_弹窗_暂停_锁释放与恢复审查.md`
- Modify: `Docs/引擎异常暂停_缓存锁与恢复流程详解.md`
- Modify: `Docs/superpowers/specs/2026-07-08-line-shared-locks-and-rear-dispatch-design.md`

- [ ] **Step 1: Document that Line2 now has the same two-stage return as Line1**

Update the exception/recovery docs to say both 1号线 and 2号线 can run:

```text
放板到货叉 → Z回0 → 到本线货叉释放安全Y → M801 → 门闩打开 → 保持机械手锁回Manipulator1PickupStandbyY
```

Also state that if the release safe Y equals pickup standby Y, the helper logs “无需第二段移动” and returns.

### Task 4: Verify

**Files:**
- Build target: `AutomaticOnlineHostComputer.csproj`

- [ ] **Step 1: Static search**

Run:

```powershell
rg -n "MoveManipulator1ToPickupStandbyAfterForkReleaseAsync|manipulator1Line2SafeY|manipulator1PickupStandbyY" Service Config Docs -S
```

Expected: both Line1 and Line2 contain the helper/call; config shows Line2 safe Y 2000 and pickup standby 1000.

- [ ] **Step 2: Build**

Run:

```powershell
dotnet build AutomaticOnlineHostComputer.csproj -clp:ErrorsOnly
```

Expected: build succeeds with 0 errors.

- [ ] **Step 3: Diff whitespace check**

Run:

```powershell
git diff --check -- Service Config Docs
```

Expected: no whitespace errors. CRLF warnings are acceptable.
