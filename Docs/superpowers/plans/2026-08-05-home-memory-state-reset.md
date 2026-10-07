# 主页面设备内存状态复位 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在主页面为单台斜床或研磨机提供只复位指定内存字段的入口，使 PLC 已就绪但旧内存候选门槛残留时可继续自动派发。

**Architecture:** 新建独立的 `MemoryStateResetDialog`，避免混淆现有“清空状态”的设备寄存器语义。主页通过 `HomeViewModel` 路由到三个流程引擎；两个后端引擎仅把目标床 `St` 设为 `Idle` 且 `Wp` 设为 `null`，研磨引擎仅把目标机 `State` 设为 `Idle` 且 `PendingWorkpiece` 设为 `null`。

**Tech Stack:** C#、WPF/XAML、xUnit 源码契约测试。

---

### Task 1: 建立内存复位契约测试

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/HomeMemoryStateResetContractTests.cs`
- Test: `AutomaticOnlineHostComputer.Tests/FlowEngine/HomeMemoryStateResetContractTests.cs`

- [x] **Step 1: 写失败测试**

创建下列契约测试，要求三个引擎存在独立公开入口，并把修改范围限制在已确认字段：

```csharp
[Theory]
[InlineData("Line1RearFlowEngine.cs")]
[InlineData("Line2RearFlowEngine.cs")]
public void Skew_memory_reset_only_sets_idle_and_clears_workpiece(string fileName)
{
    string source = Read("Service", "FlowEngine", fileName);
    int start = source.IndexOf("public string ResetSkewMemoryState", StringComparison.Ordinal);
    Assert.True(start >= 0);
    string method = source[start..source.IndexOf("    /// <summary>", start + 1, StringComparison.Ordinal)];

    Assert.Contains("bed.St = SkewState.Idle", method, StringComparison.Ordinal);
    Assert.Contains("bed.Wp = null", method, StringComparison.Ordinal);
    Assert.DoesNotContain("CompletionExported =", method, StringComparison.Ordinal);
    Assert.DoesNotContain("WpRecoveryNeeded =", method, StringComparison.Ordinal);
    Assert.DoesNotContain("SignalFresh =", method, StringComparison.Ordinal);
}

[Fact]
public void Grinder_memory_reset_only_sets_idle_and_clears_pending_workpiece()
{
    string source = Read("Service", "FlowEngine", "GrindingFlowEngine.cs");
    int start = source.IndexOf("public string ResetGrindingMemoryState", StringComparison.Ordinal);
    Assert.True(start >= 0);
    string method = source[start..source.IndexOf("    /// <summary>", start + 1, StringComparison.Ordinal)];

    Assert.Contains("grinder.State = GrinderState.Idle", method, StringComparison.Ordinal);
    Assert.Contains("grinder.PendingWorkpiece = null", method, StringComparison.Ordinal);
    Assert.DoesNotContain("WpRecoveryNeeded =", method, StringComparison.Ordinal);
    Assert.DoesNotContain("LastRequestData =", method, StringComparison.Ordinal);
}
```

新增第三个测试断言 `HomeView.xaml` 包含 `ResetMemoryState_Click` 和“恢复内存状态”，`HomeViewModel` 包含两条路由方法，`MemoryStateResetDialog` 包含 10 台斜床和 4 台研磨机的站号按钮。

- [x] **Step 2: 运行测试并确认失败**

Run:

```powershell
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~HomeMemoryStateResetContractTests --no-restore
```

Expected: FAIL，原因是复位接口、对话框和主页入口尚不存在。

- [x] **Step 3: 暂不修改生产代码**

保持本任务只建立失败契约；生产代码放在任务 2 和任务 3，确保测试能证明新增入口的范围。

- [ ] **Step 4: 提交测试基线（本次与功能实现合并提交）**

```powershell
git add -- AutomaticOnlineHostComputer.Tests/FlowEngine/HomeMemoryStateResetContractTests.cs
git commit -m "test: define memory state reset contract"
```

### Task 2: 增加流程引擎与主页路由

**Files:**
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`
- Test: `AutomaticOnlineHostComputer.Tests/FlowEngine/HomeMemoryStateResetContractTests.cs`

- [x] **Step 1: 在两条后端引擎加入单床复位入口**

在各自 `FindBed` 可用的公开方法区域加入同名方法；它仅复位 `St` 和 `Wp`，并返回带旧值的操作结果：

```csharp
public string ResetSkewMemoryState(string bedCode)
{
    var bed = FindBed(bedCode);
    if (bed == null) return $"未找到斜床 {bedCode}";

    string oldState = StateText(bed.St);
    string oldWorkpiece = bed.Wp?.IdentityText ?? "无";
    bed.St = SkewState.Idle;
    bed.Wp = null;
    string result = $"{bed.Code} 内存状态已复位：状态 {oldState}→空闲，工件 {oldWorkpiece}→无。未写PLC/CNC，未清信号、快照、账本或锁。";
    Console.WriteLine($"[LineRear] {result}");
    return result;
}
```

两条线使用各自现有的 `StateText` 与日志前缀；不得写入 `CompletionExported`、`WpRecoveryNeeded`、信号、动作租约、任务牌或锁。

- [x] **Step 2: 在研磨引擎加入单机复位入口**

在 `GrindingFlowEngine` 的公开人工操作方法附近加入：

```csharp
public string ResetGrindingMemoryState(string stationCode)
{
    var grinder = _grinders.FirstOrDefault(g =>
        string.Equals(g.StationCode, stationCode, StringComparison.OrdinalIgnoreCase));
    if (grinder == null) return $"未找到研磨机 {stationCode}";

    string oldState = GrinderStateText(grinder.State);
    string oldWorkpiece = grinder.PendingWorkpiece?.IdentityText ?? "无";
    grinder.State = GrinderState.Idle;
    grinder.PendingWorkpiece = null;
    string result = $"{grinder.StationCode} 内存状态已复位：状态 {oldState}→空闲，Pending工件 {oldWorkpiece}→无。未写PLC，未清信号、恢复标记、快照、账本或锁。";
    Console.WriteLine($"[GrindingEngine] {result}");
    return result;
}
```

方法不写 `WpRecoveryNeeded`、`LastRequestData`、扫描版本、天车锁或派发门闩。

- [x] **Step 3: 在主页 ViewModel 路由公开入口**

紧靠既有 `EmergencyClearSkewBed` 和研磨应急路由加入：

```csharp
public string ResetSkewMemoryState(int line, string bedCode) => line switch
{
    1 => _line1RearEngine?.ResetSkewMemoryState(bedCode) ?? "1号线后端引擎未初始化",
    2 => _line2RearEngine?.ResetSkewMemoryState(bedCode) ?? "2号线后端引擎未初始化",
    _ => $"无效线体: {line}"
};

public string ResetGrindingMemoryState(string stationCode) =>
    _grindingEngine?.ResetGrindingMemoryState(stationCode) ?? "研磨引擎未初始化";
```

路由不暂停、启动或恢复任何引擎。

- [x] **Step 4: 运行聚焦测试并确认通过**

Run:

```powershell
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~HomeMemoryStateResetContractTests --no-restore
```

Expected: PASS，0 failed。

- [ ] **Step 5: 提交流程接口（本次与 UI 合并提交）**

```powershell
git add -- Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs Presentation/ViewModels/Home/HomeViewModel.cs AutomaticOnlineHostComputer.Tests/FlowEngine/HomeMemoryStateResetContractTests.cs
git commit -m "feat: reset stale device memory state"
```

### Task 3: 增加主页入口和独立设备选择窗口

**Files:**
- Create: `Views/Home/Dialogs/MemoryStateResetDialog.xaml`
- Create: `Views/Home/Dialogs/MemoryStateResetDialog.xaml.cs`
- Modify: `Views/Home/HomeView.xaml`
- Modify: `Views/Home/HomeView.xaml.cs`
- Test: `AutomaticOnlineHostComputer.Tests/FlowEngine/HomeMemoryStateResetContractTests.cs`

- [x] **Step 1: 创建窗口布局**

建立 `MemoryStateResetDialog`，按现有 `ClearMachineStatusDialog` 的“标题、分组站号按钮、操作日志、关闭按钮”布局显示以下站号：

```xml
<Button Tag="1:ST108" Click="SkewButton_Click" Content="1号斜床 ST108" />
<Button Tag="1:ST109" Click="SkewButton_Click" Content="2号斜床 ST109" />
<Button Tag="1:ST111" Click="SkewButton_Click" Content="3号斜床 ST111" />
<Button Tag="1:ST110" Click="SkewButton_Click" Content="4号斜床 ST110" />
<Button Tag="1:ST112" Click="SkewButton_Click" Content="5号斜床 ST112" />
<Button Tag="2:ST606" Click="SkewButton_Click" Content="ST606" />
<Button Tag="2:ST607" Click="SkewButton_Click" Content="ST607" />
<Button Tag="2:ST608" Click="SkewButton_Click" Content="ST608" />
<Button Tag="2:ST609" Click="SkewButton_Click" Content="ST609" />
<Button Tag="2:ST610" Click="SkewButton_Click" Content="ST610" />
<Button Tag="ST701" Click="GrinderButton_Click" Content="ST701" />
<Button Tag="ST702" Click="GrinderButton_Click" Content="ST702" />
<Button Tag="ST703" Click="GrinderButton_Click" Content="ST703" />
<Button Tag="ST704" Click="GrinderButton_Click" Content="ST704" />
```

标题警示文字必须明确：斜床只改 `St=Idle、Wp=null`；研磨只改 `State=Idle、PendingWorkpiece=null`；不写 PLC 或移动设备。

- [x] **Step 2: 实现一次确认和操作日志**

对话框构造函数接收 `Func<int, string, string> resetSkew` 与 `Func<string, string> resetGrinding`。每个按钮在调用前弹出确认，文案为：

```csharp
string prompt = isSkew
    ? $"确认复位 {stationCode} 的上位机内存？\n\n将清除：St→Idle、Wp→null。\n不会写PLC/CNC，不会移动设备，不会清信号、快照、账本或锁。\n\n仅在PLC已正常请求且天车未派发时使用。"
    : $"确认复位 {stationCode} 的上位机内存？\n\n将清除：State→Idle、PendingWorkpiece→null。\n不会写PLC，不会移动设备，不会清信号、恢复标记、快照、账本或锁。\n\n仅在PLC已正常请求且天车未派发时使用。";
```

用户确认后只调用注入的委托；按钮禁用期间防止重复点击，并将返回字符串写入窗口日志。

- [x] **Step 3: 接入主页按钮**

在 `HomeView.xaml` 的“应急处理”和“清空状态”之间加入：

```xml
<Button
    Width="104"
    Margin="0,0,8,0"
    Click="ResetMemoryState_Click"
    Content="恢复内存状态"
    Style="{StaticResource WarningButtonStyle}" />
```

在 `HomeView.xaml.cs` 加入：

```csharp
private void ResetMemoryState_Click(object sender, RoutedEventArgs e)
{
    var dialog = new MemoryStateResetDialog(
        _viewModel.ResetSkewMemoryState,
        _viewModel.ResetGrindingMemoryState)
    { Owner = Window.GetWindow(this) };
    dialog.ShowDialog();
}
```

- [x] **Step 4: 运行聚焦测试和构建**

Run:

```powershell
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~HomeMemoryStateResetContractTests --no-restore
dotnet build AutomaticOnlineHostComputer.csproj --no-restore
```

Expected: 两个命令均成功；测试 0 failed，构建 0 errors。

- [ ] **Step 5: 提交 UI（本次与功能实现合并提交）**

```powershell
git add -- Views/Home/Dialogs/MemoryStateResetDialog.xaml Views/Home/Dialogs/MemoryStateResetDialog.xaml.cs Views/Home/HomeView.xaml Views/Home/HomeView.xaml.cs AutomaticOnlineHostComputer.Tests/FlowEngine/HomeMemoryStateResetContractTests.cs
git commit -m "feat: add home memory state reset dialog"
```

### Task 4: 全量回归和提交范围检查

**Files:**
- Verify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Verify: `Service/FlowEngine/Line2RearFlowEngine.cs`
- Verify: `Service/FlowEngine/GrindingFlowEngine.cs`
- Verify: `Presentation/ViewModels/Home/HomeViewModel.cs`
- Verify: `Views/Home/Dialogs/MemoryStateResetDialog.xaml`
- Verify: `Views/Home/Dialogs/MemoryStateResetDialog.xaml.cs`
- Verify: `Views/Home/HomeView.xaml`
- Verify: `Views/Home/HomeView.xaml.cs`

- [x] **Step 1: 运行全量测试**

Run:

```powershell
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --no-build --no-restore
```

Expected: 所有测试通过，0 failed。

- [x] **Step 2: 检查空白和修改范围**

Run:

```powershell
git diff --check -- Service/FlowEngine/Line1RearFlowEngine.cs Service/FlowEngine/Line2RearFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs Presentation/ViewModels/Home/HomeViewModel.cs Views/Home/Dialogs/MemoryStateResetDialog.xaml Views/Home/Dialogs/MemoryStateResetDialog.xaml.cs Views/Home/HomeView.xaml Views/Home/HomeView.xaml.cs AutomaticOnlineHostComputer.Tests/FlowEngine/HomeMemoryStateResetContractTests.cs
```

Expected: 无输出；除新窗口、主页入口、三个内存复位接口和对应测试外没有本功能引入的修改。
