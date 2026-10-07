# 手动页 XYZ 原点输入显示 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在手动操作页只读显示选中设备的 X3、X4、X5 三路 XYZ 物理原点输入。

**Architecture:** `CraneManualControlViewModel` 在既有单次刷新中复用已连接的 `CraneService.ReadXBitAsync`，把每一路的 `bool?` 保存为独立页面属性；读取失败保存 `null`。XAML 只绑定文本属性，核心业务文档说明这三项只展示、不参与任何业务。

**Tech Stack:** C#/.NET、WPF/XAML、Modbus TCP FC01、xUnit。

---

## 文件结构

- 修改 `Communication/DeviceAddresses/CraneAddress.cs`：声明 X3/X4/X5 三个只读 FC01 线圈地址常量。
- 修改 `Presentation/ViewModels/Home/CraneManualControlViewModel.cs`：维护原点输入的未知/0/1状态，并在现有刷新中读取。
- 修改 `Views/Motion/Controls/ManualMotionControlPanel.xaml`：在设备实际反馈区显示三项原点输入文本。
- 修改 `Docs/全线业务逻辑详解.md`：记录手动页显示语义及严格的非业务边界。
- 创建 `AutomaticOnlineHostComputer.Tests/Presentation/ManualOriginInputDisplayContractTests.cs`：验证常量、刷新读取和页面绑定的源级契约。

### Task 1: 写入并验证失败契约

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/Presentation/ManualOriginInputDisplayContractTests.cs`

- [x] **Step 1: 写失败契约测试**

```csharp
[Fact]
public void Manual_refresh_exposes_three_read_only_origin_inputs()
{
    string addresses = Read("Communication", "DeviceAddresses", "CraneAddress.cs");
    string vm = Read("Presentation", "ViewModels", "Home", "CraneManualControlViewModel.cs");
    string xaml = Read("Views", "Motion", "Controls", "ManualMotionControlPanel.xaml");

    Assert.Contains("D_X3_XHome", addresses, StringComparison.Ordinal);
    Assert.Contains("D_X4_YHome", addresses, StringComparison.Ordinal);
    Assert.Contains("D_X5_ZHome", addresses, StringComparison.Ordinal);
    Assert.Contains("IsXOriginInput", vm, StringComparison.Ordinal);
    Assert.Contains("IsYOriginInput", vm, StringComparison.Ordinal);
    Assert.Contains("IsZOriginInput", vm, StringComparison.Ordinal);
    Assert.Contains("CraneAddress.D_X3_XHome", vm, StringComparison.Ordinal);
    Assert.Contains("CraneAddress.D_X4_YHome", vm, StringComparison.Ordinal);
    Assert.Contains("CraneAddress.D_X5_ZHome", vm, StringComparison.Ordinal);
    Assert.Contains("X轴原点", xaml, StringComparison.Ordinal);
    Assert.Contains("Y轴原点", xaml, StringComparison.Ordinal);
    Assert.Contains("Z轴原点", xaml, StringComparison.Ordinal);
}
```

- [x] **Step 2: 运行测试，确认当前失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~ManualOriginInputDisplayContractTests --verbosity:minimal`

Expected: FAIL，原因是 X3/X4/X5 的地址常量、状态属性及 XAML 文案尚不存在。

### Task 2: 增加原点输入的只读刷新与显示

**Files:**
- Modify: `Communication/DeviceAddresses/CraneAddress.cs`
- Modify: `Presentation/ViewModels/Home/CraneManualControlViewModel.cs`
- Modify: `Views/Motion/Controls/ManualMotionControlPanel.xaml`

- [x] **Step 1: 声明三项输入常量**

```csharp
/// <summary>X3：X轴物理原点输入（FC01线圈63491，仅供手动页显示）。</summary>
public const int D_X3_XHome = 63491;
/// <summary>X4：Y轴物理原点输入（FC01线圈63492，仅供手动页显示）。</summary>
public const int D_X4_YHome = 63492;
/// <summary>X5：Z轴物理原点输入（FC01线圈63493，仅供手动页显示）。</summary>
public const int D_X5_ZHome = 63493;
```

- [x] **Step 2: 在 ViewModel 增加三个 nullable 反馈属性和文本**

```csharp
public bool? IsXOriginInput { get; private set; }
public bool? IsYOriginInput { get; private set; }
public bool? IsZOriginInput { get; private set; }

public string XOriginInputText => IsXOriginInput switch
{
    true => "X轴原点已触发（X3=1）",
    false => "X轴原点未触发（X3=0）",
    null => "X轴原点读取未知"
};
```

为 Y/Z 使用同样的 `bool?` 与文本模式。设备切换和刷新开始时将三项置为 `null`；在现有 `TryReadXFeedbackAsync` 调用序列中分别读取 `CraneAddress.D_X3_XHome`、`CraneAddress.D_X4_YHome`、`CraneAddress.D_X5_ZHome`，只有设备未在读取期间切换时才写回页面属性。

- [x] **Step 3: 在现有状态栏绑定三个文本**

```xml
<TextBlock Text="{Binding XOriginInputText}" />
<TextBlock Text="{Binding YOriginInputText}" />
<TextBlock Text="{Binding ZOriginInputText}" />
```

将它们放到现有“设备实际反馈”区域，沿用该区域的字体、间距与文本颜色，不新增按钮、命令或自动刷新。

- [x] **Step 4: 运行契约测试，确认通过**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~ManualOriginInputDisplayContractTests --verbosity:minimal`

Expected: PASS，且不执行任何 PLC 写入。

### Task 3: 同步核心业务说明并完成验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`

- [x] **Step 1: 补充手动状态显示规则**

在手动操作页状态规则中写明：刷新读取 X3/63491、X4/63492、X5/63493，分别仅显示 X/Y/Z 物理原点输入；`1=已触发`、`0=未触发`、读取失败=未知；它们不写 PLC，也不参与自动动作、位置锁、报警、工件、回原点完成或线路启动判断。

- [ ] **Step 2: 构建并检查差异格式**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore --verbosity:minimal`

Expected: `0 个警告`、`0 个错误`。

Run: `git diff --check -- Communication/DeviceAddresses/CraneAddress.cs Presentation/ViewModels/Home/CraneManualControlViewModel.cs Views/Motion/Controls/ManualMotionControlPanel.xaml Docs/全线业务逻辑详解.md`

Expected: 无空白错误。

> 用户明确要求不提交 Git，因此本计划不包含提交步骤。
