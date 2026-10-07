# 全流程状态悬停详情 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在既有全流程状态页中，为斜床、研磨机、中转架和五台天车提供只读工件/任务 Tooltip，不改变设备控制和调度。

**Architecture:** 新增不可变展示快照类型；各流程引擎只在内存中维护并复制快照，`HomeViewModel` 通过现有状态同步循环组合 Tooltip 文本。XAML 只绑定内存文本，不在页面层连接或读取设备。

**Tech Stack:** .NET 8、WPF、MVVM、现有 FlowEngine 内存状态、XAML Tooltip。

---

### Task 1: 添加展示快照类型和纯格式化器

**Files:**
- Create: `Service/FlowEngine/FlowStatusDisplaySnapshots.cs`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] **Step 1: 定义不可变快照类型**

```csharp
namespace AutomaticOnlineHostComputer.Service;

public sealed record WorkpieceDisplaySnapshot(
    string PlateNo, string Sequence, double Diameter, double Length, string Process)
{
    public string IdentityText => string.IsNullOrWhiteSpace(PlateNo)
        ? "版号=未知" : $"版号={PlateNo} 序号={Sequence}";

    public static WorkpieceDisplaySnapshot? From(WorkpieceCache? workpiece)
    {
        if (!workpiece.HasValue) return null;
        var value = workpiece.Value;
        return new(value.PlateNo, value.Sequence, value.Diameter, value.Length, value.SkewBedProcess);
    }
}

public sealed record CraneTaskSnapshot(
    bool IsActive, int CraneNo, string CraneName, WorkpieceDisplaySnapshot? Workpiece,
    string Stage, string SourceStation, string TargetStation,
    bool NeedsManualConfirmation, string ManualConfirmationText, DateTime UpdatedAtUtc)
{
    public static CraneTaskSnapshot Idle(int craneNo, string craneName)
        => new(false, craneNo, craneName, null, "空闲", string.Empty, string.Empty,
            false, string.Empty, DateTime.UtcNow);
}

public sealed record ProcessStationSnapshot(
    string StationCode, string State, WorkpieceDisplaySnapshot? Workpiece,
    DateTime StateChangedAtUtc, bool NeedsManualWorkpiece);

public sealed record TransferRackSnapshot(
    string StationCode, bool PhysicalHasPlate, WorkpieceDisplaySnapshot? Workpiece)
{
    public bool HasIdentityMismatch => PhysicalHasPlate != (Workpiece != null);
}
```

- [ ] **Step 2: 添加只使用参数的格式化器**

在 `HomeViewModel` 添加 `FormatProcessTooltip`、`FormatCraneTooltip`、`FormatTransferRackTooltip` 和 `FormatDuration`。格式化器不调用任何设备服务或引擎控制方法。

```csharp
private static string FormatDuration(DateTime changedAtUtc)
{
    var elapsed = DateTime.UtcNow - changedAtUtc;
    if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
    return elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours}时{elapsed.Minutes}分{elapsed.Seconds}秒"
        : $"{elapsed.Minutes}分{elapsed.Seconds}秒";
}
```

- [ ] **Step 3: 静态验证展示层没有控制调用**

运行：

```powershell
rg -n "ConnectAsync|ReadStatusAsync|MoveAbsoluteAsync|MagnetOnAsync|MagnetOffAsync|Enqueue|Dequeue|WaitAsync|Release" Service\FlowEngine\FlowStatusDisplaySnapshots.cs Presentation\ViewModels\Home\HomeViewModel.cs
```

预期：新快照类型和格式化器所在新增区段不出现设备连接、运动、磁铁、队列或信号量操作。

### Task 2: 为两条后端线暴露中转架和斜床快照

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] **Step 1: 让斜床状态记录状态开始时间**

在两个私有 `SkewCtx` 中把 `St` 改为带私有 backing field 的属性。值实际变化时更新 `StateChangedAtUtc`，同状态轮询不重置时间：

```csharp
private SkewState _st;
public SkewState St
{
    get => _st;
    set
    {
        if (_st == value) return;
        _st = value;
        StateChangedAtUtc = DateTime.UtcNow;
    }
}
public DateTime StateChangedAtUtc { get; private set; } = DateTime.UtcNow;
```

- [ ] **Step 2: 添加斜床快照方法**

在每个后端引擎公开 `GetSkewStationSnapshots()`；只复制站号、现有 `StateText(b.St)`、`WorkpieceDisplaySnapshot.From(b.Wp)`、状态时间和 `WpRecoveryNeeded`，不得返回 `SkewCtx`。

```csharp
public ProcessStationSnapshot[] GetSkewStationSnapshots()
    => _beds.Where(b => b != null).Select(b => new ProcessStationSnapshot(
        b.Code, StateText(b.St), WorkpieceDisplaySnapshot.From(b.Wp),
        b.StateChangedAtUtc, b.WpRecoveryNeeded)).ToArray();
```

- [ ] **Step 3: 添加中转架软件缓存快照方法**

在 `_wpLock` 内复制 `_wps`，锁外组合固定站号和既有前端 `DeviceStatus` 的物理有板位；不得调用 PLC：

```csharp
public TransferRackSnapshot[] GetTransferRackSnapshots()
{
    Dictionary<string, WorkpieceCache> cached;
    lock (_wpLock) cached = new Dictionary<string, WorkpieceCache>(_wps);
    return new[]
    {
        new TransferRackSnapshot("ST105", RackHasPhysicalPlateSnapshot("ST105"),
            cached.TryGetValue("ST105", out var wp1) ? WorkpieceDisplaySnapshot.From(wp1) : null),
        // 同样创建另外两个固定中转架位置。
    };
}
```

- [ ] **Step 4: 验证后端快照只读**

运行：

```powershell
rg -n "StateChangedAtUtc|GetSkewStationSnapshots|GetTransferRackSnapshots|new TransferRackSnapshot" Service\FlowEngine\Line1RearFlowEngine.cs Service\FlowEngine\Line2RearFlowEngine.cs
```

预期：两个后端引擎都有状态时间和两个快照方法；中转架快照方法不包含 `await`、`Read` 或 `Write`。

### Task 3: 为五台天车维护当前任务快照

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`

- [ ] **Step 1: 前天车记录出队工件和阶段**

两个前端引擎增加 `_frontCraneTask` 和短锁，公开 `GetCraneTaskSnapshot()` 返回复制值。`TryDequeue` 成功后设置“前往货叉 Pos3”；在 `ProcessCraneFrontAsync` 中更新为“前往打号机”“打号处理”“前往中转架”“归位”。

正常完成才清为 `Idle`。安全异常且工件可能仍在天车、打号机或中转通道时，保留工件并设置 `NeedsManualConfirmation=true`；不得在 `finally` 无条件清空。

- [ ] **Step 2: 后天车覆盖上料和下料在途区间**

两个后端引擎增加 `_rearCraneTask` 和短锁。`DoLoad` 从 `_wps` 成功移除工件后立刻设置“中转架取料，目标斜床”；`DoUnload` 从 `bed.Wp` 取得工件后设置“从斜床下料”，分流目标确定后更新为 ST019/ST010 或 ST020/ST021。

`holdingWorkpiece`、`loadState.PlacedOnBed`、`zMayBeDown` 等既有异常分支保留工件并标记人工确认；正常动作完成且既有逻辑已清工件时再清为空闲。

- [ ] **Step 3: 研磨天车复制已有动作记录**

在 `GrindingFlowEngine` 增加 `GetCraneTaskSnapshot()`；在 `_emergencyLock` 内只复制 `_craneLockHeldByFlow`、`_craneLockAction`、`_craneLockStation` 和目标研磨机 `PendingWorkpiece`。`ProcessWorkpieceAsync`、`UnloadFromGrinderAsync` 更新来源、目标和阶段；异常保留 `PendingWorkpiece` 时标记人工确认。

- [ ] **Step 4: 应急成功路径才清快照**

在既有前端、后端、研磨应急成功路径中，仅在旧动作退出、既有缓存清理和现场确认已成功的路径清快照。超时、取消失败、寄存器清理失败均保留。

- [ ] **Step 5: 验证快照不参与调度判断**

运行：

```powershell
rg -n "GetCraneTaskSnapshot|_frontCraneTask|_rearCraneTask|CraneTaskSnapshot" Service\FlowEngine
```

人工检查：快照字段只在阶段写入和 getter 读取；现有 `if` 调度条件、PLC 写入、队列和信号量代码不以快照字段作为判断条件。

### Task 4: 在 ViewModel 组装 Tooltip 文本

**Files:**
- Modify: `Presentation/ViewModels/Home/StationCardViewModel.cs`
- Modify: `Presentation/ViewModels/Home/CraneCardViewModel.cs`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] **Step 1: 添加独立 Tooltip 属性**

`StationCardViewModel` 添加：

```csharp
private string _tooltipText = string.Empty;
public string TooltipText { get => _tooltipText; set => SetField(ref _tooltipText, value); }
```

`CraneCardViewModel` 添加 `FlowTaskTooltipText`，不修改其既有5秒 PLC 卡片轮询。

- [ ] **Step 2: 映射斜床和研磨机快照**

在既有 `SyncLine1StatusToCardsAsync`、`SyncLine2StatusToCardsAsync` 和研磨状态同步路径中读取快照并写 `StationCardViewModel.TooltipText`。运行状态但工件为空或 `NeedsManualWorkpiece=true` 时显示“需人工补录工件信息”；空闲无工件显示“当前无工件”。

- [ ] **Step 3: 映射中转架和天车快照**

添加 `Line1TransferRackTooltip`、`Line2TransferRackTooltip` 属性，使用 `FormatTransferRackTooltip`。每次既有状态同步触发属性通知。

将五个引擎快照格式化后写入 `Crane1F`、`Crane1R`、`Crane2F`、`Crane2R`、`CraneGL` 的 `FlowTaskTooltipText`。ViewModel 只调用快照 getter 和格式化器。

- [ ] **Step 4: 验证 UI 组装没有设备调用**

运行：

```powershell
rg -n "TooltipText|FlowTaskTooltipText|TransferRackTooltip|GetSkewStationSnapshots|GetTransferRackSnapshots|GetCraneTaskSnapshot" Presentation\ViewModels\Home
```

预期：ViewModel 新增逻辑只使用快照 getter；不存在新的 `ConnectAsync`、设备 `Read` 或设备 `Write`。

### Task 5: 绑定全流程页 Tooltip 并保证离开即关闭

**Files:**
- Modify: `Views/FlowStatus/FlowStatusView.xaml`

- [ ] **Step 1: 给通用站卡模板绑定 TooltipText**

在 `StationNodeTemplate`、`DetailedStationNodeTemplate` 和 `CompactStationNodeTemplate` 外层 `Border` 添加：

```xml
ToolTipService.InitialShowDelay="1000"
ToolTipService.ShowDuration="2147483647"
ToolTip="{Binding TooltipText}"
```

不得使用 `ToolTipOpening` 事件读取设备。

- [ ] **Step 2: 给斜床组每一行绑定独立 Tooltip**

斜床组是手写 Grid。给 ST108~ST112、ST606~ST610 的状态区域绑定 `StationCards[STxxx].TooltipText`，同时设置 1000ms 延迟；保留原 `Status1`、`Status2`、尺寸和布局。

- [ ] **Step 3: 给中转架组和五台天车绑定 Tooltip**

1号线中转架组外层 Border 绑定 `Line1TransferRackTooltip`，2号线对应 `Line2TransferRackTooltip`。`CraneMotionNodeTemplate` 外层 Border 绑定 `FlowTaskTooltipText`。Tooltip 默认非固定，鼠标离开目标卡片即关闭；不创建窗口或页面。

### Task 6: 构建与回归验证

**Files:**
- Modify: `Docs/superpowers/plans/2026-07-11-flow-status-hover-details.md`（勾选执行结果，不提交）

- [ ] **Step 1: 编译项目**

运行：

```powershell
dotnet build AutomaticOnlineHostComputer.csproj --no-restore
```

预期：0 errors；记录现有第三方/FANUC warnings，不将它们归因于本功能。

- [ ] **Step 2: 静态回归检查**

运行：

```powershell
rg -n "ToolTipOpening|ConnectAsync|ReadStatusAsync|MoveAbsoluteAsync|MagnetOnAsync|MagnetOffAsync" Views\FlowStatus Presentation\ViewModels\Home Service\FlowEngine\FlowStatusDisplaySnapshots.cs
```

预期：FlowStatus XAML 不含 TooltipOpening 或设备调用；展示快照文件不含设备调用。

- [ ] **Step 3: 检查改动范围和空白错误**

运行：

```powershell
git diff --stat
git diff --check
```

预期：仅快照、相关引擎展示字段、ViewModel、FlowStatus XAML 和本计划文件有变更；无空白错误。按用户要求不执行 `git add` 或 `git commit`。
