# 运动参数页接液盘控制与门反馈 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让运动参数页只对 5 台天车显示接液盘开关按钮，并在刷新设备状态时读取和显示 X0/X1 门开、门关到位反馈。

**Architecture:** 继续复用 `CraneService.ReadXBitAsync` 和现有手动控制 ViewModel，不新增连接或轮询器。地址层补充 X0/X1 常量，ViewModel 使用两个独立 `bool?` 保存三态反馈，XAML 通过天车选择属性控制按钮与状态标签可见性。

**Tech Stack:** C# 12、.NET 8、WPF/XAML、xUnit、Modbus TCP FC01/FC06

> 用户明确要求本次不提交 Git，因此以下步骤不包含暂存或提交动作。

---

### Task 1: 增加接液盘页面契约测试

**Files:**
- Modify: `AutomaticOnlineHostComputer.Tests/Presentation/ManualMotionMagnetFeedbackContractTests.cs`

- [ ] **Step 1: 写入失败的地址、ViewModel 与 XAML 契约断言**

在现有测试中读取 `CraneAddress.cs`，并断言以下关键契约存在：

```csharp
Assert.Contains("D_X0_DrainOpenOk", addresses, StringComparison.Ordinal);
Assert.Contains("D_X1_DrainCloseOk", addresses, StringComparison.Ordinal);
Assert.Contains("public bool IsCraneOnly", viewModel, StringComparison.Ordinal);
Assert.Contains("public bool? IsDrainOpenFeedback", viewModel, StringComparison.Ordinal);
Assert.Contains("public bool? IsDrainClosedFeedback", viewModel, StringComparison.Ordinal);
Assert.Contains("CraneAddress.D_X0_DrainOpenOk", viewModel, StringComparison.Ordinal);
Assert.Contains("CraneAddress.D_X1_DrainCloseOk", viewModel, StringComparison.Ordinal);
Assert.Contains("ManualControl.DrainOpenFeedbackText", xaml, StringComparison.Ordinal);
Assert.Contains("ManualControl.DrainClosedFeedbackText", xaml, StringComparison.Ordinal);
Assert.Contains("ManualControl.IsCraneOnly", xaml, StringComparison.Ordinal);
```

- [ ] **Step 2: 运行定向测试并确认先失败**

Run:

```powershell
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter ManualMotionMagnetFeedbackContractTests
```

Expected: 新增断言因 X0/X1 常量、反馈属性或可见性绑定尚不存在而失败。

### Task 2: 接入 X0/X1 地址与三态反馈

**Files:**
- Modify: `Communication/DeviceAddresses/CraneAddress.cs`
- Modify: `Presentation/ViewModels/Home/CraneManualControlViewModel.cs`

- [ ] **Step 1: 在地址类补充门开和门关反馈常量**

在 X 区输入定义中加入：

```csharp
public const int D_X0_DrainOpenOk = 63488;
public const int D_X1_DrainCloseOk = 63489;
```

- [ ] **Step 2: 将天车类型判断公开给 XAML**

把现有私有属性调整为：

```csharp
public bool IsCraneOnly => _selectedDevice?.DeviceType == ManualDeviceType.Crane;
```

并在 `SelectedDevice` 变化时通知 `IsCraneOnly`，同时清空门反馈：

```csharp
OnPropertyChanged(nameof(IsCraneOnly));
IsDrainOpenFeedback = null;
IsDrainClosedFeedback = null;
```

- [ ] **Step 3: 增加两个独立的可空反馈及文本**

```csharp
public bool? IsDrainOpenFeedback { get; private set; }
public bool? IsDrainClosedFeedback { get; private set; }

public string DrainOpenFeedbackText => IsDrainOpenFeedback switch
{
    true => "门开到位（X0=1）",
    false => "门开未到位（X0=0）",
    null => "门开读取未知"
};

public string DrainClosedFeedbackText => IsDrainClosedFeedback switch
{
    true => "门关到位（X1=1）",
    false => "门关未到位（X1=0）",
    null => "门关读取未知"
};
```

实际实现使用项目现有 `SetField` 模式，并在状态变化时通知对应文本属性。

- [ ] **Step 4: 只对天车读取 X0/X1**

在 `RefreshTargetsFromCurrentAsync` 中先把两个状态清空；选中天车时调用：

```csharp
drainOpenFeedback = await TryReadXFeedbackAsync(
    service, CraneAddress.D_X0_DrainOpenOk, "X0接液盘门开到位", cts.Token);
drainClosedFeedback = await TryReadXFeedbackAsync(
    service, CraneAddress.D_X1_DrainCloseOk, "X1接液盘门关到位", cts.Token);
```

通过现有 `ReferenceEquals(deviceAtRead, SelectedDevice)` 防止旧设备结果覆盖，并仅在读请求仍属于天车时赋值。

- [ ] **Step 5: 运行定向测试**

Run:

```powershell
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter ManualMotionMagnetFeedbackContractTests
```

Expected: 地址和 ViewModel 契约通过；XAML 契约可能仍因未增加标签或隐藏绑定而失败。

### Task 3: 只为天车显示按钮与门反馈

**Files:**
- Modify: `Views/Motion/Controls/ManualMotionControlPanel.xaml`

- [ ] **Step 1: 在设备实际反馈行增加门开、门关标签**

沿用现有反馈 `Border` 样式，为两个标签增加 `Visibility` 数据触发器：

```xml
<DataTrigger Binding="{Binding ManualControl.IsCraneOnly}" Value="False">
    <Setter Property="Visibility" Value="Collapsed" />
</DataTrigger>
```

标签文本分别绑定：

```xml
Text="{Binding ManualControl.DrainOpenFeedbackText}"
Text="{Binding ManualControl.DrainClosedFeedbackText}"
```

到位为绿色，未到位和未知保持中性灰色，不把普通未到位显示成报警。

- [ ] **Step 2: 隐藏机械手的两个接液盘按钮**

给现有 D4512/D4513 按钮增加相同的 `IsCraneOnly` 可见性触发器；选中机械手时使用 `Collapsed`，不保留空白按钮。

- [ ] **Step 3: 运行定向测试并确认通过**

Run:

```powershell
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter ManualMotionMagnetFeedbackContractTests
```

Expected: `ManualMotionMagnetFeedbackContractTests` 全部通过。

### Task 4: 更新协议说明并完整验证

**Files:**
- Modify: `Docs/02-通信协议手册/天车机械手_项目实际使用寄存器清单.md`
- Modify: `Docs/SCADA+轻量MES_页面职责与排障导航手册.md`

- [ ] **Step 1: 更新 X 区实际使用清单**

在 X 区表格加入：

```markdown
| X0 门开到位 | 63488 | 运动参数页选择天车后刷新读取 | 1=接液盘门开到位；机械手不读取。 |
| X1 门关到位 | 63489 | 运动参数页选择天车后刷新读取 | 1=接液盘门关到位；机械手不读取。 |
```

同时保留“自动 Z 下降仍为 500ms 延时、尚未使用 X0/X1 互锁”的边界说明。

- [ ] **Step 2: 更新页面职责说明**

说明运动参数页可以对 5 台天车手动打开/关闭接液盘，并通过刷新查看 X0/X1；机械手不显示这些入口。

- [ ] **Step 3: 运行完整测试与主项目构建**

Run:

```powershell
dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj
dotnet build AutomaticOnlineHostComputer.csproj
```

Expected: 测试无失败，主项目构建退出码为 0。

- [ ] **Step 4: 检查最终差异范围**

Run:

```powershell
git diff -- Communication/DeviceAddresses/CraneAddress.cs Presentation/ViewModels/Home/CraneManualControlViewModel.cs Views/Motion/Controls/ManualMotionControlPanel.xaml AutomaticOnlineHostComputer.Tests/Presentation/ManualMotionMagnetFeedbackContractTests.cs Docs/02-通信协议手册/天车机械手_项目实际使用寄存器清单.md Docs/SCADA+轻量MES_页面职责与排障导航手册.md Docs/superpowers/specs/2026-08-20-motion-drain-tray-feedback-design.md Docs/superpowers/plans/2026-08-20-motion-drain-tray-feedback.md
```

Expected: 只包含接液盘按钮隐藏、X0/X1 刷新显示、测试和文档改动，没有自动流程行为变化。
