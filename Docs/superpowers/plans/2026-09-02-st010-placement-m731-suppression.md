# ST010 放板标志抑制 M731 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 当机械手3或2号天车正在向 ST010 放板时，让天车5跳过本趟 M731 写入；标志只用于此判断，所有流程退出路径均清除自身标志。

**Architecture:** 新建一个无设备依赖、线程安全的 `St010PlacementFlags`，由 `HomeViewModel` 创建为单例并注入三个相关引擎。M3 和2号天车只在其 ST010 最终移动窗口内置位；研磨天车5延时后只读快照决定写或跳过 M731。

**Tech Stack:** .NET 8、C#、WPF MVVM、现有 FlowEngine、`Interlocked`/`Volatile`、xUnit 源代码合同测试。

---

### Task 1: 建立共享内存标志及其回归合同

**Files:**
- Create: `Service/FlowEngine/St010PlacementFlags.cs`
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/St010PlacementM731SuppressionContractTests.cs`

- [x] **Step 1: 写失败的标志对象合同测试**

```csharp
[Fact]
public void Placement_flags_are_two_independent_atomic_memory_markers()
{
    string source = Read("Service", "FlowEngine", "St010PlacementFlags.cs");

    Assert.Contains("class St010PlacementFlags", source);
    Assert.Contains("BeginM3Placement", source);
    Assert.Contains("EndM3Placement", source);
    Assert.Contains("BeginCrane2Placement", source);
    Assert.Contains("EndCrane2Placement", source);
    Assert.Contains("Snapshot", source);
    Assert.Contains("Interlocked.Exchange", source);
    Assert.DoesNotContain("SemaphoreSlim", source);
    Assert.DoesNotContain("WriteMBit", source);
}
```

- [x] **Step 2: 运行测试，确认当前失败**

运行：

```powershell
dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~St010PlacementM731SuppressionContractTests
```

预期：失败，因为 `St010PlacementFlags.cs` 尚不存在。

- [x] **Step 3: 实现独立、幂等的标志对象**

创建：

```csharp
namespace AutomaticOnlineHostComputer.Service;

public sealed class St010PlacementFlags
{
    private int _m3St010Placing;
    private int _crane2St010Placing;

    public bool BeginM3Placement() => Interlocked.Exchange(ref _m3St010Placing, 1) == 0;
    public bool EndM3Placement() => Interlocked.Exchange(ref _m3St010Placing, 0) == 1;
    public bool BeginCrane2Placement() => Interlocked.Exchange(ref _crane2St010Placing, 1) == 0;
    public bool EndCrane2Placement() => Interlocked.Exchange(ref _crane2St010Placing, 0) == 1;

    public St010PlacementSnapshot Snapshot() => new(
        Volatile.Read(ref _m3St010Placing) == 1,
        Volatile.Read(ref _crane2St010Placing) == 1);
}

public readonly record struct St010PlacementSnapshot(bool M3St010Placing, bool Crane2St010Placing)
{
    public bool AnyPlacing => M3St010Placing || Crane2St010Placing;
}
```

该对象不得记录动作账本、工件、坐标、PLC 或锁。

- [x] **Step 4: 重跑标志对象合同测试**

运行：

```powershell
dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~St010PlacementM731SuppressionContractTests
```

预期：标志对象合同通过。

### Task 2: 在 M3 和2号天车维护各自标志

**Files:**
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs:649-672,2186-2272,2398-2415`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:237-255,2789-2827,2941-3066,3117-3132`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:2607-2662`
- Modify: `AutomaticOnlineHostComputer.Tests/FlowEngine/St010PlacementM731SuppressionContractTests.cs`

- [x] **Step 1: 写失败的生产者时序合同**

```csharp
[Fact]
public void M3_marks_before_final_st010_y_move_and_finally_clears()
{
    string method = ExtractMethod(Read("Service", "FlowEngine", "BalancingFlowEngine.cs"), "private async Task M3Flow");
    Assert.True(Index(method, "await ConfirmM720EmptyBeforePlaceAsync(ct);")
        < Index(method, "BeginM3Placement()")
        && Index(method, "BeginM3Placement()")
        < Index(method, "await _m3.MoveAbsoluteAsync(-1, destY, -1, ct: ct);"));
    Assert.Contains("EndM3Placement()", ExtractFinally(method));
}

[Fact]
public void Crane2_marks_only_short_st010_branch_before_xy_and_finally_clears()
{
    string method = ExtractMethod(Read("Service", "FlowEngine", "Line1RearFlowEngine.cs"), "private async Task DoUnload");
    string st010Branch = ExtractBetween(method, "if (!TryCoords(\"ST010\"", "await cr.MoveAbsoluteAsync(gx + _ox, gy + _oy, -1, ct: ct);");
    Assert.True(Index(st010Branch, "BeginCrane2Placement()") >= 0);
    Assert.DoesNotContain("BeginCrane2Placement()", ExtractBetween(method, "if (needsBalancing)", "else"));
    Assert.Contains("EndCrane2Placement()", ExtractFinally(method));
}
```

- [x] **Step 2: 运行测试，确认当前失败**

运行同 Task 1 Step 2。预期：失败，因为两个引擎尚未接收或调用共享标志。

- [x] **Step 3: 注入同一标志对象**

在 `HomeViewModel.InitializeEnginesAsync` 先创建：

```csharp
var st010PlacementFlags = new St010PlacementFlags();
```

并把它作为必需依赖传入 `GrindingFlowEngine`、`BalancingFlowEngine` 和 `Line1RearFlowEngine`。三个构造器增加 `St010PlacementFlags st010PlacementFlags` 参数，保存为只读字段；不得新增 `SemaphoreSlim` 或让研磨天车获取 M720 锁。

- [x] **Step 4: 在 M3 正确置位和无条件清除**

在 `M3Flow` 中，紧跟：

```csharp
await ConfirmM720EmptyBeforePlaceAsync(ct);
```

且在 `MoveAbsoluteAsync(-1, destY, -1...)` 前加入：

```csharp
if (_st010PlacementFlags.BeginM3Placement())
    Console.WriteLine("[平衡引擎] [M3] ST010放板标志=1，研磨天车将跳过本趟M731");
```

在 M721 成功返回后立即调用：

```csharp
if (_st010PlacementFlags.EndM3Placement())
    Console.WriteLine("[平衡引擎] [M3] ST010放板标志=0（M721成功）");
```

并在 `M3Flow` 最外层 `finally` 开头再次加入：

```csharp
if (_st010PlacementFlags.EndM3Placement())
    Console.WriteLine("[平衡引擎] [M3] ST010放板标志=0（流程退出兜底）");
```

`End` 幂等，因此未置位、正常完成、异常、取消和提前 return 都安全。

- [x] **Step 5: 在2号天车短板 ST010 分支正确置位和无条件清除**

在 `DoUnload` 中增加 `bool crane2St010PlacementStarted = false;`。仅在短板分支的：

```csharp
await WaitForM720CanPlaceBeforePlaceAsync(wp, ct);
```

成功返回后、ST010 `MoveAbsoluteAsync(gx + _ox, gy + _oy, -1...)` 之前加入：

```csharp
if (_st010PlacementFlags.BeginCrane2Placement())
    Console.WriteLine("│ [分流] 2号天车ST010放板标志=1，研磨天车将跳过本趟M731");
crane2St010PlacementStarted = true;
```

在 M721、研磨缓存回调和 `st010SafeZTask` 均成功返回后调用：

```csharp
if (crane2St010PlacementStarted && _st010PlacementFlags.EndCrane2Placement())
    Console.WriteLine("│ [分流] 2号天车ST010放板标志=0（M721和Z安全到位）");
```

并在该 `DoUnload` 的既有 `finally` 开头再次加入：

```csharp
if (crane2St010PlacementStarted && _st010PlacementFlags.EndCrane2Placement())
    Console.WriteLine("│ [分流] 2号天车ST010放板标志=0（流程退出兜底）");
```

长板 `needsBalancing` 分支不能调用 `BeginCrane2Placement`。

- [x] **Step 6: 重跑生产者合同测试**

运行同 Task 1 Step 2。预期：生产者时序、短板限定和 finally 清除合同通过。

### Task 3: 让天车5只读标志决定跳过或写 M731

**Files:**
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:277-289,1825-1868`
- Modify: `AutomaticOnlineHostComputer.Tests/FlowEngine/St010PlacementM731SuppressionContractTests.cs`

- [x] **Step 1: 写失败的 M731 分支合同**

```csharp
[Fact]
public void Grinding_crane_skips_m731_only_when_a_st010_placement_flag_is_active()
{
    string method = ExtractMethod(Read("Service", "FlowEngine", "GrindingFlowEngine.cs"), "private async Task ProcessWorkpieceAsync");
    int delay = Index(method, "await Task.Delay(_cfg.Grinding.M731NotifyDelayMs, ct);");
    int snapshot = Index(method, "var st010Placement = _st010PlacementFlags.Snapshot();");
    int write = Index(method, "Task m731Task = _mc65.WriteMBitInWordAsync(720, 11, true, ct);");

    Assert.True(delay < snapshot && snapshot < write);
    Assert.Contains("if (st010Placement.AnyPlacing)", method);
    Assert.Contains("await zSafeTask;", ExtractIfBody(method, "if (st010Placement.AnyPlacing)"));
    Assert.DoesNotContain("WriteMBitInWordAsync", ExtractIfBody(method, "if (st010Placement.AnyPlacing)"));
}
```

- [x] **Step 2: 运行测试，确认当前失败**

运行同 Task 1 Step 2。预期：失败，因为当前研磨天车延时后无条件写 M731。

- [x] **Step 3: 实现只读跳过分支**

在现有 M731 延时后加入：

```csharp
var st010Placement = _st010PlacementFlags.Snapshot();
if (st010Placement.AnyPlacing)
{
    await zSafeTask;
    action.Confirm(new FlowActionPosition(null, null, safeZ),
        "Z安全到位；ST010正在放板，按PLC新逻辑跳过本趟M731");
    Console.WriteLine($"[GrindingEngine] [{craneName}] ST010放板中(M3={st010Placement.M3St010Placing},天车2={st010Placement.Crane2St010Placing})，跳过本趟M731");
}
else
{
    // 保留原有MC65连接检查、M731位写入、Task.WhenAll与失败处理。
}
```

两条分支结束后再进入现有“目标XY与最终加工参数并行准备”。跳过分支不写 M731、不报警、不暂停，也不修改任何放板标志。

- [x] **Step 4: 重跑 M731 分支合同测试**

运行同 Task 1 Step 2。预期：跳过分支在延时和写入之间，等待 Z 安全但没有 M731 写入；空闲分支仍保留 bit 11 写入。

### Task 4: 更新文档并完成验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md:805-825`
- Modify: `Docs/superpowers/plans/2026-09-02-st010-placement-m731-suppression.md`

- [x] **Step 1: 更新第12.7节 M731步骤**

把“延时后并行写 M731”改为：延时后读取两个 ST010 放板内存标志；任一为真则等待 Z 安全并按 PLC 新逻辑跳过本趟 M731；均为假才并行写 M731。明确标志只读给天车5、任意流程退出由 finally 清零。

- [x] **Step 2: 运行目标测试和主项目构建**

运行：

```powershell
dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter "FullyQualifiedName~St010PlacementM731SuppressionContractTests|FullyQualifiedName~GrindingM731OverlapContractTests"
dotnet build AutomaticOnlineHostComputer.csproj --no-restore
```

预期：新合同测试全部通过；主项目 0 errors。若旧 `GrindingM731OverlapContractTests` 的配置 JSON 断言仍因用户本机配置不同失败，单独记录，不修改用户配置。

- [x] **Step 3: 做静态边界检查**

运行：

```powershell
rg -n "St010PlacementFlags|BeginM3Placement|BeginCrane2Placement|EndM3Placement|EndCrane2Placement|st010Placement" Service\FlowEngine Presentation\ViewModels\Home\HomeViewModel.cs
git diff --check
git diff --stat
```

预期：标志对象无 PLC/坐标/锁调用；研磨天车没有 `Begin`/`End`；M3/天车2没有读取标志快照；无空白错误。手工现场测试只在 PLC 新内部握手已部署的环境进行。
