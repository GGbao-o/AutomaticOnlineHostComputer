# 全线 X11 稳定等待配置 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在配置管理页公开并保存全线共用的“充磁后 X11 稳定等待（ms）”参数。

**Architecture:** 保持 `MotionConfig.Grinding.X11StableDelayMs` 作为唯一数据源。配置页 ViewModel 提供一个直接映射属性，XAML 在既有“研磨 & 动平衡”卡片中绑定该属性；现有保存命令继续通过 `_cfg.Save()` 写入 JSON，不改变任何自动流程。

**Tech Stack:** .NET 8、WPF/XAML、MVVM、xUnit。

---

### Task 1: 为配置页绑定建立契约测试

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/X11StableDelayConfigContractTests.cs`
- Reference: `Infrastructure/Config/MotionConfig.cs`
- Reference: `Config/motion_settings.json`
- Reference: `Presentation/ViewModels/Config/ConfigPageViewModel.cs`
- Reference: `Views/Config/ConfigPageView.xaml`

- [ ] **Step 1: 写入失败的契约测试**

```csharp
using AutomaticOnlineHostComputer.Infrastructure.Config;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class X11StableDelayConfigContractTests
{
    [Fact]
    public void Global_x11_stable_delay_is_exposed_on_configuration_page()
    {
        string model = Read("Infrastructure", "Config", "MotionConfig.cs");
        string json = Read("Config", "motion_settings.json");
        string viewModel = Read("Presentation", "ViewModels", "Config", "ConfigPageViewModel.cs");
        string view = Read("Views", "Config", "ConfigPageView.xaml");

        Assert.Contains("public int X11StableDelayMs { get; set; } = 3000;", model, StringComparison.Ordinal);
        Assert.Contains("\"x11StableDelayMs\": 3000", json, StringComparison.Ordinal);
        Assert.Contains("public int X11StableDelayMs", viewModel, StringComparison.Ordinal);
        Assert.Contains("_cfg.Grinding.X11StableDelayMs", viewModel, StringComparison.Ordinal);
        Assert.Contains("充磁后X11稳定等待", view, StringComparison.Ordinal);
        Assert.Contains("X11StableDelayMs", view, StringComparison.Ordinal);
        Assert.Contains("全线共用", view, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~X11StableDelayConfigContractTests" --no-restore`

Expected: FAIL，因为 ViewModel 和 XAML 尚未公开该参数。

### Task 2: 增加 ViewModel 属性与配置页面输入项

**Files:**
- Modify: `Presentation/ViewModels/Config/ConfigPageViewModel.cs:108-112`
- Modify: `Views/Config/ConfigPageView.xaml:425-439`

- [ ] **Step 1: 增加直接映射属性**

在现有 `GrindingSafeZ` 和研磨状态超时属性之间加入：

```csharp
public int X11StableDelayMs
{
    get => _cfg.Grinding.X11StableDelayMs;
    set
    {
        _cfg.Grinding.X11StableDelayMs = value;
        OnPropertyChanged();
    }
}
```

该属性不复制值、不作单位转换，沿用页面现有即时更新共享 `_cfg` 实例的方式。

- [ ] **Step 2: 增加 XAML 行和操作员说明**

在“研磨 & 动平衡”卡片的 `GrindingCraneNo` 输入项后加入：

```xml
<StackPanel Orientation="Horizontal" Margin="0,0,0,4">
    <TextBlock Text="充磁后X11稳定等待" Style="{StaticResource ParamLabelStyle}" />
    <TextBox Text="{Binding X11StableDelayMs, UpdateSourceTrigger=PropertyChanged}"
             Style="{StaticResource ParamTextBoxStyle}" Width="80" />
    <TextBlock Text="ms" Style="{StaticResource ParamLabelStyle}" />
</StackPanel>
<TextBlock FontSize="11" Foreground="{StaticResource TextMutedBrush}" Margin="0,0,0,6"
           Text="全线共用：前/后天车、机械手、动平衡和研磨天车每次充磁后，等待该时长再读取X11有板信号。" />
```

- [ ] **Step 3: 运行新增测试并确认通过**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~X11StableDelayConfigContractTests" --no-restore`

Expected: PASS，验证默认值、JSON、ViewModel 属性、XAML 输入和“全线共用”说明均存在。

### Task 3: 构建与回归验证

**Files:**
- Verify: `AutomaticOnlineHostComputer.csproj`
- Verify: `AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj`

- [ ] **Step 1: 编译应用**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: 0 errors；已有第三方 SDK 警告可保留。

- [ ] **Step 2: 运行相关配置与微调契约测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~X11StableDelayConfigContractTests|FullyQualifiedName~AbsFineTuneVerificationConfigContractTests" --no-restore`

Expected: PASS。

- [ ] **Step 3: 检查变更范围**

Run: `git diff --check` 和 `git diff -- Presentation/ViewModels/Config/ConfigPageViewModel.cs Views/Config/ConfigPageView.xaml AutomaticOnlineHostComputer.Tests/FlowEngine/X11StableDelayConfigContractTests.cs`

Expected: 无空白错误；没有 `Service/FlowEngine` 或 PLC 交互代码变更。
